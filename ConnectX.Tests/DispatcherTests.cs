using ConnectX.Actors;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class DispatcherTests
{
    [Test]
    public async Task OneShotReplyIgnoresOtherConnectionsAndDuplicateReplies()
    {
        var dispatcher = new ActorDispatcher(new TestCodec(), NullLogger<ActorDispatcher>.Instance);
        var expected = new TestSession(1); var stranger = new TestSession(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = dispatcher.HandleOnce<ProbeMessage>(expected, timeout.Token);
        dispatcher.Dispatch(stranger, new ProbeMessage());
        Assert.That(response.IsCompleted, Is.False);
        dispatcher.Dispatch(expected, new ProbeMessage());
        dispatcher.Dispatch(expected, new ProbeMessage());
        Assert.That(await response, Is.Not.Null);
        timeout.Cancel(); // Must not try to complete a task that already has a result.
    }

    [Test]
    public async Task RequestsOnSameConnectionAreSerializedWithoutCorrelationIds()
    {
        var codec = new TestCodec();
        var dispatcher = new ActorDispatcher(codec, NullLogger<ActorDispatcher>.Instance);
        var session = new TestSession(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var a = dispatcher.SendAndListenOnce<ProbeMessage, ProbeMessage>(session, new(), timeout.Token);
        var b = dispatcher.SendAndListenOnce<ProbeMessage, ProbeMessage>(session, new(), timeout.Token);
        Assert.That(codec.Encoded.Count, Is.EqualTo(1));
        dispatcher.Dispatch(session, new ProbeMessage());
        await a;
        for (var i = 0; i < 100 && codec.Encoded.Count < 2; i++) await Task.Delay(10);
        Assert.That(b.IsCompleted, Is.False);
        dispatcher.Dispatch(session, new ProbeMessage());
        Assert.That(await b, Is.Not.Null);
    }

    [Test]
    public async Task OutboxPreservesOrderAndSlowConnectionDoesNotBlockActor()
    {
        var codec = new TestCodec();
        var dispatcher = new ActorDispatcher(codec, NullLogger<ActorDispatcher>.Instance);
        using var actor = new ControlPlaneActor(NullLogger<ControlPlaneActor>.Instance);
        await actor.StartAsync(default);
        var blocked = new TestSession(1) { BlockSends = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var fast = new TestSession(2);
        await actor.InvokeAsync(() =>
        {
            actor.Send(dispatcher, blocked, "blocked");
            actor.Send(dispatcher, fast, "first");
            actor.Send(dispatcher, fast, "second");
        });
        Assert.That(await actor.AskAsync(() => 1), Is.EqualTo(1));
        for (var i = 0; i < 100 && fast.Sent.Count < 2; i++) await Task.Delay(10);
        Assert.That(fast.Sent.Count, Is.EqualTo(2));
        var messages = codec.Encoded.OfType<string>().Where(x => x != "blocked").ToArray();
        Assert.That(messages, Is.EqualTo(new[] { "first", "second" }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.StopAsync(timeout.Token);
        Assert.That(blocked.Closed, Is.True);
    }

    [Test]
    public void AdmissionIsOneShotAndCannotBeClaimedByReplacedConnection()
    {
        var clock = new TestClock();
        var admission = new SessionAdmission(clock);
        var original = new TestSession(1); var replacement = new TestSession(1);
        admission.Accept(original); admission.Accept(replacement);
        Assert.That(admission.TryPromote(original), Is.False);
        Assert.That(admission.TryPromote(replacement), Is.True);
        Assert.That(admission.TryPromote(replacement), Is.False);
        admission.Accept(original);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.That(admission.RemoveExpired(TimeSpan.FromMinutes(10)), Is.EqualTo(new[] { original }));
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
