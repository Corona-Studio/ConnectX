using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Relay;

[MessageDefine]
[MemoryPackable]
public partial class RelayServerLoadInfoMessage
{
    public required int CurrentConnectionCount { get; init; }

    public required int MaxReferenceConnectionCount { get; init; }

    public required uint Priority { get; init; }
}