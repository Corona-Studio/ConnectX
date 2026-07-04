using System.Collections.Concurrent;
using ConnectX.Client.Messages.Proxy;
using ConnectX.Client.Proxy;
using ConnectX.Client.Route;
using ConnectX.Client.Transmission;
using ConnectX.Shared.Helpers;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

public sealed class ProxyManager : GenericProxyManager
{
    private readonly PartnerManager _partnerManager;
    private readonly ConcurrentBag<(IDispatcher, HandlerId)> _registeredHandlers = [];

    public ProxyManager(
        RouterPacketDispatcher packetDispatcher,
        RelayPacketDispatcher relayPacketDispatcher,
        PartnerManager partnerManager,
        IHostApplicationLifetime lifetime,
        IServiceProvider serviceProvider,
        ILogger<ProxyManager> logger)
        : base(packetDispatcher, relayPacketDispatcher, lifetime, serviceProvider, logger)
    {
        _partnerManager = partnerManager;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _partnerManager.OnPartnerAdded += OnP2PPartnerAdded;

        foreach (var (_, partner) in _partnerManager.Partners)
        {
            var id = partner.Connection.Dispatcher.AddHandler<ProxyConnectReq>(ctx =>
            {
                ReceivedProxyConnectReq(ctx, partner.Connection);
            });

            _registeredHandlers.Add((partner.Connection.Dispatcher, id));
        }

        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _partnerManager.OnPartnerAdded -= OnP2PPartnerAdded;

        while (_registeredHandlers.TryTake(out var item))
        {
            var (dispatcher, id) = item;

            dispatcher.RemoveHandler(id);
        }

        return base.StopAsync(cancellationToken);
    }

    private void OnP2PPartnerAdded(Partner partner)
    {
        var id = partner.Connection.Dispatcher.AddHandler<ProxyConnectReq>(ctx =>
        {
            ReceivedProxyConnectReq(ctx, partner.Connection);
        });

        _registeredHandlers.Add((partner.Connection.Dispatcher, id));
    }

    public GenericProxyAcceptor? GetOrCreateAcceptor(
        Guid partnerId,
        ushort remoteRealMcServerPort,
        bool isIpv6)
    {
        if (!_partnerManager.Partners.TryGetValue(partnerId, out var value))
        {
            Logger.LogPartnerNotFound(partnerId);

            return null;
        }

        var con = value.Connection;

        return GetOrCreateAcceptor(
            partnerId,
            isIpv6,
            NetworkHelper.GetAvailablePrivatePort,
            remoteRealMcServerPort,
            con);
    }

    public override void RemoveAllProxies()
    {
        while (_registeredHandlers.TryTake(out var item))
        {
            var (dispatcher, id) = item;

            dispatcher.RemoveHandler(id);
        }

        _registeredHandlers.Clear();

        base.RemoveAllProxies();

        Logger.LogProxiesCleared();
    }
}

internal static partial class ProxyManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "[PROXY_MANAGER] Proxies cleared.")]
    public static partial void LogProxiesCleared(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[PROXY_MANAGER] Partner {PartnerId} not found")]
    public static partial void LogPartnerNotFound(this ILogger logger, Guid partnerId);
}