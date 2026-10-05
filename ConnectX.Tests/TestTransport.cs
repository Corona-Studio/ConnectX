using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Tests;

internal sealed class TestSession(int id) : ISession
{
    public SessionId Id => id;
    public IPEndPoint? LocalEndPoint => new(IPAddress.Loopback, 1000);
    public IPEndPoint? RemoteEndPoint => new(IPAddress.Loopback, 2000);
    public event SessionReceivedHandler? OnMessageReceived;
    public ConcurrentQueue<byte[]> Sent { get; } = new();
    public Action? OnSend { get; set; }
    public TaskCompletionSource? BlockSends { get; set; }
    public bool Closed { get; private set; }
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public void Receive(byte[] bytes) => OnMessageReceived?.Invoke(this, new ReadOnlySequence<byte>(bytes));
    public void Close() => Closed = true;
    public async ValueTask SendAsync(Stream stream, CancellationToken token = default) => await TrySendAsync(stream, token);
    public async ValueTask<bool> TrySendAsync(Stream stream, CancellationToken token = default)
    {
        if (BlockSends != null) await BlockSends.Task.WaitAsync(token);
        if (Closed) return false;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, token);
        Sent.Enqueue(copy.ToArray());
        OnSend?.Invoke();
        return true;
    }
}

internal sealed class TestCodec : IPacketCodec
{
    public ConcurrentQueue<object> Encoded { get; } = new();
    public int Encode<T>(T message, Stream stream)
    {
        Encoded.Enqueue(message!);
        stream.WriteByte(1);
        return 1;
    }
    public object? Decode(ReadOnlySequence<byte> buffer) => null;
}
