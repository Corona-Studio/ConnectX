using ConnectX.Actors;
using ConnectX.Shared.Messages;
using Hive.Network.Abstractions.Session;
using Hive.Network.Abstractions;
using Microsoft.Extensions.Logging;
using System.Net;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using Hive.Both.General.Dispatchers;
using ConnectX.Shared.Messages.Relay;

namespace ConnectX.Relay;

public partial class RelayServer : ActorTcpListener
{

    private readonly RelayManager _relayManager;

    private readonly ILogger _logger;

    public RelayServer(
        ControlPlaneActor actor,
        TimeProvider timeProvider,
        IDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        IServerSettingProvider serverSettingProvider,
        RelayManager relayManager,
        ILogger<RelayServer> logger)
        : base(actor, dispatcher, serverSettingProvider.RelayEndPoint, logger, loggerFactory, timeProvider)
    {

        _relayManager = relayManager;

        _logger = logger;

        RegisterActorHandlers(dispatcher, actor);
    }

    [ActorMessage]
    private void OnCreateRelayDataLinkMessageReceived(MessageContext<CreateRelayDataLinkMessage> ctx)
    {
        var session = ctx.FromSession;

        // Only the accepted connection can complete admission once.
        if (!TryPromote(session)) return;

        _logger.LogRelayLinkCreateMessageReceived(session.RemoteEndPoint!, session.Id);

        _relayManager.CreateDataLink(session, ctx.Message.UserId, ctx.Message.RoomId);
    }

    [ActorMessage]
    private void OnCreateRelayWorkerLinkMessageReceived(MessageContext<CreateRelayWorkerLinkMessage> ctx)
    {
        var session = ctx.FromSession;

        // Only the accepted connection can complete admission once.
        if (!TryPromote(session)) return;

        _relayManager.CreateWorkerLink(session, ctx.Message.UserId, ctx.Message.RelayTo, ctx.Message.RoomId);
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

    [LoggerMessage(LogLevel.Information, "[CLIENT] RelayLinkCreate received from [{endPoint}] ID [{id}]")]
    public static partial void LogRelayLinkCreateMessageReceived(this ILogger logger, IPEndPoint endPoint, SessionId id);

    [LoggerMessage(LogLevel.Information, "Server stopped.")]
    public static partial void LogServerStopped(this ILogger logger);
}