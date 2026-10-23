// SecretsTests.cs - the FTP password is kept encrypted in the settings file, and comes back.

using System.Text;
using RFpix.Upload;
using Xunit;

namespace RFpix.Tests;

public class SecretsTests
{
    [Fact]
    public void A_password_comes_back_as_it_was_typed()
    {
        WithKeyFolder(() =>
        {
            string stored = Secrets.Protect("pa55 wörd!")!;
            Assert.DoesNotContain("pa55", stored);
            Assert.Equal("pa55 wörd!", Secrets.Reveal(stored));
            Assert.Null(Secrets.Protect(""));
            Assert.Equal("", Secrets.Reveal(null));
        });
    }

    [Fact]
    public void On_the_Mac_the_key_file_is_readable_only_by_you()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows encrypts for your account instead
        WithKeyFolder(folder =>
        {
            string stored = Secrets.Protect("secret")!;
            Assert.StartsWith("aes:", stored);
            var mode = File.GetUnixFileMode(Path.Combine(folder, "password.key"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            // The same password twice is stored differently (a fresh random start each time).
            Assert.False(stored == Secrets.Protect("secret"));
        });
    }

    [Fact]
    public void Older_and_damaged_passwords_are_handled()
    {
        WithKeyFolder(folder =>
        {
            // Saved by older Mac and Linux versions: still readable.
            Assert.Equal("old one", Secrets.Reveal("plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("old one"))));
            if (OperatingSystem.IsWindows()) return;
            string stored = Secrets.Protect("secret")!;
            // Damaged, or the key replaced: empty, so it's typed again (no crash).
            Assert.Equal("", Secrets.Reveal(stored[..^4] + "AAAA"));
            Assert.Equal("", Secrets.Reveal("aes:not base 64!"));
            File.WriteAllBytes(Path.Combine(folder, "password.key"), new byte[32]);
            Assert.Equal("", Secrets.Reveal(stored));
            File.Delete(Path.Combine(folder, "password.key"));
            Assert.Equal("", Secrets.Reveal(stored));
        });
    }

    private static readonly object OneAtATime = new();

    private static void WithKeyFolder(Action<string> test)
    {
        lock (OneAtATime)
        {
            string old = Secrets.KeyFolder;
            string folder = Path.Combine(Path.GetTempPath(), "rfpix-secrets-" + Guid.NewGuid().ToString("N"));
            Secrets.KeyFolder = folder;
            try { test(folder); }
            finally
            {
                Secrets.KeyFolder = old;
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }

    private static void WithKeyFolder(Action test) => WithKeyFolder(_ => test());
}
