using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public sealed class GenericProxyClient : GenericProxyBase
{
    private readonly bool _isIpv6;

    public GenericProxyClient(
        TunnelIdentifier tunnelIdentifier,
        bool isIpv6,
        CancellationToken cancellationToken,
        ILogger<GenericProxyClient> logger)
        : base(tunnelIdentifier, cancellationToken, logger)
    {
        _isIpv6 = isIpv6;
    }

    private ushort LocalServerPort => TunnelIdentifier.LocalRealPort;
    private ushort RemoteClientPort => TunnelIdentifier.RemoteRealPort;

    protected override object GetProxyInfoForLog()
    {
        return new
        {
            Type = "Client",
            LocalMcPort = LocalServerPort,
            IsIpv6 = _isIpv6
        };
    }

    protected override Socket CreateSocket()
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var address = _isIpv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;

        socket.Connect(new IPEndPoint(address, LocalServerPort));

        socket.NoDelay = true;
        //socket.LingerState = new LingerOption(true, 3);

        Logger.LogConnectedToMc(LocalServerPort, GetProxyInfoForLog());

        InvokeRealServerConnected();

        return socket;
    }
}

internal static partial class GenericProxyClientLoggers
{
    [LoggerMessage(LogLevel.Information, "[PROXY_CLIENT] Connected to MC. Mapping: {McPort}, ProxyInfo: {ProxyInfo}")]
    public static partial void LogConnectedToMc(this ILogger logger, ushort mcPort, object proxyInfo);
}