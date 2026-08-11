namespace ConnectX.Client.Models;

public class PacketContext(Guid senderId)
{
    public Guid SenderId { get; } = senderId;
}