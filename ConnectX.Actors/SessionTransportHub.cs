using System.Runtime.CompilerServices;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Actors;

/// <summary>Thread-safe I/O boundary. No business state or actor continuations live here.</summary>
internal sealed class SessionTransportHub(CancellationToken lifetime, ILogger logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<ISession, SessionOutbox> _outboxes = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<ISession, object> _closed = new();
    private readonly HashSet<Task> _retired = [];
    private bool _stopping;

    private SessionOutbox? GetOutbox(ISession session)
    {
        lock (_gate)
        {
            if (_stopping || _closed.TryGetValue(session, out _)) return null;
            if (_outboxes.TryGetValue(session, out var outbox)) return outbox;
            outbox = new SessionOutbox(session, lifetime, logger);
            _outboxes[session] = outbox;
            return outbox;
        }
    }

    public Task<bool> FlushPendingAsync(ISession session)
        => GetOutbox(session)?.FlushPendingAsync() ?? Task.FromResult(false);

    public void Prepare(ISession session) => GetOutbox(session);

    public void Send<T>(IDispatcher dispatcher, ISession session, T message, bool closeAfter = false)
    {
        var outbox = GetOutbox(session);
        if (outbox == null) return;
        outbox.Enqueue(token => dispatcher.SendAsync(session, message, token));
        if (closeAfter) outbox.Complete();
    }

    public void SendRaw(ISession session, byte[] payload) => GetOutbox(session)?.Enqueue(async token =>
    {
        using var stream = new MemoryStream(payload, writable: false);
        return await session.TrySendAsync(stream, token);
    });

    public Task ObserveAsync(ISession session)
    {
        var outbox = GetOutbox(session);
        return Task.Run(async () =>
        {
            try { await session.StartAsync(lifetime); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { logger.LogOutboxFailed(exception); }
            finally
            {
                Close(session);
                if (outbox != null) await outbox.Completion;
                if (session is IDisposable disposable) disposable.Dispose();
            }
        });
    }

    public void Close(ISession session)
    {
        lock (_gate)
        {
            _closed.GetValue(session, static _ => new object());
            if (_outboxes.Remove(session, out var outbox))
            {
                outbox.Complete();
                _retired.Add(outbox.Completion);
            }
        }
        SessionHealth.Close(session);
    }

    public void Reap()
    {
        lock (_gate)
        {
            foreach (var session in _outboxes.Where(x => x.Value.Completion.IsCompleted).Select(x => x.Key).ToArray())
            {
                _closed.GetValue(session, static _ => new object());
                _outboxes.Remove(session);
            }
            _retired.RemoveWhere(task => task.IsCompleted);
        }
    }

    public async Task StopAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            _stopping = true;
            foreach (var outbox in _outboxes.Values) outbox.Complete();
            pending = _outboxes.Values.Select(x => x.Completion).Concat(_retired).ToArray();
            _outboxes.Clear();
            _retired.Clear();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
