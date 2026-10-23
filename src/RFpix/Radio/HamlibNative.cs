// HamlibNative.cs - the doorway into Hamlib, the radio-control library.
//
// Hamlib is written in C, so C# calls into it through "P/Invoke": each function
// below is declared with the same inputs and outputs as in Hamlib's rig.h, and
// .NET finds it in the library file at run time.
//   Windows: libhamlib-4.dll (with libgcc_s_seh-1.dll, libwinpthread-1.dll and
//            libusb-1.0.dll), downloaded by the build and placed next to RFpix.exe
//   Linux:   libhamlib.so.4 (the hamlib package)
//   Mac:     libhamlib.4.dylib, carried inside RFpix.app (Contents/MacOS, next to RFpix);
//            Homebrew's hamlib is used if it isn't there (as when running from source)
//
// RFpix only ever holds a pointer to Hamlib's RIG record and never looks inside
// it, so changes to that record between Hamlib versions can't upset RFpix.
//
// One trap: C's "long" is 4 bytes on Windows but 8 on Linux and Mac. Hamlib uses
// it for config tokens and passband widths, so those are declared as CLong, which
// .NET sizes correctly on each system.

using System.Reflection;
using System.Runtime.InteropServices;

namespace RFpix.Radio;

internal static class HamlibNative
{
    private const string Library = "hamlib";

    // ---- values from Hamlib's rig.h (4.x) ----

    public const uint VfoCurrent = 1u << 29;   // RIG_VFO_CURR

    public const ulong ModeUsb = 1ul << 2, ModeLsb = 1ul << 3, ModeFm = 1ul << 5;
    public const ulong ModePktLsb = 1ul << 10, ModePktUsb = 1ul << 11, ModePktFm = 1ul << 12;

    public const int PttOff = 0, PttOn = 1;

    public static readonly CLong PassbandNoChange = new(-1);   // RIG_PASSBAND_NOCHANGE

    // ---- functions ----

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void rig_set_debug(int level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rig_init(uint model);

    // The list of every radio Hamlib knows (HamlibModels.cs).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_load_all_backends();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int RigListCallback(IntPtr caps, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_list_foreach(RigListCallback callback, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern CLong rig_token_lookup(IntPtr rig, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_set_conf(IntPtr rig, CLong token, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_open(IntPtr rig);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_close(IntPtr rig);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_cleanup(IntPtr rig);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_get_freq(IntPtr rig, uint vfo, out double freq);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_set_freq(IntPtr rig, uint vfo, double freq);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_get_mode(IntPtr rig, uint vfo, out ulong mode, out CLong width);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_set_mode(IntPtr rig, uint vfo, ulong mode, CLong width);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_set_ptt(IntPtr rig, uint vfo, int ptt);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_get_ptt(IntPtr rig, uint vfo, out int ptt);

    // Sends bytes straight to the radio over Hamlib's open connection and reads one answer
    // (for Icom radios, one CI-V message ending in FD). Returns how many bytes were read, or
    // a negative error. Used for the IC-9700's data mode (IcomCiv.cs).
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_send_raw(IntPtr rig, byte[] send, int sendLength, byte[] reply, int replyLength, byte[] terminator);

    // ---- finding the library file ----

    static HamlibNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(HamlibNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;   // not ours: the normal search

        string[] candidates =
            OperatingSystem.IsWindows() ? new[] { Path.Combine(AppContext.BaseDirectory, "libhamlib-4.dll"), "libhamlib-4.dll" }
            : OperatingSystem.IsMacOS() ? new[] { Path.Combine(AppContext.BaseDirectory, "libhamlib.4.dylib"), "libhamlib.4.dylib", "/opt/homebrew/lib/libhamlib.4.dylib", "/usr/local/lib/libhamlib.4.dylib" }
            : new[] { "libhamlib.so.4", "libhamlib.so" };
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;   // .NET then reports the library as missing (DllNotFoundException)
    }
}
