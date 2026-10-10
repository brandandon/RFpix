// Fft.cs - the Fast Fourier Transform.
//
// An FFT takes a slice of audio (a wiggly line over time) and works out how much
// of each frequency is in it. That is exactly what a waterfall shows: one FFT per
// line, low frequencies on the left, high on the right.
//
// This is the classic "radix-2" FFT. You don't need to follow the maths to use it:
// put audio in "re", zeros in "im", call Transform, and each pair (re[k], im[k])
// then describes frequency number k.

namespace RFpix.Dsp;

public static class Fft
{
    /// <summary>
    /// Transforms the data in place. Both arrays must be the same length,
    /// and that length must be a power of two (1024, 2048, 4096, ...).
    /// </summary>
    public static void Transform(double[] re, double[] im)
    {
        int n = re.Length;
        if (n != im.Length || n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT arrays must be the same power-of-two length.");

        // Step 1: reorder the samples ("bit reversal") so the next step can work in place.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Step 2: combine pairs, then groups of 4, 8, 16 ... up to the whole array.
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double stepRe = Math.Cos(angle), stepIm = Math.Sin(angle);
            int half = len / 2;

            for (int start = 0; start < n; start += len)
            {
                double wRe = 1, wIm = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = start + k, b = a + half;
                    double tRe = re[b] * wRe - im[b] * wIm;
                    double tIm = re[b] * wIm + im[b] * wRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;

                    double nextRe = wRe * stepRe - wIm * stepIm;
                    wIm = wRe * stepIm + wIm * stepRe;
                    wRe = nextRe;
                }
            }
        }
    }
}
