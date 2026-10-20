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
}
