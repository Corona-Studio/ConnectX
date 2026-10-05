using ConnectX.Actors;
using System.Net;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Server.Messages;
using ConnectX.Shared;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.Server;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

public partial class Server : ActorTcpListener
{

    private readonly GroupManager _groupManager;
    private readonly ClientManager _clientManager;
    // ReSharper disable once InconsistentNaming
    private readonly P2PManager _p2pManager;
    private readonly RelayServerManager _relayServerManager;
    private readonly InterconnectServerManager _interconnectServerManager;

    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly ControlPlaneActor _actor;

    public Server(
        ControlPlaneActor actor,
        TimeProvider timeProvider,
        IDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        IServerSettingProvider serverSettingProvider,
        GroupManager groupManager,
        ClientManager clientManager,
        // ReSharper disable once InconsistentNaming
        P2PManager p2pManager,
        RelayServerManager relayServerManager,
        InterconnectServerManager interconnectServerManager,
        ILogger<Server> logger)
        : base(actor, dispatcher, serverSettingProvider.ListenIpEndPoint, logger, loggerFactory, timeProvider)
    {
        _actor = actor;
        _dispatcher = dispatcher;

        _groupManager = groupManager;
        _clientManager = clientManager;
        _p2pManager = p2pManager;
        _relayServerManager = relayServerManager;
        _interconnectServerManager = interconnectServerManager;

        _logger = logger;

        RegisterActorHandlers(dispatcher, actor);
    }

    [ActorMessage]
    private void OnInterconnectServerRegistrationReceived(MessageContext<InterconnectServerRegistration> ctx)
    {
        var session = ctx.FromSession;

        // Only the accepted connection can complete admission once.
        if (!TryPromote(session))
            return;

        _logger.LogSigninMessageReceived(session.RemoteEndPoint!, session.Id);

        _clientManager.AttachSession(session.Id, session);

        if (_interconnectServerManager.AttachSession(session.Id, session, ctx.Message) == default)
        {
            _clientManager.DetachSession(session.Id);
            return;
        }
        _actor.Send(_dispatcher, session, new InterconnectServerRegistrationSucceeded());
    }

    private static bool CheckProtocolCompatibility(
        int protocolMajor,
        int protocolMinor)
    {
        if (protocolMajor != LinkProtocolConstants.ProtocolMajor)
            return false;
        if (protocolMinor > LinkProtocolConstants.ProtocolMinor)
            return false;
        if (protocolMinor < LinkProtocolConstants.ProtocolMinor - LinkProtocolConstants.MaxAllowedMinorShiftCount)
            return false;

        return true;
    }

    [ActorMessage]
    private void OnSigninMessageReceived(MessageContext<SigninMessage> ctx)
    {
        var session = ctx.FromSession;

        // Only the accepted connection can complete admission once.
        if (!TryPromote(session))
            return;

        if (!CheckProtocolCompatibility(ctx.Message.LinkProtocolMajor, ctx.Message.LinkProtocolMinor) ||
            ctx.Message.JoinP2PNetwork &&
            ctx.Message.LinkProtocolMinor < LinkProtocolConstants.MinimumDirectConnectionProtocolMinor)
        {
            var metadata = new Dictionary<string, string>(2)
            {
                {SigninResult.MetadataServerProtocolMajor, LinkProtocolConstants.ProtocolMajor.ToString("D")},
                {SigninResult.MetadataServerProtocolMinor, LinkProtocolConstants.ProtocolMinor.ToString("D")}
            };
            var result = new SigninResult(
                false,
                Guid.Empty,
                SigninResult.ErrorProtocolMismatch,
                metadata);

            _actor.SendAndClose(_dispatcher, session, result);

            _logger.LogSessionProtocolMismatch(
                session.RemoteEndPoint!,
                session.Id,
                ctx.Message.LinkProtocolMajor,
                ctx.Message.LinkProtocolMinor);

            return;
        }

        _logger.LogSigninMessageReceived(session.RemoteEndPoint!, session.Id);

        if (_clientManager.AttachSession(session.Id, session) == default) { _actor.Close(session); return; }

        var userId = _groupManager.AttachSession(session.Id, session, ctx.Message);

        if (userId == Guid.Empty) { _clientManager.DetachSession(session.Id); return; }
        _relayServerManager.AttachSession(session.Id, userId, session);
        _p2pManager.AttachSession(session, userId, ctx.Message);
        _actor.Send(_dispatcher, session, new SigninResult(true, userId));
    }

}

internal static partial class ServerLoggers
{
    [LoggerMessage(LogLevel.Information, "[SERVER] Current online [{count}]")]
    public static partial void LogCurrentOnline(this ILogger logger, long count);

    [LoggerMessage(LogLevel.Information, "[SERVER] Starting server...")]
    public static partial void LogStartingServer(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[CLIENT] Session [{id}] login timeout, disconnecting...")]
    public static partial void LogSessionLoginTimeout(this ILogger logger, SessionId id);

    [LoggerMessage(LogLevel.Information, "Server started on endpoint [{endPoint}]")]
    public static partial void LogServerStarted(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information,
        "[CLIENT] New Session joined, EndPoint [{endPoint}] ID [{id}], wait for signin message.")]
    public static partial void LogNewSessionJoined(this ILogger logger, IPEndPoint endPoint, SessionId id);

    [LoggerMessage(LogLevel.Information, "[CLIENT] SigninMessage received from [{endPoint}] ID [{id}]")]
    public static partial void LogSigninMessageReceived(this ILogger logger, IPEndPoint endPoint, SessionId id);

    [LoggerMessage(LogLevel.Information, "Server stopped.")]
    public static partial void LogServerStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[CLIENT] Session protocol mismatch, EndPoint [{endPoint}] ID [{id}], Version [{protocolMajor}.{protocolMinor}], disconnecting from the server...")]
    public static partial void LogSessionProtocolMismatch(this ILogger logger, IPEndPoint endPoint, SessionId id, int protocolMajor, int protocolMinor);
}
