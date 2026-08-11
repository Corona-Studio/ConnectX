using ConnectX.Client.Messages.Proxy;
using ConnectX.Client.Transmission.Connections;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Client.Proxy;

public class GenericProxyPair : IDisposable
{
    private readonly IDispatcher _dispatcher;

    public GenericProxyPair(
        Guid id,
        GenericProxyBase proxyBase,
        ushort localRealPort,
        ushort remoteRealPort,
        IDispatcher dispatcher,
        ISender sender)
    {
        Id = id;
        ProxyBase = proxyBase;
        LocalRealPort = localRealPort;
        RemoteRealPort = remoteRealPort;
        Sender = sender;

        _dispatcher = dispatcher;

        dispatcher.AddHandler<ForwardPacketCarrier>(ReceivedForwardPacket);
        proxyBase.OutwardSenders.Add(OnSend);
    }

    public ISender Sender { get; }

    /// <summary>
    ///     Partner的ID
    /// </summary>
    public Guid Id { get; }

    public GenericProxyBase? ProxyBase { get; }

    /// <summary>
    ///     连接到本机的客户端的真实端口
    /// </summary>
    public ushort LocalRealPort { get; }

    /// <summary>
    ///     对应远程客户端的在外网的映射端口
    /// </summary>
    public ushort RemoteRealPort { get; }

    public void Dispose()
    {
        _dispatcher.RemoveHandler<ForwardPacketCarrier>(ReceivedForwardPacket);
        ProxyBase?.Dispose();
    }

    private bool OnSend(ForwardPacketCarrier data)
    {
        if (data.SelfRealPort != LocalRealPort) return false;
        if (Sender is RelayConnection relayConnection)
        {
            relayConnection.SendByWorker(data.Payload);
            return true;
        }

        Sender.SendData(data);
        return true;
    }

    private void ReceivedForwardPacket(MessageContext<ForwardPacketCarrier> ctx)
    {
        ProxyBase?.OnReceiveMcPacketCarrier(ctx.Message);
    }
}