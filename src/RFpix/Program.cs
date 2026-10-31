// Program.cs - where RFpix starts.
// When you double-click RFpix, Windows runs the Main method below first.
// It begins this run's log (Diagnostics/AppLog.cs), then starts Avalonia and hands control to
// App (see App.axaml.cs). If RFpix stops because of an error nobody expected, the details go
// into the log as its last lines.

using Avalonia;
using RFpix.Diagnostics;

namespace RFpix;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppLog.Crash(ex);
        };
        // A background task's error that nothing looked at: RFpix carries on, but it's noted.
        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppLog.Error("RFpix", $"A background task went wrong: {e.Exception.GetBaseException()}");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Crash(ex);
            throw;
        }
        AppLog.Stop();
    }

    // Sets up Avalonia: which App class to use, and "detect the platform"
    // so the same code runs on Windows, Linux and the Mac.
    // Visual Studio's live preview also calls this method.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
