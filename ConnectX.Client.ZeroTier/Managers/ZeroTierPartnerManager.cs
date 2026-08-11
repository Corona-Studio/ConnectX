using ConnectX.Client.Transmission.Connections;
using ConnectX.Client.Route;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

public sealed class ZeroTierPartnerManager : PartnerManager
{
    private readonly PeerManager _peerManager;
    private readonly Router _router;
    private readonly ILogger _logger;

    public ZeroTierPartnerManager(
        PeerManager peerManager,
        Router router,
        IDispatcher dispatcher,
        Interfaces.IRoomInfoManager roomInfoManager,
        Interfaces.IServerLinkHolder serverLinkHolder,
        IServiceProvider serviceProvider,
        ILogger<PartnerManager> baseLogger,
        ILogger<ZeroTierPartnerManager> logger)
        : base(dispatcher, roomInfoManager, serverLinkHolder, serviceProvider, baseLogger)
    {
        _peerManager = peerManager;
        _router = router;
        _logger = logger;
    }

    protected override bool AddDirectPartner(Guid partnerId)
    {
        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(ServiceProvider);
        var connection = ActivatorUtilities.CreateInstance<P2PConnection>(
            ServiceProvider, partnerId, dispatcher);

        if (!TryAddConnection(partnerId, connection)) return false;

        _peerManager.AddLink(partnerId);
        _logger.LogDirectPartnerAdded(partnerId);
        return true;
    }

    public override void RemoveAllPartners()
    {
        _peerManager.RemoveAllPeer();
        _router.RemoveAllPeers();
        base.RemoveAllPartners();
    }
}

internal static partial class ZeroTierPartnerManagerLoggers
{
    [LoggerMessage(LogLevel.Information,
        "[ZEROTIER_PARTNER_MANAGER] Direct partner added with user ID [{userId}]")]
    public static partial void LogDirectPartnerAdded(this ILogger logger, Guid userId);
}
