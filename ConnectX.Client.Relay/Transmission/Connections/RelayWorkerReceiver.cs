using System.Buffers;
using ConnectX.Shared.Messages.Relay;
using Hive.Codec.Abstractions;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Client.Transmission.Connections;

/// <summary>Consumes the acknowledgement and subsequent stream frames in the same receive callback.</summary>
internal sealed class RelayWorkerReceiver : IDisposable
{
    private readonly ISession _session;
    private readonly IPacketCodec _codec;
    private readonly Action<ISession, ReadOnlySequence<byte>> _receive;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _streaming;
    private bool _disposed;

    public RelayWorkerReceiver(ISession session, IPacketCodec codec, Action<ISession, ReadOnlySequence<byte>> receive)
    {
        _session = session;
        _codec = codec;
        _receive = receive;
        session.OnMessageReceived += Receive;
    }

    public async Task EstablishAsync(CreateRelayWorkerLinkMessage request, CancellationToken token)
    {
        try
        {
            using var stream = new MemoryStream();
            _codec.Encode(request, stream);
            if (!await _session.TrySendAsync(stream, token)) throw new IOException("Failed to send relay worker handshake.");
            await _ready.Task.WaitAsync(token);
        }
        catch
        {
            Dispose();
            _session.Close();
            throw;
        }
    }

    private void Receive(ISession session, ReadOnlySequence<byte> buffer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_streaming)
            {
                _receive(session, buffer);
                return;
            }
            try
            {
                if (_codec.Decode(buffer) is not RelayWorkerLinkCreatedMessage)
                    throw new IOException("Unexpected relay worker handshake response.");
                // Publish receive readiness before waking the asynchronous establishment continuation.
                _streaming = true;
                _ready.TrySetResult();
            }
            catch (Exception error)
            {
                _ready.TrySetException(error);
                Dispose();
                session.Close();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session.OnMessageReceived -= Receive;
            _ready.TrySetCanceled();
        }
    }
}
