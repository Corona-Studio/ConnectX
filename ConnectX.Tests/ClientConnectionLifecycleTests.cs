using System.Net;
using ConnectX.Actors;
using ConnectX.Server;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Server.Services;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using ServerClientManager = ConnectX.Server.Managers.ClientManager;

namespace ConnectX.Tests;

[TestFixture]
public sealed class ClientConnectionLifecycleTests
{
    [Test]
    public async Task RequestedDisconnectRemoteShutdownAndStaleHeartbeatAreDistinct()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:ListenAddress"] = "127.0.0.1", ["Server:ListenPort"] = "0",
            ["Server:PublicListenAddress"] = "127.0.0.1", ["Server:PublicListenPort"] = "0",
            ["Server:ServerId"] = "ddad03b9-d122-421d-add7-1d09e65b4295"
        });
        AddTransport(builder.Services);
        builder.Services.RegisterConnectXServerPackets();
        builder.Services.AddSingleton<ConnectX.Server.Interfaces.IServerSettingProvider, ConfigSettingProvider>();
        builder.Services.AddSingleton<IInterconnectServerSettingProvider, InterconnectServerSettingProvider>();
        builder.Services.AddSingleton<ServerClientManager>();
        builder.Services.AddSingleton<GroupManager>();
        builder.Services.AddSingleton<P2PManager>();
        builder.Services.AddSingleton<RelayServerManager>();
        builder.Services.AddSingleton<RelayLoadManager>();
        builder.Services.AddSingleton<InterconnectServerManager>();
        builder.Services.AddSingleton<RoomCreationRecordService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerClientManager>());
        builder.Services.AddSingleton<ConnectX.Server.Server>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectX.Server.Server>());
        using var serverHost = builder.Build();
        await serverHost.StartAsync(lifetime.Token);
        var serverAddress = serverHost.Services.GetRequiredService<ConnectX.Server.Server>().ListenEndPoint!;

        var holderCollection = new ServiceCollection();
        holderCollection.AddLogging(); holderCollection.AddConnectXEssentials();
        holderCollection.AddSingleton<IDispatcher, ActorDispatcher>();
        using var holderServices = holderCollection.BuildServiceProvider();
        var holderDispatcher = holderServices.GetRequiredService<IDispatcher>();
        using (var holder = new ConnectX.Client.ServerLinkHolder(holderDispatcher, new ClientSettings(serverAddress),
                   new ConnectX.Client.RelayRoomTransportCoordinator(), holderServices.GetRequiredService<IConnector<TcpSession>>(),
                   Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnectX.Client.ServerLinkHolder>.Instance))
        {
            var disconnects = new System.Collections.Concurrent.ConcurrentQueue<bool>();
            holder.OnServerLinkDisconnected += () => disconnects.Enqueue(holder.LastDisconnectWasRequested);
            Assert.That((await holder.ConnectAsync(lifetime.Token))?.Succeeded, Is.True);
            var oldSession = holder.ServerSession;
            await holder.DisconnectAsync(lifetime.Token);
            Assert.That(disconnects.ToArray(), Is.EqualTo((bool[])[true]));
            Assert.That(holder.ServerSession, Is.Null);
            Assert.That((await holder.ConnectAsync(lifetime.Token))?.Succeeded, Is.True);
            var newSession = holder.ServerSession;
            using var staleCancellation = new CancellationTokenSource(); staleCancellation.Cancel();
            var monitor = typeof(ConnectX.Client.ServerLinkHolder).GetMethod("CheckServerLivenessAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)monitor.Invoke(holder, [oldSession, staleCancellation.Token])!;
            Assert.That(holder.ServerSession, Is.SameAs(newSession), "An old heartbeat cleanup must not close a reconnected session.");
            Assert.That(disconnects.Count, Is.EqualTo(1));
            var shutdown = typeof(ConnectX.Client.ServerLinkHolder).GetMethod("OnShutdownMessageReceived",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            shutdown.Invoke(holder, [new MessageContext<ShutdownMessage>(newSession!, holderDispatcher, new())]);
            await TaskHelper.WaitUntilAsync(() => disconnects.Count == 2, lifetime.Token);
            Assert.That(disconnects.ToArray(), Is.EqualTo((bool[])[true, false]), "Remote shutdown must remain distinguishable from a requested disconnect.");
        }
        await serverHost.StopAsync();
    }
    private static void AddTransport(IServiceCollection services)
    {
        InitHelper.AddConnectXEssentials(services); services.AddSingleton<IDispatcher, ActorDispatcher>();
        services.AddSingleton(TimeProvider.System); services.AddSingleton<ControlPlaneActor>();
        services.AddHostedService(sp => sp.GetRequiredService<ControlPlaneActor>());
    }

    private sealed class ClientSettings(IPEndPoint server) : ConnectX.Client.Interfaces.IClientSettingProvider
    {
        public IPAddress ServerAddress => server.Address;
        public ushort ServerPort => (ushort)server.Port;
    }
}
