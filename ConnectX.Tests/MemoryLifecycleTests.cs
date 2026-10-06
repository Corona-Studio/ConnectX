using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using ConnectX.Actors;
using ConnectX.Relay.Managers;
using ConnectX.Server;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Server.Models;
using ConnectX.Server.Models.Contexts;
using ConnectX.Server.Services;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using ServerClients = ConnectX.Server.Managers.ClientManager;

namespace ConnectX.Tests;

[TestFixture]
public sealed class MemoryLifecycleTests
{
    [Test]
    public async Task CreationHistoryReleasesEachUnitOfWorkAcrossRepeatedWrites()
    {
        var probe = new SaveProbe();
        using var provider = DatabaseServices(probe);
        using var service = new RoomCreationRecordService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RoomCreationRecordService>.Instance);
        for (var i = 0; i < 128; i++) service.CreateRecord(NewRecord());
        await service.StartAsync(default);
        await Until(() => probe.Saves == 128);
        await service.StopAsync(default);
        using var scope = provider.CreateScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>().RoomCreateHistories.CountAsync(), Is.EqualTo(128));
        AssertDisposed(probe, 128);
    }

    [Test]
    public async Task FailedCreationWritesRetryInFreshContextsWithoutDuplicateEntities()
    {
        var probe = new SaveProbe { FailuresRemaining = 2 };
        using var provider = DatabaseServices(probe);
        using var service = new RoomCreationRecordService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RoomCreationRecordService>.Instance);
        service.CreateRecord(NewRecord());
        await service.StartAsync(default);
        await Until(() => probe.Saves == 1, TimeSpan.FromSeconds(20));
        await service.StopAsync(default);
        using var scope = provider.CreateScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>().RoomCreateHistories.CountAsync(), Is.EqualTo(1));
        AssertDisposed(probe, 3);
    }

    [Test]
    public void CreationHistoryBacklogIsBoundedAndReleasedOnDisposal()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new RoomCreationRecordService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RoomCreationRecordService>.Instance);
        for (var i = 0; i < 4096; i++) service.CreateRecord(NewRecord());
        var queue = Field<Channel<RoomRecord>>(service, "_roomRecords");
        Assert.That(queue.Reader.Count, Is.EqualTo(1024));
        service.Dispose();
        Assert.That(queue.Reader.Count, Is.Zero);
        service.CreateRecord(NewRecord());
        Assert.That(queue.Reader.Count, Is.Zero);
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task JoinHistoryReleasesEachContextAndCleansThrottleCacheWhileIdle(int failures)
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var probe = new SaveProbe { FailuresRemaining = failures };
        using var provider = DatabaseServices(probe, actor);
        var groups = provider.GetRequiredService<GroupManager>();
        var clients = provider.GetRequiredService<ServerClients>();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var peers = provider.GetRequiredService<PeerInfoService>();
        typeof(PeerInfoService).GetProperty(nameof(PeerInfoService.NetworkPeers))!.SetValue(peers,
            new ConnectX.Server.Models.ZeroTier.NetworkPeerModel[]
            {
                new() { Address = "test-node", Paths = [new() { Active = true, Address = "127.0.0.1/9999" }] }
            });
        using var service = new RoomJoinRecordService(actor, groups, peers, dispatcher,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RoomJoinRecordService>.Instance);
        await actor.InvokeAsync(() =>
        {
            for (var i = 1; i <= 64; i++)
            {
                var session = new TestSession(i);
                clients.AttachSession(session.Id, session);
                var user = groups.AttachSession(session.Id, session, new SigninMessage { DisplayName = "test", JoinP2PNetwork = false, LinkProtocolMajor = 2, LinkProtocolMinor = 2 });
                var owner = new UserSessionInfo(new BasicUserInfo { UserId = user, DisplayName = "test", JoinP2PNetwork = false, Session = session }, null);
                var room = new Group("test", null, owner, [owner]) { MaxUserCount = 2, NetworkId = 1 };
                Field<Dictionary<Guid, Group>>(groups, "_groupMappings").Add(room.RoomId, room);
                dispatcher.Dispatch(session, new UpdateRoomMemberNetworkInfo { NetworkNodeId = "test-node", NetworkIpAddresses = [] });
            }
        });
        await actor.InvokeAsync(() => { });
        await service.StartAsync(default);
        await Until(() => probe.Saves == 64);
        var cache = Field<ConcurrentDictionary<Guid, DateTime>>(service, "_lastRefreshTimes");
        await actor.InvokeAsync(() =>
        {
            foreach (var key in cache.Keys) cache[key] = DateTime.UtcNow.AddMinutes(-6);
        });
        await Until(() => cache.IsEmpty);
        await service.StopAsync(default);
        AssertDisposed(probe, 64 + failures);
        clients.Dispose(); // Clear events before stopping the actor used by this test provider.
        await actor.StopAsync(default);
    }

    [Test]
    public async Task DuplicateAttachmentsDoNotLeaveOrphanedSessionIndexes()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        using var provider = DatabaseServices(new SaveProbe(), actor);
        var groups = provider.GetRequiredService<GroupManager>();
        var clients = provider.GetRequiredService<ServerClients>();
        var relays = provider.GetRequiredService<RelayServerManager>();
        var p2p = provider.GetRequiredService<P2PManager>();
        var session = new TestSession(1);
        var signin = new SigninMessage { DisplayName = "test", JoinP2PNetwork = true, LinkProtocolMajor = 2, LinkProtocolMinor = 2 };
        await actor.InvokeAsync(() =>
        {
            clients.AttachSession(session.Id, session);
            var user = groups.AttachSession(session.Id, session, signin);
            for (var i = 0; i < 128; i++)
            {
                Assert.That(groups.AttachSession(session.Id, session, signin), Is.EqualTo(Guid.Empty));
                relays.AttachSession(session.Id, Guid.NewGuid(), session);
                p2p.AttachSession(session, Guid.NewGuid(), signin);
            }
            clients.DetachSession(session.Id);
            foreach (var (manager, field) in new[] { ((object)groups, "_userMapping"), (relays, "_sessionMapping"), (p2p, "_userSessionMappings") })
                Assert.That(Field<IDictionary>(manager, field).Count, Is.Zero);
        });
        await actor.StopAsync(default);
    }

    [Test]
    public async Task FailedOutboxReleasesQueuedPayloads()
    {
        using var lifetime = new CancellationTokenSource();
        var session = new TestSession(1) { BlockSends = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var outbox = new SessionOutbox(session, lifetime.Token, NullLogger.Instance);
        outbox.Enqueue(token => session.TrySendAsync(new MemoryStream([1]), token));
        for (var i = 0; i < 50; i++) outbox.Enqueue(_ => ValueTask.FromResult(true));
        lifetime.Cancel();
        await outbox.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var queue = Field<Channel<Func<CancellationToken, ValueTask<bool>>>>(outbox, "_queue");
        Assert.That(queue.Reader.Count, Is.Zero);
        Assert.That(session.Closed, Is.True);
    }

    [Test]
    public void WorkerStartupBufferIsBoundedByBytesAndPairingHasDeadline()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        var clock = new ManualClock();
        var source = new TestSession(1);
        var input = new RelayWorkerInput(actor, source, clock);
        input.Receive(source, new ReadOnlySequence<byte>(new byte[RelayWorkerInput.MaxBufferedBytes]));
        input.Receive(source, new ReadOnlySequence<byte>(new byte[1]));
        Assert.That(source.Closed, Is.True);
        Assert.That(Field<Queue<byte[]>>(input, "_pending"), Is.Empty);
        var waiting = new RelayWorkerInput(actor, new TestSession(2), clock);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That(waiting.IsPairingTimeoutExceeded(), Is.True);
        waiting.Close();
        Assert.That(waiting.IsPairingTimeoutExceeded(), Is.False);
    }

    [Test]
    public async Task UnpairedWorkerSweepClosesConnectionAndRemovesInputHandler()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var clock = new ManualClock();
        var dispatcher = new ActorDispatcher(new TestCodec(), NullLogger<ActorDispatcher>.Instance);
        var main = new TestSession(100);
        var holder = new TestHolder(main);
        using var clients = new ConnectX.Relay.Managers.ClientManager(actor, clock, holder, dispatcher,
            NullLogger<ConnectX.Relay.Managers.ClientManager>.Instance);
        var settings = new ConnectX.Relay.DefaultServerSettingProvider
        {
            ServerAddress = IPAddress.Loopback, RelayServerAddress = IPAddress.Loopback,
            EndPoint = new(IPAddress.Loopback, 1), RelayEndPoint = new(IPAddress.Loopback, 2)
        };
        using var relay = new RelayManager(actor, clients, holder, dispatcher, settings, NullLogger<RelayManager>.Instance, clock);
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var room = Guid.NewGuid();
        foreach (var user in new[] { a, b })
            dispatcher.Dispatch(main, new ConnectX.Shared.Messages.Relay.UpdateRelayUserRoomMappingMessage
            { UserId = user, RoomId = room, State = ConnectX.Shared.Models.GroupUserStates.Joined });
        var data = new TestSession(1); var worker = new TestSession(2);
        await actor.InvokeAsync(() =>
        {
            relay.CreateDataLink(data, a, room);
            relay.CreateWorkerLink(worker, a, b, room);
            Assert.That(relay.GetRelayServerLoad().CurrentConnectionCount, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(31));
            relay.SweepLinks();
            Assert.That(relay.GetRelayServerLoad().CurrentConnectionCount, Is.Zero);
            Assert.That(Field<IDictionary>(relay, "_workerInputs").Count, Is.Zero);
        });
        Assert.That(worker.Closed, Is.True);
        relay.Dispose();
        Assert.That(typeof(ConnectX.Relay.Managers.ClientManager).GetField("OnSessionDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clients), Is.Null);
        await clients.StopAsync(default);
        await actor.StopAsync(default);
    }

    [Test]
    public async Task ClosingUnpairedBorrowedWorkerReleasesOutstandingReceive()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        var source = new BorrowedSession();
        var input = new RelayWorkerInput(actor, source, TimeProvider.System);
        var receive = input.ReceiveAsync(source, new ReadOnlySequence<byte>(new byte[1024]), default).AsTask();
        Assert.That(receive.IsCompleted, Is.False);
        input.Close();
        await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(receive.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task TransportShutdownReleasesActiveAndRetiredOutboxes()
    {
        using var lifetime = new CancellationTokenSource();
        var hub = new SessionTransportHub(lifetime.Token, NullLogger.Instance);
        for (var i = 0; i < 256; i++)
        {
            var session = new TestSession(i);
            hub.Prepare(session);
            if (i % 2 == 0) hub.Close(session);
        }
        lifetime.Cancel();
        await hub.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(Field<IDictionary>(hub, "_outboxes").Count, Is.Zero);
        Assert.That(Field<HashSet<Task>>(hub, "_retired"), Is.Empty);
    }

    private sealed class BorrowedSession : Hive.Network.Abstractions.Session.IBorrowedBufferSession
    {
        public Hive.Network.Abstractions.Session.SessionReceivedAsyncHandler? ReceiveHandler { get; set; }
        public Hive.Network.Abstractions.SessionId Id => 1;
        public IPEndPoint? LocalEndPoint => new(IPAddress.Loopback, 1);
        public IPEndPoint? RemoteEndPoint => new(IPAddress.Loopback, 2);
        public event Hive.Network.Abstractions.Session.SessionReceivedHandler? OnMessageReceived { add { } remove { } }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public void Close() { }
        public ValueTask SendAsync(Stream stream, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask<bool> TrySendAsync(Stream stream, CancellationToken token = default) => ValueTask.FromResult(true);
        public ValueTask<bool> TrySendAsync(ReadOnlySequence<byte> buffer, CancellationToken token = default) => ValueTask.FromResult(true);
    }

    private sealed class TestHolder(Hive.Network.Abstractions.Session.ISession session) : ConnectX.Relay.Interfaces.IServerLinkHolder
    {
        public Hive.Network.Abstractions.Session.ISession? ServerSession => session;
        public bool IsConnected => true;
        public bool IsSignedIn => true;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }

    private static RoomRecord NewRecord() => new(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, "test", "test", null, null, 2);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var lifetime = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, lifetime.Token);
    }
    private static void AssertDisposed(SaveProbe probe, int count)
    {
        Assert.That(probe.Contexts.Count, Is.EqualTo(count));
        Assert.That(probe.Contexts.Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(count));
        Assert.That(probe.MaxTracked, Is.EqualTo(1));
        foreach (var context in probe.Contexts)
            Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray());
    }
    private static ServiceProvider DatabaseServices(SaveProbe probe, ControlPlaneActor? actor = null)
    {
        var file = Path.Combine(Path.GetTempPath(), $"connectx-memory-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DatabaseFile(file));
        services.AddDbContext<RoomOpsHistoryContext>(o => o.UseSqlite($"Data Source={file};Pooling=False").AddInterceptors(probe));
        if (actor != null)
        {
            services.AddSingleton(actor); services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IDispatcher>(new ActorDispatcher(new TestCodec(), NullLogger<ActorDispatcher>.Instance));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:ListenAddress"] = "127.0.0.1", ["Server:PublicListenAddress"] = "127.0.0.1", ["Server:PublicListenPort"] = "1"
            }).Build());
            services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
            services.AddSingleton<IInterconnectServerSettingProvider, InterconnectServerSettingProvider>();
            services.AddSingleton<ServerClients>(); services.AddSingleton<GroupManager>();
            services.AddSingleton<RelayServerManager>(); services.AddSingleton<RelayLoadManager>();
            services.AddSingleton<InterconnectServerManager>(); services.AddSingleton<P2PManager>();
            services.AddSingleton<RoomCreationRecordService>(); services.AddSingleton<PeerInfoService>();
        }
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<DatabaseFile>();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>().Database.EnsureCreated();
        return provider;
    }
    private sealed class DatabaseFile(string path) : IDisposable
    {
        public void Dispose() => File.Delete(path);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan time) => _timestamp += time.Ticks;
    }
    private sealed class SaveProbe : SaveChangesInterceptor
    {
        public ConcurrentQueue<DbContext> Contexts { get; } = new();
        public int FailuresRemaining;
        public int MaxTracked;
        private int _saves;
        public int Saves => Volatile.Read(ref _saves);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            Contexts.Enqueue(data.Context!);
            MaxTracked = Math.Max(MaxTracked, data.Context!.ChangeTracker.Entries().Count());
            if (FailuresRemaining-- > 0) throw new IOException("Injected write failure");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            Interlocked.Increment(ref _saves);
            return ValueTask.FromResult(result);
        }
    }
}
