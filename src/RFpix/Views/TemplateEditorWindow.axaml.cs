// TemplateEditorWindow.axaml.cs - the code half of the template editor.
//
// What happens here:
//   1. The template is drawn on a picture (yours, or a sample) at an analog or a digital
//      picture's size, exactly as it would go out (OverlayRenderer), and shown on the left.
//   2. Clicking the picture chooses the topmost item there; dragging moves it; dragging the
//      square at its lower right corner resizes it. Positions are kept as shares of the
//      picture, so the template fits any size.
//   3. The chosen item's settings are made on the right, for its kind: text, box, logo, border.
//      Every change draws the preview again.
//   4. Save keeps it in Documents\RFpix\Templates. RFpix's own templates can't be changed:
//      they open as a copy ("Copy of ...") to save under a name of your own.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RFpix.Imaging;
using RFpix.Templates;

namespace RFpix.Views;

public partial class TemplateEditorWindow : Window
{
    private static readonly string[] Fonts =
        { "Arial", "Arial Black", "Segoe UI", "Verdana", "Tahoma", "Trebuchet MS", "Impact", "Georgia", "Times New Roman", "Courier New", "Comic Sans MS" };
    private static readonly (string Name, string Hex)[] Swatches =
    {
        ("White", "#FFFFFF"), ("Black", "#000000"), ("Yellow", "#FFE14D"), ("Orange", "#FF8C1A"),
        ("Red", "#E53935"), ("Green", "#43A047"), ("Cyan", "#26C6DA"), ("Blue", "#1E63D6"),
    };

    private readonly OverlayTemplate _template;   // the one being edited (a copy for RFpix's own)
    private readonly DecodedPicture _source;      // the picture it's shown on
    private readonly TemplateFields _fields;
    private DecodedPicture? _background;          // _source at the preview size
    private OverlayItem? _selected;
    private bool _dirty, _closeArmed, _building;

    public TemplateEditorWindow() : this(null, TemplatePickerWindow.SamplePicture(640, 480),
                                         new TemplateFields("KD9DD", "W9ABC", "Bob", "595", DateTime.Now)) { }

    /// <param name="template">The template to change (RFpix's own: a copy is made), or null for a new one.</param>
    public TemplateEditorWindow(OverlayTemplate? template, DecodedPicture picture, TemplateFields fields)
    {
        InitializeComponent();
        _template = template is null ? new OverlayTemplate { Name = "My template" }
                  : template.BuiltIn ? template.Copy($"Copy of {template.Name}")
                  : template;
        _source = picture;
        _fields = fields;
        NameBox.Text = _template.Name;
        NameBox.TextChanged += (_, _) => _dirty = true;
        DeleteButton.IsEnabled = _template.Path is not null;
        AnalogSizeButton.IsChecked = true;
        AnalogSizeButton.IsCheckedChanged += (_, _) => { _background = null; Redraw(); };
        ItemsList.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            int i = ItemsList.SelectedIndex;
            Select(i >= 0 && i < _template.Items.Count ? _template.Items[i] : null);
        };
        PreviewHost.PointerPressed += OnPreviewPressed;
        PreviewHost.PointerMoved += OnPreviewMoved;
        PreviewHost.PointerReleased += OnPreviewReleased;
        PreviewHost.SizeChanged += (_, _) => ShowSelection();
        if (template?.BuiltIn == true) Status($"\"{template.Name}\" comes with RFpix, so this is a copy: give it a name of your own and Save.");
        FillList();
        Select(_template.Items.LastOrDefault(i => i.Kind != OverlayKind.Border));
        Redraw();
    }

    // ---- the preview ----

    private (int W, int H) PreviewSize => AnalogSizeButton.IsChecked == true ? (320, 256) : (1280, 960);

    private void Redraw()
    {
        var (w, h) = PreviewSize;
        if (_background is null || _background.Width != w)
        {
            // The picture filling the preview frame (as analog SSTV fits it), so the template is seen at that shape.
            _background = RFpix.Analog.SstvEncoder.Fit(_source, w, h);
        }
        var drawn = OverlayRenderer.Draw(_background, _template, _fields);
        var old = PreviewImage.Source as IDisposable;
        PreviewImage.Source = Bitmaps.From(drawn);
        old?.Dispose();
        ShowSelection();
    }

    /// <summary>Where the picture is drawn in the preview box (it keeps its shape, centred).</summary>
    private Rect PictureArea()
    {
        var (w, h) = PreviewSize;
        var box = PreviewHost.Bounds;
        double scale = Math.Min(box.Width / w, box.Height / h);
        double pw = w * scale, ph = h * scale;
        return new Rect((box.Width - pw) / 2, (box.Height - ph) / 2, pw, ph);
    }

    private const double HandleSize = 12;

    private void ShowSelection()
    {
        SelectionCanvas.Children.Clear();
        if (_selected is not { } item || item.Kind == OverlayKind.Border) return;
        var area = PictureArea();
        if (area.Width <= 0) return;
        double x = area.X + item.X * area.Width, y = area.Y + item.Y * area.Height;
        double w = item.Width * area.Width, h = item.Height * area.Height;
        var outline = new Rectangle
        {
            Width = Math.Max(1, w), Height = Math.Max(1, h),
            Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 },
        };
        Canvas.SetLeft(outline, x);
        Canvas.SetTop(outline, y);
        var handle = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.DeepSkyBlue, Stroke = Brushes.White, StrokeThickness = 1 };
        Canvas.SetLeft(handle, x + w - HandleSize / 2);
        Canvas.SetTop(handle, y + h - HandleSize / 2);
        SelectionCanvas.Children.Add(outline);
        SelectionCanvas.Children.Add(handle);
    }

    // ---- choosing, moving and resizing with the mouse ----

    private enum DragKind { None, Move, Resize }
    private DragKind _drag;
    private Point _dragFrom;
    private (double X, double Y, double W, double H) _dragStart;

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewHost).Properties.IsLeftButtonPressed) return;
        var area = PictureArea();
        var p = e.GetPosition(PreviewHost);
        double fx = (p.X - area.X) / area.Width, fy = (p.Y - area.Y) / area.Height;

        // The selected item's corner handle: resize.
        if (_selected is { Kind: not OverlayKind.Border } s)
        {
            double cx = area.X + (s.X + s.Width) * area.Width, cy = area.Y + (s.Y + s.Height) * area.Height;
            if (Math.Abs(p.X - cx) <= HandleSize && Math.Abs(p.Y - cy) <= HandleSize)
            {
                Begin(DragKind.Resize, p, s, e);
                return;
            }
        }
        // Otherwise the topmost item under the pointer: choose it, and move it.
        var hit = _template.Items.LastOrDefault(i => i.Kind != OverlayKind.Border &&
                                                    fx >= i.X && fx <= i.X + i.Width && fy >= i.Y && fy <= i.Y + i.Height);
        Select(hit);
        if (hit is not null) Begin(DragKind.Move, p, hit, e);
    }

    private void Begin(DragKind kind, Point p, OverlayItem item, PointerPressedEventArgs e)
    {
        _drag = kind;
        _dragFrom = p;
        _dragStart = (item.X, item.Y, item.Width, item.Height);
        e.Pointer.Capture(PreviewHost);
        e.Handled = true;
    }

    private void OnPreviewMoved(object? sender, PointerEventArgs e)
    {
        if (_drag == DragKind.None || _selected is not { } item) return;
        var area = PictureArea();
        var p = e.GetPosition(PreviewHost);
        double dx = (p.X - _dragFrom.X) / area.Width, dy = (p.Y - _dragFrom.Y) / area.Height;
        var (x, y, w, h) = _dragStart;
        if (_drag == DragKind.Move)
        {
            item.X = Math.Clamp(x + dx, 0, Math.Max(0, 1 - w));
            item.Y = Math.Clamp(y + dy, 0, Math.Max(0, 1 - h));
        }
        else
        {
            item.Width = Math.Clamp(w + dx, 0.02, 1 - x);
            item.Height = Math.Clamp(h + dy, 0.02, 1 - y);
        }
        _dirty = true;
        Redraw();
    }

    private void OnPreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        e.Pointer.Capture(null);
    }

    // ---- the items ----

    private void FillList()
    {
        _building = true;
        ItemsList.ItemsSource = _template.Items.Select(i => i.Describe()).ToList();
        ItemsList.SelectedIndex = _selected is null ? -1 : _template.Items.IndexOf(_selected);
        _building = false;
    }

    private void Select(OverlayItem? item)
    {
        _selected = item;
        _building = true;
        ItemsList.SelectedIndex = item is null ? -1 : _template.Items.IndexOf(item);
        _building = false;
        BuildProperties();
        ShowSelection();
    }

    private void Add(OverlayItem item)
    {
        _template.Items.Add(item);
        _dirty = true;
        FillList();
        Select(item);
        Redraw();
    }

    private void OnAddTextClick(object? sender, RoutedEventArgs e) =>
        Add(new OverlayItem { Kind = OverlayKind.Text, X = 0.05, Y = 0.4, Width = 0.9, Height = 0.18, Text = "de %me", Size = 0.12, Shadow = true });

    private void OnAddBoxClick(object? sender, RoutedEventArgs e) =>
        Add(new OverlayItem { Kind = OverlayKind.Box, X = 0, Y = 0.85, Width = 1, Height = 0.15, Fill = "#000000", Opacity = 0.55 });

    private void OnAddBorderClick(object? sender, RoutedEventArgs e) =>
        Add(new OverlayItem { Kind = OverlayKind.Border, X = 0, Y = 0, Width = 1, Height = 1, Fill = "#FFFFFF", Opacity = 1, Thickness = 0.02 });

    private async void OnAddLogoClick(object? sender, RoutedEventArgs e)
    {
        var data = await ChooseLogoAsync();
        if (data is null) return;
        Add(new OverlayItem { Kind = OverlayKind.Image, X = 0.72, Y = 0.04, Width = 0.24, Height = 0.24, ImageData = data, Opacity = 1 });
    }

    /// <summary>A picture file for a logo (PNG keeps a see-through background), as kept in the template; or null.</summary>
    private async Task<string?> ChooseLogoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a logo (a PNG with a see-through background works best)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" } } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length > 2_000_000) { Status("That logo is over 2 MB: make it smaller first (a few hundred pixels across is plenty)."); return null; }
            using var check = SkiaSharp.SKBitmap.Decode(bytes);
            if (check is null) { Status($"{System.IO.Path.GetFileName(path)} isn't a picture RFpix can use as a logo."); return null; }
            return Convert.ToBase64String(bytes);
        }
        catch (IOException ex) { Status($"Couldn't read it: {ex.Message}"); return null; }
    }

    private void Move(int by)
    {
        if (_selected is not { } item) return;
        int i = _template.Items.IndexOf(item), j = Math.Clamp(i + by, 0, _template.Items.Count - 1);
        if (i == j) return;
        _template.Items.RemoveAt(i);
        _template.Items.Insert(j, item);
        _dirty = true;
        FillList();
        Redraw();
    }

    private void OnUpClick(object? sender, RoutedEventArgs e) => Move(+1);
    private void OnDownClick(object? sender, RoutedEventArgs e) => Move(-1);

    private void OnCopyItemClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } item) return;
        var copy = item.Copy();
        copy.X = Math.Min(copy.X + 0.03, Math.Max(0, 1 - copy.Width));
        copy.Y = Math.Min(copy.Y + 0.03, Math.Max(0, 1 - copy.Height));
        Add(copy);
    }

    private void OnRemoveItemClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } item) return;
        _template.Items.Remove(item);
        _dirty = true;
        _selected = null;
        FillList();
        Select(null);
        Redraw();
    }

    // ---- the chosen item's settings ----

    private void Changed(bool relist = false)
    {
        _dirty = true;
        if (relist) FillList();
        Redraw();
    }

    private void BuildProperties()
    {
        var panel = PropertiesPanel.Children;
        panel.Clear();
        if (_selected is not { } item)
        {
            panel.Add(Hint("Choose an item (in the list, or click it on the picture), or add one."));
            return;
        }
        switch (item.Kind)
        {
            case OverlayKind.Text:
                panel.Add(Label("Text"));
                var text = new TextBox { Text = item.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
                text.TextChanged += (_, _) => { item.Text = text.Text ?? ""; Changed(relist: true); };
                panel.Add(text);
                panel.Add(Hint("Codes: %me your callsign, %to their callsign, %name their name, %rst the report, %date, %time, %utc."));
                panel.Add(Label("Font"));
                var font = new ComboBox { ItemsSource = Fonts, SelectedItem = Fonts.Contains(item.Font) ? item.Font : null, HorizontalAlignment = HorizontalAlignment.Stretch };
                font.SelectionChanged += (_, _) => { if (font.SelectedItem is string f) { item.Font = f; Changed(); } };
                panel.Add(font);
                panel.Add(SliderRow("Size", item.Size, 0.02, 0.35, v => item.Size = v, v => $"{v * 100:0}% of the height"));
                panel.Add(Row(Check("Bold", item.Bold, v => item.Bold = v), Check("Italic", item.Italic, v => item.Italic = v),
                              Check("Shadow", item.Shadow, v => item.Shadow = v)));
                panel.Add(Label("Line up"));
                var align = new ComboBox { ItemsSource = new[] { "Left", "Centre", "Right" }, SelectedIndex = (int)item.Align };
                align.SelectionChanged += (_, _) => { item.Align = (OverlayAlign)Math.Max(0, align.SelectedIndex); Changed(); };
                panel.Add(align);
                panel.Add(ColorRow("Colour", item.Color, v => item.Color = v));
                panel.Add(Check("Outline round the letters", item.OutlineColor is not null,
                                v => { item.OutlineColor = v ? item.OutlineColor ?? "#000000" : null; BuildProperties(); }));
                if (item.OutlineColor is not null)
                {
                    panel.Add(ColorRow("Outline colour", item.OutlineColor, v => item.OutlineColor = v));
                    panel.Add(SliderRow("Outline thickness", item.Outline, 0.02, 0.25, v => item.Outline = v, v => $"{v * 100:0}%"));
                }
                panel.Add(Check("Make it smaller if it's too long to fit", item.ShrinkToFit, v => item.ShrinkToFit = v));
                break;

            case OverlayKind.Box:
                panel.Add(ColorRow("Colour", item.Fill, v => item.Fill = v));
                panel.Add(Check("Fade to a second colour (left to right)", item.FillTo is not null,
                                v => { item.FillTo = v ? item.FillTo ?? "#1E63D6" : null; BuildProperties(); }));
                if (item.FillTo is not null) panel.Add(ColorRow("Fade to", item.FillTo, v => item.FillTo = v));
                panel.Add(SliderRow("Solid", item.Opacity, 0, 1, v => item.Opacity = v, v => $"{v * 100:0}%"));
                panel.Add(SliderRow("Rounded corners", item.Corner, 0, 0.1, v => item.Corner = v, v => v == 0 ? "square" : $"{v * 100:0.0}%"));
                break;

            case OverlayKind.Border:
                panel.Add(Hint("A frame round the whole picture."));
                panel.Add(ColorRow("Colour", item.Fill, v => item.Fill = v));
                panel.Add(SliderRow("Thickness", item.Thickness, 0.005, 0.08, v => item.Thickness = v, v => $"{v * 100:0.0}% of the height"));
                panel.Add(SliderRow("Solid", item.Opacity, 0, 1, v => item.Opacity = v, v => $"{v * 100:0}%"));
                panel.Add(SliderRow("Rounded corners", item.Corner, 0, 0.1, v => item.Corner = v, v => v == 0 ? "square" : $"{v * 100:0.0}%"));
                break;

            case OverlayKind.Image:
                var choose = new Button { Content = "Choose another logo…" };
                choose.Click += async (_, _) =>
                {
                    if (await ChooseLogoAsync() is { } data) { item.ImageData = data; Changed(); }
                };
                panel.Add(choose);
                panel.Add(Check("Keep its shape", item.KeepShape, v => item.KeepShape = v));
                panel.Add(SliderRow("Solid", item.Opacity, 0, 1, v => item.Opacity = v, v => $"{v * 100:0}%"));
                panel.Add(Hint("The logo is kept inside the template, so it goes wherever the template goes."));
                break;
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
    private static TextBlock Hint(string text) => new() { Text = text, Classes = { "Hint" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel();
        foreach (var c in controls) { c.Margin = new Thickness(0, 0, 14, 4); row.Children.Add(c); }
        return row;
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); Changed(); };
        return box;
    }

    private Control SliderRow(string label, double value, double min, double max, Action<double> set, Func<double, string> show)
    {
        var text = new TextBlock { Text = show(value), VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
        slider.ValueChanged += (_, _) => { set(slider.Value); text.Text = show(slider.Value); Changed(); };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(text, 1);
        top.Children.Add(text);
        return new StackPanel { Spacing = 0, Children = { top, slider } };
    }

    // A colour: a box to type it in (#RRGGBB) and a row of ready-made ones.
    private Control ColorRow(string label, string value, Action<string> set)
    {
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var box = new TextBox { Text = value, Width = 100 };
        void Show(string hex) { if (Color.TryParse(hex, out var c)) swatch.Background = new SolidColorBrush(c); }
        Show(value);
        box.TextChanged += (_, _) =>
        {
            string hex = (box.Text ?? "").Trim();
            if (!Color.TryParse(hex, out _)) return;
            set(hex);
            Show(hex);
            Changed();
        };
        var swatches = new WrapPanel();
        foreach (var (name, hex) in Swatches)
        {
            var b = new Button { Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4),
                                 Background = new SolidColorBrush(Color.Parse(hex)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            ToolTip.SetTip(b, name);
            b.Click += (_, _) => box.Text = hex;
            swatches.Children.Add(b);
        }
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, box } };
        return new StackPanel { Spacing = 4, Children = { Label(label), line, swatches } };
    }

    // ---- saving ----

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Save(asNew: false);
    private void OnSaveAsClick(object? sender, RoutedEventArgs e) => Save(asNew: true);

    private void Save(bool asNew)
    {
        var toSave = asNew ? _template.Copy((NameBox.Text ?? "").Trim()) : _template;
        toSave.Name = (NameBox.Text ?? "").Trim();
        try
        {
            TemplateStore.Save(toSave);
            if (asNew) Status($"Saved a copy as \"{toSave.Name}\". (You're still changing \"{_template.Name}\".)");
            else
            {
                _dirty = false;
                DeleteButton.IsEnabled = true;
                Status($"Saved \"{toSave.Name}\".");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status(ex.Message, problem: true);
        }
    }

    private bool _deleteArmed;

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_template.Path is null) return;
        if (!_deleteArmed) { _deleteArmed = true; DeleteButton.Content = "Click again to delete"; return; }
        try
        {
            TemplateStore.Delete(_template);
            _dirty = false;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Couldn't delete it: {ex.Message}", problem: true);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_dirty && !_closeArmed)
        {
            _closeArmed = true;
            Status("Not saved yet. Click Save, or Close again to leave without saving.", problem: true);
            return;
        }
        Close();
    }

    private void Status(string message, bool problem = false)
    {
        StatusText.Text = message;
        if (problem) StatusText.Foreground = Brushes.OrangeRed;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }
}
