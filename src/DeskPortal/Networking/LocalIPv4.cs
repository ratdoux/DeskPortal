using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskLink;

static class LocalIPv4
{
    public static IPAddress[] Read(IPAddress? peer)
    {
        IPAddress? routed = null;
        if (peer != null)
        {
            // Connecting a UDP socket selects the local route without sending a datagram.
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(peer, NearbyComputers.Port);
                routed = (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (SocketException) { }
        }
        var addresses = new List<(IPAddress Address, bool HasGateway)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var properties = adapter.GetIPProperties();
                    bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var unicast in properties.UnicastAddresses)
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                            addresses.Add((unicast.Address, gateway));
                }
                catch (NetworkInformationException) { } // An adapter can disappear while being read.
            }
        }
        catch (NetworkInformationException) { }
        return addresses.OrderByDescending(a => a.Address.Equals(routed)).ThenByDescending(a => a.HasGateway)
            .Select(a => a.Address).Distinct().ToArray();
    }
}
