using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using System.Net;

namespace ConnectX.Server.Models;

public class Group(
    string roomName,
    string? roomPassword,
    UserSessionInfo roomOwner,
    List<UserSessionInfo> users)
{
    public Guid RoomId { get; } = Guid.CreateVersion7();
    public IPEndPoint? AssignedRelayServer { get; set; }
    public UserSessionInfo RoomOwner { get; } = roomOwner;
    public string RoomShortId { get; } = RandomHelper.GetRandomString();
    public required ulong NetworkId { get; set; }
    public bool IsPrivate { get; set; }
    public string RoomName { get; set; } = roomName;
    public string? RoomDescription { get; set; }
    public string? RoomPassword { get; } = roomPassword;
    public required int MaxUserCount { get; init; }
    public List<UserSessionInfo> Users { get; } = users;

    public static implicit operator GroupInfo(Group group)
    {
        return new GroupInfo
        {
            IsPrivate = group.IsPrivate,
            CurrentUserCount = group.Users.Count,
            MaxUserCount = group.MaxUserCount,
            RoomDescription = group.RoomDescription,
            RoomId = group.RoomId,
            RoomName = group.RoomName,
            RoomOwnerId = group.RoomOwner.UserId,
            RoomShortId = group.RoomShortId,
            RoomNetworkId = group.NetworkId,
            Users = group.Users.Select(x => (UserInfo)x).ToArray(),
            UseRelayServer = group.AssignedRelayServer != null
        };
    }
}