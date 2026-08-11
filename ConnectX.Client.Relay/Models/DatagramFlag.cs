namespace ConnectX.Client.Models;

[Flags]
public enum DatagramFlag : byte
{
    SYN = 0x01,
    ACK = 0x02,
    FIN = 0x04,
    RST = 0x08,
    CON = 0x10
}