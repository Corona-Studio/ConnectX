using System.Net;
using ConnectX.Client.Helpers;
using ConnectX.Client.Interfaces;
using Hive.Common.Shared.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroTier.Core;

namespace ConnectX.Client;

public class ZeroTierNodeLinkHolder(ILogger<ZeroTierNodeLinkHolder> logger) : BackgroundService, IZeroTierNodeLinkHolder
{
    private ulong? _networkId;

    // If this is not null, then you can assume that you are currently in a room
    public Node? Node { get; private set; }

    public bool IsZeroTierInitialized { get; private set; }

    public IPAddress[] GetIpAddresses()
    {
        ArgumentNullException.ThrowIfNull(Node);

        return Node.GetNetworkAddresses(_networkId!.Value).ToArray();
    }

    public IPAddress? GetFirstAvailableV4Address()
    {
        ArgumentOutOfRangeException.ThrowIfEqual(IsNodeOnline(), false);

        var address = Node!.GetNetworkAddresses(_networkId!.Value);

        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfEqual(address.Count, 0);

        return address.GetFirstAvailableV4();
    }

    public IPAddress? GetFirstAvailableV6Address()
    {
        ArgumentOutOfRangeException.ThrowIfEqual(IsNodeOnline(), false);

        var address = Node!.GetNetworkAddresses(_networkId!.Value);

        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfEqual(address.Count, 0);

        return address.GetFirstAvailableV6();
    }

    public bool IsNodeOnline()
    {
        return Node is { Online: true } &&
               Node.Networks.Count != 0;
    }

    public bool IsNetworkReady()
    {
        return _networkId.HasValue &&
               (Node?.IsNetworkTransportReady(_networkId.Value) ?? false);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // TODO: Add support for other platform
        if (!OperatingSystem.IsWindows())
            return base.StartAsync(cancellationToken);

        logger.LogStartingZeroTierNodeLinkHolder();

        try
        {
            Node = new Node();

            Node.InitAllowNetworkCaching(false);
            Node.InitAllowPeerCaching(true);
            Node.InitSetRandomPortRange(IZeroTierNodeLinkHolder.RandomPortLower,
                IZeroTierNodeLinkHolder.RandomPortUpper);
            Node.InitSetEventHandler(OnReceivedZeroTierEvent);

            Node.Start();
            IsZeroTierInitialized = true;
        }
        catch (Exception e)
        {
            IsZeroTierInitialized = false;
            logger.LogFailedToInitZeroTierSocketConnection(e);
            return base.StartAsync(cancellationToken);
        }

        return base.StartAsync(cancellationToken);
    }

    public async Task<bool> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken)
    {
        if (Node == null) return false;

        await TaskHelper.WaitUtil(() => Node?.Online ?? false, cancellationToken);

        Node.Join(networkId);

        await TaskHelper.WaitUtil(() => Node?.Networks.Count != 0, cancellationToken);

        logger.LogZeroTierConnected(
            Node!.IdString,
            Node.Version,
            Node.PrimaryPort,
            Node.SecondaryPort,
            Node.TertiaryPort);

        await TaskHelper.WaitUtil(() => Node.IsNetworkTransportReady(networkId), cancellationToken);

        logger.LogNetworkJoined();

        var addresses = Node.GetNetworkAddresses(networkId);

        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentOutOfRangeException.ThrowIfEqual(addresses.Count, 0);

        logger.LogNumOfAssignedAddresses(addresses.Count);

        foreach (var (index, address) in addresses.Index())
        {
            logger.LogAssignedAddress(index, address);
        }

        _networkId = networkId;

        return true;
    }

    public Task LeaveNetworkAsync(CancellationToken cancellationToken)
    {
        if (Node == null) return Task.CompletedTask;
        if (!_networkId.HasValue) return Task.CompletedTask;

        Node.Leave(_networkId.Value);
        _networkId = null;

        return Task.CompletedTask;
    }

    private void OnReceivedZeroTierEvent(Event nodeEvent)
    {
        logger.LogEventReceived(nodeEvent.Code, nodeEvent.Name);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Update peers
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Node == null)
            {
                // Node is not initialized because of error
                break;
            }

            if (!IsNodeOnline() || !_networkId.HasValue)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            var routes = Node!.GetNetworkRoutes(_networkId.Value);

            if (routes == null || routes.Count == 0)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Node?.Free();
        Node?.Stop();
        Node = null;
        _networkId = null;

        logger.LogZeroTierNodeLinkHolderStopped();

        return base.StopAsync(cancellationToken);
    }
}

internal static partial class ZeroTierNodeLinkHolderLogger
{
    [LoggerMessage(LogLevel.Debug, "[ZeroTier] Event.Code = {eventId} ({eventName})")]
    public static partial void LogEventReceived(this ILogger logger, int eventId, string eventName);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Starting ZeroTier Node Link Holder")]
    public static partial void LogStartingZeroTierNodeLinkHolder(this ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "[ZeroTier] ZeroTier connected, ID [{id}] Version [{version}]. Ports: I[{port1}] II[{port2}] III[{port3}]")]
    public static partial void LogZeroTierConnected(this ILogger logger, string id, string version, ushort port1,
        ushort port2, ushort port3);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Network joined")]
    public static partial void LogNetworkJoined(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Num of assigned addresses: {count}")]
    public static partial void LogNumOfAssignedAddresses(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] Assigned address {index}: {address}")]
    public static partial void LogAssignedAddress(this ILogger logger, int index, IPAddress address);

    [LoggerMessage(LogLevel.Information, "[ZeroTier] ZeroTier Node Link Holder stopped")]
    public static partial void LogZeroTierNodeLinkHolderStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Critical, "[ZeroTier] Failed to init ZeroTier socket connection.")]
    public static partial void LogFailedToInitZeroTierSocketConnection(this ILogger logger, Exception exception);
}