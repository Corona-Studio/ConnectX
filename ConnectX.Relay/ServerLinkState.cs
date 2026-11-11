using Hive.Network.Abstractions.Session;

namespace ConnectX.Relay;

/// <summary>A single atomically published snapshot of the relay registration handshake.</summary>
internal closed class ServerLinkState
{
    public static ServerLinkState Disconnected { get; } = new Offline();

    public ISession? Session => this switch
    {
        Offline => null,
        Handshaking state => state.Connection,
        Registered state => state.Connection
    };

    public bool IsReady => this switch
    {
        Offline => false,
        Handshaking => false,
        Registered => true
    };

    private sealed class Offline : ServerLinkState;

    public sealed class Handshaking(ISession connection) : ServerLinkState
    {
        public ISession Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public sealed class Registered(ISession connection) : ServerLinkState
    {
        public ISession Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));
    }
}
