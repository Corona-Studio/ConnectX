using System.Collections.Concurrent;
using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Transmission;
using ConnectX.Client.Transmission.Connections;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Managers;

/// <summary>
/// Owns the logical partner connections. The base implementation is relay-only;
/// an optional transport project can override <see cref="AddDirectPartner"/>.
/// </summary>
public class PartnerManager
{
    private readonly IDispatcher _dispatcher;
    private readonly IRoomInfoManager _roomInfoManager;
    private IPEndPoint? _assignedRelayServerAddress;

    protected readonly ILogger Logger;
    protected readonly IServerLinkHolder ServerLinkHolder;
    protected readonly IServiceProvider ServiceProvider;

    public PartnerManager(
        IDispatcher dispatcher,
        IRoomInfoManager roomInfoManager,
        IServerLinkHolder serverLinkHolder,
        IServiceProvider serviceProvider,
        ILogger<PartnerManager> logger)
    {
        _dispatcher = dispatcher;
        _roomInfoManager = roomInfoManager;
        ServerLinkHolder = serverLinkHolder;
        ServiceProvider = serviceProvider;
        Logger = logger;

        _roomInfoManager.OnMemberAddressInfoUpdated += UpdatePartnerInfo;
        _dispatcher.AddHandler<GroupUserStateChanged>(OnGroupUserStateChanged);
        _dispatcher.AddHandler<RelayServerAddressAssignedMessage>(OnRelayServerAddressAssignedMessageReceived);
    }

    public ConcurrentDictionary<Guid, Partner> Partners { get; } = new();

    public event Action<Partner>? OnPartnerAdded;
    public event Action<Partner>? OnPartnerRemoved;

    private void OnRelayServerAddressAssignedMessageReceived(MessageContext<RelayServerAddressAssignedMessage> ctx)
    {
        if (ctx.Message.UserId != ServerLinkHolder.UserId)
        {
            Logger.LogWrongRelayServerAddressAssignedMessageReceived(ctx.Message.UserId);
            return;
        }

        _assignedRelayServerAddress = ctx.Message.ServerAddress;
        Logger.LogRelayServerAddressAssigned(ctx.Message.ServerAddress);
    }

    private void UpdatePartnerInfo(UserInfo[] userInfos)
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
        {
            Logger.LogRoomInfoEmpty();
            return;
        }

        foreach (var userInfo in userInfos)
            AddPartner(userInfo);
    }

    private void OnGroupUserStateChanged(MessageContext<GroupUserStateChanged> ctx)
    {
        var message = ctx.Message;
        Logger.LogRoomStateChanged(message.State, message.UserInfo?.UserId ?? Guid.Empty);

        switch (message.State)
        {
            case GroupUserStates.Dismissed:
                RemoveAllPartners();
                return;
            case GroupUserStates.Disconnected:
            case GroupUserStates.Kicked:
            case GroupUserStates.Left:
                RemovePartner(message.UserInfo!.UserId);
                return;
            case GroupUserStates.Joined:
                AddPartner(message.UserInfo!);
                return;
            case GroupUserStates.InfoUpdated:
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(message.State));
        }
    }

    private bool AddPartner(UserInfo userInfo)
    {
        if (userInfo.UserId == ServerLinkHolder.UserId) return false;

        var relayAddress = userInfo.RelayServerAddress ?? _assignedRelayServerAddress;
        return relayAddress != null
            ? AddRelayPartner(userInfo.UserId, relayAddress)
            : AddDirectPartner(userInfo.UserId);
    }

    protected bool AddRelayPartner(Guid partnerId, IPEndPoint relayServerAddress)
    {
        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(ServiceProvider);
        var connection = ActivatorUtilities.CreateInstance<RelayConnection>(
            ServiceProvider, partnerId, relayServerAddress, dispatcher);

        if (!TryAddConnection(partnerId, connection)) return false;

        Logger.LogRelayPartnerAdded(relayServerAddress, partnerId);
        return true;
    }

    protected virtual bool AddDirectPartner(Guid partnerId)
    {
        Logger.LogDirectTransportUnavailable(partnerId);
        return false;
    }

    protected bool TryAddConnection(Guid partnerId, ConnectionBase connection)
    {
        if (Partners.ContainsKey(partnerId)) return false;

        var partner = ActivatorUtilities.CreateInstance<Partner>(
            ServiceProvider, ServerLinkHolder.UserId, partnerId, connection);

        if (!Partners.TryAdd(partnerId, partner)) return false;

        OnPartnerAdded?.Invoke(partner);
        return true;
    }

    private bool RemovePartner(Guid partnerId)
    {
        if (!Partners.TryRemove(partnerId, out var partner))
        {
            Logger.LogFailedToRemovePartner(partnerId);
            return false;
        }

        partner.Disconnect();
        Logger.LogDisconnectedWithPartnerId(partnerId);
        OnPartnerRemoved?.Invoke(partner);
        return true;
    }

    public virtual void RemoveAllPartners()
    {
        _assignedRelayServerAddress = null;

        foreach (var (_, partner) in Partners)
            partner.Disconnect();

        Partners.Clear();
        Logger.LogAllPartnersRemoved();
    }
}

internal static partial class PartnerManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[PARTNER_MANAGER] Partner disconnected with user ID [{partnerId}]")]
    public static partial void LogDisconnectedWithPartnerId(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information,
        "[PARTNER_MANAGER] Room state changed for user [{userId}] with state [{groupState:G}]")]
    public static partial void LogRoomStateChanged(this ILogger logger, GroupUserStates groupState, Guid userId);

    [LoggerMessage(LogLevel.Information,
        "[PARTNER_MANAGER] Partner using relay server [{relayServerAddress}] added with user ID [{userId}]")]
    public static partial void LogRelayPartnerAdded(this ILogger logger, IPEndPoint relayServerAddress, Guid userId);

    [LoggerMessage(LogLevel.Warning,
        "[PARTNER_MANAGER] Wrong relay server address assigned message received with user ID [{userId}]")]
    public static partial void LogWrongRelayServerAddressAssignedMessageReceived(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] Relay server address assigned [{serverAddress}]")]
    public static partial void LogRelayServerAddressAssigned(this ILogger logger, IPEndPoint serverAddress);

    [LoggerMessage(LogLevel.Warning, "[PARTNER_MANAGER] Direct transport is unavailable for user [{userId}]")]
    public static partial void LogDirectTransportUnavailable(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Error, "[PARTNER_MANAGER] Failed to remove partner with user ID [{partnerId}]")]
    public static partial void LogFailedToRemovePartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] All partners removed")]
    public static partial void LogAllPartnersRemoved(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "[PARTNER_MANAGER] Room info is empty")]
    public static partial void LogRoomInfoEmpty(this ILogger logger);
}
