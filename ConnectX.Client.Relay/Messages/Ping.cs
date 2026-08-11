using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages;

[MessageDefine]
[MemoryPackable]
public sealed partial class Ping
{
    public required Guid From { get; init; }
    public required Guid To { get; init; }
    public required byte Ttl { get; set; }
    public required long SendTime { get; init; }
    public required uint SeqId { get; init; }
}
