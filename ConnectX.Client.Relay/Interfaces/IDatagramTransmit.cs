namespace ConnectX.Client.Interfaces;

public interface IDatagramTransmit<in TDatagram> where TDatagram : struct
{
    void SendDatagram(TDatagram datagram);
}