using ConnectX.Actors;
using ConnectX.Relay.Interfaces;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Relay.Datagram;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Relay.Managers;

/// <summary>Control state belongs to the actor; stream input/output adapters own all cross-thread I/O.</summary>
public partial class RelayManager : BackgroundService
{
    private sealed record PendingLink(ISession Session, Guid User, Guid? Target, Guid Room, long Started);
    private readonly Dictionary<SessionId, PendingLink> _pendingLinks = [];
    private readonly Dictionary<SessionId, RelayWorkerInput> _workerInputs = [];
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, Guid> _rooms = [];
    private readonly Dictionary<Guid, ISession> _data = [];
    private readonly Dictionary<SessionId, Guid> _dataUsers = [];
    private readonly Dictionary<(Guid From, Guid To), ISession> _workers = [];
    private readonly ControlPlaneActor _actor;
    private readonly ClientManager _clientManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IServerSettingProvider _settings;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    public RelayManager(ControlPlaneActor actor, ClientManager clientManager, IServerLinkHolder serverLinkHolder,
        IDispatcher dispatcher, IServerSettingProvider serverSettingProvider, ILogger<RelayManager> logger, TimeProvider clock)
    {
        _actor = actor;
        _clock = clock;
        _clientManager = clientManager;
        _serverLinkHolder = serverLinkHolder;
        _settings = serverSettingProvider;
        _dispatcher = dispatcher;
        _logger = logger;
        clientManager.OnSessionDisconnected += OnDisconnected;
        RegisterActorHandlers(dispatcher, actor);
    }

    public void CreateDataLink(ISession session, Guid user, Guid room) => BeginLink(session, user, null, room);
    public void CreateWorkerLink(ISession session, Guid user, Guid target, Guid room) => BeginLink(session, user, target, room);

    private void BeginLink(ISession session, Guid user, Guid? target, Guid room)
    {
        _actor.AssertAccess();
        var request = new PendingLink(session, user, target, room, _clock.GetTimestamp());
        if (TryCompleteLink(request)) { CompletePendingLinks(); return; }
        if (_pendingLinks.Count >= 1024)
        {
            _logger.LogLinkRejected(session.Id, user, room);
            _actor.Close(session);
            return;
        }
        _pendingLinks[session.Id] = request;
    }

    private bool TryCompleteLink(PendingLink request)
    {
        if ((_rooms.TryGetValue(request.User, out var room) && room != request.Room) ||
            (request.Target is { } target && (_rooms.TryGetValue(target, out var targetRoom) && targetRoom != request.Room)))
        {
            _logger.LogLinkRejected(request.Session.Id, request.User, request.Room);
            _actor.Close(request.Session);
            return true;
        }
        // Different TCP connections can deliver client handshakes before the authoritative mapping.
        if (!IsMember(request.User, request.Room) || (request.Target is { } to &&
            (!IsMember(to, request.Room) || !_data.ContainsKey(request.User)))) return false;
        if (request.Target is { } relayTo)
        {
            if (!AttachWorkerSession(request.Session, request.User, relayTo, request.Room)) _actor.Close(request.Session);
            else
            {
                _actor.Send(_dispatcher, request.Session, new RelayWorkerLinkCreatedMessage());
                PublishRoutes();
            }
        }
        else
        {
            if (!AttachDataSession(request.Session, request.User, request.Room)) _actor.Close(request.Session);
            else
            {
                _clientManager.AttachSession(request.Session.Id, request.Session);
                _actor.Send(_dispatcher, request.Session, new RelayDataLinkCreatedMessage());
            }
        }
        return true;
    }

    private void CompletePendingLinks()
    {
        foreach (var (id, request) in _pendingLinks.ToArray())
        {
            if (_clock.GetElapsedTime(request.Started) > TimeSpan.FromSeconds(5) || !SessionHealth.IsConnected(request.Session))
            {
                _pendingLinks.Remove(id);
                _actor.Close(request.Session);
            }
            else if (TryCompleteLink(request)) _pendingLinks.Remove(id);
        }
    }

    public bool AttachDataSession(ISession session, Guid userId, Guid roomId)
    {
        _actor.AssertAccess();
        if (!IsMember(userId, roomId) || session.RemoteEndPoint == null) return false;
        if (_data.TryGetValue(userId, out var previous))
        {
            if (ReferenceEquals(previous, session)) return true;
            _clientManager.DetachSession(previous.Id);
        }
        _data[userId] = session;
        _dataUsers[session.Id] = userId;
        return true;
    }

    public bool AttachWorkerSession(ISession session, Guid userId, Guid relayTo, Guid roomId)
    {
        _actor.AssertAccess();
        if (userId == relayTo || !IsMember(userId, roomId) || !IsMember(relayTo, roomId) ||
            !_data.ContainsKey(userId) || session.RemoteEndPoint == null) return false;
        var pair = (userId, relayTo);
        if (_workers.Remove(pair, out var previous)) CloseWorker(previous);
        _workers[pair] = session;
        _actor.PrepareOutput(session);
        session.OnMessageReceived -= _dispatcher.Dispatch;
        var input = new RelayWorkerInput(_actor, session);
        _workerInputs[session.Id] = input;
        if (session is IBorrowedBufferSession borrowed) borrowed.ReceiveHandler = input.ReceiveAsync;
        else session.OnMessageReceived += input.Receive;
        return true;
    }

    // Publish only after the handshake acknowledgement has entered the connection's output queue.
    public void ActivateWorkerSession() { _actor.AssertAccess(); PublishRoutes(); }

    private bool IsMember(Guid userId, Guid roomId) => _rooms.TryGetValue(userId, out var room) && room == roomId;

    public RelayServerLoadInfoMessage GetRelayServerLoad()
    {
        _actor.AssertAccess();
        return new RelayServerLoadInfoMessage
        {
            CurrentConnectionCount = _workers.Count,
            MaxReferenceConnectionCount = _settings.MaxReferenceConnectionCount,
            Priority = _settings.ServerPriority
        };
    }

    private void PublishRoutes()
    {
        foreach (var (pair, source) in _workers)
            _workerInputs[source.Id].ConnectTo(_workers.GetValueOrDefault((pair.To, pair.From)));
    }

    [ActorMessage]
    private void OnMappingUpdated(MessageContext<UpdateRelayUserRoomMappingMessage> ctx)
    {
        if (!ReferenceEquals(ctx.FromSession, _serverLinkHolder.ServerSession))
        {
            _logger.LogMappingUnauthorized(ctx.FromSession.Id);
            return;
        }
        var message = ctx.Message;
        if (message.State == GroupUserStates.Joined)
        {
            if (_rooms.TryGetValue(message.UserId, out var previous) && previous != message.RoomId) RemoveUser(message.UserId);
            _rooms[message.UserId] = message.RoomId;
            CompletePendingLinks();
            return;
        }
        if (!_rooms.TryGetValue(message.UserId, out var current) || current != message.RoomId) return;
        if (message.State == GroupUserStates.Dismissed)
        {
            foreach (var user in _rooms.Where(x => x.Value == message.RoomId).Select(x => x.Key).ToArray()) RemoveUser(user);
        }
        else if (message.State is GroupUserStates.Left or GroupUserStates.Kicked or GroupUserStates.Disconnected)
            RemoveUser(message.UserId);
    }

    [ActorMessage]
    private void OnDatagram(MessageContext<RelayDatagram> ctx)
    {
        var message = ctx.Message;
        if (!_dataUsers.TryGetValue(ctx.FromSession.Id, out var from) || from != message.From ||
            !_data.TryGetValue(from, out var source) || !ReferenceEquals(source, ctx.FromSession) ||
            !_rooms.TryGetValue(from, out var room) || !IsMember(message.To, room) ||
            !_data.TryGetValue(message.To, out var target)) return;
        _actor.Send(_dispatcher, target, new UnwrappedRelayDatagram(from, message.Payload));
    }

    private void OnDisconnected(SessionId id)
    {
        if (!_dataUsers.Remove(id, out var user)) return;
        if (!_data.TryGetValue(user, out var current) || current.Id != id) return;
        _data.Remove(user);
        RemoveWorkers(user);
    }

    private void RemoveUser(Guid user)
    {
        _rooms.Remove(user);
        foreach (var (id, request) in _pendingLinks.Where(x => x.Value.User == user || x.Value.Target == user).ToArray())
        {
            _pendingLinks.Remove(id);
            _actor.Close(request.Session);
        }
        if (_data.TryGetValue(user, out var session)) _clientManager.DetachSession(session.Id);
        RemoveWorkers(user);
    }

    private void RemoveWorkers(Guid user)
    {
        foreach (var (pair, session) in _workers.Where(x => x.Key.From == user || x.Key.To == user).ToArray())
        {
            _workers.Remove(pair);
            CloseWorker(session);
        }
        PublishRoutes();
    }

    private void CloseWorker(ISession session)
    {
        if (_workerInputs.Remove(session.Id, out var input))
        {
            if (session is IBorrowedBufferSession borrowed) borrowed.ReceiveHandler = null;
            else session.OnMessageReceived -= input.Receive;
            input.Close();
        }
        _actor.Close(session);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await _actor.InvokeAsync(() =>
                {
                    CompletePendingLinks();
                    var changed = false;
                    foreach (var (pair, session) in _workers.ToArray())
                        if (!SessionHealth.IsConnected(session))
                        {
                            _workers.Remove(pair);
                            CloseWorker(session);
                            changed = true;
                        }
                    if (changed) PublishRoutes();
                }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _actor.InvokeAsync(() =>
        {
            foreach (var request in _pendingLinks.Values) _actor.Close(request.Session);
            _pendingLinks.Clear();
            foreach (var session in _workers.Values) CloseWorker(session);
            _workers.Clear();
            PublishRoutes();
        }, cancellationToken);
    }
}

internal static partial class RelayManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "Relay link rejected: session {sessionId}, user {userId}, room {roomId}")]
    public static partial void LogLinkRejected(this ILogger logger, SessionId sessionId, Guid userId, Guid roomId);
    [LoggerMessage(LogLevel.Warning, "Unauthorized relay mapping update from session {sessionId}")]
    public static partial void LogMappingUnauthorized(this ILogger logger, SessionId sessionId);
}
