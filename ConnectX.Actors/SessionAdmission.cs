using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Actors;

/// <summary>Actor-owned login state. Promotion is a one-time transition for the exact connection.</summary>
public sealed class SessionAdmission(TimeProvider timeProvider)
{
    private readonly Dictionary<SessionId, (long AcceptedAt, ISession Session)> _pending = [];

    public ISession? Accept(ISession session)
    {
        var replaced = _pending.GetValueOrDefault(session.Id).Session;
        _pending[session.Id] = (timeProvider.GetTimestamp(), session);
        return replaced;
    }

    public bool TryPromote(ISession session)
    {
        if (!_pending.TryGetValue(session.Id, out var pending) || !ReferenceEquals(pending.Session, session)) return false;
        return _pending.Remove(session.Id);
    }

    public ISession[] RemoveExpired(TimeSpan timeout)
    {
        var now = timeProvider.GetTimestamp();
        var expired = _pending.Where(x => timeProvider.GetElapsedTime(x.Value.AcceptedAt, now) >= timeout || !SessionHealth.IsConnected(x.Value.Session)).ToArray();
        foreach (var item in expired) _pending.Remove(item.Key);
        return expired.Select(x => x.Value.Session).ToArray();
    }

    public ISession[] Drain()
    {
        var sessions = _pending.Values.Select(x => x.Session).ToArray();
        _pending.Clear();
        return sessions;
    }
}
