// PictureFileTests.cs - checks opening pictures and making and reading JPEG and AVIF,
// which Magick.NET does (see Pictures.cs).

using RFpix.Imaging;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class PictureFileTests
{
    private static DecodedPicture Photo(int w, int h) => PictureScaler.Resize(OptimizerTests.TestPhoto(), w, h);

    private static double Difference(DecodedPicture a, DecodedPicture b)
    {
        double sum = 0;
        for (int i = 0; i < a.Bgra.Length; i++) sum += Math.Abs(a.Bgra[i] - b.Bgra[i]);
        return sum / a.Bgra.Length;
    }

    [Fact]
    public void AVIF_comes_back_looking_like_the_picture()
    {
        var picture = Photo(640, 480);
        var file = Pictures.EncodeAvif(picture, 50);
        Assert.Equal(PictureFormat.Avif, PictureFormats.Detect(file));
        var back = Pictures.Decode(file);
        Assert.Equal((640, 480), (back.Width, back.Height));
        Assert.True(Difference(picture, back) < 10, $"average difference {Difference(picture, back):0.0}");
    }

    [Fact]
    public void JPEG_comes_back_looking_like_the_picture()
    {
        var picture = Photo(320, 240);
        var file = Pictures.EncodeJpeg(picture, 80);
        Assert.Equal(PictureFormat.Jpeg, PictureFormats.Detect(file));
        var back = Pictures.Decode(file);
        Assert.Equal((320, 240), (back.Width, back.Height));
        Assert.True(Difference(picture, back) < 8, $"average difference {Difference(picture, back):0.0}");
    }

    [Fact]
    public void Higher_quality_makes_bigger_files()
    {
        var picture = Photo(320, 240);
        Assert.True(Pictures.EncodeAvif(picture, 30).Length < Pictures.EncodeAvif(picture, 70).Length);
        Assert.True(Pictures.EncodeJpeg(picture, 30).Length < Pictures.EncodeJpeg(picture, 70).Length);
    }

    [Fact]
    public void A_picture_file_opens_for_sending()
    {
        // A test picture (a JPEG) saved to a file, then opened as the operator would.
        string path = Path.Combine(Path.GetTempPath(), $"rfpix-open-test-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, TestTransmissions.All[0].Picture());
            var picture = Pictures.Load(path);
            Assert.True(picture.Width > 100 && picture.Height > 100);
            Assert.All(Enumerable.Range(0, picture.Width * picture.Height), i => Assert.Equal(255, picture.Bgra[i * 4 + 3]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_isnt_a_picture_gives_a_plain_message()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rfpix-open-test-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllText(path, "not a picture");
            Assert.Throws<PictureException>(() => Pictures.Load(path));
            Assert.Throws<PictureException>(() => Pictures.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Auto_fits_an_AVIF_into_two_minutes_with_a_bigger_picture_than_JPEG_2000()
    {
        var fitter = new PictureFitter(Pictures.Encode);
        var photo = OptimizerTests.TestPhoto();
        var avif = fitter.Auto(photo, "tower", PictureFormat.Avif, SendProfile.NetRepeater);
        var jp2 = fitter.Auto(photo, "tower", PictureFormat.Jpeg2000, SendProfile.NetRepeater);
        Assert.True(avif.FitsTarget, $"{avif.Duration}");
        Assert.Equal("tower.avif", avif.FileName);
        Assert.True(avif.Picture.Width > jp2.Picture.Width, $"AVIF {avif.Picture.Width}, JPEG 2000 {jp2.Picture.Width}");
        Assert.Equal(avif.Picture.Width, Pictures.Decode(avif.File).Width);
    }
}
