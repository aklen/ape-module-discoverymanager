namespace Ape.Module.DiscoveryManager.Transport.Mdns;

/// <summary>
/// Predicate builder for mDNS browse results (fluent And / Or / Not, like DeviceManager's DeviceFilter).
/// </summary>
public sealed class MdnsDiscoveryFilter
{
    public Func<MdnsDiscoveredEndpoint, bool> Predicate { get; }

    private MdnsDiscoveryFilter(Func<MdnsDiscoveredEndpoint, bool> predicate)
    {
        Predicate = predicate;
    }

    /// <summary>Match every endpoint.</summary>
    public static MdnsDiscoveryFilter AllowAll => new(_ => true);

    public static MdnsDiscoveryFilter Custom(Func<MdnsDiscoveredEndpoint, bool> predicate)
        => new(predicate);

    /// <summary>Resolved <c>location</c> URL must contain <paramref name="substring"/> (ordinal ignore case).</summary>
    public static MdnsDiscoveryFilter ByLocationContains(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        return new(ep => ep.ResolvedLocation.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolved location must not contain any of the given substrings.</summary>
    public static MdnsDiscoveryFilter LocationExcludesSubstrings(IEnumerable<string> substrings)
    {
        var arr = substrings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (arr.Length == 0)
            return AllowAll;
        return new(ep => arr.All(s => !ep.ResolvedLocation.Contains(s, StringComparison.OrdinalIgnoreCase)));
    }

    public static MdnsDiscoveryFilter ByBrowseProtocol(string normalizedProtocol)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedProtocol);
        return new(ep => ep.BrowseProtocol.Equals(normalizedProtocol, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Browse protocol (normalized) contains substring, e.g. <c>http</c> for <c>_http._tcp.local.</c></summary>
    public static MdnsDiscoveryFilter ByBrowseProtocolContains(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        return new(ep => ep.BrowseProtocol.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public static MdnsDiscoveryFilter ByPort(int port) => new(ep => ep.Port == port);

    public static MdnsDiscoveryFilter ByTxtEquals(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return new(ep =>
            ep.Txt.TryGetValue(key, out var v) &&
            string.Equals(v, value, StringComparison.Ordinal));
    }

    /// <summary>TXT key must exist; value substring match (ignore case).</summary>
    public static MdnsDiscoveryFilter ByTxtValueContains(string key, string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(substring);
        return new(ep =>
            ep.Txt.TryGetValue(key, out var v) &&
            v != null &&
            v.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public static MdnsDiscoveryFilter ByDisplayNameContains(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        return new(ep =>
            ep.DisplayName != null &&
            ep.DisplayName.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public static MdnsDiscoveryFilter ByInstanceNameContains(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        return new(ep =>
            ep.InstanceName != null &&
            ep.InstanceName.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    public MdnsDiscoveryFilter And(MdnsDiscoveryFilter other)
        => new(ep => Predicate(ep) && other.Predicate(ep));

    public MdnsDiscoveryFilter Or(MdnsDiscoveryFilter other)
        => new(ep => Predicate(ep) || other.Predicate(ep));

    public MdnsDiscoveryFilter Not()
        => new(ep => !Predicate(ep));
}
