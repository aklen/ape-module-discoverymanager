using System.Net;
using System.Net.Sockets;
using System.Text;
using Ape.Core.Logging;
using Ape.Core.Network.Discovery;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// SSDP discovery: one UDP socket on port 1900 with <see cref="MulticastOption"/> membership on each eligible
/// IPv4 interface, periodic M-SEARCH on all interfaces, and
/// passive NOTIFY / search-response handling. Optional inbound M-SEARCH handling uses the same socket.
/// </summary>
public sealed class SsdpDiscoveryTransport : IDiscoveryTransport
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse(MulticastAddress), MulticastPort);

    private readonly ILogger _logger;
    private readonly string? _localDeviceUuid;
    private readonly string? _localIpAddress;
    private readonly SsdpDiscoveryMsearchResponderOptions? _msearchResponder;
    private readonly SsdpDiscoveryOptions _options;
    private readonly HashSet<string> _discoveredDevices = new(StringComparer.Ordinal);
    private readonly byte[] _msearchPayload = SsdpDatagramParser.BuildMSearchRootDevice();

    private Socket? _socket;
    private IReadOnlyList<IPAddress> _joinedInterfaces = Array.Empty<IPAddress>();
    private IReadOnlyList<SsdpIpv4Subnet> _vpnRewriteSubnets = Array.Empty<SsdpIpv4Subnet>();
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _msearchTask;
    private readonly object _searchSessionLock = new();
    private List<string>? _activeSearchBuffer;

    public string TransportType => "ssdp";

    public event Action<IEnumerable<string>>? DevicesDiscovered;
    public event Action<string>? DeviceLost;

    public SsdpDiscoveryTransport(
        ILogger logger,
        string? localDeviceUuid = null,
        string? localIpAddress = null,
        SsdpDiscoveryMsearchResponderOptions? msearchResponder = null,
        SsdpDiscoveryOptions? options = null)
    {
        _logger = logger;
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

            var eligible = SsdpInterfaceHelper.GetEligibleLocalIpv4UnicastAddresses();
            var joined = new List<IPAddress>();
            var group = IPAddress.Parse(MulticastAddress);

            if (eligible.Count > 0)
            {
                foreach (var local in eligible)
                {
                    try
                    {
                        socket.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(group, local));
                        joined.Add(local);
                        _logger.LogInfo($"SSDP Discovery: multicast joined on {local}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"SSDP Discovery: joinMulticastGroup failed on {local}: {ex.Message}");
                    }
                }
            }

            if (joined.Count == 0)
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(group));
                    _logger.LogInfo("SSDP Discovery: multicast joined on default interface (no eligible IPv4 NIC)");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"SSDP Discovery: default joinMulticastGroup failed: {ex.Message}");
                }
            }

            _joinedInterfaces = joined;
            _socket = socket;
            RefreshVpnRewriteSubnets();

            _logger.LogInfo(
                $"SSDP Discovery: UDP {MulticastPort} multicast {MulticastAddress} — {joined.Count} interface(s) for M-SEARCH egress");

            _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, token), token);
            _msearchTask = Task.Run(() => PeriodicMsearchAsync(socket, joined, token), token);

            SendMsearchAllInterfaces(socket, joined);
            SendVpnUnicastMsearch(socket);
        }
        catch (Exception ex)
        {
            _logger.LogError($"SSDP Discovery: failed to start: {ex.Message}");
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
            _socket?.Close();
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
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        _logger.LogInfo("SSDP Discovery: Device locator stopped.");
    }

    public async Task<IEnumerable<string>> DiscoverDevicesAsync()
    {
        _logger.LogInfo("SSDP Discovery: Active search (M-SEARCH)…");
        List<string> buffer;
        lock (_searchSessionLock)
        {
            buffer = new List<string>();
            _activeSearchBuffer = buffer;
        }

        try
        {
            if (_socket != null && _socket.IsBound)
            {
                SendMsearchAllInterfaces(_socket, _joinedInterfaces);
                SendVpnUnicastMsearch(_socket);
            }
            else
                _logger.LogWarning("SSDP Discovery: socket not bound; active search skipped");

            await Task.Delay(3500).ConfigureAwait(false);
        }
        finally
        {
            lock (_searchSessionLock)
            {
                _activeSearchBuffer = null;
            }
        }

        var distinct = buffer.Distinct(StringComparer.Ordinal).ToList();
        _logger.LogInfo($"SSDP Discovery: Active search collected {distinct.Count} LOCATION(s).");
        foreach (var loc in distinct)
            _logger.LogInfo($"SSDP Discovery: Device found at {loc}");

        // Events are raised from HandleDeviceAvailable as packets arrive; return value is for API callers.
        return distinct;
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

    private async Task PeriodicMsearchAsync(Socket socket, IReadOnlyList<IPAddress> joined, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                SendMsearchAllInterfaces(socket, joined);
                SendVpnUnicastMsearch(socket);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
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
        var subnets = new List<SsdpIpv4Subnet>(SsdpInterfaceHelper.GetLocalVpnSubnets());
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

        if (!message.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!message.Contains("ST: ssdp:all", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains($"ST: {SsdpApeDevice.Urn}", StringComparison.OrdinalIgnoreCase))
            return true;

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
                + _msearchResponder.DeviceLocation
                + "\r\n"
                + $"SERVER: {SsdpApeDevice.ServerHeader}\r\n"
                + "\r\n";
            var responseBytes = Encoding.UTF8.GetBytes(response);
            socket.SendTo(responseBytes, remoteEndPoint);
            _logger.LogDebug($"SSDP Discovery: responded to M-SEARCH from {remoteEndPoint}");
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

        if (SsdpDatagramParser.IsNotifyByeBye(firstLine, headers))
        {
            if (SsdpDatagramParser.TryGetLocation(headers, out var byeLoc))
                HandleDeviceByeBye(byeLoc);
            return;
        }

        if (!SsdpDatagramParser.ShouldReportDevice(firstLine, headers))
            return;

        if (!SsdpDatagramParser.TryGetLocation(headers, out var location))
            return;

        if (remoteEndPoint is IPEndPoint ipRemote)
        {
            var rewritten = SsdpLocationHelper.RewriteHostFromResponseSource(
                location,
                ipRemote.Address,
                _vpnRewriteSubnets,
                _options.RewriteLocationHostFromVpnResponse);
            if (!string.Equals(rewritten, location, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    $"SSDP Discovery: Rewrote VPN LOCATION host {location} -> {rewritten} (response from {ipRemote.Address})");
                location = rewritten;
            }
        }

        if (!headers.TryGetValue("usn", out var usn))
            usn = string.Empty;

        HandleDeviceAvailable(location, usn);
    }

    private void HandleDeviceByeBye(string deviceLocation)
    {
        if (string.IsNullOrEmpty(deviceLocation))
            return;
        if (!_discoveredDevices.Remove(deviceLocation))
            return;
        _logger.LogWarning($"SSDP Discovery: Device unavailable at {deviceLocation}");
        DeviceLost?.Invoke(deviceLocation);
    }

    private void HandleDeviceAvailable(string deviceLocation, string deviceUsn)
    {
        if (string.IsNullOrEmpty(deviceLocation))
            return;

        if (!string.IsNullOrEmpty(_localDeviceUuid) && deviceUsn.Contains(_localDeviceUuid, StringComparison.Ordinal))
        {
            _logger.LogDebug("SSDP Discovery: Ignoring self-discovery (UUID match)");
            return;
        }

        if (!string.IsNullOrEmpty(_localIpAddress) && deviceLocation.Contains(_localIpAddress, StringComparison.Ordinal))
        {
            _logger.LogDebug($"SSDP Discovery: Ignoring self-discovery (IP match: {_localIpAddress})");
            return;
        }

        if (deviceLocation.Contains("127.0.0.1", StringComparison.Ordinal) ||
            deviceLocation.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("SSDP Discovery: Ignoring localhost device");
            return;
        }

        lock (_searchSessionLock)
        {
            _activeSearchBuffer?.Add(deviceLocation);
        }

        if (_discoveredDevices.Contains(deviceLocation))
        {
            _logger.LogDebug($"SSDP Discovery: Device already discovered, skipping: {deviceLocation}");
            return;
        }

        _discoveredDevices.Add(deviceLocation);
        _logger.LogInfo($"SSDP Discovery: ✅ New remote device discovered at {deviceLocation}");
        DevicesDiscovered?.Invoke(new[] { deviceLocation });
    }
}
