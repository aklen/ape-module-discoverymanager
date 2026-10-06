using System.Net;
using System.Net.Sockets;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

internal readonly record struct SsdpIpv4Subnet(uint Network, int PrefixLength);

internal static class SsdpSubnetHelper
{
    public static bool TryParseCidr(string cidr, out SsdpIpv4Subnet subnet)
    {
        subnet = default;
        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Trim().Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var networkIp))
            return false;

        if (networkIp.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(parts[1], out var prefix))
            return false;

        if (prefix is < 0 or > 32)
            return false;

        subnet = new SsdpIpv4Subnet(ToUInt32(networkIp), prefix);
        return true;
    }

    public static SsdpIpv4Subnet FromAddressAndPrefix(IPAddress address, int prefixLength)
    {
        var ip = ToUInt32(address);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return new SsdpIpv4Subnet(ip & mask, prefixLength);
    }

    public static bool Contains(SsdpIpv4Subnet subnet, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var mask = subnet.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - subnet.PrefixLength);
        return (ToUInt32(address) & mask) == subnet.Network;
    }

    public static IEnumerable<IPAddress> EnumerateHostAddresses(
        SsdpIpv4Subnet subnet,
        IReadOnlySet<uint> excludeHostIds,
        int maxHosts)
    {
        if (subnet.PrefixLength > 30)
            yield break;

        var hostBits = 32 - subnet.PrefixLength;
        var hostCount = hostBits >= 31 ? 0 : (1 << hostBits) - 2;
        if (hostCount <= 0)
            yield break;

        var limit = Math.Min(hostCount, maxHosts);
        var emitted = 0;

        for (var host = 1u; host <= hostCount && emitted < limit; host++)
        {
            var candidate = subnet.Network + host;
            if (excludeHostIds.Contains(candidate))
                continue;

            emitted++;
            yield return FromUInt32(candidate);
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new(new byte[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
}
