using System.Buffers;
using ConnectX.Shared.Messages.Relay.Datagram;
using Hive.Codec.Abstractions;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission;

/// <summary>
/// Dispatches decoded client payloads independently of the transport that carried
/// them. Both relay and optional direct transports feed this dispatcher.
/// </summary>
public sealed class ClientPacketDispatcher(
    IPacketCodec codec,
    ILogger<ClientPacketDispatcher> logger) : PacketDispatcherBase<UnwrappedRelayDatagram>(codec, logger)
{
    public void DispatchPacket(UnwrappedRelayDatagram packet)
    {
        OnReceiveDatagram(packet);
    }

    public void DispatchPacket(Guid from, ReadOnlyMemory<byte> payload)
    {
        OnReceiveDatagram(new UnwrappedRelayDatagram(from, payload));
    }

    protected override void OnReceiveDatagram(UnwrappedRelayDatagram packet)
    {
        var sequence = new ReadOnlySequence<byte>(packet.Payload);
        var message = Codec.Decode(sequence);
        if (message == null) return;

        var messageType = message.GetType();
        Logger.LogReceived(messageType.Name, packet.From);
        Dispatch(message, messageType, packet.From);
    }
}
