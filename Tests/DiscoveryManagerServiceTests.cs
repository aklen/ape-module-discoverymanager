using System.Reflection;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Scene.Commit;
using Ape.Module.DiscoveryManager.Graph;
using Ape.Module.DiscoveryManager.SceneEntities;
using Ape.Module.DiscoveryManager.Services;

namespace Ape.Module.DiscoveryManager.Tests;

public class DiscoveryManagerServiceTests
{
    [Fact]
    public void Host_frame_commits_a_new_device_and_marks_it_absent_on_bye_bye()
    {
        var (service, logger) = Started();
        const string location = "http://192.168.1.9:8080/device.xml";
        var discovered = new List<string>();
        string? lost = null;
        service.DevicesDiscovered += urls => discovered.AddRange(urls);
        service.DeviceLost += url => lost = url;

        service.Submit(Alive(location));
        var created = Frame(service);

        var create = Assert.Single(created.Operations.OfType<CreateRegisteredEntityCommitRequest>());
        Assert.Equal(DiscoverySceneEntityTypeIds.Device, create.TypeId);
        Assert.Equal(DiscoveryScenePaths.Device(location), create.Name);
        Assert.Contains(
            created.Operations.OfType<SetSceneEntityPropertyCommitRequest>(),
            op => op.PropertyName == nameof(DiscoveredDevice.Present) && op.Value is true);
        Assert.Equal([location], discovered);
        Assert.Contains(logger.Info, line => line.Contains(create.Name, StringComparison.Ordinal));

        service.Submit(Alive(location));
        var repeat = Frame(service);
        Assert.Empty(repeat.Operations.OfType<CreateRegisteredEntityCommitRequest>());

        service.Submit(Bye(location));
        var gone = Frame(service);
        Assert.Contains(
            gone.Operations.OfType<SetSceneEntityPropertyCommitRequest>(),
            op => op.SceneKey == create.Name && op.PropertyName == nameof(DiscoveredDevice.Present) && op.Value is false);
        Assert.Equal(location, lost);
    }

    [Fact]
    public void Host_frame_in_idle_does_not_commit()
    {
        var logger = new ListLogger();
        var service = new DiscoveryManagerService();
        service.Initialize(new MapProvider().Add<ILogger>(logger));

        service.Submit(Alive("http://192.168.1.9:8080/device.xml"));
        var batch = Frame(service);

        Assert.Empty(batch.Operations);
    }

    [Fact]
    public void Host_frame_drops_the_local_device()
    {
        var (service, logger) = Started();
        var line = logger.Info.Single(entry => entry.Contains("Local UUID:", StringComparison.Ordinal));
        var afterLabel = line[(line.IndexOf("Local UUID:", StringComparison.Ordinal) + "Local UUID:".Length)..];
        var uuid = afterLabel.Split(',')[0].Trim();

        service.Submit(Alive("http://192.168.1.9:8080/device.xml", usn: $"uuid:{uuid}::urn:ape"));
        service.Submit(Alive("http://127.0.0.1:5000/device.xml"));
        var batch = Frame(service);

        Assert.Empty(batch.Operations);
    }

    private static (DiscoveryManagerService Service, ListLogger Logger) Started()
    {
        var logger = new ListLogger();
        var service = new DiscoveryManagerService();
        service.Initialize(new MapProvider().Add<ILogger>(logger));
        Runtime(service).EnqueueEvent("start");
        return (service, logger);
    }

    private static RecordingBatch Frame(DiscoveryManagerService service)
    {
        var batch = new RecordingBatch();
        service.OnHostFrame(new FrameContext(1, DateTimeOffset.UnixEpoch), batch);
        return batch;
    }

    private static PlanRuntime Runtime(DiscoveryManagerService service)
    {
        var field = typeof(DiscoveryManagerService).GetField(
            "_runtime",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (PlanRuntime)field!.GetValue(service)!;
    }

    private static DiscoverySample Alive(string location, string usn = "uuid:peer") => new()
    {
        Source = DiscoverySources.Ssdp,
        Kind = DiscoverySampleKind.Alive,
        Location = location,
        Usn = usn,
    };

    private static DiscoverySample Bye(string location) => new()
    {
        Source = DiscoverySources.Ssdp,
        Kind = DiscoverySampleKind.ByeBye,
        Location = location,
    };
}
