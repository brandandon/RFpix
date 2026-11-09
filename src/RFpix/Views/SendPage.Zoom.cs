// SendPage.Zoom.cs - part of SendPage (SendPage.axaml.cs): zooming in on the original and as-sent pictures.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RFpix.Analog;
using RFpix.Audio;
using RFpix.Gallery;
using RFpix.HamDrm;
using RFpix.Imaging;
using RFpix.Radio;
using RFpix.Send;
using RFpix.Settings;
using RFpix.Templates;

namespace RFpix.Views;

public partial class SendPage
{
    // ---- zoom ----

    private void OnZoomChanged(object? sender, RoutedEventArgs e) => ApplyZoom();

    /// <summary>Not zoomed, each picture fits its box. Zoomed, the boxes show scrolling copies
    /// instead: the sent picture at its own pixel size, the original scaled to the same size
    /// so the two line up, and they move together.</summary>
    private void ApplyZoom()
    {
        bool zoom = ZoomBox.IsChecked == true && _fitted is not null && !_analog;
        OriginalImage.IsVisible = SentImage.IsVisible = !zoom;
        OriginalScroll.IsVisible = SentScroll.IsVisible = ZoomHint.IsVisible = zoom;
        if (!zoom) return;
        OriginalZoomImage.Width = _fitted!.Picture.Width;
        OriginalZoomImage.Height = _fitted.Picture.Height;
        // Start in the middle of the picture.
        Dispatcher.UIThread.Post(() =>
        {
            var middle = new Vector(Math.Max(0, (SentScroll.Extent.Width - SentScroll.Viewport.Width) / 2),
                                    Math.Max(0, (SentScroll.Extent.Height - SentScroll.Viewport.Height) / 2));
            SentScroll.Offset = OriginalScroll.Offset = middle;
        }, DispatcherPriority.Loaded);
    }

    private bool _syncingScroll;

    private void OnPreviewScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || sender is not ScrollViewer moved) return;
        _syncingScroll = true;
        var other = moved == OriginalScroll ? SentScroll : OriginalScroll;
        other.Offset = moved.Offset;
        _syncingScroll = false;
    }

    // Dragging a zoomed picture moves it under the pointer, like a map.
    private Point? _dragFrom;
    private Vector _dragStartOffset;

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !e.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
        _dragFrom = e.GetPosition(scroll);
        _dragStartOffset = scroll.Offset;
        e.Pointer.Capture(scroll);
        e.Handled = true;
    }

    private void OnPreviewDragged(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from || sender is not ScrollViewer scroll) return;
        var moved = e.GetPosition(scroll) - from;
        double x = Math.Clamp(_dragStartOffset.X - moved.X, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width));
        double y = Math.Clamp(_dragStartOffset.Y - moved.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        scroll.Offset = new Vector(x, y);   // the other box follows (OnPreviewScrolled)
        e.Handled = true;
    }

    private void OnPreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
