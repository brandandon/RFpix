// OverlayTemplate.cs - overlay templates: text, boxes and logos drawn on a picture before
// it's sent (a callsign bar, "CQ SSTV NET", "W9ABC de KD9DD  RST 595"...).
//
// Everything is measured as a share of the picture, not in pixels: an item 3% in from the
// left, 86% down, 60% wide and 14% tall, with letters 9% of the picture's height. So one
// template looks the same on a 1280-pixel HamDRM picture and a 320 x 256 Martin 1 frame.
// It's drawn at the size that goes out (after the optimizer, or after fitting to the
// analog mode's frame), so the letters stay sharp.
//
// Text can hold codes filled in when sending (TemplateFields):
//   %me   your callsign        %to    the station you're sending to
//   %name their name           %rst   the signal report (e.g. 595)
//   %date today's date         %time  the time (local), %utc the time in UTC
//
// Templates are saved as small files (one each) in Documents\RFpix\Templates, logos and all,
// so they can be copied or shared.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RFpix.Templates;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayKind { Text, Box, Image, Border }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayAlign { Left, Center, Right }

/// <summary>One thing drawn on the picture. Its place is a rectangle, in shares of the picture
/// (0 to 1): left, top, width, height. Each kind uses the settings that apply to it.</summary>
public sealed class OverlayItem
{
    public OverlayKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 0.5;
    public double Height { get; set; } = 0.15;

    // ---- text ----
    public string Text { get; set; } = "";
    /// <summary>The letters' size, as a share of the picture's height.</summary>
    public double Size { get; set; } = 0.08;
    public string Font { get; set; } = "Arial";
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public OverlayAlign Align { get; set; } = OverlayAlign.Left;
    /// <summary>An outline round the letters (null for none), and its thickness as a share of the letter size.</summary>
    public string? OutlineColor { get; set; } = "#000000";
    public double Outline { get; set; } = 0.08;
    public bool Shadow { get; set; }
    /// <summary>Make the letters smaller if the text is too long for its rectangle.</summary>
    public bool ShrinkToFit { get; set; } = true;

    // ---- box and border ----
    public string Fill { get; set; } = "#000000";
    /// <summary>A second colour to fade to, left to right (null for a plain colour).</summary>
    public string? FillTo { get; set; }
    /// <summary>0 (see-through) to 1 (solid).</summary>
    public double Opacity { get; set; } = 0.6;
    /// <summary>Rounded corners, as a share of the picture's height.</summary>
    public double Corner { get; set; }
    /// <summary>For a border: its thickness, as a share of the picture's height.</summary>
    public double Thickness { get; set; } = 0.02;

    // ---- image (a logo) ----
    /// <summary>The picture (PNG, JPEG...), kept in the template so it travels with it.</summary>
    public string? ImageData { get; set; }
    public bool KeepShape { get; set; } = true;

    public OverlayItem Copy() => (OverlayItem)MemberwiseClone();

    /// <summary>For lists: what the item is.</summary>
    public string Describe() => Kind switch
    {
        OverlayKind.Text => $"Text: {(Text.Length > 30 ? Text[..30] + "…" : Text)}",
        OverlayKind.Box => "Box",
        OverlayKind.Image => "Logo",
        _ => "Border",
    };
}

public sealed class OverlayTemplate
{
    public string Name { get; set; } = "New template";
    public int FormatVersion { get; set; } = 1;
    /// <summary>Back to front: later items are drawn over earlier ones.</summary>
    public List<OverlayItem> Items { get; set; } = new();

    /// <summary>Comes with RFpix (can't be changed or deleted; "Save as" makes your own copy).</summary>
    [JsonIgnore] public bool BuiltIn { get; init; }

    /// <summary>Where it's saved, or null if it isn't (yet).</summary>
    [JsonIgnore] public string? Path { get; set; }

    /// <summary>The fill-in codes its text uses (to show only the boxes it needs).</summary>
    public IReadOnlySet<string> Codes() =>
        Items.Where(i => i.Kind == OverlayKind.Text)
             .SelectMany(i => TemplateFields.Codes.Where(c => i.Text.Contains(c, StringComparison.OrdinalIgnoreCase)))
             .ToHashSet();

    public OverlayTemplate Copy(string? name = null) => new()
    {
        Name = name ?? Name,
        FormatVersion = FormatVersion,
        Items = Items.Select(i => i.Copy()).ToList(),
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a template file's text, or throws <see cref="JsonException"/>.</summary>
    public static OverlayTemplate FromJson(string json)
    {
        var t = JsonSerializer.Deserialize<OverlayTemplate>(json, Json) ?? throw new JsonException("Empty template.");
        t.Items ??= new();
        return t;
    }
}

/// <summary>What fills a template's codes when it's used.</summary>
public sealed record TemplateFields(string MyCall, string To, string Name, string Rst, DateTime When)
{
    public static readonly string[] Codes = { "%me", "%to", "%name", "%rst", "%date", "%time", "%utc" };

    /// <summary>The text with its codes filled in. A line left empty by them is dropped, and spaces
    /// tidied ("%to de %me" with no To callsign becomes "de KD9DD").</summary>
    public string Fill(string text)
    {
        var utc = When.ToUniversalTime();
        string filled = text
            .Replace("%name", Name, StringComparison.OrdinalIgnoreCase)
            .Replace("%me", MyCall, StringComparison.OrdinalIgnoreCase)
            .Replace("%to", To, StringComparison.OrdinalIgnoreCase)
            .Replace("%rst", Rst, StringComparison.OrdinalIgnoreCase)
            .Replace("%date", When.ToString("MMM d, yyyy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("%time", When.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("%utc", utc.ToString("HHmm", CultureInfo.InvariantCulture) + "Z", StringComparison.OrdinalIgnoreCase);
        var lines = filled.Replace("\r", "").Split('\n')
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }
}
