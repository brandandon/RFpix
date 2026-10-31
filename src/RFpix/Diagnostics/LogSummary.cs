// LogSummary.cs - the settings that matter for problems, in words, for the top of each log
// (and the radio's, again, each time it connects).
//
// Never in it: the FTP server's password, user name or address. Uploading is only said to be
// on or off, and how (FTP, FTPS or SFTP). (The uploader's own lines, also logged, name the
// server it connects to, never the user name or password.)

using RFpix.Radio;
using RFpix.Settings;

namespace RFpix.Diagnostics;

public static class LogSummary
{
    /// <summary>The settings, a line each.</summary>
    /// <param name="inputName">The sound input's name for its ID, or null if it isn't connected now.</param>
    /// <param name="outputName">The same for the sound output.</param>
    /// <param name="portName">A serial port's full name ("Silicon Labs ... Enhanced COM Port"), or null.</param>
    /// <param name="modelName">Hamlib's name for a model number, or null.</param>
    public static IReadOnlyList<string> Lines(AppSettings s, Func<string, string?> inputName, Func<string, string?> outputName,
                                              Func<string, string?> portName, Func<int, string?> modelName, string picturesFolder)
    {
        var lines = new List<string>
        {
            $"Callsign: {Or(s.Callsign, "(not set)")}  ·  grid square: {Or(s.GridSquare, "(not set)")}",
            Radio(s.Radio, s.Radio.Port is { Length: > 0 } port ? portName(port) : null,
                  RadioChoices.Find(s.Radio.Choice).HamlibModel == -1 ? modelName(s.Radio.OtherModel) : null),
            "Sound input (receive): " + Device(s.InputDeviceId, inputName) + Calibration(s.InputClock, s.InputDeviceId),
            "Sound output (send): " + Device(s.OutputDeviceId, outputName) + Calibration(s.OutputClock, s.OutputDeviceId),
            "Send tab: " + (s.SendAnalog
                ? $"analog SSTV, {RFpix.Analog.SstvModes.Get(s.AnalogMode).Name}"
                : $"HamDRM, {s.SendSettings?.Settings.Description ?? "the first settings"}" +
                  (s.SendSettings is { } p ? $", RS{p.ReedSolomon}, lead-in {p.LeadInSeconds:0.0} s" : "")) +
                $", transmit level {s.TransmitLevelDb:0} dB",
            $"Pictures folder: {picturesFolder}",
            $"Website copies: {(s.Upload.SaveCopies ? "on" : "off")}  ·  uploading: {(s.Upload.Upload ? $"on, by {s.Upload.Protocol}" : "off")}",
        };
        return lines;
    }

    /// <summary>How RFpix reaches the radio, e.g. "Radio: Yaesu FT-991A (Hamlib model 1035), port COM4
    /// (Silicon Labs Dual CP2105 USB to UART Bridge: Enhanced COM Port), 38400 baud, PTT by CAT command".</summary>
    public static string Radio(RadioSettings r, string? portName, string? modelName)
    {
        var choice = RadioChoices.Find(r.Choice);
        if (choice.Connection == "none") return $"Radio: {choice.Name}";
        string ptt = $"PTT by {Ptt(r.Keying())}";
        if (choice.Connection == "network") return $"Radio: {choice.Name}, at {Or(r.Port, "(no address)")}, {ptt}";

        int model = choice.HamlibModel == -1 ? r.OtherModel : choice.HamlibModel;
        string name = choice.HamlibModel == -1 ? (modelName ?? "Other radio") : choice.Name;
        string text = $"Radio: {name} (Hamlib model {model}), port {Or(r.Port, "(none chosen)")}" +
                      (portName is { Length: > 0 } ? $" ({portName})" : "") + $", {r.BaudRate} baud, {ptt}";
        if (!string.IsNullOrWhiteSpace(r.CivAddress)) text += $", CI-V address {r.CivAddress.Trim()}";
        if (RadioChoices.CanSwitchDataMode(r.Choice)) text += r.DataModeForTransmit ? ", data mode while transmitting" : ", data mode not switched";
        return text;
    }

    /// <summary>A HamDRM transmission that ended, e.g. "HamDRM transmission ended: mode B, 2.5 kHz · 16-QAM ·
    /// long interleave · high protection, from N0CALL, "tower.jp2": 120 of 120 pieces, quality 18 dB,
    /// 300 packets good, 2 bad".</summary>
    public static string Transmission(RFpix.HamDrm.TransmissionReport r)
    {
        var parts = new List<string> { r.Mode is { } mode ? $"mode {mode}" : "mode not read" };
        if (r.Settings is { } fac) parts.Add(fac.Description);
        parts.Add(string.IsNullOrEmpty(r.Callsign) ? "no callsign read" : $"from {r.Callsign}");
        string file = r.Name is null ? "no file name read" : $"\"{r.Name}\"";
        if (r.PiecesExpected > 0) file += $": {r.PiecesReceived} of {r.PiecesExpected} pieces";
        if (r.File is not null) file += ", complete";
        parts.Add(file);
        if (r.QualityDb is double q) parts.Add($"quality {q:0} dB");
        parts.Add($"{r.PacketsGood} packets good, {r.PacketsBad} bad");
        return $"HamDRM transmission {(r.ContinuesPrevious ? "(continued after a dropout) " : "")}ended: {string.Join(", ", parts)}";
    }

    private static string Ptt(PttMethod ptt) => ptt switch
    {
        PttMethod.Cat => "CAT command",
        PttMethod.Rts => "RTS",
        PttMethod.Dtr => "DTR",
        _ => "VOX (the radio keys itself)",
    };

    private static string Device(string? id, Func<string, string?> name) =>
        id is null ? "(none chosen)" : name(id) ?? "(the one chosen isn't connected now)";

    private static string Calibration(ClockCalibration? c, string? id) =>
        c is not null && c.DeviceId == id ? $", calibrated {c.Ppm:+0.0;-0.0} ppm" : "";

    private static string Or(string? text, string otherwise) => string.IsNullOrWhiteSpace(text) ? otherwise : text.Trim();
}
