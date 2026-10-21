// RemoteFolders.cs - the three ways of reaching the website's folder: FTP, FTPS (FTP with
// encryption, "explicit TLS") and SFTP (file transfer over SSH). FTP and FTPS use the
// FluentFTP library, SFTP uses SSH.NET (both MIT licence).

using FluentFTP;
using Renci.SshNet;

namespace RFpix.Upload;

public static class RemoteFolders
{
    /// <summary>Logs in with the settings (and changes to the server folder, if one is set).</summary>
    public static IRemoteFolder Connect(UploadSettings s)
    {
        string password = Secrets.Reveal(s.ProtectedPassword);
        IRemoteFolder remote = s.Protocol == "SFTP" ? new Sftp(s, password) : new Ftp(s, password);
        return remote;
    }

    private sealed class Ftp : IRemoteFolder
    {
        private readonly FtpClient _client;

        public Ftp(UploadSettings s, string password)
        {
            _client = new FtpClient(s.Host, s.Username, password, s.Port);
            _client.Config.ConnectTimeout = 30000;
            _client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
            if (s.Protocol == "FTPS")
            {
                _client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
                // As Python's FTP_TLS (which the SSTV Auto Uploader uses): the connection is
                // encrypted, but the server's certificate isn't checked (many hosts use their own).
                _client.Config.ValidateAnyCertificate = true;
            }
            _client.Connect();
            if (!string.IsNullOrWhiteSpace(s.RemoteFolder)) _client.SetWorkingDirectory(s.RemoteFolder.Trim());
        }

        public string WorkingFolder() => _client.GetWorkingDirectory();
        public IReadOnlyList<string> List() =>
            _client.GetNameListing().Select(n => n.Replace('\\', '/').Split('/')[^1]).ToList();
        public void Put(string localPath, string remoteName)
        {
            var status = _client.UploadFile(localPath, remoteName, FtpRemoteExists.Overwrite);
            if (status == FtpStatus.Failed) throw new IOException("The server refused the upload.");
        }
        public long? Size(string remoteName) => _client.GetFileSize(remoteName) is long n && n >= 0 ? n : null;
        public void Delete(string remoteName) => _client.DeleteFile(remoteName);
        public void Rename(string from, string to) => _client.Rename(from, to);
        public void Dispose()
        {
            try { _client.Disconnect(); } catch (Exception) { }
            _client.Dispose();
        }
    }

    private sealed class Sftp : IRemoteFolder
    {
        private readonly SftpClient _client;

        public Sftp(UploadSettings s, string password)
        {
            _client = new SftpClient(s.Host, s.Port, s.Username, password);
            _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
            _client.Connect();
            if (!string.IsNullOrWhiteSpace(s.RemoteFolder)) _client.ChangeDirectory(s.RemoteFolder.Trim());
        }

        public string WorkingFolder() => _client.WorkingDirectory;
        public IReadOnlyList<string> List() =>
            _client.ListDirectory(".").Select(f => f.Name).Where(n => n is not "." and not "..").ToList();
        public void Put(string localPath, string remoteName)
        {
            using var file = File.OpenRead(localPath);
            _client.UploadFile(file, remoteName, canOverride: true);
        }
        public long? Size(string remoteName) => _client.GetAttributes(remoteName).Size;
        public void Delete(string remoteName) => _client.DeleteFile(remoteName);
        public void Rename(string from, string to)
        {
            try { _client.RenameFile(from, to, isPosix: true); }
            catch (Exception) { _client.RenameFile(from, to); }
        }
        public void Dispose()
        {
            try { _client.Disconnect(); } catch (Exception) { }
            _client.Dispose();
        }
    }
}
