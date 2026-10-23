// FitToWindow.cs - shrinks a page to fit its window, so nothing is lost off the bottom.
//
// Every page sits in one of these (MainWindow, and the pop-out Receive window). When the page
// fits the window at its normal size, nothing changes. When it doesn't, the whole page (text,
// buttons, pictures) is drawn smaller, just enough to fit, the way a web page zooms out. It
// never grows bigger than normal, and never shrinks below 60% (MinScale) so text stays
// readable: in a window smaller than that, the page scrolls.
//
// How it works: the page is laid out as if the window were bigger (its size divided by the
// scale), then drawn scaled down. To find the scale, the page is asked how tall it needs to be
// at each trial width (with as much height as it likes); parts that should fill whatever room
// there is (the waterfall, the received picture, the Gallery's tiles) are wrapped in Flex, so
// they ask for only a little then. While the window keeps its size, the scale is checked again
// at most a few times a second, so the Receive tab's constant updates (the level, the signal)
// don't make the whole page lay itself out over and over.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RFpix.Controls;

public sealed class FitToWindow : Decorator
{
    /// <summary>The smallest the page is drawn: 60% of normal. Below that it scrolls.</summary>
    public const double MinScale = 0.6;

    private readonly ScaleHost _host = new();

    public FitToWindow(Control page)
    {
        _host.Page = page;
        Child = new ScrollViewer
        {
            Content = _host,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>The page inside.</summary>
    public Control Page => _host.Page!;

    /// <summary>The scale the page is drawn at (1 = normal size).</summary>
    public double Scale => _host.Scale;

    /// <summary>Takes the page out (to move it elsewhere).</summary>
    public Control Release()
    {
        var page = _host.Page!;
        _host.Page = null;
        return page;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _host.ViewportHeight = availableSize.Height;   // the ScrollViewer passes on unlimited height
        return base.MeasureOverride(availableSize);
    }

    /// <summary>Lays the page out at the trial size and draws it scaled.</summary>
    private sealed class ScaleHost : Control
    {
        private Control? _page;
        private double _lastWidth = double.NaN, _lastHeight = double.NaN, _virtualHeight, _needed, _applied = 1;
        private DateTime _lastSearch = DateTime.MinValue;
        private DispatcherTimer? _recheck;
        private static readonly TimeSpan RecheckAfter = TimeSpan.FromMilliseconds(400);

        public double ViewportHeight { get; set; } = double.PositiveInfinity;
        public double Scale { get; private set; } = 1;

        public Control? Page
        {
            get => _page;
            set
            {
                if (_page is not null)
                {
                    _page.RenderTransform = null;
                    _applied = 1;
                    LogicalChildren.Remove(_page);
                    VisualChildren.Remove(_page);
                }
                _page = value;
                if (_page is not null)
                {
                    _page.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
                    LogicalChildren.Add(_page);
                    VisualChildren.Add(_page);
                }
                Scale = 1;
                _lastWidth = double.NaN;
                _lastSearch = DateTime.MinValue;
                InvalidateMeasure();
            }
        }

        // The height the page needs, drawn at scale s, in a window W wide.
        private double Needed(double width, double s)
        {
            _page!.Measure(new Size(width / s, double.PositiveInfinity));
            return _page.DesiredSize.Height * s;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_page is null) return default;
            double width = availableSize.Width, height = ViewportHeight;
            if (double.IsInfinity(width) || double.IsInfinity(height) || width <= 0 || height <= 0)
            {
                Scale = 1;
                if (_applied != 1) { _page.RenderTransform = null; _applied = 1; }
                _page.Measure(availableSize);
                _virtualHeight = _page.DesiredSize.Height;
                return _page.DesiredSize;
            }

            double s;
            var now = DateTime.UtcNow;
            if (width == _lastWidth && height == _lastHeight && now - _lastSearch < RecheckAfter)
            {
                // The same window, checked a moment ago: keep the scale, and check again shortly.
                s = Scale;
                ScheduleRecheck();
            }
            else
            {
                s = FindScale(width, height);
                _needed = Needed(width, s) / s;
                _lastSearch = now;
            }
            Scale = s;
            _lastWidth = width;
            _lastHeight = height;
            // Laid out at least as tall as the window (so parts that fill it do), or taller to scroll.
            _virtualHeight = Math.Max(height / s, _needed);
            _page.Measure(new Size(width / s, _virtualHeight));
            if (Math.Abs(s - _applied) > 1e-6)
            {
                _page.RenderTransform = s >= 0.999 ? null : new ScaleTransform(s, s);
                _applied = s;
            }
            return new Size(width, _virtualHeight * s);
        }

        private void ScheduleRecheck()
        {
            if (_recheck is null)
            {
                _recheck = new DispatcherTimer { Interval = RecheckAfter };
                _recheck.Tick += (_, _) => { _recheck!.Stop(); _lastSearch = DateTime.MinValue; InvalidateMeasure(); };
            }
            if (!_recheck.IsEnabled) _recheck.Start();
        }

        private double FindScale(double width, double height)
        {
            const double Step = 0.01;
            bool Fits(double s) => Needed(width, s) <= height + 0.5;

            // Most layouts are an update at the same size: try the last scale first.
            if (!double.IsNaN(_lastWidth) && width == _lastWidth && height == _lastHeight)
            {
                double last = Scale;
                if (last >= 0.999 && Fits(1)) return 1;
                if (last < 0.999 && Fits(last) && !Fits(Math.Min(1, last + Step)) && last > MinScale - 1e-9) return last;
            }
            if (Fits(1)) return 1;
            if (!Fits(MinScale)) return MinScale;
            // The largest scale that fits, to the nearest 1%.
            double lo = MinScale, hi = 1;
            while (hi - lo > Step / 2)
            {
                double mid = (lo + hi) / 2;
                if (Fits(mid)) lo = mid; else hi = mid;
            }
            return Math.Max(MinScale, Math.Floor(lo / Step + 1e-6) * Step);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_page is null) return finalSize;
            double s = Scale;
            _page.Arrange(new Rect(0, 0, finalSize.Width / s, Math.Max(_virtualHeight, finalSize.Height / s)));
            return finalSize;
        }
    }
}

/// <summary>A part that fills whatever room the page has (the waterfall, a picture, a list that
/// scrolls). While the page is working out how much height it needs, it asks for only
/// <see cref="MinHeight"/>-ish (its Min); given real room, it takes it all.</summary>
public sealed class Flex : Decorator
{
    public static readonly StyledProperty<double> MinProperty = AvaloniaProperty.Register<Flex, double>(nameof(Min), 160);

    /// <summary>The height it asks for when the page is finding out what it needs.</summary>
    public double Min
    {
        get => GetValue(MinProperty);
        set => SetValue(MinProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Height)) return base.MeasureOverride(availableSize);
        var size = base.MeasureOverride(new Size(availableSize.Width, Min));
        return new Size(size.Width, Math.Min(size.Height, Min));
    }
}
