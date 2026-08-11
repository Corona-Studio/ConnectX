using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Network.ZeroTier.Tcp;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Socket = ZeroTier.Sockets.Socket;
using SocketException = ZeroTier.Sockets.SocketException;

namespace ConnectX.Client.P2P.LinkMaker;

/// <summary>
///     基于 ZeroTier 的 TCP 单端口连接建立器
///     如果是主动方，那么就是接受远端；如果是被动方，那么就是连接远端
/// </summary>
/// <returns></returns>
public sealed class ZtTcpSinglePortLinkMaker(
    bool isInitiator,
    long startTimeTick,
    Guid partnerId,
    IPAddress localAddress,
    ushort localPort,
    IPEndPoint remoteIpe,
    CancellationToken cancellationToken,
    IServiceProvider serviceProvider,
    ILogger<ZtTcpSinglePortLinkMaker> logger)
    : SingleToSingleLinkMaker(
        serviceProvider,
        logger,
        startTimeTick,
        partnerId,
        localAddress,
        localPort,
        remoteIpe,
        cancellationToken)
{
    private const int DefaultTryTime = 10;
    public Socket? ZtServerSocket { get; private set; }

    private async Task<ISession?> AcceptConnAsync()
    {
        ZtTcpSession? acceptedLink = null;

        var tryTime = DefaultTryTime;
        var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        receiveSocket.Bind(new IPEndPoint(IPAddress.Any, LocalPort));
        receiveSocket.Listen(DefaultTryTime);

        ZtServerSocket = receiveSocket;

        Logger.LogStartTime(new DateTime(StartTimeTick).ToLongTimeString());

        await Task.Run(async () =>
        {
            logger.LogStartedToAcceptP2PConnection();

            while (!Token.IsCancellationRequested && tryTime > 0)
            {
                Socket conSocket;

                try
                {
                    tryTime--;

                    conSocket = receiveSocket.Accept();
                    acceptedLink = ActivatorUtilities.CreateInstance<ZtTcpSession>(
                        ServiceProvider,
                        0,
                        true,
                        conSocket);

                    ArgumentNullException.ThrowIfNull(acceptedLink);
                }
                catch (ArgumentNullException)
                {
                    continue;
                }
                catch (SocketException e)
                {
                    Logger.LogFailedToAcceptConnection(e, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogFailedToAcceptConnectionWithoutRetry();
                        InvokeOnFailed();
                    }

                    await Task.Delay(3000, Token);

                    continue;
                }

                logger.LogConnectionAccepted((IPEndPoint)conSocket.RemoteEndPoint);
                break;
            }
        }, CancellationToken.None);

        if (acceptedLink == null)
        {
            InvokeOnFailed();
            return null;
        }

        Logger.LogConnectionEstablished();
        Logger.LogEstablishSessionInfo("Accepted", acceptedLink.LocalEndPoint, acceptedLink.RemoteEndPoint);

        InvokeOnConnected(acceptedLink);

        return acceptedLink;
    }

    private async Task<ISession?> ConnectToRemoteAsync()
    {
        ZtTcpSession? connectLink = null;

        var tryTime = DefaultTryTime;

        Logger.LogStartTime(new DateTime(StartTimeTick).ToLongTimeString());

        await Task.Run(async () =>
        {
            var receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            Logger.LogStartedToTryTcpConnectionWithRemoteIpe(LocalPort, RemoteIpe);

            await TaskHelper.WaitUtil(() => !Token.IsCancellationRequested &&
                                            DateTime.UtcNow.Ticks < StartTimeTick, Token);

            while (!Token.IsCancellationRequested && tryTime > 0)
            {
                try
                {
                    tryTime--;

                    receiveSocket.Connect(RemoteIpe);
                    connectLink = ActivatorUtilities.CreateInstance<ZtTcpSession>(
                        ServiceProvider,
                        0,
                        false,
                        receiveSocket);

                    ArgumentNullException.ThrowIfNull(connectLink);
                }
                catch (ArgumentNullException)
                {
                    continue;
                }
                catch (SocketException e)
                {
                    Logger.LogFailedToConnectToRemoteIpe(e, LocalPort, RemoteIpe, tryTime);

                    if (tryTime == 0)
                    {
                        Logger.LogFailedToConnectToRemoteIpe(RemoteIpEndPoint);
                        InvokeOnFailed();

                        break;
                    }

                    await Task.Delay(3000, Token);

                    continue;
                }

                Logger.LogSucceedToConnectToRemoteIpe(LocalPort, RemoteIpe);
                break;
            }
        }, CancellationToken.None);

        if (connectLink == null)
        {
            InvokeOnFailed();
            return null;
        }

        Logger.LogConnectionEstablished();
        Logger.LogEstablishSessionInfo("Connected", connectLink.LocalEndPoint, connectLink.RemoteEndPoint);

        InvokeOnConnected(connectLink);

        return connectLink;
    }

    public override async Task<ISession?> BuildLinkAsync()
    {
        if (isInitiator) return await AcceptConnAsync();

        return await ConnectToRemoteAsync();
    }
}

internal static partial class ZtTcpSinglePortLinkMakerLoggers
{
    [LoggerMessage(LogLevel.Information,
        "[ZT_TCP_S2S] Established [{type}] session info: [Local {local}] [Remote {remote}]")]
    public static partial void LogEstablishSessionInfo(this ILogger logger, string type, IPEndPoint? local,
        IPEndPoint? remote);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] Connection established.")]
    public static partial void LogConnectionEstablished(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] New connection accepted, remote endpoint: {remoteIpe}.")]
    public static partial void LogConnectionAccepted(this ILogger logger, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Error, "[ZT_TCP_S2S] Failed to accept new connection, no trials left.")]
    public static partial void LogFailedToAcceptConnectionWithoutRetry(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "[ZT_TCP_S2S] Failed to accept new connection, remaining try time {tryTime}")]
    public static partial void LogFailedToAcceptConnection(this ILogger logger, Exception ex, int tryTime);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] Started to accept P2P connection...")]
    public static partial void LogStartedToAcceptP2PConnection(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] {localPort} Started to try TCP connection with {remoteIpe}")]
    public static partial void LogStartedToTryTcpConnectionWithRemoteIpe(
        this ILogger logger,
        int localPort,
        IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] Start time {dateTime}")]
    public static partial void LogStartTime(this ILogger logger, string dateTime);

    [LoggerMessage(LogLevel.Error,
        "[ZT_TCP_S2S] {localPort} Failed to connect with {remoteIpe}, remaining try time {tryTime}")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, Exception ex, int localPort,
        IPEndPoint remoteIpe, int tryTime);

    [LoggerMessage(LogLevel.Error,
        "[ZT_TCP_S2S] Failed to connect with {remoteIpe}, maybe the network is special, or the other party has dropped")]
    public static partial void LogFailedToConnectToRemoteIpe(this ILogger logger, IPEndPoint remoteIpe);

    [LoggerMessage(LogLevel.Information, "[ZT_TCP_S2S] {LocalPort} Succeed to connect with {remoteIpe}")]
    public static partial void LogSucceedToConnectToRemoteIpe(this ILogger logger, int localPort, IPEndPoint remoteIpe);
}