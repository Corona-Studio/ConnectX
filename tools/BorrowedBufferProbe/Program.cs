using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ConnectX.Client.Proxy;
using ConnectX.Client.Messages.Proxy;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Hive.Common.Shared.Pooling;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Codec.MemoryPack;
using Hive.Codec.Shared;
using Hive.Network.Shared;
using ConnectX.Client.Messages;
using ConnectX.Client.Helpers;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.Options;
using Snappier;
using ConnectX.Client;
using ConnectX.Client.Models;
using Hive.Network.Abstractions.Session;
using Hive.Network.Shared.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--benchmark"))
{
    var output = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "tools/BorrowedBufferProbe/results";
    await BenchmarkRunner.RunAsync(output);
}
else await RegressionSuite.RunAsync();

public static class RegressionSuite
{
    public static async Task RunAsync()
    {
        await ReceiveLifetime();
        await InvalidFrames();
        await Sending();
        await WritableFrameSending();
        PacketMappingConcurrency();
        await TcpRoundTrip();
        await QueuedOwnerLifetime();
        AllocationProbe();
        DecoderCompatibilityProbe();
        PoolAndCodecProbe();
        ForgetProbe();
        PoolConcurrencyAndLimits();
        await BoundedQueueRetryAndDrain();
        CompressionProbe();
        await TcpAllocationProbe();
        Console.WriteLine("PASS: lifetime, fragmented/coalesced frames, invalid frames, cancellation, partial writes, concurrent TCP sends and allocations.");
    }

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 6];
        BitConverter.TryWriteBytes(frame.AsSpan(), (ushort)frame.Length);
        BitConverter.TryWriteBytes(frame.AsSpan(2), 1);
        payload.CopyTo(frame, 6);
        return frame;
    }

    public static async Task ReceiveLifetime()
    {
        using var pool = new TrackingPool();
        using var session = new ProbeSession(pool);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        session.ReceiveHandler = async (_, sequence, _) =>
        {
            count++;
            if (count == 1)
            {
                entered.SetResult();
                await resume.Task;
                Check(sequence.FirstSpan[0] == 42 && pool.Returns == 0, "Borrowed bytes were recycled across await.");
            }
            else Check(sequence.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "Fragmented body changed.");
        };
        var receive = session.Receive();
        // First frame is 7 bytes; with 8-byte pool segments, the next length
        // straddles a segment. Both packets are in the same pipe read.
        await session.Feed(Frame([42]).Concat(Frame([1, 2, 3, 4, 5, 6, 7, 8])).ToArray());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(count == 1, "Receive loop advanced while consumer was suspended.");
        resume.SetResult();
        await session.EndInput();
        await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Check(count == 2, "Coalesced frames were not dispatched.");

        using var canceled = new ProbeSession();
        using var cts = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        canceled.ReceiveHandler = async (_, _, token) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var canceledRead = canceled.Receive(cts.Token);
        await canceled.Feed(Frame([7]));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await canceledRead.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public static async Task InvalidFrames()
    {
        foreach (var frame in new byte[][] { [0, 0, 0, 0, 0, 0], [5, 0, 0, 0, 0, 0], [10, 0, 0, 0, 0, 0, 1], [7] })
        {
            using var session = new ProbeSession();
            var receive = session.Receive();
            await session.Feed(frame);
            await session.EndInput();
            try { await receive.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Invalid frame accepted."); }
            catch (InvalidDataException) { }
        }
        using var failing = new ProbeSession();
        SessionReceivedAsyncHandler one = (_, _, _) => ValueTask.CompletedTask;
        try { failing.ReceiveHandler = one + one; throw new Exception("Multicast consumer accepted."); }
        catch (ArgumentException) { }
        failing.ReceiveHandler = (_, _, _) => throw new InvalidOperationException("consumer failure");
        var task = failing.Receive();
        await failing.Feed(Frame([1]));
        try { await task; throw new Exception("Consumer failure swallowed."); }
        catch (InvalidOperationException ex) when (ex.Message == "consumer failure") { }
    }

    public static async Task Sending()
    {
        using var session = new ProbeSession { Capture = true, MaxWrite = 2 };
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6];
        var head = new Segment(first);
        var tail = head.Append(second);
        var sequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        Check(await session.TrySendAsync(sequence), "Segmented send failed.");
        Check(session.Output.SequenceEqual(Frame([1, 2, 3, 4, 5, 6])), "Partial sends corrupted the frame.");
        Check(session.SawArrays.Contains(first) && session.SawArrays.Contains(second), "Payload arrays were copied.");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Check(!await session.TrySendAsync(sequence, cts.Token), "Canceled send succeeded.");
        Check(session.IsConnected, "Cancellation before sending closed the session.");
        using var stopped = new ProbeSession { MaxWrite = 0 };
        Check(!await stopped.TrySendAsync(sequence) && !stopped.IsConnected, "Zero-byte send must stop and close.");
        try { await session.TrySendAsync(new ReadOnlySequence<byte>(new byte[65530])); throw new Exception("Oversized frame accepted."); }
        catch (ArgumentOutOfRangeException) { }
    }

    public static async Task WritableFrameSending()
    {
        using var sender = new ProbeSession { Capture = true };
        var frame = Frame([1, 2, 3, 4]);
        Check(await sender.TrySendFrameAsync(frame), "Writable frame send failed.");
        Check(sender.Output.SequenceEqual(frame), "Writable frame changed payload or framing.");
        Check(sender.SendCalls == 1 && sender.SawArrays.Contains(frame), "Writable frame copied data or split the send.");
        using var partial = new ProbeSession { Capture = true, MaxWrite = 2 };
        Check(await partial.TrySendFrameAsync(frame) && partial.Output.SequenceEqual(frame), "Partial framed send corrupted bytes.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Check(!await sender.TrySendFrameAsync(frame, canceled.Token) && sender.IsConnected, "Canceled frame send closed the idle transport.");
        foreach (var invalid in new[] { new byte[5], new byte[65536] })
        {
            try { await sender.TrySendFrameAsync(invalid); throw new Exception("Invalid frame size accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        using var stopped = new ProbeSession { MaxWrite = 0 };
        Check(!await stopped.TrySendFrameAsync(frame) && !stopped.IsConnected, "Failed frame send did not close the stream.");
    }

    public static void PacketMappingConcurrency()
    {
        var options = new PacketIdMapperOptions();
        options.Register<Ping>();
        var mapper = new DefaultPacketIdMapper(Options.Create(options), NullLogger<DefaultPacketIdMapper>.Instance);
        var id = mapper.GetPacketId(typeof(Ping));
        Parallel.Invoke(
            () => Parallel.For(0, 10000, _ => Check(mapper.GetPacketId(typeof(Ping)).Equals(id) && mapper.GetPacketType(id) == typeof(Ping), "Mapping snapshot was inconsistent.")),
            () => mapper.Register<ForwardPacketCarrier>());
        Check(mapper.GetPacketType(mapper.GetPacketId(typeof(ForwardPacketCarrier))) == typeof(ForwardPacketCarrier), "New type was not published.");
        try { mapper.Register<Ping>(); throw new Exception("Duplicate packet registration accepted."); }
        catch (System.Data.DuplicateNameException) { }
    }

    public static async Task TcpRoundTrip()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connection = client.ConnectAsync(listener.LocalEndPoint!);
        using var accepted = await listener.AcceptAsync();
        await connection;
        using var sender = new TcpSession(23, client, NullLogger<TcpSession>.Instance);
        using var receiver = new TcpSession(24, accepted, NullLogger<TcpSession>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packets = 0;
        receiver.ReceiveHandler = async (_, bytes, token) =>
        {
            await Task.Delay(1, token); // Force suspension while referencing the pipe.
            Check(bytes.Length == 4096, "Concurrent sends interleaved frames.");
            var value = bytes.FirstSpan[0];
            foreach (var segment in bytes)
                Check(segment.Span.IndexOfAnyExcept(value) == -1, "Concurrent payloads mixed.");
            if (++packets == 33) received.TrySetResult();
        };
        var receiving = receiver.StartAsync(cts.Token);
        var sending = sender.StartAsync(cts.Token);
        var tasks = Enumerable.Range(0, 32).Select(async value =>
        {
            var data = new byte[4096];
            Array.Fill(data, (byte)value);
            var sent = value % 2 == 0
                ? await sender.TrySendFrameAsync(Frame(data), cts.Token)
                : await sender.TrySendAsync(new ReadOnlySequence<byte>(data), cts.Token);
            Check(sent, "TCP send failed.");
        }).ToArray();
        // Exercise existing stream sending alongside the new borrowed-buffer path.
        var streamTask = sender.TrySendAsync(new MemoryStream(new byte[4096]), cts.Token).AsTask();
        await Task.WhenAll(tasks);
        Check(await streamTask, "Legacy stream send failed.");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Task.WhenAll(receiving, sending);
    }

    public static async Task QueuedOwnerLifetime()
    {
        using var pool = new TrackingPool();
        using var proxy = new ProbeProxy();
        var owner = pool.Rent(8);
        owner.Memory.Span[0] = 77;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<ForwardPacketCarrier, ValueTask<bool>> sender = async packet =>
        {
            entered.SetResult();
            await resume.Task;
            Check(pool.Returns == 0 && packet.Payload.Span[0] == 77, "Queue released payload during send.");
            return true;
        };
        proxy.AddOutwardAsyncSender(sender);
        proxy.Enqueue(new ForwardPacketCarrier { PayloadOwner = owner, Payload = owner.Memory, LastTryTime = Environment.TickCount - 1000 });
        proxy.Complete();
        var loop = proxy.Run();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        proxy.RemoveOutwardAsyncSender(sender); // Subscription mutation while awaiting is safe.
        resume.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Check(pool.Returns == 1, "Queue did not release owner exactly once after sending.");

        using var failing = new ProbeProxy();
        failing.AddOutwardAsyncSender(_ => throw new IOException("send failure"));
        failing.Enqueue(new ForwardPacketCarrier { PayloadOwner = pool.Rent(8), LastTryTime = Environment.TickCount - 1000 });
        failing.Complete();
        try { await failing.Run(); throw new Exception("Send failure swallowed."); }
        catch (IOException) { }
        Check(pool.Returns == 2, "Failed send leaked the queued owner.");
    }

    public static void AllocationProbe()
    {
        using var session = new ProbeSession();
        var bytes = new byte[4096];
        var sequence = new ReadOnlySequence<byte>(bytes);
        var dispatcher = new ProbeDispatcher();
        var calls = 0;
        Action<byte[], PacketContext> callback = (_, _) => calls++;
        dispatcher.OnReceive(callback);
        for (var i = 0; i < 10000; i++)
        {
            session.TrySendAsync(sequence).GetAwaiter().GetResult();
            dispatcher.Deliver(bytes);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100000; i++)
        {
            session.TrySendAsync(sequence).GetAwaiter().GetResult();
            dispatcher.Deliver(bytes);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"Synchronous hot path allocated {allocated} bytes.");
        dispatcher.RemoveReceiver(callback);
        dispatcher.Deliver(bytes);
        Check(calls == 110000, "Callback removal failed.");
        Console.WriteLine($"Hot path: {allocated} managed bytes / 100000 borrowed sends + callback dispatches (warmed synchronous fake transport).");
    }

    public static void DecoderCompatibilityProbe()
    {
        var random = new Random(314159);
        var cases = 0;
        foreach (var size in new[] { 0, 1, 2, 3, 60, 61, 256, 4096, 20480, 65529, 131072 })
        for (var pattern = 0; pattern < 4; pattern++)
        {
            var data = new byte[size];
            random.NextBytes(data);
            if (pattern == 1) Array.Fill(data, (byte)123);
            if (pattern == 2) for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 7);
            if (pattern == 3) for (var i = 128; i < data.Length; i++) data[i] = data[i % 128];
            var compressed = Snappy.CompressToArray(data);
            foreach (var split in new[] { 0, 1, 2, compressed.Length / 2, compressed.Length })
            {
                if (split > compressed.Length) continue;
                var head = new Segment(compressed.AsMemory(0, split));
                var tail = head.Append(compressed.AsMemory(split));
                using var decoded = PooledBufferStream.Rent(NetworkSettings.MaxMessageSize);
                SnappyBlockDecoder.Decode(new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length), decoded);
                Check(decoded.Memory.Span.SequenceEqual(data), "Decoder differs from Snappier-compressed input.");
                cases++;
            }
            // Every byte can be in a separate segment, including tag and offset fields.
            var start = new Segment(compressed.AsMemory(0, 1));
            var last = start;
            for (var i = 1; i < compressed.Length; i++) last = last.Append(compressed.AsMemory(i, 1));
            using var byteSplit = PooledBufferStream.Rent(NetworkSettings.MaxMessageSize);
            SnappyBlockDecoder.Decode(new ReadOnlySequence<byte>(start, 0, last, last.Memory.Length), byteSplit);
            Check(byteSplit.Memory.Span.SequenceEqual(data), "Byte-split input failed.");
            cases++;
        }
        // Explicit copy tags cover all offset widths and overlap expansion.
        foreach (var valid in new byte[][] { [5, 0, 65, 1, 1], [5, 0, 65, 14, 1, 0], [5, 0, 65, 15, 1, 0, 0, 0] })
        {
            using var decoded = PooledBufferStream.Rent();
            SnappyBlockDecoder.Decode(new ReadOnlySequence<byte>(valid), decoded);
            Check(decoded.Memory.Span.SequenceEqual(new byte[] { 65, 65, 65, 65, 65 }), "Backreference tag failed.");
            Check(Snappy.DecompressToArray(valid).AsSpan().SequenceEqual(decoded.Memory.Span), "Explicit tag differs from Snappier.");
        }
        foreach (var invalid in new byte[][]
        {
            [], [128], [128, 128, 128, 128, 16], [0, 0], [4, 0, 65], [4, 1, 0],
            [4, 2, 1, 0], [4, 3, 1, 0, 0, 0], [1, 252, 255, 255, 255, 255],
            [2, 0, 65, 2, 0, 0], [1, 0, 65, 0], [2, 0, 65, 14, 1, 0]
        })
        {
            using var decoded = PooledBufferStream.Rent();
            try { SnappyBlockDecoder.Decode(new ReadOnlySequence<byte>(invalid), decoded); throw new Exception("Malformed Snappy block accepted."); }
            catch (InvalidDataException) { }
        }
        for (var i = 0; i < 2000; i++)
        {
            var data = new byte[random.Next(1, 4096)];
            random.NextBytes(data);
            if ((i & 1) == 0) for (var j = 16; j < data.Length; j++) data[j] = data[j % 16];
            var encoded = Snappy.CompressToArray(data);
            encoded[random.Next(encoded.Length)] ^= (byte)(1 << random.Next(8));
            using var decoded = PooledBufferStream.Rent(128 * 1024);
            try
            {
                SnappyBlockDecoder.Decode(new ReadOnlySequence<byte>(encoded), decoded);
                Check(Snappy.DecompressToArray(encoded).AsSpan().SequenceEqual(decoded.Memory.Span), "Mutated valid block differs from Snappier.");
            }
            catch (InvalidDataException) { }
        }
        Console.WriteLine($"Raw Snappy compatibility: {cases} fragmented corpus cases + all copy tags and malformed inputs passed.");
    }

    public static void PoolAndCodecProbe()
    {
        byte[] data = new byte[4096];
        Random.Shared.NextBytes(data);
        var compressed = Snappy.CompressToArray(data);
        var head = new Segment(compressed.AsMemory(0, 1));
        var tail = head.Append(compressed.AsMemory(1));
        var input = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        for (var i = 0; i < 10000; i++) DecodeAndRetain();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) DecodeAndRetain();
        var decodeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Segmented Snappy decode + two pooled carrier consumers: {decodeBytes} managed bytes / 10000 packets.");
        Check(decodeBytes == 0, "Segmented decoding still allocates per packet.");

        // Each consumer holds its own carrier; releasing one cannot reset another.
        using (var buffer = PooledBufferStream.Rent())
        {
            buffer.Write(data);
            using var original = ForwardPacketCarrier.Rent();
            original.PayloadOwner = buffer.Retain();
            original.Payload = buffer.Memory;
            using var first = original.Retain();
            using var second = original.Retain();
            first.Dispose();
            Check(second.Payload.Span.SequenceEqual(data), "Releasing one consumer invalidated another.");
        }
        try
        {
            using var limited = PooledBufferStream.Rent(100);
            SnappyBlockDecoder.Decode(input, limited);
            throw new Exception("Decompression exceeded its configured limit.");
        }
        catch (InvalidDataException) { }
        using (var memory = PooledMemoryPool.Shared.Rent(100))
            Check(memory.Memory.Length >= 100 && MemoryMarshal.TryGetArray<byte>(memory.Memory, out _), "Pipe pool did not supply array-backed storage.");

        var options = new PacketIdMapperOptions();
        options.Register<Ping>();
        var mapper = new DefaultPacketIdMapper(Options.Create(options), NullLogger<DefaultPacketIdMapper>.Instance);
        var codec = new MemoryPackPacketCodec(mapper, new DefaultCustomCodecProvider());
        var dispatcher = new DefaultDispatcher(codec, NullLogger<DefaultDispatcher>.Instance);
        var packet = new Ping { From = Guid.NewGuid(), To = Guid.NewGuid(), SeqId = 3, Ttl = 4, SendTime = 5 };
        using var session = new ProbeSession();
        var calls = 0;
        Action<MessageContext<Ping>> a = _ => calls++;
        Action<MessageContext<Ping>> b = _ => calls += 10;
        var id = dispatcher.AddHandler(a);
        dispatcher.AddHandler(b);
        Check(dispatcher.AddHandler(a).Equals(id), "Duplicate registration should reuse its handler id.");
        dispatcher.RemoveHandler(id);
        dispatcher.Dispatch(session, packet);
        Check(calls == 10, "Removing one handler removed a different handler.");
        dispatcher.RemoveHandler(b);
        dispatcher.Dispatch(session, packet);
        Check(calls == 10, "Removed handler was invoked.");
        dispatcher.AddHandler(a);

        using (var legacy = RecycleMemoryStreamManagerHolder.Shared.GetStream())
        using (var pooled = PooledBufferStream.Rent())
        {
            codec.Encode(packet, legacy);
            codec.Encode(packet, pooled);
            Check(legacy.GetReadOnlySequence().ToArray().AsSpan().SequenceEqual(pooled.Memory.Span), "Pooled encoding changed the wire bytes.");
            var decoded = (Ping)codec.Decode(pooled.WrittenSequence)!;
            Check(decoded.From == packet.From && decoded.To == packet.To && decoded.SeqId == 3, "MemoryPack round-trip failed.");
        }
        for (var i = 0; i < 10000; i++)
        {
            dispatcher.SendAsync(session, packet).GetAwaiter().GetResult();
            dispatcher.Dispatch(session, packet);
        }
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100000; i++)
        {
            dispatcher.SendAsync(session, packet).GetAwaiter().GetResult();
            dispatcher.Dispatch(session, packet);
        }
        var codecBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Real MemoryPack encode + Hive send/dispatch: {codecBytes} managed bytes / 100000 packets (synchronous fake transport).");
        Check(codecBytes == 0, "Hive encode/send/dispatch still allocates per packet.");

        // Compare the actual prior allocation sites using identical payloads.
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            using var flatten = MemoryPool<byte>.Shared.Rent(compressed.Length);
            input.CopyTo(flatten.Memory.Span);
            using var carrier = new ForwardPacketCarrier();
            carrier.PayloadOwner = Snappy.DecompressToMemory(flatten.Memory.Span[..compressed.Length]);
            carrier.Payload = carrier.PayloadOwner.Memory;
        }
        var oldDecodeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Prior flattened Snappy decode + carrier: {oldDecodeBytes} managed bytes / 10000 packets.");

        void DecodeAndRetain()
        {
            using var output = PooledBufferStream.Rent(NetworkSettings.MaxMessageSize);
            SnappyBlockDecoder.Decode(input, output);
            using var carrier = ForwardPacketCarrier.Rent();
            carrier.PayloadOwner = output.Retain();
            carrier.Payload = output.Memory;
            using var first = carrier.Retain();
            using var second = carrier.Retain();
            Check(second.Payload.Span.SequenceEqual(data), "Segmented decode corrupted output.");
        }
    }

    public static async Task TcpAllocationProbe()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var connect = client.ConnectAsync(listener.LocalEndPoint!);
        using var accepted = await listener.AcceptAsync();
        await connect;
        using var sender = new TcpSession(10, client, NullLogger<TcpSession>.Instance);
        using var receiver = new TcpSession(11, accepted, NullLogger<TcpSession>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var count = 0;
        var expected = 2000;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new ReadOnlySequence<byte>(new byte[512]);
        receiver.ReceiveHandler = (_, sequence, _) =>
        {
            Check(sequence.Length == 512, "TCP allocation probe framing failed.");
            if (Interlocked.Increment(ref count) == Volatile.Read(ref expected)) completion.TrySetResult();
            return ValueTask.CompletedTask;
        };
        var receiving = receiver.StartAsync(cts.Token);
        var sending = sender.StartAsync(cts.Token);
        for (var i = 0; i < 2000; i++) Check(await sender.TrySendAsync(payload, cts.Token), "Warmup send failed.");
        await completion.Task.WaitAsync(cts.Token);
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        expected = 12000;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 10000; i++) Check(await sender.TrySendAsync(payload, cts.Token), "Measured send failed.");
        await completion.Task.WaitAsync(cts.Token);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine($"Real warmed loopback TCP: {allocated} process-wide managed bytes / 10000 packets (includes runtime/thread scheduling).");
        cts.Cancel();
        await Task.WhenAll(receiving, sending);
    }

    public static void PoolConcurrencyAndLimits()
    {
        Parallel.For(0, 20000, new ParallelOptions { MaxDegreeOfParallelism = 64 }, i =>
        {
            using var buffer = PooledBufferStream.Rent();
            BitConverter.TryWriteBytes(buffer.GetSpan(4), i);
            buffer.Advance(4);
            using var retained = buffer.Retain();
            Thread.Yield();
            Check(BitConverter.ToInt32(retained.Memory.Span) == i, "Concurrent buffer leases shared storage.");
        });
        using var stream = PooledBufferStream.Rent(10);
        stream.Write([1, 2, 3]);
        stream.SetLength(6);
        Check(stream.Memory.Span.SequenceEqual(new byte[] { 1, 2, 3, 0, 0, 0 }), "SetLength exposed stale pooled bytes.");
        stream.Position = 6;
        try { stream.GetMemory(5); throw new Exception("Writer limit ignored."); }
        catch (InvalidDataException) { }
    }

    public static async Task BoundedQueueRetryAndDrain()
    {
        using var pool = new TrackingPool();
        using var proxy = new ProbeProxy(1);
        var order = new List<byte>();
        var attempts = 0;
        proxy.AddOutwardAsyncSender(packet =>
        {
            if (++attempts == 1) return ValueTask.FromResult(false);
            order.Add(packet.Payload.Span[0]);
            return ValueTask.FromResult(true);
        });
        ForwardPacketCarrier Packet(byte value)
        {
            var owner = pool.Rent(8);
            owner.Memory.Span[0] = value;
            return new ForwardPacketCarrier { PayloadOwner = owner, Payload = owner.Memory };
        }
        proxy.Enqueue(Packet(1));
        var pending = proxy.WriteAsync(Packet(2));
        Check(!pending.IsCompleted, "Bounded queue did not apply backpressure.");
        var loop = proxy.Run();
        await pending;
        proxy.Complete();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Check(order.SequenceEqual(new byte[] { 1, 2 }) && pool.Returns == 2, "Retry deadlocked, reordered or leaked packets.");
        using var disposed = new ProbeProxy();
        disposed.Enqueue(Packet(3));
        disposed.Enqueue(Packet(4));
        disposed.Dispose();
        Check(pool.Returns == 4, "Disposal leaked pending packets.");
    }

    public static void CompressionProbe()
    {
        var source = new byte[20480];
        var output = new byte[Snappy.GetMaxCompressedLength(source.Length)];
        for (var i = 0; i < 10000; i++) Snappy.Compress(source, output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) Snappy.Compress(source, output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Snappier span compression: {allocated} managed bytes / 10000 packets.");
    }

    public static void ForgetProbe()
    {
        var source = new CompletionSource();
        for (var i = 0; i < 10000; i++) Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100000; i++) Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Pending ValueTask Forget observers: {allocated} managed bytes / 100000 completions.");
        Check(allocated == 0 && source.Consumed == 110000, "Forget failed to consume sources without per-call allocation.");
        void Run()
        {
            source.Reset();
            source.Task.Forget();
            source.Complete();
        }
    }


}

sealed class CompletionSource : IValueTaskSource<bool>
{
    private ManualResetValueTaskSourceCore<bool> _core;
    public int Consumed;
    public ValueTask<bool> Task => new(this, _core.Version);
    public void Reset() => _core.Reset();
    public void Complete() => _core.SetResult(true);
    public bool GetResult(short token) { Consumed++; return _core.GetResult(token); }
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> callback, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(callback, state, token, flags);
}

sealed class ProbeDispatcher() : PacketDispatcherBase<byte[]>(null!, NullLogger.Instance)
{
    public void Deliver(byte[] bytes) => Dispatch(bytes, typeof(byte[]), Guid.Empty);
    protected override void OnReceiveDatagram(byte[] bytes) => Deliver(bytes);
}

sealed class Segment : ReadOnlySequenceSegment<byte>
{
    public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
    public Segment Append(ReadOnlyMemory<byte> bytes)
    {
        var segment = new Segment(bytes) { RunningIndex = RunningIndex + Memory.Length };
        Next = segment;
        return segment;
    }
}

sealed class ProbeSession : AbstractSession, IWritableFrameSession
{
    public ProbeSession(MemoryPool<byte>? pool = null) : base(1, NullLogger<ProbeSession>.Instance)
    {
        ReceivePipe = new Pipe(new PipeOptions(pool: pool, minimumSegmentSize: 8));
    }
    public bool Capture { get; init; }
    public int MaxWrite { get; init; } = int.MaxValue;
    public int SendCalls { get; private set; }
    public List<byte> Output { get; } = [];
    public HashSet<byte[]> SawArrays { get; } = [];
    public override IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 1);
    public override IPEndPoint RemoteEndPoint => LocalEndPoint;
    public override bool CanSend => IsConnected;
    public override bool CanReceive => IsConnected;
    public Task Receive(CancellationToken token = default) => ReceiveLoop(token);
    public async Task Feed(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            ReceivePipe!.Writer.GetMemory(1).Span[0] = value;
            ReceivePipe.Writer.Advance(1);
        }
        await ReceivePipe!.Writer.FlushAsync();
    }
    public ValueTask EndInput() => ReceivePipe!.Writer.CompleteAsync();
    public ValueTask<bool> TrySendFrameAsync(Memory<byte> frame, CancellationToken token = default)
        => SendWritableFrameAsync(frame, token);
    public ValueTask<bool> TrySendAsync(ReadOnlySequence<byte> payload, CancellationToken token = default)
        => SendBorrowedSequenceAsync(payload, token);
    public override ValueTask<int> SendOnce(ArraySegment<byte> data, CancellationToken token)
    {
        SendCalls++;
        var count = Math.Min(data.Count, MaxWrite);
        if (Capture)
        {
            SawArrays.Add(data.Array!);
            for (var i = 0; i < count; i++) Output.Add(data[i]);
        }
        return ValueTask.FromResult(count);
    }
    public override ValueTask<int> ReceiveOnce(ArraySegment<byte> data, CancellationToken token) => throw new NotSupportedException();
    public override void Close() { base.Close(); IsConnected = false; }
}

sealed class TrackingPool : MemoryPool<byte>
{
    public int Returns;
    public override int MaxBufferSize => int.MaxValue;
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1) => new Owner(this, Math.Max(8, minBufferSize));
    protected override void Dispose(bool disposing) { }
    private sealed class Owner(TrackingPool pool, int size) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[size];
        public Memory<byte> Memory => _bytes;
        public void Dispose() { Array.Fill(_bytes, (byte)0xcc); pool.Returns++; }
    }
}

sealed class ProbeProxy : GenericProxyBase
{
    public ProbeProxy(int capacity = 128) : base(new TunnelIdentifier(Guid.Empty, 1, 2), CancellationToken.None, NullLogger<GenericProxyBase>.Instance)
    {
        OutwardBuffersQueue = Channel.CreateBounded<ForwardPacketCarrier>(capacity);
    }
    public void Enqueue(ForwardPacketCarrier packet) => OutwardBuffersQueue!.Writer.TryWrite(packet);
    public ValueTask WriteAsync(ForwardPacketCarrier packet) => OutwardBuffersQueue!.Writer.WriteAsync(packet);
    public void Complete() => OutwardBuffersQueue!.Writer.Complete();
    public Task Run() => OuterSendLoopAsync(CancellationToken.None);
    protected override Socket CreateSocket() => throw new NotSupportedException();
}
