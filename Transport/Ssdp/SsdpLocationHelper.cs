using System.Net;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

internal static class SsdpLocationHelper
{
    /// <summary>
    /// When a device responds over VPN with a LOCATION host that is not reachable from the VPN path,
    /// rewrite the host to the UDP source address while keeping port and path.
    /// </summary>
    public static string RewriteHostFromResponseSource(
        string location,
        IPAddress responseSource,
        IReadOnlyList<SsdpIpv4Subnet> localVpnSubnets,
        bool rewriteEnabled)
    {
        if (!rewriteEnabled || localVpnSubnets.Count == 0)
            return location;

        if (responseSource.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return location;

        if (!localVpnSubnets.Any(s => SsdpSubnetHelper.Contains(s, responseSource)))
            return location;

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
            return location;

        if (string.Equals(uri.Host, responseSource.ToString(), StringComparison.OrdinalIgnoreCase))
            return location;

        if (IPAddress.TryParse(uri.Host, out var locationHost) && locationHost.Equals(responseSource))
            return location;

        var builder = new UriBuilder(uri) { Host = responseSource.ToString() };
        return builder.Uri.ToString().TrimEnd('/');
    }
}
