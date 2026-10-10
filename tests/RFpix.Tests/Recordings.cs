// Recordings.cs - finds the test recordings in tests/recordings, wherever the tests run from.

namespace RFpix.Tests;

public static class Recordings
{
    /// <summary>Full path of the tests/recordings folder.</summary>
    public static string Folder { get; } = FindFolder();

    /// <summary>Full path of one recording, by file name.</summary>
    public static string Path(string fileName) => System.IO.Path.Combine(Folder, fileName);

    /// <summary>Every recording, for tests that check them all.</summary>
    public static IEnumerable<object[]> All() =>
        Directory.GetFiles(Folder, "*.wav").OrderBy(f => f)
                 .Select(f => new object[] { System.IO.Path.GetFileName(f) });

    // The tests run from deep inside a build folder, so walk up until we find
    // the folder holding RFpix.sln (the top of the repository).
    private static string FindFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "RFpix.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException("Couldn't find the RFpix repository folder (RFpix.sln).");
        return System.IO.Path.Combine(dir.FullName, "tests", "recordings");
    }
}
