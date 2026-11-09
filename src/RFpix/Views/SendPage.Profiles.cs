// SendPage.Profiles.cs - part of SendPage (SendPage.axaml.cs): the HamDRM send settings and saved profiles.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RFpix.Analog;
using RFpix.Audio;
using RFpix.Gallery;
using RFpix.HamDrm;
using RFpix.Imaging;
using RFpix.Radio;
using RFpix.Send;
using RFpix.Settings;
using RFpix.Templates;

namespace RFpix.Views;

public partial class SendPage
{
    // ---- the send settings and profiles ----

    /// <summary>The send settings the drop-downs show, as a profile (named after the chosen one, if any).</summary>
    private SendProfile CurrentSettings() => new(
        (ProfileBox.SelectedItem as string) ?? "",
        Modes[Math.Max(0, ModeBox.SelectedIndex)],
        Bandwidths[Math.Max(0, BandwidthBox.SelectedIndex)],
        Qams[Math.Max(0, QamBox.SelectedIndex)],
        Interleaves[Math.Max(0, InterleaveBox.SelectedIndex)],
        Protections[Math.Max(0, ProtectionBox.SelectedIndex)],
        Math.Max(0, ReedSolomonBox.SelectedIndex),
        TargetTimes[Math.Max(0, TargetBox.SelectedIndex)],
        Timeouts[Math.Max(0, TimeoutBox.SelectedIndex)],
        LeadIns[Math.Max(0, LeadInBox.SelectedIndex)]);

    private void ShowSendSettings(SendProfile p)
    {
        ModeBox.SelectedIndex = Array.IndexOf(Modes, p.Mode);
        BandwidthBox.SelectedIndex = Array.IndexOf(Bandwidths, p.Bandwidth);
        QamBox.SelectedIndex = Math.Max(0, Array.IndexOf(Qams, p.Qam));
        InterleaveBox.SelectedIndex = Array.IndexOf(Interleaves, p.Interleave);
        ProtectionBox.SelectedIndex = Array.IndexOf(Protections, p.Protection);
        ReedSolomonBox.SelectedIndex = Math.Clamp(p.ReedSolomon, 0, 4);
        int target = Array.IndexOf(TargetTimes, p.TargetSeconds);
        TargetBox.SelectedIndex = target >= 0 ? target : Array.IndexOf(TargetTimes, 120);
        int timeout = Array.IndexOf(Timeouts, p.TimeoutSeconds);
        TimeoutBox.SelectedIndex = timeout >= 0 ? timeout : 0;
        int lead = Array.FindIndex(LeadIns, l => Math.Abs(l - p.LeadInSeconds) < 0.01);
        LeadInBox.SelectedIndex = lead >= 0 ? lead : 0;
    }

    /// <summary>Keeps the transmit time within the repeater's time-out (the longer choices can't be picked).</summary>
    private bool KeepTargetInsideTimeout()
    {
        int timeout = Timeouts[Math.Max(0, TimeoutBox.SelectedIndex)];
        int target = TargetTimes[Math.Max(0, TargetBox.SelectedIndex)];
        if (timeout <= 0 || target <= timeout) return false;
        int best = Array.FindLastIndex(TargetTimes, t => t <= timeout);
        bool was = _updating;
        _updating = true;
        TargetBox.SelectedIndex = Math.Max(0, best);
        _updating = was;
        return true;
    }

    /// <summary>The waterfall text's time (before and after the picture), from Settings > Misc.</summary>
    private static TimeSpan TextTime =>
        WaterfallText.Extra(AppSettings.Current.WaterfallTextBefore, AppSettings.Current.WaterfallTextAfter);

    private static IEnumerable<SendProfile> AllProfiles => SendProfile.BuiltIn.Concat(AppSettings.Current.SendProfiles);

    /// <summary>Lists the profiles, and picks the one the drop-downs match (if any).</summary>
    private void FillProfiles()
    {
        bool was = _updating;
        _updating = true;
        ProfileBox.ItemsSource = AllProfiles.Select(p => p.Name).ToList();
        var current = CurrentSettings();
        string? wanted = AppSettings.Current.SendSettings?.Name;
        var match = AllProfiles.FirstOrDefault(p => p.Name == wanted && p.SameAs(current))
                    ?? AllProfiles.FirstOrDefault(p => p.SameAs(current));
        ProfileBox.SelectedItem = match?.Name;
        DeleteProfileButton.IsEnabled = match is not null && !SendProfile.BuiltIn.Contains(match);
        _updating = was;
    }

    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var profile = AllProfiles.FirstOrDefault(p => p.Name == ProfileBox.SelectedItem as string);
        DeleteProfileButton.IsEnabled = profile is not null && !SendProfile.BuiltIn.Contains(profile);
        if (profile is null) return;
        _updating = true;
        ShowSendSettings(profile);
        _updating = false;
        SaveSendSettings();
        Refit(auto: true);
    }

    private void OnSendSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (KeepTargetInsideTimeout())
            ShowStatus("The transmit time can't be longer than the repeater's time-out.", problem: false);
        FillProfiles();   // the chosen profile no longer matches, perhaps (or another now does)
        SaveSendSettings();
        Refit(auto: true);
    }

    private void SaveSendSettings()
    {
        AppSettings.Current.SendSettings = CurrentSettings();
        AppSettings.Current.Save();
    }

    private void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        string name = (ProfileNameBox.Text ?? "").Trim();
        if (name.Length == 0) { ProfileNameProblem.Text = "Type a name first."; return; }
        if (SendProfile.BuiltIn.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ProfileNameProblem.Text = "That name comes with RFpix: pick another.";
            return;
        }
        var saved = AppSettings.Current.SendProfiles;
        saved.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));   // saving again replaces it
        var profile = CurrentSettings() with { Name = name };
        saved.Add(profile);
        AppSettings.Current.SendSettings = profile;
        AppSettings.Current.Save();
        FillProfiles();
        ProfileNameProblem.Text = "";
        ProfileNameBox.Text = "";
        SaveProfileButton.Flyout?.Hide();
        ShowStatus($"Saved the profile \"{name}\".", problem: false);
    }

    private void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name) return;
        if (AppSettings.Current.SendProfiles.RemoveAll(p => p.Name == name) == 0) return;
        AppSettings.Current.SendSettings = CurrentSettings() with { Name = "" };
        AppSettings.Current.Save();
        FillProfiles();
        ShowStatus($"Deleted the profile \"{name}\". The settings it had are still in use.", problem: false);
    }
}
