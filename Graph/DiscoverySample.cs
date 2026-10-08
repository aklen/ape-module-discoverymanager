namespace Ape.Module.DiscoveryManager.Graph;

public static class DiscoverySources
{
    public const string Ssdp = "ssdp";
    public const string Mdns = "mdns";
}

public enum DiscoverySampleKind
{
    Alive,
    ByeBye,
    Snapshot,
}

/// <summary>
/// One observation from a socket adapter. The adapter does not decide whether it is new, self, or gone.
/// </summary>
public sealed class DiscoverySample
{
    public required string Source { get; init; }

    public DiscoverySampleKind Kind { get; init; }

    public string Location { get; init; } = "";

    public string Usn { get; init; } = "";

    public string? RemoteAddress { get; init; }

    /// <summary>Populated for <see cref="DiscoverySampleKind.Snapshot"/> (mDNS browse result).</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];
}

public interface IDiscoverySampleSink
{
    void Submit(DiscoverySample sample);
}
