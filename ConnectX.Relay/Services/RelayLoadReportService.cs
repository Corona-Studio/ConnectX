using ConnectX.Actors;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Relay.Services;

public sealed class RelayLoadReportService(ControlPlaneActor actor, IDispatcher dispatcher,
    IServerLinkHolder serverLinkHolder, RelayManager relayManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                await actor.InvokeAsync(() =>
                {
                    var session = serverLinkHolder.ServerSession;
                    if (session != null && serverLinkHolder.IsConnected && serverLinkHolder.IsSignedIn)
                        actor.Send(dispatcher, session, relayManager.GetRelayServerLoad());
                }, stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
