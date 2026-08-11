using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConnectX.Client.Messages.Proxy;
using ConnectX.Client.Models;
using ConnectX.Client.Proxy;
using ConnectX.Client.Transmission;
using ConnectX.Shared.Helpers;
using ConnectX.Shared.Interfaces;
using Hive.Both.General.Dispatchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHelper = Hive.Common.Shared.Helpers.TaskHelper;

namespace ConnectX.Client.Managers;

public abstract class GenericProxyManager : BackgroundService
{
    private readonly ConcurrentDictionary<TunnelIdentifier, Socket> _acceptedSockets = [];
    private readonly ConcurrentDictionary<ValueTuple<Guid, ushort>, GenericProxyAcceptor> _acceptors = [];

    private readonly IHostApplicationLifetime _lifetime;

    //使用一个二元组确定一个连接
    private readonly ConcurrentDictionary<TunnelIdentifier, GenericProxyPair> _proxies = [];
    private readonly IServiceProvider _serviceProvider;
    protected readonly ILogger Logger;

    protected GenericProxyManager(
        ClientPacketDispatcher clientPacketDispatcher,
        IHostApplicationLifetime lifetime,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _lifetime = lifetime;
        _serviceProvider = serviceProvider;

        Logger = logger;

        clientPacketDispatcher.OnReceive<ProxyDisconnectReq>(RecycleClientProxy);
    }

    public override void Dispose()
    {
        foreach (var (_, value) in _acceptors) value.Dispose();
        foreach (var proxyPair in _proxies) proxyPair.Value.Dispose();

        foreach (var acceptedSocket in _acceptedSockets)
            acceptedSocket.Value.Close();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RecycleClientProxy(ProxyDisconnectReq req, PacketContext ctx)
    {
        var key = new TunnelIdentifier(req.ClientId, req.ServerRealPort, req.ClientRealPort);

        if (_proxies.TryRemove(key, out var pair))
        {
            Logger.LogClientProxyDisconnectedBecauseRequested(key);
            pair.Dispose();
            return;
        }

        Logger.LogFailedToShutdownClientProxyBecauseNotFound(key);
    }

    public void ReceivedProxyConnectReq(
        MessageContext<ProxyConnectReq> ctx,
        ISender sender)
    {
        var connectReq = ctx.Message;

        if (connectReq.IsResponse)
        {
            //客户端侧收到服务器侧的连接响应
            Logger.LogClientReceivedConnectResponse(connectReq);

            //收到回复，启动服务端代理
            if (_acceptors.ContainsKey((connectReq.ClientId, connectReq.ServerRealPort)))
            {
                var key = new TunnelIdentifier(
                    connectReq.ClientId,
                    connectReq.ClientRealPort,
                    connectReq.ServerRealPort);

                if (_acceptedSockets.Remove(key, out var socket))
                    CreateServerProxy(
                        connectReq.ClientId,
                        connectReq.ServerRealPort,
                        connectReq.ClientRealPort,
                        socket,
                        ctx.Dispatcher,
                        sender);
                else
                    Logger.LogErrorCanNotFindSocket(key.PartnerId, key.LocalRealPort);
            }
            else
            {
                Logger.LogErrorCanNotFindAcceptor(connectReq.ClientId, connectReq.ServerRealPort);
            }
        }
        else
        {
            CreateClientProxy(
                connectReq.ClientId,
                connectReq.ClientRealPort,
                connectReq.ServerRealPort,
                connectReq.IsIpv6,
                ctx.Dispatcher,
                sender);

            var mcConnectReq = new ProxyConnectReq
            {
                IsResponse = true,
                ClientId = connectReq.ClientId,
                ClientRealPort = connectReq.ClientRealPort,
                ServerRealPort = connectReq.ServerRealPort,
                IsIpv6 = connectReq.IsIpv6
            };

            sender.SendData(mcConnectReq);

            Logger.LogDebugClientSentConnectResponse(mcConnectReq);
        }
    }

    public virtual GenericProxyPair CreateClientProxy(
        Guid partnerId,
        ushort clientRealPort,
        ushort serverRealMcPort,
        bool isIpv6,
        IDispatcher dispatcher,
        ISender sender)
    {
        // 在真实服务端一侧，创建一个客户端代理，serverRealMcPort是localRealPort, clientRealPort是remoteRealPort
        var key = new TunnelIdentifier(partnerId, serverRealMcPort, clientRealPort);

        if (_proxies.Remove(key, out var prevProxy))
        {
            Logger.LogErrorProxyPairWithSameKey(partnerId, clientRealPort, serverRealMcPort);
            prevProxy.Dispose();
        }

        var proxy = ActivatorUtilities.CreateInstance<GenericProxyClient>(
            _serviceProvider,
            key,
            isIpv6,
            _lifetime.ApplicationStopping);
        proxy.OnRealServerDisconnected += OnProxyDisconnected;

        var pair = new GenericProxyPair(
            partnerId,
            proxy,
            serverRealMcPort,
            clientRealPort,
            dispatcher,
            sender);

        _proxies.AddOrUpdate(key, _ => pair, (_, old) =>
        {
            old.Dispose();
            return pair;
        });

        Logger.LogCreateProxy(key);

        proxy.Start(); //对于客户端，直接启动

        return pair;
    }

    public GenericProxyServer? CreateServerProxy(
        Guid partnerId,
        ushort serverRealPort,
        ushort clientRealPort,
        Socket socket,
        IDispatcher dispatcher,
        ISender sender)
    {
        var key = new TunnelIdentifier(partnerId, clientRealPort, serverRealPort);

        if (_proxies.Remove(key, out var prevProxy))
        {
            Logger.LogErrorProxyPairWithSameKey(partnerId, clientRealPort, serverRealPort);

            prevProxy.Dispose();
        }

        var proxy = ActivatorUtilities.CreateInstance<GenericProxyServer>(
            _serviceProvider,
            socket,
            key,
            _lifetime.ApplicationStopping);

        proxy.OnRealServerDisconnected += OnProxyDisconnected;
        proxy.OnRealServerDisconnected += NotifyServerProxyDisconnect;

        var pair = new GenericProxyPair(
            partnerId,
            proxy,
            clientRealPort,
            serverRealPort,
            dispatcher,
            sender);

        _proxies.AddOrUpdate(key, _ => pair, (_, old) =>
        {
            old.Dispose();
            return pair;
        });
        proxy.Start();

        return proxy;

        void NotifyServerProxyDisconnect(TunnelIdentifier id, GenericProxyBase proxy)
        {
            proxy.OnRealServerDisconnected -= NotifyServerProxyDisconnect;

            // 真客户端离开了房间或掉线，通知房主回收对应的 proxy
            sender.SendData(new ProxyDisconnectReq
            {
                ClientId = id.PartnerId,
                ClientRealPort = id.LocalRealPort,
                ServerRealPort = id.RemoteRealPort
            });

            Logger.LogNotifyServerProxyNeedToDisconnect(id);
        }
    }

    private void OnProxyDisconnected(TunnelIdentifier id, GenericProxyBase proxy)
    {
        proxy.Dispose();

        if (_proxies.Remove(id, out var proxyPair))
            proxyPair.Dispose();

        Logger.LogProxyDisconnected(id);
    }

    public bool HasAcceptor(Guid id, ushort port)
    {
        return _acceptors.ContainsKey((id, port));
    }

    public virtual GenericProxyAcceptor CreateAcceptor(
        Guid partnerId,
        bool isIpv6,
        ushort localMapPort,
        ushort remoteRealServerPort,
        ISender sender)
    {
        if (!NetworkHelper.PortIsAvailable(localMapPort))
            throw new InvalidOperationException($"Port {localMapPort} is not available");

        var key = (partnerId, remoteRealServerPort);

        if (_acceptors.TryRemove(key, out var oldAcceptor))
        {
            if (oldAcceptor.IsRunning)
            {
                _acceptors.AddOrUpdate((partnerId, remoteRealServerPort), _ => oldAcceptor, (_, _) => oldAcceptor);
                throw new InvalidOperationException(
                    $"There has been a acceptor with same key: {partnerId}-{remoteRealServerPort}");
            }

            oldAcceptor.Dispose();
        }

        var acceptor = ActivatorUtilities.CreateInstance<GenericProxyAcceptor>(
            _serviceProvider,
            partnerId,
            isIpv6,
            remoteRealServerPort,
            localMapPort,
            _lifetime.ApplicationStopping);

        _acceptors.AddOrUpdate((partnerId, remoteRealServerPort), _ => acceptor, (_, _) => acceptor);

        // 当有真客户端连接的时候，发送一个McConnectReq，当收到回复后启动Proxy（启动的逻辑在OnReceive里）
        acceptor.OnRealClientConnected += (_, socket) =>
        {
            if (socket.RemoteEndPoint is not IPEndPoint remoteEndPoint)
            {
                Logger.LogErrorCanNotGetRemoteEndPoint();
                return;
            }

            var id = new TunnelIdentifier(
                partnerId,
                (ushort)remoteEndPoint.Port,
                remoteRealServerPort);

            _acceptedSockets.AddOrUpdate(id, _ => socket, (_, _) => socket);

            sender.SendData(new ProxyConnectReq
            {
                IsResponse = false,
                ClientId = partnerId,
                ClientRealPort = (ushort)remoteEndPoint.Port,
                ServerRealPort = remoteRealServerPort,
                IsIpv6 = isIpv6
            });
        };

        Logger.LogCreateAcceptor(key, isIpv6 && Socket.OSSupportsIPv6, localMapPort);

        TaskHelper.FireAndForget(acceptor.StartAcceptAsync);

        return acceptor;
    }

    public GenericProxyAcceptor GetOrCreateAcceptor(
        Guid partnerId,
        bool isIpv6,
        Func<ushort> localMapPortGetter,
        ushort remoteRealServerPort,
        ISender sender)
    {
        var key = (partnerId, remoteRealServerPort);

        return _acceptors.TryGetValue(key, out var value) && value.IsRunning
            ? value
            : CreateAcceptor(partnerId, isIpv6, localMapPortGetter(), remoteRealServerPort, sender);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    public virtual void RemoveAllProxies()
    {
        foreach (var (_, value) in _acceptors)
            try
            {
                value.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }

        _acceptors.Clear();

        foreach (var proxyPair in _proxies)
            try
            {
                proxyPair.Value.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }

        _proxies.Clear();

        foreach (var acceptedSocket in _acceptedSockets)
            try
            {
                acceptedSocket.Value.Close();
            }
            catch (Exception)
            {
                // ignored
            }

        _acceptedSockets.Clear();

        Logger.LogGenProxiesCleared();
    }
}

internal static partial class GenericProxyManagerLoggers
{
    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Proxies cleared.")]
    public static partial void LogGenProxiesCleared(this ILogger logger);

    [LoggerMessage(LogLevel.Debug, "[GEN_PROXY_MANAGER] Client received connect response {Packet}")]
    public static partial void LogClientReceivedConnectResponse(this ILogger logger, ProxyConnectReq packet);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Can not find socket with id: {Id}, port: {Port}")]
    public static partial void LogErrorCanNotFindSocket(this ILogger logger, Guid id, ushort port);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Can not find acceptor with id: {Id}, port: {Port}")]
    public static partial void LogErrorCanNotFindAcceptor(this ILogger logger, Guid id, ushort port);

    [LoggerMessage(LogLevel.Debug, "[GEN_PROXY_MANAGER] Client sent connect response {Packet}")]
    public static partial void LogDebugClientSentConnectResponse(this ILogger logger, ProxyConnectReq packet);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Create client proxy {Key}")]
    public static partial void LogCreateProxy(this ILogger logger, TunnelIdentifier key);

    [LoggerMessage(LogLevel.Error,
        "[GEN_PROXY_MANAGER] There has been a proxy pair with same key: {ID}-{ClientRealPort}-{RemotePort}")]
    public static partial void LogErrorProxyPairWithSameKey(this ILogger logger, Guid id, ushort clientRealPort,
        ushort remotePort);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Proxy {Key} disconnected")]
    public static partial void LogProxyDisconnected(this ILogger logger, TunnelIdentifier key);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Can not get remote endpoint")]
    public static partial void LogErrorCanNotGetRemoteEndPoint(this ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "[GEN_PROXY_MANAGER] Create acceptor {Key}, is IPV6 [{isIpv6}], local map port [{localMapPort}]")]
    public static partial void LogCreateAcceptor(this ILogger logger, (Guid, ushort) key, bool isIpv6,
        ushort localMapPort);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Notify server proxy need to disconnect {Id}")]
    public static partial void LogNotifyServerProxyNeedToDisconnect(this ILogger logger, TunnelIdentifier id);

    [LoggerMessage(LogLevel.Error, "[GEN_PROXY_MANAGER] Failed to shutdown client proxy because not found {key}")]
    public static partial void LogFailedToShutdownClientProxyBecauseNotFound(this ILogger logger, TunnelIdentifier key);

    [LoggerMessage(LogLevel.Information, "[GEN_PROXY_MANAGER] Client proxy disconnected because requested {key}")]
    public static partial void LogClientProxyDisconnectedBecauseRequested(this ILogger logger, TunnelIdentifier key);
}
