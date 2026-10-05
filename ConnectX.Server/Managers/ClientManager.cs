using ConnectX.Actors;
using ConnectX.Shared.Messages;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Server.Managers;

public delegate void SessionDisconnectedHandler(SessionId sessionId);

/// <summary>Actor-owned connection liveness. Detachment is idempotent and precedes notifications.</summary>
public partial class ClientManager : BackgroundService
{
    private readonly ControlPlaneActor _actor;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Dictionary<SessionId, WatchDog> _watchDogMapping = [];

    public ClientManager(ControlPlaneActor actor, TimeProvider timeProvider, IServiceScopeFactory serviceScopeFactory,
        IDispatcher dispatcher, ILogger<ClientManager> logger)
    {
        _actor = actor;
        _timeProvider = timeProvider;
        _dispatcher = dispatcher;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        RegisterActorHandlers(dispatcher, actor);
    }

    public event SessionDisconnectedHandler? OnSessionDisconnected;

    public SessionId AttachSession(SessionId id, ISession session)
    {
        _actor.AssertAccess();
        if (!_watchDogMapping.TryAdd(id, new WatchDog(session, _timeProvider))) return default;
        _logger.LogSessionAttached(id);
        return id;
    }

    public bool IsSessionAttached(SessionId id)
    {
        _actor.AssertAccess();
        return _watchDogMapping.ContainsKey(id);
    }

    public void DetachSession(SessionId id)
    {
        _actor.AssertAccess();
        if (!_watchDogMapping.Remove(id, out var watchdog)) return;
        _actor.Close(watchdog.Session);
        OnSessionDisconnected?.Invoke(id);
    }

    [ActorMessage]
    private void OnReceivedShutdownMessage(MessageContext<ShutdownMessage> ctx)
    {
        if (_watchDogMapping.TryGetValue(ctx.FromSession.Id, out var watchdog) &&
            ReferenceEquals(watchdog.Session, ctx.FromSession)) DetachSession(ctx.FromSession.Id);
    }

    [ActorMessage]
    private void OnReceivedHeartBeat(MessageContext<HeartBeat> ctx)
    {
        if (!_watchDogMapping.TryGetValue(ctx.FromSession.Id, out var watchdog) ||
            !ReferenceEquals(watchdog.Session, ctx.FromSession))
        {
            _actor.SendAndClose(ctx.Dispatcher, ctx.FromSession, new ShutdownMessage());
            return;
        }
        watchdog.Received();
        using var scope = _serviceScopeFactory.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<InterconnectServerManager>()
            .IsServerRegisteredForInterconnect(ctx.FromSession.Id)) return;
        _actor.Send(ctx.Dispatcher, ctx.FromSession, new HeartBeat());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await _actor.InvokeAsync(() =>
                {
                    foreach (var (id, watchdog) in _watchDogMapping.ToArray())
                        if (watchdog.IsTimeoutExceeded() || !SessionHealth.IsConnected(watchdog.Session)) DetachSession(id);
                }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _actor.InvokeAsync(() =>
        {
            foreach (var id in _watchDogMapping.Keys.ToArray()) DetachSession(id);
        }, cancellationToken);
    }
}

internal static partial class ClientManagerLoggers
{
    [LoggerMessage(LogLevel.Error,
        "[CLIENT_MANAGER] Failed to add session to the session mapping, session id: {sessionId}")]
    public static partial void LogFailedToAddSessionToSessionMapping(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[CLIENT_MANAGER] Session attached, session id: {sessionId}")]
    public static partial void LogSessionAttached(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[CLIENT_MANAGER] Received shutdown message from session, session id: {sessionId}")]
    public static partial void LogReceivedShutdownMessage(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning,
        "[CLIENT_MANAGER] Received heartbeat from unattached session, session id: {sessionId}")]
    public static partial void LogReceivedHeartBeatFromUnattachedSession(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[CLIENT_MANAGER] Watchdog started.")]
    public static partial void LogWatchDogStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Warning,
        "[CLIENT_MANAGER] Session timeout, session id: {sessionId}, removed from session mapping.")]
    public static partial void LogSessionTimeout(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[CLIENT_MANAGER] Watchdog stopped.")]
    public static partial void LogWatchDogStopped(this ILogger logger);
}
