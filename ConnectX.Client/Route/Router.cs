using System.Collections.Concurrent;
using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Models;
using ConnectX.Client.P2P;
using ConnectX.Client.Route.Packet;
using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;
using Hive.Common.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHelper = ConnectX.Shared.Helpers.TaskHelper;

#if DEBUG
using ConsoleTables;
#endif

namespace ConnectX.Client.Route;

public class Router : BackgroundService
{
    private readonly ILogger _logger;
    private readonly PeerManager _peerManager;

    private readonly ConcurrentDictionary<Guid, PingChecker<IPEndPoint>> _pingCheckers = new();

    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IServiceProvider _serviceProvider;
    private long _currentPeerCount;

    public Router(
        IServerLinkHolder serverLinkHolder,
        PeerManager peerManager,
        RouteTable routeTable,
        IServiceProvider serviceProvider,
        ILogger<Router> logger)
    {
        _serverLinkHolder = serverLinkHolder;
        _peerManager = peerManager;
        RouteTable = routeTable;
        _serviceProvider = serviceProvider;
        _logger = logger;

        peerManager.OnPeerAdded += OnPeerAdded;
        peerManager.OnPeerRemoved += OnPeerRemoved;
    }

    public RouteTable RouteTable { get; }
    public event Action<P2PPacket>? OnDelivery;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartingRouter();

        await TaskHelper.WaitUntilAsync(
            () => _serverLinkHolder is { IsConnected: true, IsSignedIn: true },
            stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            _logger.LogRouterStoppedBecauseServerLinkHolderIsNotConnectedOrSignedIn();
            return;
        }

        _logger.LogRouterStarted();

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckLinkStateAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        _logger.LogRouterStopped();
    }

    private void OnPeerAdded(Guid id, Peer peer)
    {
        var addedCount = Interlocked.Increment(ref _currentPeerCount);

        peer.DirectLink.Dispatcher.AddHandler<P2PPacket>(OnP2PPacketReceived);
        peer.DirectLink.Dispatcher.AddHandler<LinkStatePacket>(OnLinkStatePacketReceived);
        peer.DirectLink.Dispatcher.AddHandler<P2PTransmitErrorPacket>(OnP2PTransmitErrorPacketReceived);

        var pingWrapper = new SessionPingWrapper(peer.DirectLink);
        var pingChecker = ActivatorUtilities.CreateInstance<PingChecker<IPEndPoint>>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            peer.Id,
            pingWrapper);

        _pingCheckers.AddOrUpdate(peer.Id, pingChecker, (_, _) => pingChecker);

        if (RouteTable.GetForwardInterface(id) == Guid.Empty)
            RouteTable.ForceAdd(id, id);

        _logger.LogPeerAdded(peer.Id, addedCount);

        CheckLinkStateAsync().CatchException();
    }

    private void OnPeerRemoved(Guid id, Peer peer)
    {
        var decreasedCount = Interlocked.Decrement(ref _currentPeerCount);

        peer.DirectLink.Dispatcher.RemoveHandler<P2PPacket>(OnP2PPacketReceived);
        peer.DirectLink.Dispatcher.RemoveHandler<LinkStatePacket>(OnLinkStatePacketReceived);
        peer.DirectLink.Dispatcher.RemoveHandler<P2PTransmitErrorPacket>(OnP2PTransmitErrorPacketReceived);

        _pingCheckers.TryRemove(peer.Id, out _);

        _logger.LogPeerRemoved(peer.Id, decreasedCount);

        var selfLinkState = RouteTable.GetSelfLinkState();
        if (selfLinkState != null)
        {
            for (var i = 0; i < selfLinkState.Interfaces.Length; i++)
            {
                if (selfLinkState.Interfaces[i] != id)
                    continue;

                selfLinkState.Costs[i] = int.MaxValue;

                break;
            }

            RouteTable.Update(selfLinkState);
        }

        CheckLinkStateAsync().CatchException();
    }

    private void OnP2PTransmitErrorPacketReceived(MessageContext<P2PTransmitErrorPacket> ctx)
    {
        var packet = ctx.Message;

        _logger.LogP2PTransmitError(packet.Error, packet.From, packet.To, packet.OriginalTo);
    }

    private void OnP2PPacketReceived(MessageContext<P2PPacket> ctx)
    {
        var packet = ctx.Message;

        if (packet.To == _serverLinkHolder.UserId)
        {
            _logger.LogP2PPacketReceived(ctx.FromSession.RemoteEndPoint);
            OnDelivery?.Invoke(packet);
            return;
        }

        packet.Ttl--;

        if (packet.Ttl == 0)
        {
            _logger.LogP2PPacketDiscarded(ctx.FromSession.RemoteEndPoint, packet.To);

            var error = new P2PTransmitErrorPacket
            {
                Error = P2PTransmitError.TransmitExpired,
                From = _serverLinkHolder.UserId,
                To = packet.From,
                OriginalTo = packet.To,
                Payload = packet.Payload,
                Ttl = 32
            };

            Send(error);

            return;
        }

        _logger.LogForwardP2PPacketReceived(ctx.FromSession.RemoteEndPoint, packet.To);

        Send(packet);
    }

    private void OnLinkStatePacketReceived(MessageContext<LinkStatePacket> ctx)
    {
        var linkState = ctx.Message;

        if (linkState.Source == _serverLinkHolder.UserId)
            return;

        _logger.LogLinkStateReceived(ctx.FromSession.RemoteEndPoint);

        linkState.Ttl--;

        if (linkState.Ttl == 0)
        {
            var error = new P2PTransmitErrorPacket
            {
                Error = P2PTransmitError.TransmitExpired,
                From = _serverLinkHolder.UserId,
                To = linkState.Source,
                Ttl = 32
            };
            Send(error);

            return;
        }

        RouteTable.Update(linkState);

        foreach (var (_, peer) in _peerManager)
            if (peer.DirectLink.Session != ctx.FromSession)
            {
                peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, linkState).Forget();
                _logger.LogLinkStateForwarded(peer.RemoteIpe);
            }
    }

    public void Send(Guid id, ReadOnlyMemory<byte> payload)
    {
        Send(new P2PPacket
        {
            From = _serverLinkHolder.UserId,
            To = id,
            Payload = payload,
            Ttl = 32
        });
    }

    public void Send<T>(T packet) where T : RouteLayerPacket
    {
        var interfaceId = RouteTable.GetForwardInterface(packet.To);

        if (_peerManager.HasLink(interfaceId))
        {
            var link = _peerManager[interfaceId];

            if (!link.IsConnected)
            {
                _logger.LogLinkIsNotConnected(interfaceId);
                return;
            }

            link.DirectLink.Dispatcher.SendAsync(link.DirectLink.Session, packet).Forget();
            _logger.LogSendToLink(link.RemoteIpe, packet.To);

            return;
        }

        if (!_peerManager.HasLink(packet.To))
        {
            if (packet.To != Guid.Empty)
                _logger.LogLinkIsNotReachable(interfaceId);
            return;
        }

        var peer = _peerManager[packet.To];
        peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, packet).Forget();
        _logger.LogSendToPeer(peer.RemoteIpe, packet.To);
    }

    private async Task CheckLinkStateAsync()
    {
        _logger.LogCheckLinkState(Interlocked.Read(ref _currentPeerCount));

        var interfaces = new List<Guid>();
        var states = new List<int>();
        var pingTasks = new List<(Guid, Task<int>)>();
        var ipMappings = new Dictionary<Guid, IPEndPoint>();

        lock (_peerManager)
        {
            foreach (var (key, peer) in _peerManager)
            {
                if (!_pingCheckers.TryGetValue(peer.Id, out var ping))
                    continue;

                pingTasks.Add((key, ping.CheckPingAsync()));
                ipMappings.Add(key, peer.RemoteIpe);

                _logger.LogCheckLinkStateTo(peer.RemoteIpe);
            }
        }

        foreach (var (key, ping) in pingTasks)
        {
            interfaces.Add(key);
            var pingResult = await ping;
            states.Add(pingResult);

            _logger.LogLinkState(ipMappings[key], pingResult);
        }

        var linkState = new LinkStatePacket
        {
            Costs = [.. states],
            Interfaces = [.. interfaces],
            Source = _serverLinkHolder.UserId,
            Timestamp = DateTime.UtcNow.Ticks,
            Ttl = 32
        };

        _logger.LogLinkStateCheckingDone();

        LogLinkState(linkState);
        BroadcastLinkState(linkState);
        RouteTable.Update(linkState);
    }

    private void BroadcastLinkState(LinkStatePacket linkStatePacket)
    {
        _logger.LogBroadcastLinkState(Interlocked.Read(ref _currentPeerCount));

        foreach (var (_, peer) in _peerManager)
        {
            peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, linkStatePacket).Forget();
            _logger.LogBroadcastLinkStateTo(peer.RemoteIpe);
        }
    }

    private void LogLinkState(LinkStatePacket linkState)
    {
        _logger.LogLinkState(linkState.Source, linkState.Timestamp, linkState.Ttl);

        if (linkState.Interfaces.Length > 0 ||
            linkState.Costs.Length > 0)
        {
#if DEBUG
            var table = new ConsoleTable("Interfaces", "Costs");

            for (var i = 0; i < linkState.Interfaces.Length; i++)
                table.AddRow(linkState.Interfaces[i], $"{linkState.Costs[i]}ms");

            table.Write();
#endif

            _logger.LogLinkStateTablePrinted();
        }
    }

    public void RemoveAllPeers()
    {
        _pingCheckers.Clear();
        Interlocked.Exchange(ref _currentPeerCount, 0);
        RouteTable.RemoveAllEntries();

        _logger.LogPeerCleared();
    }
}

internal static partial class RouterLoggers
{
    [LoggerMessage(LogLevel.Trace, "[ROUTER] Peer info cleared.")]
    public static partial void LogPeerCleared(this ILogger logger);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] Sending packet using direct link to [{RemoteEndPoint}]({To})")]
    public static partial void LogSendToLink(this ILogger logger, IPEndPoint? remoteEndPoint, Guid to);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] Sending packet using peer mapping to [{RemoteEndPoint}]({To})")]
    public static partial void LogSendToPeer(this ILogger logger, IPEndPoint? remoteEndPoint, Guid to);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Starting router...")]
    public static partial void LogStartingRouter(this ILogger logger);

    [LoggerMessage(LogLevel.Warning,
        "[ROUTER] Router stopped because server link holder is not connected or signed in, now the app will exit")]
    public static partial void LogRouterStoppedBecauseServerLinkHolderIsNotConnectedOrSignedIn(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Router started")]
    public static partial void LogRouterStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Router stopped")]
    public static partial void LogRouterStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Peer {PeerId} added, current peer count: {PeerCount}")]
    public static partial void LogPeerAdded(this ILogger logger, Guid peerId, long peerCount);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Peer {PeerId} removed, current peer count: {PeerCount}")]
    public static partial void LogPeerRemoved(this ILogger logger, Guid peerId, long peerCount);

    [LoggerMessage(LogLevel.Warning,
        "[ROUTER] P2P transmit error: {Error}, from {From}, to {To}, original to {OriginalTo}")]
    public static partial void LogP2PTransmitError(this ILogger logger, P2PTransmitError error, Guid from, Guid to,
        Guid originalTo);

    [LoggerMessage(LogLevel.Warning,
        "[ROUTER] P2P packet received from {RemoteEndPoint} to {To}, but TTL is 0 now, discard this packet.")]
    public static partial void LogP2PPacketDiscarded(this ILogger logger, IPEndPoint? remoteEndPoint, Guid to);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] P2P packet received from {RemoteEndPoint}, forwarding to {To}")]
    public static partial void LogForwardP2PPacketReceived(this ILogger logger, IPEndPoint? remoteEndPoint, Guid to);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] P2P packet received from {RemoteEndPoint}")]
    public static partial void LogP2PPacketReceived(this ILogger logger, IPEndPoint? remoteEndPoint);

    [LoggerMessage(LogLevel.Debug, "[ROUTER] Link state received from {RemoteEndPoint}")]
    public static partial void LogLinkStateReceived(this ILogger logger, IPEndPoint? remoteEndPoint);

    [LoggerMessage(LogLevel.Debug, "[ROUTER] Link state forwarded to {RemoteEndPoint}")]
    public static partial void LogLinkStateForwarded(this ILogger logger, IPEndPoint? remoteEndPoint);

    [LoggerMessage(LogLevel.Debug, "[ROUTER] {LinkId} is not connected")]
    public static partial void LogLinkIsNotConnected(this ILogger logger, Guid linkId);

    [LoggerMessage(LogLevel.Debug, "[ROUTER] {LinkId} is not reachable")]
    public static partial void LogLinkIsNotReachable(this ILogger logger, Guid linkId);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Check link state, current peer count: {PeerCount}")]
    public static partial void LogCheckLinkState(this ILogger logger, long peerCount);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] Check link state to {RemoteEndPoint}")]
    public static partial void LogCheckLinkStateTo(this ILogger logger, IPEndPoint? remoteEndPoint);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] Link state to {RemoteEndPoint} is {PingResult}")]
    public static partial void LogLinkState(this ILogger logger, IPEndPoint? remoteEndPoint, int pingResult);

    [LoggerMessage(LogLevel.Information, "Link state checking done")]
    public static partial void LogLinkStateCheckingDone(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Broadcast link state to all peers, current peer count: {PeerCount}")]
    public static partial void LogBroadcastLinkState(this ILogger logger, long peerCount);

    [LoggerMessage(LogLevel.Trace, "[ROUTER] Broadcast link state to {RemoteEndPoint}")]
    public static partial void LogBroadcastLinkStateTo(this ILogger logger, IPEndPoint? remoteEndPoint);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Link state - Source: {Source} Timestamp: {TimeStamp} Ttl: {Ttl}")]
    public static partial void LogLinkState(this ILogger logger, Guid source, long timeStamp, byte ttl);

    [LoggerMessage(LogLevel.Information, "[ROUTER] Link state table printed")]
    public static partial void LogLinkStateTablePrinted(this ILogger logger);
}