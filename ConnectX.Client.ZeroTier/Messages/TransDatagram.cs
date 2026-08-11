using ConnectX.Client.Models;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages;

[MessageDefine]
[MemoryPackable]
public readonly partial struct TransDatagram(DatagramFlag flag, int synOrAck, ReadOnlyMemory<byte>? payload)
{
    public readonly DatagramFlag Flag = flag;
    public readonly int SynOrAck = synOrAck;

    [BrotliFormatter<ReadOnlyMemory<byte>?>]
    public readonly ReadOnlyMemory<byte>? Payload = payload;

    public const DatagramFlag FirstHandShakeFlag = DatagramFlag.CON | DatagramFlag.SYN;
    public const DatagramFlag SecondHandShakeFlag = DatagramFlag.CON | DatagramFlag.SYN | DatagramFlag.ACK;
    public const DatagramFlag ThirdHandShakeFlag = DatagramFlag.CON | DatagramFlag.ACK;

    /// <summary>
    ///     创建 Connect 请求包，建立连接的第一次握手
    /// </summary>
    public static TransDatagram CreateHandShakeFirst(int synOrAck)
    {
        return new TransDatagram(FirstHandShakeFlag, synOrAck, null);
    }

    /// <summary>
    ///     创建 Connect SYN ACK 请求包，建立连接时的第二次握手
    /// </summary>
    public static TransDatagram CreateHandShakeSecond(int synOrAck)
    {
        return new TransDatagram(SecondHandShakeFlag, synOrAck, null);
    }

    /// <summary>
    ///     创建 Connect ACK 请求包，建立连接时的第三次握手
    /// </summary>
    public static TransDatagram CreateHandShakeThird(int synOrAck)
    {
        return new TransDatagram(ThirdHandShakeFlag, synOrAck, null);
    }

    public static TransDatagram CreateNormal(int syn, ReadOnlyMemory<byte> payload)
    {
        return new TransDatagram(DatagramFlag.SYN, syn, payload);
    }

    public static TransDatagram CreateAck(int ack)
    {
        return new TransDatagram(DatagramFlag.ACK, ack, null);
    }
}