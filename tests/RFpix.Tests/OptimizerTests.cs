// OptimizerTests.cs - checks the image optimizer: making pictures smaller, JPEG 2000 files
// of a chosen size, and fitting a picture to the transmit time.
// (Opening photos and AVIF need Magick.NET; those are in PictureFileTests.cs.)

using RFpix.HamDrm;
using RFpix.Imaging;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class OptimizerTests
{
    /// <summary>A made-up photo: soft colour gradients, a few hard edges and some grain,
    /// so it compresses roughly like a real one.</summary>
    internal static DecodedPicture TestPhoto(int width = 1600, int height = 1200)
    {
        var random = new Random(1);
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                double sky = (double)y / height;
                bool tower = Math.Abs(x - width / 2) < width / 40 + (height - y) / 30 && y > height / 5;
                int grain = random.Next(-12, 13);
                byte r = (byte)Math.Clamp((tower ? 40 : 250 - 120 * sky) + grain, 0, 255);
                byte g = (byte)Math.Clamp((tower ? 40 : 140 - 60 * sky + 30 * Math.Sin(x / 37.0)) + grain, 0, 255);
                byte b = (byte)Math.Clamp((tower ? 50 : 60 + 140 * sky) + grain, 0, 255);
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }
        return new DecodedPicture(width, height, bgra);
    }

    private static int[] Pixel(DecodedPicture p, int x, int y)
    {
        int i = (y * p.Width + x) * 4;
        return new[] { (int)p.Bgra[i], p.Bgra[i + 1], p.Bgra[i + 2], p.Bgra[i + 3] };
    }

    // ---- making pictures smaller ----

    [Fact]
    public void Fitting_the_long_side_keeps_the_shape_and_never_enlarges()
    {
        Assert.Equal((640, 480), PictureScaler.FitLongSide(1600, 1200, 640));
        Assert.Equal((360, 640), PictureScaler.FitLongSide(1080, 1920, 640));   // a phone held upright
        Assert.Equal((320, 240), PictureScaler.FitLongSide(320, 240, 1280));    // already smaller
    }

    [Fact]
    public void Halving_averages_each_two_by_two_block()
    {
        // 4 x 2: two blocks, 0/100/200/40 in the red of the first, all 90 in the second.
        var bgra = new byte[4 * 2 * 4];
        byte[] reds = { 0, 100, 90, 90, 200, 40, 90, 90 };
        for (int i = 0; i < 8; i++) { bgra[i * 4 + 2] = reds[i]; bgra[i * 4 + 3] = 255; }
        var half = PictureScaler.Resize(new DecodedPicture(4, 2, bgra), 2, 1);
        Assert.Equal((2, 1), (half.Width, half.Height));
        Assert.Equal(new[] { 0, 0, 85, 255 }, Pixel(half, 0, 0));   // (0 + 100 + 200 + 40) / 4
        Assert.Equal(new[] { 0, 0, 90, 255 }, Pixel(half, 1, 0));
    }

    [Fact]
    public void Uneven_steps_count_partly_covered_pixels_by_how_much_they_cover()
    {
        // 3 pixels across become 2: each new one covers 1.5 old ones.
        var bgra = new byte[3 * 4];
        byte[] reds = { 0, 90, 180 };
        for (int i = 0; i < 3; i++) { bgra[i * 4 + 2] = reds[i]; bgra[i * 4 + 3] = 255; }
        var smaller = PictureScaler.Resize(new DecodedPicture(3, 1, bgra), 2, 1);
        Assert.Equal(30, Pixel(smaller, 0, 0)[2]);    // (1 x 0 + 0.5 x 90) / 1.5
        Assert.Equal(150, Pixel(smaller, 1, 0)[2]);   // (0.5 x 90 + 1 x 180) / 1.5
    }

    [Fact]
    public void A_smooth_picture_stays_the_same_colour_when_made_smaller()
    {
        var photo = TestPhoto(400, 300);
        var small = PictureScaler.Resize(photo, 100, 75);
        // The average colour barely changes.
        double Average(DecodedPicture p, int c) => Enumerable.Range(0, p.Width * p.Height).Average(i => p.Bgra[i * 4 + c]);
        for (int c = 0; c < 3; c++) Assert.InRange(Average(small, c) - Average(photo, c), -1.0, 1.0);
        Assert.Same(photo, PictureScaler.Resize(photo, 400, 300));
    }

    // ---- formats ----

    [Fact]
    public void Formats_are_recognised_by_their_first_bytes()
    {
        var jp2 = Jpeg2000.Encode(TestPhoto(64, 48), 50);
        Assert.Equal(PictureFormat.Jpeg2000, PictureFormats.Detect(jp2));
        Assert.Equal(PictureFormat.Jpeg, PictureFormats.Detect(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }));
        // An AVIF starts with an "ftyp" box: size 32, "ftyp", main brand, version, compatible brands.
        byte[] Ftyp(string main, string compatible) =>
            new byte[] { 0, 0, 0, 24 }.Concat("ftyp"u8.ToArray()).Concat(System.Text.Encoding.ASCII.GetBytes(main))
                .Concat(new byte[4]).Concat(System.Text.Encoding.ASCII.GetBytes(compatible + "mif1")).ToArray();
        Assert.Equal(PictureFormat.Avif, PictureFormats.Detect(Ftyp("avif", "miaf")));
        Assert.Equal(PictureFormat.Avif, PictureFormats.Detect(Ftyp("mif1", "avif")));
        Assert.Null(PictureFormats.Detect(Ftyp("heic", "heix")));   // a phone photo, not AVIF
        Assert.Null(PictureFormats.Detect(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
        Assert.Equal("avif", PictureFormat.Avif.Extension());
        Assert.True(PictureFormat.Avif.RFpixOnly());
        Assert.False(PictureFormat.Jpeg2000.RFpixOnly());
    }

    [Fact]
    public void JPEG_2000_files_come_out_the_size_asked_for()
    {
        var picture = PictureScaler.Resize(TestPhoto(), 640, 480);
        foreach (int size in new[] { 8_000, 15_000, 30_000 })
        {
            var file = Jpeg2000.EncodeToSize(picture, size);
            Assert.InRange(file.Length, size * 97 / 100, size);
            var back = Jpeg2000.Decode(file);
            Assert.Equal((640, 480), (back.Width, back.Height));
        }
    }

    [Fact]
    public void A_JPEG_2000_file_looks_like_the_picture()
    {
        var picture = PictureScaler.Resize(TestPhoto(), 320, 240);
        var back = Jpeg2000.Decode(Jpeg2000.EncodeToSize(picture, 15_000));
        double error = 0;
        for (int i = 0; i < picture.Bgra.Length; i++) error += Math.Abs(picture.Bgra[i] - back.Bgra[i]);
        Assert.True(error / picture.Bgra.Length < 8, $"average difference {error / picture.Bgra.Length:0.0}");
    }

    [Fact]
    public void JPEG_2000_quality_grows_the_file_steadily()
    {
        Assert.Equal(0.05, Jpeg2000.BitsPerPixel(1), 3);
        Assert.Equal(3.0, Jpeg2000.BitsPerPixel(100), 3);
        var picture = PictureScaler.Resize(TestPhoto(), 320, 240);
        Assert.True(Jpeg2000.Encode(picture, 40).Length < Jpeg2000.Encode(picture, 60).Length);
    }

    [Fact]
    public void Reed_Solomon_keeps_the_avif_extension()
    {
        var file = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        var (back, extension) = RsFile.Unwrap(RsFile.Wrap(file, "avif", 2), 2)!.Value;
        Assert.Equal(file, back);
        Assert.Equal("avif", extension);
        Assert.Equal("jp2", RsFile.Unwrap(RsFile.Wrap(file, "jp2", 1), 1)!.Value.Extension);
    }

    [Fact]
    public void An_unwrapped_AVIF_loses_the_padding_zeros()
    {
        // A pretend AVIF: two boxes (ftyp of 24 bytes, then one of 166), 190 bytes in all.
        // RS2 blocks carry 192 bytes, so 190 + the 7-byte header lands in QSSTV's quirk:
        // the unwrapped file would have zeros on the end.
        var file = new byte[190];
        file[3] = 24; "ftyp"u8.CopyTo(file.AsSpan(4)); "avif"u8.CopyTo(file.AsSpan(8));
        file[27] = 166; "mdat"u8.CopyTo(file.AsSpan(28));
        for (int i = 32; i < 190; i++) file[i] = (byte)(i | 1);
        Assert.Equal(file, RsFile.Unwrap(RsFile.Wrap(file, "avif", 2), 2)!.Value.File);
        // Anything else keeps what QSSTV gives it.
        Assert.True(RsFile.Unwrap(RsFile.Wrap(file, "jpg", 2), 2)!.Value.File.Length > 190);
    }

    // ---- fitting to the transmit time ----

    [Fact]
    public void Names_are_kept_simple()
    {
        Assert.Equal("tower-sunset", PictureFitter.BaseName(Path.Combine("Pictures", "tower sunset.jpg")));
        Assert.Equal("IMG_2041", PictureFitter.BaseName("IMG_2041.HEIC"));
        Assert.Equal("Field-Day-2026", PictureFitter.BaseName("Field Day (2026)!.png"));
        Assert.Equal("picture", PictureFitter.BaseName("ÄÖÜ.jpg"));
        Assert.Equal(40, PictureFitter.BaseName(new string('a', 90) + ".jpg").Length);
    }

    [Fact]
    public void The_budget_is_the_most_that_goes_out_in_the_target_time()
    {
        var profile = SendProfile.NetRepeater;
        int budget = PictureFitter.MaxFileBytes("tower.jp2", profile);
        Assert.InRange(budget, 25_000, 35_000);   // about 30 kB in 2 minutes at the net's settings
        Assert.True(PictureFitter.DurationFor("tower.jp2", budget, profile.Settings) <= TimeSpan.FromMinutes(2));
        // One more frame's worth would run over.
        Assert.True(PictureFitter.DurationFor("tower.jp2", budget + 200, profile.Settings) > TimeSpan.FromMinutes(2));

        // Reed-Solomon RS2 carries 192 of every 255 bytes, so about three quarters as much picture.
        var rs2 = profile with { ReedSolomon = 2 };
        int rsBudget = PictureFitter.MaxFileBytes("tower.rs2", rs2);
        Assert.InRange(rsBudget, budget * 70 / 100, budget * 77 / 100);
        Assert.True(PictureFitter.DurationFor("tower.rs2", PictureFitter.SendLength(rsBudget, 2), rs2.Settings) <= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Auto_may_use_three_seconds_more_than_the_target()
    {
        var profile = SendProfile.NetRepeater;
        int budget = PictureFitter.MaxFileBytes("tower.jp2", profile);
        int withAllowance = PictureFitter.MaxFileBytes("tower.jp2", profile, PictureFitter.AutoAllowance);
        Assert.True(withAllowance > budget);
        Assert.True(PictureFitter.DurationFor("tower.jp2", withAllowance, profile.Settings) <= TimeSpan.FromSeconds(123));
    }

    [Fact]
    public void The_time_shows_green_yellow_or_red()
    {
        var picture = new DecodedPicture(1, 1, new byte[4]);
        FittedPicture Taking(double seconds, bool automatic) =>
            new(picture, PictureFormat.Jpeg2000, 50, Array.Empty<byte>(), "a.jp2", Array.Empty<byte>(), "a.jp2",
                SendProfile.NetRepeater, TimeSpan.FromSeconds(seconds)) { Automatic = automatic };
        Assert.Equal(TimeFit.Fits, Taking(120, automatic: false).Fit);
        Assert.Equal(TimeFit.SlightlyOver, Taking(122, automatic: false).Fit);   // a slider moved: over is over
        Assert.Equal(TimeFit.Fits, Taking(122.8, automatic: true).Fit);          // Auto's 3 extra seconds
        Assert.Equal(TimeFit.SlightlyOver, Taking(123.2, automatic: true).Fit);
        Assert.Equal(TimeFit.SlightlyOver, Taking(127, automatic: false).Fit);
        Assert.Equal(TimeFit.Over, Taking(127.2, automatic: false).Fit);
    }

    [Fact]
    public void Auto_fits_a_photo_into_two_minutes_and_uses_most_of_them()
    {
        var fitter = new PictureFitter((p, _, q) => Jpeg2000.Encode(p, q));
        var fitted = fitter.Auto(TestPhoto(), "tower", PictureFormat.Jpeg2000, SendProfile.NetRepeater);
        Assert.True(fitted.FitsTarget, $"{fitted.Duration}");
        Assert.True(fitted.Automatic);
        Assert.True(fitted.Duration > TimeSpan.FromSeconds(100), $"{fitted.Duration}");
        Assert.True(fitted.Duration <= TimeSpan.FromSeconds(123), $"{fitted.Duration}");
        Assert.Equal("tower.jp2", fitted.FileName);
        Assert.Equal(fitted.File, fitted.SendData);
        Assert.InRange(Math.Max(fitted.Picture.Width, fitted.Picture.Height), 400, 900);
        Assert.Equal(4.0 / 3, (double)fitted.Picture.Width / fitted.Picture.Height, 1);
        Assert.Equal(fitted.Picture.Width, Jpeg2000.Decode(fitted.File).Width);
    }

    [Fact]
    public void Auto_with_Reed_Solomon_wraps_the_file_and_still_fits()
    {
        var fitter = new PictureFitter((p, _, q) => Jpeg2000.Encode(p, q));
        var profile = SendProfile.NetRepeater with { ReedSolomon = 2 };
        var fitted = fitter.Auto(TestPhoto(), "tower", PictureFormat.Jpeg2000, profile);
        Assert.True(fitted.FitsTarget);
        Assert.Equal("tower.rs2", fitted.SendName);
        Assert.Equal("tower.jp2", fitted.FileName);
        // The file comes back (with up to a block of zeros on the end, as QSSTV's wrapping leaves them).
        var back = RsFile.Unwrap(fitted.SendData, 2)!.Value.File;
        Assert.Equal(fitted.File, back[..fitted.File.Length]);
    }

    // A stand-in encoder: the file is 1 byte per pixel per 50 quality points, so the
    // sizes are known exactly.
    private static byte[] StandIn(DecodedPicture p, PictureFormat f, int q) => new byte[p.Width * p.Height * q / 50];

    [Fact]
    public void Auto_makes_the_picture_smaller_when_the_quality_would_drop_too_far()
    {
        var fitter = new PictureFitter(StandIn);
        var profile = SendProfile.NetRepeater;
        int budget = PictureFitter.MaxFileBytes("tower.jpg", profile, PictureFitter.AutoAllowance);
        var fitted = fitter.Auto(TestPhoto(), "tower", PictureFormat.Jpeg, profile);
        Assert.True(fitted.FitsTarget);
        Assert.True(fitted.Quality >= PictureFitter.QualityFloor(PictureFormat.Jpeg), $"quality {fitted.Quality}");
        Assert.True(fitted.File.Length <= budget);
    }

    [Fact]
    public void Auto_never_enlarges_a_small_picture_and_may_finish_early()
    {
        var fitter = new PictureFitter((p, _, q) => Jpeg2000.Encode(p, q));
        var fitted = fitter.Auto(TestPhoto(200, 150), "tiny", PictureFormat.Jpeg2000, SendProfile.NetRepeater);
        Assert.Equal((200, 150), (fitted.Picture.Width, fitted.Picture.Height));
        Assert.Equal(PictureFitter.MaxAutoQuality, fitted.Quality);
        Assert.True(fitted.Duration < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Manual_uses_the_size_and_quality_given_and_reports_overtime()
    {
        var fitter = new PictureFitter((p, _, q) => Jpeg2000.Encode(p, q));
        var fitted = fitter.Manual(TestPhoto(), "tower", PictureFormat.Jpeg2000, SendProfile.NetRepeater, 1024, 90);
        Assert.Equal((1024, 768), (fitted.Picture.Width, fitted.Picture.Height));
        Assert.Equal(90, fitted.Quality);
        Assert.False(fitted.FitsTarget);   // 1024 x 768 at quality 90 is far more than 2 minutes
        Assert.Equal(TimeFit.Over, fitted.Fit);
        Assert.False(fitted.Automatic);
        Assert.Equal(fitted.Duration, fitted.Transmitter("KD9DD").Duration);
    }

    [Fact]
    public void Auto_can_be_cancelled()
    {
        var fitter = new PictureFitter((p, _, q) => Jpeg2000.Encode(p, q));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            fitter.Auto(TestPhoto(), "tower", PictureFormat.Jpeg2000, SendProfile.NetRepeater, cancel.Token));
    }

    [Fact]
    public void A_fitted_picture_goes_over_the_air_and_arrives_exactly()
    {
        // A short one (30 s at 64-QAM) to keep the test quick.
        var profile = SendProfile.NetRepeater with { Qam = 64, TargetSeconds = 30 };
        var fitter = new PictureFitter((p, _, q) => Jpeg2000.Encode(p, q));
        var fitted = fitter.Auto(TestPhoto(), "tower", PictureFormat.Jpeg2000, profile);
        var (received, callsigns, _) = TransmitterTests.Receive(fitted.Transmitter("KD9DD"));
        Assert.Equal("tower.jp2", received?.Name);
        Assert.Equal(fitted.File, received!.Data);
        Assert.Contains("KD9DD", callsigns);
    }

    [Fact]
    public void Profiles_compare_by_their_settings_not_their_names()
    {
        var mine = SendProfile.NetRepeater with { Name = "Mine" };
        Assert.True(mine.SameAs(SendProfile.NetRepeater));
        Assert.False((mine with { Qam = 4 }).SameAs(SendProfile.NetRepeater));
        Assert.Equal(new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
                     SendProfile.NetRepeater.Settings);
    }
}
