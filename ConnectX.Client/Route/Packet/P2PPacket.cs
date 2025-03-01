﻿using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Route.Packet;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PPacket : RouteLayerPacket
{
    [BrotliFormatter]
    public required byte[] Payload { get; init; }
}