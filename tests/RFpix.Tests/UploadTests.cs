// UploadTests.cs - checks the website upload (with a pretend server) and the time stamp.

using RFpix.Imaging;
using RFpix.Upload;
using Xunit;

namespace RFpix.Tests;

public class UploadTests
{
    /// <summary>A server folder in memory.</summary>
    private sealed class FakeServer : IRemoteFolder
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public bool Fail, ShortUpload;
        public int Connections;
        public string WorkingFolder() => "/";
        public IReadOnlyList<string> List() => Files.Keys.ToList();
        public void Put(string localPath, string remoteName)
        {
            if (Fail) throw new IOException("Connection refused.");
            var data = File.ReadAllBytes(localPath);
            Files[remoteName] = ShortUpload ? data[..(data.Length / 2)] : data;
        }
        public long? Size(string remoteName) => Files.TryGetValue(remoteName, out var d) ? d.Length : null;
        public void Delete(string remoteName) => Files.Remove(remoteName);
        public void Rename(string from, string to) { Files[to] = Files[from]; Files.Remove(from); }
        public void Dispose() { }
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rfpix-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static (FolderUploader, FakeServer, UploadSettings) Make(string folder, int count = 3)
    {
        var server = new FakeServer();
        var settings = new UploadSettings { Folder = folder, Host = "example.com", Username = "u", ImageCount = count, StableSeconds = 3 };
        return (new FolderUploader(settings, _ => { server.Connections++; return server; }), server, settings);
    }

    [Fact]
    public void The_newest_picture_is_image1_and_the_others_move_up()
    {
        string folder = NewFolder();
        try
        {
            var (uploader, server, _) = Make(folder, count: 3);
            for (byte n = 1; n <= 4; n++)
            {
                string path = Path.Combine(folder, $"pic{n}.jpg");
                File.WriteAllBytes(path, new[] { n, n, n });
                uploader.UploadOne(path);
            }
            Assert.Equal(new byte[] { 4, 4, 4 }, server.Files["image1.jpg"]);
            Assert.Equal(new byte[] { 3, 3, 3 }, server.Files["image2.jpg"]);
            Assert.Equal(new byte[] { 2, 2, 2 }, server.Files["image3.jpg"]);
            Assert.Equal(3, server.Files.Count);   // the oldest is gone, and no temporary file is left
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_short_upload_leaves_the_website_as_it_was()
    {
        string folder = NewFolder();
        try
        {
            var (uploader, server, _) = Make(folder);
            server.Files["image1.jpg"] = new byte[] { 9 };
            string path = Path.Combine(folder, "new.jpg");
            File.WriteAllBytes(path, new byte[100]);
            server.ShortUpload = true;
            Assert.Throws<IOException>(() => uploader.UploadOne(path));
            Assert.Equal(new byte[] { 9 }, server.Files["image1.jpg"]);
            Assert.False(server.Files.ContainsKey(FolderUploader.TempRemoteName));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_picture_is_uploaded_once_it_stops_changing_then_archived()
    {
        string folder = NewFolder();
        try
        {
            var (uploader, server, _) = Make(folder);
            string path = Path.Combine(folder, "20261020_194212.jpg");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a picture");
            var t = DateTime.UtcNow;
            uploader.Look(t);                      // first seen
            uploader.Look(t.AddSeconds(1));        // not settled for 3 seconds yet
            Assert.Empty(server.Files);
            uploader.Look(t.AddSeconds(4));
            Assert.Equal(new byte[] { 1, 2, 3 }, server.Files["image1.jpg"]);
            Assert.False(File.Exists(path));
            var archived = Directory.GetFiles(Path.Combine(folder, "Archives"));
            Assert.Single(archived);
            Assert.Matches(@"\d{4}-\d{2}-\d{2}_\d{6}\.jpg$", archived[0]);
            Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void When_the_server_cant_be_reached_it_tries_again_30_seconds_later()
    {
        string folder = NewFolder();
        try
        {
            var (uploader, server, _) = Make(folder);
            string path = Path.Combine(folder, "a.jpg");
            File.WriteAllBytes(path, new byte[] { 5 });
            server.Fail = true;
            var t = DateTime.UtcNow;
            uploader.Look(t);
            uploader.Look(t.AddSeconds(4));                 // fails
            Assert.True(File.Exists(path));
            server.Fail = false;
            uploader.Look(t.AddSeconds(10));                // too soon to try again
            Assert.Empty(server.Files);
            uploader.Look(t.AddSeconds(40));
            Assert.Single(server.Files);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Settings_problems_are_explained()
    {
        Assert.NotNull(new UploadSettings().Problem());   // no server yet
        Assert.Null(new UploadSettings { Host = "ftp.example.com", Username = "n0call" }.Problem());
        Assert.Equal("image12.jpg", new UploadSettings().RemoteName(12));
        Assert.Equal(@"C:\FTPIMAGE", new UploadSettings().Folder);
    }

    [Fact]
    public void The_time_stamp_is_written_in_the_lower_left_corner_as_MMSSTV_does()
    {
        Assert.Equal("OCT-19-2026 2141", TimeStamp.Text(new DateTime(2026, 10, 19, 21, 41, 5)));
        var bgra = new byte[4 * 320 * 256];
        Array.Fill(bgra, (byte)128);
        var stamped = TimeStamp.Stamp(new DecodedPicture(320, 256, bgra), "OCT-19-2026 2141");
        byte At(int x, int y) => stamped.Bgra[4 * (y * 320 + x)];
        Assert.Equal(0, At(1, 255));                       // the black strip at the bottom left
        Assert.Equal(128, At(300, 255));                   // the right of the bottom row untouched
        Assert.Equal(128, At(1, 200));                     // and above the strip
        int white = 0;
        for (int y = 240; y < 256; y++) for (int x = 0; x < 120; x++) if (At(x, y) == 255) white++;
        Assert.True(white > 100, $"{white} white pixels");  // the letters
        Assert.Equal(128, bgra[0]);                        // the original is unchanged
    }

    [Fact]
    public void Digital_and_analog_copies_go_to_their_own_folders_here_and_on_the_server()
    {
        var s = new UploadSettings { Folder = @"D:\Copies", RemoteFolder = "/sstv/digital", RemoteFolderAnalog = "/sstv/analog", ImageCount = 3, ImageCountAnalog = 12 };
        Assert.Equal(Path.Combine(@"D:\Copies", "Digital"), s.CopyFolder(analog: false));
        Assert.Equal(Path.Combine(@"D:\Copies", "Analog"), s.For(analog: true).Folder);
        Assert.Equal(3, s.For(analog: false).ImageCount);
        Assert.Equal(12, s.For(analog: true).ImageCount);
        Assert.Equal("/sstv/digital", s.For(analog: false).RemoteFolder);
        Assert.Equal("/sstv/analog", s.For(analog: true).RemoteFolder);
        Assert.Equal(@"D:\Copies", s.Folder);   // the settings themselves are unchanged
    }
}
