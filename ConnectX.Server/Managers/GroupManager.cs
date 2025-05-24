using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Messages.Queries;
using ConnectX.Server.Models;
using ConnectX.Server.Models.ZeroTier;
using ConnectX.Server.Services;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages.Group;
using ConnectX.Shared.Messages.Identity;
using ConnectX.Shared.Messages.Relay;
using ConnectX.Shared.Messages.Server;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Managers;

public class GroupManager
{
    private readonly IZeroTierNodeInfoService _zeroTierNodeInfoService;
    private readonly RelayServerManager _relayServerManager;
    private readonly RelayLoadManager _relayLoadManager;
    private readonly ClientManager _clientManager;
    private readonly RoomCreationRecordService _roomCreationRecordService;
    private readonly InterconnectServerManager _interconnectServerManager;
    private readonly IDispatcher _dispatcher;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<Guid, Group> _groupMappings = new();
    private readonly ConcurrentDictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly ConcurrentDictionary<string, Guid> _shortIdGroupMappings = new();
    private readonly ConcurrentDictionary<Guid, BasicUserInfo> _userMapping = new();

    public GroupManager(
        IZeroTierNodeInfoService zeroTierNodeInfoService,
        IDispatcher dispatcher,
        RelayServerManager relayServerManager,
        RelayLoadManager relayLoadManager,
        ClientManager clientManager,
        RoomCreationRecordService roomCreationRecordService,
        InterconnectServerManager interconnectServerManager,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<GroupManager> logger)
    {
        _zeroTierNodeInfoService = zeroTierNodeInfoService;
        _dispatcher = dispatcher;
        _relayServerManager = relayServerManager;
        _relayLoadManager = relayLoadManager;
        _clientManager = clientManager;
        _roomCreationRecordService = roomCreationRecordService;
        _interconnectServerManager = interconnectServerManager;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        _clientManager.OnSessionDisconnected += ClientManagerOnSessionDisconnected;

        _dispatcher.AddHandler<CreateGroup>(OnCreateGroupReceived);
        _dispatcher.AddHandler<JoinGroup>(OnJoinGroupReceived);
        _dispatcher.AddHandler<LeaveGroup>(OnLeaveGroupReceived);
        _dispatcher.AddHandler<KickUser>(OnKickUserReceived);
        _dispatcher.AddHandler<AcquireGroupInfo>(OnAcquireGroupInfoReceived);
        _dispatcher.AddHandler<UpdateRoomMemberNetworkInfo>(OnUpdateRoomMemberNetworkInfoReceived);
        _dispatcher.AddHandler<UpdateDisplayNameMessage>(UpdateDisplayNameReceived);
    }

    /// <summary>
    ///     Attach the session to the manager
    /// </summary>
    /// <param name="id"></param>
    /// <param name="session"></param>
    /// <param name="signinMessage"></param>
    /// <returns>the assigned id for the session, if the return value is default, it means the add has failed</returns>
    public Guid AttachSession(
        SessionId id,
        ISession session,
        SigninMessage signinMessage)
    {
        if (!_clientManager.IsSessionAttached(id))
        {
            _logger.LogFailedToAttachSession(id);
            return Guid.Empty;
        }

        var assignedId = Guid.CreateVersion7();
        var user = new BasicUserInfo
        {
            UserId = assignedId,
            DisplayName = signinMessage.DisplayName,
            Session = session,
            JoinP2PNetwork = signinMessage.JoinP2PNetwork
        };

        if (!_userMapping.TryAdd(assignedId, user) ||
            !_sessionIdMapping.TryAdd(id, assignedId))
        {
            _logger.LogGroupManagerFailedToAddSessionToSessionMapping(id);
            return Guid.Empty;
        }

        _logger.LogSessionAttached(signinMessage.DisplayName, id, assignedId);

        return assignedId;
    }

    private bool IsSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_clientManager.IsSessionAttached(session.Id)) return true;

        var err = new GroupOpResult(GroupCreationStatus.SessionDetached, "Session does not attached to CM.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return true;
    }

    private bool IsGroupSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_sessionIdMapping.ContainsKey(session.Id)) return true;

        var err = new GroupOpResult(GroupCreationStatus.SessionDetached, "Session does not attached to GM.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return false;
    }

    private bool HasUserMapping(
        Guid userId,
        IDispatcher dispatcher,
        ISession session)
    {
        if (_userMapping.ContainsKey(userId)) return true;

        var err = new GroupOpResult(GroupCreationStatus.UserNotExists, "User does not exist.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogUserDoesNotExist(session.Id);

        return false;
    }

    private bool IsAlreadyInGroup(
        Guid userId,
        IDispatcher dispatcher,
        ISession session,
        bool sendErr = true)
    {
        var isAlreadyInGroup = _groupMappings.Values
            .Select(g => g.Users)
            .SelectMany(u => u)
            .Any(u => u.UserId == userId);

        if (!isAlreadyInGroup) return false;
        if (!sendErr) return true;

        var err = new GroupOpResult(GroupCreationStatus.AlreadyInRoom, "User is already in a group.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogUserAlreadyInGroupPerformingGroupOp(session.Id);

        return true;
    }

    public bool TryQueryGroup(
        QueryRemoteServerRoomInfo query,
        [NotNullWhen(true)] out Group? group)
    {
        var groupId = string.IsNullOrEmpty(query.JoinGroup.RoomShortId)
            ? query.JoinGroup.GroupId
            : _shortIdGroupMappings.TryGetValue(query.JoinGroup.RoomShortId, out var id)
                ? id
                : Guid.Empty;

        return _groupMappings.TryGetValue(groupId, out group);
    }

    private bool TryGetGroup(
        Guid groupId,
        IDispatcher? dispatcher,
        ISession? session,
        [NotNullWhen(true)] out Group? group)
    {
        if (_groupMappings.TryGetValue(groupId, out group)) return true;
        if (dispatcher == null || session == null) return false;

        var err = new GroupOpResult(GroupCreationStatus.GroupNotExists, "Group does not exist.");
        dispatcher.SendAsync(session, err).Forget();

        _logger.LogGroupDoesNotExist(session.Id);

        return false;
    }

    private async Task NotifyGroupMembersAsync<T>(
        Group group,
        T stateChange)
    {
        foreach (var member in group.Users)
        {
            await _dispatcher.SendAsync(member.Session, stateChange);

            if (member.RelayServerAddress != null &&
                _relayServerManager.TryGetRelayServerSession(member.RelayServerAddress, out var relaySession) &&
                stateChange is GroupUserStateChanged { UserInfo: not null } stateChangedMessage)
            {
                var relayUpdate = new UpdateRelayUserRoomMappingMessage
                {
                    RoomId = group.RoomId,
                    UserId = stateChangedMessage.UserInfo.UserId,
                    State = stateChangedMessage.State,
                    IsGroupOwner = false
                };

                _dispatcher.SendAsync(relaySession, relayUpdate).Forget();
            }
        }
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionIdMapping.TryRemove(sessionId, out var userId)) return;
        if (!_userMapping.TryRemove(userId, out var user)) return;

        var group = _groupMappings.Values.FirstOrDefault(g => g.Users.Any(u => u.UserId == user.UserId));

        if (group == null)
        {
            _logger.LogGroupNotFound(userId);
            return;
        }

        var isGroupOwner = group.RoomOwner.UserId == user.UserId;

        if (!isGroupOwner)
        {
            RemoveUser(group.RoomId, user.UserId, null, null, GroupUserStates.Disconnected);
            _logger.LogUserHasBeenRemovedFromGroupBecauseOfDisconnection(user.UserId, group.RoomId);
            return;
        }

        DeleteGroupNetworkAsync(group).Forget();
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Dismissed, group.RoomOwner)).Forget();

        _logger.LogGroupHasBeenDismissedBy(group.RoomId, sessionId);
    }

    public bool TryGetUserId(SessionId sessionId, out Guid userId)
    {
        return _sessionIdMapping.TryGetValue(sessionId, out userId);
    }

    public bool TryGetUserRoomId(Guid userId, out Guid roomId)
    {
        var user = _groupMappings.Values
            .FirstOrDefault(g => g.Users.Any(u => u.UserId == userId));

        if (user == null)
        {
            roomId = Guid.Empty;
            return false;
        }

        roomId = user.RoomId;
        return true;
    }

    private void OnUpdateRoomMemberNetworkInfoReceived(MessageContext<UpdateRoomMemberNetworkInfo> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserRoomId(userId, out var groupId)) return;

        if (!TryGetGroup(groupId, ctx.Dispatcher, ctx.FromSession, out var group)) return;

        var user = group.Users.FirstOrDefault(u => u.UserId == userId);

        if (user == null)
        {
            var err = new GroupOpResult(GroupCreationStatus.UserNotExists, "User not found");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
            return;
        }

        if (user.UserId == group.RoomOwner.UserId)
        {
            group.RoomOwner.NetworkNodeId = ctx.Message.NetworkNodeId;
            group.RoomOwner.NetworkAddresses = ctx.Message.NetworkIpAddresses;
        }

        user.NetworkNodeId = ctx.Message.NetworkNodeId;
        user.NetworkAddresses = ctx.Message.NetworkIpAddresses;

        var result = new GroupOpResult(GroupCreationStatus.Succeeded);
        ctx.Dispatcher.SendAsync(ctx.FromSession, result).Forget();

        _logger.LogMemberInfoUpdated(userId, ctx.Message);

        NotifyGroupMembersAsync(group, new RoomMemberInfoUpdated { UserInfo = user }).Forget();
    }

    private void OnCreateGroupReceived(MessageContext<CreateGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (IsAlreadyInGroup(userId, ctx.Dispatcher, ctx.FromSession)) return;

        CreateRoomAsync(userId, ctx).Forget();
    }

    private IPEndPoint? TryAssignRelayServerAddress<T>(Guid userId, MessageContext<T> ctx)
    {        
        if (!_relayLoadManager.TryGetMostAvailableRelaySession(out var sessionId))
        {
            _logger.LogFailedToGetRelayServerAddress();
            return null;
        }

        if (!_relayServerManager.TryGetRelayServerAddress(sessionId.Value, out var relayServerAddress))
        {
            _logger.LogFailedToGetRelayServerAddress();
            return null;
        }

        ctx.Dispatcher.SendAsync(ctx.FromSession, new RelayServerAddressAssignedMessage(userId, relayServerAddress)).Forget();

        _logger.LogRelayServerAddressAssigned(userId, relayServerAddress);

        return relayServerAddress;
    }

    private async Task CreateRoomAsync(Guid userId, MessageContext<CreateGroup> ctx)
    {
        NetworkDetailsModel? networkDetail = null;

        if (!ctx.Message.UseRelayServer)
        {
            // Room owner asked to not use relay server, so we need to create a virtual network for the group.
            if (_zeroTierNodeInfoService.NodeStatus == null)
            {
                var err = new GroupOpResult(GroupCreationStatus.NetworkControllerNotReady);
                ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
                return;
            }

            var networkId = $"{_zeroTierNodeInfoService.NodeStatus.Address}______";
            var networkCreationReq = new NetworkDetailsReqModel
            {
                Name = GuidHelper.Hash($"GROUP: {ctx.Message.RoomName}{DateTime.Now.ToFileTimeUtc()}").ToString("N"),
                EnableBroadcast = true,
                IpAssignmentPools =
                [
                    new IpAssignment
                    {
                        IpRangeStart = "114.51.4.1",
                        IpRangeEnd = "114.51.4.254"
                    }
                ],
                Mtu = 2800,
                Private = false,
                Routes =
                [
                    new Route
                    {
                        Target = "114.51.4.0/24"
                    }
                ],
                V4AssignMode = new V4AssignMode { Zt = true },
                V6AssignMode = new V6AssignMode { Zt = false, Rfc4193 = false },
                MulticastLimit = 32
            };

            try
            {
                using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await using var scope = IZeroTierNodeInfoService.CreateZtApi(_serviceScopeFactory, out var zeroTierApiService);

                networkDetail = await zeroTierApiService.CreateOrUpdateNetwork(networkId, networkCreationReq, ct.Token);

                ArgumentNullException.ThrowIfNull(networkDetail);

                _logger.LogNewNetworkCreated(networkDetail.Id);
            }
            catch (ArgumentNullException)
            {
                _logger.LogFailedToCreateNetwork("Network detail is null.");

                var err = new GroupOpResult(GroupCreationStatus.NetworkControllerError);
                ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
                return;
            }
            catch (HttpRequestException e)
            {
                _logger.LogFailedToCreateNetwork(e.ToString());

                var err = new GroupOpResult(GroupCreationStatus.NetworkControllerError, e.ToString());
                ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();
                return;
            }
        }

        var message = ctx.Message;
        var owner = _userMapping[userId];
        var assignedRelayServerAddress = ctx.Message.UseRelayServer
            ? TryAssignRelayServerAddress(userId, ctx)
            : null;
        var ownerSession = new UserSessionInfo(owner, assignedRelayServerAddress);

        var group = new Group(message.RoomName, message.RoomPassword, ownerSession, [ownerSession])
        {
            IsPrivate = message.IsPrivate,
            MaxUserCount = message.MaxUserCount <= 0 ? 10 : message.MaxUserCount,
            RoomDescription = message.RoomDescription,
            NetworkId = networkDetail == null ? 0 : Convert.ToUInt64(networkDetail.Id, 16),
            AssignedRelayServer = assignedRelayServerAddress
        };

        if (assignedRelayServerAddress != null &&
            _relayServerManager.TryGetRelayServerSession(assignedRelayServerAddress, out var relaySession))
        {
            var relayUpdate = new UpdateRelayUserRoomMappingMessage
            {
                RoomId = group.RoomId,
                UserId = userId,
                State = GroupUserStates.Joined,
                IsGroupOwner = true
            };

            _dispatcher.SendAsync(relaySession, relayUpdate).Forget();
        }

        if (!_groupMappings.TryAdd(group.RoomId, group) ||
            !_shortIdGroupMappings.TryAdd(group.RoomShortId, group.RoomId))
        {
            var err = new GroupOpResult(GroupCreationStatus.InternalError, "Failed to add group to the group mapping.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogFailedToAddGroupToGroupMapping(ctx.FromSession.Id);

            return;
        }

        var metadata = new Dictionary<string, string>(1)
        {
            {GroupOpResult.UseRelayServerKey, (assignedRelayServerAddress != null).ToString()}
        };

        var success = new GroupOpResult(
            GroupCreationStatus.Succeeded,
            null,
            metadata)
        {
            RoomId = group.RoomId
        };

        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        _logger.LogGroupCreated(ctx.FromSession.Id, group.RoomName, group.RoomShortId);

        var creationRecord = new RoomRecord(
            ownerSession.UserId,
            group.RoomId,
            DateTime.UtcNow,
            message.RoomName,
            ownerSession.DisplayName,
            message.RoomDescription,
            message.RoomPassword,
            message.MaxUserCount);

        _roomCreationRecordService.CreateRecord(creationRecord);
    }

    private async Task TryGetRoomInfoFromRemoteServerAsync(MessageContext<JoinGroup> ctx)
    {
        var fetchRemoteRoomInfo = await _interconnectServerManager.TryFindTargetRemoteServerForRoomAsync(ctx.Message);

        if (fetchRemoteRoomInfo == null)
        {
            var err = new GroupOpResult(GroupCreationStatus.GroupNotExists, "Group does not exist.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogGroupDoesNotExist(ctx.FromSession.Id);

            return;
        }

        var infoJson = JsonSerializer.Serialize(
            fetchRemoteRoomInfo,
            InterconnectServerRegistrationContexts.Default.InterconnectServerRegistration);
        var metadata = new Dictionary<string, string>(1)
        {
            {GroupOpResult.RedirectInfoKey, infoJson}
        };
        var redirectMsg = new GroupOpResult(
            GroupCreationStatus.NeedRedirect,
            null,
            metadata);

        ctx.Dispatcher.SendAsync(ctx.FromSession, redirectMsg).Forget();

        _logger.LogRedirectMessageSent(fetchRemoteRoomInfo);
    }

    private void OnJoinGroupReceived(MessageContext<JoinGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession)) return;

        var message = ctx.Message;
        var groupId = string.IsNullOrEmpty(message.RoomShortId)
            ? message.GroupId
            : _shortIdGroupMappings.TryGetValue(message.RoomShortId, out var id)
                ? id
                : Guid.Empty;

        if (!_groupMappings.TryGetValue(groupId, out var group))
        {
            TryGetRoomInfoFromRemoteServerAsync(ctx).Forget();
            return;
        }

        if (group.MaxUserCount != 0 &&
            group.MaxUserCount == group.Users.Count)
        {
            var err = new GroupOpResult(GroupCreationStatus.GroupIsFull, "Group is full.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogGroupIsFull(ctx.FromSession.Id, groupId);

            return;
        }

        if (!string.IsNullOrEmpty(group.RoomPassword) &&
            group.RoomPassword != message.RoomPassword)
        {
            var err = new GroupOpResult(GroupCreationStatus.PasswordIncorrect, "Wrong password.");
            ctx.Dispatcher.SendAsync(ctx.FromSession, err).Forget();

            _logger.LogWrongPassword(ctx.FromSession.Id, groupId);

            return;
        }

        var user = _userMapping[userId];
        IPEndPoint? assignedRelayServerAddress = null;

        // If room owner is going to use relay server, we need to force all other member to use relay server too.
        if (group.AssignedRelayServer != null)
        {
            if (group.AssignedRelayServer != null &&
                _relayServerManager.TryGetRelayServerSession(group.AssignedRelayServer, out var session) &&
                _clientManager.IsSessionAttached(session.Id))
            {
                assignedRelayServerAddress = group.AssignedRelayServer;
            }
            else
            {
                assignedRelayServerAddress = TryAssignRelayServerAddress(userId, ctx);
                group.AssignedRelayServer = assignedRelayServerAddress;
            }

            _logger.LogAssignedRelayServerAddressToGroupMember(
                group.RoomId,
                user.UserId,
                user.DisplayName,
                assignedRelayServerAddress);
        }

        var info = new UserSessionInfo(user, assignedRelayServerAddress);

        group.Users.Add(info);
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Joined, info)).Forget();

        var metadata = new Dictionary<string, string>(1)
        {
            {GroupOpResult.UseRelayServerKey, (assignedRelayServerAddress != null).ToString()}
        };

        var success = new GroupOpResult(
            GroupCreationStatus.Succeeded,
            null,
            metadata) { RoomId = group.RoomId };

        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        _logger.LogUserJoinedGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void RemoveUser(
        Guid groupId,
        Guid userId,
        IDispatcher? dispatcher,
        ISession? session,
        GroupUserStates state)
    {
        if (!TryGetGroup(groupId, dispatcher, session, out var group)) return;

        var user = group.Users.FirstOrDefault(u => u.UserId == userId);

        if (user == null) return;

        group.Users.Remove(user);

        if (state == GroupUserStates.Kicked) // 通知被踢客户端
            _dispatcher.SendAsync(user.Session, new GroupUserStateChanged(state, user)).Forget();

        DeleteGroupNetworkMemberAsync(group, user).Forget();
        NotifyGroupMembersAsync(group, new GroupUserStateChanged(state, user)).Forget();
    }

    private async Task DeleteGroupNetworkAsync(Group group)
    {
        try
        {
            var networkId = group.NetworkId.ToString("X").ToLowerInvariant();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await using var scope = IZeroTierNodeInfoService.CreateZtApi(_serviceScopeFactory, out var zeroTierApiService);

            await zeroTierApiService.DeleteNetworkAsync(networkId, cts.Token);

            _logger.LogNetworkDeleted(networkId);
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound) return;

            _logger.LogFailedToDeleteNetwork(e.ToString());
        }
    }

    private async Task DeleteGroupNetworkMemberAsync(Group group, UserInfo user)
    {
        if (string.IsNullOrEmpty(user.NetworkNodeId))
            return;

        try
        {
            var networkId = group.NetworkId.ToString("X").ToLowerInvariant();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await using var scope = IZeroTierNodeInfoService.CreateZtApi(_serviceScopeFactory, out var zeroTierApiService);

            await zeroTierApiService.DeleteNetworkMemberAsync(networkId, user.NetworkNodeId, cts.Token);

            _logger.LogNetworkMemberDeleted(networkId);
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound) return;

            _logger.LogFailedToDeleteNetworkMember(e.ToString());
        }
    }

    private void OnLeaveGroupReceived(MessageContext<LeaveGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!TryGetUserRoomId(userId, out var groupId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession, false)) return;

        var group = _groupMappings.Values.First(g => g.Users.Any(u => u.UserId == userId));
        var success = new GroupOpResult(GroupCreationStatus.Succeeded);

        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        if (group.RoomOwner.UserId == userId)
        {
            _groupMappings.TryRemove(group.RoomId, out _);
            DeleteGroupNetworkAsync(group).Forget();
            NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.Dismissed, null)).Forget();

            _logger.LogGroupHasBeenDismissedBy(groupId, ctx.FromSession.Id);

            return;
        }

        RemoveUser(groupId, userId, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Left);

        _logger.LogUserLeftGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void OnKickUserReceived(MessageContext<KickUser> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!TryGetUserRoomId(userId, out var groupId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession, false)) return;

        var message = ctx.Message;
        var group = _groupMappings[groupId];

        if (group.RoomOwner.UserId != userId)
        {
            _logger.LogCanNotKickWithoutPermission(ctx.FromSession.Id.Id, userId, groupId);
            return;
        }

        if (group.RoomOwner.UserId == message.UserToKick)
        {
            _logger.LogRoomOwnerTryingToKickSelf(ctx.FromSession.Id.Id, userId, groupId);

            var error = new GroupOpResult(GroupCreationStatus.Other, "You can not kick yourself!");
            ctx.Dispatcher.SendAsync(ctx.FromSession, error).Forget();

            return;
        }

        RemoveUser(groupId, message.UserToKick, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Kicked);

        var success = new GroupOpResult(GroupCreationStatus.Succeeded);
        ctx.Dispatcher.SendAsync(ctx.FromSession, success).Forget();

        _logger.LogUserHasBeenKickedFromGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    private void OnAcquireGroupInfoReceived(MessageContext<AcquireGroupInfo> ctx)
    {
        var session = ctx.FromSession;

        if (!_clientManager.IsSessionAttached(session.Id) ||
            !_sessionIdMapping.TryGetValue(session.Id, out var userId) ||
            !_userMapping.ContainsKey(userId) ||
            !TryGetUserRoomId(userId, out var groupId) ||
            !_groupMappings.TryGetValue(groupId, out var group))
        {
            ctx.Dispatcher.SendAsync(session, GroupInfo.Invalid).Forget();
            return;
        }

        var isInGroup = group.Users.Any(u => u.UserId == userId);

        if (isInGroup)
        {
            ctx.Dispatcher.SendAsync(session, (GroupInfo)group).Forget();
            return;
        }

        // Not in group, send group info with empty user info

        var groupWithEmptyUserInfo = (GroupInfo)group with { Users = [] };
        ctx.Dispatcher.SendAsync(session, groupWithEmptyUserInfo).Forget();
    }

    private void UpdateDisplayNameReceived(MessageContext<UpdateDisplayNameMessage> ctx)
    {
        var session = ctx.FromSession;

        if (!_clientManager.IsSessionAttached(session.Id) ||
            !_sessionIdMapping.TryGetValue(session.Id, out var userId) ||
            !_userMapping.TryGetValue(userId, out var basicUserInfo))
        {
            _logger.LogFailedToModifyDisplayNameBecauseUserNotFound(session.Id);
            return;
        }

        if (string.IsNullOrEmpty(ctx.Message.DisplayName))
        {
            _logger.LogFailedToModifyDisplayNameBecauseNewNameIsEmpty();
            return;
        }

        var group = _groupMappings.Values.FirstOrDefault(g => g.Users.Any(u => u.UserId == basicUserInfo.UserId));

        if (group != null)
        {
            var user = group.Users.FirstOrDefault(u => u.UserId == basicUserInfo.UserId)!;
            var updatedUser = new UserSessionInfo(new BasicUserInfo
            {
                DisplayName = ctx.Message.DisplayName,
                JoinP2PNetwork = user.JoinP2PNetwork,
                UserId = user.UserId,
                Session = user.Session
            }, user.RelayServerAddress);

            group.Users.Remove(user);
            group.Users.Add(updatedUser);

            NotifyGroupMembersAsync(group, new GroupUserStateChanged(GroupUserStates.InfoUpdated, updatedUser)).Forget();
        }

        var oldName = basicUserInfo.DisplayName;

        basicUserInfo.DisplayName = ctx.Message.DisplayName;

        _logger.LogUserDisplayNameUpdated(userId, oldName, ctx.Message.DisplayName);
    }

    public bool TryGetGroup(Guid groupId, [NotNullWhen(true)] out Group? group)
    {
        return _groupMappings.TryGetValue(groupId, out group);
    }
}

internal static partial class GroupManagerLoggers
{
    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] Can not find any group related {userId}!")]
    public static partial void LogGroupNotFound(this ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{id}] updated its member info: {info}")]
    public static partial void LogMemberInfoUpdated(this ILogger logger, Guid id, UpdateRoomMemberNetworkInfo info);

    [LoggerMessage(LogLevel.Error,
        "[GROUP_MANAGER] Failed to attach session, session id: {sessionId}, invalid session.")]
    public static partial void LogFailedToAttachSession(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Error,
        "[GROUP_MANAGER] Failed to attach session, session id: {sessionId}, failed to add user.")]
    public static partial void LogGroupManagerFailedToAddSessionToSessionMapping(
        this ILogger logger,
        SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] Session attached, display name: {displayName}, session id: {sessionId}, assigned id: {assignedId}")]
    public static partial void LogSessionAttached(this ILogger logger, string displayName, SessionId sessionId, Guid assignedId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] Received group op message from unattached session, session id: {sessionId}")]
    public static partial void LogReceivedGroupOpMessageFromUnattachedSession(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] User does not exist, session id: {sessionId}")]
    public static partial void LogUserDoesNotExist(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] A user who is already in a group are trying to perform group op, session id: {sessionId}")]
    public static partial void LogUserAlreadyInGroupPerformingGroupOp(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] Group does not exist, session id: {sessionId}")]
    public static partial void LogGroupDoesNotExist(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] User [{userId}] has been removed from group [{groupId}] because of disconnection.")]
    public static partial void LogUserHasBeenRemovedFromGroupBecauseOfDisconnection(
        this ILogger logger,
        Guid userId,
        Guid groupId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] New network created, id: 0x{id}.")]
    public static partial void LogNewNetworkCreated(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to create new network, {message}.")]
    public static partial void LogFailedToCreateNetwork(this ILogger logger, string message);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Network deleted, id: {id}.")]
    public static partial void LogNetworkDeleted(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to delete network, {message}.")]
    public static partial void LogFailedToDeleteNetwork(this ILogger logger, string message);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Network member deleted, id: {id}.")]
    public static partial void LogNetworkMemberDeleted(this ILogger logger, string id);

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to delete network member, {message}.")]
    public static partial void LogFailedToDeleteNetworkMember(this ILogger logger, string message);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] Failed to add group to the group mapping, session id: 0x{sessionId}")]
    public static partial void LogFailedToAddGroupToGroupMapping(this ILogger logger, SessionId sessionId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] Group created by [{userId}] with info [{groupName}]({shortId})")]
    public static partial void LogGroupCreated(this ILogger logger, SessionId userId, string groupName, string shortId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] User [{sessionId}] tried to join group [{groupId}], but it is full.")]
    public static partial void LogGroupIsFull(this ILogger logger, SessionId sessionId, Guid groupId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] User [{sessionId}] tried to join group [{groupId}], but the password is wrong.")]
    public static partial void LogWrongPassword(this ILogger logger, SessionId sessionId, Guid groupId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{SessionId}] joined group [{groupName}]({shortId})")]
    public static partial void LogUserJoinedGroup(
        this ILogger logger,
        SessionId sessionId,
        string groupName,
        string shortId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Group [{groupId}] has been dismissed by [{SessionId}]")]
    public static partial void LogGroupHasBeenDismissedBy(this ILogger logger, Guid groupId, SessionId sessionId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{SessionId}] left group [{groupName}]({shortId})")]
    public static partial void LogUserLeftGroup(
        this ILogger logger,
        SessionId sessionId,
        string groupName,
        string shortId);

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] User [{SessionId}] tried to kick user [{userId}] from group [{groupId}], but the user is not the owner.")]
    public static partial void LogCanNotKickWithoutPermission(
        this ILogger logger,
        SessionId sessionId,
        Guid userId,
        Guid groupId);

    [LoggerMessage(LogLevel.Information,
        "[GROUP_MANAGER] User [{SessionId}] has been kicked from group [{groupName}]({shortId})")]
    public static partial void LogUserHasBeenKickedFromGroup(
        this ILogger logger,
        SessionId sessionId,
        string groupName,
        string shortId);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] Session [{id}] Failed to modify display name because user not found.")]
    public static partial void LogFailedToModifyDisplayNameBecauseUserNotFound(this ILogger logger, SessionId id);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] Failed to modify display name because new name is empty.")]
    public static partial void LogFailedToModifyDisplayNameBecauseNewNameIsEmpty(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] User [{userId}] display name updated from [{oldName}] to [{newName}].")]
    public static partial void LogUserDisplayNameUpdated(this ILogger logger, Guid userId, string oldName, string newName);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] User required to use relay server, but there are no server available at this time.")]
    public static partial void LogFailedToGetRelayServerAddress(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Relay server address assigned to user [{userId}], address: {address}")]
    public static partial void LogRelayServerAddressAssigned(this ILogger logger, Guid userId, IPEndPoint address);

    [LoggerMessage(LogLevel.Warning, "[GROUP_MANAGER] User [{SessionId}] tried to kick self [{userId}] from group [{groupId}], but the user is not the owner.")]
    public static partial void LogRoomOwnerTryingToKickSelf(
        this ILogger logger,
        SessionId sessionId,
        Guid userId,
        Guid groupId);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Redirect message sent to client! [{redirectMsg}]")]
    public static partial void LogRedirectMessageSent(this ILogger logger, InterconnectServerRegistration redirectMsg);

    [LoggerMessage(LogLevel.Information, "[GROUP_MANAGER] Relay server assigned for group ({groupId}) member [{userId}]({userName}) {address}")]
    public static partial void LogAssignedRelayServerAddressToGroupMember(
        this ILogger logger,
        Guid groupId,
        Guid userId,
        string userName,
        IPEndPoint? address);
}