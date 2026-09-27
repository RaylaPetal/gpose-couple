using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CoPose.Net;

public static class NetworkInfo
{
    /// <summary>The IPv4 address of the interface that has the default gateway, or null.</summary>
    public static IPAddress? GetLanAddress()
    {
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                             && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (candidate != null)
                return candidate;

            // Fallback: the address the OS would route public traffic from (no packet is sent for UDP connect).
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True for addresses that cannot be reached from the internet: RFC 1918 private ranges, carrier-grade NAT
    /// (100.64.0.0/10), link-local and loopback.
    /// </summary>
    public static bool IsPrivateOrCarrierNat(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
               || (b[0] == 169 && b[1] == 254)
               || b[0] == 127
               || b[0] == 0;
    }

    public static bool IsCarrierNat(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
