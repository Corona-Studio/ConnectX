using System.Net;
using System.Net.Sockets;

namespace ConnectX.Client.Helpers;

public static class AddressHelper
{
    public static IPAddress? GetFirstAvailableV4(this IEnumerable<IPAddress> addresses)
    {
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    public static IPAddress? GetFirstAvailableV6(this IEnumerable<IPAddress> addresses)
    {
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
    }
}