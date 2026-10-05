using ConnectX.Actors;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using ConnectX.Server.Managers;
using ConnectX.Server.Models.Contexts;
using ConnectX.Server.Models.DataBase;
using ConnectX.Shared.Messages.Group;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Services;

public partial class RoomJoinRecordService : BackgroundService
{
    private record FetchedRoomInfo(Guid UserId, Guid RoomId, UpdateRoomMemberNetworkInfo Info);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastRefreshTimes = new();
    private readonly ConcurrentQueue<FetchedRoomInfo> _roomInfoUpdateQueue = [];

    private readonly GroupManager _groupManager;
    private readonly PeerInfoService _peerInfoService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<RoomJoinRecordService> _logger;

    private readonly ControlPlaneActor _actor;

    public RoomJoinRecordService(
        ControlPlaneActor actor,
        GroupManager groupManager,
        PeerInfoService peerInfoService,
        IDispatcher dispatcher,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RoomJoinRecordService> logger)
    {
        _actor = actor;
        _groupManager = groupManager;
        _peerInfoService = peerInfoService;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        RegisterActorHandlers(dispatcher, actor);
    }

    [ActorMessage]
    private void OnReceivedRoomInfoUpdate(MessageContext<UpdateRoomMemberNetworkInfo> ctx)
    {
        if (string.IsNullOrEmpty(ctx.Message.NetworkNodeId)) return;
        if (!_groupManager.TryGetUserId(ctx.FromSession.Id, out var userId)) return;
        if (!_groupManager.TryGetUserRoomId(userId, out var roomId)) return;
        if (_lastRefreshTimes.TryGetValue(userId, out var time) &&
            (DateTime.UtcNow - time).TotalSeconds < 5)
            return;

        var roomInfo = new FetchedRoomInfo(userId, roomId, ctx.Message);

        _lastRefreshTimes[userId] = DateTime.UtcNow;
        _roomInfoUpdateQueue.Enqueue(roomInfo);
    }

    private void RefreshTimeCleanup()
    {
        var now = DateTime.UtcNow;
        var toRemove = new List<Guid>();

        foreach (var (userId, time) in _lastRefreshTimes)
        {
            if ((now - time).TotalMinutes < 5) continue;
            toRemove.Add(userId);
        }

        foreach (var userId in toRemove)
            _lastRefreshTimes.TryRemove(userId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>();
        var groupManager = scope.ServiceProvider.GetRequiredService<GroupManager>();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_roomInfoUpdateQueue.IsEmpty)
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            RefreshTimeCleanup();

            if (!_roomInfoUpdateQueue.TryDequeue(out var update))
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            var group = await _actor.AskAsync(() => groupManager.GetGroupSnapshot(update.RoomId), stoppingToken);
            if (group == null)
            {
                _logger.LogFailedToGetGroup(update.RoomId);
                continue;
            }

            var peerInfo = _peerInfoService.NetworkPeers
                .FirstOrDefault(x => x.Address.Equals(update.Info.NetworkNodeId, StringComparison.OrdinalIgnoreCase));

            if (peerInfo?.Paths == null || peerInfo.Paths.Length == 0)
            {
                _logger.LogPeerInfoNotFound(update.Info.NetworkNodeId);
                _roomInfoUpdateQueue.Enqueue(update);

                await Task.Delay(5000, stoppingToken);
                continue;
            }

            var addresses = peerInfo.Paths
                .Where(p => p.Active)
                .Select(p => p.Address)
                .Where(a => !string.IsNullOrEmpty(a))
                .OfType<string>()
                .ToFrozenSet();

            if (addresses.Count == 0)
            {
                _logger.LogPeerAddressNotReady(update.Info.NetworkNodeId);
                _roomInfoUpdateQueue.Enqueue(update);

                await Task.Delay(5000, stoppingToken);
                continue;
            }

            try
            {
                var joinHistory = new RoomJoinHistory
                {
                    UserId = update.UserId,
                    RoomId = update.RoomId,
                    LogTime = DateTime.UtcNow,
                    NetworkNodeId = update.Info.NetworkNodeId,
                    RoomName = group.RoomName,
                    UserPhysicalAddress = string.Join(',', addresses)
                };

                dbContext.RoomJoinHistories.Add(joinHistory);
                await dbContext.SaveChangesAsync(stoppingToken);

                _logger.LogRoomJoinRecordAdded(update.UserId, update.RoomId, update.Info.NetworkNodeId, joinHistory.UserPhysicalAddress);
            }
            catch (Exception e)
            {
                _roomInfoUpdateQueue.Enqueue(update);
                _logger.LogFailedToAddJoinRecordToDatabase(e);
            }
        }
    }
}

internal static partial class RoomOperationRecordServiceLoggers
{
    [LoggerMessage(LogLevel.Error, "[ROOM_JOIN_RECORD_SRV] Failed to get group with ID [{groupId}]")]
    public static partial void LogFailedToGetGroup(this ILogger logger, Guid groupId);

    [LoggerMessage(LogLevel.Debug, "[ROOM_JOIN_RECORD_SRV] Peer info not found for address [{address}], retrying...")]
    public static partial void LogPeerInfoNotFound(this ILogger logger, string address);

    [LoggerMessage(LogLevel.Debug, "[ROOM_JOIN_RECORD_SRV] Peer address not ready for address [{address}], retrying...")]
    public static partial void LogPeerAddressNotReady(this ILogger logger, string address);

    [LoggerMessage(LogLevel.Information, "[ROOM_JOIN_RECORD_SRV] Room join record added, User [{userId}] Group [{groupId}] Node [{nodeId}] Address [{address}]")]
    public static partial void LogRoomJoinRecordAdded(this ILogger logger, Guid userId, Guid groupId, string nodeId, string address);

    [LoggerMessage(LogLevel.Warning, "[ROOM_JOIN_RECORD_SRV] Failed to add join record to database, retry later...")]
    public static partial void LogFailedToAddJoinRecordToDatabase(this ILogger logger, Exception ex);
}