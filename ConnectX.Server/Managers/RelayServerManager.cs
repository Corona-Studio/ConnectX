using ConnectX.Actors;
using Microsoft.Extensions.Logging;
using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using ConnectX.Server.Interfaces;

namespace ConnectX.Server.Managers;

public partial class RelayServerManager
{
    partial void DisposeActorResources() { _clientManager.OnSessionDisconnected -= ClientManagerOnOnSessionDisconnected; }

    private readonly ClientManager _clientManager;
    private readonly IServerSettingProvider _serverSettingProvider;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly Dictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly Dictionary<Guid, ISession> _sessionMapping = new();
    private readonly Dictionary<Guid, IPEndPoint> _serverAddressMapping = new();
    private readonly Dictionary<IPEndPoint, ISession> _relayAddressSessionMapping = new();

    private readonly ControlPlaneActor _actor;

    public RelayServerManager(
        ControlPlaneActor actor,
        ClientManager clientManager,
        IServerSettingProvider serverSettingProvider,
        IDispatcher dispatcher,
        ILogger<RelayServerManager> logger)
    {
        _actor = actor;
        _clientManager = clientManager;
        _serverSettingProvider = serverSettingProvider;
        _dispatcher = dispatcher;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnOnSessionDisconnected;

        RegisterActorHandlers(dispatcher, actor);
    }

    private bool IsSessionAttached(ISession session)
    {
        if (_clientManager.IsSessionAttached(session.Id)) return true;

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return false;
    }

    public IPEndPoint? GetRandomRelayServerAddress(int roomSeed)
    {
        _actor.AssertAccess();
        if ((_serverAddressMapping.Count == 0)) return null;

        var servers = _serverAddressMapping.Values.ToArray();

        return servers[(int)((uint)roomSeed % (uint)servers.Length)];
    }

    public bool TryGetRelayServerSession(IPEndPoint endPoint, [NotNullWhen(true)] out ISession? session)
    {
        _actor.AssertAccess();
        return _relayAddressSessionMapping.TryGetValue(endPoint, out session);
    }

    public bool TryGetRelayServerAddress(SessionId sessionId, [NotNullWhen(true)] out IPEndPoint? iPEndPoint)
    {
        _actor.AssertAccess();
        iPEndPoint = null;

        if (!_sessionIdMapping.TryGetValue(sessionId, out var userId)) return false;
        return _serverAddressMapping.TryGetValue(userId, out iPEndPoint);
    }

    /// <summary>
    ///     Attach the session to the manager
    /// </summary>
    /// <param name="id"></param>
    /// <param name="sessionUserId"></param>
    /// <param name="session"></param>
    /// <returns>the assigned id for the session, if the return value is default, it means the add has failed</returns>
    public void AttachSession(
        SessionId id,
        Guid sessionUserId,
        ISession session)
    {
        _actor.AssertAccess();
        if (!_clientManager.IsSessionAttached(id))
        {
            _logger.LogFailedToAttachSession(id);
            return;
        }

        if (_sessionMapping.ContainsKey(sessionUserId) || _sessionIdMapping.ContainsKey(id))
        {
            _logger.LogRelayServerManagerFailedToAddSessionToSessionMapping(id);
            return;
        }
        _sessionMapping.Add(sessionUserId, session);
        _sessionIdMapping.Add(id, sessionUserId);
    }

    private void ClientManagerOnOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionIdMapping.Remove(sessionId, out var userId)) return;
        if (!_sessionMapping.Remove(userId, out var session)) return;
        if (_serverAddressMapping.Remove(userId, out var endPoint) &&
            _relayAddressSessionMapping.TryGetValue(endPoint, out var registered) && ReferenceEquals(registered, session))
            _relayAddressSessionMapping.Remove(endPoint);

        _actor.Close(session);
    }

    [ActorMessage]
    private void OnReceivedRegisterRelayServerMessage(MessageContext<RegisterRelayServerMessage> ctx)
    {
        if (!IsSessionAttached(ctx.FromSession)) return;
        if (!_sessionIdMapping.TryGetValue(ctx.FromSession.Id, out var userId)) return;
        if (!_sessionMapping.TryGetValue(userId, out var session)) return;
        if (ctx.Message.ServerId == Guid.Empty ||
            _serverSettingProvider.ServerId != ctx.Message.ServerId)
        {
            _logger.LogFailedToRegisterRelayServerBecauseIdNotMatch(ctx.Message.ServerId);
            return;
        }

        if (_serverAddressMapping.TryGetValue(userId, out var registered))
        {
            if (!registered.Equals(ctx.Message.ServerAddress)) return;
        }
        else
        {
            if (_relayAddressSessionMapping.ContainsKey(ctx.Message.ServerAddress)) return;
            _serverAddressMapping[userId] = ctx.Message.ServerAddress;
            _relayAddressSessionMapping[ctx.Message.ServerAddress] = ctx.FromSession;
        }

        _actor.Send(_dispatcher, session, new RelayServerRegisteredMessage(_serverSettingProvider.ServerId));

        _logger.LogRelayServerRegistered(ctx.FromSession.Id, ctx.Message.ServerId);
    }
}

internal static partial class RelayServerManagerLoggers
{
    [LoggerMessage(LogLevel.Error, "[RELAY_SERVER_MANAGER] Failed to attach session {SessionId} to relay server manager.")]
    public static partial void LogRelayServerManagerFailedToAddSessionToSessionMapping(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning, "[RELAY_SERVER_MANAGER] Failed to register relay server because id [{id}] not match.")]
    public static partial void LogFailedToRegisterRelayServerBecauseIdNotMatch(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Information, "[RELAY_SERVER_MANAGER] Relay server registered {SessionId} with server id {ServerId}")]
    public static partial void LogRelayServerRegistered(this ILogger logger, SessionId sessionId, Guid serverId);

}
