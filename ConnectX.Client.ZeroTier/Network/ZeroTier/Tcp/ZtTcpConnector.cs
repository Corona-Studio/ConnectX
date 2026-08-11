using System.Net;
using System.Net.Sockets;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Socket = ZeroTier.Sockets.Socket;
using SocketException = ZeroTier.Sockets.SocketException;

namespace ConnectX.Client.Network.ZeroTier.Tcp;

public sealed class ZtTcpConnector : IConnector<ZtTcpSession>
{
    private readonly ILogger<ZtTcpConnector> _logger;
    private readonly IServiceProvider _serviceProvider;
    private int _currentSessionId;

    public ZtTcpConnector(
        ILogger<ZtTcpConnector> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public ValueTask<ZtTcpSession?> ConnectAsync(IPEndPoint remoteEndPoint, CancellationToken token = default)
    {
        try
        {
            var socket = new Socket(remoteEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(remoteEndPoint);

            var session =
                ActivatorUtilities.CreateInstance<ZtTcpSession>(_serviceProvider, GetNextSessionId(), false, socket);

            return ValueTask.FromResult(session)!;
        }
        catch (SocketException e)
        {
            _logger.LogConnectFailed(e, remoteEndPoint);
            return ValueTask.FromResult<ZtTcpSession?>(null);
        }
        catch (Exception e)
        {
            _logger.LogConnectFailed(e, remoteEndPoint);
            throw;
        }
    }

    public int GetNextSessionId()
    {
        return Interlocked.Increment(ref _currentSessionId);
    }
}

internal static partial class TcpConnectorLoggers
{
    [LoggerMessage(LogLevel.Error, "[TCP_CONN] Connect to {RemoteEndPoint} failed")]
    public static partial void LogConnectFailed(this ILogger logger, Exception ex, IPEndPoint remoteEndPoint);
}