using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Module.DiscoveryManager.SceneEntities;

namespace Ape.Module.DiscoveryManager.Tests;

public class DiscoverySceneTests
{
    [Fact]
    public void Device_path_is_stable_and_prefixed()
    {
        const string location = "http://192.168.1.9:8080/device.xml";

        var first = DiscoveryScenePaths.Device(location);
        var second = DiscoveryScenePaths.Device(location);

        Assert.Equal(first, second);
        Assert.StartsWith(DiscoveryScenePaths.DevicesPrefix, first, StringComparison.Ordinal);
        Assert.Equal(DiscoveryScenePaths.DevicesPrefix.Length + 16, first.Length);
        Assert.NotEqual(first, DiscoveryScenePaths.Device(location + "/other"));
        Assert.Equal(first, first.ToLowerInvariant());
    }

    [Fact]
    public void Discovered_device_uses_registry_type_id()
    {
        var device = new DiscoveredDevice();

        Assert.Equal(DiscoverySceneEntityTypeIds.Device, device.TypeId);
        Assert.Equal("ape.discovery.device", device.TypeId);
        Assert.False(device.Present);
    }

    [Fact]
    public void Registration_service_creates_device_with_scene_key_as_id()
    {
        var registry = new SceneEntityRegistry();
        var logger = new ListLogger();
        var provider = new MapProvider()
            .Add<ISceneEntityRegistry>(registry)
            .Add<ILogger>(logger);

        new DiscoveredDeviceRegistrationService().Initialize(provider);

        const string key = "discovery/devices/abcdef0123456789";
        Assert.True(registry.TryCreate(DiscoverySceneEntityTypeIds.Device, provider, key, null, out var entity));
        var device = Assert.IsType<DiscoveredDevice>(entity);
        Assert.Equal(key, device.Id);
        Assert.Contains(DiscoverySceneEntityTypeIds.Device, registry.RegisteredTypeIds);
    }
}
