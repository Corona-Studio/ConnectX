using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Proxy.FakeServerMultiCasters;
using ConnectX.Client.Relay;
using ConnectX.Client.Transmission;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Client.Helpers;

public static class ClientFactory
{
    /// <summary>
    /// Registers the relay-only ConnectX client. This dependency graph contains no
    /// ZeroTier package, native library, hosted service, or direct-network type.
    /// </summary>
    public static void UseConnectXRelay(
        this IServiceCollection services,
        Func<IServiceProvider, IClientSettingProvider> settingGetter)
    {
        services.AddSingleton(settingGetter);

        services.RegisterConnectXClientRelayPackets();
        services.AddConnectXEssentials();

        services.AddSingleton<ClientPacketDispatcher>();
        services.AddSingleton<IRoomTransportCoordinator, RelayRoomTransportCoordinator>();
        services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
        services.AddSingleton<IRoomInfoManager, RoomInfoManager>();

        services.AddHostedService(sp => sp.GetRequiredService<IServerLinkHolder>());
        services.AddHostedService(sp => sp.GetRequiredService<IRoomInfoManager>());

        services.AddSingleton<PartnerManager>();

        services.AddSingleton<ProxyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<ProxyManager>());

        services.AddHostedService<FakeServerMultiCasterV4>();
        services.AddHostedService<FakeServerMultiCasterV6>();

        services.AddSingleton<Client>();
    }
}
