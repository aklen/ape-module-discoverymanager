using Ape.Core.Logging;
using Ape.Core.Network.Discovery;
using Rssdp;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// SSDP implementation of the advertisement transport.
/// Handles broadcasting availability and system URL.
/// </summary>
public class SsdpAdvertisementTransport : IDiscoveryTransport
{
    private readonly ILogger _logger;
    private SsdpDevicePublisher? _publisher;
    private SsdpRootDevice? _rootDevice;

    public string TransportType => "ssdp";

    public event Action<IEnumerable<string>>? DevicesDiscovered;
    public event Action<string>? DeviceLost;

    public SsdpAdvertisementTransport(ILogger logger)
    {
        _logger = logger;
        _logger.LogInfo($"SsdpAdvertisementTransport initialized");
    }

    public void Start()
    {
        _logger.LogInfo($"SSDP Advertisement: Starting advertisement transport...");
        _publisher = new SsdpDevicePublisher();
    }

    public void Stop()
    {
        _logger.LogInfo($"SSDP Advertisement: Stopping advertisement transport...");
        if (_rootDevice != null && _publisher != null)
        {
            _publisher.RemoveDevice(_rootDevice);
        }
        _publisher?.Dispose();
        _publisher = null;
    }

    public Task<IEnumerable<string>> DiscoverDevicesAsync()
    {
        throw new NotImplementedException("Use SsdpDiscoveryTransport for discovery.");
    }

    /// <summary>
    /// Advertise device with device description and location URL.
    /// </summary>
    public void Advertise(string deviceDescription, string locationUrl)
    {
        if (_publisher == null)
        {
            _logger.LogWarning("SSDP Advertisement: Publisher not started. Call Start() first.");
            return;
        }

        if (_rootDevice != null)
        {
            _publisher.RemoveDevice(_rootDevice);
        }

        var deviceUuid = Guid.NewGuid().ToString();

        _rootDevice = new SsdpRootDevice()
        {
            CacheLifetime = TimeSpan.FromMinutes(30),
            Location = new Uri(locationUrl),
            DeviceTypeNamespace = SsdpApeDevice.TypeNamespace,
            DeviceType = SsdpApeDevice.DeviceType,
            FriendlyName = deviceDescription,
            Manufacturer = SsdpApeDevice.Manufacturer,
            ModelName = SsdpApeDevice.ModelName,
            Uuid = deviceUuid
        };

        _publisher.AddDevice(_rootDevice);
        _logger.LogInfo($"SSDP Advertisement: Device advertised with UUID: {deviceUuid}, Location: {locationUrl}");
    }

    /// <summary>
    /// Advertise device with default localhost URL (for backward compatibility).
    /// </summary>
    public void Advertise(string deviceDescription)
    {
        Advertise(deviceDescription, "http://localhost:5000/device.xml");
    }

    public void Dispose()
    {
        Stop();
    }
}
