using System.Net;
using Microsoft.Extensions.Hosting;
using ZeroTier.Core;

namespace ConnectX.Client.Interfaces;

public interface IZeroTierNodeLinkHolder : IHostedService
{
    public const ushort RandomPortLower = 30000;
    public const ushort RandomPortUpper = 40000;

    bool IsZeroTierInitialized { get; }
    Node? Node { get; }

    Task<bool> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken);
    Task LeaveNetworkAsync(CancellationToken cancellationToken);

    IPAddress[] GetIpAddresses();
    IPAddress? GetFirstAvailableV4Address();
    IPAddress? GetFirstAvailableV6Address();

    bool IsNodeOnline();
    bool IsNetworkReady();
}