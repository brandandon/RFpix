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
            return patterns.SelectMany(p => Directory.GetFiles("/dev", p)).Order()
                           .Select(p => new SerialPortInfo(p, null)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Array.Empty<SerialPortInfo>();
        }
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
