using System.Net;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

public record P2PConContextInit
{
    public required IPAddress PublicAddress { get; init; }
    public int Diff { get; init; }
    public bool SameIfNotConflict { get; init; }
    public ushort PublicPort { get; init; }
    public ushort CurrentUsedPort { get; init; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(P2PConAccept))]
[MemoryPackUnion(1, typeof(P2PConReady))]
[MemoryPackUnion(2, typeof(P2PConRequest))]
public abstract partial class P2PConContext
{
    [MemoryPackConstructor]
    public P2PConContext()
    {
    }

    protected P2PConContext(P2PConContext context)
    {
        PublicPort = context.PublicPort;
        Diff = context.Diff;
        SameIfNotConflict = context.SameIfNotConflict;
        CurrentUsedPort = context.CurrentUsedPort;
        PublicAddress = context.PublicAddress;
    }

    protected P2PConContext(P2PConContextInit context)
    {
        PublicPort = context.PublicPort;
        Diff = context.Diff;
        SameIfNotConflict = context.SameIfNotConflict;
        CurrentUsedPort = context.CurrentUsedPort;
        PublicAddress = context.PublicAddress;
    }

    [MemoryPackAllowSerialize] public IPAddress PublicAddress { get; init; }

    public int Diff { get; init; }
    public bool SameIfNotConflict { get; init; }

    public ushort PublicPort { get; init; }
    public ushort CurrentUsedPort { get; init; }
}