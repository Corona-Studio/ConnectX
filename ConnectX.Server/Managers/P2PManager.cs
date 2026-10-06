using Microsoft.Extensions.Hosting;
using ConnectX.Actors;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.P2P;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Managers;

public partial class P2PManager : BackgroundService
{
    partial void DisposeActorResources() { _clientManager.OnSessionDisconnected -= ClientManagerOnSessionDisconnected; }

    private readonly ClientManager _clientManager;
    private readonly Dictionary<(int Bargain, Guid RequesterId, Guid TargetId),
        (ISession Session, P2PConRequest Request, long CreatedAt)> _conRequests = new();
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private readonly Dictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly Dictionary<Guid, ISession> _userSessionMappings = new();

    private readonly ControlPlaneActor _actor;

    public P2PManager(
        ControlPlaneActor actor,
        IDispatcher dispatcher,
        ClientManager clientManager,
        TimeProvider clock,
        ILogger<P2PManager> logger)
    {
        _actor = actor;
        _dispatcher = dispatcher;
        _clientManager = clientManager;
        _clock = clock;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        RegisterActorHandlers(dispatcher, actor);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        _logger.LogUserDisconnected(sessionId);

        if (_sessionIdMapping.Remove(sessionId, out var userId) &&
            _userSessionMappings.Remove(userId, out var attachedSession))
        {
            _actor.Close(attachedSession);

            foreach (var request in _conRequests
                         .Where(item => item.Value.Session.Id == sessionId ||
                                        item.Key.RequesterId == userId ||
                                        item.Key.TargetId == userId)
                         .ToArray())
                _conRequests.Remove(request.Key, out _);
        }
    }

    [ActorMessage]
    private void OnReceivedP2PConRequest(MessageContext<P2PConRequest> ctx)
    {
        _logger.LogUserTryingToMakeP2PConnWithTarget(ctx.Message.SelfId, ctx.Message.TargetId);

        var session = ctx.FromSession;
        var message = ctx.Message;

        if (!_sessionIdMapping.TryGetValue(session.Id, out var requesterId) || requesterId != message.SelfId)
        {
            _actor.Send(ctx.Dispatcher, session, new P2POpResult(false, "Invalid requester identity")
            {
                Bargain = message.Bargain,
                PartnerId = message.TargetId
            });
            return;
        }

        if (_conRequests.Count >= 4096 || _conRequests.Keys.Count(key => key.RequesterId == message.SelfId) >= 256)
        {
            _actor.Send(ctx.Dispatcher, session, new P2POpResult(false, "Too many pending connection requests")
            { Bargain = message.Bargain, PartnerId = message.TargetId });
            return;
        }

        var requestKey = (message.Bargain, message.SelfId, message.TargetId);
        if (!_conRequests.TryAdd(requestKey, (session, message, _clock.GetTimestamp())))
        {
            _logger.LogUserTryingToMakeP2PConnWithTargetButTheRequestAlreadyExists(message.SelfId, message.TargetId);
            _actor.Send(ctx.Dispatcher, session, new P2POpResult(false, "Duplicate connection request")
            {
                Bargain = message.Bargain,
                PartnerId = message.TargetId
            });
            return;
        }

        if (!_userSessionMappings.TryGetValue(message.TargetId, out var targetConnection))
        {
            _logger.LogUserTryingToMakeP2PConnWithTargetButTheTargetDoesNotExist(message.SelfId, message.TargetId);
            _conRequests.Remove(requestKey, out _);

            var err = new P2POpResult(false, "Target does not exist")
            {
                Bargain = message.Bargain,
                PartnerId = message.TargetId
            };
            _actor.Send(ctx.Dispatcher, session, err);

            return;
        }

        _actor.Send(_dispatcher,
            targetConnection,
            new P2PConNotification
            {
                Bargain = message.Bargain,
                PartnerIds = message.SelfId,
                PartnerIp = session.RemoteEndPoint!
            });

        var result = new P2POpResult(true)
        {
            Bargain = message.Bargain,
            PartnerId = message.TargetId
        };
        _actor.Send(ctx.Dispatcher, session, result);

        _logger.LogUserTryingToMakeP2PConnWithTarget(message.SelfId, message.TargetId, session.Id);
    }

    [ActorMessage]
    private void OnReceivedP2PConAccept(MessageContext<P2PConAccept> ctx)
    {
        _logger.LogUserAcceptedP2PConn(ctx.Message.SelfId, ctx.FromSession.Id);

        var from = ctx.FromSession;
        var message = ctx.Message;

        if (!_sessionIdMapping.TryGetValue(from.Id, out var accepterId) || accepterId != message.SelfId)
        {
            _actor.Send(ctx.Dispatcher, from, new P2POpResult(false, "Invalid accepter identity")
            {
                Bargain = message.Bargain,
                PartnerId = message.PartnerId
            });
            return;
        }

        if (!_conRequests.Remove((message.Bargain, message.PartnerId, message.SelfId), out var value))
        {
            _logger.LogUserTryingToAcceptP2PConnButTheRequestDoesNotExist(message.SelfId, ctx.FromSession.Id);

            var err = new P2POpResult(false, "Request does not exist")
            {
                Bargain = message.Bargain,
                PartnerId = message.PartnerId
            };
            _actor.Send(ctx.Dispatcher, from, err);

            return;
        }

        var (requesterCon, request, _) = value;

        if (request.SelfId != message.PartnerId)
        {
            var err = new P2POpResult(false, "Connection request partner mismatch")
            {
                Bargain = message.Bargain,
                PartnerId = message.PartnerId
            };
            _actor.Send(ctx.Dispatcher, from, err);
            return;
        }

        var time = DateTime.UtcNow.AddSeconds(5).Ticks;

        _actor.Send(_dispatcher,
            requesterCon,
            new P2PConReady(message.SelfId, time, message)
            {
                PublicAddress = message.PublicAddress,
                Bargain = message.Bargain
            });

        var result = new P2POpResult(true)
        {
            Bargain = message.Bargain,
            PartnerId = request.SelfId,
            Context = new P2PConReady(request.SelfId, time, request)
            {
                PublicAddress = request.PublicAddress,
                Bargain = message.Bargain
            }
        };
        _actor.Send(ctx.Dispatcher, from, result);

        _logger.LogUserAcceptedP2PConn(message.SelfId, ctx.FromSession.Id);
    }

    public void AttachSession(
        ISession session,
        Guid userId,
        SigninMessage signinMessage)
    {
        _actor.AssertAccess();
        if (!signinMessage.JoinP2PNetwork) return;
        if (_userSessionMappings.ContainsKey(userId) || _sessionIdMapping.ContainsKey(session.Id))
        {
            _logger.LogP2PFailedToAddSessionToSessionMapping(session.Id);
            return;
        }
        _userSessionMappings.Add(userId, session);
        _sessionIdMapping.Add(session.Id, userId);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await _actor.InvokeAsync(() =>
                {
                    foreach (var (key, request) in _conRequests.ToArray())
                        if (_clock.GetElapsedTime(request.CreatedAt) > TimeSpan.FromSeconds(30))
                        {
                            _conRequests.Remove(key);
                            _actor.Send(_dispatcher, request.Session, new P2POpResult(false, "Connection request expired")
                            { Bargain = key.Bargain, PartnerId = key.TargetId });
                        }
                }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

}

internal static partial class P2PManagerLoggers
{

    [LoggerMessage(LogLevel.Information, "[P2P_MANAGER] User disconnected, session id: {sessionId}")]
    public static partial void LogUserDisconnected(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}]")]
    public static partial void LogUserTryingToMakeP2PConnWithTarget(this ILogger logger, Guid userId, Guid targetId);

    [LoggerMessage(LogLevel.Error,
        "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], but the request already exists")]
    public static partial void LogUserTryingToMakeP2PConnWithTargetButTheRequestAlreadyExists(this ILogger logger,
        Guid userId, Guid targetId);

    [LoggerMessage(LogLevel.Warning,
        "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], but the target does not exist")]
    public static partial void LogUserTryingToMakeP2PConnWithTargetButTheTargetDoesNotExist(this ILogger logger,
        Guid userId, Guid targetId);

    [LoggerMessage(LogLevel.Information,
        "[P2P_MANAGER] User {userId} trying to make P2P conn with target [{targetId}], session id: {sessionId}")]
    public static partial void LogUserTryingToMakeP2PConnWithTarget(this ILogger logger, Guid userId, Guid targetId,
        SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[P2P_MANAGER] User {userId} accepted P2P conn, session id: {sessionId}")]
    public static partial void LogUserAcceptedP2PConn(this ILogger logger, Guid userId, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning,
        "[P2P_MANAGER] User {userId} trying to accept P2P conn, but the request does not exist, session id: {sessionId}")]
    public static partial void LogUserTryingToAcceptP2PConnButTheRequestDoesNotExist(this ILogger logger, Guid userId,
        SessionId sessionId);

    [LoggerMessage(LogLevel.Error,
        "[P2P_MANAGER] Failed to add session to the session mapping, session id: {sessionId}")]
    public static partial void LogP2PFailedToAddSessionToSessionMapping(this ILogger logger, SessionId sessionId);

}
