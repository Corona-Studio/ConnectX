using System.Buffers;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages.Proxy;

[MessageDefine]
[MemoryPackable]
public partial class ForwardPacketCarrier : IDisposable
{
    public ushort TargetRealPort { get; set; }
    public ushort SelfRealPort { get; set; }

    [MemoryPackIgnore] public IMemoryOwner<byte>? PayloadOwner { get; set; }

    [BrotliFormatter<ReadOnlyMemory<byte>>]
    public ReadOnlyMemory<byte> Payload { get; set; }

    [MemoryPackIgnore] public int TryCount { get; set; }
    [MemoryPackIgnore] public int LastTryTime { get; set; }

    public void Dispose()
    {
        PayloadOwner?.Dispose();
        PayloadOwner = null;
        Payload = ReadOnlyMemory<byte>.Empty;

        GC.SuppressFinalize(this);
    }
}