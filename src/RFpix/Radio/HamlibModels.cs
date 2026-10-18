// HamlibModels.cs - every radio Hamlib knows: make, model and Hamlib's number for it.
//
// For the Radio page's "Other radio" box: type part of a make or model ("991") and pick
// it from the list, instead of looking up the number. The list comes from the Hamlib
// library RFpix ships with, so it always matches what that Hamlib can drive.
//
// How: Hamlib's rig_list_foreach calls back once per radio with a pointer to that
// radio's "caps" record. Only its first three fields are read, which have stayed the
// same through Hamlib 4.x: the model number (a 4-byte number), then pointers to the
// model name and the maker's name.

using System.Runtime.InteropServices;

namespace RFpix.Radio;

/// <summary>One radio Hamlib knows.</summary>
public sealed record HamlibModel(int Number, string Make, string Model)
{
    /// <summary>What the list shows, e.g. "Yaesu FT-991A (1035)".</summary>
    public override string ToString() => $"{Make} {Model} ({Number})";
}

public static class HamlibModels
{
    private static IReadOnlyList<HamlibModel>? _all;

    /// <summary>Every radio, sorted by make then model. Empty if Hamlib can't be loaded.</summary>
    public static IReadOnlyList<HamlibModel> All()
    {
        if (_all is not null) return _all;
        var models = new List<HamlibModel>();
        try
        {
            HamlibNative.rig_set_debug(0);
            HamlibNative.rig_load_all_backends();
            HamlibNative.RigListCallback each = (caps, _) =>
            {
                int number = Marshal.ReadInt32(caps);
                // The pointers come after the 4-byte number, lined up on a pointer boundary.
                string? model = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(caps, IntPtr.Size));
                string? make = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(caps, 2 * IntPtr.Size));
                if (number > 0 && !string.IsNullOrWhiteSpace(model))
                    models.Add(new HamlibModel(number, (make ?? "").Trim(), model.Trim()));
                return 1;   // carry on to the next radio
            };
            HamlibNative.rig_list_foreach(each, IntPtr.Zero);
            GC.KeepAlive(each);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Array.Empty<HamlibModel>();   // the Radio page already explains a missing Hamlib
        }
        return _all = models
            .GroupBy(m => m.Number).Select(g => g.First())
            .OrderBy(m => m.Make, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The model number in what was typed: a plain number ("1035") or a list entry
    /// ("Yaesu FT-991 (1035)"). Null if there isn't one.</summary>
    public static int? NumberFrom(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, out int plain)) return plain > 0 ? plain : null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\((\d+)\)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out int listed) ? listed : null;
    }

    /// <summary>The radio with this number, or null.</summary>
    public static HamlibModel? Find(int number) => All().FirstOrDefault(m => m.Number == number);
}
