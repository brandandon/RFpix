// GalleryTests.cs - checks the Gallery's list of pictures and what it knows about each.

using RFpix.Gallery;
using Xunit;

namespace RFpix.Tests;

public class GalleryTests
{
    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rfpix-gallery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void RFpix_names_tell_the_date_time_and_callsign()
    {
        var e = GalleryIndex.FromName("2026-10-20 1942 W9ABC tower.jp2", DateTime.MinValue);
        Assert.Equal(new DateTime(2026, 10, 20, 19, 42, 0), e.When);
        Assert.Equal("W9ABC", e.Callsign);
        Assert.Equal("tower.jp2", e.Name);

        var numbered = GalleryIndex.FromName("2026-10-20 1942 KD9DD img04 (2).jpg", DateTime.MinValue);
        Assert.Equal("KD9DD", numbered.Callsign);
        Assert.Equal("img04.jpg", numbered.Name);

        var sent = GalleryIndex.FromName("2026-10-20 1942 IMG 0001.jpg", DateTime.MinValue);   // no callsign
        Assert.Null(sent.Callsign);
        Assert.Equal("IMG 0001.jpg", sent.Name);

        var other = GalleryIndex.FromName("holiday.png", new DateTime(2026, 1, 2));
        Assert.Equal(new DateTime(2026, 1, 2), other.When);
        Assert.Equal("holiday.png", other.Name);
    }

    [Fact]
    public void Pictures_are_listed_newest_first_with_their_details()
    {
        string folder = NewFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "2026-10-19 2000 KD9DD old.jp2"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(folder, "2026-10-20 1942 W9ABC tower.jp2"), new byte[] { 2 });
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a picture");
            GalleryIndex.Add(folder, new GalleryEntry("2026-10-20 1942 W9ABC tower.jp2", new DateTime(2026, 10, 20, 19, 42, 5),
                "tower.jp2", "W9ABC", "Mode B", 0, 17.2, CompletedByBsr: true));

            var list = GalleryIndex.List(folder);
            Assert.Equal(2, list.Count);
            Assert.Equal("tower.jp2", list[0].Name);
            Assert.Equal(17.2, list[0].QualityDb);
            Assert.True(list[0].CompletedByBsr);
            Assert.Equal("old.jp2", list[1].Name);   // no entry: read from its name

            // Deleted outside RFpix: gone from the list, and from the index when it's next written.
            File.Delete(Path.Combine(folder, "2026-10-20 1942 W9ABC tower.jp2"));
            Assert.Single(GalleryIndex.List(folder));
            GalleryIndex.Remove(folder, "nothing");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_damaged_index_still_lists_the_pictures()
    {
        string folder = NewFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "2026-10-20 1942 W9ABC tower.jp2"), new byte[] { 2 });
            File.WriteAllText(Path.Combine(folder, GalleryIndex.IndexFileName), "{ not json");
            Assert.Equal("W9ABC", Assert.Single(GalleryIndex.List(folder)).Callsign);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Sent_pictures_are_saved_with_the_date_and_time_and_never_overwritten()
    {
        string folder = NewFolder();
        try
        {
            var when = new DateTime(2026, 10, 20, 19, 42, 0);
            string a = GalleryIndex.SaveSent(folder, "tower.jp2", new byte[] { 1 }, when);
            string b = GalleryIndex.SaveSent(folder, "tower.jp2", new byte[] { 2 }, when);
            Assert.Equal("2026-10-20 1942 tower.jp2", Path.GetFileName(a));
            Assert.Equal("2026-10-20 1942 tower (2).jp2", Path.GetFileName(b));
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(a));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Pictures_from_several_folders_are_listed_together_newest_first()
    {
        string folder = NewFolder();
        try
        {
            string digital = Path.Combine(folder, "Digital"), analog = Path.Combine(folder, "Analog");
            Directory.CreateDirectory(digital);
            Directory.CreateDirectory(analog);
            File.WriteAllBytes(Path.Combine(folder, "2026-10-19 2000 KD9DD old.jp2"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(digital, "2026-10-21 0900 W9ABC new.jp2"), new byte[] { 2 });
            File.WriteAllBytes(Path.Combine(analog, "2026-10-20 1200 SSTV Martin 1.png"), new byte[] { 3 });
            var list = GalleryIndex.List(new[] { folder, digital, analog });
            Assert.Equal(new[] { "new.jp2", "SSTV Martin 1.png", "old.jp2" }, list.Select(e => e.Name));
            Assert.Equal(Path.Combine(analog, "2026-10-20 1200 SSTV Martin 1.png"), list[1].FullPath);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Analog_and_digital_pictures_are_told_apart()
    {
        string root = Path.Combine("x", "Received");
        Assert.True(new GalleryEntry("a.png", DateTime.Now) { Folder = Path.Combine(root, "Analog") }.IsAnalog);
        Assert.False(new GalleryEntry("a.jpg", DateTime.Now) { Folder = Path.Combine(root, "Digital") }.IsAnalog);
        // From before they were kept apart: by what was noted, or by the name RFpix gives analog pictures.
        Assert.True(new GalleryEntry("b.png", DateTime.Now, "SSTV Martin 1.png", null, "Analog SSTV  ·  Martin 1") { Folder = root }.IsAnalog);
        Assert.False(new GalleryEntry("c.jp2", DateTime.Now, "tower.jp2", "N0ABC", "Mode B, 2.5 kHz") { Folder = root }.IsAnalog);
        Assert.True(GalleryIndex.FromName("2026-10-20 1942 SSTV Martin 1.png", DateTime.MinValue).IsAnalog);
        Assert.False(GalleryIndex.FromName("2026-10-20 1942 N0ABC tower.jp2", DateTime.MinValue).IsAnalog);
    }
}
