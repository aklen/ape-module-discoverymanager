using Ape.Core.Graph;
using Ape.Module.DiscoveryManager.Graph;

namespace Ape.Module.DiscoveryManager.Tests;

public class DiscoveryPipelineTests
{
    [Fact]
    public void Compile_exposes_running_stages_and_hsm()
    {
        var plan = DiscoveryPipeline.Compile();

        Assert.Equal(["normalize", "filter", "dedupe", "emit"], plan.StageOrder);
        Assert.Equal(["Idle", "Running", "Stopping"], plan.HsmStates);
        Assert.Contains("Running", plan.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_drops_samples()
    {
        var plan = DiscoveryPipeline.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        var scratch = new DiscoveryScratch();

        Frame(plan, scratch, runtime, Alive("http://192.168.1.9:8080/device.xml"));

        Assert.Equal("Idle", runtime.CurrentState);
        Assert.Empty(scratch.Effects);
    }

    [Fact]
    public void Alive_emits_once_until_bye_bye()
    {
        var (plan, runtime, scratch) = Running();
        const string location = "http://192.168.1.9:8080/device.xml";

        Frame(plan, scratch, runtime, Alive(location, usn: "uuid:peer"));
        var appeared = Assert.Single(scratch.Effects);
        Assert.True(appeared.Appeared);
        Assert.Equal(location, appeared.Location);
        Assert.Equal(DiscoverySources.Ssdp, appeared.Source);
        Assert.Equal("uuid:peer", appeared.Usn);

        Frame(plan, scratch, runtime, Alive(location, usn: "uuid:peer"));
        Assert.Empty(scratch.Effects);

        Frame(plan, scratch, runtime, Bye(location));
        var lost = Assert.Single(scratch.Effects);
        Assert.False(lost.Appeared);
        Assert.Equal(location, lost.Location);
    }

    [Fact]
    public void Unknown_bye_bye_emits_nothing()
    {
        var (plan, runtime, scratch) = Running();

        Frame(plan, scratch, runtime, Bye("http://192.168.1.9:8080/device.xml"));

        Assert.Empty(scratch.Effects);
    }

    [Fact]
    public void Filter_drops_self_localhost_and_blank_location()
    {
        var (plan, runtime, scratch) = Running();
        scratch.LocalUuid = "self-uuid";
        scratch.LocalAddresses.Add("10.1.1.5");

        Frame(
            plan,
            scratch,
            runtime,
            Alive("http://192.168.1.9:8080/device.xml", usn: "uuid:self-uuid::urn:ape"),
            Alive("http://10.1.1.5:8080/device.xml"),
            Alive("http://127.0.0.1:8080/device.xml"),
            Alive("http://localhost:8080/device.xml"),
            Alive("   "),
            Alive("http://192.168.1.20:8080/device.xml"));

        var kept = Assert.Single(scratch.Effects);
        Assert.Equal("http://192.168.1.20:8080/device.xml", kept.Location);
    }

    [Fact]
    public void Normalize_rewrites_location_before_dedupe()
    {
        var (plan, runtime, scratch) = Running();
        scratch.RewriteLocation = (location, remote) =>
            remote == "10.8.0.5" ? "http://10.8.0.5:8080/device.xml" : location;

        Frame(
            plan,
            scratch,
            runtime,
            Alive("http://192.168.1.9:8080/device.xml", remote: "10.8.0.5"));

        Assert.Equal("http://10.8.0.5:8080/device.xml", Assert.Single(scratch.Effects).Location);

        Frame(plan, scratch, runtime, Alive("http://10.8.0.5:8080/device.xml", remote: "10.8.0.5"));
        Assert.Empty(scratch.Effects);
    }

    [Fact]
    public void Mdns_snapshot_emits_added_and_missing_locations()
    {
        var (plan, runtime, scratch) = Running();
        const string first = "http://192.168.1.9:8080/device.xml";
        const string second = "http://192.168.1.10:8080/device.xml";

        Frame(plan, scratch, runtime, Snapshot(first, second));
        Assert.Equal(2, scratch.Effects.Count);
        Assert.All(scratch.Effects, effect => Assert.True(effect.Appeared));
        Assert.Equal(DiscoverySources.Mdns, scratch.Effects[0].Source);

        Frame(plan, scratch, runtime, Snapshot(first));
        var lost = Assert.Single(scratch.Effects);
        Assert.False(lost.Appeared);
        Assert.Equal(second, lost.Location);
    }

    [Fact]
    public void Mdns_loss_keeps_a_device_still_seen_on_ssdp()
    {
        var (plan, runtime, scratch) = Running();
        const string location = "http://192.168.1.9:8080/device.xml";

        Frame(plan, scratch, runtime, Alive(location));
        Assert.Single(scratch.Effects);

        Frame(plan, scratch, runtime, Snapshot(location));
        Assert.Empty(scratch.Effects);

        Frame(plan, scratch, runtime, Snapshot());
        Assert.Empty(scratch.Effects);

        Frame(plan, scratch, runtime, Bye(location));
        Assert.False(Assert.Single(scratch.Effects).Appeared);
    }

    [Fact]
    public void Stopping_ignores_later_samples()
    {
        var (plan, runtime, scratch) = Running();
        runtime.EnqueueEvent("stop");
        Frame(plan, scratch, runtime);

        Assert.Equal("Stopping", runtime.CurrentState);

        Frame(plan, scratch, runtime, Alive("http://192.168.1.9:8080/device.xml"));
        Assert.Empty(scratch.Effects);
    }

    private static (FrozenPlan<DiscoveryScratch> Plan, PlanRuntime Runtime, DiscoveryScratch Scratch) Running()
    {
        var plan = DiscoveryPipeline.Compile();
        var runtime = new PlanRuntime();
        plan.InitRuntime(ref runtime);
        runtime.EnqueueEvent("start");
        var scratch = new DiscoveryScratch();
        Frame(plan, scratch, runtime);
        Assert.Equal("Running", runtime.CurrentState);
        return (plan, runtime, scratch);
    }

    private static void Frame(
        FrozenPlan<DiscoveryScratch> plan,
        DiscoveryScratch scratch,
        PlanRuntime runtime,
        params DiscoverySample[] samples)
    {
        scratch.Effects.Clear();
        scratch.Inbox.Clear();
        scratch.Inbox.AddRange(samples);
        plan.Tick(ref scratch, ref runtime);
    }

    private static DiscoverySample Alive(string location, string usn = "", string? remote = null) => new()
    {
        Source = DiscoverySources.Ssdp,
        Kind = DiscoverySampleKind.Alive,
        Location = location,
        Usn = usn,
        RemoteAddress = remote,
    };

    private static DiscoverySample Bye(string location) => new()
    {
        Source = DiscoverySources.Ssdp,
        Kind = DiscoverySampleKind.ByeBye,
        Location = location,
    };

    private static DiscoverySample Snapshot(params string[] locations) => new()
    {
        Source = DiscoverySources.Mdns,
        Kind = DiscoverySampleKind.Snapshot,
        Locations = locations,
    };
}
