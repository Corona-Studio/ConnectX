using System.Net;
using System.Net.Sockets;
using System.Text;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Messages.Proxy.MulticastMessages;
using ConnectX.Client.Models;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy.FakeServerMultiCasters;

public sealed class FakeServerMultiCasterV4(
    PartnerManager partnerManager,
    ProxyManager channelManager,
    RouterPacketDispatcher packetDispatcher,
    RelayPacketDispatcher relayPacketDispatcher,
    IRoomInfoManager roomInfoManager,
    ILogger<FakeServerMultiCasterV4> logger)
    : FakeServerMultiCasterBase<McMulticastMessageV4>(
        partnerManager, channelManager,
        packetDispatcher, relayPacketDispatcher,
        roomInfoManager, logger)
{
    protected override IPAddress MulticastAddress => IPAddress.Parse("224.0.2.60");
    protected override int MulticastPort => 4445;
    protected override IPEndPoint MulticastPacketReceiveAddress => new(IPAddress.Any, 0);

    protected override void OnReceiveMcMulticastMessage(McMulticastMessageV4 message, PacketContext context)
    {
        Logger.LogReceivedMulticastMessage(context.SenderId, message.Port, message.Name, false);

        var proxy = ProxyManager.GetOrCreateAcceptor(context.SenderId, message.Port, false);
        if (proxy == null)
        {
            Logger.LogProxyCreationFailed(context.SenderId);
            return;
        }

        Socket? socket = null;

        if (OperatingSystem.IsWindows())
        {
            socket = new Socket(SocketType.Dgram, ProtocolType.Udp);

            var multicastOption = new MulticastOption(MulticastAddress, IPAddress.Any);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

            Logger.LogSocketSetupForWindows("IPV4", MulticastAddress.ToString());
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            var localIp = GetLocalIpAddress();

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                localIp.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.Bind(new IPEndPoint(localIp, 0));

            Logger.LogSocketSetupForLinuxMacOs("IPV4", localIp.ToString());
        }

        ArgumentNullException.ThrowIfNull(socket);

        using var multicastSocket = socket;

        var mess = $"[MOTD]{message.Name}[/MOTD][AD]{proxy.LocalMappingPort}[/AD]";
        var buf = Encoding.Default.GetBytes(mess).AsSpan();
        var sentLen = 0;

        while (sentLen < buf.Length)
            sentLen += multicastSocket.SendTo(buf[sentLen..], MulticastIpe);

        Logger.LogMulticastMessageToClient(proxy.LocalMappingPort, message.Name);
    }

    protected override McMulticastMessageV4 CreateMcMulticastMessage(string serverName, ushort port)
    {
        return new McMulticastMessageV4
        {
            Port = port,
            Name = $"[{Prefix}]{serverName}"
        };
    }

    protected override Socket CreateMultiCastDiscoverySocket()
    {
        var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        multicastSocket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

        var multicastOption = new MulticastOption(MulticastAddress, IPAddress.Any);
        multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

        return multicastSocket;
    }
}