using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// IPv4 interfaces eligible for SSDP multicast (up, non-loopback, has a non-loopback IPv4).
/// </summary>
internal static class SsdpInterfaceHelper
{
    public static IReadOnlyList<IPAddress> GetEligibleLocalIpv4UnicastAddresses()
    {
        var set = new HashSet<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(addr.Address))
                    continue;
                set.Add(addr.Address);
            }
        }

        return set.Count > 0 ? set.ToList() : Array.Empty<IPAddress>();
    }

    public static IReadOnlyList<SsdpIpv4Subnet> GetLocalVpnSubnets()
    {
        var subnets = new List<SsdpIpv4Subnet>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (!LooksLikeVpnInterface(ni))
                continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(addr.Address))
                    continue;
                if (addr.PrefixLength is <= 0 or > 30)
                    continue;

                subnets.Add(SsdpSubnetHelper.FromAddressAndPrefix(addr.Address, addr.PrefixLength));
            }
        }

        return subnets;
    }

    public static bool LooksLikeVpnInterface(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return true;

        var name = ni.Name;
        if (name.Length == 0)
            return false;

        return name.StartsWith("utun", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("tap", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("ppp", StringComparison.OrdinalIgnoreCase);
    }
}
