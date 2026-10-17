// SendTests.cs - checks the Send page's parts without a screen or sound card:
//   - the eight test transmissions: their pictures are built in, and every one decodes
//     in RFpix's receiver to the exact picture, with its settings and the callsign;
//   - the level, which follows the slider while the sound plays;
//   - the send sequence: the radio (Hamlib's pretend "Dummy" radio) is keyed before the
//     sound starts, stays keyed while it plays, and is always released: at the end,
//     on Stop, and when the sound device fails.

using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class SendTests
{
    // ---- the test transmissions ----

    [Fact]
    public void There_are_ten_tests_each_with_its_own_picture_and_settings()
    {
        var all = TestTransmissions.All;
        Assert.Equal(Enumerable.Range(1, 10), all.Select(t => t.Number));
        Assert.Equal(10, all.Select(t => (t.Settings, t.ReedSolomon)).Distinct().Count());
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 3, 2 }, all.Select(t => t.ReedSolomon));
        Assert.Equal("rfpix-test-9.rs3", all[8].SendName);   // sent wrapped, as QSSTV sends RS files
        foreach (var t in all)
        {
            var picture = t.Picture();
            Assert.True(picture.Length is > 3000 and < 9000, $"{t.FileName} is {picture.Length} bytes");
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, picture[..2]);   // a JPEG
            Assert.StartsWith("rfpix-test-", t.FileName);
        }
    }

    public static IEnumerable<object[]> Numbers() => TestTransmissions.All.Select(t => new object[] { t.Number });

    [Theory]
    [MemberData(nameof(Numbers))]
    public void Every_test_transmission_decodes_to_its_exact_picture(int number)
    {
        var test = TestTransmissions.All.Single(t => t.Number == number);
        var tx = test.Transmitter("KD9DD");
        Assert.InRange(tx.Duration.TotalSeconds, 20, test.ReedSolomon > 0 ? 120 : 90);   // short enough to send a few in a row

        var (received, callsigns, fac) = TransmitterTests.Receive(tx);

        Assert.Equal(test.FileName, received?.Name);
        Assert.Equal(test.Picture(), received!.Data);
        Assert.Contains("KD9DD", callsigns);
        Assert.Equal((test.Settings.Qam, test.Settings.Interleave, test.Settings.Protection, test.Settings.Bandwidth),
                     (fac!.Qam, fac.Interleave, fac.Protection, fac.Bandwidth));
    }

    // ---- the sound that goes to the radio ----

    [Fact]
    public void The_sound_has_a_lead_in_when_keyed_a_tail_always_and_the_chosen_level()
    {
        var signal = Enumerable.Repeat(0.5f, 48000).ToArray();
        var keyed = Transmission.Prepare(signal, 0.4, keyed: true);
        int lead = 48000 * 3 / 10, tail = 48000 / 2;
        Assert.Equal(lead + 48000 + tail, keyed.Length);
        Assert.All(keyed[..lead], s => Assert.Equal(0f, s));
        Assert.All(keyed[lead..(lead + 48000)], s => Assert.Equal(0.2f, s, 6));
        Assert.All(keyed[^tail..], s => Assert.Equal(0f, s));

        var vox = Transmission.Prepare(signal, 1.5, keyed: false);   // VOX: no silence before the sound
        Assert.Equal(0.5f, vox[0]);                                   // and the level never goes above 100%
        Assert.Equal(48000 + tail, vox.Length);
    }

    // ---- the level, live ----

    [Fact]
    public void Moving_the_level_during_sending_changes_the_sound_smoothly()
    {
        double wanted = 1;
        var ramp = new LevelRamp(() => wanted);
        var chunk = Enumerable.Repeat(1f, 480).ToArray();
        ramp.Apply(chunk, 0, 480);
        Assert.All(chunk, s => Assert.Equal(1f, s));

        wanted = 0.1;                                  // the slider moves from 0 dB to -20 dB
        chunk = Enumerable.Repeat(1f, 480).ToArray();
        ramp.Apply(chunk, 0, 480);
        Assert.Equal(0.1f, chunk[^1], 5);              // there by the end of the next 10 ms
        for (int i = 1; i < 480; i++)                  // in small, even steps: no click
            Assert.InRange(chunk[i - 1] - chunk[i], 0f, 0.0025f);

        chunk = Enumerable.Repeat(1f, 480).ToArray();
        ramp.Apply(chunk, 0, 480);
        Assert.All(chunk, s => Assert.Equal(0.1f, s, 5));
    }

    [Fact]
    public void The_level_never_goes_above_100_percent_or_below_silence()
    {
        double wanted = 3;
        var ramp = new LevelRamp(() => wanted);
        Assert.Equal(1, ramp.Current);
        wanted = -1;
        ramp.Apply(new float[10], 0, 10);
        Assert.Equal(0, ramp.Current);
    }

    [Fact]
    public async Task The_sound_is_played_with_the_level_the_page_gives()
    {
        using var radio = RadioConnection.Open(Dummy());
        var output = new FakeOutput();
        await Transmission.SendAsync(new float[4800], output, "radio", radio, () => 0.25, null, CancellationToken.None);
        Assert.Equal(0.25, output.LevelSeen);
    }

    // ---- keying around the sound ----

    private static RadioSettings Dummy(PttMethod ptt = PttMethod.Cat) =>
        new() { Choice = "other", OtherModel = RadioConnection.DummyModel, BaudRate = 9600, Ptt = ptt };

    [Fact]
    public async Task The_radio_is_keyed_while_the_sound_plays_and_released_after()
    {
        using var radio = RadioConnection.Open(Dummy());
        var output = new FakeOutput { WhilePlaying = () => radio.ReadPtt() };
        await Transmission.SendAsync(new float[4800], output, "radio", radio, null, null, CancellationToken.None);

        Assert.Equal(new[] { true }, output.KeyedWhilePlaying);
        Assert.False(radio.IsKeyed);
        Assert.False(radio.ReadPtt());
    }

    [Fact]
    public async Task Stop_releases_the_radio()
    {
        using var radio = RadioConnection.Open(Dummy());
        using var stop = new CancellationTokenSource();
        var output = new FakeOutput { WhilePlaying = () => { stop.Cancel(); return radio.ReadPtt(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Transmission.SendAsync(new float[4800], output, "radio", radio, null, null, stop.Token));
        Assert.False(radio.IsKeyed);
        Assert.False(radio.ReadPtt());
    }

    [Fact]
    public async Task A_sound_device_failure_releases_the_radio_and_says_why()
    {
        using var radio = RadioConnection.Open(Dummy());
        var output = new FakeOutput { Failure = new AudioException("The sound device disappeared.") };

        var ex = await Assert.ThrowsAsync<AudioException>(() =>
            Transmission.SendAsync(new float[4800], output, "radio", radio, null, null, CancellationToken.None));
        Assert.Equal("The sound device disappeared.", ex.Message);
        Assert.False(radio.ReadPtt());
    }

    [Fact]
    public async Task With_VOX_nothing_is_keyed()
    {
        using var radio = RadioConnection.Open(Dummy(PttMethod.Vox));
        var output = new FakeOutput { WhilePlaying = () => radio.IsKeyed };
        await Transmission.SendAsync(new float[4800], output, "radio", radio, null, null, CancellationToken.None);
        Assert.Equal(new[] { true }, output.KeyedWhilePlaying);   // "transmitting", by RFpix's account
        Assert.False(radio.IsKeyed);
    }

    /// <summary>A pretend sound device: notes what happened instead of playing.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        public Func<bool>? WhilePlaying { get; init; }
        public Exception? Failure { get; init; }
        public List<bool> KeyedWhilePlaying { get; } = new();
        public double? LevelSeen { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => new[] { new AudioDeviceInfo("radio", "Speakers (USB Audio CODEC)") };

        public async Task PlayAsync(string deviceId, float[] samples, int sampleRate, Func<double>? level, IProgress<double>? progress,
                                    CancellationToken cancel)
        {
            await Task.Yield();
            if (Failure is not null) throw Failure;
            LevelSeen = level?.Invoke();
            if (WhilePlaying is not null) KeyedWhilePlaying.Add(WhilePlaying());
            cancel.ThrowIfCancellationRequested();
        }

        public void Dispose() { }
    }
}
