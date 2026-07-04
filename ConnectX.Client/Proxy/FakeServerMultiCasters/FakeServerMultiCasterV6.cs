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

public sealed class FakeServerMultiCasterV6(
    PartnerManager partnerManager,
    ProxyManager channelManager,
    RouterPacketDispatcher packetDispatcher,
    RelayPacketDispatcher relayPacketDispatcher,
    IRoomInfoManager roomInfoManager,
    ILogger<FakeServerMultiCasterV6> logger)
    : FakeServerMultiCasterBase<McMulticastMessageV6>(partnerManager, channelManager, packetDispatcher,
        relayPacketDispatcher,
        roomInfoManager, logger)
{
    protected override IPAddress MulticastAddress => IPAddress.Parse("FF75:230::60");
    protected override int MulticastPort => 4445;
    protected override IPEndPoint MulticastPacketReceiveAddress => new(IPAddress.IPv6Any, 0);

    protected override McMulticastMessageV6 CreateMcMulticastMessage(string serverName, ushort port)
    {
        return new McMulticastMessageV6
        {
            Port = port,
            Name = $"[{Prefix}]{serverName}"
        };
    }

    private Socket FallbackToV4()
    {
        Socket? socket = null;

        if (OperatingSystem.IsWindows())
        {
            socket = new Socket(SocketType.Dgram, ProtocolType.Udp);

            var address = IPAddress.Parse("224.0.2.60");
            var multicastOption = new MulticastOption(address, IPAddress.Any);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multicastOption);

            Logger.LogSocketSetupForWindows("IPV4", address.ToString());
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

        return socket!;
    }

    protected override void OnReceiveMcMulticastMessage(McMulticastMessageV6 message, PacketContext context)
    {
        Logger.LogReceivedMulticastMessage(context.SenderId, message.Port, message.Name, Socket.OSSupportsIPv6);

        var proxy = ProxyManager.GetOrCreateAcceptor(context.SenderId, message.Port, true);
        if (proxy == null)
        {
            Logger.LogProxyCreationFailed(context.SenderId);
            return;
        }

        Socket? socket = null;

        if (Socket.OSSupportsIPv6)
        {
            if (OperatingSystem.IsWindows())
            {
                socket = new Socket(SocketType.Dgram, ProtocolType.Udp);

                var multicastOption = new IPv6MulticastOption(MulticastAddress, GetIpv6MulticastInterfaceIndex());
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, multicastOption);

                Logger.LogSocketSetupForWindows("IPV6", MulticastAddress.ToString());
            }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

                var index = GetIpv6MulticastInterfaceIndex();
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 128);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

                Logger.LogSocketSetupForLinuxMacOs("IPV6", $"IPv6 (Interface Index: {index})");
            }
        }
        else
        {
            socket = FallbackToV4();
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

    protected override Socket? CreateMultiCastDiscoverySocket()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Logger.LogMultiCasterDoesNotSupportIpv6();
            return null;
        }

        var multicastSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

        multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        multicastSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));

        var index = GetIpv6MulticastInterfaceIndex();
        var multicastOption = new IPv6MulticastOption(MulticastAddress, index);

        multicastSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, multicastOption);

        return multicastSocket;
    }
}

internal static partial class FakeServerMultiCasterV6Loggers
{
    [LoggerMessage(LogLevel.Warning,
        "Looks like system does not support IPv6 multicast. Stopping IPV6 multi-caster...")]
    public static partial void LogMultiCasterDoesNotSupportIpv6(this ILogger logger);
}