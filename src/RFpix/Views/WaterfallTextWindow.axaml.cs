// WaterfallTextWindow.axaml.cs - the code half of the "WF TXT" window. It returns the text
// to send (ShowDialog<string?>), or null if cancelled; the Send tab does the sending.

using Avalonia.Controls;
using Avalonia.Interactivity;
using RFpix.Imaging;
using RFpix.Send;
using RFpix.Settings;

namespace RFpix.Views;

public partial class WaterfallTextWindow : Window
{
    public WaterfallTextWindow()
    {
        InitializeComponent();
        WfTextBox.Text = AppSettings.Current.LastWaterfallText ?? "";
        WfTextBox.TextChanged += (_, _) => ShowPreview();
        Opened += (_, _) => { WfTextBox.Focus(); WfTextBox.SelectAll(); };
        ShowPreview();
    }

    private void ShowPreview()
    {
        string text = (WfTextBox.Text ?? "").Trim();
        CountText.Text = $"{(WfTextBox.Text ?? "").Length} / {WaterfallText.MaxLength}";
        var rows = WaterfallText.Pixels(text);
        SendButton.IsEnabled = rows.Length > 0;
        TimeText.Text = rows.Length > 0 ? $"Takes {WaterfallText.Duration(text).TotalSeconds:0.0} s on the air" : "";
        var old = PreviewImage.Source as IDisposable;
        PreviewImage.Source = rows.Length > 0
            ? Bitmaps.From(WaterfallPicture.Preview(rows.Select(r => r.Select(on => on ? 1.0 : 0.0).ToArray()).ToArray()))
            : null;
        old?.Dispose();
    }

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        string text = (WfTextBox.Text ?? "").Trim();
        if (text.Length == 0) return;
        AppSettings.Current.LastWaterfallText = text;
        AppSettings.Current.Save();
        Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
