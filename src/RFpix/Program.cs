// Program.cs - where RFpix starts.
// When you double-click RFpix, Windows runs the Main method below first.
// Its only job is to start Avalonia and hand control to App (see App.axaml.cs).

using Avalonia;

namespace RFpix;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Sets up Avalonia: which App class to use, and "detect the platform"
    // so the same code runs on Windows now and on Linux and Mac later.
    // Visual Studio's live preview also calls this method.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
