using System.Net;
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

    private Socket? _socket;
    private IReadOnlyList<IPAddress> _joinedInterfaces = Array.Empty<IPAddress>();
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
        if (_socket != null && _socket.IsBound)
        {
            SendMsearchAllInterfaces(_socket, _joinedInterfaces);
            SendVpnUnicastMsearch(_socket);
        }
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
