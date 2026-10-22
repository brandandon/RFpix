// GridSquare.cs - checks a Maidenhead grid square ("QTH locator") as typed: two letters A to R,
// two digits, and optionally two letters a to x. Written the usual way: EN52 or EN52wd.

namespace RFpix.Templates;

public static class GridSquare
{
    /// <summary>The grid square written the usual way, or null if it isn't one (4 or 6 characters).</summary>
    public static string? Normalise(string typed)
    {
        string g = typed.Trim();
        if (g.Length is not (4 or 6)) return null;
        char a = char.ToUpperInvariant(g[0]), b = char.ToUpperInvariant(g[1]);
        if (a is < 'A' or > 'R' || b is < 'A' or > 'R' || !char.IsAsciiDigit(g[2]) || !char.IsAsciiDigit(g[3])) return null;
        if (g.Length == 4) return $"{a}{b}{g[2]}{g[3]}";
        char c = char.ToLowerInvariant(g[4]), d = char.ToLowerInvariant(g[5]);
        if (c is < 'a' or > 'x' || d is < 'a' or > 'x') return null;
        return $"{a}{b}{g[2]}{g[3]}{c}{d}";
    }
}
