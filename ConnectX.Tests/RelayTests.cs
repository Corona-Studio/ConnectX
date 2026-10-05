using ConnectX.Actors;
using ConnectX.Relay;
using ConnectX.Relay.Interfaces;
using ConnectX.Relay.Managers;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Relay.Datagram;
using ConnectX.Shared.Models;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class RelayTests
{
    [Test]
    public async Task AuthorizedMappingReplacementsAndRoomDismissalKeepRoutesConsistent()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var codec = new TestCodec();
        var dispatcher = new ActorDispatcher(codec, NullLogger<ActorDispatcher>.Instance);
        var main = new TestSession(100);
        var holder = new TestLinkHolder(main);
        using var clients = new ClientManager(actor, TimeProvider.System, holder, dispatcher, NullLogger<ClientManager>.Instance);
        var settings = new DefaultServerSettingProvider
        {
            ServerAddress = System.Net.IPAddress.Loopback, RelayServerAddress = System.Net.IPAddress.Loopback,
            EndPoint = new(System.Net.IPAddress.Loopback, 1), RelayEndPoint = new(System.Net.IPAddress.Loopback, 2),
            MaxReferenceConnectionCount = 100, ServerPriority = 80
        };
        using var relay = new RelayManager(actor, clients, holder, dispatcher, settings, NullLogger<RelayManager>.Instance, TimeProvider.System);
        var room = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var dataA = new TestSession(1); var dataB = new TestSession(2);
        var rogue = new TestSession(3);
        dispatcher.Dispatch(rogue, new UpdateRelayUserRoomMappingMessage { RoomId = room, UserId = a, State = GroupUserStates.Joined });
        await actor.InvokeAsync(() => Assert.That(relay.AttachDataSession(dataA, a, room), Is.False));
        await actor.InvokeAsync(() => relay.CreateDataLink(dataA, a, room)); // Mapping has not arrived yet.
        Assert.That(codec.Encoded.OfType<RelayDataLinkCreatedMessage>(), Is.Empty);
        dispatcher.Dispatch(main, new UpdateRelayUserRoomMappingMessage { RoomId = room, UserId = a, State = GroupUserStates.Joined, IsGroupOwner = true });
        dispatcher.Dispatch(main, new UpdateRelayUserRoomMappingMessage { RoomId = room, UserId = b, State = GroupUserStates.Joined });
        var workerA = new TestSession(4); var workerB = new TestSession(5);
        await actor.InvokeAsync(() =>
        {
            Assert.That(relay.AttachDataSession(dataA, a, room), Is.True); clients.AttachSession(dataA.Id, dataA);
            Assert.That(relay.AttachDataSession(dataB, b, room), Is.True); clients.AttachSession(dataB.Id, dataB);
            Assert.That(relay.AttachWorkerSession(workerA, a, b, Guid.NewGuid()), Is.False);
            Assert.That(relay.AttachWorkerSession(workerA, a, b, room), Is.True);
            relay.ActivateWorkerSession();
            workerA.Receive([41]); // Frame arrives before the reverse handshake.
            Assert.That(relay.AttachWorkerSession(workerB, b, a, room), Is.True);
            relay.ActivateWorkerSession();
        });
        workerA.Receive([42]);
        for (var i = 0; i < 100 && workerB.Sent.Count < 2; i++) await Task.Delay(10);
        Assert.That(workerB.Sent.ToArray(), Is.EqualTo(new[] { new byte[] { 41 }, new byte[] { 42 } }));
        dispatcher.Dispatch(rogue, typeof(RelayDatagram), new RelayDatagram(a, b, new byte[] { 9 }));
        await actor.InvokeAsync(() => { });
        Assert.That(codec.Encoded.OfType<UnwrappedRelayDatagram>(), Is.Empty);
        var replacement = new TestSession(6);
        await actor.InvokeAsync(() =>
        {
            Assert.That(relay.AttachDataSession(replacement, a, room), Is.True);
            clients.AttachSession(replacement.Id, replacement);
            clients.DetachSession(dataA.Id); // A late old disconnect cannot evict the replacement.
            Assert.That(relay.GetRelayServerLoad().CurrentConnectionCount, Is.Zero);
        });
        Assert.That(dataA.Closed && workerA.Closed && workerB.Closed, Is.True);
        dispatcher.Dispatch(replacement, typeof(RelayDatagram), new RelayDatagram(a, b, new byte[] { 10 }));
        await actor.InvokeAsync(() => { });
        for (var i = 0; i < 100 && !codec.Encoded.OfType<UnwrappedRelayDatagram>().Any(); i++) await Task.Delay(10);
        Assert.That(codec.Encoded.OfType<UnwrappedRelayDatagram>().Count(), Is.EqualTo(1));
        dispatcher.Dispatch(main, new UpdateRelayUserRoomMappingMessage { RoomId = room, UserId = a, State = GroupUserStates.Dismissed });
        await actor.InvokeAsync(() => { });
        Assert.That(replacement.Closed && dataB.Closed, Is.True);
        await actor.StopAsync(default);
    }

    private sealed class TestLinkHolder(ISession session) : IServerLinkHolder
    {
        public ISession? ServerSession => session;
        public bool IsConnected => true;
        public bool IsSignedIn => true;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }
}
