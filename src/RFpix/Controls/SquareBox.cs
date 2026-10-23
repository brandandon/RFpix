// SquareBox.cs - a box (with a border) that is always as tall as it is wide.
//
// The Send tab's two picture boxes use it: they take whatever width their column has (Scale
// makes it a share of that: 0.9 is 10% smaller), as long as there's the height for it; in a
// short window they shrink to the height that's left, so the buttons below stay in view.
// While the page works out how much height it needs (FitToWindow), it asks for MinSide.

using Avalonia;
using Avalonia.Controls;

namespace RFpix.Controls;

public class SquareBox : Border
{
    /// <summary>The side used when there's no width to go by (it isn't the case on the Send tab).</summary>
    private const double FallbackSide = 240;

    /// <summary>The smallest side it asks for when there's no limit on height.</summary>
    public const double MinSide = 200;

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
        side = double.IsInfinity(availableSize.Height) ? Math.Min(side, MinSide) : Math.Min(side, availableSize.Height);
        side = Math.Max(0, side);
        base.MeasureOverride(new Size(side, side));   // the picture inside gets a square to fit in
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double side = Math.Min(finalSize.Width, finalSize.Height);
        base.ArrangeOverride(new Size(side, side));
        return new Size(side, side);
    }
}
