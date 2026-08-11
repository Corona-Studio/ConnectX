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
        lock (ReceiveCallbackDic)
        {
            if (!ReceiveCallbackDic.ContainsKey(typeof(T))) ReceiveCallbackDic.Add(typeof(T), new CallbackWarp());
            ReceiveCallbackDic[typeof(T)].UniformCallback.Add(new ReceiveCallback<T>(callback));
        }
    }

    public bool RemoveReceiver<T>(Action<T, PacketContext> callback)
    {
        lock (ReceiveCallbackDic)
        {
            if (!ReceiveCallbackDic.TryGetValue(typeof(T), out var callbacks)) return false;

            var index = callbacks.UniformCallback.FindIndex(x => x.Original.Equals(callback));
            if (index < 0) return false;

            callbacks.UniformCallback.RemoveAt(index);
            return true;
        }
    }

    public void OnReceive<T>(Guid receiver, Action<T, PacketContext> callback)
    {
        lock (ReceiveCallbackDic)
        {
            if (!ReceiveCallbackDic.ContainsKey(typeof(T))) ReceiveCallbackDic.Add(typeof(T), new CallbackWarp());
            ReceiveCallbackDic[typeof(T)].SpecificCallback[receiver] = new ReceiveCallback<T>(callback);
        }
    }

    public bool RemoveReceiver<T>(Guid receiver)
    {
        lock (ReceiveCallbackDic)
        {
            return ReceiveCallbackDic.TryGetValue(typeof(T), out var callbacks) &&
                   callbacks.SpecificCallback.Remove(receiver);
        }
    }

    protected void SetTemporaryReceiver<T>(Guid sender, Action<T, PacketContext> callback)
    {
        lock (ReceiveCallbackDic)
        {
            if (!ReceiveCallbackDic.TryGetValue(typeof(T), out var callbacks))
            {
                callbacks = new CallbackWarp();
                ReceiveCallbackDic.Add(typeof(T), callbacks);
            }

            callbacks.TempCallback[sender] = new ReceiveCallback<T>(callback);
        }
    }

    protected bool RemoveTemporaryReceiver<T>(Guid sender, Action<T, PacketContext> callback)
    {
        lock (ReceiveCallbackDic)
        {
            if (!ReceiveCallbackDic.TryGetValue(typeof(T), out var callbacks) ||
                !callbacks.TempCallback.TryGetValue(sender, out var currentCallback) ||
                !ReferenceEquals(currentCallback.Original, callback))
                return false;

            return callbacks.TempCallback.Remove(sender);
        }
    }

    protected void Dispatch(object message, Type messageType, Guid from)
    {
        IReceiveCallback? tempCallback = null;
        IReceiveCallback? specificCallback = null;
        IReceiveCallback[] uniformCallbacks;

        lock (ReceiveCallbackDic)
        {
            if (!ReceiveCallbackDic.TryGetValue(messageType, out var callbackWarp)) return;

            callbackWarp.TempCallback.TryGetValue(from, out tempCallback);

            callbackWarp.SpecificCallback.TryGetValue(from, out specificCallback);
            uniformCallbacks = [.. callbackWarp.UniformCallback];
        }

        var context = new PacketContext(from);

        if (tempCallback != null)
            tempCallback.Invoke(message, context);

        // 调用同步回调

        if (specificCallback != null)
            specificCallback.Invoke(message, context);

        foreach (var callback in uniformCallbacks) callback.Invoke(message, context);
    }

    protected abstract void OnReceiveDatagram(TInPacket packet);

    protected interface IReceiveCallback
    {
        Delegate Original { get; }

        void Invoke(object message, PacketContext context);
    }

    private sealed class ReceiveCallback<T>(Action<T, PacketContext> callback) : IReceiveCallback
    {
        public Delegate Original => callback;

        public void Invoke(object message, PacketContext context)
        {
            callback((T)message, context);
        }
    }

    protected readonly struct CallbackWarp()
    {
        public readonly List<IReceiveCallback> UniformCallback = [];
        public readonly Dictionary<Guid, IReceiveCallback> SpecificCallback = [];
        public readonly Dictionary<Guid, IReceiveCallback> TempCallback = [];
    }
}

public static partial class PacketDispatcherBaseLoggers
{
    [LoggerMessage(LogLevel.Trace, "[PACKET_DISPATCHER] {DataType} received from {From}")]
    public static partial void LogReceived(this ILogger logger, string dataType, Guid from);
}
