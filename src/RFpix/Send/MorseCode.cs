// MorseCode.cs - sound for sending text (a callsign) in Morse code (CW).
//
// The timing is the standard one, counted in "dits": a dah is 3 dits, the gap inside a
// letter 1 dit, between letters 3 dits, between words 7. Speed in words per minute uses
// the word "PARIS" (50 dits long), so a dit lasts 1.2 / WPM seconds: 80 ms at 15 WPM.
// Each tone fades in and out over 5 ms, so it doesn't click on neighbouring frequencies.

namespace RFpix.Send;

public static class MorseCode
{
    public const int SampleRate = 48000;
    public const double DefaultToneHz = 750, DefaultWpm = 15;

    /// <summary>The tone's strength: its average power matches a HamDRM signal's (about -15 dBFS).</summary>
    public const double Amplitude = 0.25;

    private const double RampSeconds = 0.005;

    private static readonly Dictionary<char, string> Codes = new()
    {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".", ['F'] = "..-.",
        ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---", ['K'] = "-.-", ['L'] = ".-..",
        ['M'] = "--", ['N'] = "-.", ['O'] = "---", ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.",
        ['S'] = "...", ['T'] = "-", ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-",
        ['Y'] = "-.--", ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['/'] = "-..-.", ['?'] = "..--..", ['.'] = ".-.-.-", [','] = "--..--", ['='] = "-...-",
        ['+'] = ".-.-.", ['-'] = "-....-",
    };

    /// <summary>The dots and dashes for a character, or null if Morse has none.</summary>
    public static string? Code(char c) => Codes.TryGetValue(char.ToUpperInvariant(c), out var code) ? code : null;

    /// <summary>The text's on/off pattern in dits: true = tone. Characters Morse doesn't have are skipped.</summary>
    public static bool[] Pattern(string text)
    {
        var dits = new List<bool>();
        bool newWord = false;
        foreach (char c in (text ?? "").Trim())
        {
            if (c == ' ') { newWord = true; continue; }
            if (Code(c) is not { } code) continue;
            if (dits.Count > 0) dits.AddRange(Enumerable.Repeat(false, newWord ? 7 : 3));
            newWord = false;
            for (int i = 0; i < code.Length; i++)
            {
                if (i > 0) dits.Add(false);
                dits.AddRange(Enumerable.Repeat(true, code[i] == '-' ? 3 : 1));
            }
        }
        return dits.ToArray();
    }

    /// <summary>How long the text takes in Morse code.</summary>
    public static TimeSpan Duration(string text, double wpm = DefaultWpm) =>
        TimeSpan.FromSeconds(Pattern(text).Length * (int)Math.Round(1.2 / wpm * SampleRate) / (double)SampleRate);

    /// <summary>The Morse sound for the text: 48 kHz, -1 to 1.</summary>
    public static float[] Audio(string text, double toneHz = DefaultToneHz, double wpm = DefaultWpm)
    {
        var pattern = Pattern(text);
        int ditSamples = (int)Math.Round(1.2 / wpm * SampleRate);
        int ramp = (int)(RampSeconds * SampleRate);
        var audio = new float[pattern.Length * ditSamples];
        int i = 0;
        while (i < pattern.Length)
        {
            if (!pattern[i]) { i++; continue; }
            int start = i;
            while (i < pattern.Length && pattern[i]) i++;   // one dit or dah: a run of "on"
            int from = start * ditSamples, length = (i - start) * ditSamples;
            for (int n = 0; n < length; n++)
            {
                // Fade in and out (a raised-cosine shape) over the first and last 5 ms.
                double shape = n < ramp ? 0.5 - 0.5 * Math.Cos(Math.PI * n / ramp)
                             : n >= length - ramp ? 0.5 - 0.5 * Math.Cos(Math.PI * (length - n) / ramp)
                             : 1;
                audio[from + n] = (float)(Amplitude * shape * Math.Sin(2 * Math.PI * toneHz * (from + n) / SampleRate));
            }
        }
        return audio;
    }
}
