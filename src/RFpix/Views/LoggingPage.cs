// LoggingPage.cs - Settings > Logging: where contacts logged with Log QSO (TX/RX page) go. Each
// is saved in RFpix's own log file (ADIF), and sent to a logging program on this computer (HRD
// Logbook, say), the way WSJT-X sends them. Saved as you change it, like the Misc tab.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RFpix.Logbook;
using RFpix.Settings;

namespace RFpix.Views;

public sealed class LoggingPage : UserControl
{
    private readonly CheckBox _send = new() { Content = "Send logged contacts to a logging program" };
    private readonly TextBox _address = new() { Width = 160 }, _port = new() { Width = 80 };
    private readonly TextBlock _count = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _clear = new() { Content = "Clear log file" };
    private bool _confirming;

    public LoggingPage()
    {
        var s = AppSettings.Current.Logging;
        _send.IsChecked = s.SendToLogger;
        _address.Text = s.Address;
        _port.Text = s.Port.ToString(CultureInfo.InvariantCulture);
        _address.IsEnabled = _port.IsEnabled = s.SendToLogger;
        _send.IsCheckedChanged += (_, _) =>
        {
            AppSettings.Current.Logging.SendToLogger = _send.IsChecked == true;
            _address.IsEnabled = _port.IsEnabled = _send.IsChecked == true;
            AppSettings.Current.Save();
        };
        _address.LostFocus += (_, _) =>
        {
            string address = (_address.Text ?? "").Trim();
            if (address.Length == 0) { _address.Text = AppSettings.Current.Logging.Address; return; }
            AppSettings.Current.Logging.Address = address;
            AppSettings.Current.Save();
        };
        _port.LostFocus += (_, _) =>
        {
            if (int.TryParse((_port.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535)
            {
                AppSettings.Current.Logging.Port = port;
                AppSettings.Current.Save();
                Status("", problem: false);
            }
            else
            {
                _port.Text = AppSettings.Current.Logging.Port.ToString(CultureInfo.InvariantCulture);
                Status("The port is a number from 1 to 65535 (2237, as WSJT-X uses, for HRD Logbook).", problem: true);
            }
        };

        var open = new Button { Content = "Open folder" };
        open.Click += async (_, _) =>
        {
            try
            {
                string folder = Path.GetDirectoryName(ContactLog.DefaultFile)!;
                Directory.CreateDirectory(folder);
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
            }
            catch (Exception ex) { Status($"Couldn't open the folder: {ex.Message}", problem: true); }
        };
        _clear.Click += (_, _) =>
        {
            // Asks first: a second click within the note's time clears it.
            if (!_confirming)
            {
                _confirming = true;
                _clear.Content = "Yes, clear it";
                Status($"Clear RFpix's log file? Its {ContactLog.Count()} contacts are removed from it. (Your logging program keeps its own copy of the ones sent to it.) Click Yes, clear it to go ahead.", problem: false);
                return;
            }
            _confirming = false;
            _clear.Content = "Clear log file";
            try
            {
                ContactLog.Clear();
                Status("RFpix's log file is cleared.", problem: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status($"Couldn't clear it: {ex.Message}", problem: true);
            }
            ShowCount();
        };

        Content = new ScrollViewer
        {
            FontSize = 14,
            Content = new StackPanel
            {
                Spacing = 22,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    Section("Logging program",
                        Hint("Each contact you log with Log QSO (at the top of the TX/RX page) is sent to a logging program on this computer, the way WSJT-X sends them. In HRD Logbook: QSO Forwarding, UDP Receive, port 2237. If no logging program is listening, nothing goes wrong: the contact is still saved in RFpix's log file."),
                        _send,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 10,
                            Children = { Label("Address"), _address, Label("Port"), _port },
                        }),
                    Section("RFpix's log file",
                        Hint($"Every contact is also saved in {ContactLog.DefaultFile}, in ADIF, the format every logging program can import."),
                        _count,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, _clear } },
                        _status),
                },
            },
        };
        AttachedToVisualTree += (_, _) => { ShowCount(); _confirming = false; _clear.Content = "Clear log file"; };
    }

    private void ShowCount()
    {
        try
        {
            int n = ContactLog.Count();
            _count.Text = n == 0 ? "No contacts in it yet." : n == 1 ? "1 contact in it." : $"{n} contacts in it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _count.Text = ""; }
    }

    private void Status(string message, bool problem) => StatusLine.Show(_status, message, problem, "Logging");

    private static StackPanel Section(string title, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "Hint" } };
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
}
