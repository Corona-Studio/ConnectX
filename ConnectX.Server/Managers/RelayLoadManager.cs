using ConnectX.Actors;
using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace ConnectX.Server.Managers;

public partial class RelayLoadManager
{
    private readonly ClientManager _clientManager;
    private readonly RelayServerManager _relayServers;

    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly Dictionary<SessionId, double> _relayAvailabilityMapping = [];

    private readonly ControlPlaneActor _actor;

    public RelayLoadManager(
        ControlPlaneActor actor,
        ClientManager clientManager,
        RelayServerManager relayServers,
        IDispatcher dispatcher,
        ILogger<RelayLoadManager> logger)
    {
        _actor = actor;
        _dispatcher = dispatcher;
        _logger = logger;
        _clientManager = clientManager;
        _relayServers = relayServers;

        clientManager.OnSessionDisconnected += OnSessionDisconnected;

        RegisterActorHandlers(dispatcher, actor);
    }

    public bool TryGetMostAvailableRelaySession([NotNullWhen(true)] out SessionId? sessionId)
    {
        _actor.AssertAccess();
        sessionId = _relayAvailabilityMapping.Count > 0
            ? _relayAvailabilityMapping.MaxBy(x => x.Value).Key
            : null;

        return _relayAvailabilityMapping.Count > 0;
    }

    private void OnSessionDisconnected(SessionId sessionId)
    {
        _relayAvailabilityMapping.Remove(sessionId, out _);
    }

    [ActorMessage]
    private void OnRelayServerLoadInfoMessageRecevied(MessageContext<RelayServerLoadInfoMessage> ctx)
    {
        if (!_clientManager.IsSessionAttached(ctx.FromSession.Id) ||
            !_relayServers.TryGetRelayServerAddress(ctx.FromSession.Id, out _))
            return;

        if (ctx.Message.MaxReferenceConnectionCount <= 0 || ctx.Message.CurrentConnectionCount < 0) return;
        double availability = CalculateRelayServerAvailability(ctx.Message);
        _relayAvailabilityMapping[ctx.FromSession.Id] = availability;

        _logger.LogRelayServerLoadReceviced(ctx.FromSession.Id, availability);
    }

    private static double CalculateRelayServerAvailability(RelayServerLoadInfoMessage relayServerLoad)
    {
        return
            ((relayServerLoad.MaxReferenceConnectionCount - relayServerLoad.CurrentConnectionCount) / (double)relayServerLoad.MaxReferenceConnectionCount) * 0.4 +
            (relayServerLoad.Priority / (double)100) * 0.6;
    }
}

internal static partial class RelayLoadManagerLoggers
{
    [LoggerMessage(LogLevel.Debug, "[RELAY_LOAD_MANAGER] Relay server {SessionId} reported its load: [Availability: {Availability}]")]
    public static partial void LogRelayServerLoadReceviced(this ILogger logger, SessionId sessionId, double availability);
}
