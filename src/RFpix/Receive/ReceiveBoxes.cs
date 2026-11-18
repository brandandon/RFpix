// ReceiveBoxes.cs - which picture's details the Receive tab's boxes show: the picture arriving, or the
// last one received, until the next one really starts.
//
// A HamDRM transmission counts as started only once its settings (the FAC) have been read, a second or
// two in, as for the waterfall marks (WaterfallMarks.cs). Before (Dan, 20 m, Nov 15, 2026), the boxes
// switched as soon as the receiver thought a HamDRM signal had begun: a burst of noise or voice that
// briefly looked like one ("Mode E") replaced an analog picture's boxes, or wiped a HamDRM picture's
// kept settings with "…".

using RFpix.HamDrm;

namespace RFpix.Receive;

/// <summary>The kind of picture whose details the boxes show.</summary>
public enum BoxesShow { Nothing, Digital, Analog }

public static class ReceiveBoxes
{
    /// <summary>What the boxes show next: an analog picture arriving (or waiting for its signal to come
    /// back) takes them; a HamDRM transmission takes them once its settings have been read; otherwise
    /// they keep showing the last picture's details.</summary>
    public static BoxesShow Next(BoxesShow current, bool analogArriving, HeardSettings? heard) =>
        analogArriving ? BoxesShow.Analog
        : heard is { Fac: not null } ? BoxesShow.Digital
        : current;

    /// <summary>Whether the HamDRM boxes are filled in with this transmission's settings now: only once
    /// they've been read, so noise between pictures leaves the last picture's settings in place.</summary>
    public static bool ShowsNewSettings(BoxesShow boxes, HeardSettings? heard) =>
        boxes == BoxesShow.Digital && heard is { Fac: not null };
}
