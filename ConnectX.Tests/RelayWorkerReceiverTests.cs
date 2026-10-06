using System.Buffers;
using ConnectX.Client.Transmission.Connections;
using ConnectX.Shared.Messages.Relay;
using Hive.Codec.Abstractions;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class RelayWorkerReceiverTests
{
    [Test]
    public async Task FramesImmediatelyFollowingAckAreReceivedBeforeEstablishmentResumes()
    {
        var session = new TestSession(1);
        var codec = new HandshakeCodec();
        var received = new List<byte[]>();
        using var receiver = new RelayWorkerReceiver(session, codec, (_, frame) => received.Add(frame.ToArray()));
        session.OnSend = () =>
        {
            session.Receive([1]);
            session.Receive([2, 3]);
            session.Receive([4, 5]);
            Assert.That(received, Has.Count.EqualTo(2));
        };
        await receiver.EstablishAsync(new() { UserId = Guid.NewGuid(), RelayTo = Guid.NewGuid(), RoomId = Guid.NewGuid() }, CancellationToken.None);
        Assert.That(received, Is.EqualTo(new[] { new byte[] { 2, 3 }, new byte[] { 4, 5 } }));
        Assert.That(codec.DecodeCount, Is.EqualTo(1), "Stream frames must bypass packet decoding.");
        receiver.Dispose();
        session.Receive([6]);
        Assert.That(received, Has.Count.EqualTo(2));
    }

    [Test]
    public void CancelledHandshakeClosesSessionAndDetachesReceiver()
    {
        var session = new TestSession(2);
        var codec = new HandshakeCodec();
        using var receiver = new RelayWorkerReceiver(session, codec, (_, _) => Assert.Fail("Unexpected stream."));
        using var cancellation = new CancellationTokenSource();
        var establishment = receiver.EstablishAsync(new() { UserId = Guid.NewGuid(), RelayTo = Guid.NewGuid(), RoomId = Guid.NewGuid() }, cancellation.Token);
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await establishment);
        session.Receive([1]);
        Assert.That(session.Closed, Is.True);
        Assert.That(codec.DecodeCount, Is.Zero);
    }

    private sealed class HandshakeCodec : IPacketCodec
    {
        public int DecodeCount { get; private set; }
        public int Encode<T>(T message, Stream stream) { stream.WriteByte(0); return 1; }
        public object Decode(ReadOnlySequence<byte> buffer)
        {
            DecodeCount++;
            return new RelayWorkerLinkCreatedMessage();
        }
    }
}
