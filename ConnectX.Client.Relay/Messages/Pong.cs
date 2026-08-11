using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages;

[MessageDefine]
[MemoryPackable]
public sealed partial class Pong
{
    public required Guid From { get; init; }
    public required Guid To { get; init; }
    public required byte Ttl { get; set; }
    public required uint SeqId { get; init; }

    [MemoryPackIgnore] public long SelfReceiveTime { get; set; }
}
