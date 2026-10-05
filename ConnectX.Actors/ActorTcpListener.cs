using System.Net;
using System.Net.Sockets;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Actors;

/// <summary>Transport ingress has no access to control state except via the mailbox.</summary>
public abstract class ActorTcpListener(
    ControlPlaneActor actor, IDispatcher dispatcher, IPEndPoint endPoint,
    ILogger logger, ILoggerFactory loggerFactory, TimeProvider timeProvider) : BackgroundService
{
    private readonly SessionAdmission _admission = new(timeProvider);
    private Socket? _listener;
    protected bool TryPromote(ISession session) { actor.AssertAccess(); return _admission.TryPromote(session); }
    public IPEndPoint? ListenEndPoint => _listener?.LocalEndPoint as IPEndPoint;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            _listener.Bind(endPoint);
            _listener.Listen(512);
            await base.StartAsync(cancellationToken);
        }
        catch { _listener.Dispose(); _listener = null; throw; }
    }

    private async Task AcceptAsync(CancellationToken stoppingToken)
    {
        var nextId = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var socket = await _listener!.AcceptAsync(stoppingToken);
            var session = new TcpSession(++nextId, socket, loggerFactory.CreateLogger<TcpSession>());
            session.OnMessageReceived += dispatcher.Dispatch;
            if (!actor.TryPost(() =>
            {
                var replaced = _admission.Accept(session);
                if (replaced != null) actor.Close(replaced);
                socket.NoDelay = true;
                actor.ObserveSession(session);
            }))
            {
                SessionHealth.Close(session);
                session.Dispose();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogListenerStarted(ListenEndPoint!);
        using var loops = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var accept = AcceptAsync(loops.Token);
        var sweep = SweepAsync(loops.Token);
        try
        {
            await Task.WhenAny(accept, sweep);
            loops.Cancel();
            await Task.WhenAll(accept, sweep);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await actor.InvokeAsync(() =>
            {
                actor.ReapCompletedIO();
                foreach (var session in _admission.RemoveExpired(TimeSpan.FromMinutes(10))) actor.Close(session);
            }, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _listener?.Dispose();
        _listener = null;
        await actor.InvokeAsync(() =>
        {
            foreach (var session in _admission.Drain()) actor.Close(session);
        }, cancellationToken);
    }

    public override void Dispose() { _listener?.Dispose(); base.Dispose(); }
}

internal static partial class ListenerLoggers
{
    [LoggerMessage(LogLevel.Information, "Actor listener started on {endPoint}")]
    public static partial void LogListenerStarted(this ILogger logger, IPEndPoint endPoint);
}
