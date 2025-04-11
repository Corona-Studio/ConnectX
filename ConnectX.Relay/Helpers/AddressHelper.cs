﻿using System.Net;
using System.Net.Sockets;

namespace ConnectX.Relay.Helpers;

public static class AddressHelper
{
    public static IEnumerable<IPAddress> GetServerPublicAddress()
    {
        var hostName = Dns.GetHostName();
        var addresses = Dns.GetHostAddresses(hostName);

        foreach (var address in addresses)
        {
            if (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            {
                yield return address;
            }
        }
    }
}