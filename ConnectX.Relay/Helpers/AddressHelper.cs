﻿using System.Net;
using System.Net.Sockets;
using ConnectX.Shared.Helpers;

namespace ConnectX.Relay.Helpers;

public static class AddressHelper
{
    public static IEnumerable<IPAddress> GetServerPublicAddress()
    {
        var hostName = Dns.GetHostName();
        var addresses = Dns.GetHostAddresses(hostName);

        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork &&
                !NetworkHelper.IsPrivateIpAddress(address))
            {
                yield return address;
            }
        }
    }
}