using ConnectX.Actors;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class ActorTests
{
    [Test]
    public async Task AwaitKeepsThreadAndContextAndDoesNotInterleaveTurns()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var first = new AwaitCommand(actor);
        Assert.That(actor.TryPost(first), Is.True);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var next = actor.AskAsync(() => 42);
        Assert.That(next.IsCompleted, Is.False);
        first.Resume.SetResult();
        await first.Done.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(await next, Is.EqualTo(42));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.StopAsync(timeout.Token);
    }

    [Test]
    public async Task SeparateActorsHaveSeparateContextsAndRejectForeignAccess()
    {
        using var a = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        using var b = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await a.StartAsync(default); await b.StartAsync(default);
        var aThread = await a.AskAsync(() => Environment.CurrentManagedThreadId);
        var bThread = await b.AskAsync(() => Environment.CurrentManagedThreadId);
        Assert.That(aThread, Is.Not.EqualTo(bThread));
        Assert.Throws<InvalidOperationException>(a.AssertAccess);
        await a.InvokeAsync(() => Assert.Throws<InvalidOperationException>(b.AssertAccess));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await a.StopAsync(timeout.Token); await b.StopAsync(timeout.Token);
    }

    [Test]
    public async Task FullMailboxRejectsWithoutCreatingWaitingTasks()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var hold = new AwaitCommand(actor);
        actor.TryPost(hold); await hold.Entered.Task.ConfigureAwait(false);
        for (var i = 0; i < 4096; i++) Assert.That(actor.TryPost(() => { }), Is.True);
        Assert.That(actor.TryPost(() => { }), Is.False);
        hold.Resume.SetResult(); await hold.Done.Task.ConfigureAwait(false);
        await actor.InvokeAsync(() => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.StopAsync(timeout.Token);
        Assert.That(actor.TryPost(() => { }), Is.False);
    }

    [Test]
    public async Task GeneratedBindingsRunInContextAndDisposeOnlyTheirOwnHandlers()
    {
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var dispatcher = new ActorDispatcher(new TestCodec(), NullLogger<ActorDispatcher>.Instance);
        using var probe = new GeneratedProbe(dispatcher, actor);
        var session = new TestSession(1);
        dispatcher.Dispatch(session, new ProbeMessage());
        await probe.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var retained = 0;
        dispatcher.AddHandler<ProbeMessage>(_ => retained++);
        probe.Dispose();
        dispatcher.Dispatch(session, new ProbeMessage());
        Assert.That(retained, Is.EqualTo(1));
        await actor.StopAsync(default);
    }

    private sealed class AwaitCommand(ControlPlaneActor actor) : IActorCommand
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ExecuteAsync(CancellationToken token)
        {
            actor.AssertAccess();
            var thread = Environment.CurrentManagedThreadId;
            var context = SynchronizationContext.Current;
            Entered.SetResult();
            await Resume.Task;
            actor.AssertAccess();
            Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(thread));
            Assert.That(SynchronizationContext.Current, Is.SameAs(context));
            Done.SetResult();
        }
        public void Fail(Exception exception) => Done.TrySetException(exception);
    }
}

public sealed class ProbeMessage;
public partial class GeneratedProbe
{
    private readonly ControlPlaneActor _actor;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public GeneratedProbe(IDispatcher dispatcher, ControlPlaneActor actor)
    {
        _actor = actor; RegisterActorHandlers(dispatcher, actor);
    }
    [ActorMessage]
    private async Task OnMessage(MessageContext<ProbeMessage> context)
    {
        _actor.AssertAccess();
        await Task.Delay(1);
        _actor.AssertAccess();
        Completion.TrySetResult();
    }
}
