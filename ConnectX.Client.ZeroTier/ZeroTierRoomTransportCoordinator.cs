using System.Net.Sockets;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public sealed class ZeroTierRoomTransportCoordinator(
    IZeroTierNodeLinkHolder zeroTierNodeLinkHolder,
    ILogger<ZeroTierRoomTransportCoordinator> logger) : IRoomTransportCoordinator
{
    public bool SupportsDirectConnections => true;
    public bool IsAvailable => zeroTierNodeLinkHolder.IsZeroTierInitialized;

    public bool IsMemberReady(UserInfo userInfo)
    {
        if (userInfo.RelayServerAddress != null) return true;

        return userInfo.NetworkIpAddresses?.Any(address =>
            address.AddressFamily == AddressFamily.InterNetwork &&
            address.GetAddressBytes()[3] != 0) == true;
    }

    public async Task<string?> PrepareGroupAsync(
        GroupInfo groupInfo,
        IServerLinkHolder serverLinkHolder,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (groupInfo.UseRelayServer) return null;
        if (!IsAvailable) return "ZeroTier is not available";

        logger.LogJoiningNetwork(groupInfo.RoomNetworkId);
        if (!await zeroTierNodeLinkHolder.JoinNetworkAsync(groupInfo.RoomNetworkId, cancellationToken))
            return "Failed to join the ZeroTier network";

        await TaskHelper.WaitUntilAsync(zeroTierNodeLinkHolder.IsNodeOnline, cancellationToken);
        await TaskHelper.WaitUntilAsync(zeroTierNodeLinkHolder.IsNetworkReady, cancellationToken);

        var updateInfo = new UpdateRoomMemberNetworkInfo
        {
            NetworkNodeId = zeroTierNodeLinkHolder.Node!.IdString,
            NetworkIpAddresses = zeroTierNodeLinkHolder.GetIpAddresses()
        };

        var result = await dispatcher.SendAndListenOnce<UpdateRoomMemberNetworkInfo, GroupOpResult>(
            serverLinkHolder.ServerSession!, updateInfo, cancellationToken);

        return result is { Status: GroupCreationStatus.Succeeded }
            ? null
            : result?.ErrorMessage ?? "Failed to update room member network info";
    }

    public Task LeaveGroupAsync(CancellationToken cancellationToken)
    {
        return zeroTierNodeLinkHolder.LeaveNetworkAsync(cancellationToken);
    }
}

internal static partial class ZeroTierRoomTransportCoordinatorLoggers
{
    [LoggerMessage(LogLevel.Information,
        "[ZEROTIER_TRANSPORT] Joining room network [0x{roomId:X}]")]
    public static partial void LogJoiningNetwork(this ILogger logger, ulong roomId);
}
