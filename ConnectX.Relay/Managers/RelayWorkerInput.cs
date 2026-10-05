using System.Buffers;
using System.Runtime.CompilerServices;
using ConnectX.Actors;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Relay.Managers;

/// <summary>
/// I/O boundary for one worker stream. Borrowed streams retain their pipe read
/// until the reverse handshake is ready. Legacy streams use a bounded startup buffer.
/// Business routing decisions are made exclusively by the control actor.
/// </summary>
internal sealed class RelayWorkerInput(ControlPlaneActor actor, ISession source)
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private ISession? _destination;
    private bool _closed;
    private TaskCompletionSource _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? _outputReady;

    public void ConnectTo(ISession? destination)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (ReferenceEquals(_destination, destination)) return;
            _destination = destination;
            if (source is IBorrowedBufferSession)
            {
                if (destination == null)
                {
                    _outputReady = null;
                    _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    // The acknowledgement must leave the outbox before raw data.
                    _outputReady = actor.FlushPendingOutputAsync(destination);
                    _routeReady.TrySetResult();
                }
                return;
            }
            if (destination != null)
                while (_pending.TryDequeue(out var payload)) actor.SendRaw(destination, payload);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask ReceiveAsync(ISession session, ReadOnlySequence<byte> buffer, CancellationToken token)
    {
        while (true)
        {
            Task routeReady;
            Task<bool>? outputReady;
            ISession? destination;
            lock (_gate)
            {
                if (_closed) return;
                routeReady = _routeReady.Task;
                destination = _destination;
                outputReady = _outputReady;
            }
            if (destination == null)
            {
                // Keep the source pipe read alive while the reverse handshake arrives.
                await routeReady.WaitAsync(token).ConfigureAwait(false);
                continue;
            }
            if (outputReady == null || !await outputReady.WaitAsync(token).ConfigureAwait(false))
            {
                SessionHealth.Close(source);
                return;
            }
            if (destination is not IBorrowedBufferSession borrowed ||
                !await borrowed.TrySendAsync(buffer, token).ConfigureAwait(false))
                SessionHealth.Close(source);
            return;
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
        lock (_gate) { _closed = true; _destination = null; _pending.Clear(); _routeReady.TrySetResult(); }
    }
}
