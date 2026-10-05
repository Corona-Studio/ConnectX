using System.Collections.Concurrent;

namespace ConnectX.Actors;

/// <summary>
/// Each instance owns one thread and one asynchronous continuation context.
/// The bounded mailbox is the only ingress; this queue contains only continuations
/// of the current turn. Awaiting a command never starts the next mailbox command.
/// </summary>
internal sealed class ActorSynchronizationContext(string name) : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _continuations = new();
    private int _started;
    private int _threadId;
    public bool IsCurrent => Environment.CurrentManagedThreadId == _threadId && Current == this;
    public override SynchronizationContext CreateCopy() => this;
    public override void Post(SendOrPostCallback callback, object? state) => _continuations.Add((callback, state));
    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (!IsCurrent) throw new InvalidOperationException("Synchronous cross-actor calls are forbidden; send a message instead.");
        callback(state);
    }

    public Task RunAsync(Func<Task> run)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Actor context already started.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            _threadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            Post(async _ =>
            {
                try { await run(); }
                catch (Exception exception) { failure = exception; }
                finally { _continuations.CompleteAdding(); }
            }, null);
            try
            {
                foreach (var (callback, state) in _continuations.GetConsumingEnumerable())
                {
                    SetSynchronizationContext(this);
                    callback(state);
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                SetSynchronizationContext(null);
                _continuations.Dispose();
                if (failure == null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }) { Name = name, IsBackground = true };
        using (ExecutionContext.SuppressFlow()) thread.Start();
        return completion.Task;
    }
}
