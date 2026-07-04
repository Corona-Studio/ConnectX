using ConnectX.Client.Interfaces;
using ConnectX.Client.Route.Packet;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Network.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission.Connections;

public abstract class ConnectionBase : ISender, ICanPing<Guid>
{
    protected const int Timeout = 5000;
    protected const int BufferLength = 256;
    protected readonly IPacketCodec Codec;

    protected readonly IHostApplicationLifetime Lifetime;
    protected readonly ILogger Logger;

    protected readonly string Source;

    protected ConnectionBase(
        string source,
        Guid targetId,
        IDispatcher dispatcher,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        ILogger logger)
    {
        Source = source;

        Dispatcher = dispatcher;
        To = targetId;

        Codec = codec;
        Lifetime = lifetime;
        Logger = logger;
    }

    public bool IsConnected { get; protected set; }

    public Guid To { get; }
    public IDispatcher Dispatcher { get; }
    public bool ShouldUseDispatcherSenderInfo => false;

    public void SendPingPacket<T>(T packet) where T : RouteLayerPacket
    {
        SendData(packet);
    }

    public abstract void Send(ReadOnlyMemory<byte> payload);

    public void SendData<T>(T data)
    {
        using var stream = RecycleMemoryStreamManagerHolder.Shared.GetStream();
        Codec.Encode(data, stream);

        stream.Seek(0, SeekOrigin.Begin);

        var buffer = stream.GetBuffer();

        Send(buffer.AsMemory(0, (int)stream.Length));
    }

    public virtual void Disconnect()
    {
        IsConnected = false;
    }

    public abstract Task<bool> ConnectAsync(CancellationToken token);
}

internal static partial class ConnectionBaseLoggers
{
    [LoggerMessage(LogLevel.Trace,
        "[{source}] Receive first handshake packet, send second handshake packet. (TargetId: {Id})")]
    public static partial void LogReceiveFirstShakeHandPacket(this ILogger logger, string source, Guid id);

    [LoggerMessage(LogLevel.Error,
        "[{source}] Decode message with payload length [{Length}] failed. (TargetId: {Id})")]
    public static partial void LogDecodeMessageFailed(this ILogger logger, string source, long length, Guid id);

    [LoggerMessage(LogLevel.Debug, "[{source}] Resend coroutine started. (TargetId: {Id})")]
    public static partial void LogResendCoroutineStarted(this ILogger logger, string source, Guid id);

    [LoggerMessage(LogLevel.Information, "[{source}] Connecting to {TargetId}")]
    public static partial void LogConnectingTo(this ILogger logger, string source, Guid targetId);

    [LoggerMessage(LogLevel.Error, "[{source}] Connect failed, no SYN ACK response. (TargetId: {Id})")]
    public static partial void LogConnectFailed(this ILogger logger, string source, Guid id);
}