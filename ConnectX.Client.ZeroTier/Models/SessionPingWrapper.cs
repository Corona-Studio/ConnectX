using System.Net;
using ConnectX.Client.Interfaces;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;

namespace ConnectX.Client.Models;

public class SessionPingWrapper : ICanPing<IPEndPoint>
{
    private readonly IDispatchableSession _dispatchableSession;

    public SessionPingWrapper(IDispatchableSession dispatchableSession)
    {
        _dispatchableSession = dispatchableSession;

        Dispatcher = dispatchableSession.Dispatcher;
        To = dispatchableSession.Session.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, IPEndPoint.MaxPort);
    }

    public IPEndPoint To { get; }
    public IDispatcher Dispatcher { get; }
    public bool ShouldUseDispatcherSenderInfo => true;

    public void SendPingPacket<T>(T packet)
    {
        _dispatchableSession.Dispatcher.SendAsync(_dispatchableSession.Session, packet).Forget();
    }
}
