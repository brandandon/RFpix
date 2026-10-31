// MacMicrophone.cs - asking macOS whether RFpix may listen to a sound input.
//
// macOS treats every sound input as a microphone (the radio's included), and only lets a program
// listen once you've said yes. RFpix asks through AVFoundation's AVCaptureDevice: the first time,
// macOS shows "RFpix would like to access the microphone" with Allow and Don't Allow, and RFpix
// then appears in System Settings > Privacy & Security > Microphone, where it can be turned on
// or off later.
//
// The question's answer comes back through an Objective-C "block" (a function with a little
// record in front of it); a block that needs nothing from around it is made here by hand, once.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RFpix.Audio.CoreAudio;

[SupportedOSPlatform("macos")]
internal static class MacMicrophone
{
    public enum Access { NotAskedYet = 0, Restricted = 1, Denied = 2, Allowed = 3 }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendForInteger(IntPtr target, IntPtr selector, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Send(IntPtr target, IntPtr selector, IntPtr a, IntPtr b);

    private static IntPtr AudioMediaType()
    {
        IntPtr library = NativeLibrary.Load(AVFoundation);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "AVMediaTypeAudio"));   // an NSString
    }

    /// <summary>What macOS says now (without asking).</summary>
    public static Access Status()
    {
        IntPtr device = objc_getClass("AVCaptureDevice");
        return (Access)(int)SendForInteger(device, sel_registerName("authorizationStatusForMediaType:"), AudioMediaType());
    }

    // ---- asking ----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Answered(IntPtr block, byte granted);

    // The answer goes to MicrophoneAccess.Answered (on a thread of macOS's): the Receive tab starts
    // listening by itself after Allow. Nothing may escape back into macOS from here.
    private static readonly Answered OnAnswered = (_, granted) =>
    {
        try { MicrophoneAccess.RaiseAnswered(granted != 0); }
        catch (Exception) { }
    };
    private static IntPtr _block;   // made once and kept for good (macOS may hold on to it)

    /// <summary>Asks the question (macOS shows it only if it hasn't been answered before).</summary>
    public static void Ask()
    {
        if (_block == IntPtr.Zero)
        {
            // A global block: isa, flags (BLOCK_IS_GLOBAL), reserved, the function, its descriptor.
            IntPtr descriptor = Marshal.AllocHGlobal(16);
            Marshal.WriteInt64(descriptor, 0, 0);     // reserved
            Marshal.WriteInt64(descriptor, 8, 32);    // the block record's size
            IntPtr block = Marshal.AllocHGlobal(32);
            IntPtr system = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
            Marshal.WriteIntPtr(block, 0, NativeLibrary.GetExport(system, "_NSConcreteGlobalBlock"));
            Marshal.WriteInt32(block, 8, 1 << 28);
            Marshal.WriteInt32(block, 12, 0);
            Marshal.WriteIntPtr(block, 16, Marshal.GetFunctionPointerForDelegate(OnAnswered));
            Marshal.WriteIntPtr(block, 24, descriptor);
            _block = block;
        }
        Send(objc_getClass("AVCaptureDevice"), sel_registerName("requestAccessForMediaType:completionHandler:"), AudioMediaType(), _block);
    }
}
