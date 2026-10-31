// StatusLine.cs - the line of text under a page's buttons that says what happened.
//
// Every page shows its messages the same way: in the normal colour, or orange when something
// went wrong. A problem also goes into RFpix's log (Diagnostics/AppLog.cs), under the page's name.

using Avalonia.Controls;
using Avalonia.Media;
using RFpix.Diagnostics;

namespace RFpix.Views;

internal static class StatusLine
{
    /// <summary>Shows <paramref name="message"/> in <paramref name="line"/>; a problem in orange (and in
    /// the log, under <paramref name="area"/>).</summary>
    public static void Show(TextBlock line, string message, bool problem, string area)
    {
        if (problem) AppLog.Error(area, message);
        line.Text = message;
        if (problem) line.Foreground = Brushes.OrangeRed;
        else line.ClearValue(TextBlock.ForegroundProperty);
    }
}
