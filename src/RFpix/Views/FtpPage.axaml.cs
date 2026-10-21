// FtpPage.axaml.cs - the code half of the FTP server tab. The boxes show the saved settings;
// Save keeps them (the password encrypted) and starts, restarts or stops uploading; Test
// logs in to the server and lists its folder.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RFpix.Settings;
using RFpix.Upload;

namespace RFpix.Views;

public partial class FtpPage : UserControl, IDisposable
{
    private static readonly string[] Protocols = { "FTP", "FTPS", "SFTP" };

    public FtpPage()
    {
        InitializeComponent();
        ProtocolBox.ItemsSource = new[] { "FTP (plain)", "FTPS (FTP with encryption)", "SFTP (over SSH)" };
        ProtocolBox.SelectionChanged += (_, _) => OnProtocolChanged();
        SaveCopiesBox.IsCheckedChanged += (_, _) => StampBox.IsEnabled = CopySentBox.IsEnabled = SaveCopiesBox.IsChecked == true;
        Show(AppSettings.Current.Upload);
        LogBox.Text = string.Join("\n", UploadService.RecentLog);
        UploadService.Logged += OnLogged;
    }

    private void Show(UploadSettings s)
    {
        SaveCopiesBox.IsChecked = s.SaveCopies;
        StampBox.IsChecked = s.StampCopies;
        CopySentBox.IsChecked = s.CopySent;
        StampBox.IsEnabled = CopySentBox.IsEnabled = s.SaveCopies;
        FolderBox.Text = s.Folder;
        UploadBox.IsChecked = s.Upload;
        ProtocolBox.SelectedIndex = Math.Max(0, Array.IndexOf(Protocols, s.Protocol));
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString();
        UserBox.Text = s.Username;
        PasswordBox.Text = "";
        PasswordBox.Watermark = s.ProtectedPassword is null ? "" : "(saved; type to change)";
        RemoteFolderBox.Text = s.RemoteFolder;
        CountBox.Text = s.ImageCount.ToString();
        PrefixBox.Text = s.RemotePrefix;
        ExtensionBox.Text = s.RemoteExtension;
        ArchiveBox.Text = s.ArchiveName;
        StableBox.Text = s.StableSeconds.ToString("0.#");
    }

    // FTP and FTPS use port 21, SFTP 22: switch it if it's still one of those.
    private void OnProtocolChanged()
    {
        string port = (PortBox.Text ?? "").Trim();
        if (port is "21" or "22" or "") PortBox.Text = ProtocolBox.SelectedIndex == 2 ? "22" : "21";
    }

    /// <summary>The settings as the boxes show them, or a problem.</summary>
    private (UploadSettings? Settings, string? Problem) Read()
    {
        var old = AppSettings.Current.Upload;
        if (!int.TryParse(PortBox.Text?.Trim(), out int port)) return (null, "The port must be a number (usually 21, or 22 for SFTP).");
        if (!int.TryParse(CountBox.Text?.Trim(), out int count) || count < 1) return (null, "Pictures shown must be a whole number, 1 or more.");
        if (!double.TryParse(StableBox.Text?.Trim(), out double stable) || stable < 0) return (null, "The wait must be a number of seconds (0 or more).");
        string ext = (ExtensionBox.Text ?? "").Trim();
        if (ext.Length > 0 && !ext.StartsWith('.')) ext = "." + ext;
        string folder = (FolderBox.Text ?? "").Trim();
        if (folder.Length == 0) return (null, "Choose the folder for the copies.");
        string archive = (ArchiveBox.Text ?? "").Trim();
        if (archive.Length == 0 || archive.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (null, "Enter a name for the archive subfolder (such as Archives).");
        string typed = PasswordBox.Text ?? "";
        var s = new UploadSettings
        {
            SaveCopies = SaveCopiesBox.IsChecked == true,
            StampCopies = StampBox.IsChecked == true,
            CopySent = CopySentBox.IsChecked == true,
            Folder = folder,
            Upload = UploadBox.IsChecked == true,
            ArchiveName = archive,
            Protocol = Protocols[Math.Clamp(ProtocolBox.SelectedIndex, 0, 2)],
            Host = (HostBox.Text ?? "").Trim(),
            Port = port,
            Username = (UserBox.Text ?? "").Trim(),
            ProtectedPassword = typed.Length > 0 ? Secrets.Protect(typed) : old.ProtectedPassword,
            RemoteFolder = (RemoteFolderBox.Text ?? "").Trim(),
            StableSeconds = stable,
            ImageCount = count,
            RemotePrefix = (PrefixBox.Text ?? "").Trim(),
            RemoteExtension = ext,
        };
        if (s.Upload && s.Problem() is { } problem) return (null, problem);
        return (s, null);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var (s, problem) = Read();
        if (s is null) { ShowStatus(problem!, problem: true); return; }
        AppSettings.Current.Upload = s;
        AppSettings.Current.Save();
        UploadService.Restart();
        Show(s);
        ShowStatus(s.Upload ? $"Saved. Uploading new pictures from {s.Folder} to {s.Host}."
                   : s.SaveCopies ? $"Saved. Copies go to {s.Folder}; uploading is off."
                   : "Saved. Copies and uploading are off.", problem: false);
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        var (s, problem) = Read();
        if (s is null || s.Problem() is { } p2) { ShowStatus(problem ?? s!.Problem()!, problem: true); return; }
        TestButton.IsEnabled = false;
        ShowStatus($"Connecting to {s.Host} ({s.Protocol}, port {s.Port})…", problem: false);
        try
        {
            string result = await Task.Run(() => FolderUploader.Test(s, RemoteFolders.Connect));
            ShowStatus(result, problem: false);
            UploadService.Add($"Test: {result}");
        }
        catch (Exception ex)
        {
            ShowStatus($"The test failed: {ex.Message}", problem: true);
            UploadService.Add($"Test failed: {ex.Message}");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var start = Directory.Exists(FolderBox.Text) ? await top.StorageProvider.TryGetFolderFromPathAsync(FolderBox.Text!) : null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for the website copies",
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) FolderBox.Text = path;
    }

    private void OnLogged(string line) => Dispatcher.UIThread.Post(() =>
    {
        var lines = (LogBox.Text ?? "").Split('\n').Where(l => l.Length > 0).TakeLast(199).Append(line);
        LogBox.Text = string.Join("\n", lines);
        LogBox.CaretIndex = LogBox.Text.Length;
    });

    private void ShowStatus(string message, bool problem)
    {
        StatusText.Text = message;
        if (problem) StatusText.Foreground = Brushes.OrangeRed;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    public void Dispose() => UploadService.Logged -= OnLogged;
}
