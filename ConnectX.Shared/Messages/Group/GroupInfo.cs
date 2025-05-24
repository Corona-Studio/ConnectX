using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial record GroupInfo
{
    public static readonly GroupInfo Invalid = new()
    {
        RoomId = Guid.Empty,
        RoomOwnerId = Guid.Empty,
        RoomShortId = string.Empty,
        IsPrivate = false,
        RoomName = string.Empty,
        RoomDescription = string.Empty,
        CurrentUserCount = 0,
        MaxUserCount = 0,
        Users = [],
        RoomNetworkId = 0,
        UseRelayServer = false
    };

    public required Guid RoomId { get; init; }
    public required Guid RoomOwnerId { get; init; }
    public required ulong RoomNetworkId { get; init; }
    public required string RoomShortId { get; init; }
    public required bool IsPrivate { get; init; }
    public required string RoomName { get; init; }
    public required string? RoomDescription { get; init; }
    public required int CurrentUserCount { get; init; }
    public required int MaxUserCount { get; init; }
    public required UserInfo[] Users { get; init; }
    public required bool UseRelayServer { get; init; }
}