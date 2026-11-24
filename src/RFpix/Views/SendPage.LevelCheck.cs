// SendPage.LevelCheck.cs - part of SendPage (SendPage.axaml.cs): the level check for RFpix's FM signal
// (FmLevelCheck.cs, new in 0.7.4). Level check sends six frames, 3 dB apart around the Transmit level; each
// listening station's RFpix shows its best step, which they read out over the air (decided by Dan, Nov 23,
// 2026: no reply frame); here the steps are listed with the levels they went out at, to pick from and set.
// If this station hears its own check back (a second radio, or the repeater's output), its own result is
// shown and its best step picked to start with.

using Avalonia.Interactivity;
using RFpix.FmSignal;

namespace RFpix.Views;

public partial class SendPage
{
    private int[]? _levelOffsets;          // the last check's steps, against _levelBaseDb
    private double _levelBaseDb;           // the Transmit level when it was sent
    private DateTime _levelSentAt = DateTime.MinValue;
    private FmLevelResult? _ownLevel;      // the check as this station's own RFpix heard it, if it did

    private void OnLevelCheckClick(object? sender, RoutedEventArgs e)
    {
        if (_sending is not null || SendSupport.IsSending) return;
        _levelBaseDb = LevelSlider.Value;
        var offsets = _levelOffsets = FmLevelCheck.Offsets(_levelBaseDb);
        _levelSentAt = DateTime.Now;
        _ownLevel = null;
        ShowLevelSteps();
        Transmit(() => FmLevelCheck.Audio(offsets), "a level check",
                 "Level check sent. When the stations read out their best step, pick it below and click Use this level.",
                 TimeSpan.FromSeconds(CurrentSettings().LeadInSeconds), hamDrm: true);
    }

    /// <summary>The Receive tab heard a level check: if it's the one just sent from here, this station's own result.</summary>
    public void ShowOwnLevelCheck(FmLevelResult result)
    {
        if (_levelOffsets is not { } offsets || DateTime.Now - _levelSentAt > TimeSpan.FromMinutes(2)) return;
        if (!result.Steps.All(s => s.Step.Step < offsets.Length && offsets[s.Step.Step] == s.Step.OffsetDb)) return;
        _ownLevel = result;
        ShowLevelSteps();
    }

    private void ShowLevelSteps()
    {
        if (_levelOffsets is not { } offsets) return;
        LevelStepBox.ItemsSource = offsets.Select((o, i) => $"Step {i + 1}: {SendSupport.LevelLabel(_levelBaseDb + o)} ({o:+0;-0;0} dB)").ToList();
        int nearest = Array.IndexOf(offsets, offsets.MinBy(o => Math.Abs(o)));
        LevelStepBox.SelectedIndex = _ownLevel is { } own ? own.Best.Step.Step : nearest;
        LevelCheckText.Text =
            $"Level check at {_levelSentAt:HH:mm}: six steps of about 3 seconds, from {SendSupport.LevelLabel(_levelBaseDb + offsets[0])} " +
            $"to {SendSupport.LevelLabel(_levelBaseDb + offsets[^1])}. Each listening RFpix (0.7.4 or newer) shows which step came " +
            "through best. With several stations, use the best step of the one whose quality was lowest." +
            (_ownLevel is { } r ? $" Your own RFpix heard it back: best at {r.Best.Step}, quality {r.Best.QualityDb:0} dB." : "");
        LevelCheckPanel.IsVisible = true;
    }

    private void OnUseLevelClick(object? sender, RoutedEventArgs e)
    {
        if (_levelOffsets is not { } offsets || LevelStepBox.SelectedIndex is < 0 or >= FmLevelCheck.Steps) return;
        int step = LevelStepBox.SelectedIndex;
        LevelSlider.Value = Math.Clamp(_levelBaseDb + offsets[step], LevelSlider.Minimum, 0);   // saved, as when it's moved by hand
        ShowStatus($"Transmit level set to {SendSupport.LevelLabel(LevelSlider.Value)} (step {step + 1} of the level check).", problem: false);
    }

    private void OnCloseLevelCheckClick(object? sender, RoutedEventArgs e) => LevelCheckPanel.IsVisible = false;
}
