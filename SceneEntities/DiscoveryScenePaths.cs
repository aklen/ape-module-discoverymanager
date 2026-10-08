using System.Security.Cryptography;
using System.Text;

namespace Ape.Module.DiscoveryManager.SceneEntities;

public static class DiscoveryScenePaths
{
    public const string DevicesPrefix = "discovery/devices/";

    public static string Device(string location)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location)));
        return DevicesPrefix + hash[..16].ToLowerInvariant();
    }
}
