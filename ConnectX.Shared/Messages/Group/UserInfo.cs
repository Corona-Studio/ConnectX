using System.Net;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class UserInfo : IEquatable<UserInfo>
{
    public required bool JoinP2PNetwork { get; init; }
    public required string DisplayName { get; init; }
    public required Guid UserId { get; init; }
    public string? NetworkNodeId { get; init; }
    public IPAddress[]? NetworkIpAddresses { get; init; }

    [MemoryPackAllowSerialize] public IPEndPoint? RelayServerAddress { get; init; }

    public bool Equals(UserInfo? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return JoinP2PNetwork == other.JoinP2PNetwork &&
               DisplayName == other.DisplayName &&
               UserId.Equals(other.UserId) &&
               (NetworkNodeId?.Equals(other.NetworkNodeId) ?? false) &&
               (NetworkIpAddresses?.SequenceEqual(other.NetworkIpAddresses ?? []) ?? false) &&
               (RelayServerAddress?.Equals(other.RelayServerAddress) ?? false);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;

        return Equals((UserInfo)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(JoinP2PNetwork, DisplayName, UserId, NetworkNodeId, NetworkIpAddresses,
            RelayServerAddress);
    }
}