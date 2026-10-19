"""make_font.py - draws the waterfall-text font and writes it as C# (src/RFpix/Send/WaterfallFont.cs).

Waterfall text is drawn with a small bitmap font: each character is a grid of on/off pixels.
This script draws every printable ASCII character with Liberation Sans Bold (SIL Open Font
Licence; it has the same letter widths as Arial, which QSSTV uses for its waterfall text),
at a size where a capital letter is 11 pixels tall, and stores the pixels.

Run:  python3 tools/waterfall-font/make_font.py  (needs Pillow and the Liberation fonts)
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

FONT = "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf"
SIZE = 15          # pixels per em: capitals come out 11 pixels tall
THRESHOLD = 110    # 0-255: grey above this counts as "on"
OUT = Path(__file__).resolve().parents[2] / "src/RFpix/Send/WaterfallFont.cs"

font = ImageFont.truetype(FONT, SIZE)
ascent, descent = font.getmetrics()
height = ascent + descent

glyphs = {}
for code in range(32, 127):
    ch = chr(code)
    advance = round(font.getlength(ch))
    img = Image.new("L", (advance + 4, height), 0)
    ImageDraw.Draw(img).text((0, 0), ch, font=font, fill=255)
    rows = []
    for y in range(height):
        bits = 0
        for x in range(advance + 4):
            if img.getpixel((x, y)) > THRESHOLD:
                bits |= 1 << x
        rows.append(bits)
    width = max(advance, max((r.bit_length() for r in rows), default=0))
    glyphs[ch] = (width, rows)

# Drop the rows no character uses (above the tallest, below the lowest).
used = [y for y in range(height) if any(rows[y] for _, rows in glyphs.values())]
top, bottom = min(used), max(used)
glyphs = {ch: (w, rows[top:bottom + 1]) for ch, (w, rows) in glyphs.items()}
rows_per_glyph = bottom - top + 1

def literal(ch):
    return {"\\": "'\\\\'", "'": "'\\''"}.get(ch, f"'{ch}'")

lines = []
for ch, (w, rows) in glyphs.items():
    lines.append(f"        [{literal(ch)}] = new({w}, new uint[] {{ {', '.join(f'0x{r:X}' for r in rows)} }}),")

OUT.write_text(f"""// WaterfallFont.cs - the font waterfall text is drawn with. MADE BY A SCRIPT: don't edit by hand;
// change and run tools/waterfall-font/make_font.py instead.
//
// Each printable character is a grid of on/off pixels, {rows_per_glyph} rows tall, drawn from Liberation Sans Bold
// (SIL Open Font Licence; same letter widths as Arial, which QSSTV uses). Each row is a number whose
// bits are the pixels: bit 0 is the leftmost.

namespace RFpix.Send;

/// <summary>One character: how many pixels wide it is (including the gap after it) and its rows, top first.</summary>
public sealed record WaterfallGlyph(int Width, uint[] Rows);

public static class WaterfallFont
{{
    /// <summary>Rows in every character.</summary>
    public const int Height = {rows_per_glyph};

    public static IReadOnlyDictionary<char, WaterfallGlyph> Glyphs {{ get; }} = new Dictionary<char, WaterfallGlyph>
    {{
{chr(10).join(lines)}
    }};
}}
""")
print(f"{len(glyphs)} characters, {rows_per_glyph} rows; widths {min(w for w,_ in glyphs.values())}-{max(w for w,_ in glyphs.values())}")
