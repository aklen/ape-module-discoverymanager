using Ape.Core.Config.Models;
using Ape.Module.DiscoveryManager.Transport.Mdns;
using Ape.Module.DiscoveryManager.Transport.Ssdp;

namespace Ape.Module.DiscoveryManager.Tests;

public class DiscoveryOptionsTests
{
    [Fact]
    public void Mdns_is_off_without_a_browse_list()
    {
        var missing = MdnsDiscoveryOptions.Parse(null);
        Assert.False(missing.MdnsEnabled);
        Assert.Empty(missing.BrowseProtocols);

        var root = new ConfigNode();
        root.GetObject("mdns").SetBool("enabled", false);
        Assert.False(MdnsDiscoveryOptions.Parse(root).MdnsEnabled);
    }

    [Fact]
    public void Mdns_normalizes_browse_protocol_and_descriptor_path()
    {
        var root = new ConfigNode();
        var mdns = root.GetObject("mdns");
        mdns.SetArray("browseServices", ["_http._tcp", "_http._tcp"]);
        mdns.SetInt("scanSeconds", 5);
        mdns.SetInt("pollSeconds", 20);
        mdns.SetString("defaultDescriptorPath", "desc.xml");

        var options = MdnsDiscoveryOptions.Parse(root);

        Assert.True(options.MdnsEnabled);
        Assert.Equal(["_http._tcp.local."], options.BrowseProtocols);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ScanTime);
        Assert.Equal(TimeSpan.FromSeconds(20), options.PollInterval);
        Assert.Equal("/desc.xml", options.DefaultDescriptorPath);
    }

    [Fact]
    public void Mdns_filter_excludes_configured_locations()
    {
        var root = new ConfigNode();
        var mdns = root.GetObject("mdns");
        mdns.SetArray("browseServices", ["_http._tcp"]);
        var filter = mdns.GetObject("filter");
        filter.SetString("locationContains", "device.xml");
        filter.SetArray("locationExcludes", ["127.0.0.1"]);

        var options = MdnsDiscoveryOptions.Parse(root);
        Assert.NotNull(options.Filter);
        Assert.True(options.Filter!.Predicate(new MdnsDiscoveredEndpoint(
            "http://192.168.1.9/device.xml",
            "_http._tcp.local.",
            "svc",
            null,
            null,
            80,
            new Dictionary<string, string?>())));
        Assert.False(options.Filter.Predicate(new MdnsDiscoveredEndpoint(
            "http://127.0.0.1/device.xml",
            "_http._tcp.local.",
            "svc",
            null,
            null,
            80,
            new Dictionary<string, string?>())));
    }

    [Fact]
    public void Ssdp_defaults_enable_vpn_probe_and_rewrite()
    {
        var options = SsdpDiscoveryOptions.Parse(null);

        Assert.True(options.VpnUnicastProbeEnabled);
        Assert.True(options.RewriteLocationHostFromVpnResponse);
        Assert.Empty(options.ProbeSubnets);
        Assert.Equal(254, options.MaxHostsPerSubnet);
    }

    [Fact]
    public void Ssdp_reads_vpn_probe_settings()
    {
        var root = new ConfigNode();
        var vpn = root.GetObject("ssdp").GetObject("vpnUnicastProbe");
        vpn.SetBool("enabled", false);
        vpn.SetBool("rewriteLocationHost", false);
        vpn.SetArray("probeSubnets", ["10.8.0.0/24"]);
        vpn.SetArray("extraProbeHosts", ["10.8.0.1"]);
        vpn.SetInt("maxHostsPerSubnet", 8);

        var options = SsdpDiscoveryOptions.Parse(root);

        Assert.False(options.VpnUnicastProbeEnabled);
        Assert.False(options.RewriteLocationHostFromVpnResponse);
        Assert.Equal(["10.8.0.0/24"], options.ProbeSubnets);
        Assert.Equal(["10.8.0.1"], options.ExtraProbeHosts);
        Assert.Equal(8, options.MaxHostsPerSubnet);
    }
}
