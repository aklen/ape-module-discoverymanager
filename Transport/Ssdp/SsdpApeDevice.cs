namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>SSDP / UPnP device identifiers advertised and accepted by this module.</summary>
internal static class SsdpApeDevice
{
    public const string TypeNamespace = "ape";
    public const string DeviceType = "core";
    public const string Urn = "urn:ape:device:core:1";
    public const string Manufacturer = "Ape";
    public const string FriendlyName = "Ape Instance";
    public const string ModelName = "Ape Instance";
    public const string ServerHeader = "Ape/1.0 UPnP/1.1";
}
