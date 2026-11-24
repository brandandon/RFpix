// FmLevelCheck.cs - the level check for RFpix's FM signal (new in RFpix 0.7.4).
//
// On FM, how good a picture arrives depends mostly on the sending station's transmit level (its deviation):
// too low and FM's noise wins; too high and the radio's limiter (and the repeater's) clips the signal.
// Nov 23, 2026, Dan's transmit level went from -41 to 0 dB, and his pictures arrived at 18 to 29 dB quality.
//
// The check: the sender sends six frames of the FM signal (about 18 seconds), each at a different level,
// 3 dB apart, from 9 dB below the Transmit level to 6 dB above it (lower if that would pass 0 dB). Each
// frame's header says which step it is and how far it is from the Transmit level. The frames carry known
// data (64-QAM on every tone, the same in every frame, with a real picture's average and peaks), so every
// listening RFpix measures each step's quality exactly, even steps too weak to decode. The steps below the
// best one are held back by noise, the ones above it by clipping. Each listener reads out their best step
// over the air (decided by Dan, Nov 23: no reply frame), and the sender picks it on the Send tab.
//
// RFpix before 0.7.4 doesn't know the header's number for these frames (15), and ignores them.

using System.Numerics;
using static RFpix.FmSignal.FmSignalLayout;

namespace RFpix.FmSignal;

/// <summary>One step of a level check: its number (0 to 5) and how far its level is from the sender's
/// Transmit level, in dB.</summary>
public sealed record FmLevelStep(int Step, int OffsetDb)
{
    /// <summary>For people: "step 5 (+3 dB)".</summary>
    public override string ToString() => $"step {Step + 1} ({OffsetDb:+0;-0;0} dB)";
}

/// <summary>A level check as one station heard it: each step heard and its quality.</summary>
public sealed record FmLevelResult(IReadOnlyList<(FmLevelStep Step, double QualityDb)> Steps)
{
    /// <summary>The step that came through best.</summary>
    public (FmLevelStep Step, double QualityDb) Best => Steps.MaxBy(s => s.QualityDb);

    /// <summary>The fastest speed the best step's quality is enough for (with 2 dB to spare), if any.</summary>
    public FmSignalMode? Speed => FmSignalMode.All.LastOrDefault(m => m.QualityNeededDb + 2 <= Best.QualityDb);

    /// <summary>What the best step says about the level: still rising at the loudest step, or best at the
    /// quietest (null when the best is in between).</summary>
    public string? Advice
    {
        get
        {
            if (Steps.Count < 2) return null;
            var best = Best.Step;
            if (best.Step == FmLevelCheck.Steps - 1 && Steps[^1].Step.Step == best.Step)
                return "still getting better at the loudest step: the sender can go louder (raise the radio's own audio input level if the Transmit level is already at 0 dB)";
            if (best.Step == 0 && Steps[0].Step.Step == 0)
                return "best at the quietest step: the sender should turn down";
            return null;
        }
    }

    /// <summary>One line for the Receive tab and the log.</summary>
    public string Text
    {
        get
        {
            var (step, quality) = Best;
            string steps = string.Join(", ", Steps.Select(s => $"{s.Step.Step + 1}: {s.QualityDb:0}"));
            string speed = Speed is { } m ? $", enough for {m.ShortName}" : ", too low for any RFpix FM speed";
            return $"Level check: best at {step}, quality {quality:0} dB{speed}. Read out \"step {step.Step + 1}\". " +
                   $"Quality at each step (dB): {steps}." + (Advice is { } a ? $" Best {a}." : "");
        }
    }
}

public static class FmLevelCheck
{
    /// <summary>The number in the frame header that marks a level check frame (speeds use 0 to 5).</summary>
    public const int HeaderId = 15;
    public const int Steps = 6, StepDb = 3;
    private const int Bits = 6;   // 64-QAM on every data tone

    /// <summary>How long the check takes on the air (without a lead-in).</summary>
    public static double Seconds => Steps * FrameSeconds;

    /// <summary>The steps' levels against the Transmit level <paramref name="transmitDb"/>: 3 dB apart up to
    /// 6 dB above it, or up to 0 dB if that's less (the sound card can't go past full volume).</summary>
    public static int[] Offsets(double transmitDb)
    {
        int top = Math.Clamp((int)Math.Floor(-transmitDb + 1e-9), 0, 6);
        return Enumerable.Range(0, Steps).Select(i => top - (Steps - 1 - i) * StepDb).ToArray();
    }

    /// <summary>The 12-bit header value for a step (the header's speed field holds 15).</summary>
    public static int Header(FmLevelStep step) => HeaderId << 8 | (step.Step & 7) << 5 | (step.OffsetDb + 16) & 31;

    /// <summary>The step a header value names, or null if it isn't a level check frame.</summary>
    public static FmLevelStep? FromHeader(int value) => value >> 8 == HeaderId ? new((value >> 5) & 7, (value & 31) - 16) : null;

    // The known data, the same in every frame.
    private static readonly Complex[][] Data = MakeData();

    private static Complex[][] MakeData()
    {
        var random = new Random(9100);
        var bits = new byte[Bits];
        var data = new Complex[DataSymbols][];
        for (int s = 0; s < DataSymbols; s++)
        {
            data[s] = new Complex[DataCarriers.Length];
            for (int t = 0; t < DataCarriers.Length; t++)
            {
                for (int b = 0; b < Bits; b++) bits[b] = (byte)random.Next(2);
                data[s][t] = Qam.Map(bits);
            }
        }
        return data;
    }

    /// <summary>Everything a level check frame with this header sends, symbol by symbol and tone by tone.</summary>
    public static Complex[][] Sent(int header)
    {
        var sent = new Complex[FrameSymbols][];
        for (int s = 0; s < FrameSymbols; s++)
        {
            sent[s] = new Complex[FftSize];
            if (s < SyncSymbols) { foreach (int k in Carriers) sent[s][k] = Sync[k]; continue; }
            foreach (int k in PilotCarriers) sent[s][k] = Pilot(k, s);
            for (int t = 0; t < DataCarriers.Length; t++)
            {
                int k = DataCarriers[t];
                if (s == SyncSymbols)
                {
                    int bit = t < HeaderBits * HeaderCopies ? (header >> (HeaderBits - 1 - t % HeaderBits)) & 1 : 0;
                    sent[s][k] = bit == 0 ? 1 : -1;
                }
                else sent[s][k] = Data[s - SyncSymbols - HeaderSymbols][t];
            }
        }
        return sent;
    }

    /// <summary>The check's sound: one frame per step, at <paramref name="offsets"/> dB against the level a
    /// picture goes out at (so the Transmit level applies on top, as for a picture).</summary>
    public static float[] Audio(IReadOnlyList<int> offsets)
    {
        var sound = new float[offsets.Count * FrameLength];
        for (int f = 0; f < offsets.Count; f++)
        {
            var cells = Sent(Header(new FmLevelStep(f, offsets[f])));
            for (int s = 0; s < FrameSymbols; s++) FmSignalTransmitter.WriteSymbol(sound, f * FrameLength + s * SymbolLength, cells[s]);
        }
        // The same average level as a picture (FmSignalTransmitter.Level), then each step's own.
        double rms = Math.Sqrt(sound.Average(v => (double)v * v));
        double gain = FmSignalTransmitter.Level / rms;
        for (int f = 0; f < offsets.Count; f++)
        {
            float g = (float)(gain * Math.Pow(10, offsets[f] / 20.0));
            for (int i = f * FrameLength; i < (f + 1) * FrameLength; i++) sound[i] *= g;
        }
        return sound;
    }
}
