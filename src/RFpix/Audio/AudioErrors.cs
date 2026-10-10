// AudioErrors.cs - turns Windows' cryptic audio error codes into plain advice.
//
// Windows reports problems as numbers (for example 0x80070005). Each case below
// is a problem a ham is likely to hit, with what to do about it.

namespace RFpix.Audio;

public static class AudioErrors
{
    public static string Explain(Exception ex)
    {
        if (ex is AudioException)
        {
            return ex.Message;   // already written in plain words
        }

        return unchecked((uint)ex.HResult) switch
        {
            0x80070005 =>   // E_ACCESSDENIED
                "Windows is blocking access to recording devices. Open Settings > Privacy & security > " +
                "Microphone and turn on both \"Microphone access\" and \"Let desktop apps access your microphone\", " +
                "then click Start listening again.",

            0x8889000A =>   // AUDCLNT_E_DEVICE_IN_USE
                "Another program has taken over this sound device. Close other radio or sound programs " +
                "(WSJT-X, fldigi, MMSSTV and so on) and try again.",

            0x88890004 =>   // AUDCLNT_E_DEVICE_INVALIDATED
                "The sound device disappeared. The radio may have been switched off or its USB cable unplugged. " +
                "Check it, then click Refresh.",

            0x80070490 =>   // ELEMENT_NOT_FOUND
                "That sound device isn't connected right now. Plug in or switch on the radio, then click Refresh.",

            0x88890008 =>   // AUDCLNT_E_UNSUPPORTED_FORMAT
                "Windows refused the sound format for this device. In Windows Sound settings, open the device's " +
                "properties and set the default format to 16 bit, 48000 Hz.",

            _ => $"Could not use the sound device: {ex.Message} (Windows code 0x{ex.HResult:X8})"
        };
    }
}
