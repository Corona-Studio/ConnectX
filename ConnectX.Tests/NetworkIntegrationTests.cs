using System.Buffers;

using System.Net;
using ConnectX.Actors;
using ConnectX.Client.Transmission.Connections;
using ConnectX.Relay;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Services;
using ConnectX.Server;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Server.Services;
using ConnectX.Shared;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Relay.Datagram;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using ServerClientManager = ConnectX.Server.Managers.ClientManager;
using RelayClientManager = ConnectX.Relay.Managers.ClientManager;

namespace ConnectX.Tests;

[TestFixture]
public sealed class NetworkIntegrationTests
{
    [Test]
    public async Task RealTcpLoginRoomsDatagramsWorkerStreamsAndShutdown()
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

        var relayBuilder = Host.CreateApplicationBuilder();
        relayBuilder.Logging.ClearProviders();
        AddTransport(relayBuilder.Services);
        var relaySettings = new RelaySettings(serverAddress);
        relayBuilder.Services.AddSingleton<ConnectX.Relay.Interfaces.IServerSettingProvider>(relaySettings);
        relayBuilder.Services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
        relayBuilder.Services.AddSingleton<RelayClientManager>();
        relayBuilder.Services.AddSingleton<ConnectX.Relay.Managers.RelayManager>();
        relayBuilder.Services.AddHostedService(sp => sp.GetRequiredService<RelayClientManager>());
        relayBuilder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectX.Relay.Managers.RelayManager>());
        relayBuilder.Services.AddSingleton<RelayServer>();
        relayBuilder.Services.AddHostedService(sp => sp.GetRequiredService<RelayServer>());
        using var relayHost = relayBuilder.Build();
        await relayHost.StartAsync(lifetime.Token);
        relaySettings.PublicListenPort = (ushort)relayHost.Services.GetRequiredService<RelayServer>().ListenEndPoint!.Port;
        var upstream = relayHost.Services.GetRequiredService<IServerLinkHolder>();
        await upstream.StartAsync(lifetime.Token);
        var services = new ServiceCollection();
        services.AddLogging(); services.AddConnectXEssentials();
        services.AddSingleton<IDispatcher, ActorDispatcher>();
        using var clientServices = services.BuildServiceProvider();
        var dispatcher = clientServices.GetRequiredService<IDispatcher>();
        var connector = clientServices.GetRequiredService<IConnector<TcpSession>>();
        var sessions = new List<ISession>(); var loops = new List<Task>();

        async Task<ISession> Connect(IPEndPoint endpoint)
        {
            var session = await connector.ConnectAsync(endpoint, lifetime.Token) ?? throw new IOException("Connect failed.");
            session.BindTo(dispatcher);
            loops.Add(session.StartAsync(lifetime.Token)); sessions.Add(session);
            return session;
        }
        async Task<R> Request<T, R>(ISession session, T message)
        {
            var response = await dispatcher.SendAndListenOnce<T, R>(session, message, lifetime.Token)
                ?? throw new IOException($"No {typeof(R).Name} reply.");
            return response;
        }
        SigninMessage Signin(string name) => new()
        {
            DisplayName = name, JoinP2PNetwork = false, LinkProtocolMajor = LinkProtocolConstants.ProtocolMajor,
            LinkProtocolMinor = LinkProtocolConstants.ProtocolMinor
        };
        try
        {
            while (!upstream.IsConnected) await Task.Delay(10, lifetime.Token);
            var relayActor = relayHost.Services.GetRequiredService<ControlPlaneActor>();
            await relayActor.InvokeAsync(() => relayActor.Send(relayHost.Services.GetRequiredService<IDispatcher>(),
                upstream.ServerSession!, relayHost.Services.GetRequiredService<ConnectX.Relay.Managers.RelayManager>().GetRelayServerLoad()));
            var serverActor = serverHost.Services.GetRequiredService<ControlPlaneActor>();
            while (!await serverActor.AskAsync(() => serverHost.Services.GetRequiredService<RelayLoadManager>().TryGetMostAvailableRelaySession(out _)))
                await Task.Delay(10, lifetime.Token);
            var a = await Connect(serverAddress); var b = await Connect(serverAddress);
            var signA = await Request<SigninMessage, SigninResult>(a, Signin("a"));
            var signB = await Request<SigninMessage, SigninResult>(b, Signin("b"));
            Assert.That(signA.Succeeded && signB.Succeeded, Is.True);
            var rogue = await Connect(serverAddress);
            await Request<HeartBeat, ShutdownMessage>(rogue, new());
            await Request<HeartBeat, HeartBeat>(a, new()); // Rogue heartbeat must not unregister the global handler.
            var direct = await Request<CreateGroup, GroupOpResult>(a, new() { RoomName = "direct", MaxUserCount = 2, UseRelayServer = false });
            Assert.That(direct.Status, Is.EqualTo(GroupCreationStatus.NetworkControllerNotReady));
            var created = await Request<CreateGroup, GroupOpResult>(a, new() { RoomName = "actor-test", UseRelayServer = true, MaxUserCount = 2 });
            Assert.That(created.Status, Is.EqualTo(GroupCreationStatus.Succeeded));
            var joined = await Request<JoinGroup, GroupOpResult>(b, new() { GroupId = created.RoomId });
            Assert.That(joined.Status, Is.EqualTo(GroupCreationStatus.Succeeded));
            var relayAddress = new IPEndPoint(IPAddress.Loopback, relaySettings.PublicListenPort);
            var dataA = await Connect(relayAddress); var dataB = await Connect(relayAddress);
            await Request<CreateRelayDataLinkMessage, RelayDataLinkCreatedMessage>(dataA, new() { UserId = signA.UserId, RoomId = created.RoomId });
            await Request<CreateRelayDataLinkMessage, RelayDataLinkCreatedMessage>(dataB, new() { UserId = signB.UserId, RoomId = created.RoomId });
            var datagram = dispatcher.HandleOnce<UnwrappedRelayDatagram>(dataB, lifetime.Token);
            await dispatcher.SendAsync(dataA, new RelayDatagram(signA.UserId, signB.UserId, new byte[] { 1, 2, 3 }), lifetime.Token);
            Assert.That((await datagram)!.Payload.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            var workerA = await Connect(relayAddress); var workerB = await Connect(relayAddress);
            await Request<CreateRelayWorkerLinkMessage, RelayWorkerLinkCreatedMessage>(workerA, new() { UserId = signA.UserId, RelayTo = signB.UserId, RoomId = created.RoomId });
            workerB.OnMessageReceived -= dispatcher.Dispatch;
            var raw = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var receiver = new RelayWorkerReceiver(workerB, clientServices.GetRequiredService<Hive.Codec.Abstractions.IPacketCodec>(),
                (_, bytes) => raw.TrySetResult(bytes.ToArray()));
            // The relay buffers this frame until the reverse worker is ready, then sends it directly after the ACK.
            using var payload = new MemoryStream([4, 5, 6]);
            Assert.That(await workerA.TrySendAsync(payload, lifetime.Token), Is.True);
            await receiver.EstablishAsync(new() { UserId = signB.UserId, RelayTo = signA.UserId, RoomId = created.RoomId }, lifetime.Token);
            Assert.That(await raw.Task.WaitAsync(lifetime.Token), Is.EqualTo(new byte[] { 4, 5, 6 }));
            await Request<LeaveGroup, GroupOpResult>(b, new());
            await Request<LeaveGroup, GroupOpResult>(a, new());
            var afterLeave = await Request<AcquireGroupInfo, GroupInfo>(a, new());
            Assert.That(afterLeave.RoomId, Is.EqualTo(GroupInfo.Invalid.RoomId));
        }
        finally
        {
            foreach (var session in sessions) SessionHealth.Close(session);
            lifetime.Cancel();
            await Task.WhenAll(loops);
            foreach (var session in sessions.OfType<IDisposable>()) session.Dispose();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await upstream.StopAsync(shutdown.Token);
            await relayHost.StopAsync(shutdown.Token);
            await serverHost.StopAsync(shutdown.Token);
        }
    }

    private static void AddTransport(IServiceCollection services)
    {
        services.AddConnectXEssentials(); services.AddSingleton<IDispatcher, ActorDispatcher>();
        services.AddSingleton(TimeProvider.System); services.AddSingleton<ControlPlaneActor>();
        services.AddHostedService(sp => sp.GetRequiredService<ControlPlaneActor>());
    }

    private sealed class RelaySettings(IPEndPoint server) : ConnectX.Relay.Interfaces.IServerSettingProvider
    {
        public IPAddress ServerAddress => server.Address;
        public ushort ServerPort => (ushort)server.Port;
        public IPAddress RelayServerAddress => IPAddress.Loopback;
        public ushort RelayServerPort => PublicListenPort;
        public IPAddress? PublicListenAddress => IPAddress.Loopback;
        public ushort PublicListenPort { get; set; }
        public bool JoinP2PNetwork => false;
        public Guid ServerId => Guid.Parse("ddad03b9-d122-421d-add7-1d09e65b4295");
        public IPEndPoint EndPoint => server;
        public IPEndPoint RelayEndPoint => new(IPAddress.Loopback, 0);
        public int MaxReferenceConnectionCount => 100;
        public uint ServerPriority => 80;
    }
}
