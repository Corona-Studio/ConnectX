using System.Net.Sockets;
using Hive.Network.Abstractions.Session;
using Hive.Network.Tcp;

namespace ConnectX.Actors;

public static class SessionHealth
{
    public static void Close(ISession session)
    {
        // Closing a transport may be triggered by its receive loop, output loop or control actor.
        lock (session)
            try { session.Close(); }
            catch (ObjectDisposedException) { }
    }

    public static bool IsConnected(ISession session)
    {
        if (session is not TcpSession tcp) return true;
        var socket = tcp.Socket;
        if (socket == null) return false;
        try { return socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0); }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }
}
