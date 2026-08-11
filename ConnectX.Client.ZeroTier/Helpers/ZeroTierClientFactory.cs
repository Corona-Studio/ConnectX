using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Route;
using ConnectX.Client.ZeroTier;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConnectX.Client.Helpers;

public static class ZeroTierClientFactory
{
    /// <summary>
    /// Registers the full direct-connect client. The ZeroTier dependency remains
    /// isolated in this optional project.
    /// </summary>
    public static void UseConnectXZeroTier(
        this IServiceCollection services,
        Func<IServiceProvider, IClientSettingProvider> settingGetter)
    {
        services.UseConnectXRelay(settingGetter);
        services.RegisterConnectXClientZeroTierPackets();

        services.Replace(ServiceDescriptor.Singleton<IRoomTransportCoordinator,
            ZeroTierRoomTransportCoordinator>());

        services.AddSingleton<IZeroTierNodeLinkHolder, ZeroTierNodeLinkHolder>();
        services.AddHostedService(sp => sp.GetRequiredService<IZeroTierNodeLinkHolder>());

        services.AddSingleton<RouterPacketDispatcher>();
        services.AddSingleton<RouteTable>();
        services.AddSingleton<Router>();

        services.AddSingleton<PeerManager>();
        services.AddHostedService(sp => sp.GetRequiredService<PeerManager>());
        services.AddHostedService(sp => sp.GetRequiredService<Router>());

        services.Replace(ServiceDescriptor.Singleton<PartnerManager, ZeroTierPartnerManager>());
    }
}
