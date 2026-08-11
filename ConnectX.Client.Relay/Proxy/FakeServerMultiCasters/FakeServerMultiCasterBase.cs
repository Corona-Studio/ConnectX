using System.Collections.Frozen;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Models;
using ConnectX.Client.Transmission;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy.FakeServerMultiCasters;

public abstract class FakeServerMultiCasterBase<TMultiCastPacket> : BackgroundService
{
    protected const string Prefix = "ConnectX";

    private static readonly FrozenSet<string> VirtualKeywords = FrozenSet.Create(
        "virtual", "vmware", "loopback",
        "pseudo", "tunneling", "tap",
        "container", "hyper-v", "bluetooth",
        "docker");

    private readonly PartnerManager _partnerManager;
    private readonly IRoomInfoManager _roomInfoManager;
    protected readonly ILogger Logger;

    protected readonly ProxyManager ProxyManager;

    protected FakeServerMultiCasterBase(
        PartnerManager partnerManager,
        ProxyManager channelManager,
        ClientPacketDispatcher clientPacketDispatcher,
        IRoomInfoManager roomInfoManager,
        ILogger logger)
    {
        _partnerManager = partnerManager;
        ProxyManager = channelManager;
        _roomInfoManager = roomInfoManager;
        Logger = logger;

        clientPacketDispatcher.OnReceive<TMultiCastPacket>(OnReceiveMcMulticastMessage);
    }

    protected abstract IPAddress MulticastAddress { get; }
    protected abstract int MulticastPort { get; }
    protected abstract IPEndPoint MulticastPacketReceiveAddress { get; }

    protected IPEndPoint MulticastIpe => new(MulticastAddress, MulticastPort);

    public event Action<string, int>? OnListenedLanServer;

    protected static IPAddress GetLocalIpAddress()
    {
        var candidates = new List<IPAddress>();

        var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.OperationalStatus == OperationalStatus.Up &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                !VirtualKeywords.Contains(ni.Description.ToLowerInvariant()) &&
                !VirtualKeywords.Contains(ni.Name.ToLowerInvariant())
            );

        foreach (var ni in networkInterfaces)
        {
            var ipProps = ni.GetIPProperties();
            foreach (var address in ipProps.UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = address.Address;
                    if (ip.ToString().StartsWith("192.168."))
                        return ip;

                    candidates.Add(ip);
                }
        }

        if (candidates.Count > 0)
            return candidates[0];

        throw new Exception("No suitable IPv4 address found.");
    }

    protected static int GetIpv6MulticastInterfaceIndex()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up);

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            foreach (var address in props.UnicastAddresses)
                if (address.Address is { AddressFamily: AddressFamily.InterNetworkV6, IsIPv6LinkLocal: false })
                    return props.GetIPv6Properties().Index;
        }

        return 0;
    }

    protected abstract void OnReceiveMcMulticastMessage(TMultiCastPacket message, PacketContext context);

    private void ListenedLanServer(string serverName, ushort port)
    {
        Logger.LogLanServerIsListened(serverName, port);

        OnListenedLanServer?.Invoke(serverName, port);

        foreach (var (id, partner) in _partnerManager.Partners)
        {
            var message = CreateMcMulticastMessage(serverName, port);

            if (!partner.Connection.IsConnected) continue;

            partner.Connection.SendData(message);

            Logger.LogSendLanServerToPartner(serverName, port, id);
        }
    }

    protected abstract TMultiCastPacket CreateMcMulticastMessage(string serverName, ushort port);

    protected abstract Socket? CreateMultiCastDiscoverySocket();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogStartListeningLanMulticast();
        var buffer = new byte[256];

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_roomInfoManager.CurrentGroupInfo == null)
            {
                await Task.Delay(3000, stoppingToken);
                continue;
            }

            using var multicastSocket = CreateMultiCastDiscoverySocket();

            if (multicastSocket == null)
                break;

            try
            {
                var receiveFromResult = await multicastSocket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    MulticastPacketReceiveAddress,
                    stoppingToken);

                var message = Encoding.UTF8.GetString(buffer, 0, receiveFromResult.ReceivedBytes);
                var serverName = message["[MOTD]".Length..message.IndexOf("[/MOTD]", StringComparison.Ordinal)];
                var portStart = message.IndexOf("[AD]", StringComparison.Ordinal) + 4;
                var portEnd = message.IndexOf("[/AD]", StringComparison.Ordinal);
                var port = ushort.Parse(message[portStart..portEnd]);

                if (!serverName.StartsWith($"[{Prefix}]"))
                {
                    ListenedLanServer(serverName, port);
                    Logger.LogLocalGameDiscovered(serverName, port);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                // Shutdown
            }
            catch (Exception ex)
            {
                Logger.LogFailedToReceiveMulticastMessage(ex);
            }

            await Task.Delay(3000, stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogStoppingFakeMcServerMultiCaster();

        return base.StopAsync(cancellationToken);
    }
}

internal static partial class FakeServerMultiCasterLoggers
{
    [LoggerMessage(LogLevel.Information,
        "[MC_MULTI_CASTER] Received multicast message from {SenderId}, remote real port is {Port}, name is {Name}, is IPV6 [{isIpv6}]")]
    public static partial void LogReceivedMulticastMessage(this ILogger logger, Guid senderId, ushort port, string name,
        bool isIpv6);

    [LoggerMessage(LogLevel.Error, "Proxy creation failed, sender ID: {SenderId}")]
    public static partial void LogProxyCreationFailed(this ILogger logger, Guid senderId);

    [LoggerMessage(LogLevel.Debug, "Multicast message to client, mapping port is {Port}, name is {Name}")]
    public static partial void LogMulticastMessageToClient(this ILogger logger, ushort port, string name);

    [LoggerMessage(LogLevel.Debug, "Lan server {ServerName}:{Port} is listened")]
    public static partial void LogLanServerIsListened(this ILogger logger, string serverName, ushort port);

    [LoggerMessage(LogLevel.Trace, "[MC_MULTI_CASTER] Send lan server {ServerName}:{Port} to {Partner}")]
    public static partial void LogSendLanServerToPartner(this ILogger logger, string serverName, ushort port,
        Guid partner);

    [LoggerMessage(LogLevel.Information, "[MC_MULTI_CASTER] Stopping FakeMcServerMultiCaster")]
    public static partial void LogStoppingFakeMcServerMultiCaster(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Start listening LAN multicast")]
    public static partial void LogStartListeningLanMulticast(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "Failed to receive multicast message.")]
    public static partial void LogFailedToReceiveMulticastMessage(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Debug, "Socket setup [{mode}], multicast address is {MulticastAddress}")]
    public static partial void LogSocketSetup(this ILogger logger, string mode, string multicastAddress);

    [LoggerMessage(LogLevel.Information, "Local game discovered, name [{name}], port [{port}]")]
    public static partial void LogLocalGameDiscovered(this ILogger logger, string name, ushort port);

    [LoggerMessage(LogLevel.Debug, "[{type}] Socket setup for Windows, multicast address is {MulticastAddress}")]
    public static partial void LogSocketSetupForWindows(this ILogger logger, string type, string multicastAddress);

    [LoggerMessage(LogLevel.Debug, "[{type}] Socket setup for Linux/MacOS, local IP is {LocalIp}")]
    public static partial void LogSocketSetupForLinuxMacOs(this ILogger logger, string type, string localIp);
}
