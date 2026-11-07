using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MeowFilm.Desktop;

public sealed class ColorCommittedEventArgs(string color) : EventArgs
{
    public string Color { get; } = color;
}

public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(string), typeof(ColorPicker),
        new PropertyMetadata(AppearanceColors.DefaultColor, SelectedChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorPicker), new PropertyMetadata("自定义颜色"));
    public string SelectedColor { get => (string)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public event EventHandler<ColorCommittedEventArgs>? ColorCommitted;
    private bool updating;
    private double saturation, brightness;
    private Point dragOrigin;

    public ColorPicker()
    {
        InitializeComponent();
        UpdateSelected();
        Unloaded += (_, _) => Editor.IsOpen = false;
        IsVisibleChanged += (_, _) => { if (!IsVisible) Editor.IsOpen = false; };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Editor.IsOpen = false; };
    }

    private static void SelectedChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) =>
        ((ColorPicker)owner).UpdateSelected();
    private void UpdateSelected()
    {
        if (SavedHex is not null && AppearanceColors.TryParse(SelectedColor, out var color))
            SavedHex.Text = AppearanceColors.Hex(color);
    }
    private void SavedHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();
    private void SavedHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { UpdateSelected(); e.Handled = true; }
        else if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; }
    }
    private void CommitHex()
    {
        if (IsEnabled && IsVisible && AppearanceColors.TryParse(SavedHex.Text, out var color) &&
            AppearanceColors.Hex(color) != SelectedColor)
            ColorCommitted?.Invoke(this, new(AppearanceColors.Hex(color)));
        UpdateSelected();
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!AppearanceColors.TryParse(SelectedColor, out var color)) return;
        SetDraft(color);
        Editor.IsOpen = true;
        ColorThumb.Focus();
    }
    private void SetDraft(Color color)
    {
        var hsv = AppearanceColors.Hsv(color);
        updating = true;
        Hue.Value = hsv.Hue; saturation = hsv.Saturation; brightness = hsv.Value;
        HexInput.Text = AppearanceColors.Hex(color);
        updating = false;
        Paint(color);
    }
    private void Paint(Color color)
    {
        HueSurface.Background = new SolidColorBrush(AppearanceColors.FromHsv(Hue.Value, 1, 1));
        DraftSwatch.Background = new SolidColorBrush(color);
        Canvas.SetLeft(Marker, saturation * 280 - 6);
        Canvas.SetTop(Marker, (1 - brightness) * 170 - 6);
        ConfirmButton.IsEnabled = true;
    }
    private void UpdateHsv()
    {
        var color = AppearanceColors.FromHsv(Hue.Value, saturation, brightness);
        updating = true; HexInput.Text = AppearanceColors.Hex(color); updating = false;
        Paint(color);
    }
    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!updating && HexInput is not null) UpdateHsv(); }
    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || ConfirmButton is null) return;
        ConfirmButton.IsEnabled = AppearanceColors.TryParse(HexInput.Text, out var color);
        if (ConfirmButton.IsEnabled)
        {
            var hsv = AppearanceColors.Hsv(color);
            updating = true; Hue.Value = hsv.Hue; updating = false;
            saturation = hsv.Saturation; brightness = hsv.Value;
            Paint(color);
        }
    }
    private void SetPoint(Point point)
    {
        saturation = Math.Clamp(point.X / 280, 0, 1);
        brightness = 1 - Math.Clamp(point.Y / 170, 0, 1);
        UpdateHsv();
    }
    private void Color_DragStarted(object sender, DragStartedEventArgs e)
    { dragOrigin = new(e.HorizontalOffset, e.VerticalOffset); SetPoint(dragOrigin); }
    private void Color_DragDelta(object sender, DragDeltaEventArgs e)
    { SetPoint(dragOrigin + new Vector(e.HorizontalChange, e.VerticalChange)); }
    private void Color_KeyDown(object sender, KeyEventArgs e)
    {
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .1 : .01;
        switch (e.Key)
        {
            case Key.Left: saturation = Math.Max(0, saturation - step); break;
            case Key.Right: saturation = Math.Min(1, saturation + step); break;
            case Key.Up: brightness = Math.Min(1, brightness + step); break;
            case Key.Down: brightness = Math.Max(0, brightness - step); break;
            default: return;
        }
        UpdateHsv(); e.Handled = true;
    }
    private void Editor_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { Editor.IsOpen = false; e.Handled = true; } }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Editor.IsOpen = false;
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!AppearanceColors.TryParse(HexInput.Text, out var color)) return;
        Editor.IsOpen = false;
        ColorCommitted?.Invoke(this, new(AppearanceColors.Hex(color)));
    }
    private void Editor_Closed(object? sender, EventArgs e)
    { if (IsVisible && IsEnabled) OpenButton.Focus(); }
}
