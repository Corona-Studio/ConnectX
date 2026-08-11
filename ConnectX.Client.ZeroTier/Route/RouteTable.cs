using ConnectX.Client.Interfaces;
using ConnectX.Client.Route.Packet;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Route;

public class RouteTable
{
    private readonly Dictionary<Guid, LinkStatePacket> _linkStates = [];
    private readonly ILogger _logger;
    private readonly IServerLinkHolder _serverLinkHolder;
    private Dictionary<Guid, Guid> _routeTableInternal = [];

    public RouteTable(
        IServerLinkHolder serverLinkHolder,
        ILogger<RouteTable> logger)
    {
        _serverLinkHolder = serverLinkHolder;
        _logger = logger;
    }

    public Guid GetForwardInterface(Guid to)
    {
        return _routeTableInternal.GetValueOrDefault(to);
    }

    public void ForceAdd(Guid to, Guid interfaceId)
    {
        _routeTableInternal[to] = interfaceId;
    }

    public void Update(LinkStatePacket linkState)
    {
        lock (_linkStates)
        {
            _logger.LogUpdateRouteTable(linkState.Source, linkState.Timestamp);

            if (_linkStates.TryGetValue(linkState.Source, out var value))
                if (value.Timestamp < linkState.Timestamp)
                    _linkStates[linkState.Source] = linkState;
                else
                    return; //已有更加新的数据
            else
                _linkStates.Add(linkState.Source, linkState);

            CalculateRouteTable();
        }
    }

    private void CalculateRouteTable()
    {
        if (!_linkStates.TryGetValue(_serverLinkHolder.UserId, out var selfConnectDirectly))
            return;

        _logger.LogCalculateRouteTable(selfConnectDirectly.Source, selfConnectDirectly.Timestamp);

        HashSet<Guid> notChecked = [];
        Dictionary<Guid, Guid> routeTableTmp = [];
        Dictionary<Guid, int> dist = [];

        foreach (var (guid, _) in _linkStates)
        {
            notChecked.Add(guid);
            dist.Add(guid, int.MaxValue);
        }

        routeTableTmp.Add(_serverLinkHolder.UserId, _serverLinkHolder.UserId);
        dist[_serverLinkHolder.UserId] = 0;
        notChecked.Remove(_serverLinkHolder.UserId);

        for (var i = 0; i < selfConnectDirectly.Interfaces.Length; i++)
        {
            dist[selfConnectDirectly.Interfaces[i]] = selfConnectDirectly.Costs[i];
            if (selfConnectDirectly.Costs[i] != int.MaxValue)
                routeTableTmp.Add(selfConnectDirectly.Interfaces[i], selfConnectDirectly.Interfaces[i]);
        }

        // Dijkstra
        while (notChecked.Count > 0)
        {
            var minId = notChecked.First();
            var minDist = int.MaxValue;

            foreach (var id in notChecked.Where(id => dist[id] < minDist))
            {
                minDist = dist[id];
                minId = id;
            }

            notChecked.Remove(minId);

            if (minDist == int.MaxValue)
                continue;

            if (!_linkStates.TryGetValue(minId, out var linkState))
                continue;

            for (var i = 0; i < linkState.Interfaces.Length; i++) //更新最小距离
                if (dist.TryGetValue(linkState.Interfaces[i], out var value) && value != int.MaxValue)
                {
                    var newDist = dist[minId] + linkState.Costs[i];
                    if (newDist < dist[linkState.Interfaces[i]])
                    {
                        //通过minId前往对应的接口代价是最小的，因此路由表项等于前往minId的路由表项
                        routeTableTmp[linkState.Interfaces[i]] = routeTableTmp[minId];
                        dist[linkState.Interfaces[i]] = dist[minId] + linkState.Costs[i];
                    }
                }
                else
                {
                    dist[linkState.Interfaces[i]] = dist[minId] + linkState.Costs[i];
                    routeTableTmp[linkState.Interfaces[i]] = routeTableTmp[minId];
                }
        }

        _routeTableInternal = routeTableTmp;

        _logger.LogRouteTableUpdated(_routeTableInternal);
    }

    public List<KeyValuePair<Guid, Guid>> GetRouteTableEntries()
    {
        return [.. _routeTableInternal];
    }

    public LinkStatePacket? GetSelfLinkState()
    {
        return _linkStates.GetValueOrDefault(_serverLinkHolder.UserId);
    }

    public void RemoveAllEntries()
    {
        _routeTableInternal.Clear();
        _linkStates.Clear();
        _logger.LogRouteTableCleared();
    }
}

internal static partial class RouteTableLoggers
{
    [LoggerMessage(LogLevel.Information, "[ROUTE_TABLE] Route table cleared")]
    public static partial void LogRouteTableCleared(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ROUTE_TABLE] Update route table from {@Source} at {@Timestamp}")]
    public static partial void LogUpdateRouteTable(this ILogger logger, Guid source, long timestamp);

    [LoggerMessage(LogLevel.Information, "[ROUTE_TABLE] Calculate route table from {@Source} at {@Timestamp}")]
    public static partial void LogCalculateRouteTable(this ILogger logger, Guid source, long timestamp);

    [LoggerMessage(LogLevel.Trace, "[ROUTE_TABLE] Route table updated: \n{@RouteTable}")]
    public static partial void LogRouteTableUpdated(this ILogger logger, Dictionary<Guid, Guid> routeTable);
}