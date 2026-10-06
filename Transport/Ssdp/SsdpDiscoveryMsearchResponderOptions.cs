namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// Optional SSDP responder behavior merged into <see cref="SsdpDiscoveryTransport"/> so only one UDP socket
/// binds to port 1900 (avoids losing unicast M-SEARCH replies to a competing receiver).
/// </summary>
/// <param name="DeviceUsnField">
/// Inserted into the SSDP USN line the same way as the legacy responder constructor argument (often a full USN fragment).
/// </param>
/// <param name="DeviceLocation">LOCATION header value (full URL).</param>
public sealed record SsdpDiscoveryMsearchResponderOptions(string DeviceUsnField, string DeviceLocation);
