using System.Buffers;
using ConnectX.Actors;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Relay.Managers;

/// <summary>
/// I/O boundary for one worker stream. A bounded startup buffer bridges the two
/// handshakes; publishing the destination drains it atomically with later frames.
/// Business routing decisions are made exclusively by the control actor.
/// </summary>
internal sealed class RelayWorkerInput(ControlPlaneActor actor, ISession source)
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private ISession? _destination;
    private bool _closed;

    public void ConnectTo(ISession? destination)
    {
        lock (_gate)
        {
            if (_closed) return;
            _destination = destination;
            if (destination != null)
                while (_pending.TryDequeue(out var payload)) actor.SendRaw(destination, payload);
        }
    }

    public void Receive(ISession session, ReadOnlySequence<byte> buffer)
    {
        // Hive owns receive buffers only for this callback.
        var payload = buffer.ToArray();
        var overflow = false;
        lock (_gate)
        {
            if (_closed) return;
            if (_destination != null) actor.SendRaw(_destination, payload);
            else if (_pending.Count < 128) _pending.Enqueue(payload);
            else { _closed = true; _pending.Clear(); overflow = true; }
        }
        if (overflow) SessionHealth.Close(source);
    }

    public void Close()
    {
        lock (_gate) { _closed = true; _destination = null; _pending.Clear(); }
    }
}
