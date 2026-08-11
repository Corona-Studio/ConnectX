using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public sealed partial class P2PConAccept : P2PConContext
{
    [MemoryPackConstructor]
    private P2PConAccept()
    {
    }

    public P2PConAccept(
        int bargain,
        Guid selfId,
        Guid partnerId,
        P2PConContext context) : base(context)
    {
        SelfId = selfId;
        PartnerId = partnerId;
        Bargain = bargain;
    }

    public P2PConAccept(
        int bargain,
        Guid selfId,
        Guid partnerId,
        P2PConContextInit context) : base(context)
    {
        SelfId = selfId;
        PartnerId = partnerId;
        Bargain = bargain;
    }

    public Guid SelfId { get; init; }
    public Guid PartnerId { get; init; }
    public int Bargain { get; init; }
}
