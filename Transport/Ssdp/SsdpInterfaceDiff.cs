using System.Net;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// Difference between the multicast memberships already held and the interfaces that should hold them.
/// </summary>
internal readonly record struct SsdpMembershipPlan(
    IReadOnlyList<IPAddress> Join,
    IReadOnlyList<IPAddress> Leave,
    bool JoinDefault,
    bool LeaveDefault)
{
    public bool HasChanges => Join.Count > 0 || Leave.Count > 0 || JoinDefault || LeaveDefault;
}

/// <summary>
/// Plans SSDP multicast membership from the current joined set and the latest eligible IPv4 addresses.
/// Default membership (no local address) is only used when no eligible interface remains.
/// </summary>
internal static class SsdpInterfaceDiff
{
    public static SsdpMembershipPlan Plan(
        IReadOnlyCollection<IPAddress> joined,
        bool defaultMembership,
        IReadOnlyCollection<IPAddress> eligible)
    {
        var current = new HashSet<IPAddress>(joined);
        var desired = new HashSet<IPAddress>(eligible);
        var join = Sort(desired.Where(address => !current.Contains(address)));
        var leave = Sort(current.Where(address => !desired.Contains(address)));
        var willHaveSpecific = desired.Count > 0;
        return new SsdpMembershipPlan(
            join,
            leave,
            JoinDefault: !willHaveSpecific && !defaultMembership,
            LeaveDefault: willHaveSpecific && defaultMembership);
    }

    private static IReadOnlyList<IPAddress> Sort(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(static address => address.ToString(), StringComparer.Ordinal).ToList();
}
