using ConnectX.Client.Interfaces;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Client;

/// <summary>
/// Relay-only transport policy. It deliberately has no reference to ZeroTier or
/// any other direct-network runtime.
/// </summary>
public sealed class RelayRoomTransportCoordinator : IRoomTransportCoordinator
{
    public bool SupportsDirectConnections => false;
    public bool IsAvailable => true;

    public bool IsMemberReady(UserInfo userInfo)
    {
        return userInfo.RelayServerAddress != null;
    }

    public Task<string?> PrepareGroupAsync(
        GroupInfo groupInfo,
        IServerLinkHolder serverLinkHolder,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(groupInfo.UseRelayServer
            ? null
            : "This client supports relay rooms only");
    }

    public Task LeaveGroupAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
