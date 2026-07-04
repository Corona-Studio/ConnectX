using System.Collections.ObjectModel;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class GroupOpResult(
    GroupCreationStatus status,
    string? errorMessage = null,
    IReadOnlyDictionary<string, string>? metadata = null)
{
    public const string MetadataRedirectInfo = "RedirectInfo";
    public const string MetadataUseRelayServer = "UseRelayServer";

    public string? ErrorMessage { get; } = errorMessage;
    public Guid RoomId { get; init; }
    public GroupCreationStatus Status { get; } = status;
    public IReadOnlyDictionary<string, string> Metadata { get; } = metadata ?? ReadOnlyDictionary<string, string>.Empty;
}