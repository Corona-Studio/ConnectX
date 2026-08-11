using System.Collections.Concurrent;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.P2P;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Managers;

public class P2PManager
{
    private readonly ClientManager _clientManager;
    private readonly ConcurrentDictionary<(int Bargain, Guid RequesterId, Guid TargetId),
        (ISession, P2PConRequest)> _conRequests = new();
    private readonly IDispatcher _dispatcher;
    private readonly GroupManager _groupManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly ConcurrentDictionary<Guid, ISession> _userSessionMappings = new();

    public P2PManager(
        IDispatcher dispatcher,
        ClientManager clientManager,
        GroupManager groupManager,
        ILogger<P2PManager> logger)
    {
        _dispatcher = dispatcher;
        _clientManager = clientManager;
        _groupManager = groupManager;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _dispatcher.AddHandler<P2PConRequest>(OnReceivedP2PConRequest);
        _dispatcher.AddHandler<P2PConAccept>(OnReceivedP2PConAccept);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        _logger.LogUserDisconnected(sessionId);

        if (_sessionIdMapping.TryRemove(sessionId, out var userId) &&
            _userSessionMappings.TryRemove(userId, out var attachedSession))
        {
            attachedSession.Close();

            foreach (var request in _conRequests
                         .Where(item => item.Value.Item1.Id == sessionId ||
                                        item.Key.RequesterId == userId ||
                                        item.Key.TargetId == userId)
                         .ToArray())
                _conRequests.TryRemove(request.Key, out _);
        }
    }

    private void OnReceivedP2PConRequest(MessageContext<P2PConRequest> ctx)
    {
        _logger.LogUserTryingToMakeP2PConnWithTarget(ctx.Message.SelfId, ctx.Message.TargetId);

        var session = ctx.FromSession;
        var message = ctx.Message;

        if (!_sessionIdMapping.TryGetValue(session.Id, out var requesterId) || requesterId != message.SelfId)
        {
            ctx.Dispatcher.SendAsync(session, new P2POpResult(false, "Invalid requester identity")
            {
                Bargain = message.Bargain,
                PartnerId = message.TargetId
            }).Forget();
            return;
        }

        var requestKey = (message.Bargain, message.SelfId, message.TargetId);
        if (!_conRequests.TryAdd(requestKey, (session, message)))
        {
            _logger.LogUserTryingToMakeP2PConnWithTargetButTheRequestAlreadyExists(message.SelfId, message.TargetId);
            ctx.Dispatcher.SendAsync(session, new P2POpResult(false, "Duplicate connection request")
            {
                Bargain = message.Bargain,
                PartnerId = message.TargetId
            }).Forget();
            return;
        }

        if (!_userSessionMappings.TryGetValue(message.TargetId, out var targetConnection))
        {
            _logger.LogUserTryingToMakeP2PConnWithTargetButTheTargetDoesNotExist(message.SelfId, message.TargetId);
            _conRequests.TryRemove(requestKey, out _);

            var err = new P2POpResult(false, "Target does not exist")
            {
                Bargain = message.Bargain,
                PartnerId = message.TargetId
            };
            ctx.Dispatcher.SendAsync(session, err).Forget();

            return;
        }

        _dispatcher.SendAsync(
            targetConnection,
            new P2PConNotification
            {
                Bargain = message.Bargain,
                PartnerIds = message.SelfId,
                PartnerIp = session.RemoteEndPoint!
            }).Forget();

        var result = new P2POpResult(true)
        {
            Bargain = message.Bargain,
            PartnerId = message.TargetId
        };
        ctx.Dispatcher.SendAsync(session, result).Forget();

        _logger.LogUserTryingToMakeP2PConnWithTarget(message.SelfId, message.TargetId, session.Id);
    }

    private void OnReceivedP2PConAccept(MessageContext<P2PConAccept> ctx)
    {
        _logger.LogUserAcceptedP2PConn(ctx.Message.SelfId, ctx.FromSession.Id);

        var from = ctx.FromSession;
        var message = ctx.Message;

        if (!_sessionIdMapping.TryGetValue(from.Id, out var accepterId) || accepterId != message.SelfId)
        {
            ctx.Dispatcher.SendAsync(from, new P2POpResult(false, "Invalid accepter identity")
            {
                Bargain = message.Bargain,
                PartnerId = message.PartnerId
            }).Forget();
            return;
        }

        if (!_conRequests.TryRemove((message.Bargain, message.PartnerId, message.SelfId), out var value))
        {
            _logger.LogUserTryingToAcceptP2PConnButTheRequestDoesNotExist(message.SelfId, ctx.FromSession.Id);

            var err = new P2POpResult(false, "Request does not exist")
            {
                Bargain = message.Bargain,
                PartnerId = message.PartnerId
            };
            ctx.Dispatcher.SendAsync(from, err).Forget();

            return;
        }

        var (requesterCon, request) = value;

        if (request.SelfId != message.PartnerId)
        {
            var err = new P2POpResult(false, "Connection request partner mismatch")
            {
                Bargain = message.Bargain,
                PartnerId = message.PartnerId
            };
            ctx.Dispatcher.SendAsync(from, err).Forget();
            return;
        }

        var time = DateTime.UtcNow.AddSeconds(5).Ticks;

        _dispatcher.SendAsync(
            requesterCon,
            new P2PConReady(message.SelfId, time, message)
            {
                PublicAddress = message.PublicAddress,
                Bargain = message.Bargain
            }).Forget();

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
        ctx.Dispatcher.SendAsync(from, result).Forget();

        _logger.LogUserAcceptedP2PConn(message.SelfId, ctx.FromSession.Id);
    }

    public void AttachSession(
        ISession session,
        Guid userId,
        SigninMessage signinMessage)
    {
        if (!signinMessage.JoinP2PNetwork) return;
        if (!_userSessionMappings.TryAdd(userId, session) ||
            !_sessionIdMapping.TryAdd(session.Id, userId))
        {
            _logger.LogP2PFailedToAddSessionToSessionMapping(session.Id);
        }
    }
}

internal static partial class P2PManagerLoggers
{
    [LoggerMessage(LogLevel.Error, "[P2P_MANAGER] Session [{sessionId}] not found in the record!")]
    public static partial void LogSessionNotFound(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[P2P_MANAGER] No possible interconnect user found!")]
    public static partial void LogNoInterconnectUserFound(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[P2P_MANAGER] User disconnected, session id: {sessionId}")]
    public static partial void LogUserDisconnected(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[P2P_MANAGER] Temp link disconnected, session id: {sessionId}")]
    public static partial void LogTempLinkDisconnected(this ILogger logger, SessionId sessionId);

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

    [LoggerMessage(LogLevel.Information,
        "[P2P_MANAGER] User has {count} possible interconnect users, session id: {sessionId}")]
    public static partial void LogUserHasPossibleInterconnectUsers(this ILogger logger, int count, SessionId sessionId);
}
