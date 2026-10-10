// SettingsPage.axaml.cs - the code half of the Settings page.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using RFpix.Settings;

namespace RFpix.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();

        // Show the saved theme: 0 = follow Windows, 1 = light, 2 = dark.
        ThemeBox.SelectedIndex = AppSettings.Current.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
    }

    // Runs when a different theme is picked.
    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };

        App.ApplyTheme(theme);
        AppSettings.Current.Theme = theme;
        AppSettings.Current.Save();
    }
}
