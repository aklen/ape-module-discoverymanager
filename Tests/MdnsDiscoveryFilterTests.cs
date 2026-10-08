using Ape.Module.DiscoveryManager.Transport.Mdns;

namespace Ape.Module.DiscoveryManager.Tests;

public class MdnsDiscoveryFilterTests
{
    [Fact]
    public void Location_contains_and_excludes_combine()
    {
        var filter = MdnsDiscoveryFilter
            .ByLocationContains("device.xml")
            .And(MdnsDiscoveryFilter.LocationExcludesSubstrings(["127.0.0.1", "localhost"]))
            .And(MdnsDiscoveryFilter.ByPort(8080));

        Assert.True(filter.Predicate(Endpoint("http://192.168.1.9:8080/device.xml", 8080)));
        Assert.False(filter.Predicate(Endpoint("http://127.0.0.1:8080/device.xml", 8080)));
        Assert.False(filter.Predicate(Endpoint("http://192.168.1.9:80/device.xml", 80)));
    }

    [Fact]
    public void Empty_excludes_allow_every_location()
    {
        var filter = MdnsDiscoveryFilter.LocationExcludesSubstrings(["", "  "]);

        Assert.True(filter.Predicate(Endpoint("http://127.0.0.1/device.xml", 80)));
    }

    [Fact]
    public void Or_and_not_follow_the_predicate()
    {
        var filter = MdnsDiscoveryFilter.ByPort(80).Or(MdnsDiscoveryFilter.ByPort(443)).Not();

        Assert.False(filter.Predicate(Endpoint("http://192.168.1.9/device.xml", 80)));
        Assert.True(filter.Predicate(Endpoint("http://192.168.1.9:8080/device.xml", 8080)));
    }

    [Fact]
    public void Txt_and_protocol_match_the_endpoint()
    {
        var endpoint = Endpoint(
            "http://192.168.1.9/device.xml",
            9,
            protocol: "_http._tcp.local.",
            txt: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["path"] = "/device.xml" });

        Assert.True(MdnsDiscoveryFilter.ByBrowseProtocol("_http._tcp.local.").Predicate(endpoint));
        Assert.True(MdnsDiscoveryFilter.ByTxtEquals("path", "/device.xml").Predicate(endpoint));
        Assert.False(MdnsDiscoveryFilter.ByTxtEquals("path", "/other").Predicate(endpoint));
    }

    private static MdnsDiscoveredEndpoint Endpoint(
        string location,
        int port,
        string protocol = "_http._tcp.local.",
        IReadOnlyDictionary<string, string?>? txt = null) =>
        new(location, protocol, "svc", "instance", "display", port, txt ?? new Dictionary<string, string?>());
}
