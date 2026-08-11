using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.P2P;

[MessageDefine]
[MemoryPackable]
public partial class P2POpResult(bool isSucceeded, string? errorMessage = null)
{
    public bool IsSucceeded { get; init; } = isSucceeded;
    public string? ErrorMessage { get; init; } = errorMessage;
    public int Bargain { get; init; }
    public Guid PartnerId { get; init; }
    public P2PConContext? Context { get; init; }
}
