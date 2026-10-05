using ConnectX.Actors;
using ConnectX.Server;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Managers;
using ConnectX.Server.Models.ZeroTier;
using ConnectX.Server.Services;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class GroupStateTests
{
    [Test]
    public async Task DisconnectedOwnerCannotCommitLateNetworkCreationAndOrphanIsDeleted()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var codec = new TestCodec();
        var dispatcher = new ActorDispatcher(codec, NullLogger<ActorDispatcher>.Instance);
        var api = new DelayedApi();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton(actor); services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDispatcher>(dispatcher);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:ListenAddress"] = "127.0.0.1", ["Server:PublicListenAddress"] = "127.0.0.1", ["Server:PublicListenPort"] = "1"
        }).Build());
        services.AddSingleton<IServerSettingProvider, ConfigSettingProvider>();
        services.AddSingleton<IInterconnectServerSettingProvider, InterconnectServerSettingProvider>();
        services.AddSingleton<ClientManager>(); services.AddSingleton<RelayServerManager>();
        services.AddSingleton<RelayLoadManager>(); services.AddSingleton<InterconnectServerManager>();
        services.AddSingleton<RoomCreationRecordService>(); services.AddSingleton<GroupManager>();
        services.AddSingleton<IZeroTierApiService>(api); services.AddSingleton<IZeroTierNodeInfoService>(new NodeInfo());
        using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<ClientManager>(); var groups = provider.GetRequiredService<GroupManager>();
        var session = new TestSession(1);
        await actor.InvokeAsync(() =>
        {
            clients.AttachSession(session.Id, session);
            groups.AttachSession(session.Id, session, new SigninMessage { DisplayName = "owner", JoinP2PNetwork = true, LinkProtocolMajor = ConnectX.Shared.LinkProtocolConstants.ProtocolMajor, LinkProtocolMinor = 2 });
        });
        dispatcher.Dispatch(session, new CreateGroup { RoomName = "pending", MaxUserCount = 2, UseRelayServer = false });
        await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await actor.InvokeAsync(() => clients.DetachSession(session.Id));
        api.Created.SetResult(new NetworkDetailsModel { Id = "abcdef1234567890", NetworkId = "abcdef1234567890" });
        Assert.That(await api.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false), Is.EqualTo("abcdef1234567890"));
        Assert.That(codec.Encoded.OfType<GroupOpResult>().Any(x => x.Status == GroupCreationStatus.Succeeded), Is.False);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.StopAsync(timeout.Token);
    }

    private sealed class NodeInfo : IZeroTierNodeInfoService
    {
        public NodeStatusModel? NodeStatus { get; } = new()
        {
            Address = "abcdef1234", Clock = 0, Config = null!, Online = true, PlanetWorldId = 0,
            PlanetWorldTimestamp = 0, PublicIdentity = "", TcpFallbackActive = false, Version = "test",
            VersionBuild = 0, VersionMajor = 1, VersionMinor = 0, VersionRev = 0
        };
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class DelayedApi : IZeroTierApiService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<NetworkDetailsModel?> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NetworkDetailsModel?> CreateOrUpdateNetwork(string id, NetworkDetailsReqModel details, CancellationToken token)
        { Entered.TrySetResult(); return await Created.Task.WaitAsync(token); }
        public Task<NetworkDetailsModel?> DeleteNetworkAsync(string id, CancellationToken token)
        { Deleted.TrySetResult(id); return Task.FromResult<NetworkDetailsModel?>(null); }
        public Task<NetworkDetailsModel?> DeleteNetworkMemberAsync(string id, string node, CancellationToken token) => throw new NotSupportedException();
        public Task<NetworkDetailsModel?> GetNetworkDetailsAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeStatusModel?> GetNodeStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<string[]> ListNetworkIds(CancellationToken token) => throw new NotSupportedException();
        public Task<NetworkPeerModel[]?> GetNetworkPeersAsync(CancellationToken token) => throw new NotSupportedException();
    }
}
