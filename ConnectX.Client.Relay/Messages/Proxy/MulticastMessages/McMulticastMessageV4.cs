using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages.Proxy.MulticastMessages;

[MessageDefine]
[MemoryPackable]
public partial class McMulticastMessageV4
{
    public required ushort Port { get; init; }
    public required string Name { get; init; }
}