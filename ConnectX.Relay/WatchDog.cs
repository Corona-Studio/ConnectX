using Hive.Network.Abstractions.Session;

namespace ConnectX.Relay;

/// <summary>Monotonic liveness clock, read and updated only by the control actor.</summary>
public sealed class WatchDog(ISession session, TimeProvider timeProvider)
{
    public const int MaxHeartbeatInterval = 15;
    private long _lastHeartbeat = timeProvider.GetTimestamp();
    public ISession Session { get; } = session;
    public void Received() => _lastHeartbeat = timeProvider.GetTimestamp();
    public bool IsTimeoutExceeded() => timeProvider.GetElapsedTime(_lastHeartbeat) > TimeSpan.FromSeconds(MaxHeartbeatInterval);
}
