// App.axaml.cs - the code half of App.axaml.
// Every .axaml file (the layout) has a matching .axaml.cs file (the code).

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RFpix;

public partial class App : Application
{
    // Reads App.axaml (themes and styles).
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Runs once Avalonia is ready: create the main window and show it.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
