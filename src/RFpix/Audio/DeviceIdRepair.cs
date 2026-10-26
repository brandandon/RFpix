// DeviceIdRepair.cs - mends the sound device choices saved on a Mac by RFpix 0.5.2 to 0.6.0.
//
// Those versions read the Mac's device IDs wrongly: each one came out as the first half of its
// letters' bytes, one byte to a letter ("BuiltInMicrophoneDevice" as "B\0u\0i\0l\0t\0I\0n\0M\0i\0c\0r\0o").
// The same garbling of a device's real ID gives exactly the saved one, so the saved choice can be
// matched to the device it meant, and put right.

using System.Text;

namespace RFpix.Audio;

public static class DeviceIdRepair
{
    /// <summary>A saved ID garbled that way (real IDs never contain a zero character).</summary>
    public static bool LooksGarbled(string? id) => id is not null && id.Contains('\0');

    /// <summary>The garbled form of a real ID.</summary>
    public static string Garbled(string id) =>
        new(Encoding.Unicode.GetBytes(id).Take(id.Length).Select(b => (char)b).ToArray());

    /// <summary>The device a garbled ID meant, among those connected now (null if none).</summary>
    public static AudioDeviceInfo? Find(string garbled, IEnumerable<AudioDeviceInfo> devices) =>
        devices.FirstOrDefault(d => Garbled(d.Id) == garbled);
}
