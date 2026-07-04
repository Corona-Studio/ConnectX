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

public class PartnerManager
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly PeerManager _peerManager;
    private readonly IRoomInfoManager _roomInfoManager;
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly IServiceProvider _serviceProvider;

    private IPEndPoint? _assignedRelayServerAddress;

    public PartnerManager(
        PeerManager peerManager,
        IDispatcher dispatcher,
        IRoomInfoManager roomInfoManager,
        IServerLinkHolder serverLinkHolder,
        IServiceProvider serviceProvider,
        ILogger<PartnerManager> logger)
    {
        _peerManager = peerManager;
        _dispatcher = dispatcher;
        _roomInfoManager = roomInfoManager;
        _serverLinkHolder = serverLinkHolder;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _roomInfoManager.OnMemberAddressInfoUpdated += UpdatePartnerInfo;

        _dispatcher.AddHandler<GroupUserStateChanged>(OnGroupUserStateChanged);
        _dispatcher.AddHandler<RelayServerAddressAssignedMessage>(OnRelayServerAddressAssignedMessageReceived);
    }

    public ConcurrentDictionary<Guid, Partner> Partners { get; } = new();

    private void OnRelayServerAddressAssignedMessageReceived(MessageContext<RelayServerAddressAssignedMessage> ctx)
    {
        if (ctx.Message.UserId != _serverLinkHolder.UserId)
        {
            _logger.LogWrongRelayServerAddressAssignedMessageReceived(ctx.Message.UserId);
            return;
        }

        _assignedRelayServerAddress = ctx.Message.ServerAddress;

        _logger.LogRelayServerAddressAssigned(ctx.Message.ServerAddress);
    }

    private void UpdatePartnerInfo(UserInfo[] userInfos)
    {
        if (_roomInfoManager.CurrentGroupInfo == null)
        {
            _logger.LogRoomInfoEmpty();
            return;
        }

        foreach (var userInfo in userInfos)
        {
            var userId = userInfo.UserId;

            if (userId == _serverLinkHolder.UserId)
                continue;

            if (userInfo.RelayServerAddress != null)
            {
                AddRelayPartner(userId, userInfo.RelayServerAddress);
                continue;
            }

            if (_assignedRelayServerAddress != null &&
                userInfo.RelayServerAddress == null)
            {
                // We do not create P2P connection if the user asked to use relay server
                AddRelayPartner(userId, _assignedRelayServerAddress);
                continue;
            }

            AddPartner(userId);
        }
    }

    public event Action<Partner>? OnPartnerAdded;
    public event Action<Partner>? OnPartnerRemoved;

    private void OnGroupUserStateChanged(MessageContext<GroupUserStateChanged> ctx)
    {
        var message = ctx.Message;

        _logger.LogRoomStateChanged(message.State, message.UserInfo?.UserId ?? Guid.Empty);

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
                if (message.UserInfo!.RelayServerAddress != null)
                {
                    AddRelayPartner(message.UserInfo.UserId, message.UserInfo!.RelayServerAddress);
                    return;
                }

                AddPartner(message.UserInfo!.UserId);
                return;
            case GroupUserStates.InfoUpdated:
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(message.State));
        }
    }

    private bool AddRelayPartner(Guid partnerId, IPEndPoint relayServerAddress)
    {
        if (partnerId == _serverLinkHolder.UserId) return false;

        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var connection = ActivatorUtilities.CreateInstance<RelayConnection>(
            _serviceProvider,
            partnerId,
            relayServerAddress,
            dispatcher);

        if (Partners.ContainsKey(partnerId)) return false;

        var partner = ActivatorUtilities.CreateInstance<Partner>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            partnerId,
            connection);

        if (!Partners.TryAdd(partnerId, partner)) return false;

        OnPartnerAdded?.Invoke(partner);

        _logger.LogRelayPartnerAdded(relayServerAddress, partnerId);

        return true;
    }

    private bool AddPartner(Guid partnerId)
    {
        if (partnerId == _serverLinkHolder.UserId) return false;

        var dispatcher = ActivatorUtilities.CreateInstance<DefaultDispatcher>(_serviceProvider);
        var connection = ActivatorUtilities.CreateInstance<P2PConnection>(
            _serviceProvider,
            partnerId,
            dispatcher);

        if (Partners.ContainsKey(partnerId)) return false;

        var partner = ActivatorUtilities.CreateInstance<Partner>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            partnerId,
            connection);

        if (!Partners.TryAdd(partnerId, partner)) return false;

        _peerManager.AddLink(partnerId);
        OnPartnerAdded?.Invoke(partner);

        _logger.LogPartnerAdded(partnerId);

        return true;
    }

    private bool RemovePartner(Guid partnerId)
    {
        if (!Partners.TryRemove(partnerId, out var partner))
        {
            _logger.LogFailedToRemovePartner(partnerId);
            return false;
        }

        partner.Disconnect();
        _logger.LogDisconnectedWithPartnerId(partnerId);

        OnPartnerRemoved?.Invoke(partner);

        return true;
    }

    public void RemoveAllPartners()
    {
        _assignedRelayServerAddress = null;
        _peerManager.RemoveAllPeer();

        foreach (var (_, partner) in Partners)
            partner.Disconnect();

        Partners.Clear();

        _logger.LogAllPartnersRemoved();
    }
}

internal static partial class PartnerManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[PARTNER_MANAGER] Partner disconnected with user ID [{partnerId}]")]
    public static partial void LogDisconnectedWithPartnerId(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information,
        "[PARTNER_MANAGER] Room state changed for user [{userId}] with state [{groupState:G}]")]
    public static partial void LogRoomStateChanged(this ILogger logger, GroupUserStates groupState, Guid userId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] Partner added with user ID [{userId}]")]
    public static partial void LogPartnerAdded(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information,
        "[PARTNER_MANAGER] Partner using relay server [{relayServerAddress}] added with user ID [{userId}]")]
    public static partial void LogRelayPartnerAdded(this ILogger logger, IPEndPoint relayServerAddress, Guid userId);

    [LoggerMessage(LogLevel.Warning,
        "[PARTNER_MANAGER] Wrong relay server address assigned message received with user ID [{userId}]")]
    public static partial void LogWrongRelayServerAddressAssignedMessageReceived(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] Relay server address assigned [{serverAddress}]")]
    public static partial void LogRelayServerAddressAssigned(this ILogger logger, IPEndPoint serverAddress);

    [LoggerMessage(LogLevel.Error, "[PARTNER_MANAGER] Failed to remove partner with user ID [{partnerId}]")]
    public static partial void LogFailedToRemovePartner(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[PARTNER_MANAGER] All partners removed")]
    public static partial void LogAllPartnersRemoved(this ILogger logger);
}