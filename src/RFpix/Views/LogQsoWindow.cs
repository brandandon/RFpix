// LogQsoWindow.cs - Log QSO (on the TX/RX page), like WSJT-X's: the contact filled in from the
// last picture received and the Send tab, every box editable; Log saves it in RFpix's own log
// file and sends it to a logging program (Settings > Logging).

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RFpix.Logbook;
using RFpix.Settings;

namespace RFpix.Views;

public sealed class LogQsoWindow : Window
{
    private readonly TextBox _call = Box(140, upper: true), _name = Box(220), _when = Box(170), _frequency = Box(140),
                             _mode = Box(100, upper: true), _rstSent = Box(70), _rstReceived = Box(70),
                             _grid = Box(90), _comment = Box(320);
    private readonly TextBlock _band = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string _myCall, _myGrid;

    /// <summary>The result, once logged (null if cancelled).</summary>
    public LogResult? Result { get; private set; }
    public Qso? Logged { get; private set; }

    public LogQsoWindow(Qso draft)
    {
        Title = "RFpix – Log QSO";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding = new Thickness(20, 16);
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            Icon = main.Icon;
        _myCall = draft.MyCall;
        _myGrid = draft.MyGrid;

        _call.Text = draft.Call;
        _name.Text = draft.Name;
        _when.Text = draft.StartUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        _frequency.Text = draft.FrequencyMHz is double mhz ? mhz.ToString("0.000000", CultureInfo.InvariantCulture) : "";
        _mode.Text = draft.Mode;
        _rstSent.Text = draft.RstSent;
        _rstReceived.Text = draft.RstReceived;
        _grid.Text = draft.Grid;
        _comment.Text = draft.Comment;
        _frequency.TextChanged += (_, _) => ShowBand();
        ShowBand();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 8 };
        void Row(string label, Control box, Control? beside = null)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Control right = box;
            if (beside is not null) right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { box, beside } };
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            grid.Children.Add(text);
            grid.Children.Add(right);
        }
        Row("Call", _call);
        Row("Name", _name);
        Row("Date and time (UTC)", _when);
        Row("Frequency (MHz)", _frequency, _band);
        Row("Mode", _mode);
        Row("RST sent", _rstSent);
        Row("RST received", _rstReceived);
        Row("Their grid", _grid);
        Row("Comment", _comment);

        var log = new Button { Content = "Log", Classes = { "accent" }, MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        log.Click += async (_, _) =>
        {
            if (Read() is not { } qso) return;
            log.IsEnabled = false;
            var settings = AppSettings.Current.Logging;
            var result = await Task.Run(() => ContactLog.Log(qso, settings));
            log.IsEnabled = true;
            if (!result.Saved && !result.Sent)
            {
                _status.Foreground = Brushes.OrangeRed;
                _status.Text = result.SaveProblem is not null
                    ? $"Couldn't save it: {result.SaveProblem}" + (result.SendProblem is not null ? $" Nor send it: {result.SendProblem}" : "")
                    : $"Couldn't send it: {result.SendProblem}";
                return;
            }
            Logged = qso;
            Result = result;
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                grid,
                _status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { log, cancel } },
            },
        };
        Opened += (_, _) => { _call.Focus(); _call.SelectAll(); };
    }

    private static TextBox Box(double width, bool upper = false)
    {
        var box = new TextBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        if (upper) box.LostFocus += (_, _) => box.Text = (box.Text ?? "").Trim().ToUpperInvariant();
        return box;
    }

    private void ShowBand() =>
        _band.Text = ParseFrequency(_frequency.Text) is double mhz ? Bands.For(mhz) ?? "(not a ham band)" : "";

    /// <summary>"14.23", "14.230000" or "14,230" (MHz); also "14.230.000" as radios show it.</summary>
    internal static double? ParseFrequency(string? text)
    {
        string t = (text ?? "").Trim().Replace(',', '.');
        if (t.Length == 0) return null;
        int first = t.IndexOf('.');
        if (first >= 0 && t.IndexOf('.', first + 1) >= 0) t = t[..(first + 1)] + t[(first + 1)..].Replace(".", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) && mhz > 0 ? mhz : null;
    }

    private Qso? Read()
    {
        string call = (_call.Text ?? "").Trim().ToUpperInvariant();
        if (!IsCall(call)) return Problem("Type their call (letters, numbers and /).");
        if (!DateTime.TryParseExact((_when.Text ?? "").Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            return Problem("The date and time should look like 2026-11-01 18:10 (UTC).");
        double? mhz = null;
        if ((_frequency.Text ?? "").Trim().Length > 0)
        {
            mhz = ParseFrequency(_frequency.Text);
            if (mhz is null) return Problem("The frequency should be in MHz, like 14.230.");
        }
        string mode = (_mode.Text ?? "").Trim().ToUpperInvariant();
        return new Qso(call, (_name.Text ?? "").Trim(), when, mhz, mode.Length > 0 ? mode : "SSTV",
                       (_rstSent.Text ?? "").Trim(), (_rstReceived.Text ?? "").Trim(), (_grid.Text ?? "").Trim(),
                       (_comment.Text ?? "").Trim(), _myCall, _myGrid);
    }

    internal static bool IsCall(string call) =>
        call.Length is >= 3 and <= 13 && call.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '/') && call.Any(char.IsDigit);

    private Qso? Problem(string message)
    {
        _status.Foreground = Brushes.OrangeRed;
        _status.Text = message;
        return null;
    }
}
