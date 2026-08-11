using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Proxy;

public sealed class GenericProxyServer : GenericProxyBase
{
    private readonly Socket _socket;

    public GenericProxyServer(
        Socket socket,
        TunnelIdentifier tunnelIdentifier,
        CancellationToken cancellationToken,
        ILogger<GenericProxyServer> logger) :
        base(tunnelIdentifier, cancellationToken, logger)
    {
        _socket = socket;
    }

    private ushort RemoteRealPort => TunnelIdentifier.RemoteRealPort;

    protected override object GetProxyInfoForLog()
    {
        return new
        {
            Type = "Server",
            FakePort = TunnelIdentifier.LocalRealPort,
            RemteRealPort = RemoteRealPort
        };
    }

    protected override Socket CreateSocket()
    {
        return _socket;
    }
}