using Ape.Core.Config.Models;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>Parses <c>modules["Ape.Module.DiscoveryManager"].ssdp</c> for VPN unicast probe behavior.</summary>
public sealed class SsdpDiscoveryOptions
{
    private SsdpDiscoveryOptions(
        bool vpnUnicastProbeEnabled,
        bool rewriteLocationHostFromVpnResponse,
        IReadOnlyList<string> probeSubnets,
        IReadOnlyList<string> extraProbeHosts,
        int maxHostsPerSubnet)
    {
        VpnUnicastProbeEnabled = vpnUnicastProbeEnabled;
        RewriteLocationHostFromVpnResponse = rewriteLocationHostFromVpnResponse;
        ProbeSubnets = probeSubnets;
        ExtraProbeHosts = extraProbeHosts;
        MaxHostsPerSubnet = maxHostsPerSubnet;
    }

    public bool VpnUnicastProbeEnabled { get; }

    public bool RewriteLocationHostFromVpnResponse { get; }

    /// <summary>Explicit CIDR probes (e.g. <c>10.8.0.0/24</c>). When empty, VPN NIC subnets are inferred.</summary>
    public IReadOnlyList<string> ProbeSubnets { get; }

    /// <summary>Always probed in addition to subnet scans (useful for /32 VPN interfaces).</summary>
    public IReadOnlyList<string> ExtraProbeHosts { get; }

    public int MaxHostsPerSubnet { get; }

    public static SsdpDiscoveryOptions Parse(IConfigNode? discoveryModuleSection)
    {
        const bool defaultEnabled = true;
        const bool defaultRewrite = true;
        const int defaultMaxHosts = 254;

        if (discoveryModuleSection == null || !discoveryModuleSection.TryGetChildObject("ssdp", out var ssdp))
            return new SsdpDiscoveryOptions(defaultEnabled, defaultRewrite, Array.Empty<string>(), Array.Empty<string>(), defaultMaxHosts);

        if (!ssdp.TryGetChildObject("vpnUnicastProbe", out var vpn))
            return new SsdpDiscoveryOptions(defaultEnabled, defaultRewrite, Array.Empty<string>(), Array.Empty<string>(), defaultMaxHosts);

        var enabled = !vpn.HasKey("enabled") || vpn.GetBool("enabled", true);
        var rewrite = !vpn.HasKey("rewriteLocationHost") || vpn.GetBool("rewriteLocationHost", true);
        var subnets = vpn.GetStringArray("probeSubnets");
        var extra = vpn.GetStringArray("extraProbeHosts");
        var maxHosts = Math.Clamp(vpn.GetInt("maxHostsPerSubnet", defaultMaxHosts), 1, 4096);

        return new SsdpDiscoveryOptions(enabled, rewrite, subnets, extra, maxHosts);
    }
}
