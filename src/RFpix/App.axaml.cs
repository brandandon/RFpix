// App.axaml.cs - the code half of App.axaml.
// Every .axaml file (the layout) has a matching .axaml.cs file (the code).

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using RFpix.Settings;

namespace RFpix;

public partial class App : Application
{
    // Reads App.axaml (themes and styles).
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Runs once Avalonia is ready: apply the saved theme, then create and show the main window.
    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(AppSettings.Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>"Light", "Dark", or anything else to follow the computer's setting.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;

        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
