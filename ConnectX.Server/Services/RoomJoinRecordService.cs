using Microsoft.EntityFrameworkCore;
using ConnectX.Actors;
using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private record FetchedRoomInfo(Guid UserId, Guid RoomId, UpdateRoomMemberNetworkInfo Info, DateTime ExpiresAt, Guid HistoryId, int Failures = 0);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastRefreshTimes = new();
    private readonly Channel<FetchedRoomInfo> _roomInfoUpdateQueue = Channel.CreateBounded<FetchedRoomInfo>(1024);

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

        var roomInfo = new FetchedRoomInfo(userId, roomId, ctx.Message, DateTime.UtcNow.AddMinutes(5), Guid.CreateVersion7());

        if (_roomInfoUpdateQueue.Writer.TryWrite(roomInfo)) _lastRefreshTimes[userId] = DateTime.UtcNow;
        else _logger.LogWarning("Room join history queue is full or stopped; record for {UserId} rejected", userId);
    }

    private void RefreshTimeCleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _lastRefreshTimes)
            if ((now - entry.Value).TotalMinutes >= 5)
                ((ICollection<KeyValuePair<Guid, DateTime>>)_lastRefreshTimes).Remove(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Cleanup also runs while the queue is idle; conditional removal preserves newer updates.
            RefreshTimeCleanup();
            if (!_roomInfoUpdateQueue.Reader.TryRead(out var update))
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }
            if (DateTime.UtcNow >= update.ExpiresAt)
            {
                _logger.LogWarning("Room join history expired for {UserId}", update.UserId);
                continue;
            }

            var group = await _actor.AskAsync(() => _groupManager.GetGroupSnapshot(update.RoomId), stoppingToken);
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
                Requeue(update);

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
                Requeue(update);

                await Task.Delay(5000, stoppingToken);
                continue;
            }

            try
            {
                await using var scope = _serviceScopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>();
                var joinHistory = new RoomJoinHistory
                {
                    Id = update.HistoryId,
                    UserId = update.UserId,
                    RoomId = update.RoomId,
                    LogTime = DateTime.UtcNow,
                    NetworkNodeId = update.Info.NetworkNodeId,
                    RoomName = group.RoomName,
                    UserPhysicalAddress = string.Join(',', addresses)
                };

                if (!await dbContext.RoomJoinHistories.AnyAsync(x => x.Id == update.HistoryId, stoppingToken))
                {
                    dbContext.RoomJoinHistories.Add(joinHistory);
                    await dbContext.SaveChangesAsync(stoppingToken);
                }

                _logger.LogRoomJoinRecordAdded(update.UserId, update.RoomId, update.Info.NetworkNodeId, joinHistory.UserPhysicalAddress);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                if (update.Failures < 2) Requeue(update with { Failures = update.Failures + 1 });
                else _logger.LogError("Room join history abandoned after 3 database failures for {UserId}", update.UserId);
                _logger.LogFailedToAddJoinRecordToDatabase(e);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private void Requeue(FetchedRoomInfo update)
    {
        if (DateTime.UtcNow < update.ExpiresAt && _roomInfoUpdateQueue.Writer.TryWrite(update)) return;
        _logger.LogWarning("Room join history expired or retry queue is full for {UserId}", update.UserId);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _roomInfoUpdateQueue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        while (_roomInfoUpdateQueue.Reader.TryRead(out _)) { }
        _lastRefreshTimes.Clear();
    }

    partial void DisposeActorResources()
    {
        _roomInfoUpdateQueue.Writer.TryComplete();
        while (_roomInfoUpdateQueue.Reader.TryRead(out _)) { }
        _lastRefreshTimes.Clear();
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