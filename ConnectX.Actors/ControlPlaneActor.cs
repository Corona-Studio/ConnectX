using System.Threading.Channels;
using Hive.Both.General.Dispatchers;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Actors;

/// <summary>
/// Owns all control state in one process. Commands run to completion without interleaving.
/// Network callbacks use TryPost: overload rejects the connection instead of retaining
/// an unbounded number of waiting producers. Local producers can await admission.
/// </summary>
public sealed class ControlPlaneActor(ILogger<ControlPlaneActor> logger) : BackgroundService
{
    private readonly Channel<IActorCommand> _mailbox = Channel.CreateBounded<IActorCommand>(
        new BoundedChannelOptions(4096) { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly ActorSynchronizationContext _context = new("ConnectX control actor");
    private bool _inTurn;
    private readonly CancellationTokenSource _ioLifetime = new();
    private SessionTransportHub? _transport;
    private SessionTransportHub Transport => _transport ??= new SessionTransportHub(_ioLifetime.Token, logger);
    private readonly HashSet<Task> _effects = [];
    private readonly SemaphoreSlim _effectSlots = new(32);
    private readonly HashSet<Task> _sessionTasks = [];
    public CancellationToken Lifetime => _ioLifetime.Token;

    public void ObserveSession(ISession session)
    {
        AssertAccess();
        _sessionTasks.RemoveWhere(task => task.IsCompleted);
        using (ExecutionContext.SuppressFlow()) _sessionTasks.Add(Transport.ObserveAsync(session));
    }

    public void AssertAccess()
    {
        if (!_context.IsCurrent || !_inTurn) throw new InvalidOperationException("Control state must be accessed through its actor mailbox.");
    }

    public bool TryPost(IActorCommand command) => _mailbox.Writer.TryWrite(command);

    public bool TryPost(Action action) => TryPost(new ActionCommand(action));

    public async Task<T> AskAsync<T>(Func<T> query, CancellationToken cancellationToken = default)
    {
        if (_context.IsCurrent) throw new InvalidOperationException("An actor turn cannot ask itself; call the state module directly.");
        var command = new QueryCommand<T>(query);
        await _mailbox.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        AskAsync(() => { action(); return true; }, cancellationToken);

    public void PrepareOutput(ISession session) { AssertAccess(); Transport.Prepare(session); }

    public void Send<T>(IDispatcher dispatcher, ISession session, T message)
    {
        AssertAccess();
        Transport.Send(dispatcher, session, message);
    }

    public void SendAndClose<T>(IDispatcher dispatcher, ISession session, T message)
    {
        AssertAccess();
        Transport.Send(dispatcher, session, message, closeAfter: true);
    }

    // Raw forwarding is an I/O boundary operation against an immutable route snapshot.
    public void SendRaw(ISession session, byte[] payload) => _transport?.SendRaw(session, payload);

    public void Close(ISession session) { AssertAccess(); Transport.Close(session); }

    public void ReapCompletedIO()
    {
        AssertAccess();
        Transport.Reap();
        _sessionTasks.RemoveWhere(task => task.IsCompleted);
        _effects.RemoveWhere(task => task.IsCompleted);
    }

    /// <summary>Bounded external work; completion must re-enter via InvokeAsync/AskAsync.</summary>
    public bool RunEffect(Func<CancellationToken, Task> effect)
    {
        AssertAccess();
        _effects.RemoveWhere(task => task.IsCompleted);
        if (_ioLifetime.IsCancellationRequested || !_effectSlots.Wait(0)) return false;
        // Suppress the actor ownership marker in external work and its continuations.
        using (ExecutionContext.SuppressFlow())
        {
            _effects.Add(Task.Run(async () =>
            {
                try { await effect(_ioLifetime.Token); }
                catch (OperationCanceledException) when (_ioLifetime.IsCancellationRequested) { }
                catch (Exception exception) { logger.LogEffectFailed(exception); }
                finally { _effectSlots.Release(); }
            }));
        }
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _context.RunAsync(() => ConsumeAsync(stoppingToken));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var command in _mailbox.Reader.ReadAllAsync(stoppingToken))
            {
                _inTurn = true;
                try { await command.ExecuteAsync(stoppingToken); }
                catch (Exception exception)
                {
                    logger.LogCommandFailed(exception, command.GetType().Name);
                    command.Fail(exception);
                }
                finally { _inTurn = false; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            while (_mailbox.Reader.TryRead(out var command))
                command.Fail(new OperationCanceledException("Actor stopped."));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // All ingress hosted services stop before this service (reverse registration order).
        _ioLifetime.Cancel();
        var work = await AskAsync(() => (_sessionTasks.Concat(_effects).ToArray(), Transport.StopAsync()), cancellationToken).ConfigureAwait(false);
        // Effects can still enqueue their final completion; drain them before sealing the mailbox.
        await Task.WhenAll(work.Item1).WaitAsync(cancellationToken).ConfigureAwait(false);
        await work.Item2.WaitAsync(cancellationToken).ConfigureAwait(false);
        _mailbox.Writer.TryComplete();
        if (ExecuteTask != null) await ExecuteTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _mailbox.Writer.TryComplete();
        _ioLifetime.Cancel();
        base.Dispose();
    }

    private sealed class ActionCommand(Action action) : IActorCommand
    {
        public ValueTask ExecuteAsync(CancellationToken _) { action(); return ValueTask.CompletedTask; }
        public void Fail(Exception exception) { }
    }

    private sealed class QueryCommand<T>(Func<T> query) : IActorCommand
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask ExecuteAsync(CancellationToken _) { Completion.TrySetResult(query()); return ValueTask.CompletedTask; }
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}

internal static partial class ActorLoggers
{
    [LoggerMessage(LogLevel.Error, "Actor command {command} failed")]
    public static partial void LogCommandFailed(this ILogger logger, Exception exception, string command);
    [LoggerMessage(LogLevel.Error, "Actor external effect failed")]
    public static partial void LogEffectFailed(this ILogger logger, Exception exception);
    [LoggerMessage(LogLevel.Warning, "Session outbox failed; closing connection")]
    public static partial void LogOutboxFailed(this ILogger logger, Exception exception);
}
