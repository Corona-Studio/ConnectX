using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;
using ConnectX.Server.Models.Contexts;
using ConnectX.Server.Models.DataBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Server.Services;

public record RoomRecord(
    Guid CreatedBy,
    Guid RoomId,
    DateTime CreatedTime,
    string RoomName,
    string UserDisplayName,
    string? RoomDescription,
    string? RoomPassword,
    int MaxUserCount);

public class RoomCreationRecordService : BackgroundService
{
    private readonly Channel<RoomRecord> _roomRecords = Channel.CreateBounded<RoomRecord>(1024);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<RoomCreationRecordService> _logger;

    public RoomCreationRecordService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RoomCreationRecordService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public void CreateRecord(RoomRecord roomRecord)
    {
        if (!_roomRecords.Writer.TryWrite(roomRecord))
            _logger.LogWarning("Room creation history queue is full or stopped; record for {RoomId} rejected", roomRecord.RoomId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var roomRecord in _roomRecords.Reader.ReadAllAsync(stoppingToken))
        {
            var history = new RoomCreateHistory
            {
                CreatedBy = roomRecord.CreatedBy,
                CreatedTime = roomRecord.CreatedTime,
                RoomId = roomRecord.RoomId,
                RoomName = roomRecord.RoomName,
                UserDisplayName = roomRecord.UserDisplayName,
                RoomDescription = roomRecord.RoomDescription,
                RoomPassword = roomRecord.RoomPassword,
                MaxUserCount = roomRecord.MaxUserCount
            };

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    // A fresh unit of work releases tracked entities on both success and failure.
                    await using var scope = _serviceScopeFactory.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<RoomOpsHistoryContext>();
                    // Keep the ID across retries, including an ambiguous database acknowledgement.
                    if (!await dbContext.RoomCreateHistories.AnyAsync(x => x.Id == history.Id, stoppingToken))
                    {
                        dbContext.RoomCreateHistories.Add(history);
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                    _logger.LogRoomCreationHistoryCreated(roomRecord.CreatedBy, roomRecord.RoomName, roomRecord.UserDisplayName);
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    _logger.LogFailedToAddCreationRecordToDatabase(e);
                    if (attempt == 2)
                        _logger.LogError("Room creation history abandoned after 3 attempts for {RoomId}", roomRecord.RoomId);
                    else await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _roomRecords.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        while (_roomRecords.Reader.TryRead(out _)) { }
    }

    public override void Dispose()
    {
        _roomRecords.Writer.TryComplete();
        while (_roomRecords.Reader.TryRead(out _)) { }
        base.Dispose();
    }
}

internal static partial class RoomCreationRecordServiceLoggers
{
    [LoggerMessage(LogLevel.Information, "[ROOM_CREATION_RECORD_SRV] Room creation history created. CreatedBy: {CreatedBy}, RoomName: {RoomName}, UserDisplayName: {UserDisplayName}")]
    public static partial void LogRoomCreationHistoryCreated(this ILogger logger, Guid createdBy, string roomName, string userDisplayName);

    [LoggerMessage(LogLevel.Warning, "[ROOM_CREATION_RECORD_SRV] Failed to add room creation record to database, retry later...")]
    public static partial void LogFailedToAddCreationRecordToDatabase(this ILogger logger, Exception exception);
}