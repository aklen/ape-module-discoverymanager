using System.Net;
using Ape.Module.DiscoveryManager.Transport.Ssdp;

namespace Ape.Module.DiscoveryManager.Tests;

public class SsdpInterfaceDiffTests
{
    [Fact]
    public void First_snapshot_joins_every_eligible_address()
    {
        var plan = SsdpInterfaceDiff.Plan([], defaultMembership: false, [Ip("192.168.0.10"), Ip("10.0.0.5")]);

        Assert.Equal([Ip("10.0.0.5"), Ip("192.168.0.10")], plan.Join);
        Assert.Empty(plan.Leave);
        Assert.False(plan.JoinDefault);
        Assert.False(plan.LeaveDefault);
    }

    [Fact]
    public void New_address_is_joined_and_removed_address_is_left()
    {
        var plan = SsdpInterfaceDiff.Plan(
            [Ip("192.168.0.10"), Ip("10.0.0.5")],
            defaultMembership: false,
            [Ip("10.0.0.5"), Ip("10.1.1.8")]);

        Assert.Equal([Ip("10.1.1.8")], plan.Join);
        Assert.Equal([Ip("192.168.0.10")], plan.Leave);
        Assert.False(plan.JoinDefault);
    }

    [Fact]
    public void Unchanged_set_has_no_membership_changes()
    {
        var addresses = new[] { Ip("10.0.0.5") };

        var plan = SsdpInterfaceDiff.Plan(addresses, defaultMembership: false, addresses);

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Empty_eligible_set_uses_default_membership_once()
    {
        var first = SsdpInterfaceDiff.Plan([Ip("10.0.0.5")], defaultMembership: false, []);
        Assert.Equal([Ip("10.0.0.5")], first.Leave);
        Assert.True(first.JoinDefault);

        var held = SsdpInterfaceDiff.Plan([], defaultMembership: true, []);
        Assert.False(held.HasChanges);
    }

    [Fact]
    public void Specific_interfaces_replace_default_membership()
    {
        var plan = SsdpInterfaceDiff.Plan([], defaultMembership: true, [Ip("10.0.0.5")]);

        Assert.Equal([Ip("10.0.0.5")], plan.Join);
        Assert.True(plan.LeaveDefault);
        Assert.False(plan.JoinDefault);
    }

    private static IPAddress Ip(string text) => IPAddress.Parse(text);
}
