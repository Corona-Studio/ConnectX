namespace ConnectX.Client.Models;

public readonly struct PacketContext(Guid senderId)
{
    public Guid SenderId { get; } = senderId;
}