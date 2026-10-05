using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Hive.Network.Abstractions.Session;
using System.Net;
using System.Net.Sockets;
using Hive.Network.Shared;
using Hive.Network.Shared.Session;
using Microsoft.Extensions.Logging;
using Socket = ZeroTier.Sockets.Socket;

namespace ConnectX.Client.Network.ZeroTier.Tcp;

public sealed class ZtTcpSession : AbstractSession, IWritableFrameSession
{
    private readonly bool _isAcceptedSocket;
    private bool _closed;

    public ZtTcpSession(
        int sessionId,
        bool isAcceptedSocket,
        Socket socket,
        ILogger<ZtTcpSession> logger)
        : base(sessionId, logger)
    {
        _isAcceptedSocket = isAcceptedSocket;

        Socket = socket;
        _localEndPoint = socket.LocalEndPoint as IPEndPoint;
        _remoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
    }

    private readonly IPEndPoint? _localEndPoint;
    private readonly IPEndPoint? _remoteEndPoint;
    public Socket? Socket { get; private set; }

    public override IPEndPoint? LocalEndPoint => Socket == null ? null : _localEndPoint;

    public override IPEndPoint? RemoteEndPoint => Socket == null ? null : _remoteEndPoint;

    public override bool CanSend => IsConnected;

    public override bool CanReceive => IsConnected;

    public override bool IsConnected => (_isAcceptedSocket && !_closed) || Socket is { Connected: true };

    public event EventHandler<SocketError>? OnSocketError;

    public ValueTask<bool> TrySendFrameAsync(Memory<byte> frame, CancellationToken token = default)
        => SendWritableFrameAsync(frame, token);

    public ValueTask<bool> TrySendAsync(ReadOnlySequence<byte> payload, CancellationToken token = default)
        => SendBorrowedSequenceAsync(payload, token);

    public override ValueTask<int> SendOnce(ArraySegment<byte> data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        token.ThrowIfCancellationRequested();
        var len = Socket.Send(data.Array!, data.Offset, data.Count, SocketFlags.None);

        if (len == 0)
            OnSocketError?.Invoke(this, SocketError.ConnectionReset);

        return ValueTask.FromResult(len);
    }

    protected override async Task FillReceivePipeAsync(PipeWriter writer, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        using var pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = Socket;
                if (socket == null) break;
                if (!socket.Poll(0, SelectMode.SelectRead))
                {
                    await pollTimer.WaitForNextTickAsync(token);
                    continue;
                }

                var memory = writer.GetMemory(NetworkSettings.DefaultBufferSize);
                if (!MemoryMarshal.TryGetArray<byte>(memory, out var segment))
                    throw new InvalidOperationException("ZeroTier requires array-backed receive memory.");
                var receiveLen = await ReceiveOnce(segment, token);
                if (receiveLen <= 0) break;

                Logger.LogDataReceived(RemoteEndPoint!, receiveLen);
                writer.Advance(receiveLen);
                var flush = await writer.FlushAsync(token);
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    public override ValueTask<int> ReceiveOnce(ArraySegment<byte> buffer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(Socket);

        token.ThrowIfCancellationRequested();
        var len = Socket.Receive(buffer.Array!, buffer.Offset, buffer.Count, SocketFlags.None);

        return ValueTask.FromResult(len);
    }

    public override void Close()
    {
        base.Close();

        _closed = true;
        IsConnected = false;
        Socket?.Close();
        Socket = null;
    }
}

internal static partial class ZtTcpSessionLoggers
{
    [LoggerMessage(LogLevel.Trace, "Payload received from [{endPoint}] with length [{length}]")]
    public static partial void LogDataReceived(this ILogger logger, IPEndPoint endPoint, int length);
}