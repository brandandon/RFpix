// PictureFolderTests.cs - moving the Gallery's pictures to a new pictures folder, safely.

using RFpix.Gallery;
using Xunit;

namespace RFpix.Tests;

public class PictureFolderTests
{
    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rfpix-pictures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Put(string root, string relative, byte value)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new[] { value, value, value });
        return path;
    }

    [Fact]
    public void Pictures_and_their_details_move_together()
    {
        string old = NewFolder(), fresh = NewFolder();
        try
        {
            Put(old, Path.Combine("Received", "Digital", "2026-10-20 1942 W9ABC tower.jp2"), 1);
            Put(old, Path.Combine("Received", "Analog", "2026-10-20 2010 N0CALL SSTV Martin 1.png"), 2);
            Put(old, Path.Combine("Sent", "2026-10-21 1900 cq.jpg"), 3);
            string digital = Path.Combine(old, "Received", "Digital");
            GalleryIndex.Add(digital, new GalleryEntry("2026-10-20 1942 W9ABC tower.jp2", new DateTime(2026, 10, 20, 19, 42, 5),
                "tower.jp2", "W9ABC", "Mode B", QualityDb: 17.2));
            Assert.Equal(3, PictureFolders.CountPictures(old));

            var result = PictureFolders.Move(old, fresh);

            Assert.Null(result.Problem);
            Assert.Equal(3, result.Moved);
            Assert.Equal(3, PictureFolders.CountPictures(fresh));
            Assert.Equal(0, PictureFolders.CountPictures(old));
            var moved = Assert.Single(GalleryIndex.List(Path.Combine(fresh, "Received", "Digital")));
            Assert.Equal(17.2, moved.QualityDb);   // the details came too
            Assert.Equal("W9ABC", moved.Callsign);
            Assert.True(File.Exists(Path.Combine(fresh, "Received", "Digital", GalleryIndex.IndexFileName)));
            Assert.False(Directory.Exists(Path.Combine(old, "Received")));   // emptied folders tidied away
        }
        finally { Directory.Delete(old, true); Directory.Delete(fresh, true); }
    }

    [Fact]
    public void Pictures_already_there_are_kept_and_names_never_clash()
    {
        string old = NewFolder(), fresh = NewFolder();
        try
        {
            const string name = "2026-10-20 1942 W9ABC tower.jp2";
            Put(old, Path.Combine("Received", "Digital", name), 1);
            Put(fresh, Path.Combine("Received", "Digital", name), 9);   // from another computer, say
            string oldDigital = Path.Combine(old, "Received", "Digital"), newDigital = Path.Combine(fresh, "Received", "Digital");
            GalleryIndex.Add(oldDigital, new GalleryEntry(name, new DateTime(2026, 10, 20, 19, 42, 5), "tower.jp2", "W9ABC", QualityDb: 11));
            GalleryIndex.Add(newDigital, new GalleryEntry(name, new DateTime(2026, 10, 20, 19, 42, 5), "tower.jp2", "W9ABC", QualityDb: 22));

            var result = PictureFolders.Move(old, fresh);

            Assert.Null(result.Problem);
            Assert.Equal(1, result.Renamed);
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(newDigital, name)));   // untouched
            Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(Path.Combine(newDigital, "2026-10-20 1942 W9ABC tower (2).jp2")));
            var list = GalleryIndex.List(newDigital);
            Assert.Equal(2, list.Count);
            Assert.Contains(list, e => e.File == name && e.QualityDb == 22);
            Assert.Contains(list, e => e.File == "2026-10-20 1942 W9ABC tower (2).jp2" && e.QualityDb == 11);
        }
        finally { Directory.Delete(old, true); Directory.Delete(fresh, true); }
    }

    [Fact]
    public void If_a_copy_fails_nothing_is_lost()
    {
        string old = NewFolder(), fresh = NewFolder();
        try
        {
            string a = Put(old, Path.Combine("Sent", "2026-10-21 1900 a.jpg"), 1);
            string b = Put(old, Path.Combine("Sent", "2026-10-21 1901 b.jpg"), 2);
            // The second picture can't be read (on Windows: it's open in another program), so its copy fails.
            using (File.Open(b, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // On Linux and the Mac an open file can still be copied; force a failure there another way.
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(b, UnixFileMode.None);
                var result = PictureFolders.Move(old, fresh);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (!OperatingSystem.IsWindows() && Environment.UserName == "root") return;   // root can read anything: can't make it fail

                Assert.NotNull(result.Problem);
                Assert.StartsWith("Nothing was moved", result.Problem);
            }
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
            Assert.Equal(0, PictureFolders.CountPictures(fresh));   // the copies made were taken back
        }
        finally { Directory.Delete(old, true); Directory.Delete(fresh, true); }
    }

    [Fact]
    public void The_new_folder_must_be_a_different_one_and_not_inside_the_old()
    {
        string old = NewFolder();
        try
        {
            Assert.NotNull(PictureFolders.Problem(old, old));
            Assert.NotNull(PictureFolders.Problem(old, Path.Combine(old, "Received", "New")));
            Assert.Null(PictureFolders.Problem(old, Path.Combine(old, "Elsewhere")));
            Assert.Null(PictureFolders.Problem(old, Path.Combine(Path.GetTempPath(), "rfpix-other")));
        }
        finally { Directory.Delete(old, true); }
    }
}
