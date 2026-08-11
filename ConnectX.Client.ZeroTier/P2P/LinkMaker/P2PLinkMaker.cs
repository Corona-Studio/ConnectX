using System.Net;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.P2P.LinkMaker;

public abstract class P2PLinkMaker(
    long startTimeTick,
    Guid partnerId,
    IServiceProvider serviceProvider,
    ILogger<P2PLinkMaker> logger,
    CancellationToken cancellationToken)
{
    protected readonly ILogger<P2PLinkMaker> Logger = logger;
    protected readonly IServiceProvider ServiceProvider = serviceProvider;
    protected CancellationToken Token = cancellationToken;

    public long StartTimeTick { get; } = startTimeTick;
    public Guid PartnerId { get; } = partnerId;

    public abstract IPEndPoint? RemoteIpEndPoint { get; }
    public abstract Task<ISession?> BuildLinkAsync();
    public event Action<ISession>? OnLinkBuilt;

    public event Action? OnConnectedFailed;

    protected void InvokeOnConnected(ISession connection)
    {
        OnLinkBuilt?.Invoke(connection);
    }

    protected void InvokeOnFailed()
    {
        OnConnectedFailed?.Invoke();
    }

    protected async Task WaitUntilStartTimeAsync(CancellationTokenSource? tokenSource)
    {
        while (tokenSource is not { IsCancellationRequested: true } &&
               DateTime.UtcNow.Ticks < StartTimeTick)
            await Task.Delay(TimeSpan.FromTicks(1), tokenSource!.Token);
    }
}