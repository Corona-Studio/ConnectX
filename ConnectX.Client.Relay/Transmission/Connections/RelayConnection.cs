using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Messages.Proxy;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Relay.Datagram;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Hive.Network.Shared;
using Hive.Network.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snappier;
using TaskHelper = Hive.Common.Shared.Helpers.TaskHelper;

namespace ConnectX.Client.Transmission.Connections;

public sealed class RelayConnection : ConnectionBase, IDatagramTransmit<RelayDatagram>
{
    public override bool IsDirectConnection => false;

    private static readonly ConcurrentDictionary<IPEndPoint, ISession> RelayServerDataLinkPool = new();
    private static readonly ConcurrentDictionary<IPEndPoint, SemaphoreSlim> ConnectionLocks = new();
    private static readonly ConcurrentDictionary<IPEndPoint, CancellationTokenSource> ConnectionCts = new();
    private static readonly ConcurrentDictionary<IPEndPoint, uint> ConnectionRefCount = new();

    private readonly IPEndPoint _relayEndPoint;
    private readonly ClientPacketDispatcher _clientPacketDispatcher;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnector<TcpSession> _tcpConnector;

    private DateTime _lastHeartBeatTime;

    private CancellationToken _linkCt;
    private ISession? _relayServerDataLink;
    private ISession? _relayServerWorkerLink;

    public RelayConnection(
        Guid targetId,
        IPEndPoint relayEndPoint,
        IDispatcher dispatcher,
        ClientPacketDispatcher clientPacketDispatcher,
        IRoomInfoManager roomInfoManager,
        IServerLinkHolder serverLinkHolder,
        IConnector<TcpSession> tcpConnector,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        IServiceProvider serviceProvider,
        ILogger<RelayConnection> logger) : base("RELAY_CONN", targetId, dispatcher, codec, lifetime, logger)
    {
        _relayEndPoint = relayEndPoint;
        _clientPacketDispatcher = clientPacketDispatcher;
        _tcpConnector = tcpConnector;
        _roomInfoManager = roomInfoManager;
        _serverLinkHolder = serverLinkHolder;
        _serviceProvider = serviceProvider;

        dispatcher.AddHandler<UnwrappedRelayDatagram>(OnUnwrappedRelayDatagramReceived);
        dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
    }

    public void SendDatagram(RelayDatagram datagram)
    {
        if (!IsConnected || _relayServerDataLink == null)
        {
            Logger.LogSendFailedBecauseLinkNotReadyYet(Source, To);
            return;
        }

        Dispatcher.SendAsync(_relayServerDataLink, datagram, _linkCt).Forget();
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> obj)
    {
        _lastHeartBeatTime = DateTime.UtcNow;
        Logger.LogHeartbeatReceivedFromServer();
    }

    private void OnUnwrappedRelayDatagramReceived(MessageContext<UnwrappedRelayDatagram> ctx)
    {
        if (ctx.Message.From != To)
            // we want to make sure we are processing the right packet
            return;

        if (!IsConnected || _relayServerDataLink == null)
        {
            Logger.LogReceiveFailedBecauseLinkDown(Source, To);
            return;
        }

        var datagram = ctx.Message;
        var sequence = new ReadOnlySequence<byte>(datagram.Payload);
        var message = Codec.Decode(sequence);

        if (message == null)
        {
            Logger.LogDecodeMessageFailed(Source, datagram.Payload.Length, To);

            return;
        }

        _clientPacketDispatcher.DispatchPacket(datagram);
        Dispatcher.Dispatch(_relayServerDataLink, message.GetType(), message);
    }

    public override void Send(ReadOnlyMemory<byte> payload)
    {
        SendDatagram(new RelayDatagram(_serverLinkHolder.UserId, To, payload));
    }

    private async Task<bool> EstablishDataLinkAsync(CancellationToken token)
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
            return false;

        Logger.LogConnectingToRelayServer(_relayEndPoint);

        SemaphoreSlim? connectionLock = null;

        try
        {
            // Make a random delay to avoid duplicate creation
            await Task.Delay(Random.Shared.Next(100, 1000), token);

            if (!ConnectionLocks.TryGetValue(_relayEndPoint, out connectionLock))
            {
                connectionLock = new SemaphoreSlim(1, 1);
                ConnectionLocks.TryAdd(_relayEndPoint, connectionLock);
            }

            // Wait for the connection lock
            Logger.LogWaitingForConnectionLock(_relayEndPoint);
            await connectionLock.WaitAsync(token);

            var isReusingLink = false;
            ISession? session;

            // If we can find the link in the pool, we can reuse it.
            if (RelayServerDataLinkPool.TryGetValue(_relayEndPoint, out var link))
            {
                isReusingLink = true;
                session = link;
            }
            else
            {
                session = await _tcpConnector.ConnectAsync(_relayEndPoint, token);
            }

            if (!ConnectionCts.TryGetValue(_relayEndPoint, out var linkCts))
            {
                var newCts = new CancellationTokenSource();

                linkCts = newCts;

                ConnectionCts.TryAdd(_relayEndPoint, linkCts);
            }

            _linkCt = linkCts.Token;

            if (session == null)
            {
                Logger.LogConnectFailed(Source, To);
                Logger.LogFailedToConnectToRelayServer(_relayEndPoint);

                return false;
            }

            session.BindTo(Dispatcher);

            ConnectionRefCount.AddOrUpdate(_relayEndPoint, _ => 1, (_, u) => u + 1);

            if (!isReusingLink)
            {
                session.StartAsync(_linkCt).Forget();

                await Task.Delay(1000, token);

                var linkCreationReq = new CreateRelayDataLinkMessage
                {
                    UserId = _serverLinkHolder.UserId,
                    RoomId = _roomInfoManager.CurrentGroupInfo.RoomId
                };

                await Dispatcher.SendAndListenOnce<CreateRelayDataLinkMessage, RelayDataLinkCreatedMessage>(
                    session,
                    linkCreationReq,
                    token);

                RelayServerDataLinkPool.AddOrUpdate(_relayEndPoint, _ => session, (_, oldSession) =>
                {
                    Logger.LogClosingOldLink(oldSession.Id);

                    oldSession.OnMessageReceived -= Dispatcher.Dispatch;
                    oldSession.Close();

                    return session;
                });

                Logger.LogConnectedToRelayServer(_relayEndPoint);
            }
            else
            {
                Logger.LogConnectedToRelayServerUsingPool(_relayEndPoint);
            }

            _relayServerDataLink = session;

            return true;
        }
        finally
        {
            ArgumentNullException.ThrowIfNull(connectionLock);

            if (connectionLock.CurrentCount == 0)
                connectionLock.Release();
        }
    }

    private async Task<bool> EstablishWorkerLinkAsync(CancellationToken token)
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
            return false;

        Logger.LogEstablishingWorkerSession(_relayEndPoint);

        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var session = await _tcpConnector.ConnectAsync(_relayEndPoint, token);

        if (session == null)
        {
            Logger.LogConnectFailed(Source, To);
            Logger.LogFailedToEstablishingWorkerSession(_relayEndPoint);
            return false;
        }

        //session.Socket!.LingerState = new LingerOption(true, 10);
        session.Socket!.NoDelay = true;

        session.BindTo(dispatcher);

        session.StartAsync(_linkCt).Forget();

        await Task.Delay(1000, token);

        var linkCreationReq = new CreateRelayWorkerLinkMessage
        {
            UserId = _serverLinkHolder.UserId,
            RelayTo = To,
            RoomId = _roomInfoManager.CurrentGroupInfo.RoomId
        };

        await dispatcher.SendAndListenOnce<CreateRelayWorkerLinkMessage, RelayWorkerLinkCreatedMessage>(
            session,
            linkCreationReq,
            token);

        await Task.Delay(1000, token);

        // Switch to streaming mode
        session.OnMessageReceived -= dispatcher.Dispatch;
        session.OnMessageReceived += SessionOnOnMessageReceived;

        _relayServerWorkerLink = session;

        return true;
    }

    private void SessionOnOnMessageReceived(ISession session, ReadOnlySequence<byte> buffer)
    {
        if (_relayServerDataLink == null)
            return;

        try
        {
            var dataLength = (int)buffer.Length;

            using var memoryOwner = MemoryPool<byte>.Shared.Rent(dataLength);
            buffer.CopyTo(memoryOwner.Memory.Span);

            var decompressedOwner = Snappy.DecompressToMemory(memoryOwner.Memory.Span[..dataLength]);
            var carrier = new ForwardPacketCarrier
            {
                PayloadOwner = decompressedOwner,
                Payload = decompressedOwner.Memory,
                LastTryTime = 0,
                TryCount = 0
            };

            Dispatcher.Dispatch(session, carrier);
        }
        catch (Exception e)
        {
            Logger.LogFailedToDecompressMessage(e, buffer.Length, Source, To);
        }
    }

    public override async Task<bool> ConnectAsync(CancellationToken token)
    {
        try
        {
            var dataLinkCreated = await EstablishDataLinkAsync(token);
            var workerLinkCreated = await EstablishWorkerLinkAsync(token);

            IsConnected = dataLinkCreated && workerLinkCreated;

            TaskHelper.FireAndForget(SendHeartBeatAsync);
            TaskHelper.FireAndForget(CheckServerLivenessAsync);

            return true;
        }
        catch (TaskCanceledException)
        {
            IsConnected = false;

            return false;
        }
        catch (SocketException e)
        {
            IsConnected = false;

            Logger.LogFailedToConnectToRelayServerWithException(e, _relayEndPoint);
            return false;
        }
    }

    private async Task SendHeartBeatAsync()
    {
        Logger.LogHeartbeatStarted();

        while (_linkCt is { IsCancellationRequested: false } && _relayServerDataLink != null)
        {
            await Dispatcher.SendAsync(_relayServerDataLink, new HeartBeat(), _linkCt);
            await Task.Delay(TimeSpan.FromSeconds(10), _linkCt);
        }

        Logger.LogHeartbeatStopped();
    }

    private async Task CheckServerLivenessAsync()
    {
        try
        {
            Logger.LogServerLivenessProbeStarted(_relayEndPoint);

            // Set the last for init
            _lastHeartBeatTime = DateTime.UtcNow;

            while (_linkCt is { IsCancellationRequested: false } &&
                   IsConnected &&
                   _relayServerDataLink != null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _linkCt);

                var lastReceiveTimeSeconds = (DateTime.UtcNow - _lastHeartBeatTime).TotalSeconds;

                if (lastReceiveTimeSeconds <= 15)
                    continue;

                Logger.LogServerHeartbeatTimeout(_relayEndPoint, lastReceiveTimeSeconds);

                break;
            }
        }
        catch (TaskCanceledException)
        {
            // ignored
            _lastHeartBeatTime = default;
            IsConnected = false;
        }

        Logger.LogServerLivenessProbeStopped(_relayEndPoint);
    }

    public override void Disconnect()
    {
        base.Disconnect();

        if (_relayServerWorkerLink != null)
        {
            _relayServerWorkerLink.OnMessageReceived -= SessionOnOnMessageReceived;
            _relayServerWorkerLink.Close();
        }

        if (!ConnectionRefCount.TryGetValue(_relayEndPoint, out var count)) return;
        if (!ConnectionRefCount.TryUpdate(_relayEndPoint, count - 1, count))
        {
            Logger.LogFailedToUpdateRefCountForLink(_relayEndPoint);
            return;
        }

        if (_relayServerDataLink != null)
            _relayServerDataLink.OnMessageReceived -= Dispatcher.Dispatch;
        _relayServerDataLink = null;

        if (count > 1)
            // There are still other connections using this link, so we don't close it.
            return;

        RelayServerDataLinkPool.TryRemove(_relayEndPoint, out _);

        if (ConnectionCts.TryRemove(_relayEndPoint, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_relayServerDataLink == null) return;
        _relayServerDataLink.Close();

        Logger.LogRelayDisconnected(_relayEndPoint);
    }

    public void SendByWorker(ReadOnlyMemory<byte> data)
    {
        var dataLength = data.Length;

        using var memoryOwner = MemoryPool<byte>.Shared.Rent(dataLength);
        data.CopyTo(memoryOwner.Memory);

        var ms = RecycleMemoryStreamManagerHolder.Shared.GetStream();
        var seq = new ReadOnlySequence<byte>(memoryOwner.Memory[..dataLength]);

        if (data.Length <= ms.Length)
            Logger.LogUnderperformedCompression(data.Length, (int)ms.Length);

        Snappy.Compress(seq, ms);
        ms.Seek(0, SeekOrigin.Begin);

        _relayServerWorkerLink?.TrySendAsync(ms, _linkCt).Forget();
    }
}

internal static partial class RelayConnectionLoggers
{
    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connecting to relay server [{relayEndPoint}]")]
    public static partial void LogConnectingToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Failed to connect to relay server [{relayEndPoint}]")]
    public static partial void LogFailedToConnectToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Failed to connect to relay server [{relayEndPoint}]")]
    public static partial void LogFailedToConnectToRelayServerWithException(this ILogger logger, Exception ex,
        IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Heartbeat started")]
    public static partial void LogHeartbeatStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Heartbeat stopped")]
    public static partial void LogHeartbeatStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connected to relay server [{relayEndPoint}]")]
    public static partial void LogConnectedToRelayServer(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error,
        "[RELAY_CONN] Send failed because link is not ready yet. (Source: {source}, Target: {target})")]
    public static partial void LogSendFailedBecauseLinkNotReadyYet(this ILogger logger, string source, Guid target);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Connected to relay server [{relayEndPoint}] using pool")]
    public static partial void LogConnectedToRelayServerUsingPool(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Debug, "[RELAY_CONN] Waiting for connection lock [{relayEndPoint}]")]
    public static partial void LogWaitingForConnectionLock(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error,
        "[RELAY_CONN] Receive failed because link is down. (Source: {source}, Target: {target})")]
    public static partial void LogReceiveFailedBecauseLinkDown(this ILogger logger, string source, Guid target);

    [LoggerMessage(LogLevel.Critical, "[RELAY_CONN] Failed to update ref count for link [{endPoint}]")]
    public static partial void LogFailedToUpdateRefCountForLink(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information,
        "[RELAY_CONN] Link with server [{relayEndPoint}] is down, last heartbeat received [{seconds} seconds ago]")]
    public static partial void LogServerHeartbeatTimeout(this ILogger logger, IPEndPoint relayEndPoint, double seconds);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Closing old link [{sessionId}]")]
    public static partial void LogClosingOldLink(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Server liveness probe started for [{relayEndPoint}]")]
    public static partial void LogServerLivenessProbeStarted(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Server liveness probe stopped for [{relayEndPoint}]")]
    public static partial void LogServerLivenessProbeStopped(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Relay disconnected [{relayEndPoint}]")]
    public static partial void LogRelayDisconnected(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Information, "[RELAY_CONN] Establishing worker session [{relayEndPoint}]")]
    public static partial void LogEstablishingWorkerSession(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Error, "[RELAY_CONN] Failed to establish worker session [{relayEndPoint}]")]
    public static partial void LogFailedToEstablishingWorkerSession(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Debug,
        "[RELAY_CONN] Underperformed compression! Original length [{original} bytes], compressed length [{compressed} bytes].")]
    public static partial void LogUnderperformedCompression(this ILogger logger, int original, int compressed);

    [LoggerMessage(LogLevel.Critical,
        "[RELAY_CONN] Failed to decompress message [{length} bytes] from [{source}] to [{target}]")]
    public static partial void LogFailedToDecompressMessage(this ILogger logger, Exception ex, long length,
        string source, Guid target);
}
