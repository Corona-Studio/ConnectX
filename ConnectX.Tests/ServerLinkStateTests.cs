using ConnectX.Relay;
using NUnit.Framework;

namespace ConnectX.Tests;

[TestFixture]
public sealed class ServerLinkStateTests
{
    [Test]
    public void HandshakeExposesSessionButPublishesReadinessOnlyAfterRegistration()
    {
        var session = new TestSession(1);
        Assert.Multiple(() =>
        {
            Assert.That(ServerLinkState.Disconnected.Session, Is.Null);
            Assert.That(ServerLinkState.Disconnected.IsReady, Is.False);
        });

        ServerLinkState state = new ServerLinkState.Handshaking(session);
        Assert.Multiple(() =>
        {
            Assert.That(state.Session, Is.SameAs(session));
            Assert.That(state.IsReady, Is.False);
        });

        state = new ServerLinkState.Registered(session);
        Assert.Multiple(() =>
        {
            Assert.That(state.Session, Is.SameAs(session));
            Assert.That(state.IsReady, Is.True);
        });
    }

    [Test]
    public void ConnectedStatesRejectMissingSession()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerLinkState.Handshaking(null!));
        Assert.Throws<ArgumentNullException>(() => new ServerLinkState.Registered(null!));
    }
}
