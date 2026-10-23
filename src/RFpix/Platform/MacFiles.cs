// MacFiles.cs - two things the Gallery does with files on the Mac:
//   - putting a picture in the Trash, through macOS's own file manager (NSFileManager), so
//     it can be put back from the Trash like any other file;
//   - showing a picture in Finder, with it selected ("open -R", as Finder's own Reveal).
// macOS's file manager is an Objective-C object; it's reached through objc_msgSend, the one
// function every Objective-C call goes through.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RFpix.Platform;

[SupportedOSPlatform("macos")]
public static class MacFiles
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte Send(IntPtr target, IntPtr selector, IntPtr a, IntPtr b, out IntPtr error);

    /// <summary>Moves a file to the Trash. Throws IOException (with macOS's reason) if it can't.</summary>
    public static void MoveToTrash(string path)
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(path));
        try
        {
            IntPtr text = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
            IntPtr url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), text);
            IntPtr manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
            if (Send(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out IntPtr error) != 0) return;
            string reason = "macOS didn't say why";
            if (error != IntPtr.Zero)
            {
                IntPtr description = Send(error, sel_registerName("localizedDescription"));
                IntPtr chars = description == IntPtr.Zero ? IntPtr.Zero : Send(description, sel_registerName("UTF8String"));
                if (chars != IntPtr.Zero) reason = Marshal.PtrToStringUTF8(chars) ?? reason;
            }
            throw new IOException($"It couldn't be moved to the Trash: {reason}");
        }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    /// <summary>Opens a Finder window with the file selected.</summary>
    public static void ShowInFinder(string path)
    {
        var start = new ProcessStartInfo("open") { UseShellExecute = false };
        start.ArgumentList.Add("-R");
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }
}
