using System.Net;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class RelayServerAddressAssignedMessage(Guid userId, IPEndPoint serverAddress)
{
    public Guid UserId { get; } = userId;

    [MemoryPackAllowSerialize] public IPEndPoint ServerAddress { get; } = serverAddress;
}