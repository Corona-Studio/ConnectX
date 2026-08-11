using System.Net;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public abstract class SingleToSingleLinkMaker(
    IServiceProvider serviceProvider,
    ILogger<SingleToSingleLinkMaker> logger,
    long startTimeTick,
    Guid partnerId,
    IPAddress localAddress,
    ushort localPort,
    IPEndPoint remoteIpe,
    CancellationToken cancellationToken)
    : P2PLinkMaker(startTimeTick, partnerId, serviceProvider, logger, cancellationToken)
{
    protected readonly IPAddress LocalAddress = localAddress;
    protected readonly ushort LocalPort = localPort;
    protected readonly IPEndPoint RemoteIpe = remoteIpe;

    public override IPEndPoint RemoteIpEndPoint => RemoteIpe;
}