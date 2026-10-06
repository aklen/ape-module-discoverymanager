using Ape.Core.Config.Models;

namespace Ape.Module.DiscoveryManager.Transport.Mdns;

/// <summary>Parses <c>modules["Ape.Module.DiscoveryManager"].mdns</c> for browse list, timing, default path, and declarative filter.</summary>
public sealed class MdnsDiscoveryOptions
{
    private MdnsDiscoveryOptions(
        bool mdnsEnabled,
        IReadOnlyList<string> browseProtocols,
        TimeSpan scanTime,
        TimeSpan pollInterval,
        string defaultDescriptorPath,
        MdnsDiscoveryFilter? filter)
    {
        MdnsEnabled = mdnsEnabled;
        BrowseProtocols = browseProtocols;
        ScanTime = scanTime;
        PollInterval = pollInterval;
        DefaultDescriptorPath = defaultDescriptorPath;
        Filter = filter;
    }

    public bool MdnsEnabled { get; }

    public IReadOnlyList<string> BrowseProtocols { get; }

    public TimeSpan ScanTime { get; }

    public TimeSpan PollInterval { get; }

    /// <summary>Path appended when TXT has no <c>location=</c> (leading slash).</summary>
    public string DefaultDescriptorPath { get; }

    public MdnsDiscoveryFilter? Filter { get; }

    /// <summary>
    /// Browse list comes from host JSON <c>mdns.browseServices</c> (e.g. <c>_http._tcp</c>).
    /// When the <c>mdns</c> subtree is missing, <c>mdns.enabled</c> is false, or <c>browseServices</c> is omitted/empty, mDNS browse is off.
    /// </summary>
    public static MdnsDiscoveryOptions Parse(IConfigNode? discoveryModuleSection)
    {
        const string defaultPath = "/device.xml";
        var off = new MdnsDiscoveryOptions(false, Array.Empty<string>(), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12), defaultPath, null);

        if (discoveryModuleSection == null || !discoveryModuleSection.TryGetChildObject("mdns", out var mdns))
            return off;

        if (mdns.HasKey("enabled") && !mdns.GetBool("enabled", true))
            return off;

        if (!mdns.HasKey("browseServices"))
            return off;

        var raw = mdns.GetStringArray("browseServices");
        if (raw.Count == 0)
            return off;

        var protos = raw.Select(NormalizeBrowseProtocol).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (protos.Count == 0)
            return off;

        return BuildFromMdnsNode(mdns, protos, defaultPath);
    }

    private static MdnsDiscoveryOptions BuildFromMdnsNode(IConfigNode mdns, List<string> protocols, string defaultPathFallback)
    {
        var scan = TimeSpan.FromSeconds(Math.Clamp(mdns.GetInt("scanSeconds", 4), 1, 120));
        var poll = TimeSpan.FromSeconds(Math.Clamp(mdns.GetInt("pollSeconds", 12), 2, 600));
        var path = mdns.GetString("defaultDescriptorPath", defaultPathFallback);
        if (string.IsNullOrWhiteSpace(path))
            path = defaultPathFallback;
        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;

        var filter = ParseDeclarativeFilter(mdns);
        return new MdnsDiscoveryOptions(true, protocols, scan, poll, path, filter);
    }

    /// <summary>Ensures a Zeroconf browse PTR form ending with <c>.local.</c></summary>
    public static string NormalizeBrowseProtocol(string raw)
    {
        var t = raw.Trim().TrimEnd('.');
        if (t.Length == 0)
            return string.Empty;
        if (!t.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            t += ".local";
        return t + ".";
    }

    private static MdnsDiscoveryFilter? ParseDeclarativeFilter(IConfigNode mdns)
    {
        if (!mdns.TryGetChildObject("filter", out var f))
            return null;

        MdnsDiscoveryFilter? acc = null;

        void And(MdnsDiscoveryFilter next) =>
            acc = acc is null ? next : acc.And(next);

        var locContains = f.GetString("locationContains");
        if (!string.IsNullOrWhiteSpace(locContains))
            And(MdnsDiscoveryFilter.ByLocationContains(locContains));

        var excludes = f.GetStringArray("locationExcludes");
        if (excludes.Count > 0)
            And(MdnsDiscoveryFilter.LocationExcludesSubstrings(excludes));

        if (f.TryGetChildObject("txtEquals", out var txtEq))
        {
            foreach (var key in txtEq.Keys)
            {
                var val = txtEq.GetString(key);
                And(MdnsDiscoveryFilter.ByTxtEquals(key, val));
            }
        }

        return acc;
    }
}
