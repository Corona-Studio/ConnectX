using ConnectX.Shared.Helpers;
using ConnectX.Actors;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using ConnectX.Server.Interfaces;
using ConnectX.Server.Messages.Queries;
using ConnectX.Server.Models;
using ConnectX.Server.Models.ZeroTier;
using ConnectX.Server.Services;
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

public partial class GroupManager
{
    partial void DisposeActorResources() { _clientManager.OnSessionDisconnected -= ClientManagerOnSessionDisconnected; }

    private readonly IZeroTierNodeInfoService? _zeroTierNodeInfoService;
    private readonly RelayServerManager _relayServerManager;
    private readonly RelayLoadManager _relayLoadManager;
    private readonly ClientManager _clientManager;
    private readonly RoomCreationRecordService _roomCreationRecordService;
    private readonly InterconnectServerManager _interconnectServerManager;
    private readonly IDispatcher _dispatcher;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    private readonly Dictionary<Guid, Guid> _pendingRoomOperations = [];
    private readonly Dictionary<Guid, Group> _groupMappings = new();
    private readonly Dictionary<SessionId, Guid> _sessionIdMapping = new();
    private readonly Dictionary<string, Guid> _shortIdGroupMappings = new();
    private readonly Dictionary<Guid, BasicUserInfo> _userMapping = new();

    private readonly ControlPlaneActor _actor;

    public GroupManager(
        ControlPlaneActor actor,
        IDispatcher dispatcher,
        RelayServerManager relayServerManager,
        RelayLoadManager relayLoadManager,
        ClientManager clientManager,
        RoomCreationRecordService roomCreationRecordService,
        InterconnectServerManager interconnectServerManager,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<GroupManager> logger,
        IZeroTierNodeInfoService? zeroTierNodeInfoService = null)
    {
        _actor = actor;
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

        RegisterActorHandlers(dispatcher, actor);
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
        _actor.AssertAccess();
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

        if (_userMapping.ContainsKey(assignedId) || _sessionIdMapping.ContainsKey(id))
        {
            _logger.LogGroupManagerFailedToAddSessionToSessionMapping(id);
            return Guid.Empty;
        }

        _userMapping.Add(assignedId, user);
        _sessionIdMapping.Add(id, assignedId);
        _logger.LogSessionAttached(signinMessage.DisplayName, id, assignedId);

        return assignedId;
    }

    private bool IsSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_clientManager.IsSessionAttached(session.Id)) return true;

        var err = new GroupOpResult(GroupCreationStatus.SessionDetached, "Session does not attached to CM.");
        _actor.Send(dispatcher, session, err);

        _logger.LogReceivedGroupOpMessageFromUnattachedSession(session.Id);

        return false;
    }

    private bool IsGroupSessionAttached(
        IDispatcher dispatcher,
        ISession session)
    {
        if (_sessionIdMapping.ContainsKey(session.Id)) return true;

        var err = new GroupOpResult(GroupCreationStatus.SessionDetached, "Session does not attached to GM.");
        _actor.Send(dispatcher, session, err);

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
        _actor.Send(dispatcher, session, err);

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
        _actor.Send(dispatcher, session, err);

        _logger.LogUserAlreadyInGroupPerformingGroupOp(session.Id);

        return true;
    }

    public bool TryQueryGroup(
        QueryRemoteServerRoomInfo query,
        [NotNullWhen(true)] out Group? group)
    {
        _actor.AssertAccess();
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
        _actor.Send(dispatcher, session, err);

        _logger.LogGroupDoesNotExist(session.Id);

        return false;
    }

    private void NotifyGroupMembers<T>(
        Group group,
        T stateChange)
    {
        if (stateChange is GroupUserStateChanged change && group.AssignedRelayServer != null &&
            _relayServerManager.TryGetRelayServerSession(group.AssignedRelayServer, out var relay))
        {
            var subject = change.UserInfo ?? (UserInfo)group.RoomOwner;
            _actor.Send(_dispatcher, relay, new UpdateRelayUserRoomMappingMessage
            {
                RoomId = group.RoomId, UserId = subject.UserId, State = change.State,
                IsGroupOwner = subject.UserId == group.RoomOwner.UserId
            });
        }
        foreach (var member in group.Users) _actor.Send(_dispatcher, member.Session, stateChange);
    }

    private void ClientManagerOnSessionDisconnected(SessionId sessionId)
    {
        if (!_sessionIdMapping.Remove(sessionId, out var userId)) return;
        if (!_userMapping.Remove(userId, out var user)) return;
        _pendingRoomOperations.Remove(userId);

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

        _groupMappings.Remove(group.RoomId);
        _shortIdGroupMappings.Remove(group.RoomShortId);
        QueueNetworkDeletion(group.NetworkId);
        NotifyGroupMembers(group, new GroupUserStateChanged(GroupUserStates.Dismissed, group.RoomOwner));

        _logger.LogGroupHasBeenDismissedBy(group.RoomId, sessionId);
    }

    public bool TryGetUserId(SessionId sessionId, out Guid userId)
    {
        _actor.AssertAccess();
        return _sessionIdMapping.TryGetValue(sessionId, out userId);
    }

    public bool TryGetUserRoomId(Guid userId, out Guid roomId)
    {
        _actor.AssertAccess();
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

    [ActorMessage]
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
            _actor.Send(ctx.Dispatcher, ctx.FromSession, err);
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
        _actor.Send(ctx.Dispatcher, ctx.FromSession, result);

        _logger.LogMemberInfoUpdated(userId, ctx.Message);

        NotifyGroupMembers(group, new RoomMemberInfoUpdated { UserInfo = user });
    }

    [ActorMessage]
    private void OnCreateGroupReceived(MessageContext<CreateGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (_pendingRoomOperations.ContainsKey(userId))
        {
            _actor.Send(ctx.Dispatcher, ctx.FromSession, new GroupOpResult(GroupCreationStatus.Other, "A room operation is already pending."));
            return;
        }
        if (IsAlreadyInGroup(userId, ctx.Dispatcher, ctx.FromSession)) return;

        BeginCreateRoom(userId, ctx);
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

        _actor.Send(ctx.Dispatcher, ctx.FromSession, new RelayServerAddressAssignedMessage(userId, relayServerAddress));

        _logger.LogRelayServerAddressAssigned(userId, relayServerAddress);

        return relayServerAddress;
    }

    private void BeginCreateRoom(Guid userId, MessageContext<CreateGroup> ctx)
    {
        var operation = Guid.NewGuid();
        if (!_pendingRoomOperations.TryAdd(userId, operation))
        {
            _actor.Send(ctx.Dispatcher, ctx.FromSession, new GroupOpResult(GroupCreationStatus.Other, "A room operation is already pending."));
            return;
        }
        if (ctx.Message.UseRelayServer)
        {
            CompleteCreateRoom(userId, operation, ctx, null, null);
            return;
        }
        var nodeAddress = _zeroTierNodeInfoService?.NodeStatus?.Address;
        if (nodeAddress == null)
        {
            _pendingRoomOperations.Remove(userId);
            _actor.Send(ctx.Dispatcher, ctx.FromSession, new GroupOpResult(GroupCreationStatus.NetworkControllerNotReady,
                _zeroTierNodeInfoService == null ? "ZeroTier is disabled on this server; use a relay room." : "ZeroTier network controller is not ready."));
            return;
        }
        if (!_actor.RunEffect(async token =>
        {
            NetworkDetailsModel? network = null;
            string? error = null;
            try { network = await CreateNetworkAsync(ctx.Message, nodeAddress, token); }
            catch (Exception exception) { error = exception.Message; }
            await _actor.InvokeAsync(() => CompleteCreateRoom(userId, operation, ctx, network, error));
        }))
            CompleteCreateRoom(userId, operation, ctx, null, "Too many external operations; try again.");
    }

    private async Task<NetworkDetailsModel?> CreateNetworkAsync(CreateGroup message, string nodeAddress, CancellationToken token)
    {
        var networkId = $"{nodeAddress}______";
            var networkCreationReq = new NetworkDetailsReqModel
            {
                Name = GuidHelper.Hash($"GROUP: {message.RoomName}{DateTime.Now.ToFileTimeUtc()}").ToString("N"),
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var scope = IZeroTierNodeInfoService.CreateZtApi(_serviceScopeFactory, out var api);
        return await api.CreateOrUpdateNetwork(networkId, networkCreationReq, timeout.Token)
            ?? throw new InvalidOperationException("Network controller returned no network.");
    }

    private void CompleteCreateRoom(Guid userId, Guid operation, MessageContext<CreateGroup> ctx,
        NetworkDetailsModel? networkDetail, string? error)
    {
        _actor.AssertAccess();
        if (!_pendingRoomOperations.TryGetValue(userId, out var current) || current != operation ||
            !_userMapping.TryGetValue(userId, out var currentUser) || !ReferenceEquals(currentUser.Session, ctx.FromSession) ||
            !_clientManager.IsSessionAttached(ctx.FromSession.Id) || IsAlreadyInGroup(userId, ctx.Dispatcher, ctx.FromSession, false))
        {
            if (networkDetail != null) QueueNetworkDeletion(Convert.ToUInt64(networkDetail.Id, 16));
            return;
        }
        _pendingRoomOperations.Remove(userId);
        if (error != null)
        {
            _actor.Send(ctx.Dispatcher, ctx.FromSession, new GroupOpResult(GroupCreationStatus.NetworkControllerError, error));
            return;
        }
        var message = ctx.Message;
        var owner = _userMapping[userId];
        var assignedRelayServerAddress = ctx.Message.UseRelayServer
            ? TryAssignRelayServerAddress(userId, ctx)
            : null;
        if (ctx.Message.UseRelayServer && assignedRelayServerAddress == null)
        {
            _actor.Send(ctx.Dispatcher, ctx.FromSession,
                new GroupOpResult(GroupCreationStatus.Other, "No relay server is available."));
            return;
        }

        var ownerSession = new UserSessionInfo(owner, assignedRelayServerAddress);

        var group = new Group(message.RoomName, message.RoomPassword, ownerSession, [ownerSession])
        {
            IsPrivate = message.IsPrivate,
            MaxUserCount = message.MaxUserCount <= 0 ? 10 : message.MaxUserCount,
            RoomDescription = message.RoomDescription,
            NetworkId = networkDetail == null ? 0 : Convert.ToUInt64(networkDetail.Id, 16),
            AssignedRelayServer = assignedRelayServerAddress
        };

        if (!_groupMappings.TryAdd(group.RoomId, group) ||
            !_shortIdGroupMappings.TryAdd(group.RoomShortId, group.RoomId))
        {
            _groupMappings.Remove(group.RoomId);
            _shortIdGroupMappings.Remove(group.RoomShortId);
            QueueNetworkDeletion(group.NetworkId);
            var err = new GroupOpResult(GroupCreationStatus.InternalError, "Failed to add group to the group mapping.");
            _actor.Send(ctx.Dispatcher, ctx.FromSession, err);

            _logger.LogFailedToAddGroupToGroupMapping(ctx.FromSession.Id);

            return;
        }

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

            _actor.Send(_dispatcher, relaySession, relayUpdate);
        }

        var metadata = new Dictionary<string, string>(1)
        {
            {GroupOpResult.MetadataUseRelayServer, (assignedRelayServerAddress != null).ToString()}
        };

        var success = new GroupOpResult(
            GroupCreationStatus.Succeeded,
            null,
            metadata)
        {
            RoomId = group.RoomId
        };

        _actor.Send(ctx.Dispatcher, ctx.FromSession, success);

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

    private void BeginRemoteRoomQuery(Guid userId, MessageContext<JoinGroup> ctx)
    {
        var operation = Guid.NewGuid();
        if (!_pendingRoomOperations.TryAdd(userId, operation)) return;
        var servers = _interconnectServerManager.GetRegisteredServers();
        if (!_actor.RunEffect(async token =>
        {
            InterconnectServerRegistration? found = null;
            try { found = await _interconnectServerManager.FindRemoteRoomAsync(ctx.Message, servers, token); }
            finally
            {
                await _actor.InvokeAsync(() =>
                {
                    if (!_pendingRoomOperations.TryGetValue(userId, out var current) || current != operation) return;
                    _pendingRoomOperations.Remove(userId);
                    if (!_userMapping.TryGetValue(userId, out var user) || !ReferenceEquals(user.Session, ctx.FromSession) ||
                        IsAlreadyInGroup(userId, ctx.Dispatcher, ctx.FromSession, false)) return;
                    CompleteRemoteRoomQuery(ctx, found);
                });
            }
        }))
        {
            _pendingRoomOperations.Remove(userId);
            _actor.Send(ctx.Dispatcher, ctx.FromSession, new GroupOpResult(GroupCreationStatus.Other, "Too many external operations; try again."));
        }
    }

    private void CompleteRemoteRoomQuery(MessageContext<JoinGroup> ctx, InterconnectServerRegistration? fetchRemoteRoomInfo)
    {
        if (fetchRemoteRoomInfo == null)
        {
            var err = new GroupOpResult(GroupCreationStatus.GroupNotExists, "Group does not exist.");
            _actor.Send(ctx.Dispatcher, ctx.FromSession, err);

            _logger.LogGroupDoesNotExist(ctx.FromSession.Id);

            return;
        }

        var infoJson = JsonSerializer.Serialize(
            fetchRemoteRoomInfo,
            InterconnectServerRegistrationContexts.Default.InterconnectServerRegistration);
        var metadata = new Dictionary<string, string>(1)
        {
            {GroupOpResult.MetadataRedirectInfo, infoJson}
        };
        var redirectMsg = new GroupOpResult(
            GroupCreationStatus.NeedRedirect,
            null,
            metadata);

        _actor.Send(ctx.Dispatcher, ctx.FromSession, redirectMsg);

        _logger.LogRedirectMessageSent(fetchRemoteRoomInfo);
    }

    [ActorMessage]
    private void OnJoinGroupReceived(MessageContext<JoinGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (_pendingRoomOperations.ContainsKey(userId))
        {
            _actor.Send(ctx.Dispatcher, ctx.FromSession, new GroupOpResult(GroupCreationStatus.Other, "A room operation is already pending."));
            return;
        }
        if (IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession)) return;

        var message = ctx.Message;
        var groupId = string.IsNullOrEmpty(message.RoomShortId)
            ? message.GroupId
            : _shortIdGroupMappings.TryGetValue(message.RoomShortId, out var id)
                ? id
                : Guid.Empty;

        if (!_groupMappings.TryGetValue(groupId, out var group))
        {
            BeginRemoteRoomQuery(userId, ctx);
            return;
        }

        if (group.MaxUserCount != 0 &&
            group.Users.Count >= group.MaxUserCount)
        {
            var err = new GroupOpResult(GroupCreationStatus.GroupIsFull, "Group is full.");
            _actor.Send(ctx.Dispatcher, ctx.FromSession, err);

            _logger.LogGroupIsFull(ctx.FromSession.Id, groupId);

            return;
        }

        if (!string.IsNullOrEmpty(group.RoomPassword) &&
            group.RoomPassword != message.RoomPassword)
        {
            var err = new GroupOpResult(GroupCreationStatus.PasswordIncorrect, "Wrong password.");
            _actor.Send(ctx.Dispatcher, ctx.FromSession, err);

            _logger.LogWrongPassword(ctx.FromSession.Id, groupId);

            return;
        }

        var user = _userMapping[userId];

        if (group.AssignedRelayServer == null && !user.JoinP2PNetwork)
        {
            var err = new GroupOpResult(
                GroupCreationStatus.Other,
                "This room requires a direct-network capable client.");
            _actor.Send(ctx.Dispatcher, ctx.FromSession, err);

            _logger.LogDirectRoomRejectedForRelayOnlyClient(ctx.FromSession.Id, groupId);
            return;
        }

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
        NotifyGroupMembers(group, new GroupUserStateChanged(GroupUserStates.Joined, info));

        var metadata = new Dictionary<string, string>(1)
        {
            {GroupOpResult.MetadataUseRelayServer, (assignedRelayServerAddress != null).ToString()}
        };

        var success = new GroupOpResult(
            GroupCreationStatus.Succeeded,
            null,
            metadata) { RoomId = group.RoomId };

        _actor.Send(ctx.Dispatcher, ctx.FromSession, success);

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
            _actor.Send(_dispatcher, user.Session, new GroupUserStateChanged(state, user));

        QueueMemberDeletion(group.NetworkId, user.NetworkNodeId);
        NotifyGroupMembers(group, new GroupUserStateChanged(state, user));
    }

    private void QueueNetworkDeletion(ulong networkId) => QueueDeletion(networkId, null);
    private void QueueMemberDeletion(ulong networkId, string? nodeId)
    {
        if (!string.IsNullOrEmpty(nodeId)) QueueDeletion(networkId, nodeId);
    }
    private void QueueDeletion(ulong networkId, string? nodeId)
    {
        if (_zeroTierNodeInfoService == null || networkId == 0) return;
        if (!_actor.RunEffect(async token =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var scope = IZeroTierNodeInfoService.CreateZtApi(_serviceScopeFactory, out var api);
            var id = networkId.ToString("x");
            try
            {
                if (nodeId == null) await api.DeleteNetworkAsync(id, timeout.Token);
                else await api.DeleteNetworkMemberAsync(id, nodeId, timeout.Token);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
        })) _logger.LogFailedToDeleteNetwork("External operation capacity exhausted.");
    }

    [ActorMessage]
    private void OnLeaveGroupReceived(MessageContext<LeaveGroup> ctx)
    {
        if (!IsSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsGroupSessionAttached(ctx.Dispatcher, ctx.FromSession)) return;
        if (!TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        _pendingRoomOperations.Remove(userId);
        if (!TryGetUserRoomId(userId, out var groupId)) return;
        if (!HasUserMapping(userId, ctx.Dispatcher, ctx.FromSession)) return;
        if (!IsAlreadyInGroup(_sessionIdMapping[ctx.FromSession.Id], ctx.Dispatcher, ctx.FromSession, false)) return;

        var group = _groupMappings.Values.First(g => g.Users.Any(u => u.UserId == userId));
        var success = new GroupOpResult(GroupCreationStatus.Succeeded);

        if (group.RoomOwner.UserId == userId)
        {
            _actor.Send(ctx.Dispatcher, ctx.FromSession, success);

            _groupMappings.Remove(group.RoomId, out _);
            _shortIdGroupMappings.Remove(group.RoomShortId);
            QueueNetworkDeletion(group.NetworkId);
            NotifyGroupMembers(group, new GroupUserStateChanged(GroupUserStates.Dismissed, null));

            _logger.LogGroupHasBeenDismissedBy(groupId, ctx.FromSession.Id);

            return;
        }

        RemoveUser(groupId, userId, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Left);

        _actor.Send(ctx.Dispatcher, ctx.FromSession, success);

        _logger.LogUserLeftGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    [ActorMessage]
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
            _actor.Send(ctx.Dispatcher, ctx.FromSession, error);

            return;
        }

        RemoveUser(groupId, message.UserToKick, ctx.Dispatcher, ctx.FromSession, GroupUserStates.Kicked);

        var success = new GroupOpResult(GroupCreationStatus.Succeeded);
        _actor.Send(ctx.Dispatcher, ctx.FromSession, success);

        _logger.LogUserHasBeenKickedFromGroup(ctx.FromSession.Id, group.RoomName, group.RoomShortId);
    }

    [ActorMessage]
    private void OnAcquireGroupInfoReceived(MessageContext<AcquireGroupInfo> ctx)
    {
        var session = ctx.FromSession;

        if (!_clientManager.IsSessionAttached(session.Id) ||
            !_sessionIdMapping.TryGetValue(session.Id, out var userId) ||
            !_userMapping.ContainsKey(userId) ||
            !TryGetUserRoomId(userId, out var groupId) ||
            !_groupMappings.TryGetValue(groupId, out var group))
        {
            _actor.Send(ctx.Dispatcher, session, GroupInfo.Invalid);
            return;
        }

        var isInGroup = group.Users.Any(u => u.UserId == userId);

        if (isInGroup)
        {
            _actor.Send(ctx.Dispatcher, session, (GroupInfo)group);
            return;
        }

        // Not in group, send group info with empty user info

        var groupWithEmptyUserInfo = (GroupInfo)group with { Users = [] };
        _actor.Send(ctx.Dispatcher, session, groupWithEmptyUserInfo);
    }

    [ActorMessage]
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

            NotifyGroupMembers(group, new GroupUserStateChanged(GroupUserStates.InfoUpdated, updatedUser));
        }

        var oldName = basicUserInfo.DisplayName;

        basicUserInfo.DisplayName = ctx.Message.DisplayName;

        _logger.LogUserDisplayNameUpdated(userId, oldName, ctx.Message.DisplayName);
    }

    public GroupInfo? GetGroupSnapshot(Guid groupId)
    {
        _actor.AssertAccess();
        return _groupMappings.TryGetValue(groupId, out var group) ? (GroupInfo)group : null;
    }

    public bool TryGetGroup(Guid groupId, [NotNullWhen(true)] out Group? group)
    {
        _actor.AssertAccess();
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

    [LoggerMessage(LogLevel.Error, "[GROUP_MANAGER] Failed to delete network, {message}.")]
    public static partial void LogFailedToDeleteNetwork(this ILogger logger, string message);

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

    [LoggerMessage(LogLevel.Warning,
        "[GROUP_MANAGER] Relay-only session [{sessionId}] tried to join direct room [{groupId}]")]
    public static partial void LogDirectRoomRejectedForRelayOnlyClient(
        this ILogger logger,
        SessionId sessionId,
        Guid groupId);
}
