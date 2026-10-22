// SquareBox.cs - a box (with a border) that is always as tall as it is wide.
//
// The Send tab's two picture boxes use it: they take whatever width their column has,
// and their height follows, so they stay square as the window is resized. It works out
// its own size while the screen is laid out, so nothing has to adjust it afterwards.
// Scale makes it a share of the width (0.9: 10% smaller), kept to the left.

using Avalonia;
using Avalonia.Controls;

namespace RFpix.Controls;

public class SquareBox : Border
{
    /// <summary>The side used when there's no width to go by (it isn't the case on the Send tab).</summary>
    private const double FallbackSide = 240;

    public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<SquareBox, double>(nameof(Scale), 1.0);

    /// <summary>The side as a share of the width it's given (1 = all of it).</summary>
    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    static SquareBox() => AffectsMeasure<SquareBox>(ScaleProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = (double.IsInfinity(availableSize.Width) ? FallbackSide : availableSize.Width) * Math.Clamp(Scale, 0.1, 1);
        base.MeasureOverride(new Size(side, side));   // the picture inside gets a square to fit in
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(new Size(finalSize.Width, finalSize.Width));
        return new Size(finalSize.Width, finalSize.Width);
    }
}
