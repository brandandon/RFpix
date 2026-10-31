// PulseNative.cs - the doorway into Linux's sound system, for PulseAudioInput and PulseAudioOutput.
//
// RFpix talks to PulseAudio (libpulse). Today's Linux desktops (Ubuntu, Mint, Fedora, Debian,
// Raspberry Pi OS) mostly run PipeWire, which answers PulseAudio's programs in exactly the same
// way, so this works with both. Two parts of libpulse are used:
//   - the "simple" API (libpulse-simple): one stream of sound, in or out, read or written in
//     chunks on a thread of RFpix's own;
//   - the full API (libpulse), only for what the simple one can't do: listing the sound devices,
//     and reading and setting a device's recording level.
// Devices are known by their PulseAudio name (e.g. "alsa_input.usb-Burr-Brown_from_TI_USB_Audio_CODEC
// -00.analog-stereo"), which stays the same from one start to the next; lists show their description.
//
// The structures' layouts are those of libpulse's headers on 64-bit Linux (PCs and Raspberry Pi).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RFpix.Audio.Pulse;

[SupportedOSPlatform("linux")]
internal static class PulseNative
{
    private const string Simple = "libpulse-simple.so.0";
    private const string Pulse = "libpulse.so.0";

    public const int SampleFloat32Le = 5;              // PA_SAMPLE_FLOAT32LE
    public const int StreamPlayback = 1, StreamRecord = 2;
    public const uint VolumeNorm = 0x10000;             // PA_VOLUME_NORM: 100%
    private const int ContextReady = 4, ContextFailed = 5, ContextTerminated = 6;
    private const int OperationRunning = 0;
    private const uint InvalidIndex = uint.MaxValue;

    // pa_source_info and pa_sink_info: where the fields RFpix reads are. These positions are for 64-bit
    // programs (checked against PulseAudio's headers), which is all RFpix is built as; a 32-bit build
    // would need its own, so Need64Bit stops one reading the wrong places.
    private const int InfoName = 0, InfoDescription = 16, InfoVolume = 172, SourceMonitorOf = 308;

    private static void Need64Bit()
    {
        if (!Environment.Is64BitProcess)
            throw new AudioException("RFpix's Linux sound code is for 64-bit computers (PCs, and the Raspberry Pi with 64-bit Raspberry Pi OS).");
    }
    private const int CVolumeSize = 132, CVolumeValues = 4, ChannelsMax = 32;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }

    // ---- the simple API ----

    [DllImport(Simple)]
    public static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device, string streamName,
                                              ref SampleSpec spec, IntPtr channelMap, ref BufferAttr attr, out int error);

    [DllImport(Simple)] public static extern int pa_simple_read(IntPtr s, float[] data, UIntPtr bytes, out int error);
    [DllImport(Simple)] public static extern int pa_simple_write(IntPtr s, float[] data, UIntPtr bytes, out int error);
    [DllImport(Simple)] public static extern ulong pa_simple_get_latency(IntPtr s, out int error);
    [DllImport(Simple)] public static extern int pa_simple_flush(IntPtr s, out int error);
    [DllImport(Simple)] public static extern int pa_simple_drain(IntPtr s, out int error);
    [DllImport(Simple)] public static extern void pa_simple_free(IntPtr s);

    [DllImport(Pulse)] private static extern IntPtr pa_strerror(int error);

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}";

    // ---- the full API (a mainloop on a thread of its own, and a connection to the server) ----

    [DllImport(Pulse)] private static extern IntPtr pa_threaded_mainloop_new();
    [DllImport(Pulse)] private static extern int pa_threaded_mainloop_start(IntPtr m);
    [DllImport(Pulse)] private static extern void pa_threaded_mainloop_stop(IntPtr m);
    [DllImport(Pulse)] private static extern void pa_threaded_mainloop_free(IntPtr m);
    [DllImport(Pulse)] private static extern void pa_threaded_mainloop_lock(IntPtr m);
    [DllImport(Pulse)] private static extern void pa_threaded_mainloop_unlock(IntPtr m);
    [DllImport(Pulse)] private static extern IntPtr pa_threaded_mainloop_get_api(IntPtr m);
    [DllImport(Pulse)] private static extern IntPtr pa_context_new(IntPtr api, string name);
    [DllImport(Pulse)] private static extern int pa_context_connect(IntPtr c, string? server, int flags, IntPtr api);
    [DllImport(Pulse)] private static extern int pa_context_get_state(IntPtr c);
    [DllImport(Pulse)] private static extern void pa_context_disconnect(IntPtr c);
    [DllImport(Pulse)] private static extern void pa_context_unref(IntPtr c);
    [DllImport(Pulse)] private static extern int pa_operation_get_state(IntPtr o);
    [DllImport(Pulse)] private static extern void pa_operation_unref(IntPtr o);
    [DllImport(Pulse)] private static extern void pa_operation_cancel(IntPtr o);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InfoCallback(IntPtr context, IntPtr info, int eol, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SuccessCallback(IntPtr context, int success, IntPtr userData);

    [DllImport(Pulse)] private static extern IntPtr pa_context_get_source_info_list(IntPtr c, InfoCallback cb, IntPtr userData);
    [DllImport(Pulse)] private static extern IntPtr pa_context_get_sink_info_list(IntPtr c, InfoCallback cb, IntPtr userData);
    [DllImport(Pulse)] private static extern IntPtr pa_context_get_source_info_by_name(IntPtr c, string name, InfoCallback cb, IntPtr userData);
    [DllImport(Pulse)] private static extern IntPtr pa_context_set_source_volume_by_name(IntPtr c, string name, byte[] volume, SuccessCallback? cb, IntPtr userData);

    /// <summary>A short-lived connection to the sound server, for listing and levels.</summary>
    private sealed class Connection : IDisposable
    {
        public readonly IntPtr Loop, Context;

        public Connection()
        {
            Loop = pa_threaded_mainloop_new();
            if (Loop == IntPtr.Zero) throw new AudioException("Linux's sound system couldn't be reached.");
            Context = pa_context_new(pa_threaded_mainloop_get_api(Loop), "RFpix");
            if (Context == IntPtr.Zero || pa_context_connect(Context, null, 0, IntPtr.Zero) < 0 || pa_threaded_mainloop_start(Loop) < 0)
            {
                Dispose();
                throw new AudioException("Linux's sound system (PulseAudio or PipeWire) isn't running, so the sound devices can't be listed.");
            }
            var until = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                int state = Locked(() => pa_context_get_state(Context));
                if (state == ContextReady) break;
                if (state is ContextFailed or ContextTerminated || DateTime.UtcNow > until)
                {
                    Dispose();
                    throw new AudioException("Linux's sound system (PulseAudio or PipeWire) didn't answer, so the sound devices can't be listed.");
                }
                Thread.Sleep(10);
            }
        }

        public T Locked<T>(Func<T> action)
        {
            pa_threaded_mainloop_lock(Loop);
            try { return action(); }
            finally { pa_threaded_mainloop_unlock(Loop); }
        }

        /// <summary>Starts an operation (holding the lock) and waits for it to finish.</summary>
        public void Run(Func<IntPtr> start)
        {
            IntPtr op = Locked(start);
            if (op == IntPtr.Zero) throw new AudioException("Linux's sound system refused the request.");
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Locked(() => pa_operation_get_state(op)) == OperationRunning && DateTime.UtcNow < until) Thread.Sleep(5);
            Locked(() =>
            {
                // Not finished in time: cancelled, so its callback can't run after RFpix has moved on.
                if (pa_operation_get_state(op) == OperationRunning) pa_operation_cancel(op);
                pa_operation_unref(op);
                return 0;
            });
        }

        public void Dispose()
        {
            if (Loop != IntPtr.Zero) pa_threaded_mainloop_stop(Loop);
            if (Context != IntPtr.Zero) { pa_context_disconnect(Context); pa_context_unref(Context); }
            if (Loop != IntPtr.Zero) pa_threaded_mainloop_free(Loop);
        }
    }

    private static string Text(IntPtr info, int offset) => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info, offset)) ?? "";

    /// <summary>The recording devices (not the "monitor of" ones), or the playback devices.</summary>
    public static List<AudioDeviceInfo> ListDevices(bool input)
    {
        Need64Bit();
        var devices = new List<AudioDeviceInfo>();
        InfoCallback callback = (_, info, eol, _) =>
        {
            if (eol != 0 || info == IntPtr.Zero) return;
            if (input && (uint)Marshal.ReadInt32(info, SourceMonitorOf) != InvalidIndex) return;   // a monitor of an output
            string name = Text(info, InfoName), description = Text(info, InfoDescription);
            if (name.Length > 0) lock (devices) devices.Add(new AudioDeviceInfo(name, description.Length > 0 ? description : name));
        };
        using var connection = new Connection();
        connection.Run(() => input ? pa_context_get_source_info_list(connection.Context, callback, IntPtr.Zero)
                                   : pa_context_get_sink_info_list(connection.Context, callback, IntPtr.Zero));
        GC.KeepAlive(callback);
        lock (devices) return devices.ToList();
    }

    /// <summary>A recording device's level, 0 to 1 (the loudest of its channels), or null if it isn't there.</summary>
    public static double? SourceVolume(string name)
    {
        Need64Bit();
        double? level = null;
        InfoCallback callback = (_, info, eol, _) =>
        {
            if (eol != 0 || info == IntPtr.Zero) return;
            int channels = Math.Clamp((int)Marshal.ReadByte(info, InfoVolume), 0, ChannelsMax);
            uint loudest = 0;
            for (int i = 0; i < channels; i++) loudest = Math.Max(loudest, (uint)Marshal.ReadInt32(info, InfoVolume + CVolumeValues + 4 * i));
            if (channels > 0) level = (double)loudest / VolumeNorm;
        };
        using var connection = new Connection();
        connection.Run(() => pa_context_get_source_info_by_name(connection.Context, name, callback, IntPtr.Zero));
        GC.KeepAlive(callback);
        return level;
    }

    /// <summary>Sets a recording device's level (all its channels) to 0 to 1.</summary>
    public static bool SetSourceVolume(string name, double level)
    {
        Need64Bit();
        // Its channel count first, as the level is set channel by channel.
        int channels = 0;
        InfoCallback callback = (_, info, eol, _) =>
        {
            if (eol == 0 && info != IntPtr.Zero) channels = Math.Clamp((int)Marshal.ReadByte(info, InfoVolume), 0, ChannelsMax);
        };
        using var connection = new Connection();
        connection.Run(() => pa_context_get_source_info_by_name(connection.Context, name, callback, IntPtr.Zero));
        GC.KeepAlive(callback);
        if (channels == 0) return false;

        var volume = new byte[CVolumeSize];
        volume[0] = (byte)channels;
        uint value = (uint)Math.Round(Math.Clamp(level, 0, 1) * VolumeNorm);
        for (int i = 0; i < channels; i++) BitConverter.TryWriteBytes(volume.AsSpan(CVolumeValues + 4 * i), value);
        int ok = 0;
        SuccessCallback done = (_, success, _) => ok = success;
        connection.Run(() => pa_context_set_source_volume_by_name(connection.Context, name, volume, done, IntPtr.Zero));
        GC.KeepAlive(done);
        return ok != 0;
    }
}
