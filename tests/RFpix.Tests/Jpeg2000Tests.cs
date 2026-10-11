// Jpeg2000Tests.cs - checks that RFpix shows JPEG 2000 pictures correctly and safely.
//
// tests/reference/jpeg2000 holds three small lossless pictures made with OpenJPEG (the
// library QSSTV uses, through Python's Pillow), so their pixels are known exactly: a grey gradient, the same in
// 16 bits, and a colour pattern as a bare codestream (.j2k, no JP2 wrapper).

using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Imaging;
using Xunit;

namespace RFpix.Tests;

public class Jpeg2000Tests
{
    private static string Reference(string name) => Path.Combine(Recordings.Folder, "..", "reference", "jpeg2000", name);
    private static string Image(string name) => Path.Combine(Recordings.Folder, "images", name);

    [Fact]
    public void Recognises_JPEG_2000_by_its_first_bytes()
    {
        Assert.True(Jpeg2000.IsJpeg2000(File.ReadAllBytes(Image("img14.jp2"))));
        Assert.True(Jpeg2000.IsJpeg2000(File.ReadAllBytes(Reference("rgb-64x48-lossless.j2k"))));
        Assert.False(Jpeg2000.IsJpeg2000(File.ReadAllBytes(Image("img04.jpg"))));
        Assert.False(Jpeg2000.IsJpeg2000(Array.Empty<byte>()));
    }

    [Fact]
    public void Reads_the_size_from_the_header()
    {
        Assert.Equal((320, 240, 4), Jpeg2000.ReadSize(File.ReadAllBytes(Image("img14.jp2"))));
        Assert.Equal((64, 48, 1), Jpeg2000.ReadSize(File.ReadAllBytes(Reference("grey-64x48-lossless.jp2"))));
        Assert.Equal((64, 48, 3), Jpeg2000.ReadSize(File.ReadAllBytes(Reference("rgb-64x48-lossless.j2k"))));
        Assert.Null(Jpeg2000.ReadSize(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0 }));   // header cut short
    }

    [Fact]
    public void Decodes_grey_exactly()
    {
        var picture = Jpeg2000.Decode(File.ReadAllBytes(Reference("grey-64x48-lossless.jp2")));
        Assert.Equal((64, 48), (picture.Width, picture.Height));
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
                Assert.Equal(new[] { (x * 4 + y) % 256, (x * 4 + y) % 256, (x * 4 + y) % 256, 255 }, Pixel(picture, x, y));
    }

    [Fact]
    public void Decodes_16_bit_grey_scaled_to_8_bits()
    {
        var picture = Jpeg2000.Decode(File.ReadAllBytes(Reference("grey16-64x48-lossless.jp2")));
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                long v = (x * 1000 + y * 37) % 65536;
                int expected = (int)((v * 255 + 65535 / 2) / 65535);
                Assert.Equal(expected, Pixel(picture, x, y)[0]);
            }
    }

    [Fact]
    public void Decodes_a_bare_colour_codestream_exactly()
    {
        var picture = Jpeg2000.Decode(File.ReadAllBytes(Reference("rgb-64x48-lossless.j2k")));
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int r = x * 4 % 256, g = y * 5 % 256, b = (x + y) * 3 % 256;
                Assert.Equal(new[] { r, g, b, 255 }, Pixel(picture, x, y));
            }
    }

    [Fact]
    public void Recording_14s_picture_matches_OpenJPEG()
    {
        // The picture as received from recording 14 (not just the file on disk).
        var wav = WavFile.Read(Recordings.Path("14_modeB_2p5khz_qam16_long_high_rs0_jp2.wav"));
        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        receiver.FileReceived += f => received ??= f;
        for (int pos = 0; pos + 480 <= wav.Samples.Length; pos += 480)
            receiver.AddSamples(wav.Samples[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();

        var picture = Jpeg2000.Decode(received!.Data);
        Assert.Equal((320, 240), (picture.Width, picture.Height));

        // Pixels as OpenJPEG decodes them (red, green, blue). This is a lossy picture, and the
        // two libraries may round slightly differently, so allow 2 either way.
        foreach (var (x, y, r, g, b) in OpenJpegPixels)
        {
            var p = Pixel(picture, x, y);
            Assert.InRange(p[0], r - 2, r + 2);
            Assert.InRange(p[1], g - 2, g + 2);
            Assert.InRange(p[2], b - 2, b + 2);
        }
        // Its 4th component is transparency (fully solid here); RFpix always shows pictures solid.
        Assert.All(Enumerable.Range(0, 320 * 240), i => Assert.Equal(255, picture.Bgra[i * 4 + 3]));
    }

    [Fact]
    public void Refuses_a_huge_picture_before_decoding_it()
    {
        // A few hundred bytes can claim to be enormous; RFpix must not try to make room for it.
        var data = File.ReadAllBytes(Reference("rgb-64x48-lossless.j2k"));
        int siz = 2;                                         // the SIZ marker follows SOC (FF 4F)
        WriteUInt32(data, siz + 6, 60000);                    // width
        WriteUInt32(data, siz + 10, 50000);                   // height

        var error = Assert.Throws<PictureException>(() => Jpeg2000.Decode(data));
        Assert.Contains("60000 × 50000", error.Message);

        WriteUInt32(data, siz + 6, 4000);                     // within 4096 on a side, but 16 million pixels
        WriteUInt32(data, siz + 10, 4000);
        Assert.Throws<PictureException>(() => Jpeg2000.Decode(data));
    }

    [Fact]
    public void Damaged_pictures_give_a_plain_message_never_a_crash()
    {
        var original = File.ReadAllBytes(Image("img14.jp2"));
        var random = new Random(5);
        for (int trial = 0; trial < 40; trial++)
        {
            var damaged = (byte[])original.Clone();
            if (trial % 2 == 0)
                damaged = damaged[..random.Next(4, damaged.Length)];                 // cut short
            else
                for (int i = 0; i < 20; i++) damaged[random.Next(damaged.Length)] ^= (byte)random.Next(1, 256);

            try
            {
                var picture = Jpeg2000.Decode(damaged);
                Assert.Equal(picture.Width * picture.Height * 4, picture.Bgra.Length);   // shown, perhaps with marks
            }
            catch (PictureException)
            {
                // Fine: the operator is told the picture is damaged.
            }
        }
    }

    // ---- helpers ----

    private static int[] Pixel(DecodedPicture p, int x, int y)
    {
        int i = (y * p.Width + x) * 4;
        return new int[] { p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i], p.Bgra[i + 3] };   // red, green, blue, alpha
    }

    private static void WriteUInt32(byte[] d, int at, uint v)
    {
        d[at] = (byte)(v >> 24); d[at + 1] = (byte)(v >> 16); d[at + 2] = (byte)(v >> 8); d[at + 3] = (byte)v;
    }

    // From OpenJPEG's own decoder (opj_decompress 2.5) on tests/recordings/images/img14.jp2.
    private static readonly (int X, int Y, int R, int G, int B)[] OpenJpegPixels =
    {
        (0, 0, 65, 16, 65),
        (319, 239, 65, 16, 63),
        (160, 30, 194, 173, 195),
        (30, 100, 255, 1, 0),
        (55, 120, 255, 0, 0),
        (140, 100, 0, 255, 0),
        (240, 100, 0, 0, 255),
        (285, 125, 255, 255, 0),
        (100, 150, 65, 17, 63),
        (160, 215, 222, 188, 223),
        (60, 60, 63, 15, 64),
        (200, 180, 0, 0, 255),
        (130, 75, 0, 254, 2),
        (25, 170, 255, 2, 0),
        (290, 112, 253, 255, 0),
        (300, 20, 65, 16, 65)
    };
}
