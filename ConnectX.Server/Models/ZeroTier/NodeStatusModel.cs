using System.Text.Json.Serialization;

namespace ConnectX.Server.Models.ZeroTier;

public class Settings
{
    [JsonPropertyName("allowTcpFallbackRelay")]
    public required bool AllowTcpFallbackRelay { get; init; }

    [JsonPropertyName("forceTcpRelay")] public required bool ForceTcpRelay { get; init; }

    [JsonPropertyName("homeDir")] public required string HomeDir { get; init; }

    [JsonPropertyName("listeningOn")] public required string[] ListeningOn { get; init; }

    [JsonPropertyName("portMappingEnabled")]
    public required bool PortMappingEnabled { get; init; }

    [JsonPropertyName("primaryPort")] public required ushort PrimaryPort { get; init; }

    [JsonPropertyName("secondaryPort")] public required ushort SecondaryPort { get; init; }

    [JsonPropertyName("tertiaryPort")] public required ushort TertiaryPort { get; init; }

    [JsonPropertyName("softwareUpdate")] public string? SoftwareUpdate { get; init; }

    [JsonPropertyName("softwareUpdateChannel")]
    public string? SoftwareUpdateChannel { get; init; }

    [JsonPropertyName("surfaceAddresses")] public required string[] SurfaceAddresses { get; init; }
}

public class Config
{
    [JsonPropertyName("settings")] public required Settings Settings { get; init; }
}

public class NodeStatusModel
{
    [JsonPropertyName("address")] public required string Address { get; init; }

    [JsonPropertyName("clock")] public required ulong Clock { get; init; }

    [JsonPropertyName("config")] public required Config Config { get; init; }

    [JsonPropertyName("online")] public required bool Online { get; init; }

    [JsonPropertyName("planetWorldId")] public required ulong PlanetWorldId { get; init; }

    [JsonPropertyName("planetWorldTimestamp")]
    public required ulong PlanetWorldTimestamp { get; init; }

    [JsonPropertyName("publicIdentity")] public required string PublicIdentity { get; init; }

    [JsonPropertyName("tcpFallbackActive")]
    public required bool TcpFallbackActive { get; init; }

    [JsonPropertyName("version")] public required string Version { get; init; }

    [JsonPropertyName("versionBuild")] public required uint VersionBuild { get; init; }

    [JsonPropertyName("versionMajor")] public required uint VersionMajor { get; init; }

    [JsonPropertyName("versionMinor")] public required uint VersionMinor { get; init; }

    [JsonPropertyName("versionRev")] public required uint VersionRev { get; init; }
}