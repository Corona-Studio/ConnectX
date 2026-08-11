using System.Buffers;
using System.Collections;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Messages;
using ConnectX.Client.Models;
using ConnectX.Client.Route;
using Hive.Both.General.Dispatchers;
using Hive.Codec.Abstractions;
using Hive.Common.Shared.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission.Connections;

public sealed class P2PConnection : ConnectionBase, IDatagramTransmit<TransDatagram>
{
    private readonly RouterPacketDispatcher _routerPacketDispatcher;
    private readonly ClientPacketDispatcher _clientPacketDispatcher;
    private readonly BitArray _sendBufferAckFlag = new(BufferLength);

    private int _ackPointer;
    private int _lastAckTime;
    private int _sendPointer;

    public P2PConnection(
        Guid targetId,
        IDispatcher dispatcher,
        RouterPacketDispatcher routerPacketDispatcher,
        ClientPacketDispatcher clientPacketDispatcher,
        IPacketCodec codec,
        IHostApplicationLifetime lifetime,
        ILogger<P2PConnection> logger) : base("P2P_CONN", targetId, dispatcher, codec, lifetime, logger)
    {
        _routerPacketDispatcher = routerPacketDispatcher;
        _clientPacketDispatcher = clientPacketDispatcher;
        _routerPacketDispatcher.OnReceive<TransDatagram>(targetId, OnTransDatagramReceived);

        TaskHelper.FireAndForget(StartResendCoroutineAsync);
    }

    public override bool IsDirectConnection => true;

    public void SendDatagram(TransDatagram datagram)
    {
        _sendBufferAckFlag[_sendPointer] = false;
        _sendPointer = (_sendPointer + 1) % BufferLength;

        _routerPacketDispatcher.Send(To, datagram);
    }

    private async Task StartResendCoroutineAsync()
    {
        while (!Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Shared.Helpers.TaskHelper.WaitUntilAsync(NeedResend, Lifetime.ApplicationStopping);

            if (!Lifetime.ApplicationStopping.IsCancellationRequested) Logger.LogResendCoroutineStarted(Source, To);
        }

        return;

        bool NeedResend()
        {
            if (_ackPointer == _sendPointer) return false;
            var now = DateTime.Now.Millisecond;
            var time = now - _lastAckTime;
            return time > Timeout;
        }
    }

    public override void Send(ReadOnlyMemory<byte> payload)
    {
        SendDatagram(TransDatagram.CreateNormal(_sendPointer, payload));
    }

    public override async Task<bool> ConnectAsync(CancellationToken token)
    {
        Logger.LogConnectingTo(Source, To);

        if (IsConnected) return true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(Timeout);

        // SYN
        var succeed = await _routerPacketDispatcher.SendAndListenOnceAsync<TransDatagram, TransDatagram>(
            To,
            TransDatagram.CreateHandShakeFirst(0),
            IsSecondShakeHand,
            cts.Token);

        if (!succeed)
        {
            Logger.LogConnectFailed(Source, To);

            return false;
        }

        //ACK
        _routerPacketDispatcher.Send(To, TransDatagram.CreateHandShakeThird(2));
        IsConnected = true;

        return true;

        static bool IsSecondShakeHand(TransDatagram t)
        {
            return t is { Flag: TransDatagram.SecondHandShakeFlag, SynOrAck: 1 };
        }
    }

    private void OnTransDatagramReceived(TransDatagram datagram, PacketContext context)
    {
        // RouterPacketDispatcher is shared by every direct partner. Without this
        // source check, the first packet in a three-person room is consumed by all
        // P2PConnection instances, cross-wiring handshakes and acknowledgements.
        if (context.SenderId != To) return;

        if (datagram.Flag == TransDatagram.FirstHandShakeFlag)
        {
            // 握手的回复
            _routerPacketDispatcher.Send(To, TransDatagram.CreateHandShakeSecond(1));

            Logger.LogReceiveFirstShakeHandPacket(Source, To);

            IsConnected = true;
            return;
        }

        // 如果是TransDatagram，需要回复确认
        if ((datagram.Flag & DatagramFlag.SYN) != 0)
        {
            if (datagram.Payload != null)
            {
                var sequence = new ReadOnlySequence<byte>(datagram.Payload.Value);
                var message = Codec.Decode(sequence);

                if (message == null)
                {
                    Logger.LogDecodeMessageFailed(Source, datagram.Payload.Value.Length, To);

                    return;
                }

                _clientPacketDispatcher.DispatchPacket(context.SenderId, datagram.Payload.Value);
                Dispatcher.Dispatch(SessionPlaceHolder.Shared, message.GetType(), message);
            }

            _routerPacketDispatcher.Send(To, TransDatagram.CreateAck(datagram.SynOrAck));
        }
        else if ((datagram.Flag & DatagramFlag.ACK) != 0)
        {
            //是ACK包，需要更新发送缓冲区的状态

            _sendBufferAckFlag[datagram.SynOrAck] = true;

            if (_ackPointer != datagram.SynOrAck) return;

            _lastAckTime = DateTime.Now.Millisecond;

            // 向后寻找第一个未收到ACK的包
            for (;
                 _sendBufferAckFlag[_ackPointer] && _ackPointer <= _sendPointer;
                 _ackPointer = (_ackPointer + 1) % BufferLength)
                _sendBufferAckFlag[_ackPointer] = false;
        }
    }

    public override void Disconnect()
    {
        _routerPacketDispatcher.RemoveReceiver<TransDatagram>(To);
        base.Disconnect();
    }
}
