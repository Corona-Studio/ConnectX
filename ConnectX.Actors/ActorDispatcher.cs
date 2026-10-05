using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Logging;

namespace ConnectX.Actors;

/// <summary>
/// Typed, AOT-safe transport adapter. Registrations are removed by their exact ID;
/// one-shot replies are connection-scoped and cancellation cannot complete them twice.
/// Requests on a connection are serialized because the existing protocol has no correlation ID.
/// </summary>
public sealed class ActorDispatcher(IPacketCodec codec, ILogger<ActorDispatcher> logger) : IDispatcher
{
    private interface IHandler { Delegate Callback { get; } void Invoke(IDispatcher dispatcher, ISession session, object message); }
    private sealed class Handler<T>(Action<MessageContext<T>> callback) : IHandler
    {
        public Delegate Callback => callback;
        public void Invoke(IDispatcher dispatcher, ISession session, object message)
        {
            if (message is T value) callback(new MessageContext<T>(session, dispatcher, value));
        }
    }
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<HandlerId, IHandler>> _types = new();
    private readonly ConcurrentDictionary<HandlerId, Type> _ids = new();
    private readonly ConcurrentDictionary<Delegate, HandlerId> _delegates = new();
    private readonly ConditionalWeakTable<ISession, SemaphoreSlim> _requestGates = new();
    private int _nextId;

    public HandlerId AddHandler<T>(Action<MessageContext<T>> handler, TaskScheduler? scheduler = null)
    {
        if (scheduler != null) throw new NotSupportedException("Use the actor mailbox for scheduling.");
        var id = new HandlerId(Interlocked.Increment(ref _nextId));
        if (!_delegates.TryAdd(handler, id)) throw new InvalidOperationException("Handler already registered.");
        _ids[id] = typeof(T);
        _types.GetOrAdd(typeof(T), static _ => new())[id] = new Handler<T>(handler);
        return id;
    }

    public bool RemoveHandler<T>(Action<MessageContext<T>> handler) =>
        _delegates.TryGetValue(handler, out var id) && RemoveHandler(id);

    public bool RemoveHandler(HandlerId id)
    {
        if (!_ids.TryRemove(id, out var type) || !_types[type].TryRemove(id, out var handler)) return false;
        _delegates.TryRemove(handler.Callback, out _);
        return true;
    }

    public void Dispatch(ISession session, ReadOnlySequence<byte> buffer)
    {
        var message = codec.Decode(buffer);
        if (message != null) Dispatch(session, message.GetType(), message);
    }
    public void Dispatch<T>(ISession session, T message) where T : class => Dispatch(session, typeof(T), message);
    public void Dispatch(ISession session, Type type, object message)
    {
        if (!_types.TryGetValue(type, out var handlers)) return;
        foreach (var handler in handlers.Values)
            try { handler.Invoke(this, session, message); }
            catch (Exception exception) { logger.LogError(exception, "Transport message handler failed"); SessionHealth.Close(session); }
    }

    public async Task<T?> HandleOnce<T>(ISession session, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = AddHandler<T>(ctx =>
        {
            if (ReferenceEquals(ctx.FromSession, session)) completion.TrySetResult(ctx.Message);
        });
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try { return await completion.Task; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return default; }
        finally { RemoveHandler(id); }
    }

    public async Task<TResp?> SendAndListenOnce<TReq, TResp>(ISession session, TReq message, CancellationToken cancellationToken = default)
    {
        var gate = _requestGates.GetValue(session, static _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        using var listenLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var response = HandleOnce<TResp>(session, listenLifetime.Token);
            try
            {
                if (!await SendAsync(session, message, cancellationToken)) return default;
                return await response;
            }
            finally { listenLifetime.Cancel(); await response; }
        }
        finally { gate.Release(); }
    }

    public async ValueTask<bool> SendAsync<T>(ISession session, T message, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        codec.Encode(message, stream);
        return await session.TrySendAsync(stream, cancellationToken);
    }
}
