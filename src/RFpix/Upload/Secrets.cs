// Secrets.cs - keeps the FTP password unreadable in RFpix's settings file. On Windows it's
// encrypted for your Windows account (only you, signed in on this computer, can read it back).

using System.Security.Cryptography;
using System.Text;

namespace RFpix.Upload;

public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RFpix FTP password");

    public static string? Protect(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var bytes = Encoding.UTF8.GetBytes(password);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        return "plain:" + Convert.ToBase64String(bytes);   // (Linux and Mac: a keyring comes later)
    }

    public static string Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.CurrentUser));
            if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { }
        return "";   // made for another Windows account, or damaged: type it again
    }
}
