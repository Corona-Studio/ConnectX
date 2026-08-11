using ConnectX.Shared.Messages.Group;
using Microsoft.Extensions.Hosting;

namespace ConnectX.Client.Interfaces;

public interface IRoomInfoManager : IHostedService
{
    GroupInfo? CurrentGroupInfo { get; }

    void ClearRoomInfo();

    void UpdateRoomMemberInfo(UserInfo userInfo);

    Task<GroupInfo?> AcquireGroupInfoAsync(Guid groupId);

    event Action<UserInfo[]> OnMemberAddressInfoUpdated;
    event Action<GroupInfo> OnGroupInfoUpdated;
}