using System.Net;
using System.Net.Sockets;

namespace ConnectX.Client.Helpers;

public static class AddressHelper
{
    extension(IEnumerable<IPAddress> addresses)
    {
        public IPAddress? GetFirstAvailableV4()
        {
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }

        public IPAddress? GetFirstAvailableV6()
        {
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        }
    }
}