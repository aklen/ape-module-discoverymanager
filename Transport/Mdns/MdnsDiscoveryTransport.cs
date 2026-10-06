using Ape.Core.Logging;
using Ape.Core.Network.Discovery;
using Zeroconf;

namespace Ape.Module.DiscoveryManager.Transport.Mdns;

/// <summary>
/// Browses one or more mDNS PTR names from config (e.g. <c>_http._tcp.local.</c>).
/// Uses TXT <c>location=</c> when present; otherwise <c>http://&lt;host-ip&gt;:&lt;port&gt;&lt;defaultDescriptorPath&gt;</c>.
/// </summary>
public sealed class MdnsDiscoveryTransport : IDiscoveryTransport
{
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _browseProtocols;
    private readonly TimeSpan _scanTime;
    private readonly TimeSpan _pollInterval;
    private readonly string _defaultDescriptorPath;
    private readonly MdnsDiscoveryFilter? _filter;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly HashSet<string> _seenLocations = new(StringComparer.Ordinal);

    public MdnsDiscoveryTransport(
        ILogger logger,
        IReadOnlyList<string> browseProtocols,
        TimeSpan scanTime,
        TimeSpan pollInterval,
        string defaultDescriptorPath,
        MdnsDiscoveryFilter? filter = null)
    {
        _logger = logger;
        _browseProtocols = browseProtocols ?? throw new ArgumentNullException(nameof(browseProtocols));
        _scanTime = scanTime;
        _pollInterval = pollInterval;
        _defaultDescriptorPath = string.IsNullOrWhiteSpace(defaultDescriptorPath)
            ? "/device.xml"
            : defaultDescriptorPath;
        _filter = filter;
    }

    public string TransportType => "mdns";

    public event Action<IEnumerable<string>>? DevicesDiscovered;
    public event Action<string>? DeviceLost;

    public void Start()
    {
        var listed = string.Join(", ", _browseProtocols);
        _logger.LogInfo(
            $"mDNS: browsing [{listed}] every {_pollInterval.TotalSeconds:0}s (scan {_scanTime.TotalSeconds:0}s, default path {_defaultDescriptorPath})");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollTask = Task.Run(() => PollAsync(token), token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(8));
        }
        catch
        {
            // cancelled or faulted
        }

        _pollTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var urls = await ResolveAllProtocolsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var url in urls)
                {
                    if (!_seenLocations.Add(url))
                        continue;
                    _logger.LogInfo($"mDNS: discovered {url}");
                    DevicesDiscovered?.Invoke(new[] { url });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"mDNS: browse failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<IEnumerable<string>> DiscoverDevicesAsync()
    {
        var list = await ResolveAllProtocolsAsync(CancellationToken.None).ConfigureAwait(false);
        DevicesDiscovered?.Invoke(list);
        return list;
    }

    public void Advertise(string deviceDescription)
    {
        throw new NotImplementedException($"{nameof(MdnsDiscoveryTransport)} is browse-only.");
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task<List<string>> ResolveAllProtocolsAsync(CancellationToken cancellationToken)
    {
        var results = new List<string>();
        foreach (var protocol in _browseProtocols)
        {
            if (string.IsNullOrWhiteSpace(protocol))
                continue;

            IReadOnlyList<IZeroconfHost> hosts;
            try
            {
                hosts = await ZeroconfResolver
                    .ResolveAsync(protocol, _scanTime, retries: 2, retryDelayMilliseconds: 200, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"mDNS: resolve skipped for {protocol}: {ex.Message}");
                continue;
            }

            foreach (var host in hosts)
            {
                foreach (var kv in host.Services)
                {
                    foreach (var url in EnumerateLocationsForService(host, kv.Key, kv.Value, protocol))
                    {
                        if (!string.IsNullOrEmpty(url))
                            results.Add(url);
                    }
                }
            }
        }

        return results;
    }

    private IEnumerable<string> EnumerateLocationsForService(
        IZeroconfHost host,
        string serviceKey,
        IService svc,
        string browseProtocol)
    {
        var location = TryGetLocationFromTxt(svc);
        if (string.IsNullOrEmpty(location))
        {
            var ip = host.IPAddress ?? host.IPAddresses?.FirstOrDefault()?.ToString();
            if (string.IsNullOrEmpty(ip) || svc.Port <= 0)
                yield break;
            location = $"http://{ip}:{svc.Port}{_defaultDescriptorPath}";
        }

        var txt = MergeTxt(svc);
        var ep = new MdnsDiscoveredEndpoint(
            location,
            browseProtocol,
            serviceKey,
            instanceName: host.DisplayName,
            displayName: host.DisplayName,
            svc.Port,
            txt);

        if (_filter != null && !_filter.Predicate(ep))
            yield break;

        yield return location;
    }

    private static Dictionary<string, string?> MergeTxt(IService service)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in service.Properties)
        {
            foreach (var kv in set)
                d[kv.Key] = kv.Value;
        }

        return d;
    }

    private static string? TryGetLocationFromTxt(IService service)
    {
        foreach (var set in service.Properties)
        {
            if (set.TryGetValue("location", out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }

        return null;
    }
}
