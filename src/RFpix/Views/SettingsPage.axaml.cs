// SettingsPage.axaml.cs - the code half of the Settings page.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace RFpix.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();

        // Show whichever theme is currently in use.
        var current = Application.Current?.RequestedThemeVariant;
        ThemeBox.SelectedIndex =
            current == ThemeVariant.Light ? 1 :
            current == ThemeVariant.Dark  ? 2 : 0;
    }

    // Runs when a different theme is picked: 0 = follow Windows, 1 = light, 2 = dark.
    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Application.Current is null) return;

        Application.Current.RequestedThemeVariant = ThemeBox.SelectedIndex switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
