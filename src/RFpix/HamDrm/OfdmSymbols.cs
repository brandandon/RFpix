// OfdmSymbols.cs - splits the signal into symbols and reads every carrier in each one.
//
// Within one symbol, each carrier is a steady tone. Its strength and phase angle
// together are a "cell": the basic unit the data is written in (later stages turn
// cells into bits). Reading a carrier means measuring that tone over the useful
// part of the symbol, exactly at the carrier's frequency, which is a single "bin"
// of a Fourier transform. That's why the frequency lock had to come first: the
// tones must be measured where they really are, not where they should be.

using System.Numerics;
using RFpix.Dsp;

namespace RFpix.HamDrm;

/// <summary>Every carrier's cell in every whole symbol of a window.</summary>
/// <param name="Cells">Cells[symbol][k] is carrier number k in that symbol (k from 0 to the mode's last carrier).</param>
/// <param name="Starts">Where each symbol's guard starts in the window, in 12 kHz samples.</param>
public sealed record OfdmSymbols(HamDrmMode Mode, Complex[][] Cells, int[] Starts)
{
    public int Count => Cells.Length;

    /// <summary>Reads the carriers of every whole symbol in the window.</summary>
    /// <param name="window">12 kHz complex samples from the <see cref="Downconverter"/>.</param>
    /// <param name="mode">The HamDRM mode.</param>
    /// <param name="symbolStart">Where a symbol's guard starts (from the mode detector).</param>
    /// <param name="offsetHz">The tuning error (from the frequency lock).</param>
    public static OfdmSymbols Read(ReadOnlySpan<Complex> window, HamDrmMode mode, int symbolStart, double offsetHz)
    {
        int tu = HamDrmModes.UsefulLength(mode);
        int tg = HamDrmModes.GuardLength(mode);
        int ts = HamDrmModes.SymbolLength(mode);
        int lastCarrier = HamDrmModes.LastCarrier(mode);
        double rate = Downconverter.OutputRate;

        // One "measuring wave" per carrier, at the frequency the carrier is actually heard at.
        var waves = new Complex[lastCarrier + 1][];
        for (int k = 0; k <= lastCarrier; k++)
        {
            double heardAt = HamDrmModes.CarrierFrequency(mode, k) + offsetHz - Downconverter.CentreFrequency;
            waves[k] = new Complex[tu];
            for (int n = 0; n < tu; n++)
                waves[k][n] = Complex.FromPolarCoordinates(1.0 / tu, -2 * Math.PI * heardAt * n / rate);
        }

        var cells = new List<Complex[]>();
        var starts = new List<int>();
        for (int s = symbolStart; s + ts <= window.Length; s += ts)
        {
            var symbol = window.Slice(s + tg, tu);   // the useful part: skip the guard
            var row = new Complex[lastCarrier + 1];
            for (int k = 0; k <= lastCarrier; k++)
            {
                Complex sum = Complex.Zero;
                var wave = waves[k];
                for (int n = 0; n < tu; n++) sum += symbol[n] * wave[n];
                row[k] = sum;
            }
            cells.Add(row);
            starts.Add(s);
        }
        return new OfdmSymbols(mode, cells.ToArray(), starts.ToArray());
    }
}
