namespace Ape.Module.DiscoveryManager.Graph;

public enum DiscoveryWorkKind
{
    Alive,
    ByeBye,
    SnapshotMember,
    SnapshotCommit,
}

public sealed class DiscoveryWork
{
    public DiscoveryWorkKind Kind { get; init; }

    public string Source { get; init; } = "";

    public string Location { get; set; } = "";

    public string Usn { get; init; } = "";

    public string? RemoteAddress { get; init; }

    public bool Drop { get; set; }
}

public readonly record struct DiscoveryEffect(string Location, string Source, string Usn, bool Appeared);

/// <summary>Data bus for one host frame of the discovery plan.</summary>
public sealed class DiscoveryScratch
{
    public List<DiscoverySample> Inbox { get; } = [];

    public List<DiscoveryWork> Work { get; } = [];

    public List<DiscoveryEffect> Staged { get; } = [];

    public List<DiscoveryEffect> Effects { get; } = [];

    public string LocalUuid { get; set; } = "";

    public List<string> LocalAddresses { get; } = [];

    /// <summary>
    /// SSDP LOCATION host rewrite. Arguments are the parsed location and the UDP source address.
    /// </summary>
    public Func<string, string?, string>? RewriteLocation { get; set; }
}
