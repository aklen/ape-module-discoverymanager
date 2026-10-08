using System.Net;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

internal static class SsdpLocationHelper
{
    /// <summary>
    /// LOCATION host for an inbound M-SEARCH: the local address on the same subnet as the searcher.
    /// Keeps port and path from <paramref name="configuredLocation"/>.
    /// </summary>
    public static string LocationFacingRemote(
        string configuredLocation,
        IPAddress? remote,
        IReadOnlyList<SsdpLocalIpv4Binding> bindings)
    {
        if (remote == null || remote.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return configuredLocation;

        if (!Uri.TryCreate(configuredLocation, UriKind.Absolute, out var uri))
            return configuredLocation;

        IPAddress? best = null;
        var bestPrefix = -1;
        foreach (var binding in bindings)
        {
            if (!SsdpSubnetHelper.Contains(binding.Subnet, remote))
                continue;
            if (binding.Subnet.PrefixLength <= bestPrefix)
                continue;

            bestPrefix = binding.Subnet.PrefixLength;
            best = binding.Address;
        }

        if (best == null || string.Equals(uri.Host, best.ToString(), StringComparison.OrdinalIgnoreCase))
            return configuredLocation;

        var builder = new UriBuilder(uri) { Host = best.ToString() };
        return builder.Uri.ToString().TrimEnd('/');
    }

    /// <summary>
    /// When a reply arrives on a directly connected subnet but LOCATION names a host that is not on that
    /// path, rewrite the host to the UDP source address while keeping port and path.
    /// An advertised address already on the same subnet is left alone.
    /// </summary>
    public static string RewriteHostFromResponseSource(
        string location,
        IPAddress responseSource,
        IReadOnlyList<SsdpIpv4Subnet> localSubnets,
        bool rewriteEnabled)
    {
        if (!rewriteEnabled || localSubnets.Count == 0)
            return location;

        if (responseSource.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return location;

        var pathSubnets = localSubnets.Where(s => SsdpSubnetHelper.Contains(s, responseSource)).ToArray();
        if (pathSubnets.Length == 0)
            return location;

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
            return location;

        if (string.Equals(uri.Host, responseSource.ToString(), StringComparison.OrdinalIgnoreCase))
            return location;

        if (IPAddress.TryParse(uri.Host, out var locationHost))
        {
            if (locationHost.Equals(responseSource))
                return location;
            if (pathSubnets.Any(s => SsdpSubnetHelper.Contains(s, locationHost)))
                return location;
        }

        var builder = new UriBuilder(uri) { Host = responseSource.ToString() };
        return builder.Uri.ToString().TrimEnd('/');
    }
}
