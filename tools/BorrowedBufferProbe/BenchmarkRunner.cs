using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using Hive.Network.Tcp;
using ConnectX.Client.Helpers;
using ConnectX.Client.Messages;
using ConnectX.Client.Messages.Proxy;
using Hive.Both.General.Dispatchers;
using Hive.Codec.MemoryPack;
using Hive.Codec.Shared;
using Hive.Common.Shared.Pooling;
using Hive.Network.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snappier;

internal static class BenchmarkRunner
{
    private const int Iterations = 20000;
    private const int Samples = 7;
    private static int _checksum;

    public static async Task RunAsync(string outputDirectory)
    {
        var rows = new List<Result>();
        foreach (var repeating in new[] { false, true })
        {
            var bytes = new byte[20480];
            new Random(42).NextBytes(bytes);
            if (repeating) for (var i = 7; i < bytes.Length; i++) bytes[i] = bytes[i % 7];
            var compressed = Snappy.CompressToArray(bytes);
            var head = new Segment(compressed.AsMemory(0, 1));
            var tail = head.Append(compressed.AsMemory(1));
            var input = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
            var corpus = repeating ? "repeat20KiB" : "random20KiB";
            rows.Add(Measure($"decode.legacy.{corpus}", () =>
            {
                using var flatten = MemoryPool<byte>.Shared.Rent(compressed.Length);
                input.CopyTo(flatten.Memory.Span);
                using var carrier = new ForwardPacketCarrier();
                carrier.PayloadOwner = Snappy.DecompressToMemory(flatten.Memory.Span[..compressed.Length]);
                carrier.Payload = carrier.PayloadOwner.Memory;
                _checksum ^= carrier.Payload.Length;
            }));
            rows.Add(Measure($"decode.pooled.{corpus}", () =>
            {
                using var output = PooledBufferStream.Rent(NetworkSettings.MaxMessageSize);
                SnappyBlockDecoder.Decode(input, output);
                using var carrier = ForwardPacketCarrier.Rent();
                carrier.PayloadOwner = output.Retain();
                carrier.Payload = output.Memory;
                _checksum ^= carrier.Payload.Length;
            }));
        }

        var options = new PacketIdMapperOptions();
        options.Register<Ping>();
        var mapper = new DefaultPacketIdMapper(Options.Create(options), NullLogger<DefaultPacketIdMapper>.Instance);
        var mappingLock = new object();
        var legacyIds = new Dictionary<Type, Hive.Codec.Abstractions.PacketId> { [typeof(Ping)] = mapper.GetPacketId(typeof(Ping)) };
        rows.Add(Measure("mapper.locked.Ping", () =>
        {
            lock (mappingLock) _checksum ^= legacyIds[typeof(Ping)].GetHashCode();
        }));
        rows.Add(Measure("mapper.snapshot.Ping", () => _checksum ^= mapper.GetPacketId(typeof(Ping)).GetHashCode()));
        var codec = new MemoryPackPacketCodec(mapper, new DefaultCustomCodecProvider());
        var ping = new Ping { From = Guid.Empty, To = Guid.Empty, Ttl = 1, SeqId = 2, SendTime = 3 };
        rows.Add(Measure("encode.legacy.Ping", () =>
        {
            using var stream = RecycleMemoryStreamManagerHolder.Shared.GetStream();
            _checksum ^= codec.Encode(ping, stream);
        }));
        rows.Add(Measure("encode.pooled.Ping", () =>
        {
            using var stream = PooledBufferStream.Rent();
            _checksum ^= codec.Encode(ping, stream);
        }));
        using var session = new ProbeSession();
        var dispatcher = new DefaultDispatcher(codec, NullLogger<DefaultDispatcher>.Instance);
        dispatcher.AddHandler<Ping>(_ => _checksum++);
        rows.Add(Measure("encode.send.dispatch.pooled.Ping", () =>
        {
            dispatcher.SendAsync(session, ping).GetAwaiter().GetResult();
            dispatcher.Dispatch(session, ping);
        }));

        rows.AddRange(await MeasureTcpAsync());

        Directory.CreateDirectory(outputDirectory);
        var csv = "scenario,iterations,samples,median_ns_per_operation,median_managed_bytes_per_operation\n";
        foreach (var row in rows)
            csv += string.Create(CultureInfo.InvariantCulture, $"{row.Name},{row.Iterations},{row.Samples},{row.Nanoseconds:F2},{row.Bytes:F2}\n");
        File.WriteAllText(Path.Combine(outputDirectory, "benchmark.csv"), csv);
        var report = $"# Release microbenchmarks\n\nRuntime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}.\n\n";
        report += $"Microbenchmark warmup: 10,000 operations per row; {Samples} samples of {Iterations:N0} operations. TCP warmup: 2,000 packets per mode; {Samples} samples of 10,000 packets. Median of warmed samples; same deterministic payloads, no enabled logging.\n\n";
        report += "| Scenario | Median ns/op | Managed bytes/op |\n| --- | ---: | ---: |\n";
        foreach (var row in rows)
        {
            report += string.Create(CultureInfo.InvariantCulture, $"| {row.Name} | {row.Nanoseconds:F2} | {row.Bytes:F2} |\n");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{row.Name,-37} {row.Nanoseconds,10:F2} ns/op {row.Bytes,8:F2} B/op"));
        }
        report += "\nLegacy rows reproduce the prior allocation/copy sites with the current dependency versions; they are not a separate build of the previous commit. Encode rows measure serialization only. The combined send row uses a synchronous fake transport and is not a network throughput benchmark. CPU timings vary with host load and runtime optimization. Raw Snappy wire compatibility is tested separately. Compression remains the existing Snappier compressor. TCP rows use one warmed loopback connection, 512-byte payloads, NoDelay and alternating sequence/frame batches (seven samples of 10,000 packets each); time includes receipt of the full batch and allocation is process-wide, including runtime scheduling. The other rows measure allocations on the benchmark thread.\n";
        File.WriteAllText(Path.Combine(outputDirectory, "benchmark.md"), report);
        Console.WriteLine($"Saved {Path.GetFullPath(outputDirectory)} (checksum {_checksum}).");
    }

    private static async Task<Result[]> MeasureTcpAsync()
    {
        const int packets = 10000;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var connect = client.ConnectAsync(listener.LocalEndPoint!);
        using var accepted = await listener.AcceptAsync();
        await connect;
        using var sender = new TcpSession(10, client, NullLogger<TcpSession>.Instance);
        using var receiver = new TcpSession(11, accepted, NullLogger<TcpSession>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var count = 0;
        var expected = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new byte[NetworkSettings.PacketBodyOffset + 512];
        var sequence = new ReadOnlySequence<byte>(frame.AsMemory(NetworkSettings.PacketBodyOffset));
        receiver.ReceiveHandler = (_, body, _) =>
        {
            if (body.Length != 512) throw new Exception("Benchmark received an invalid packet.");
            if (Interlocked.Increment(ref count) == Volatile.Read(ref expected)) completion.TrySetResult();
            return ValueTask.CompletedTask;
        };
        var receiveTask = receiver.StartAsync(cts.Token);
        var sendTask = sender.StartAsync(cts.Token);
        var times = new[] { new double[Samples], new double[Samples] };
        var allocations = new[] { new double[Samples], new double[Samples] };
        try
        {
            await Batch(false, 2000);
            await Batch(true, 2000);
            for (var sample = 0; sample < Samples; sample++)
                for (var lane = 0; lane < 2; lane++)
                {
                    // Alternate which mode goes first to reduce drift/order bias.
                    var mode = (sample + lane) & 1;
                    var measured = await Batch(mode == 1, packets);
                    times[mode][sample] = measured.Nanoseconds;
                    allocations[mode][sample] = measured.Bytes;
                }
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(receiveTask, sendTask);
        }
        var results = new Result[2];
        for (var mode = 0; mode < 2; mode++)
        {
            Array.Sort(times[mode]);
            Array.Sort(allocations[mode]);
            results[mode] = new Result(mode == 0 ? "tcp.sequence.512B" : "tcp.frame.512B",
                times[mode][Samples / 2], allocations[mode][Samples / 2], packets, Samples);
        }
        return results;

        async Task<(double Nanoseconds, double Bytes)> Batch(bool contiguous, int iterations)
        {
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref expected, count + iterations);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                var success = contiguous
                    ? await sender.TrySendFrameAsync(frame, cts.Token)
                    : await sender.TrySendAsync(sequence, cts.Token);
                if (!success) throw new Exception("TCP benchmark send failed.");
            }
            await completion.Task.WaitAsync(cts.Token);
            var elapsed = Stopwatch.GetTimestamp() - started;
            return (elapsed * (1e9 / Stopwatch.Frequency) / iterations,
                (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)iterations);
        }
    }

    private static Result Measure(string name, Action operation)
    {
        for (var i = 0; i < 10000; i++) operation();
        var time = new double[Samples];
        var allocation = new double[Samples];
        for (var sample = 0; sample < Samples; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < Iterations; i++) operation();
            var elapsed = Stopwatch.GetTimestamp() - start;
            allocation[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
            time[sample] = elapsed * (1e9 / Stopwatch.Frequency) / Iterations;
        }
        Array.Sort(time);
        Array.Sort(allocation);
        return new Result(name, time[Samples / 2], allocation[Samples / 2]);
    }
    private sealed record Result(string Name, double Nanoseconds, double Bytes, int Iterations = BenchmarkRunner.Iterations, int Samples = BenchmarkRunner.Samples);
}
