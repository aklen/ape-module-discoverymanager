using Ape.Core.Config;
using Ape.Core.Config.Models;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Network.Discovery;
using Ape.Core.Runtime.Service;
using Ape.Module.DiscoveryManager.Transport.Mdns;
using Ape.Module.DiscoveryManager.Transport.Ssdp;
using Microsoft.Extensions.DependencyInjection;

using Ape.Module.DiscoveryManager;

namespace Ape.Module.DiscoveryManager.Services;

/// <summary>
/// Discovery manager service that manages device discovery using multiple discovery transports (e.g., SSDP, mDNS).
/// </summary>
public class DiscoveryManagerService : IPluggableService
{
    public string ServiceId => "discovery-service";
    public string Name => "Discovery Service";

    private readonly List<IDiscoveryTransport> _transports = new();
    private ILogger _logger = null!;
    private INetworkManager? _networkManager;
    private string _deviceDescriptionUrl = string.Empty;
    private string _localDeviceUuid = string.Empty;
    private SsdpDiscoveryTransport? _ssdpDiscoveryTransport;
    private SsdpAdvertisementTransport? _ssdpAdvertisementTransport;
    private MdnsDiscoveryTransport? _mdnsDiscoveryTransport;

    /// <summary>
    /// Event triggered when devices are discovered by any transport.
    /// </summary>
    public event Action<IEnumerable<string>>? DevicesDiscovered;

    /// <summary>
    /// Event triggered when a device is lost by any transport.
    /// </summary>
    public event Action<string>? DeviceLost;

    public void Register(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<DiscoveryManagerService>(this);
    }

    public void Initialize(IServiceProvider services)
    {
        _logger = services.GetRequiredService<ILogger>();

        _networkManager = services.GetService<INetworkManager>();
        var hostCfg = services.GetService<IStartupConfig>();
        var moduleAware = services.GetService<IModuleTable>();
        var discoveryModuleSection = moduleAware?.GetModuleSection(hostCfg?.Root, DiscoveryManagerModuleIds.ModuleId);
        var mdnsOptions = MdnsDiscoveryOptions.Parse(discoveryModuleSection);
        var ssdpOptions = SsdpDiscoveryOptions.Parse(discoveryModuleSection);

        var localIp = _networkManager?.GetPreferredLocalIPAddress() ?? "127.0.0.1";
        var port = 5000; // TODO: Get from config
        _deviceDescriptionUrl = $"http://{localIp}:{port}/api/discovery/device.xml";

        _localDeviceUuid = Guid.NewGuid().ToString();
        var usn = $"uuid:{_localDeviceUuid}::{SsdpApeDevice.Urn}";

        _logger.LogInfo($"DiscoveryManagerService initialized. Local UUID: {_localDeviceUuid}, Device URL: {_deviceDescriptionUrl}");

        var allIps = _networkManager?.GetLocalIPAddresses() ?? Enumerable.Empty<string>();
        if (allIps.Any())
        {
            _logger.LogDebug($"Available IPs: {string.Join(", ", allIps)}");
        }

        _ssdpDiscoveryTransport = new SsdpDiscoveryTransport(
            _logger,
            _localDeviceUuid,
            localIp,
            new SsdpDiscoveryMsearchResponderOptions(usn, _deviceDescriptionUrl),
            ssdpOptions);
        _ssdpAdvertisementTransport = new SsdpAdvertisementTransport(_logger);

        AddTransport(_ssdpDiscoveryTransport);
        AddTransport(_ssdpAdvertisementTransport);

        if (mdnsOptions.MdnsEnabled && mdnsOptions.BrowseProtocols.Count > 0)
        {
            _mdnsDiscoveryTransport = new MdnsDiscoveryTransport(
                _logger,
                mdnsOptions.BrowseProtocols,
                mdnsOptions.ScanTime,
                mdnsOptions.PollInterval,
                mdnsOptions.DefaultDescriptorPath,
                mdnsOptions.Filter);
            AddTransport(_mdnsDiscoveryTransport);
        }
        else
        {
            _logger.LogInfo("mDNS browse disabled or no browse protocols configured");
        }

        _logger.LogInfo(
            "Default transports registered: SSDP discovery (with inbound M-SEARCH responder), advertisement" +
            (_mdnsDiscoveryTransport != null ? " + mDNS browse" : ""));
    }

    public void Start(CancellationToken cancellationToken)
    {
        foreach (var transport in _transports)
        {
            transport.Start();
        }

        _ssdpAdvertisementTransport?.Advertise(SsdpApeDevice.FriendlyName, _deviceDescriptionUrl);

        // Prime SSDP results (M-SEARCH) without waiting for the 8s periodic timer or NOTIFY alone.
        if (_ssdpDiscoveryTransport != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ssdpDiscoveryTransport.DiscoverDevicesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"SSDP Discovery: initial active search failed: {ex.Message}");
                }
            });
        }

        _logger.LogInfo("Discovery service started");
    }

    public void Stop()
    {
        foreach (var transport in _transports)
        {
            transport.Stop();
        }
        _logger.LogInfo("Discovery service stopped");
    }

    /// <summary>
    /// Adds a discovery transport to the manager.
    /// </summary>
    public void AddTransport(IDiscoveryTransport transport)
    {
        _transports.Add(transport);

        transport.DevicesDiscovered += devices =>
        {
            foreach (var device in devices)
            {
                _logger.LogInfo($"Discovered device: {device}");
            }
            DevicesDiscovered?.Invoke(devices);
        };

        transport.DeviceLost += device =>
        {
            _logger.LogWarning($"Device lost: {device}");
            DeviceLost?.Invoke(device);
        };
    }

    /// <summary>
    /// Discovers devices using all registered transports.
    /// </summary>
    public async Task<IEnumerable<string>> DiscoverDevicesAsync()
    {
        var discoveryTasks = _transports.Select(t => t.DiscoverDevicesAsync());
        var results = await Task.WhenAll(discoveryTasks);
        return results.SelectMany(r => r);
    }
}
