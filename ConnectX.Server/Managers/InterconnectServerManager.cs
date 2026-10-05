using ConnectX.Actors;
using System.Net;
using Hive.Network.Abstractions.Session;
using Hive.Network.Abstractions;
using Microsoft.Extensions.Logging;
using ConnectX.Server.Messages.Queries;
using Hive.Both.General.Dispatchers;
using ConnectX.Server.Interfaces;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Server;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Server.Managers;

public partial class InterconnectServerManager
{
    private readonly Dictionary<SessionId, InterconnectServerRegistration> _registerServerInfo = [];
    private readonly Dictionary<SessionId, ISession> _sessionMapping = new();

    private readonly ClientManager _clientManager;
    private readonly IInterconnectServerSettingProvider _interconnectServerSettingProvider;
    private readonly IDispatcher _dispatcher;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    private readonly ControlPlaneActor _actor;

    public InterconnectServerManager(
        ControlPlaneActor actor,
        ClientManager clientManager,
        IInterconnectServerSettingProvider interconnectServerSettingProvider,
        IDispatcher dispatcher,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<InterconnectServerManager> logger)
    {
        _actor = actor;
        _clientManager = clientManager;
        _interconnectServerSettingProvider = interconnectServerSettingProvider;
        _dispatcher = dispatcher;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        _clientManager.OnSessionDisconnected += OnClientSessionDisconnected;

        // This should only be used for current server
        RegisterActorHandlers(dispatcher, actor);
    }

    public bool IsServerRegisteredForInterconnect(SessionId sessionId)
    {
        _actor.AssertAccess();
        return _sessionMapping.ContainsKey(sessionId);
    }

    private void OnClientSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionMapping.Remove(sessionId, out var session))
            return;

        _actor.Close(session);

        if (!_registerServerInfo.Remove(sessionId, out var regInfo))
            return;

        _logger.LogInterconnectServerDisconnected(sessionId, regInfo.ServerAddress, regInfo.ServerName);
    }

    [ActorMessage]
    private void OnQueryRemoteServerRoomInfoReceived(MessageContext<QueryRemoteServerRoomInfo> ctx)
    {
        if (!_sessionMapping.ContainsKey(ctx.FromSession.Id)) return;
        using var scope = _serviceScopeFactory.CreateScope();
        var groupManager = scope.ServiceProvider.GetRequiredService<GroupManager>();

        var fromSession = ctx.FromSession;

        if (!groupManager.TryQueryGroup(ctx.Message, out var group))
        {
            _logger.LogRemoteServerQueriedGroupButNotFound(
                ctx.FromSession.RemoteEndPoint,
                ctx.Message.JoinGroup.GroupId);

            var failedRes = new QueryRemoteServerRoomInfoResponse(false);

            _actor.Send(_dispatcher, fromSession, failedRes);

            return;
        }

        var res = new QueryRemoteServerRoomInfoResponse(true);

        _actor.Send(_dispatcher, fromSession, res);

        _logger.LogRemoteServerQueriedRoomInfo(
            ctx.FromSession.RemoteEndPoint,
            group.RoomId,
            group.RoomName);
    }

    public (ISession Session, InterconnectServerRegistration Registration)[] GetRegisteredServers()
    {
        _actor.AssertAccess();
        return _sessionMapping.Select(x => (x.Value, _registerServerInfo[x.Key])).ToArray();
    }

    public async Task<InterconnectServerRegistration?> FindRemoteRoomAsync(JoinGroup joinGroup,
        (ISession Session, InterconnectServerRegistration Registration)[] servers, CancellationToken token)
    {
        var query = new QueryRemoteServerRoomInfo
        {
            JoinGroup = joinGroup
        };

        foreach (var (session, regInfo) in servers)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var res = await _dispatcher.SendAndListenOnce<QueryRemoteServerRoomInfo, QueryRemoteServerRoomInfoResponse>(
                session,
                query,
                cts.Token);

            if (res == null) continue;
            if (!res.Found) continue;
            return regInfo;
        }

        _logger.LogFailedToGetRoomInfoFromRemoteServer(joinGroup);

        return null;
    }

    public SessionId AttachSession(
        SessionId id,
        ISession session,
        InterconnectServerRegistration message)
    {
        _actor.AssertAccess();
        if (!_interconnectServerSettingProvider.EndPoints.Contains(message.ServerAddress))
        {
            _logger.LogUnauthorizedServerTryingToMakeInterconnect(message.ServerAddress);
            _actor.Close(session);

            return default;
        }

        _registerServerInfo[id] = message;
        if (_sessionMapping.TryGetValue(id, out var oldSession)) _actor.Close(oldSession);
        _sessionMapping[id] = session;

        _logger.LogInterconnectServerAttached(
            id,
            message.ServerName,
            message.ServerAddress);

        return id;
    }
}

internal static partial class InterconnectServerManagerLoggers
{
    [LoggerMessage(
        LogLevel.Information,
        "Remote server [{RemoteEndPoint}] queried group {RoomId} but it does not exist on current server.")]
    public static partial void LogRemoteServerQueriedGroupButNotFound(
        this ILogger logger,
        IPEndPoint? remoteEndPoint,
        Guid roomId);

    [LoggerMessage(
        LogLevel.Information,
        "Remote server [{RemoteEndPoint}] queried group {RoomId} with name {RoomName}.")]
    public static partial void LogRemoteServerQueriedRoomInfo(
        this ILogger logger,
        IPEndPoint? remoteEndPoint,
        Guid roomId,
        string roomName);

    [LoggerMessage(
        LogLevel.Information,
        "Interconnect server [{SessionId}] disconnected from [{ServerAddress}] with name {ServerName}.")]
    public static partial void LogInterconnectServerDisconnected(
        this ILogger logger,
        SessionId sessionId,
        IPEndPoint serverAddress,
        string serverName);

    [LoggerMessage(
        LogLevel.Information,
        "Interconnect server [{SessionId}] attached with name {ServerName} from [{ServerAddress}].")]
    public static partial void LogInterconnectServerAttached(
        this ILogger logger,
        SessionId sessionId,
        string serverName,
        IPEndPoint serverAddress);

    [LoggerMessage(
        LogLevel.Critical,
        "Unauthorized server [{ServerAddress}] trying to make interconnect. Connection has been closed.")]
    public static partial void LogUnauthorizedServerTryingToMakeInterconnect(
        this ILogger logger,
        IPEndPoint serverAddress);

    [LoggerMessage(
        LogLevel.Error,
        "Failed to get room info from remote server [{JoinGroup}].")]
    public static partial void LogFailedToGetRoomInfoFromRemoteServer(
        this ILogger logger,
        JoinGroup joinGroup);

}
