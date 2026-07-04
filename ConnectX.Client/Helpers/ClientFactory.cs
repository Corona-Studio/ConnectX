using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Proxy.FakeServerMultiCasters;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using ConnectX.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectX.Client.Helpers;

public static class ClientFactory
{
    public static void UseConnectX(
        this IServiceCollection services,
        Func<IServiceProvider, IClientSettingProvider> settingGetter)
    {
        services.AddSingleton(settingGetter);

        services.RegisterConnectXClientPackets();
        services.AddConnectXEssentials();

        services.AddSingleton<RelayPacketDispatcher>();

        // Router
        services.AddSingleton<RouterPacketDispatcher>();
        services.AddSingleton<RouteTable>();
        services.AddSingleton<Router>();
        services.AddHostedService(sp => sp.GetRequiredService<Router>());

        services.AddSingleton<IZeroTierNodeLinkHolder, ZeroTierNodeLinkHolder>();
        services.AddSingleton<IServerLinkHolder, ServerLinkHolder>();
        services.AddSingleton<IRoomInfoManager, RoomInfoManager>();

        services.AddHostedService(sp => sp.GetRequiredService<IZeroTierNodeLinkHolder>());
        services.AddHostedService(sp => sp.GetRequiredService<IServerLinkHolder>());
        services.AddHostedService(sp => sp.GetRequiredService<IRoomInfoManager>());

        services.AddSingleton<PeerManager>();
        services.AddHostedService(sp => sp.GetRequiredService<PeerManager>());

        services.AddSingleton<ProxyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<ProxyManager>());

        services.AddHostedService<FakeServerMultiCasterV4>();
        services.AddHostedService<FakeServerMultiCasterV6>();

        services.AddSingleton<PartnerManager>();

        services.AddSingleton<Client>();
    }
}