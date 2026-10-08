using System.Globalization;
using System.Text;

namespace Ape.Module.DiscoveryManager.Transport.Ssdp;

/// <summary>
/// Minimal SSDP NOTIFY / M-SEARCH response parsing (LOCATION, NTS, first line).
/// </summary>
internal static class SsdpDatagramParser
{
    public static bool TryParseHeaders(ReadOnlySpan<byte> datagram, out Dictionary<string, string> headers, out string firstLine)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        firstLine = string.Empty;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(datagram);
        }
        catch
        {
            return false;
        }

        var parts = text.Split("\r\n", StringSplitOptions.None);
        if (parts.Length == 0)
            return false;

        firstLine = parts[0].Trim();

        foreach (var raw in parts.Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();
            if (key.Length > 0)
                headers[key.ToLowerInvariant()] = val;
        }

        return true;
    }

    /// <summary>
    /// Returns true if datagram should surface a device LOCATION (NOTIFY alive or HTTP 200 search response).
    /// </summary>
    public static bool ShouldReportDevice(string firstLine, IReadOnlyDictionary<string, string> headers)
    {
        if (firstLine.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase))
        {
            if (!headers.TryGetValue("nts", out var nts))
                return false;
            return nts.Contains("ssdp:alive", StringComparison.OrdinalIgnoreCase);
        }

        if (firstLine.Contains("200 OK", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// True when this host should unicast-reply. Own searches use <c>upnp:rootdevice</c>;
    /// <c>ssdp:all</c> and the Ape device URN are answered as well.
    /// </summary>
    public static bool IsAnswerableMSearch(string message)
    {
        if (!message.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase))
            return false;

        return message.Contains("ST: ssdp:all", StringComparison.OrdinalIgnoreCase)
               || message.Contains("ST: upnp:rootdevice", StringComparison.OrdinalIgnoreCase)
               || message.Contains($"ST: {SsdpApeDevice.Urn}", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNotifyByeBye(string firstLine, IReadOnlyDictionary<string, string> headers)
    {
        if (!firstLine.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase))
            return false;
        return headers.TryGetValue("nts", out var nts) &&
               nts.Contains("ssdp:byebye", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetLocation(IReadOnlyDictionary<string, string> headers, out string location)
    {
        location = string.Empty;
        if (!headers.TryGetValue("location", out var loc) || string.IsNullOrWhiteSpace(loc))
            return false;
        location = loc.Trim();
        return true;
    }

    public static byte[] BuildMSearchRootDevice()
    {
        const string msg =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: upnp:rootdevice\r\n" +
            "\r\n";
        return Encoding.UTF8.GetBytes(msg);
    }
}
