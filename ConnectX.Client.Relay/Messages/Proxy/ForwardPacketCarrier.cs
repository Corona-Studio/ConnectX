using System.Buffers;
using Hive.Common.Shared.Pooling;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages.Proxy;

[MessageDefine]
[MemoryPackable]
public partial class ForwardPacketCarrier : IDisposable
{
    private static readonly BoundedObjectPool<ForwardPacketCarrier> Pool = new(() => new ForwardPacketCarrier { _pooled = true });
    private bool _pooled;
    private int _disposed;

    public static ForwardPacketCarrier Rent()
    {
        var carrier = Pool.Rent();
        carrier._disposed = 0;
        return carrier;
    }

    /// <summary>Give each queued consumer its own carrier and payload reference.</summary>
    public ForwardPacketCarrier Retain()
    {
        var retained = Rent();
        try
        {
            retained.TargetRealPort = TargetRealPort;
            retained.SelfRealPort = SelfRealPort;
            retained.TryCount = TryCount;
            retained.LastTryTime = LastTryTime;
            if (PayloadOwner is PooledBufferStream buffer)
            {
                retained.PayloadOwner = buffer.Retain();
                retained.Payload = Payload;
            }
            else if (PayloadOwner == null)
                retained.Payload = Payload; // MemoryPack arrays already own their storage.
            else
            {
                // Preserve correctness for external, non-shareable memory owners.
                var copy = PooledBufferStream.Rent(Payload.Length);
                retained.PayloadOwner = copy;
                copy.Write(Payload.Span);
                retained.Payload = copy.Memory;
            }
            return retained;
        }
        catch
        {
            retained.Dispose();
            throw;
        }
    }

    public ushort TargetRealPort { get; set; }
    public ushort SelfRealPort { get; set; }

    [MemoryPackIgnore] public IMemoryOwner<byte>? PayloadOwner { get; set; }

    [BrotliFormatter<ReadOnlyMemory<byte>>]
    public ReadOnlyMemory<byte> Payload { get; set; }

    [MemoryPackIgnore] public int TryCount { get; set; }
    [MemoryPackIgnore] public int LastTryTime { get; set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        PayloadOwner?.Dispose();
        PayloadOwner = null;
        Payload = ReadOnlyMemory<byte>.Empty;
        TargetRealPort = SelfRealPort = 0;
        TryCount = LastTryTime = 0;
        if (_pooled) Pool.TryReturn(this);
    }
}