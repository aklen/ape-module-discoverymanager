using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Ape.Core.Logging;
using Ape.Core.Network.Discovery;
using Ape.Module.DiscoveryManager.Graph;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// SSDP discovery: one UDP socket on port 1900 with <see cref="MulticastOption"/> membership on each eligible
/// IPv4 interface, periodic M-SEARCH on all interfaces, and
/// passive NOTIFY / search-response handling. Optional inbound M-SEARCH handling uses the same socket.
/// Membership follows interface changes: <see cref="NetworkChange.NetworkAddressChanged"/> plus the periodic search.
/// </summary>
public sealed class SsdpDiscoveryTransport : IDiscoveryTransport
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse(MulticastAddress), MulticastPort);

    private readonly ILogger _logger;
    private readonly IDiscoverySampleSink _sink;
    private readonly string? _localDeviceUuid;
    private readonly string? _localIpAddress;
    private readonly SsdpDiscoveryMsearchResponderOptions? _msearchResponder;
    private readonly SsdpDiscoveryOptions _options;
    private readonly byte[] _msearchPayload = SsdpDatagramParser.BuildMSearchRootDevice();

    private static readonly TimeSpan InterfaceRefreshDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _membershipLock = new();
    private Socket? _socket;
    private IReadOnlyList<IPAddress> _joinedInterfaces = Array.Empty<IPAddress>();
    private bool _defaultMembership;
    private bool _watchingInterfaces;
    private int _interfaceRefreshGeneration;
    private IReadOnlyList<SsdpIpv4Subnet> _vpnRewriteSubnets = Array.Empty<SsdpIpv4Subnet>();
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _msearchTask;

    public string TransportType => "ssdp";

#pragma warning disable CS0067 // The discovery plan raises these. This adapter only enqueues samples.
    public event Action<IEnumerable<string>>? DevicesDiscovered;
    public event Action<string>? DeviceLost;
#pragma warning restore CS0067

    internal bool RewriteVpnLocationHost => _options.RewriteLocationHostFromVpnResponse;

    internal IReadOnlyList<SsdpIpv4Subnet> VpnRewriteSubnets => _vpnRewriteSubnets;

    public SsdpDiscoveryTransport(
        ILogger logger,
        IDiscoverySampleSink sink,
        string? localDeviceUuid = null,
        string? localIpAddress = null,
        SsdpDiscoveryMsearchResponderOptions? msearchResponder = null,
        SsdpDiscoveryOptions? options = null)
    {
        _logger = logger;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _localDeviceUuid = localDeviceUuid;
        _localIpAddress = localIpAddress;
        _msearchResponder = msearchResponder;
        _options = options ?? SsdpDiscoveryOptions.Parse(null);
        var responderNote = _msearchResponder != null ? ", M-SEARCH responder on shared socket" : string.Empty;
        var vpnNote = _options.VpnUnicastProbeEnabled ? ", VPN unicast M-SEARCH fallback enabled" : string.Empty;
        _logger.LogInfo(
            $"SsdpDiscoveryTransport initialized (local UUID: {_localDeviceUuid ?? "none"}, local IP: {_localIpAddress ?? "none"}{responderNote}{vpnNote})");
    }

    public void Start()
    {
        _logger.LogInfo("SSDP Discovery: Starting multi-interface listener…");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            _socket = socket;
            RefreshVpnRewriteSubnets();
            WatchInterfaces();

            _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, token), token);
            _msearchTask = Task.Run(() => PeriodicMsearchAsync(token), token);

            RefreshMembership(onlySearchNew: false);
        }
        catch (Exception ex)
        {
            _logger.LogError($"SSDP Discovery: failed to start: {ex.Message}");
            UnwatchInterfaces();
            try
            {
                _socket?.Close();
            }
            catch
            {
                // ignore
            }

            _socket = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Stop()
    {
        _logger.LogInfo("SSDP Discovery: Stopping device locator…");
        UnwatchInterfaces();
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        Socket? socket;
        lock (_membershipLock)
        {
            socket = _socket;
            _socket = null;
        }

        try
        {
            socket?.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }

        try
        {
            _msearchTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }

        _receiveTask = null;
        _msearchTask = null;
        _cts?.Dispose();
        _cts = null;
        _logger.LogInfo("SSDP Discovery: Device locator stopped.");
    }

    public async Task<IEnumerable<string>> DiscoverDevicesAsync()
    {
        _logger.LogInfo("SSDP Discovery: Active search (M-SEARCH)…");
        if (_socket != null && _socket.IsBound)
            RefreshMembership(onlySearchNew: false);
        else
            _logger.LogWarning("SSDP Discovery: socket not bound; active search skipped");

        await Task.Delay(3500).ConfigureAwait(false);

        // Accepted devices are published by the discovery plan on the host frame, not from this wait.
        return [];
    }

    public void Advertise(string deviceDescription)
    {
        throw new NotImplementedException("Use SsdpAdvertisementTransport for advertising.");
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken)
                    .ConfigureAwait(false);
                if (result.ReceivedBytes == 0)
                    continue;

                var span = buffer.AsSpan(0, result.ReceivedBytes);
                if (!TryRespondToInboundMSearch(socket, span, result.RemoteEndPoint))
                    ProcessDatagram(span, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    _logger.LogWarning($"SSDP Discovery: receive error: {ex.Message}");
            }
        }
    }

    private async Task PeriodicMsearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                RefreshMembership(onlySearchNew: false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void WatchInterfaces()
    {
        if (_watchingInterfaces)
            return;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _watchingInterfaces = true;
    }

    private void UnwatchInterfaces()
    {
        if (!_watchingInterfaces)
            return;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _watchingInterfaces = false;
        Interlocked.Increment(ref _interfaceRefreshGeneration);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var generation = Interlocked.Increment(ref _interfaceRefreshGeneration);
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(InterfaceRefreshDelay, token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _interfaceRefreshGeneration) || !_watchingInterfaces)
                    return;

                RefreshMembership(onlySearchNew: true);
            }
            catch (OperationCanceledException)
            {
                // shutdown or a newer address-change burst
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"SSDP Discovery: interface refresh failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Re-reads eligible IPv4 interfaces, joins new ones, drops ones that went away,
    /// then sends M-SEARCH. Address-change bursts search only the newly joined interfaces.
    /// </summary>
    private void RefreshMembership(bool onlySearchNew)
    {
        var socket = _socket;
        if (socket == null || !socket.IsBound)
            return;

        List<IPAddress> added;
        IReadOnlyList<IPAddress> targets;
        lock (_membershipLock)
        {
            if (_socket == null)
                return;

            added = ApplyMembership(socket);
            targets = onlySearchNew ? added : _joinedInterfaces.ToArray();
            if (!onlySearchNew || added.Count > 0)
                SendMsearchAllInterfaces(socket, targets);
        }

        if (!onlySearchNew || added.Count > 0)
            SendVpnUnicastMsearch(socket);

        if (added.Count > 0)
            _logger.LogInfo($"SSDP Discovery: M-SEARCH on new interface(s): {string.Join(", ", added)}");
    }

    private List<IPAddress> ApplyMembership(Socket socket)
    {
        var eligible = SsdpInterfaceHelper.GetEligibleLocalIpv4UnicastAddresses();
        var plan = SsdpInterfaceDiff.Plan(_joinedInterfaces, _defaultMembership, eligible);
        if (!plan.HasChanges)
            return [];

        var group = IPAddress.Parse(MulticastAddress);
        foreach (var address in plan.Leave)
            TryDrop(socket, new MulticastOption(group, address), address);

        if (plan.LeaveDefault && TryDrop(socket, new MulticastOption(group), address: null))
            _defaultMembership = false;

        var added = new List<IPAddress>();
        foreach (var address in plan.Join)
        {
            if (TryJoin(socket, new MulticastOption(group, address), address))
                added.Add(address);
        }

        if (plan.JoinDefault)
            _defaultMembership = TryJoin(socket, new MulticastOption(group), address: null);

        var left = new HashSet<IPAddress>(plan.Leave);
        var joined = new List<IPAddress>(_joinedInterfaces.Count);
        foreach (var address in _joinedInterfaces)
        {
            if (!left.Contains(address))
                joined.Add(address);
        }

        joined.AddRange(added);
        _joinedInterfaces = joined;
        var membership = joined.Count > 0 ? string.Join(", ", joined) : "none";
        if (_defaultMembership)
            membership = joined.Count > 0 ? membership + " + default" : "default";
        _logger.LogInfo(
            $"SSDP Discovery: UDP {MulticastPort} multicast {MulticastAddress} — membership now {membership}");
        return added;
    }

    private bool TryJoin(Socket socket, MulticastOption membership, IPAddress? address)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, membership);
            _logger.LogInfo(address == null
                ? "SSDP Discovery: multicast joined on default interface (no eligible IPv4 NIC)"
                : $"SSDP Discovery: multicast joined on {address}");
            return true;
        }
        catch (Exception ex)
        {
            var where = address?.ToString() ?? "default interface";
            _logger.LogWarning($"SSDP Discovery: joinMulticastGroup failed on {where}: {ex.Message}");
            return false;
        }
    }

    private bool TryDrop(Socket socket, MulticastOption membership, IPAddress? address)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, membership);
            _logger.LogInfo(address == null
                ? "SSDP Discovery: left default multicast membership"
                : $"SSDP Discovery: multicast left {address}");
            return true;
        }
        catch (Exception ex)
        {
            var where = address?.ToString() ?? "default interface";
            _logger.LogDebug($"SSDP Discovery: drop membership failed on {where}: {ex.Message}");
            return false;
        }
    }

    private void SendVpnUnicastMsearch(Socket socket)
    {
        if (!_options.VpnUnicastProbeEnabled)
            return;

        RefreshVpnRewriteSubnets();
        var localVpnSubnets = CollectProbeSubnets();
        if (localVpnSubnets.Count == 0 && _options.ExtraProbeHosts.Count == 0)
            return;

        var localHostIds = new HashSet<uint>();
        foreach (var local in SsdpInterfaceHelper.GetEligibleLocalIpv4UnicastAddresses())
        {
            if (local.AddressFamily == AddressFamily.InterNetwork)
                localHostIds.Add(IpToUInt32(local));
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subnet in localVpnSubnets)
        {
            foreach (var host in SsdpSubnetHelper.EnumerateHostAddresses(
                         subnet,
                         localHostIds,
                         _options.MaxHostsPerSubnet))
            {
                targets.Add(host.ToString());
            }
        }

        foreach (var raw in _options.ExtraProbeHosts)
        {
            if (IPAddress.TryParse(raw.Trim(), out var host) && host.AddressFamily == AddressFamily.InterNetwork)
                targets.Add(host.ToString());
        }

        if (targets.Count == 0)
            return;

        var sent = 0;
        foreach (var host in targets)
        {
            try
            {
                if (!IPAddress.TryParse(host, out var targetIp))
                    continue;

                socket.SendTo(_msearchPayload, SocketFlags.None, new IPEndPoint(targetIp, MulticastPort));
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"SSDP Discovery: VPN unicast M-SEARCH send failed to {host}: {ex.Message}");
            }
        }

        _logger.LogInfo(
            $"SSDP Discovery: VPN unicast M-SEARCH sent to {sent} host(s) across {localVpnSubnets.Count} VPN subnet(s)");
    }

    private List<SsdpIpv4Subnet> CollectProbeSubnets()
    {
        var subnets = new List<SsdpIpv4Subnet>();

        foreach (var cidr in _options.ProbeSubnets)
        {
            if (SsdpSubnetHelper.TryParseCidr(cidr, out var subnet))
                subnets.Add(subnet);
            else
                _logger.LogWarning($"SSDP Discovery: ignoring invalid VPN probe subnet '{cidr}'");
        }

        if (_options.ProbeSubnets.Count == 0)
            subnets.AddRange(SsdpInterfaceHelper.GetLocalVpnSubnets());

        return subnets;
    }

    private void RefreshVpnRewriteSubnets()
    {
        var subnets = SsdpInterfaceHelper.GetLocalIpv4Bindings()
            .Select(binding => binding.Subnet)
            .ToList();
        foreach (var cidr in _options.ProbeSubnets)
        {
            if (SsdpSubnetHelper.TryParseCidr(cidr, out var subnet))
                subnets.Add(subnet);
        }

        _vpnRewriteSubnets = subnets;
    }

    private static uint IpToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private void SendMsearchAllInterfaces(Socket socket, IReadOnlyList<IPAddress> joined)
    {
        try
        {
            if (joined.Count > 0)
            {
                foreach (var local in joined)
                {
                    try
                    {
                        socket.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.MulticastInterface,
                            local.GetAddressBytes());
                        socket.SendTo(_msearchPayload, SocketFlags.None, MulticastEndpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"SSDP Discovery: M-SEARCH send failed on {local}: {ex.Message}");
                    }
                }
            }
            else
            {
                socket.SendTo(_msearchPayload, SocketFlags.None, MulticastEndpoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"SSDP Discovery: M-SEARCH send failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Answers matching inbound M-SEARCH on the discovery socket (unicast reply on port 1900).
    /// </summary>
    /// <returns><c>true</c> if the datagram was an M-SEARCH we handled (including no-op ST mismatch after detect).</returns>
    private bool TryRespondToInboundMSearch(Socket socket, ReadOnlySpan<byte> data, EndPoint remoteEndPoint)
    {
        if (_msearchResponder == null)
            return false;

        if (!StartsWithAsciiIgnoreCase(data, "M-SEARCH"))
            return false;

        string message;
        try
        {
            message = Encoding.UTF8.GetString(data);
        }
        catch
        {
            return true;
        }

        if (!SsdpDatagramParser.IsAnswerableMSearch(message))
            return true;

        var remoteAddress = remoteEndPoint is IPEndPoint ipEndPoint ? ipEndPoint.Address : null;
        var location = SsdpLocationHelper.LocationFacingRemote(
            _msearchResponder.DeviceLocation,
            remoteAddress,
            SsdpInterfaceHelper.GetLocalIpv4Bindings());

        try
        {
            var response =
                "HTTP/1.1 200 OK\r\n"
                + "CACHE-CONTROL: max-age=1800\r\n"
                + $"ST: {SsdpApeDevice.Urn}\r\n"
                + "USN: "
                + _msearchResponder.DeviceUsnField
                + "\r\n"
                + "LOCATION: "
                + location
                + "\r\n"
                + $"SERVER: {SsdpApeDevice.ServerHeader}\r\n"
                + "\r\n";
            var responseBytes = Encoding.UTF8.GetBytes(response);
            socket.SendTo(responseBytes, remoteEndPoint);
            _logger.LogDebug($"SSDP Discovery: responded to M-SEARCH from {remoteEndPoint} with LOCATION {location}");
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"SSDP Discovery: M-SEARCH reply failed: {ex.Message}");
        }

        return true;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> data, string asciiPrefix)
    {
        if (data.Length < asciiPrefix.Length)
            return false;
        for (var i = 0; i < asciiPrefix.Length; i++)
        {
            var a = data[i];
            var b = (byte)asciiPrefix[i];
            if (a == b)
                continue;
            if (a is >= (byte)'a' and <= (byte)'z' && a - 32 == b)
                continue;
            if (b is >= (byte)'a' and <= (byte)'z' && b - 32 == a)
                continue;
            return false;
        }

        return true;
    }

    private void ProcessDatagram(ReadOnlySpan<byte> data, EndPoint remoteEndPoint)
    {
        if (!SsdpDatagramParser.TryParseHeaders(data, out var headers, out var firstLine))
            return;

        var remote = remoteEndPoint is IPEndPoint ipRemote ? ipRemote.Address.ToString() : null;

        if (SsdpDatagramParser.IsNotifyByeBye(firstLine, headers))
        {
            if (SsdpDatagramParser.TryGetLocation(headers, out var byeLoc))
            {
                _sink.Submit(new DiscoverySample
                {
                    Source = DiscoverySources.Ssdp,
                    Kind = DiscoverySampleKind.ByeBye,
                    Location = byeLoc,
                    RemoteAddress = remote,
                });
            }

            return;
        }

        if (!SsdpDatagramParser.ShouldReportDevice(firstLine, headers))
            return;

        if (!SsdpDatagramParser.TryGetLocation(headers, out var location))
            return;

        if (!headers.TryGetValue("usn", out var usn))
            usn = string.Empty;

        _sink.Submit(new DiscoverySample
        {
            Source = DiscoverySources.Ssdp,
            Kind = DiscoverySampleKind.Alive,
            Location = location,
            Usn = usn,
            RemoteAddress = remote,
        });
    }
}
