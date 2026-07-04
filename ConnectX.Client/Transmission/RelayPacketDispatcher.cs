using System.Buffers;
using ConnectX.Shared.Messages.Relay.Datagram;
using Hive.Codec.Abstractions;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission;

public sealed class RelayPacketDispatcher(
    IPacketCodec codec,
    ILogger<RelayPacketDispatcher> logger) : PacketDispatcherBase<UnwrappedRelayDatagram>(codec, logger)
{
    public void DispatchPacket(UnwrappedRelayDatagram packet)
    {
        OnReceiveDatagram(packet);
    }

    protected override void OnReceiveDatagram(UnwrappedRelayDatagram packet)
    {
        var sequence = new ReadOnlySequence<byte>(packet.Payload);
        var message = Codec.Decode(sequence);
        var messageType = message!.GetType();

        Logger.LogReceived(messageType.Name, packet.From);

        Dispatch(message, messageType, packet.From);
    }
}