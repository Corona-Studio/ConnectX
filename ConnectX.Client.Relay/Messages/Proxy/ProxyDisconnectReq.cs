using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages.Proxy;

[MessageDefine]
[MemoryPackable]
public partial class ProxyDisconnectReq
{
    public required Guid ClientId { get; init; }
    public required ushort ClientRealPort { get; init; }
    public required ushort ServerRealPort { get; init; }
}