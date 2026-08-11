using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Shared;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public class ServerLinkHolder : BackgroundService, IServerLinkHolder
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly IRoomTransportCoordinator _roomTransportCoordinator;
    private readonly IClientSettingProvider _settingProvider;
    private readonly IConnector<TcpSession> _tcpConnector;

    private DateTime _lastHeartBeatTime;

    public ServerLinkHolder(
        IDispatcher dispatcher,
        IClientSettingProvider settingProvider,
        IRoomTransportCoordinator roomTransportCoordinator,
        IConnector<TcpSession> tcpConnector,
        ILogger<ServerLinkHolder> logger)
    {
        _dispatcher = dispatcher;
        _settingProvider = settingProvider;
        _roomTransportCoordinator = roomTransportCoordinator;
        _tcpConnector = tcpConnector;
        _logger = logger;

        _dispatcher.AddHandler<HeartBeat>(OnHeartBeatReceived);
        _dispatcher.AddHandler<ShutdownMessage>(OnShutdownMessageReceived);
    }

    public ISession? ServerSession { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsSignedIn { get; private set; }
    public Guid UserId { get; private set; }

    public event Action? OnServerLinkDisconnected;

    public async Task<SigninResult?> ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogConnectingToServer();

        var endPoint = new IPEndPoint(_settingProvider.ServerAddress, _settingProvider.ServerPort);
        var session = await _tcpConnector.ConnectAsync(endPoint, cancellationToken);

        if (session == null)
        {
            _logger.LogFailedToConnectToServer(endPoint);
            return null;
        }

        session.BindTo(_dispatcher);
        session.StartAsync(cancellationToken).Forget();

        _logger.LogSendingSigninMessageToServer();

        await Task.Delay(1000, cancellationToken);

        var signin = new SigninMessage
        {
            JoinP2PNetwork = _roomTransportCoordinator.SupportsDirectConnections,
            DisplayName = $"User-{Guid.CreateVersion7().ToString("N")[^6..]}",
            LinkProtocolMajor = LinkProtocolConstants.ProtocolMajor,
            LinkProtocolMinor = LinkProtocolConstants.ProtocolMinor
        };

        var result =
            await _dispatcher.SendAndListenOnce<SigninMessage, SigninResult>(session, signin, cancellationToken);

        if (result == null)
        {
            _logger.LogWaitForSigninResultFailed(endPoint);
            return null;
        }

        if (!result.Succeeded)
        {
            _logger.LogServerRefusedClientToLogin(endPoint);
            return result;
        }

        _logger.LogSuccessfullyLoggedIn(UserId);

        IsSignedIn = result.Succeeded;
        UserId = result.UserId;

        await TaskHelper.WaitUntilAsync(() => IsSignedIn, cancellationToken);

        _logger.LogConnectedAndSignedToServer(endPoint);

        ServerSession = session;
        IsConnected = true;

        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(() => CheckServerLivenessAsync(cancellationToken));

        return result;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || ServerSession == null) return;

        _logger.LogDisconnectingFromServer();

        await _dispatcher.SendAsync(ServerSession, new ShutdownMessage(), CancellationToken.None);
        ServerSession.Close();

        _logger.LogDisconnectedFromServer();

        IsSignedIn = false;
        IsConnected = false;
    }

    private async Task CheckServerLivenessAsync(CancellationToken cancellationToken)
    {
        var endPoint = new IPEndPoint(_settingProvider.ServerAddress, _settingProvider.ServerPort);

        try
        {
            _logger.LogMainServerLivenessProbeStarted(endPoint);

            // Set the last for init
            _lastHeartBeatTime = DateTime.UtcNow;

            while (cancellationToken is { IsCancellationRequested: false } &&
                   IsConnected &&
                   ServerSession != null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                var lastReceiveTimeSeconds = (DateTime.UtcNow - _lastHeartBeatTime).TotalSeconds;

                if (lastReceiveTimeSeconds <= 15)
                    continue;

                _logger.LogMainServerHeartbeatTimeout(endPoint, lastReceiveTimeSeconds);

                break;
            }
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
        finally
        {
            OnServerLinkDisconnected?.Invoke();

            IsConnected = false;
            ServerSession?.Close();
            ServerSession = null;
        }
    }

    private void OnHeartBeatReceived(MessageContext<HeartBeat> ctx)
    {
        _lastHeartBeatTime = DateTime.UtcNow;
        _logger.LogHeartbeatReceivedFromServer();
    }

    private void OnShutdownMessageReceived(MessageContext<ShutdownMessage> ctx)
    {
        _logger.LogShutdownMessageReceivedFromServer();
        DisconnectAsync(CancellationToken.None).Forget();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogStartSendingHeartbeat();

        await TaskHelper.WaitUntilAsync(() => IsConnected, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsConnected || ServerSession == null)
            {
                // Wait for connection to be established
                await TaskHelper.WaitUntilAsync(() => IsConnected && ServerSession != null, stoppingToken);
                continue;
            }

            await _dispatcher.SendAsync(ServerSession, new HeartBeat(), stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        _logger.LogStopSendingHeartbeat();
    }
}

internal static partial class ServerLinkHolderLoggers
{
    [LoggerMessage(LogLevel.Information, "[CLIENT] Successfully logged into server, assigned id: {id}")]
    public static partial void LogSuccessfullyLoggedIn(this ILogger logger, Guid id);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Connecting to server...")]
    public static partial void LogConnectingToServer(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Failed to connect to server at endpoint {endPoint}")]
    public static partial void LogFailedToConnectToServer(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Sending signin message to server...")]
    public static partial void LogSendingSigninMessageToServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Connected and signed to server at endpoint {endPoint}")]
    public static partial void LogConnectedAndSignedToServer(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Disconnecting from server...")]
    public static partial void LogDisconnectingFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Disconnected from server.")]
    public static partial void LogDisconnectedFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Debug, "[CLIENT] Heartbeat received from server.")]
    public static partial void LogHeartbeatReceivedFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Shutdown message received from server.")]
    public static partial void LogShutdownMessageReceivedFromServer(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Start sending heartbeat...")]
    public static partial void LogStartSendingHeartbeat(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Stop sending heartbeat.")]
    public static partial void LogStopSendingHeartbeat(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[CLIENT] Server liveness probe started for [{relayEndPoint}]")]
    public static partial void LogMainServerLivenessProbeStarted(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Warning, "[CLIENT] Server liveness probe stopped for [{relayEndPoint}]")]
    public static partial void LogMainServerLivenessProbeStopped(this ILogger logger, IPEndPoint relayEndPoint);

    [LoggerMessage(LogLevel.Critical,
        "[CLIENT] Link with server [{relayEndPoint}] is down, last heartbeat received [{seconds} seconds ago]")]
    public static partial void LogMainServerHeartbeatTimeout(this ILogger logger, IPEndPoint relayEndPoint,
        double seconds);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Wait for signin result failed, endpoint: {endPoint}")]
    public static partial void LogWaitForSigninResultFailed(this ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(LogLevel.Error, "[CLIENT] Server refused client to login at endpoint {endPoint}")]
    public static partial void LogServerRefusedClientToLogin(this ILogger logger, IPEndPoint endPoint);
}
