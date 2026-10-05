using ConnectX.Actors;
using ConnectX.Relay.Helpers;
using ConnectX.Relay.Interfaces;
using ConnectX.Shared;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.Relay;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace ConnectX.Relay;

/// <summary>Handshake I/O is supervised; callbacks and published link state belong to the control actor.</summary>
public partial class ServerLinkHolder : BackgroundService, IServerLinkHolder
{
    private sealed record LinkState(ISession? Session, bool Ready);
    private LinkState _state = new(null, false);
    private readonly ControlPlaneActor _actor;
    private readonly TimeProvider _clock;
    private readonly IDispatcher _dispatcher;
    private readonly IServerSettingProvider _settingProvider;
    private readonly IConnector<TcpSession> _tcpConnector;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectGate = new(1);
    private long _lastHeartbeat;

    public ServerLinkHolder(ControlPlaneActor actor, TimeProvider clock, IDispatcher dispatcher,
        IServerSettingProvider settingProvider, IConnector<TcpSession> tcpConnector,
        IHostApplicationLifetime applicationLifetime, ILogger<ServerLinkHolder> logger)
    {
        _actor = actor;
        _clock = clock;
        _dispatcher = dispatcher;
        _settingProvider = settingProvider;
        _tcpConnector = tcpConnector;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        RegisterActorHandlers(dispatcher, actor);
    }

    public ISession? ServerSession => Volatile.Read(ref _state).Session;
    public bool IsConnected => Volatile.Read(ref _state).Ready;
    public bool IsSignedIn => Volatile.Read(ref _state).Ready;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken);
        ISession? session = null;
        try
        {
            if (IsConnected) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            session = await _tcpConnector.ConnectAsync(_settingProvider.EndPoint, timeout.Token)
                ?? throw new IOException("Cannot connect to the main server.");
            session.BindTo(_dispatcher);
            await _actor.InvokeAsync(() =>
            {
                Volatile.Write(ref _state, new LinkState(session, false));
                _lastHeartbeat = _clock.GetTimestamp();
                _actor.ObserveSession(session);
            }, timeout.Token);
            var result = await _dispatcher.SendAndListenOnce<SigninMessage, SigninResult>(session,
                new SigninMessage
                {
                    JoinP2PNetwork = false,
                    DisplayName = session.LocalEndPoint?.ToString() ?? "Relay",
                    LinkProtocolMajor = LinkProtocolConstants.ProtocolMajor,
                    LinkProtocolMinor = LinkProtocolConstants.ProtocolMinor
                }, timeout.Token);
            if (result is not { Succeeded: true }) throw new IOException("Main server rejected relay sign-in.");
            var address = _settingProvider.PublicListenAddress ??
                (_settingProvider.RelayServerAddress.Equals(IPAddress.Any)
                    ? AddressHelper.GetServerPublicAddress().FirstOrDefault() : _settingProvider.RelayServerAddress)
                ?? throw new IOException("Relay public address is unavailable.");
            var port = _settingProvider.PublicListenPort != 0
                ? _settingProvider.PublicListenPort : _settingProvider.RelayServerPort;
            var registration = await _dispatcher.SendAndListenOnce<RegisterRelayServerMessage, RelayServerRegisteredMessage>(
                session, new RegisterRelayServerMessage(_settingProvider.ServerId, new IPEndPoint(address, port)), timeout.Token);
            if (registration == null) throw new IOException("Main server did not register the relay.");
            await _actor.InvokeAsync(() =>
            {
                if (!ReferenceEquals(ServerSession, session)) throw new IOException("Relay connection was superseded.");
                _lastHeartbeat = _clock.GetTimestamp();
                Volatile.Write(ref _state, new LinkState(session, true));
            }, timeout.Token);
        }
        catch
        {
            await _actor.InvokeAsync(() =>
            {
                if (session != null) _actor.Close(session);
                if (ReferenceEquals(ServerSession, session)) Volatile.Write(ref _state, new LinkState(null, false));
            });
            throw;
        }
        finally { _connectGate.Release(); }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) => _actor.InvokeAsync(Disconnect, cancellationToken);

    private void Disconnect()
    {
        var session = ServerSession;
        Volatile.Write(ref _state, new LinkState(null, false));
        if (session != null) _actor.Close(session);
    }

    [ActorMessage]
    private void OnHeartBeatReceived(MessageContext<HeartBeat> ctx)
    {
        if (ReferenceEquals(ctx.FromSession, ServerSession)) _lastHeartbeat = _clock.GetTimestamp();
    }

    [ActorMessage]
    private void OnShutdownMessageReceived(MessageContext<ShutdownMessage> ctx)
    {
        if (!ReferenceEquals(ctx.FromSession, ServerSession)) return;
        Disconnect();
        _applicationLifetime.StopApplication();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ConnectAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await _actor.InvokeAsync(() =>
                {
                    var session = ServerSession;
                    if (session == null || !SessionHealth.IsConnected(session) ||
                        _clock.GetElapsedTime(_lastHeartbeat) > TimeSpan.FromSeconds(15))
                    {
                        Disconnect();
                        _applicationLifetime.StopApplication();
                        return;
                    }
                    _actor.Send(_dispatcher, session, new HeartBeat());
                }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Relay main-server link failed");
            _applicationLifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DisconnectAsync(cancellationToken);
    }
}
