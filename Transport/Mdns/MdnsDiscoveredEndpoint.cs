namespace Ape.Module.DiscoveryManager.Transport.Mdns;

/// <summary>
/// One resolved mDNS browse hit (before/after <see cref="MdnsDiscoveryFilter"/>).
/// </summary>
public sealed class MdnsDiscoveredEndpoint
{
    public MdnsDiscoveredEndpoint(
        string resolvedLocation,
        string browseProtocol,
        string serviceKey,
        string? instanceName,
        string? displayName,
        int port,
        IReadOnlyDictionary<string, string?> txt)
    {
        ResolvedLocation = resolvedLocation;
        BrowseProtocol = browseProtocol;
        ServiceKey = serviceKey;
        InstanceName = instanceName;
        DisplayName = displayName;
        Port = port;
        Txt = txt;
    }

    /// <summary>HTTP(S) URL used for discovery callbacks (from TXT <c>location=</c> or synthesized).</summary>
    public string ResolvedLocation { get; }

    /// <summary>Normalized browse name, e.g. <c>_http._tcp.local.</c></summary>
    public string BrowseProtocol { get; }

    /// <summary>Key from Zeroconf host service map (implementation-specific).</summary>
    public string ServiceKey { get; }

    public string? InstanceName { get; }

    public string? DisplayName { get; }

    public int Port { get; }

    /// <summary>Merged TXT key/value pairs (case-insensitive keys).</summary>
    public IReadOnlyDictionary<string, string?> Txt { get; }
}
