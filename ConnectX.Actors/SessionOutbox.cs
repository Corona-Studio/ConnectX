using System.Threading.Channels;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Actors;

/// <summary>One writer per connection preserves output order and isolates slow peers.</summary>
internal sealed class SessionOutbox
{
    private readonly Channel<Func<CancellationToken, ValueTask<bool>>> _queue = Channel.CreateBounded<Func<CancellationToken, ValueTask<bool>>>(
        new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly ISession _session;
    public Task Completion { get; }

    public SessionOutbox(ISession session, CancellationToken lifetime, ILogger logger)
    {
        _session = session;
        using (ExecutionContext.SuppressFlow())
            Completion = Task.Run(() => RunAsync(lifetime, logger));
    }

    public void Enqueue(Func<CancellationToken, ValueTask<bool>> send)
    {
        if (_queue.Writer.TryWrite(send)) return;
        Complete();
        SessionHealth.Close(_session);
    }

    public void Complete() => _queue.Writer.TryComplete();

    private async Task RunAsync(CancellationToken lifetime, ILogger logger)
    {
        try
        {
            await foreach (var send in _queue.Reader.ReadAllAsync(lifetime))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (!await send(timeout.Token)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { logger.LogOutboxFailed(exception); }
        finally { Complete(); SessionHealth.Close(_session); }
    }
}
