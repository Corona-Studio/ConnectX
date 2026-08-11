using System.Net;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P;

public record Peer(
    Guid Id,
    IPEndPoint RemoteIpe,
    DispatchableSession DirectLink,
    CancellationTokenSource CancellationTokenSource,
    ILogger<Peer> Logger)
{
    private DateTime _lastHeartBeatTime = DateTime.UtcNow;

    public Guid Id { get; } = Id;
    public IPEndPoint RemoteIpe { get; } = RemoteIpe;
    public int LocalPrivatePort { get; } = DirectLink.Session.LocalEndPoint?.Port ?? 0;
    public DispatchableSession DirectLink { get; } = DirectLink;
    public bool IsConnected { get; private set; } = true;

    public void StartHeartBeat()
    {
        DirectLink.Dispatcher.AddHandler<ChatMessage>(ctx =>
        {
            _lastHeartBeatTime = DateTime.UtcNow;
            Logger.LogReceivedChatMessage(ctx.FromSession.RemoteEndPoint, ctx.Message.Message);
        });

        Task.Run(async () =>
        {
            while (!CancellationTokenSource.IsCancellationRequested &&
                   (DateTime.UtcNow - _lastHeartBeatTime).TotalSeconds <= 20)
                try
                {
                    await DirectLink.Dispatcher.SendAsync(
                        DirectLink.Session,
                        new ChatMessage
                        {
                            Message = $"Hello from {Id}[{DirectLink.Session.RemoteEndPoint}]"
                        });

                    await Task.Delay(TimeSpan.FromSeconds(10), CancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    Logger.LogHeartBeatStoppedByCt(DirectLink.Session.RemoteEndPoint);

                    break;
                }

            Logger.LogHeartBeatStopped(DirectLink.Session.RemoteEndPoint, _lastHeartBeatTime);

            IsConnected = false;
        }, CancellationTokenSource.Token).Forget();
    }

    public void StopHeartBeat()
    {
        DirectLink.Dispose();
        CancellationTokenSource.Cancel();
        CancellationTokenSource.Dispose();
    }
}

internal static partial class PeerLoggers
{
    [LoggerMessage(LogLevel.Information, "[Peer] {RemoteEndPoint} say: {Message}")]
    public static partial void LogReceivedChatMessage(this ILogger logger, IPEndPoint? remoteEndPoint, string message);

    [LoggerMessage(LogLevel.Information, "[Peer] {RemoteEndPoint} HeartBeat stopped by cancellation token")]
    public static partial void LogHeartBeatStoppedByCt(this ILogger logger, IPEndPoint? remoteEndPoint);

    [LoggerMessage(LogLevel.Warning,
        "[Peer] {RemoteEndPoint} HeartBeat stopped, last heart beat time: {LastHeartBeatTime}")]
    public static partial void LogHeartBeatStopped(this ILogger logger, IPEndPoint? remoteEndPoint,
        DateTime lastHeartBeatTime);
}