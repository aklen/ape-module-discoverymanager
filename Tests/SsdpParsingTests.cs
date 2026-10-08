using System.Net;
using System.Text;
using Ape.Module.DiscoveryManager.Transport.Ssdp;

namespace Ape.Module.DiscoveryManager.Tests;

public class SsdpParsingTests
{
    [Fact]
    public void Notify_alive_exposes_location()
    {
        var ok = SsdpDatagramParser.TryParseHeaders(Packet(
            "NOTIFY * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "NTS: ssdp:alive",
            "USN: uuid:peer",
            "LOCATION: http://192.168.1.9:8080/device.xml"), out var headers, out var firstLine);

        Assert.True(ok);
        Assert.StartsWith("NOTIFY", firstLine, StringComparison.OrdinalIgnoreCase);
        Assert.True(SsdpDatagramParser.ShouldReportDevice(firstLine, headers));
        Assert.False(SsdpDatagramParser.IsNotifyByeBye(firstLine, headers));
        Assert.True(SsdpDatagramParser.TryGetLocation(headers, out var location));
        Assert.Equal("http://192.168.1.9:8080/device.xml", location);
        Assert.Equal("uuid:peer", headers["usn"]);
    }

    [Fact]
    public void Notify_bye_bye_is_not_an_alive_report()
    {
        SsdpDatagramParser.TryParseHeaders(Packet(
            "NOTIFY * HTTP/1.1",
            "NTS: ssdp:byebye",
            "LOCATION: http://192.168.1.9:8080/device.xml"), out var headers, out var firstLine);

        Assert.True(SsdpDatagramParser.IsNotifyByeBye(firstLine, headers));
        Assert.False(SsdpDatagramParser.ShouldReportDevice(firstLine, headers));
    }

    [Fact]
    public void Search_response_is_reported()
    {
        SsdpDatagramParser.TryParseHeaders(Packet(
            "HTTP/1.1 200 OK",
            "LOCATION: http://192.168.1.9:8080/device.xml"), out var headers, out var firstLine);

        Assert.True(SsdpDatagramParser.ShouldReportDevice(firstLine, headers));
        Assert.False(SsdpDatagramParser.IsNotifyByeBye(firstLine, headers));
    }

    [Fact]
    public void Msearch_payload_asks_for_root_device()
    {
        var text = Encoding.UTF8.GetString(SsdpDatagramParser.BuildMSearchRootDevice());

        Assert.Contains("M-SEARCH * HTTP/1.1", text, StringComparison.Ordinal);
        Assert.Contains("ST: upnp:rootdevice", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_replaces_host_when_reply_comes_from_vpn_subnet()
    {
        Assert.True(SsdpSubnetHelper.TryParseCidr("10.8.0.0/24", out var subnet));
        var rewritten = SsdpLocationHelper.RewriteHostFromResponseSource(
            "http://192.168.1.50:8080/device.xml",
            IPAddress.Parse("10.8.0.5"),
            [subnet],
            rewriteEnabled: true);

        Assert.Equal("http://10.8.0.5:8080/device.xml", rewritten);
    }

    [Theory]
    [InlineData(false, "10.8.0.5")]
    [InlineData(true, "192.168.1.50")]
    public void Rewrite_leaves_location_when_disabled_or_source_is_outside_subnet(bool enabled, string source)
    {
        Assert.True(SsdpSubnetHelper.TryParseCidr("10.8.0.0/24", out var subnet));
        const string location = "http://192.168.1.50:8080/device.xml";

        var rewritten = SsdpLocationHelper.RewriteHostFromResponseSource(
            location,
            IPAddress.Parse(source),
            [subnet],
            enabled);

        Assert.Equal(location, rewritten);
    }

    [Fact]
    public void Subnet_contains_hosts_and_skips_excluded_addresses()
    {
        Assert.True(SsdpSubnetHelper.TryParseCidr("10.8.0.0/24", out var subnet));
        Assert.False(SsdpSubnetHelper.TryParseCidr("10.8.0.0/33", out _));
        Assert.False(SsdpSubnetHelper.TryParseCidr("not-a-cidr", out _));

        Assert.True(SsdpSubnetHelper.Contains(subnet, IPAddress.Parse("10.8.0.5")));
        Assert.False(SsdpSubnetHelper.Contains(subnet, IPAddress.Parse("10.9.0.5")));

        var hosts = SsdpSubnetHelper.EnumerateHostAddresses(
            subnet,
            new HashSet<uint> { ToUInt32(IPAddress.Parse("10.8.0.1")) },
            maxHosts: 2).ToArray();

        Assert.Equal(IPAddress.Parse("10.8.0.2"), hosts[0]);
        Assert.Equal(IPAddress.Parse("10.8.0.3"), hosts[1]);
    }

    private static byte[] Packet(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
