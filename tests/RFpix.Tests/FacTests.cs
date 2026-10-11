// FacTests.cs - checks that RFpix reads the FAC: the transmission's settings and
// the sender's callsign, carried in 45 cells of every frame.
//
// Limits come from measurements (Oct 11, 2026): every frame of every clean recording
// reads correctly; 99-100% of frames at 6 dB SNR; about half at 3 dB.

using System.Numerics;
using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class FacTests
{
    // ---- the recordings ----

    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Reads_every_frame_of_every_recording_with_the_right_settings(string file)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var (receiver, facs) = Play(wav.Samples);

        // Frames start 1 s in and come every 0.4 s; the first and last can't be read whole.
        int frames = (int)((wav.Duration.TotalSeconds - 2.0) / 0.4);
        Assert.Equal(0, receiver.FacFailed);
        Assert.InRange(receiver.FacGood, frames - 3, frames + 1);
        Assert.All(facs, fac => AssertSettingsMatchFileName(fac, file));
        Assert.Equal("RFPIX", receiver.Callsign);
    }

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 25, 0)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", -150, -250)]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 60, 100)]
    [InlineData("05_modeB_2p2khz_qam64_short_low_rs0.wav", -30, 150)]
    public void Tuning_and_clock_errors_make_no_difference(string file, double offsetHz, double ppm)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var audio = ShiftFrequency(wav.Samples, offsetHz, 48000);
        if (ppm != 0) audio = ChangeClock(audio, ppm);
        var (receiver, facs) = Play(audio);

        Assert.True(receiver.FacFailed <= 3, $"{receiver.FacFailed} FACs failed");
        Assert.True(receiver.FacGood >= (wav.Duration.TotalSeconds - 2.0) / 0.4 - 4);
        Assert.All(facs, fac => AssertSettingsMatchFileName(fac, file));
        Assert.Equal("RFPIX", receiver.Callsign);
    }

    [Theory]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav")]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav")]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav")]
    public void Reads_nearly_every_frame_at_6_dB_SNR(string file)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var (receiver, facs) = Play(AddNoise(wav.Samples, 6, seed: 11));

        int total = receiver.FacGood + receiver.FacFailed;
        Assert.True(receiver.FacGood >= 0.95 * total, $"only {receiver.FacGood} of {total} read");
        Assert.All(facs, fac => AssertSettingsMatchFileName(fac, file, checkLabel: false));
        Assert.All(receiver.CallsignsShown, c => Assert.Equal("RFPIX", c));
    }

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav")]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav")]
    public void Never_shows_wrong_settings_or_callsign_even_when_half_the_frames_fail(string file)
    {
        // At 3 dB SNR about half the frames fail their checksum, and by chance about 1 in
        // 256 damaged ones passes it anyway. Checking that FACs agree with each other must
        // stop wrong settings, and the two-votes rule must stop wrong callsign letters.
        var wav = WavFile.Read(Recordings.Path(file));
        var (receiver, facs) = Play(AddNoise(wav.Samples, 3, seed: 12));

        Assert.True(receiver.FacGood >= 20, $"only {receiver.FacGood} read");
        Assert.All(facs, fac => AssertSettingsMatchFileName(fac, file, checkLabel: false));
        Assert.All(receiver.CallsignsShown, c => Assert.Equal("RFPIX", c));
        Assert.Contains("RFPIX", receiver.CallsignsShown);
    }

    [Fact]
    public void Noise_alone_gives_no_FAC()
    {
        var (receiver, facs) = Play(Noise(48000 * 8, 0.1, seed: 13));
        Assert.Empty(facs);
        Assert.Equal(0, receiver.FacGood);
        Assert.Equal("", receiver.TransmissionText);
    }

    [Fact]
    public void Transmission_line_shows_settings_and_callsign()
    {
        var wav = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        var (receiver, _) = Play(wav.Samples[..(48000 * 10)]);

        Assert.StartsWith("2.5 kHz  ·  16-QAM  ·  long interleave  ·  high protection  ·  from RFPIX  ·  FAC ",
                          receiver.TransmissionText);
    }

    // ---- the pieces on their own ----

    [Fact]
    public void Viterbi_repairs_damaged_bits()
    {
        var random = new Random(1);
        var steps = ConvolutionalCode.RepeatPattern(ConvolutionalCode.Patterns[6], 48);
        for (int trial = 0; trial < 20; trial++)
        {
            var bits = Enumerable.Range(0, 48).Select(_ => (byte)random.Next(2)).ToArray();
            var coded = ConvolutionalCode.Encode(bits, steps);
            Assert.Equal(90, coded.Length);

            // Received perfectly: +1 for a 0, -1 for a 1.
            var soft = coded.Select(b => b == 0 ? 1.0 : -1.0).ToArray();
            Assert.Equal(bits, ConvolutionalCode.Decode(soft, steps, 48));

            // Three bits arrive wrong, spread apart: still repaired.
            foreach (int i in new[] { 5, 40, 75 }) soft[i] = -soft[i];
            Assert.Equal(bits, ConvolutionalCode.Decode(soft, steps, 48));
        }
    }

    [Theory]
    [InlineData(90, 21)]
    [InlineData(1000, 13)]
    [InlineData(2266, 21)]
    public void Interleaver_moves_every_bit_exactly_once(int length, int t0)
    {
        var table = BitInterleaver.Table(length, t0);
        Assert.Equal(Enumerable.Range(0, length), table.OrderBy(i => i));
    }

    [Fact]
    public void Energy_dispersal_undoes_itself()
    {
        var bits = Enumerable.Range(0, 100).Select(i => (byte)(i % 3 == 0 ? 1 : 0)).ToArray();
        var copy = (byte[])bits.Clone();
        EnergyDispersal.Apply(copy);
        Assert.NotEqual(bits, copy);
        EnergyDispersal.Apply(copy);
        Assert.Equal(bits, copy);
    }

    [Fact]
    public void Decodes_a_FAC_built_from_scratch()
    {
        // Build a FAC the way a sender does, then read it back.
        // Settings: third frame, 2.2 kHz, short interleave, 4-QAM, low protection, data, "K9X".
        var bits = new List<byte>();
        void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add((byte)((value >> i) & 1)); }
        Put(0b10, 2); Put(0, 1); Put(1, 1); Put(1, 1); Put(1, 1); Put(1, 1); Put(0, 2); Put(1, 1);
        foreach (char c in "K9X") Put(c, 7);
        Put(0, 9);
        Put(Crc.Crc8(bits, 0, 40), 8);

        var scrambled = bits.ToArray();
        EnergyDispersal.Apply(scrambled);
        var steps = ConvolutionalCode.RepeatPattern(ConvolutionalCode.Patterns[6], 48);
        var coded = ConvolutionalCode.Encode(scrambled, steps);
        var table = BitInterleaver.Table(90, 21);
        var sent = Enumerable.Range(0, 90).Select(i => coded[table[i]]).ToArray();
        var cells = Enumerable.Range(0, 45)
            .Select(i => new Complex(sent[2 * i] == 0 ? 0.7071 : -0.7071, sent[2 * i + 1] == 0 ? 0.7071 : -0.7071))
            .ToArray();

        var fac = FacDecoder.Decode(cells, Enumerable.Repeat(Complex.One, 45).ToArray());

        Assert.NotNull(fac);
        Assert.Equal(new FacInfo(2, HamDrmBandwidth.Narrow, HamDrmInterleave.Short, 4,
                                 HamDrmProtection.Low, true, 0, "K9X"), fac);

        // Damage it beyond repair (every other cell flipped): the checksum must refuse it.
        for (int i = 0; i < 45; i += 2) cells[i] = -cells[i];
        Assert.Null(FacDecoder.Decode(cells, Enumerable.Repeat(Complex.One, 45).ToArray()));
    }

    // ---- helpers ----

    /// <summary>What a receiver read while a transmission was being received. (When the
    /// signal ends the receiver clears all this, so it's noted down as it goes.)</summary>
    private sealed record Heard(int FacGood, int FacFailed, string? Callsign, string TransmissionText)
    {
        /// <summary>Every callsign the receiver showed along the way.</summary>
        public HashSet<string> CallsignsShown { get; } = new();
    }

    /// <summary>Plays audio through a receiver and collects every FAC that passed its checksum.</summary>
    private static (Heard Receiver, List<FacInfo> Facs) Play(float[] audio)
    {
        var receiver = new HamDrmReceiver();
        var facs = new List<FacInfo>();
        var heard = new Heard(0, 0, null, "");
        var shown = new HashSet<string>();
        int seen = 0;
        receiver.Decided += _ =>
        {
            // Each new good FAC raises FacGood by one (several can arrive in one decision).
            if (receiver.FacGood > seen && receiver.Fac is { } fac) facs.Add(fac);
            seen = receiver.FacGood;
            if (receiver.Callsign is { } callsign) shown.Add(callsign);
            if (receiver.FacGood + receiver.FacFailed > 0)
                heard = new Heard(receiver.FacGood, receiver.FacFailed, receiver.Callsign, receiver.TransmissionText);
        };
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        heard.CallsignsShown.UnionWith(shown);
        return (heard, facs);
    }

    /// <summary>The settings in a recording's file name, e.g. 05_modeB_2p2khz_qam64_short_low_rs0.</summary>
    private static void AssertSettingsMatchFileName(FacInfo fac, string file, bool checkLabel = true)
    {
        Assert.Equal(file.Contains("2p5khz") ? HamDrmBandwidth.Wide : HamDrmBandwidth.Narrow, fac.Bandwidth);
        Assert.Equal(file.Contains("_qam64_") ? 64 : file.Contains("_qam16_") ? 16 : 4, fac.Qam);
        Assert.Equal(file.Contains("_short_") ? HamDrmInterleave.Short : HamDrmInterleave.Long, fac.Interleave);
        Assert.Equal(file.Contains("_low_") ? HamDrmProtection.Low : HamDrmProtection.High, fac.Protection);
        Assert.True(fac.IsData);
        Assert.InRange(fac.FrameInSuperframe, 0, 2);
        // "RFPIX" over three frames: "RFP", "IX", "".
        if (checkLabel) Assert.Equal(fac.FrameInSuperframe switch { 0 => "RFP", 1 => "IX", _ => "" }, fac.LabelPart);
    }
}
