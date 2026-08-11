using ConnectX.Client.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Route.Packet;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PTransmitErrorPacket : RouteLayerPacket
{
    public required P2PTransmitError Error { get; init; }
    public Guid OriginalTo { get; init; }

    [BrotliFormatter<ReadOnlyMemory<byte>>]
    public ReadOnlyMemory<byte> Payload { get; init; }
}