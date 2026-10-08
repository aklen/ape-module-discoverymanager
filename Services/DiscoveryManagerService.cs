using System.Net;
using Ape.Core.Config;
using Ape.Core.Config.Models;
using Ape.Core.Determinism;
using Ape.Core.Graph;
using Ape.Core.Logging;
using Ape.Core.Network;
using Ape.Core.Network.Discovery;
using Ape.Core.Runtime.Service;
using Ape.Core.Scene.Commit;
using Ape.Module.DiscoveryManager.Graph;
using Ape.Module.DiscoveryManager.SceneEntities;
using Ape.Module.DiscoveryManager.Transport.Mdns;
using Ape.Module.DiscoveryManager.Transport.Ssdp;
using Microsoft.Extensions.DependencyInjection;

namespace Ape.Module.DiscoveryManager.Services;

/// <summary>
/// Owns discovery transports and the decision plan.
/// Adapters enqueue samples. Each host frame drains them, ticks the plan, then commits scene entities.
/// Advertisement stays outside the plan: it only sends NOTIFY.
/// </summary>
public class DiscoveryManagerService : IPluggableService, IDeterministicFrameParticipant, IDiscoverySampleSink
{
    public string ServiceId => "discovery-service";
    public string Name => "Discovery Service";

    public string ParticipantId => "discovery";
    public FramePhase Phase => FramePhase.Ingest;
    public int Order => 10;

    private readonly List<IDiscoveryTransport> _transports = [];
    private readonly IngressBuffer<DiscoverySample> _ingress = new();
    private readonly DiscoveryScratch _scratch = new();
    private readonly PlanRuntime _runtime = new();
    private readonly Dictionary<string, string> _sceneKeys = new(StringComparer.Ordinal);
    private readonly object _searchLock = new();

    private ILogger _logger = null!;
    private INetworkManager? _networkManager;
    private FrozenPlan<DiscoveryScratch>? _plan;
    private string _deviceDescriptionUrl = string.Empty;
    private string _localDeviceUuid = string.Empty;
    private string _localIp = "127.0.0.1";
    private bool _authoritative = true;
    private volatile bool _stopRequested;
    private List<string>? _searchAccepted;
    private SsdpDiscoveryTransport? _ssdpDiscoveryTransport;
    private SsdpAdvertisementTransport? _ssdpAdvertisementTransport;
    private MdnsDiscoveryTransport? _mdnsDiscoveryTransport;

    /// <summary>
    /// Event triggered when the plan accepts one or more new devices.
    /// </summary>
    public event Action<IEnumerable<string>>? DevicesDiscovered;

    /// <summary>
    /// Event triggered when the plan drops a device that was previously accepted.
    /// </summary>
    public event Action<string>? DeviceLost;

    public void Submit(DiscoverySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        _ingress.Enqueue(sample);
    }

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

        _localIp = _networkManager?.GetPreferredLocalIPAddress() ?? "127.0.0.1";
        var port = 5000; // TODO: Get from config
        _deviceDescriptionUrl = $"http://{_localIp}:{port}/api/discovery/device.xml";

        _localDeviceUuid = Guid.NewGuid().ToString();
        var usn = $"uuid:{_localDeviceUuid}::{SsdpApeDevice.Urn}";
        _authoritative = _networkManager == null
            || !string.Equals(_networkManager.Role, "client", StringComparison.OrdinalIgnoreCase);

        _plan = DiscoveryPipeline.Compile(message => _logger.LogDebug(message));
        var runtime = _runtime;
        _plan.InitRuntime(ref runtime);
        _scratch.RewriteLocation = RewriteSsdpLocation;

        _logger.LogInfo($"DiscoveryManagerService initialized. Local UUID: {_localDeviceUuid}, Device URL: {_deviceDescriptionUrl}");
        _logger.LogDebug(_plan.Describe());

        var allIps = _networkManager?.GetLocalIPAddresses() ?? Enumerable.Empty<string>();
        if (allIps.Any())
            _logger.LogDebug($"Available IPs: {string.Join(", ", allIps)}");

        _ssdpDiscoveryTransport = new SsdpDiscoveryTransport(
            _logger,
            this,
            _localDeviceUuid,
            _localIp,
            new SsdpDiscoveryMsearchResponderOptions(usn, _deviceDescriptionUrl),
            ssdpOptions);
        _ssdpAdvertisementTransport = new SsdpAdvertisementTransport(_logger);

        AddTransport(_ssdpDiscoveryTransport);
        AddTransport(_ssdpAdvertisementTransport);

        if (mdnsOptions.MdnsEnabled && mdnsOptions.BrowseProtocols.Count > 0)
        {
            _mdnsDiscoveryTransport = new MdnsDiscoveryTransport(
                _logger,
                this,
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

        var registry = services.GetService<IFrameParticipantRegistry>();
        if (registry == null)
            _logger.LogWarning("Discovery plan will not tick: frame registry is missing.");
        else
            registry.Register(this);

        _logger.LogInfo(
            "Default transports registered: SSDP discovery (with inbound M-SEARCH responder), advertisement" +
            (_mdnsDiscoveryTransport != null ? " + mDNS browse" : ""));
    }

    public void Start(CancellationToken cancellationToken)
    {
        _runtime.EnqueueEvent("start");

        foreach (var transport in _transports)
            transport.Start();

        _ssdpAdvertisementTransport?.Advertise(SsdpApeDevice.FriendlyName, _deviceDescriptionUrl);

        if (_ssdpDiscoveryTransport != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = await AcceptDuringAsync(() => _ssdpDiscoveryTransport.DiscoverDevicesAsync())
                        .ConfigureAwait(false);
                    _logger.LogInfo($"SSDP Discovery: Active search accepted {found.Count} device(s).");
                    foreach (var location in found)
                        _logger.LogInfo($"SSDP Discovery: Device found at {location}");
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
        _stopRequested = true;
        foreach (var transport in _transports)
            transport.Stop();
        _logger.LogInfo("Discovery service stopped");
    }

    public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
    {
        if (_plan == null)
            return;

        if (_stopRequested)
        {
            _stopRequested = false;
            DrainAndTick(commits);
            _scratch.Inbox.Clear();
            _scratch.Effects.Clear();
            _runtime.EnqueueEvent("stop");
            TickPlan();
            return;
        }

        DrainAndTick(commits);
    }

    /// <summary>
    /// Adds a discovery transport to the manager.
    /// </summary>
    public void AddTransport(IDiscoveryTransport transport)
    {
        _transports.Add(transport);
    }

    /// <summary>
    /// Runs every transport search and returns the devices the plan accepted during that window.
    /// </summary>
    public async Task<IEnumerable<string>> DiscoverDevicesAsync()
    {
        return await AcceptDuringAsync(async () =>
        {
            var tasks = _transports
                .Where(transport => transport is not SsdpAdvertisementTransport)
                .Select(transport => transport.DiscoverDevicesAsync());
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<List<string>> AcceptDuringAsync(Func<Task> search)
    {
        var accepted = new List<string>();
        lock (_searchLock)
            _searchAccepted = accepted;

        try
        {
            await search().ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            lock (_searchLock)
                return accepted.Distinct(StringComparer.Ordinal).ToList();
        }
        finally
        {
            lock (_searchLock)
                _searchAccepted = null;
        }
    }

    private void DrainAndTick(IFrameCommitBatch commits)
    {
        _scratch.Effects.Clear();
        _scratch.Inbox.Clear();
        foreach (var envelope in _ingress.DrainOrdered())
            _scratch.Inbox.Add(envelope.Payload);

        RefreshFilterContext();
        TickPlan();
        PublishEffects(commits);
    }

    private void TickPlan()
    {
        var before = _runtime.CurrentState;
        var scratch = _scratch;
        var runtime = _runtime;
        _plan!.Tick(ref scratch, ref runtime);
        if (!string.Equals(before, _runtime.CurrentState, StringComparison.Ordinal))
            _logger.LogInfo($"Discovery plan: {before} -> {_runtime.CurrentState}");
    }

    private void RefreshFilterContext()
    {
        _scratch.LocalUuid = _localDeviceUuid;
        _scratch.LocalAddresses.Clear();
        var addresses = _networkManager?.GetLocalIPAddresses();
        if (addresses != null)
        {
            foreach (var address in addresses)
            {
                if (!string.IsNullOrWhiteSpace(address))
                    _scratch.LocalAddresses.Add(address);
            }
        }

        if (_scratch.LocalAddresses.Count == 0)
            _scratch.LocalAddresses.Add(_localIp);
    }

    private string RewriteSsdpLocation(string location, string? remote)
    {
        if (_ssdpDiscoveryTransport == null || string.IsNullOrEmpty(remote))
            return location;
        if (!IPAddress.TryParse(remote, out var address))
            return location;

        var rewritten = SsdpLocationHelper.RewriteHostFromResponseSource(
            location,
            address,
            _ssdpDiscoveryTransport.VpnRewriteSubnets,
            _ssdpDiscoveryTransport.RewriteVpnLocationHost);
        if (!string.Equals(rewritten, location, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                $"SSDP Discovery: Rewrote VPN LOCATION host {location} -> {rewritten} (response from {address})");
        }

        return rewritten;
    }

    private void PublishEffects(IFrameCommitBatch commits)
    {
        if (_scratch.Effects.Count == 0)
            return;

        List<string>? appeared = null;
        var now = DateTime.UtcNow;
        var owner = _networkManager?.LocalPeerId;

        foreach (var effect in _scratch.Effects)
        {
            if (effect.Appeared)
            {
                appeared ??= [];
                appeared.Add(effect.Location);
                _logger.LogInfo($"Discovered device ({effect.Source}): {effect.Location}");
                CommitPresent(commits, effect, owner, now);
                lock (_searchLock)
                    _searchAccepted?.Add(effect.Location);
            }
            else
            {
                _logger.LogWarning($"Device lost ({effect.Source}): {effect.Location}");
                CommitAbsent(commits, effect, now);
                DeviceLost?.Invoke(effect.Location);
            }
        }

        if (appeared != null)
            DevicesDiscovered?.Invoke(appeared);
    }

    private void CommitPresent(IFrameCommitBatch commits, DiscoveryEffect effect, string? owner, DateTime now)
    {
        if (!_authoritative)
            return;

        var key = DiscoveryScenePaths.Device(effect.Location);
        var known = _sceneKeys.ContainsKey(effect.Location);
        _sceneKeys[effect.Location] = key;
        if (!known)
        {
            commits.Enqueue(new CreateRegisteredEntityCommitRequest(
                DiscoverySceneEntityTypeIds.Device,
                key,
                owner));
            _logger.LogInfo($"Scene entity {key} for {effect.Location}");
        }

        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.Location), effect.Location));
        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.Source), effect.Source));
        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.Usn), effect.Usn));
        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.Present), true));
        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.LastSeenUtc), now));
    }

    private void CommitAbsent(IFrameCommitBatch commits, DiscoveryEffect effect, DateTime now)
    {
        if (!_authoritative || !_sceneKeys.TryGetValue(effect.Location, out var key))
            return;

        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.Present), false));
        commits.Enqueue(new SetSceneEntityPropertyCommitRequest(key, nameof(DiscoveredDevice.LastSeenUtc), now));
    }
}
