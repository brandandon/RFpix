// TemplateStore.cs - the templates RFpix comes with, and your own (files in
// Documents\RFpix\Templates, one .rfpixtemplate file each: the template's settings as text).

using System.Text.Json;

namespace RFpix.Templates;

public static class TemplateStore
{
    public const string Extension = ".rfpixtemplate";

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix", "Templates");

    /// <summary>The ones RFpix comes with.</summary>
    public static IReadOnlyList<OverlayTemplate> BuiltIn { get; } = new[]
    {
        new OverlayTemplate
        {
            Name = "Callsign bar", BuiltIn = true,
            Items =
            {
                new() { Kind = OverlayKind.Box, X = 0, Y = 0.86, Width = 1, Height = 0.14, Fill = "#000000", Opacity = 0.55 },
                new() { Kind = OverlayKind.Text, X = 0.03, Y = 0.86, Width = 0.55, Height = 0.14, Text = "%me", Size = 0.1, OutlineColor = null },
                new() { Kind = OverlayKind.Text, X = 0.5, Y = 0.86, Width = 0.47, Height = 0.14, Text = "%date", Size = 0.06,
                        Align = OverlayAlign.Right, Bold = false, OutlineColor = null },
            },
        },
        new OverlayTemplate
        {
            Name = "CQ SSTV net", BuiltIn = true,
            Items =
            {
                new() { Kind = OverlayKind.Text, X = 0.02, Y = 0.02, Width = 0.96, Height = 0.16, Text = "CQ SSTV NET", Size = 0.13,
                        Color = "#FFE14D", Align = OverlayAlign.Center, Shadow = true },
                new() { Kind = OverlayKind.Text, X = 0.02, Y = 0.82, Width = 0.96, Height = 0.16, Text = "de %me", Size = 0.12,
                        Align = OverlayAlign.Center, Shadow = true },
            },
        },
        new OverlayTemplate
        {
            Name = "Reply", BuiltIn = true,
            Items =
            {
                new() { Kind = OverlayKind.Text, X = 0.03, Y = 0.8, Width = 0.94, Height = 0.18, Text = "%to de %me", Size = 0.13,
                        Color = "#FFE14D", Outline = 0.1 },
                new() { Kind = OverlayKind.Box, X = 0.68, Y = 0.03, Width = 0.29, Height = 0.12, Fill = "#000000", Opacity = 0.5, Corner = 0.02 },
                new() { Kind = OverlayKind.Text, X = 0.68, Y = 0.03, Width = 0.29, Height = 0.12, Text = "RST %rst", Size = 0.07,
                        Align = OverlayAlign.Center, OutlineColor = null },
            },
        },
        new OverlayTemplate
        {
            Name = "Framed", BuiltIn = true,
            Items =
            {
                new() { Kind = OverlayKind.Border, X = 0, Y = 0, Width = 1, Height = 1, Fill = "#FFFFFF", Opacity = 1, Thickness = 0.018 },
                new() { Kind = OverlayKind.Box, X = 0.018, Y = 0.82, Width = 0.964, Height = 0.162, Fill = "#1E63D6", FillTo = "#000000", Opacity = 0.75 },
                new() { Kind = OverlayKind.Text, X = 0.05, Y = 0.82, Width = 0.55, Height = 0.162, Text = "%me", Size = 0.1, OutlineColor = null, Shadow = true },
                new() { Kind = OverlayKind.Text, X = 0.5, Y = 0.82, Width = 0.45, Height = 0.162, Text = "%date\n%utc", Size = 0.05,
                        Bold = false, Align = OverlayAlign.Right, OutlineColor = null },
            },
        },
        new OverlayTemplate
        {
            Name = "Date stamp", BuiltIn = true,
            Items =
            {
                new() { Kind = OverlayKind.Box, X = 0, Y = 0.94, Width = 0.36, Height = 0.06, Fill = "#000000", Opacity = 1 },
                new() { Kind = OverlayKind.Text, X = 0.01, Y = 0.94, Width = 0.34, Height = 0.06, Text = "%date %time", Size = 0.045,
                        Bold = false, OutlineColor = null },
            },
        },
    };

    /// <summary>RFpix's templates, then yours (sorted by name). A damaged file is skipped.</summary>
    public static List<OverlayTemplate> All(string? folder = null)
    {
        var list = BuiltIn.ToList();
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder)) return list;
        var own = new List<OverlayTemplate>();
        foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            try
            {
                var t = OverlayTemplate.FromJson(File.ReadAllText(path));
                t.Path = path;
                own.Add(t);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        list.AddRange(own.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>The template with this name, or null.</summary>
    public static OverlayTemplate? Find(string? name, string? folder = null) =>
        name is null ? null : All(folder).FirstOrDefault(t => t.Name == name);

    /// <summary>Saves your template (as a new file, or over its own). Names must be new: not one of
    /// RFpix's, nor another of yours.</summary>
    public static void Save(OverlayTemplate template, string? folder = null)
    {
        string name = template.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the template a name.");
        if (BuiltIn.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"\"{name}\" comes with RFpix: pick another name.");
        folder ??= DefaultFolder;
        var others = All(folder).Where(t => !t.BuiltIn && t.Path != template.Path);
        if (others.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"You already have a template called \"{name}\".");
        Directory.CreateDirectory(folder);
        string path = template.Path ?? NewPath(folder, name);
        template.Name = name;
        File.WriteAllText(path + ".new", template.ToJson());
        File.Move(path + ".new", path, overwrite: true);
        template.Path = path;
    }

    public static void Delete(OverlayTemplate template)
    {
        if (template.BuiltIn || template.Path is null) return;
        File.Delete(template.Path);
        template.Path = null;
    }

    private static string NewPath(string folder, string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        string stem = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        string path = System.IO.Path.Combine(folder, stem + Extension);
        for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, $"{stem} ({n}){Extension}");
        return path;
    }
}
