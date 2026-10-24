// CoreAudioNative.cs - the doorway into the Mac's own sound system (Core Audio), for
// CoreAudioInput and CoreAudioOutput.
//
// Two parts of it are used, both always present on a Mac (nothing to install):
//   - the "HAL" (AudioObject... functions in CoreAudio.framework): lists the sound devices,
//     with their names, permanent IDs ("UIDs") and how many input and output channels each has;
//   - Audio Queues (AudioQueue... in AudioToolbox.framework): records from and plays to one
//     device, in the format RFpix asks for (two channels of 32-bit floating point at 48 kHz).
//     macOS converts from the device's own format and rate, so any radio's USB sound works.
// Text comes back from macOS as CFStrings (CoreFoundation), which are turned into .NET strings here.
//
// The structures below are laid out exactly as in Apple's headers (64-bit).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RFpix.Audio.CoreAudio;

[SupportedOSPlatform("macos")]
internal static class CoreAudioNative
{
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // Four-character codes, as Apple writes them ('dev#' and so on).
    public static uint Code(string four) => (uint)(four[0] << 24 | four[1] << 16 | four[2] << 8 | four[3]);

    public const uint SystemObject = 1;   // kAudioObjectSystemObject
    public static readonly uint Devices = Code("dev#");          // kAudioHardwarePropertyDevices
    public static readonly uint Name = Code("lnam");             // kAudioObjectPropertyName
    public static readonly uint DeviceUid = Code("uid ");        // kAudioDevicePropertyDeviceUID
    public static readonly uint StreamConfiguration = Code("slay");
    public static readonly uint NominalSampleRate = Code("nsrt");
    public static readonly uint TranslateUidToDevice = Code("uidd");   // kAudioHardwarePropertyTranslateUIDToDevice
    public static readonly uint VolumeScalar = Code("volm");           // kAudioDevicePropertyVolumeScalar
    public static readonly uint ScopeGlobal = Code("glob"), ScopeInput = Code("inpt"), ScopeOutput = Code("outp");
    public const uint ElementMain = 0;

    public static readonly uint FormatLinearPcm = Code("lpcm");
    public const uint FlagIsFloat = 1, FlagIsPacked = 8;

    public static readonly uint QueueCurrentDevice = Code("aqcd");   // kAudioQueueProperty_CurrentDevice
    public static readonly uint QueueIsRunning = Code("aqrn");       // kAudioQueueProperty_IsRunning

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyAddress
    {
        public uint Selector, Scope, Element;
        public PropertyAddress(uint selector, uint scope, uint element = ElementMain) { Selector = selector; Scope = scope; Element = element; }
    }

    /// <summary>AudioStreamBasicDescription: what the sound looks like.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamFormat
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;

        /// <summary>32-bit floating point at the rate given, channels interleaved.</summary>
        public static StreamFormat Float(double rate, uint channels) => new()
        {
            SampleRate = rate, FormatId = FormatLinearPcm, FormatFlags = FlagIsFloat | FlagIsPacked,
            BytesPerPacket = 4 * channels, FramesPerPacket = 1, BytesPerFrame = 4 * channels, ChannelsPerFrame = channels, BitsPerChannel = 32,
        };
    }

    /// <summary>AudioTimeStamp (only the sample time is used).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
        public SmpteTime Smpte;
        public uint Flags, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SmpteTime
    {
        public short Subframes, SubframeDivisor;
        public uint Counter, Type, Flags;
        public short Hours, Minutes, Seconds, Frames;
    }

    // AudioQueueBuffer: the fields RFpix uses, by their place in the record.
    public const int BufferCapacityOffset = 0, BufferDataOffset = 8, BufferByteSizeOffset = 16;

    // ---- the HAL ----

    [DllImport(CoreAudioLib)]
    public static extern int AudioObjectGetPropertyDataSize(uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint size);

    [DllImport(CoreAudioLib)]
    public static extern int AudioObjectGetPropertyData(uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, IntPtr data);

    [DllImport(CoreAudioLib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AudioObjectHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(CoreAudioLib)]
    public static extern int AudioObjectIsPropertySettable(uint objectId, ref PropertyAddress address, out byte settable);

    [DllImport(CoreAudioLib)]
    public static extern int AudioObjectSetPropertyData(uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint size, ref float data);

    // ---- Audio Queues ----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InputCallback(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr packetDescriptions);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueNewInput(ref StreamFormat format, InputCallback callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueNewOutput(ref StreamFormat format, OutputCallback callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptions, IntPtr descriptions);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueSetProperty(IntPtr queue, uint property, ref IntPtr data, uint size);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueGetProperty(IntPtr queue, uint property, out uint data, ref uint size);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.U1)] bool immediate);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueFlush(IntPtr queue);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.U1)] bool immediate);

    [DllImport(AudioToolboxLib)]
    public static extern int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, out TimeStamp time, out byte discontinuity);

    // ---- CoreFoundation strings ----

    [StructLayout(LayoutKind.Sequential)]
    public struct CFRange { public nint Location, Length; }

    [DllImport(CoreFoundationLib)]
    public static extern nint CFStringGetLength(IntPtr str);

    [DllImport(CoreFoundationLib)]
    public static extern void CFStringGetCharacters(IntPtr str, CFRange range, [Out] char[] buffer);

    [DllImport(CoreFoundationLib, CharSet = CharSet.Unicode)]
    public static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string chars, nint length);

    [DllImport(CoreFoundationLib)]
    public static extern void CFRelease(IntPtr obj);

    public static string FromCFString(IntPtr str)
    {
        if (str == IntPtr.Zero) return "";
        nint length = CFStringGetLength(str);
        var chars = new char[length];
        CFStringGetCharacters(str, new CFRange { Location = 0, Length = length }, chars);
        return new string(chars);
    }

    public static void Check(int status, string what)
    {
        if (status != 0) throw new AudioException($"The Mac's sound system couldn't {what} (error {status}).");
    }

    // ---- the devices ----

    /// <summary>The sound devices with input (or output) channels: their UIDs and names.</summary>
    public static List<AudioDeviceInfo> ListDevices(bool input)
    {
        var address = new PropertyAddress(Devices, ScopeGlobal);
        Check(AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out uint size), "list the sound devices");
        int count = (int)(size / 4);
        var ids = new uint[count];
        var handle = GCHandle.Alloc(ids, GCHandleType.Pinned);
        try
        {
            Check(AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, handle.AddrOfPinnedObject()), "list the sound devices");
        }
        finally { handle.Free(); }

        var devices = new List<AudioDeviceInfo>();
        foreach (uint id in ids.Take((int)(size / 4)))
        {
            if (Channels(id, input) == 0) continue;
            string uid = StringProperty(id, DeviceUid);
            if (uid.Length == 0) continue;
            string name = StringProperty(id, Name);
            devices.Add(new AudioDeviceInfo(uid, name.Length > 0 ? name : uid));
        }
        return devices;
    }

    private static string StringProperty(uint device, uint selector)
    {
        var address = new PropertyAddress(selector, ScopeGlobal);
        uint size = (uint)IntPtr.Size;
        IntPtr value = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (AudioObjectGetPropertyData(device, ref address, 0, IntPtr.Zero, ref size, value) != 0) return "";
            IntPtr cf = Marshal.ReadIntPtr(value);
            try { return FromCFString(cf); }
            finally { if (cf != IntPtr.Zero) CFRelease(cf); }
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    /// <summary>How many input (or output) channels a device has.</summary>
    public static int Channels(uint device, bool input)
    {
        var address = new PropertyAddress(StreamConfiguration, input ? ScopeInput : ScopeOutput);
        if (AudioObjectGetPropertyDataSize(device, ref address, 0, IntPtr.Zero, out uint size) != 0 || size == 0) return 0;
        IntPtr list = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(device, ref address, 0, IntPtr.Zero, ref size, list) != 0) return 0;
            // AudioBufferList: a count, then that many AudioBuffers (channels, byte size, data
            // pointer: 16 bytes each), starting 8 bytes in.
            int buffers = Marshal.ReadInt32(list);
            int channels = 0;
            for (int i = 0; i < buffers; i++) channels += Marshal.ReadInt32(list, 8 + 16 * i);
            return channels;
        }
        finally { Marshal.FreeHGlobal(list); }
    }

    // ---- a device's input volume (the radio's recording level) ----

    /// <summary>The device with this UID (0 if it isn't connected).</summary>
    public static uint DeviceFromUid(string uid)
    {
        IntPtr cf = CFStringCreateWithCharacters(IntPtr.Zero, uid, uid.Length);
        IntPtr qualifier = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr result = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteIntPtr(qualifier, cf);
            Marshal.WriteInt32(result, 0);
            var address = new PropertyAddress(TranslateUidToDevice, ScopeGlobal);
            uint size = 4;
            return AudioObjectGetPropertyData(SystemObject, ref address, (uint)IntPtr.Size, qualifier, ref size, result) == 0
                ? (uint)Marshal.ReadInt32(result) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(result);
            Marshal.FreeHGlobal(qualifier);
            CFRelease(cf);
        }
    }

    /// <summary>Where a device's input volume can be set: its main control, or else each channel's own.</summary>
    private static List<PropertyAddress> VolumeControls(uint device)
    {
        var controls = new List<PropertyAddress>();
        var main = new PropertyAddress(VolumeScalar, ScopeInput);
        if (Settable(device, main)) { controls.Add(main); return controls; }
        int channels = Channels(device, input: true);
        for (uint channel = 1; channel <= channels; channel++)
        {
            var one = new PropertyAddress(VolumeScalar, ScopeInput, channel);
            if (Settable(device, one)) controls.Add(one);
        }
        return controls;
    }

    private static bool Settable(uint device, PropertyAddress address) =>
        AudioObjectHasProperty(device, ref address) && AudioObjectIsPropertySettable(device, ref address, out byte settable) == 0 && settable != 0;

    /// <summary>The device's input volume, 0 to 1 (null if it has none).</summary>
    public static double? InputVolume(string uid)
    {
        uint device = DeviceFromUid(uid);
        if (device == 0) return null;
        var controls = VolumeControls(device);
        if (controls.Count == 0) return null;
        var address = controls[0];
        IntPtr value = Marshal.AllocHGlobal(4);
        try
        {
            uint size = 4;
            if (AudioObjectGetPropertyData(device, ref address, 0, IntPtr.Zero, ref size, value) != 0) return null;
            return Math.Clamp(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value)), 0f, 1f);
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    /// <summary>Sets the device's input volume (every channel's, if it has no main control).</summary>
    public static bool SetInputVolume(string uid, double level)
    {
        uint device = DeviceFromUid(uid);
        if (device == 0) return false;
        var controls = VolumeControls(device);
        float volume = (float)Math.Clamp(level, 0, 1);
        bool done = controls.Count > 0;
        foreach (var control in controls)
        {
            var address = control;
            done &= AudioObjectSetPropertyData(device, ref address, 0, IntPtr.Zero, 4, ref volume) == 0;
        }
        return done;
    }

    /// <summary>Points a queue at the device with this UID.</summary>
    public static void UseDevice(IntPtr queue, string uid)
    {
        IntPtr cf = CFStringCreateWithCharacters(IntPtr.Zero, uid, uid.Length);
        try { Check(AudioQueueSetProperty(queue, QueueCurrentDevice, ref cf, (uint)IntPtr.Size), "use that sound device"); }
        finally { CFRelease(cf); }
    }
}
