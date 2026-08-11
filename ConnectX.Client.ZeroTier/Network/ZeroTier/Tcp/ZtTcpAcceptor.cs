using System.Net;
using System.Net.Sockets;
using Hive.Network.Abstractions;
using Hive.Network.Shared.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Socket = ZeroTier.Sockets.Socket;

namespace ConnectX.Client.Network.ZeroTier.Tcp;

public sealed class ZtTcpAcceptor : AbstractAcceptor<ZtTcpSession>
{
    private readonly ObjectFactory<ZtTcpSession> _sessionFactory;
    private Socket? _serverSocket;

    public ZtTcpAcceptor(
        IServiceProvider serviceProvider,
        ILogger<ZtTcpAcceptor> logger)
        : base(serviceProvider, logger)
    {
        _sessionFactory = ActivatorUtilities.CreateFactory<ZtTcpSession>([typeof(int), typeof(bool), typeof(Socket)]);
    }

    public override IPEndPoint? EndPoint => _serverSocket?.LocalEndPoint as IPEndPoint;
    public override bool IsValid => _serverSocket != null;

    private void InitSocket(IPEndPoint listenEndPoint)
    {
        _serverSocket = new Socket(listenEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    }

    public override Task SetupAsync(IPEndPoint listenEndPoint, CancellationToken token)
    {
        if (_serverSocket == null)
            InitSocket(listenEndPoint);

        if (_serverSocket == null)
            throw new NullReferenceException("ServerSocket is null and InitSocket failed.");

        _serverSocket.Bind(listenEndPoint);
        _serverSocket.Listen(listenEndPoint.Port);

        return Task.CompletedTask;
    }

    public override Task<bool> TryCloseAsync(CancellationToken token)
    {
        if (_serverSocket == null) return Task.FromResult(false);

        _serverSocket.Close();
        _serverSocket = null;

        return Task.FromResult(true);
    }


    public override ValueTask<bool> TryDoOnceAcceptAsync(CancellationToken token)
    {
        if (_serverSocket == null)
            return ValueTask.FromResult(false);

        var acceptSocket = _serverSocket.Accept();
        CreateSession(acceptSocket);

        return ValueTask.FromResult(true);
    }

    private void CreateSession(Socket acceptSocket)
    {
        var sessionId = GetNextSessionId();
        var clientSession = _sessionFactory.Invoke(ServiceProvider, [sessionId, true, acceptSocket]);
        clientSession.OnSocketError += OnSocketError;
        FireOnSessionCreate(clientSession);
    }

    private void OnSocketError(object? sender, SocketError e)
    {
        if (sender is not ZtTcpSession session) return;

        Logger.LogSocketError(session.Id, e);
        session.Close();
        FireOnSessionClosed(session);
    }

    public override void Dispose()
    {
        _serverSocket?.Close();
    }
}

internal static partial class ZeroTierAcceptorLoggers
{
    [LoggerMessage(LogLevel.Debug, "Session {sessionId} socket error: {socketError}")]
    public static partial void LogSocketError(this ILogger logger, SessionId sessionId, SocketError socketError);
}