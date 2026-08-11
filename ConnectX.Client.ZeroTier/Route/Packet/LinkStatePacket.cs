using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Route.Packet;

[MessageDefine]
[MemoryPackable]
public sealed partial class LinkStatePacket
{
    public required Guid Source { get; init; }
    public required Guid[] Interfaces { get; init; }
    public required int[] Costs { get; init; }
    public required long Timestamp { get; init; } = DateTime.UtcNow.Ticks;
    public required byte Ttl { get; set; } = 32;
}