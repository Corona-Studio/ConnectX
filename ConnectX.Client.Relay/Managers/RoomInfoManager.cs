using ConnectX.Client.Interfaces;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

public class RoomInfoManager(
    IDispatcher dispatcher,
    IServerLinkHolder serverLinkHolder,
    IRoomTransportCoordinator roomTransportCoordinator,
    ILogger<RoomInfoManager> logger) : BackgroundService, IRoomInfoManager
{
    private readonly HashSet<Guid> _possiblePeers = [];

    public GroupInfo? CurrentGroupInfo { get; private set; }

    public void ClearRoomInfo()
    {
        _possiblePeers.Clear();
        CurrentGroupInfo = null;
    }

    public void UpdateRoomMemberInfo(UserInfo userInfo)
    {
        if (CurrentGroupInfo == null) return;

        for (var i = 0; i < CurrentGroupInfo.Users.Length; i++)
        {
            if (CurrentGroupInfo.Users[i].UserId != userInfo.UserId) continue;
            CurrentGroupInfo.Users[i] = userInfo;
            return;
        }
    }

    public async Task<GroupInfo?> AcquireGroupInfoAsync(Guid groupId)
    {
        if (!serverLinkHolder.IsConnected) return null;
        if (!serverLinkHolder.IsSignedIn) return null;

        var message = new AcquireGroupInfo();
        var groupInfo = await dispatcher.SendAndListenOnce<AcquireGroupInfo, GroupInfo>(
            serverLinkHolder.ServerSession!, message);

        if (groupInfo == null || groupInfo == GroupInfo.Invalid || groupInfo.Users.Length == 0)
        {
            logger.LogFailedToAcquireGroupInfo(groupId);
            return null;
        }

        CurrentGroupInfo = groupInfo;
        OnGroupInfoUpdated?.Invoke(groupInfo);
        logger.LogRoomInfoAcquired();

        return groupInfo;
    }

    public event Action<UserInfo[]>? OnMemberAddressInfoUpdated;
    public event Action<GroupInfo>? OnGroupInfoUpdated;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (CurrentGroupInfo == null)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            var self = CurrentGroupInfo.Users.FirstOrDefault(x => x.UserId == serverLinkHolder.UserId);
            if (self == null)
            {
                logger.LogSelfNotFound();
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            var possibleUsers = new List<UserInfo>();
            foreach (var user in CurrentGroupInfo.Users)
            {
                if (user.UserId == serverLinkHolder.UserId) continue;
                if (_possiblePeers.Contains(user.UserId)) continue;
                if (!roomTransportCoordinator.IsMemberReady(user)) continue;

                _possiblePeers.Add(user.UserId);
                possibleUsers.Add(user);
            }

            if (possibleUsers.Count > 0)
                OnMemberAddressInfoUpdated?.Invoke([.. possibleUsers]);

            await Task.Delay(1000, stoppingToken);
        }
    }
}

internal static partial class RoomInfoManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "[ROOM_INFO_MANAGER] Room info acquired.")]
    public static partial void LogRoomInfoAcquired(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[ROOM_INFO_MANAGER] Can not find self in the room info, possible internal error!")]
    public static partial void LogSelfNotFound(this ILogger logger);
}
