namespace Ape.Module.DiscoveryManager.SceneEntities;

/// <summary>
/// Registry type id for a device found by discovery.
/// Scene key shape: <c>discovery/devices/{id}</c>.
/// A later REST module can expose that key as <c>/api/v1/scene/entities/discovery/devices/{id}</c>.
/// </summary>
public static class DiscoverySceneEntityTypeIds
{
    public const string Device = "ape.discovery.device";
}
