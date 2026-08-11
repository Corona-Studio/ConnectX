using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Client.Interfaces;

/// <summary>
/// Coordinates the room transport without coupling the relay client to an optional
/// direct-network implementation.
/// </summary>
public interface IRoomTransportCoordinator
{
    bool SupportsDirectConnections { get; }
    bool IsAvailable { get; }

    bool IsMemberReady(UserInfo userInfo);
    Task<string?> PrepareGroupAsync(
        GroupInfo groupInfo,
        IServerLinkHolder serverLinkHolder,
        IDispatcher dispatcher,
        CancellationToken cancellationToken);
    Task LeaveGroupAsync(CancellationToken cancellationToken);
}
