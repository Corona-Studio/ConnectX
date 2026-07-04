using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages;

[MessageDefine]
[MemoryPackable]
public partial class ChatMessage
{
    [BrotliStringFormatter] public required string Message { get; init; }
}