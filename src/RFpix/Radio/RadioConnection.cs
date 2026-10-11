// RadioConnection.cs - talks to the radio through Hamlib: frequency, mode, data mode, PTT.
//
// Every call to the radio goes over a serial cable (or a network program) and can
// take up to a few seconds if the radio doesn't answer, so the screen calls these
// from a background task. A lock lets only one command reach the radio at a time.
//
// Safety: keying is always given a time limit. If nothing un-keys the radio in time
// (a fault, a crash in the sending code), a timer un-keys it. Closing the connection
// un-keys it too, and restores the mode if data mode was switched on for transmitting.

namespace RFpix.Radio;

/// <summary>Something went wrong talking to the radio; the message is for the operator.</summary>
public sealed class RadioException(string message) : Exception(message);

public sealed class RadioConnection : IDisposable
{
    private readonly object _lock = new();
    private readonly RadioSettings _settings;
    private IntPtr _rig;
    private Timer? _pttLimit;
    private ulong? _modeBeforeTransmit;   // set while data mode was switched on for transmitting

    /// <summary>True while RFpix is transmitting (keyed, or with VOX, between Key and Unkey).</summary>
    public bool IsKeyed { get; private set; }

    /// <summary>Raised (on a timer thread) if the time limit un-keyed the radio.</summary>
    public event Action? KeyedTooLong;

    private RadioConnection(RadioSettings settings, IntPtr rig)
    {
        _settings = settings;
        _rig = rig;
    }

    /// <summary>Opens the connection described by the settings. Throws <see cref="RadioException"/>.</summary>
    public static RadioConnection Open(RadioSettings settings)
    {
        var choice = RadioChoices.Find(settings.Choice);
        int model = choice.HamlibModel == -1 ? settings.OtherModel : choice.HamlibModel;
        if (model <= 0) throw new RadioException("No radio is chosen, so there's nothing to connect to.");
        bool pretend = model == DummyModel;   // Hamlib's pretend radio: no port needed
        if (choice.Connection != "none" && !pretend && string.IsNullOrWhiteSpace(settings.Port))
            throw new RadioException(choice.Connection == "network"
                ? "Type the address of the program sharing the radio, e.g. localhost:4532."
                : "Pick the radio's COM port.");

        IntPtr rig;
        try
        {
            HamlibNative.rig_set_debug(0);   // Hamlib's own messages: none
            rig = HamlibNative.rig_init((uint)model);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new RadioException("Hamlib, the radio-control library, couldn't be loaded. " +
                (OperatingSystem.IsWindows() ? "libhamlib-4.dll should be next to RFpix.exe: try rebuilding RFpix."
                                             : "Install your system's hamlib package.") + $" ({ex.Message})");
        }
        if (rig == IntPtr.Zero) throw new RadioException($"Hamlib doesn't know radio model {model}. Check the model number.");

        try
        {
            if (!pretend) Set(rig, "rig_pathname", settings.Port!.Trim());
            if (choice.Connection == "serial")
            {
                Set(rig, "serial_speed", settings.BaudRate.ToString());
                if (!string.IsNullOrWhiteSpace(settings.CivAddress))
                {
                    if (!int.TryParse(settings.CivAddress.Trim().TrimStart('0', 'x', 'X'), System.Globalization.NumberStyles.HexNumber, null, out int civ) || civ is < 1 or > 0xFF)
                        throw new RadioException($"\"{settings.CivAddress}\" isn't a CI-V address. It's a hex number such as A2.");
                    Set(rig, "civaddr", civ.ToString());   // Hamlib wants it as a plain number
                }
                Set(rig, "ptt_type", settings.Ptt switch
                {
                    PttMethod.Rts => "RTS",
                    PttMethod.Dtr => "DTR",
                    PttMethod.Vox => "None",
                    _ => "RIG",
                });
            }
            Check(HamlibNative.rig_open(rig), "Couldn't connect to the radio");
        }
        catch
        {
            HamlibNative.rig_cleanup(rig);
            throw;
        }
        return new RadioConnection(settings, rig);
    }

    /// <summary>Hamlib's "Dummy" radio (model 1): a pretend radio inside Hamlib that remembers
    /// what it's told. Handy for trying RFpix, and for the tests, without a real radio.</summary>
    public const int DummyModel = 1;

    /// <summary>The frequency the radio is on, in Hz.</summary>
    public double ReadFrequency()
    {
        lock (_lock)
        {
            Check(HamlibNative.rig_get_freq(Rig, HamlibNative.VfoCurrent, out double hz), "Couldn't read the frequency");
            return hz;
        }
    }

    public void SetFrequency(double hz)
    {
        lock (_lock) Check(HamlibNative.rig_set_freq(Rig, HamlibNative.VfoCurrent, hz), "Couldn't set the frequency");
    }

    /// <summary>The radio's mode as Hamlib numbers it (see <see cref="ModeName"/>).</summary>
    public ulong ReadMode()
    {
        lock (_lock)
        {
            Check(HamlibNative.rig_get_mode(Rig, HamlibNative.VfoCurrent, out ulong mode, out _), "Couldn't read the mode");
            return mode;
        }
    }

    public void SetMode(ulong mode)
    {
        lock (_lock)
            Check(HamlibNative.rig_set_mode(Rig, HamlibNative.VfoCurrent, mode, HamlibNative.PassbandNoChange), "Couldn't change the mode");
    }

    /// <summary>Keys the transmitter for at most <paramref name="limit"/>. With data mode on, switches
    /// the radio into data mode first (and <see cref="Unkey"/> switches it back). With VOX, sends no
    /// key command (the radio keys itself from the audio) but still switches data mode.</summary>
    public void Key(TimeSpan limit)
    {
        lock (_lock)
        {
            if (IsKeyed) return;

            if (_settings.DataModeForTransmit)
            {
                Check(HamlibNative.rig_get_mode(Rig, HamlibNative.VfoCurrent, out ulong mode, out _), "Couldn't read the mode");
                ulong data = DataModeFor(mode);
                if (data != mode)
                {
                    Check(HamlibNative.rig_set_mode(Rig, HamlibNative.VfoCurrent, data, HamlibNative.PassbandNoChange),
                          "Couldn't switch the radio to data mode");
                    _modeBeforeTransmit = mode;
                    Thread.Sleep(SettleTime);   // give the radio a moment before keying
                }
            }

            if (_settings.Ptt != PttMethod.Vox)
                Check(HamlibNative.rig_set_ptt(Rig, HamlibNative.VfoCurrent, HamlibNative.PttOn), "Couldn't key the transmitter");
            IsKeyed = true;
            _pttLimit = new Timer(_ => { if (UnkeyIfKeyed()) KeyedTooLong?.Invoke(); }, null, limit, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Un-keys the transmitter and, if data mode was switched on for transmitting, switches back.</summary>
    public void Unkey()
    {
        lock (_lock)
        {
            if (!IsKeyed) return;
            _pttLimit?.Dispose();
            _pttLimit = null;
            // Un-key (try twice before giving up). With VOX there's nothing to un-key.
            int result = 0;
            if (_settings.Ptt != PttMethod.Vox)
            {
                result = HamlibNative.rig_set_ptt(Rig, HamlibNative.VfoCurrent, HamlibNative.PttOff);
                if (result != 0) result = HamlibNative.rig_set_ptt(Rig, HamlibNative.VfoCurrent, HamlibNative.PttOff);
            }
            IsKeyed = result != 0;
            Check(result, "Couldn't un-key the transmitter. Un-key the radio by hand");

            if (_modeBeforeTransmit is ulong before)
            {
                _modeBeforeTransmit = null;
                Thread.Sleep(SettleTime);
                Check(HamlibNative.rig_set_mode(Rig, HamlibNative.VfoCurrent, before, HamlibNative.PassbandNoChange),
                      "Couldn't switch the radio back from data mode");
            }
        }
    }

    private bool UnkeyIfKeyed()
    {
        try
        {
            lock (_lock)
            {
                if (!IsKeyed || _rig == IntPtr.Zero) return false;
                Unkey();
                return true;
            }
        }
        catch (RadioException)
        {
            return true;
        }
    }

    /// <summary>Is the radio transmitting, by its own report?</summary>
    public bool ReadPtt()
    {
        lock (_lock)
        {
            Check(HamlibNative.rig_get_ptt(Rig, HamlibNative.VfoCurrent, out int ptt), "Couldn't read the transmit state");
            return ptt != HamlibNative.PttOff;
        }
    }

    /// <summary>How long to wait after a mode change before the next command.</summary>
    public static TimeSpan SettleTime { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The data version of a mode: USB → USB-D, LSB → LSB-D, FM → FM-D; others unchanged.</summary>
    public static ulong DataModeFor(ulong mode) => mode switch
    {
        HamlibNative.ModeUsb => HamlibNative.ModePktUsb,
        HamlibNative.ModeLsb => HamlibNative.ModePktLsb,
        HamlibNative.ModeFm => HamlibNative.ModePktFm,
        _ => mode,
    };

    /// <summary>14230000 → "14.230.000 MHz", the way radios show it.</summary>
    public static string FormatFrequency(double hz)
    {
        long h = (long)Math.Round(hz);
        return $"{h / 1_000_000}.{h / 1000 % 1000:000}.{h % 1000:000} MHz";
    }

    /// <summary>A mode's name as radios show it.</summary>
    public static string ModeName(ulong mode) => mode switch
    {
        1ul << 0 => "AM",
        1ul << 1 => "CW",
        HamlibNative.ModeUsb => "USB",
        HamlibNative.ModeLsb => "LSB",
        1ul << 4 => "RTTY",
        HamlibNative.ModeFm => "FM",
        HamlibNative.ModePktLsb => "LSB-D",
        HamlibNative.ModePktUsb => "USB-D",
        HamlibNative.ModePktFm => "FM-D",
        _ => "other",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            if (_rig == IntPtr.Zero) return;
            try { Unkey(); } catch (RadioException) { }
            HamlibNative.rig_close(_rig);
            HamlibNative.rig_cleanup(_rig);
            _rig = IntPtr.Zero;
        }
    }

    // ---- helpers ----

    private IntPtr Rig => _rig != IntPtr.Zero ? _rig : throw new RadioException("Not connected to the radio.");

    private static void Set(IntPtr rig, string name, string value)
    {
        var token = HamlibNative.rig_token_lookup(rig, name);
        if (token.Value == 0) return;   // this radio has no such setting (e.g. a network connection's baud rate)
        Check(HamlibNative.rig_set_conf(rig, token, value), $"Hamlib didn't accept {name} = {value}");
    }

    private static void Check(int result, string what)
    {
        if (result == 0) return;
        throw new RadioException($"{what}: {Explain(-Math.Abs(result))}");
    }

    /// <summary>Hamlib's error numbers, in plain words with what to check.</summary>
    public static string Explain(int code) => -code switch
    {
        2 => "the settings were refused. Check the COM port and baud rate.",
        5 => "the radio didn't answer. Check it's switched on, the COM port is right, the baud rate matches the radio's menu" +
             " and, for an Icom, the CI-V address.",
        6 => "the COM port couldn't be opened. Check it's the right port and that no other program (WSJT-X, flrig, QSSTV) is using it.",
        8 => "the radio's answer didn't make sense. Check the radio model and baud rate.",
        9 => "the radio refused the command. It may not allow it in its current mode.",
        11 => "this radio can't do that through Hamlib.",
        20 => "the radio is switched off.",
        22 => "the COM port is in use by another program. Close WSJT-X, flrig or QSSTV, or share the radio through rigctld.",
        _ => $"Hamlib error {-code}.",
    };
}
