using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace WwanMessenger.Core.Modem;

public sealed record PortInfo(string PortName, string? FriendlyName, string Source);

/// <summary>Finds COM ports, preferring WWAN modem AT ports (Fibocom/Quectel/Sierra...).</summary>
public static class ModemDiscovery
{
    public static List<PortInfo> Discover()
    {
        var results = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

        // Friendly names via WMI (e.g. "Fibocom L860-GL AT Port (COM7)").
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"] as string;
                if (string.IsNullOrEmpty(name)) continue;
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (!m.Success) continue;
                var port = m.Groups[1].Value;
                results[port] = new PortInfo(port, name, "WMI");
            }
        }
        catch { /* WMI unavailable — fall back to bare enumeration */ }

        foreach (var port in SerialPort.GetPortNames())
            if (!results.ContainsKey(port))
                results[port] = new PortInfo(port, null, "Serial");

        // Modem-ish ports first: AT/Modem/diagnostic ports of WWAN modules.
        return results.Values
            .OrderByDescending(p => IsLikelyModemPort(p.FriendlyName))
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsLikelyModemPort(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName)) return false;
        return friendlyName.Contains("Fibocom", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Quectel", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Sierra", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("WWAN", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Mobile Broadband", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("AT Port", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Modem", StringComparison.OrdinalIgnoreCase);
    }
}
