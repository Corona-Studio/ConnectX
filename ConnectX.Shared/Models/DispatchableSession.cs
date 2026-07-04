using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Shared.Models;

public class DispatchableSession : IDispatchableSession
{
    public DispatchableSession(
        ISession session,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Session = session;
        Dispatcher = dispatcher;

        Session.BindTo(Dispatcher);

        TaskHelper.FireAndForget(() => Session.StartAsync(cancellationToken));
    }

    public ISession Session { get; }
    public IDispatcher Dispatcher { get; }

    public void Dispose()
    {
        Session.OnMessageReceived -= Dispatcher.Dispatch;
        GC.SuppressFinalize(this);
    }
}