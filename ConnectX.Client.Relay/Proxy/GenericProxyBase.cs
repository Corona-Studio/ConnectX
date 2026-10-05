using System.Buffers;
using System.Net.Sockets;
using System.Threading.Channels;
using ConnectX.Client.Messages.Proxy;
using Hive.Common.Shared.Helpers;
using Hive.Common.Shared.Pooling;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public abstract class GenericProxyBase : IDisposable
{
    private const int DefaultReceiveBufferSize = 20480;
    private const int RetryInterval = 500;
    private const int TryTime = 20;

    private readonly CancellationTokenSource _combinedTokenSource;
    private readonly CancellationTokenSource _internalTokenSource;

    protected readonly CancellationToken CancellationToken;

    protected readonly ILogger Logger;

    public readonly List<Func<ForwardPacketCarrier, bool>> OutwardSenders = [];
    private Func<ForwardPacketCarrier, ValueTask<bool>>[] _outwardAsyncSenders = [];

    public void AddOutwardAsyncSender(Func<ForwardPacketCarrier, ValueTask<bool>> sender)
    {
        lock (this) _outwardAsyncSenders = [.. _outwardAsyncSenders, sender];
    }

    public void RemoveOutwardAsyncSender(Func<ForwardPacketCarrier, ValueTask<bool>> sender)
    {
        lock (this) _outwardAsyncSenders = _outwardAsyncSenders.Where(x => x != sender).ToArray();
    }
    public readonly TunnelIdentifier TunnelIdentifier;

    private bool _disposed;
    private int _started;
    private Socket? _innerSocket;
    private object? _proxyInfoForLog;
    private object ProxyInfoForLog => _proxyInfoForLog ??= GetProxyInfoForLog();

    protected Channel<ForwardPacketCarrier>? InwardBuffersQueue;
    protected Channel<ForwardPacketCarrier>? OutwardBuffersQueue;

    protected GenericProxyBase(
        TunnelIdentifier tunnelIdentifier,
        CancellationToken cancellationToken,
        ILogger<GenericProxyBase> logger)
    {
        TunnelIdentifier = tunnelIdentifier;
        _internalTokenSource = new CancellationTokenSource();
        _combinedTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(_internalTokenSource.Token, cancellationToken);
        CancellationToken = _combinedTokenSource.Token;
        Logger = logger;
    }

    private ushort LocalServerPort => TunnelIdentifier.LocalRealPort;
    private ushort RemoteClientPort => TunnelIdentifier.RemoteRealPort;

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            _internalTokenSource.Cancel();
            _combinedTokenSource.Dispose();
            _internalTokenSource.Dispose();

            CompleteAndDrain(InwardBuffersQueue);
            CompleteAndDrain(OutwardBuffersQueue);

            InwardBuffersQueue = null;
            OutwardBuffersQueue = null;

            OutwardSenders.Clear();
            lock (this) _outwardAsyncSenders = [];

            _innerSocket?.Shutdown(SocketShutdown.Both);
            _innerSocket?.Close();
            _innerSocket?.Dispose();
            _innerSocket = null;

            Logger.LogProxyDisposed(ProxyInfoForLog, LocalServerPort);
        }
        catch (Exception e)
        {
            Logger.LogProxyDisposeEx(e, ProxyInfoForLog);
        }

        GC.SuppressFinalize(this);
    }

    private void ResetChannels()
    {
        CompleteAndDrain(InwardBuffersQueue);
        InwardBuffersQueue = Channel.CreateUnbounded<ForwardPacketCarrier>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        CompleteAndDrain(OutwardBuffersQueue);
        OutwardBuffersQueue = Channel.CreateBounded<ForwardPacketCarrier>(new BoundedChannelOptions(128)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public event Action<TunnelIdentifier, GenericProxyBase>? OnRealServerConnected;
    public event Action<TunnelIdentifier, GenericProxyBase>? OnRealServerDisconnected;

    public virtual void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GenericProxyBase));
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Logger.LogStartingProxy(ProxyInfoForLog);

        ResetChannels();

        TaskHelper.FireAndForget(() => OuterSendLoopAsync(CancellationToken));
        TaskHelper.FireAndForget(() => InnerSendLoopAsync(CancellationToken));
        TaskHelper.FireAndForget(() => InnerReceiveLoopAsync(CancellationToken));

        Logger.LogProxyStarted(ProxyInfoForLog);
    }

    private static void CompleteAndDrain(Channel<ForwardPacketCarrier>? channel)
    {
        if (channel == null) return;
        channel.Writer.TryComplete();
        while (channel.Reader.TryRead(out var packet)) packet.Dispose();
    }

    protected async Task OuterSendLoopAsync(CancellationToken cancellationToken)
    {
        var channel = OutwardBuffersQueue;
        if (channel == null) return;
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            while (channel.Reader.TryRead(out var packet))
            {
                try
                {
                    // Retry the current packet in place. Re-enqueuing into a full
                    // bounded queue would deadlock its only forwarding consumer.
                    while (packet.TryCount <= TryTime)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (packet.TryCount > 0)
                            await Task.Delay(RetryInterval, cancellationToken);
                        packet.LastTryTime = Environment.TickCount;
                        var sent = false;
                        foreach (var sender in _outwardAsyncSenders)
                        {
                            if (!await sender(packet)) continue;
                            sent = true;
                            break;
                        }
                        if (!sent)
                            foreach (var sender in OutwardSenders)
                            {
                                if (!sender(packet)) continue;
                                sent = true;
                                break;
                            }
                        if (sent) break;
                        packet.TryCount++;
                    }
                }
                finally
                {
                    packet.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            CompleteAndDrain(channel);
        }
    }

    /// <summary>
    ///     需要在GenericProxyManager里调用
    /// </summary>
    /// <param name="message"></param>
    public void OnReceiveMcPacketCarrier(ForwardPacketCarrier message)
    {
        Logger.LogReceivedPacket(ProxyInfoForLog, message.Payload.Length, RemoteClientPort);

        if (InwardBuffersQueue == null)
        {
            message.Dispose();
            return;
        }

        if (InwardBuffersQueue.Writer.TryWrite(message)) return;

        Logger.LogFailedToSendMcPacketCarrier(ProxyInfoForLog, message.SelfRealPort, message.TargetRealPort,
            message.LastTryTime);
        message.Dispose();
    }

    protected virtual object GetProxyInfoForLog()
    {
        return new
        {
            Type = "Client",
            LocalMcPort = LocalServerPort
        };
    }

    protected virtual async Task InnerSendLoopAsync(CancellationToken cancellationToken)
    {
        var channel = InwardBuffersQueue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!CheckSocketValid())
                {
                    await Task.Delay(20, cancellationToken);
                    continue;
                }
                if (InwardBuffersQueue == null) break;

                var reader = InwardBuffersQueue.Reader;

                while (await reader.WaitToReadAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    while (reader.TryRead(out var packetCarrier))
                    {
                        try
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            var totalLen = packetCarrier.Payload.Length;
                            var sentLen = 0;
                            var buffer = packetCarrier.Payload;

                            while (sentLen < totalLen)
                            {
                                var count = await _innerSocket!.SendAsync(buffer[sentLen..], SocketFlags.None, CancellationToken);
                                if (count <= 0) throw new IOException("Local socket closed during a send.");
                                sentLen += count;
                            }

                            Logger.LogSentPacket(ProxyInfoForLog, totalLen, LocalServerPort);
                        }
                        catch (SocketException ex)
                        {
                            Logger.LogFailedToSendPacket(ex, ProxyInfoForLog, LocalServerPort);

                            if (ex.SocketErrorCode == SocketError.ConnectionAborted)
                                break;
                        }
                        catch (ObjectDisposedException ex)
                        {
                            _innerSocket = null;
                            Logger.LogFailedToSendPacket(ex, ProxyInfoForLog, LocalServerPort);
                        }
                        finally
                        {
                            packetCarrier.Dispose();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            CompleteAndDrain(channel);
        }
    }

    private bool CheckSocketValid()
    {
        lock (this)
        {
            if (_innerSocket is { Connected: true }) return true;
            try
            {
                InitConnectionSocket();
                return true;
            }
            catch (SocketException e)
            {
                Logger.LogFailedToInitConnectionSocket(e, ProxyInfoForLog, e.SocketErrorCode);

                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private void InitConnectionSocket()
    {
        if (_innerSocket is { Connected: true })
        {
            _innerSocket.Shutdown(SocketShutdown.Both);
            _innerSocket.Close();
        }

        _innerSocket?.Dispose();
        _innerSocket = CreateSocket();
    }

    protected abstract Socket CreateSocket();

    protected virtual async Task InnerReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var channel = OutwardBuffersQueue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (OutwardBuffersQueue == null) break;

                var writer = OutwardBuffersQueue.Writer;

                if (_innerSocket is not { Connected: true })
                {
                    if (!CheckSocketValid())
                    {
                        await Task.Delay(20, cancellationToken);
                        continue;
                    }
                    await Task.Yield();
                    continue;
                }

                var carrier = ForwardPacketCarrier.Rent();
                var queued = false;
                try
                {
                    var bufferOwner = PooledBufferStream.Rent(DefaultReceiveBufferSize);
                    carrier.PayloadOwner = bufferOwner;
                    var len = await _innerSocket.ReceiveAsync(
                        bufferOwner.GetMemory(DefaultReceiveBufferSize), SocketFlags.None, CancellationToken);

                    if (len == 0)
                    {
                        Logger.LogReceivedZeroBytes(ProxyInfoForLog, LocalServerPort);
                        Logger.LogServerDisconnected(ProxyInfoForLog, LocalServerPort);
                        _innerSocket?.Shutdown(SocketShutdown.Both);
                        _innerSocket?.Dispose();
                        _innerSocket = null;
                        InvokeRealServerDisconnected();
                        writer.TryComplete();
                        break;
                    }

                    bufferOwner.Advance(len);
                    Logger.LogBytesReceived(ProxyInfoForLog, len, LocalServerPort);
                    carrier.Payload = bufferOwner.Memory;
                    carrier.SelfRealPort = LocalServerPort;
                    carrier.TargetRealPort = RemoteClientPort;
                    // The first send is immediate; only failed attempts wait.
                    carrier.LastTryTime = Environment.TickCount - RetryInterval;
                    await writer.WriteAsync(carrier, cancellationToken);
                    queued = true;
                }
                finally
                {
                    if (!queued) carrier.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ChannelClosedException) when (_disposed) { }
        finally
        {
            channel?.Writer.TryComplete();
        }
    }

    protected void InvokeRealServerDisconnected()
    {
        OnRealServerDisconnected?.Invoke(TunnelIdentifier, this);
    }

    protected void InvokeRealServerConnected()
    {
        OnRealServerConnected?.Invoke(TunnelIdentifier, this);
    }
}

internal static partial class GenericProxyBaseLoggers
{
    [LoggerMessage(LogLevel.Information, "[PROXY] Starting proxy: {ProxyInfo}")]
    public static partial void LogStartingProxy(this ILogger logger, object proxyInfo);

    [LoggerMessage(LogLevel.Information, "[PROXY] Proxy started: {ProxyInfo}")]
    public static partial void LogProxyStarted(this ILogger logger, object proxyInfo);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Received packet with length [{Length}] from {RemoteClientPort}")]
    public static partial void LogReceivedPacket(this ILogger logger, object proxyInfo, int length,
        ushort remoteClientPort);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Sent {PacketLength} bytes to {LocalRealMcPort}")]
    public static partial void LogSentPacket(this ILogger logger, object proxyInfo, int packetLength,
        ushort localRealMcPort);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Failed to send packet to {LocalRealMcPort}")]
    public static partial void LogFailedToSendPacket(this ILogger logger, Exception ex, object proxyInfo,
        ushort localRealMcPort);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Failed to init connection socket, error code: {ErrorCode}")]
    public static partial void LogFailedToInitConnectionSocket(this ILogger logger, Exception ex, object proxyInfo,
        SocketError errorCode);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Received 0 bytes from {LocalRealMcPort}")]
    public static partial void LogReceivedZeroBytes(this ILogger logger, object proxyInfo, ushort localRealMcPort);

    [LoggerMessage(LogLevel.Trace, "[{ProxyInfo}] Received {Len} bytes from {McPort}")]
    public static partial void LogBytesReceived(this ILogger logger, object proxyInfo, int len, ushort mcPort);

    [LoggerMessage(LogLevel.Error, "[{ProxyInfo}] Server {LocalRealMcPort} disconnected")]
    public static partial void LogServerDisconnected(this ILogger logger, object proxyInfo, ushort localRealMcPort);

    [LoggerMessage(LogLevel.Information, "[{ProxyInfo}] Proxy disposed, local port: {LocalPort}")]
    public static partial void LogProxyDisposed(this ILogger logger, object proxyInfo, ushort localPort);

    [LoggerMessage(LogLevel.Warning, "[{ProxyInfo}] Proxy dispose throws an exception")]
    public static partial void LogProxyDisposeEx(this ILogger logger, Exception ex, object proxyInfo);

    [LoggerMessage(LogLevel.Warning,
        "[{ProxyInfo}] Failed to send McPacketCarrier, self port [{selfRealPort}], target port [{targetRealPort}], last try time: {LastTryTime}, maybe is because proxy is disposed.")]
    public static partial void LogFailedToSendMcPacketCarrier(
        this ILogger logger,
        object proxyInfo,
        ushort selfRealPort,
        ushort targetRealPort,
        int lastTryTime);
}