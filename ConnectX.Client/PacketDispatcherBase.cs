using System.Reflection;
using ConnectX.Client.Models;
using Hive.Codec.Abstractions;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client;

public abstract class PacketDispatcherBase<TInPacket>
{
    protected readonly CancellationTokenSource CancelTokenSource;
    protected readonly IPacketCodec Codec;
    protected readonly ILogger Logger;

    protected readonly Dictionary<Type, CallbackWarp> ReceiveCallbackDic = [];

    protected PacketDispatcherBase(
        IPacketCodec codec,
        ILogger logger)
    {
        Codec = codec;
        Logger = logger;
        CancelTokenSource = new CancellationTokenSource();
    }

    public void OnReceive<T>(Action<T, PacketContext> callback)
    {
        if (!ReceiveCallbackDic.ContainsKey(typeof(T))) ReceiveCallbackDic.Add(typeof(T), new CallbackWarp());
        ReceiveCallbackDic[typeof(T)].UniformCallback.Add(callback);
    }

    public void OnReceive<T>(Guid receiver, Action<T, PacketContext> callback)
    {
        if (!ReceiveCallbackDic.ContainsKey(typeof(T))) ReceiveCallbackDic.Add(typeof(T), new CallbackWarp());
        ReceiveCallbackDic[typeof(T)].SpecificCallback.Add(receiver, callback);
    }

    protected void Dispatch(object message, Type messageType, Guid from)
    {
        if (!ReceiveCallbackDic.TryGetValue(messageType, out var callbackWarp)) return;

        var genericActionType = typeof(Action<,>).MakeGenericType(messageType, typeof(PacketContext));
        var actMethod = genericActionType.GetMethod("Invoke");

        if (callbackWarp.TempCallback.Count > 0)
            lock (callbackWarp.TempCallback)
            {
                if (callbackWarp.TempCallback.TryGetValue(from, out var value))
                    InvokeCallback(actMethod, value, message);
            }

        // 调用同步回调

        if (callbackWarp.SpecificCallback.TryGetValue(from, out var cbValue))
            InvokeCallback(actMethod, cbValue, message);

        foreach (var callback in callbackWarp.UniformCallback) InvokeCallback(actMethod, callback, message);
        return;

        void InvokeCallback(MethodBase? callback, object receiver, object message1)
        {
            callback?.Invoke(receiver, [message1, new PacketContext(from)]);
        }
    }

    protected abstract void OnReceiveDatagram(TInPacket packet);

    protected readonly struct CallbackWarp()
    {
        public readonly List<object> UniformCallback = [];
        public readonly Dictionary<Guid, object> SpecificCallback = [];
        public readonly Dictionary<Guid, object> TempCallback = [];
    }
}

internal static partial class PacketDispatcherBaseLoggers
{
    [LoggerMessage(LogLevel.Trace, "[PACKET_DISPATCHER] {DataType} received from {From}")]
    public static partial void LogReceived(this ILogger logger, string dataType, Guid from);
}