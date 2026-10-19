// SquareBox.cs - a box (with a border) that is always as tall as it is wide.
//
// The Send tab's two picture boxes use it: they take whatever width their column has,
// and their height follows, so they stay square as the window is resized. It works out
// its own size while the screen is laid out, so nothing has to adjust it afterwards.

using Avalonia;
using Avalonia.Controls;

namespace RFpix.Controls;

public class SquareBox : Border
{
    /// <summary>The side used when there's no width to go by (it isn't the case on the Send tab).</summary>
    private const double FallbackSide = 240;

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = double.IsInfinity(availableSize.Width) ? FallbackSide : availableSize.Width;
        base.MeasureOverride(new Size(side, side));   // the picture inside gets a square to fit in
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize) =>
        base.ArrangeOverride(new Size(finalSize.Width, finalSize.Width));
}
