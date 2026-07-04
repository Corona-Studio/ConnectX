using System.Collections.ObjectModel;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Identity;

[MessageDefine]
[MemoryPackable]
public partial class SigninResult(
    bool succeeded,
    Guid userId,
    string? errorMessage = null,
    IReadOnlyDictionary<string, string>? metadata = null)
{
    public const string ErrorProtocolMismatch = "Protocol mismatch";

    public const string MetadataServerProtocolMajor = "ServerProtocolMajor";
    public const string MetadataServerProtocolMinor = "ServerProtocolMinor";

    public bool Succeeded { get; } = succeeded;
    public Guid UserId { get; } = userId;
    public string? ErrorMessage { get; } = errorMessage;
    public IReadOnlyDictionary<string, string> Metadata { get; } = metadata ?? ReadOnlyDictionary<string, string>.Empty;
}