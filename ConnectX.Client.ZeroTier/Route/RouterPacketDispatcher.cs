using System.Buffers;
using ConnectX.Client.Models;
using ConnectX.Client.Route.Packet;
using ConnectX.Shared.Helpers;
using Hive.Codec.Abstractions;
using Hive.Network.Shared;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Route;

public sealed class RouterPacketDispatcher : PacketDispatcherBase<P2PPacket>
{
    private readonly Router _router;

    public RouterPacketDispatcher(
        Router router,
        IPacketCodec codec,
        ILogger<RouterPacketDispatcher> logger) : base(codec, logger)
    {
        _router = router;

        router.OnDelivery += OnReceiveDatagram;
    }

    protected override void OnReceiveDatagram(P2PPacket packet)
    {
        var sequence = new ReadOnlySequence<byte>(packet.Payload);
        var message = Codec.Decode(sequence);
        var messageType = message!.GetType();

        Logger.LogReceived(messageType.Name, packet.From);

        Dispatch(message, messageType, packet.From);
    }

    public void Send<T>(Guid target, T data)
    {
        SendToRouter(target, data);

        Logger.LogSent(typeof(T).Name, target);
    }

    private void SendToRouter<T>(Guid targetId, T datagram)
    {
        using var stream = RecycleMemoryStreamManagerHolder.Shared.GetStream();
        Codec.Encode(datagram, stream);

        stream.Seek(0, SeekOrigin.Begin);

        var buffer = stream.GetBuffer();

        _router.Send(targetId, buffer.AsMemory(0, (int)stream.Length));
    }

    /// <summary>
    ///     发送并接收，使用processor处理结果，如果processor返回true，则停止等待并返回true，否则继续接收下一个包，直到超时返回false
    /// </summary>
    /// <returns>返回处理结果，如果processor返回了true，则为true<br />如果processor一直没返回true，超时了则返回false</returns>
    public async Task<bool> SendAndListenOnceAsync<TData, T>(
        Guid target,
        TData data,
        Func<T, bool> processor,
        CancellationToken token = default)
    {
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<T, PacketContext> callback = (message, _) =>
        {
            if (processor(message)) completionSource.TrySetResult(true);
        };
        var effectiveToken = token == CancellationToken.None ? CancelTokenSource.Token : token;

        SetTemporaryReceiver(target, callback);

        try
        {
            SendToRouter(target, data);
            await completionSource.Task.WaitAsync(effectiveToken);
            return true;
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            // Only remove our own callback. A newer retry for the same partner may
            // already have replaced it by the time this wait is cancelled.
            RemoveTemporaryReceiver(target, callback);
        }
    }
}

internal static partial class RouterPacketDispatcherLoggers
{
    [LoggerMessage(LogLevel.Trace, "[ROUTER_DISPATCHER] {DataType} sent to {Target}")]
    public static partial void LogSent(this ILogger logger, string dataType, Guid target);
}
