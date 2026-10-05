using System.Net;
using ConnectX.Actors;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Messages;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Server;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server;

/// <summary>Each outbound peer has a supervised serial lifecycle; no shared reconnect queues or detached loops.</summary>
public sealed class InterconnectServerLinkHolder(ControlPlaneActor actor, IDispatcher dispatcher,
    IConnector<TcpSession> tcpConnector, IServerSettingProvider serverSettingProvider,
    IInterconnectServerSettingProvider settings, ILogger<InterconnectServerLinkHolder> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(settings.EndPoints.Distinct().Select(endpoint => RunPeerAsync(endpoint, stoppingToken)));

    private async Task RunPeerAsync(IPEndPoint endpoint, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ISession? session = null;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                session = await tcpConnector.ConnectAsync(endpoint, handshake.Token)
                    ?? throw new IOException("Remote server unavailable.");
                session.BindTo(dispatcher);
                await actor.InvokeAsync(() => actor.ObserveSession(session), handshake.Token);
                var result = await dispatcher.SendAndListenOnce<InterconnectServerRegistration, InterconnectServerRegistrationSucceeded>(
                    session, new InterconnectServerRegistration
                    {
                        ServerName = serverSettingProvider.ServerName,
                        ServerMotd = serverSettingProvider.ServerMotd,
                        ServerAddress = serverSettingProvider.ServerPublicEndPoint
                    }, handshake.Token);
                if (result == null) throw new IOException("Interconnect registration failed.");
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                while (SessionHealth.IsConnected(session) && await timer.WaitForNextTickAsync(stoppingToken))
                    await actor.InvokeAsync(() => actor.Send(dispatcher, session, new HeartBeat()), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Interconnect peer {endpoint} unavailable", endpoint); }
            finally
            {
                if (session != null) await actor.InvokeAsync(() => actor.Close(session));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
