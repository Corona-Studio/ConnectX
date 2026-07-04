using System.Net;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class UpdateRoomMemberNetworkInfo
{
    public required string NetworkNodeId { get; init; }

    public required IPAddress[] NetworkIpAddresses { get; init; }
}