// SerialPorts.cs - lists the computer's serial (COM) ports, with the names Windows gives them.
//
// The FT-991A appears as two ports, "Silicon Labs Dual CP2105 USB to UART Bridge:
// Enhanced COM Port" and "... Standard COM Port"; the IC-9700 as two "Silicon Labs
// CP210x" ports. Showing those names makes the right one easy to pick.
// Windows keeps them in its registry: HARDWARE\DEVICEMAP\SERIALCOMM lists the ports
// present now, and each device's entry under SYSTEM\CurrentControlSet\Enum holds
// its name and port.

using Microsoft.Win32;

namespace RFpix.Radio;

/// <summary>A serial port, e.g. Name "COM4", Description "Silicon Labs ... Enhanced COM Port".</summary>
public sealed record SerialPortInfo(string Name, string? Description)
{
    public override string ToString() => Description is null ? Name : $"{Name}  ({Description})";
}

public static class SerialPorts
{
    public static IReadOnlyList<SerialPortInfo> List()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ListWindows();
            // Linux: USB serial adapters; Mac: "call-out" devices.
            var patterns = OperatingSystem.IsMacOS() ? new[] { "cu.*" } : new[] { "ttyUSB*", "ttyACM*" };
            var names = OperatingSystem.IsLinux() ? LinuxNames() : new Dictionary<string, string>();
            return patterns.SelectMany(p => Directory.GetFiles("/dev", p)).Order()
                           .Select(p => new SerialPortInfo(p, names.GetValueOrDefault(p))).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Array.Empty<SerialPortInfo>();
        }
    }

    /// <summary>Linux names each USB serial port in /dev/serial/by-id, after the adapter: the FT-991A's
    /// "usb-Silicon_Labs_CP2105_Dual_USB_to_UART_Bridge_Controller_...-if00-port0" (if00 is its
    /// Enhanced port, if01 Standard). Shown tidied, e.g. "Silicon Labs CP2105 Dual USB to UART
    /// Bridge Controller (Enhanced)".</summary>
    private static Dictionary<string, string> LinuxNames()
    {
        var names = new Dictionary<string, string>();
        const string byId = "/dev/serial/by-id";
        if (!Directory.Exists(byId)) return names;
        foreach (string link in Directory.GetFiles(byId))
        {
            try
            {
                if (new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true) is not { } target) continue;
                names[target.FullName] = TidyLinuxName(Path.GetFileName(link));
            }
            catch (IOException) { }
        }
        return names;
    }

    /// <summary>"usb-Silicon_Labs_CP2105_Dual_..._Controller_0123ABCD-if00-port0" to "Silicon Labs CP2105 Dual ... Controller (Enhanced)".</summary>
    internal static string TidyLinuxName(string id)
    {
        string name = id.StartsWith("usb-", StringComparison.Ordinal) ? id[4..] : id;
        string? port = null;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"-if(\d\d)(-port\d+)?$");
        if (m.Success)
        {
            name = name[..m.Index];
            if (name.Contains("CP2105", StringComparison.OrdinalIgnoreCase)) port = m.Groups[1].Value == "00" ? "Enhanced" : "Standard";
            else if (m.Groups[1].Value != "00") port = $"port {int.Parse(m.Groups[1].Value) + 1}";
        }
        // The serial number at the end (after the last "_") isn't wanted.
        int last = name.LastIndexOf('_');
        if (last > 0 && name[(last + 1)..].All(char.IsLetterOrDigit) && name[(last + 1)..].Any(char.IsDigit)) name = name[..last];
        name = name.Replace('_', ' ').Trim();
        return port is null ? name : $"{name} ({port})";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<SerialPortInfo> ListWindows()
    {
        var present = new List<string>();
        using (var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
            if (map is not null)
                foreach (var value in map.GetValueNames())
                    if (map.GetValue(value) is string port) present.Add(port);

        // Names of USB serial devices, by port.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bus in new[] { "USB", "FTDIBUS", "SiLabser" })
        {
            try { AddNames(bus, names); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        return present.Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[3..], out int n) ? n : int.MaxValue)
                      .Select(p => new SerialPortInfo(p, names.GetValueOrDefault(p)))
                      .ToList();
    }

    // The names are a nicety: if part of the registry can't be read, the ports are still listed.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddNames(string bus, Dictionary<string, string> names)
    {
        using var busKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{bus}");
        if (busKey is null) return;
        foreach (var deviceId in busKey.GetSubKeyNames())
        {
            using var device = busKey.OpenSubKey(deviceId);
            if (device is null) continue;
            foreach (var instanceId in device.GetSubKeyNames())
            {
                using var instance = device.OpenSubKey(instanceId);
                using var parameters = instance?.OpenSubKey("Device Parameters");
                if (parameters?.GetValue("PortName") is string port && instance?.GetValue("FriendlyName") is string friendly)
                    names[port] = friendly.Replace($" ({port})", "");   // the name without "(COM4)" on the end
            }
        }
    }
}
