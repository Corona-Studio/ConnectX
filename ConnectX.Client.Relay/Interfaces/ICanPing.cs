using Hive.Both.General.Dispatchers;

namespace ConnectX.Client.Interfaces;

public interface ICanPing<out TId>
{
    TId To { get; }
    IDispatcher Dispatcher { get; }
    bool ShouldUseDispatcherSenderInfo { get; }
    void SendPingPacket<T>(T packet);
}
