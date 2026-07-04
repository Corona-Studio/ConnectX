using System.Net;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class RegisterRelayServerMessage(Guid serverId, IPEndPoint serverAddress)
{
    public Guid ServerId { get; } = serverId;

    [MemoryPackAllowSerialize] public IPEndPoint ServerAddress { get; init; } = serverAddress;
}