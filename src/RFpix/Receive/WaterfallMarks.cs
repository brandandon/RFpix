// WaterfallMarks.cs - which marks the Receive tab's waterfall shows: analog SSTV's (S, B, V, W)
// or HamDRM's three pilots.
//
//   - When RFpix starts, they match the Send tab (analog or HamDRM).
//   - When a transmission of the other kind starts arriving, they switch to its marks.
//   - They stay on the last kind received, after it ends, until a transmission of the other
//     kind arrives or the Send setting is changed. Changing Send puts them back to match it.

namespace RFpix.Receive;

public sealed class WaterfallMarks
{
    private bool _sendAnalog;

    /// <summary>Starts matching the Send tab.</summary>
    public WaterfallMarks(bool sendAnalog) => _sendAnalog = Analog = sendAnalog;

    /// <summary>Show the analog marks (true) or HamDRM's (false).</summary>
    public bool Analog { get; private set; }

    /// <summary>Called whenever the screen is brought up to date: the Send tab's setting, and
    /// whether an analog picture or a HamDRM transmission is arriving now. Returns <see cref="Analog"/>.</summary>
    public bool Update(bool sendAnalog, bool analogArriving, bool digitalArriving)
    {
        if (sendAnalog != _sendAnalog)
        {
            _sendAnalog = sendAnalog;
            Analog = sendAnalog;
        }
        if (analogArriving) Analog = true;
        else if (digitalArriving) Analog = false;
        return Analog;
    }
}
