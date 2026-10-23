// Secrets.cs - keeps the FTP password unreadable in RFpix's settings file.
//   Windows: encrypted for your Windows account (only you, signed in on this computer, can
//            read it back), "dpapi:".
//   Mac and Linux: encrypted (AES-GCM) with a key made the first time, kept in a file of its
//            own next to settings.json that only your account can read, "aes:". The Mac's
//            Keychain isn't used: until RFpix is signed by Apple, every new version would ask
//            for your Mac password to reach it.
// Passwords saved before as "plain:" (older Mac and Linux versions) can still be read, and are
// saved encrypted the next time they're saved.

using System.Security.Cryptography;
using System.Text;

namespace RFpix.Upload;

public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RFpix FTP password");

    /// <summary>The folder holding the key file (Mac and Linux). Tests point it elsewhere.</summary>
    internal static string KeyFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RFpix");

    private static string KeyFile => Path.Combine(KeyFolder, "password.key");

    public static string? Protect(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var bytes = Encoding.UTF8.GetBytes(password);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        return "aes:" + Convert.ToBase64String(Encrypt(bytes, Key(create: true)!));
    }

    public static string Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.CurrentUser));
            if (stored.StartsWith("aes:") && Key(create: false) is byte[] key)
                return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(stored[4..]), key));
            if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException) { }
        return "";   // made for another account or computer, or damaged: type it again
    }

    // ---- Mac and Linux ----

    // nonce (12 bytes), tag (16 bytes), then the encrypted password
    private static byte[] Encrypt(byte[] plain, byte[] key)
    {
        var result = new byte[12 + 16 + plain.Length];
        var nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, result.AsSpan(28), result.AsSpan(12, 16), Entropy);
        return result;
    }

    private static byte[] Decrypt(byte[] data, byte[] key)
    {
        if (data.Length < 28) throw new CryptographicException("Too short.");
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Entropy);
        return plain;
    }

    /// <summary>The key, read from its file; made (readable by this account only) if asked and missing.</summary>
    private static byte[]? Key(bool create)
    {
        string file = KeyFile;
        if (File.Exists(file))
        {
            var key = File.ReadAllBytes(file);
            if (key.Length == 32) return key;
            if (!create) return null;
        }
        else if (!create) return null;

        var made = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(KeyFolder);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(file, options)) stream.Write(made);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return made;
    }
}
