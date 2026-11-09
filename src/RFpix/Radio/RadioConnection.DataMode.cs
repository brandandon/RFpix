// RadioConnection.DataMode.cs - part of RadioConnection (RadioConnection.cs): switching the radio to data mode for transmitting,
// and back: the IC-9700 by Icom's own commands, the FT-991A by Hamlib's data modes, and radios
// shared through Ham Radio Deluxe by HRD's data-mode button.

using RFpix.Diagnostics;

namespace RFpix.Radio;

public sealed partial class RadioConnection
{
    /// <summary>How long to wait after a mode change before the next command.</summary>
    public static TimeSpan SettleTime { get; set; } = TimeSpan.FromMilliseconds(250);

    // ---- IC-9700 data mode, by Icom's own commands ----

    private IcomDataMode DataMode => new(IcomCiv.Address(_settings.CivAddress), Exchange, SettleTime);

    private void DataModeOn()
    {
        if (_hrd is { } hrd) { HrdDataModeOn(hrd); return; }
        if (_settings.Choice != "ic9700") { HamlibDataModeOn(); return; }
        var (mode, report) = DataMode.On();
        LastDataModeReport = report;
        AppLog.Info("Radio", $"Data mode: {report}");
        if (mode is null) return;
        _modeBeforeTransmit = mode;
        DataModeSwitched?.Invoke($"IC-9700 in {mode}-D");
    }

    private void DataModeOff()
    {
        string mode = _modeBeforeTransmit!;
        if (_hrd is { } hrd) hrd.SetDataMode(false);
        else if (_hamlibModeBefore is ulong before)
        {
            if (SettleTime > TimeSpan.Zero) Thread.Sleep(SettleTime);
            Check(HamlibNative.rig_set_mode(Rig, HamlibNative.VfoCurrent, before, HamlibNative.PassbandNoChange),
                  "Couldn't switch the radio back from data mode");
            _hamlibModeBefore = null;
        }
        else DataMode.Off(mode);
        _modeBeforeTransmit = null;   // only once it's back (if it failed, it's tried again later)
        LastDataModeReport += $" → {mode}";
        AppLog.Info("Radio", $"Data mode: {RadioLabel} back to {mode}.");
        DataModeSwitched?.Invoke($"{RadioLabel} back to {mode}");
    }

    /// <summary>The radio's name for the data-mode messages.</summary>
    private string RadioLabel => _hrd?.RadioName ?? (_settings.Choice == "ft991a" ? "FT-991A" : _settings.Choice == "ic9700" ? "IC-9700" : "The radio");

    // ---- data mode through Hamlib (the FT-991A): FM → DATA-FM, USB → DATA-USB, LSB → DATA-LSB ----

    private ulong? _hamlibModeBefore;   // the mode to put back, while data mode is on

    /// <summary>Hamlib's data mode for each ordinary mode.</summary>
    internal static ulong? DataModeFor(ulong mode) => mode switch
    {
        HamlibNative.ModeFm => HamlibNative.ModePktFm,
        HamlibNative.ModeUsb => HamlibNative.ModePktUsb,
        HamlibNative.ModeLsb => HamlibNative.ModePktLsb,
        _ => null,
    };

    private void HamlibDataModeOn()
    {
        Check(HamlibNative.rig_get_mode(Rig, HamlibNative.VfoCurrent, out ulong mode, out _), "Couldn't read the mode");
        string name = ModeName(mode);
        if (mode is HamlibNative.ModePktFm or HamlibNative.ModePktUsb or HamlibNative.ModePktLsb)
        {
            LastDataModeReport = $"{name} already, left as it is";
            AppLog.Info("Radio", $"Data mode: {LastDataModeReport}.");
            return;
        }
        if (DataModeFor(mode) is not ulong data)
        {
            LastDataModeReport = $"no data mode for {name}: left as it is";
            AppLog.Info("Radio", $"Data mode: {LastDataModeReport}.");
            return;
        }
        Check(HamlibNative.rig_set_mode(Rig, HamlibNative.VfoCurrent, data, HamlibNative.PassbandNoChange),
              "Couldn't switch the radio to data mode");
        if (SettleTime > TimeSpan.Zero) Thread.Sleep(SettleTime);
        _hamlibModeBefore = mode;
        _modeBeforeTransmit = name;
        LastDataModeReport = $"{name} → {ModeName(data)}";
        AppLog.Info("Radio", $"Data mode: {RadioLabel} {LastDataModeReport}.");
        DataModeSwitched?.Invoke($"{RadioLabel} in {ModeName(data)}");
    }

    // Through HRD: its data-mode button (or list) on, checked, and the mode noted to put back after.
    private void HrdDataModeOn(HrdClient hrd)
    {
        if (!hrd.CanSwitchDataMode)
        {
            LastDataModeReport = $"HRD has no data mode switch for the {hrd.RadioName}";
            return;
        }
        string mode = hrd.ReadModeText();
        if (mode.Length == 0) mode = "its mode";
        if (hrd.DataModeOn())
        {
            LastDataModeReport = $"{mode}-D already, left as it is";
            return;
        }
        hrd.SetDataMode(true);
        Thread.Sleep(SettleTime);
        if (!hrd.DataModeOn())
            throw new RadioException($"The {hrd.RadioName} didn't switch to data mode through Ham Radio Deluxe. Switch it by hand, or untick data mode.");
        _modeBeforeTransmit = mode;
        LastDataModeReport = $"{mode} → {mode}-D";
        AppLog.Info("Radio", $"Data mode: {hrd.RadioName} {LastDataModeReport} (through Ham Radio Deluxe).");
        DataModeSwitched?.Invoke($"{hrd.RadioName} in {mode}-D");
    }

    /// <summary>A mode HRD names ("USB", "FM-N") as Hamlib numbers it, with data mode on or off.</summary>
    public static ulong ModeFromName(string name, bool data)
    {
        string n = name.Trim().ToUpperInvariant();
        if (n.EndsWith("-D")) { data = true; n = n[..^2]; }
        if (n.StartsWith("PKT") || n.StartsWith("DATA")) data = true;
        return n switch
        {
            _ when n.Contains("USB") || n is "DIGU" or "PKT-U" or "DATA-U" => data ? HamlibNative.ModePktUsb : HamlibNative.ModeUsb,
            _ when n.Contains("LSB") || n is "DIGL" or "PKT-L" or "DATA-L" => data ? HamlibNative.ModePktLsb : HamlibNative.ModeLsb,
            _ when n.StartsWith("FM") || n is "PKT-FM" or "DATA(FM)" or "WFM" => data ? HamlibNative.ModePktFm : HamlibNative.ModeFm,
            _ when n.StartsWith("AM") => 1ul << 0,
            _ when n.StartsWith("CW") => 1ul << 1,
            _ when n.StartsWith("RTTY") || n.StartsWith("FSK") => 1ul << 4,
            _ => 0,
        };
    }

    /// <summary>Sends one CI-V message through Hamlib and returns the radio's answer to the
    /// computer (skipping the radio's echo of the message, and messages meant for others).</summary>
    private byte[] Exchange(byte[] frame, string what)
    {
        lock (_lock)
        {
            byte radio = frame[2];
            var reply = new byte[200];
            byte[] end = { IcomCiv.End };
            var send = frame;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int n = HamlibNative.rig_send_raw(Rig, send, send.Length, reply, reply.Length, end);
                if (n < 0) { HamlibLog.WriteRecent(); throw new RadioException($"Couldn't {what}: {Explain(n)}"); }
                if (IcomCiv.Answer(reply.AsSpan(0, Math.Min(n, reply.Length)), radio) is { } answer) return answer;
                send = Array.Empty<byte>();   // that was the echo or someone else's: read the next message
            }
            throw new RadioException($"Couldn't {what}: the radio didn't answer. Check its CI-V address and baud rate.");
        }
    }
}
