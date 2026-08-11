using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages.Proxy;

[MessageDefine]
[MemoryPackable]
public partial class ProxyConnectReq
{
    public required Guid ClientId { get; init; }
    public required bool IsResponse { get; init; }
    public required ushort ClientRealPort { get; init; }
    public required ushort ServerRealPort { get; init; }
    public required bool IsIpv6 { get; init; }
}