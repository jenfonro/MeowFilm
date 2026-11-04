using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class ShelfPageIndicator : FrameworkElement
{
    public static readonly DependencyProperty PageCountProperty = DependencyProperty.Register(nameof(PageCount),
        typeof(int), typeof(ShelfPageIndicator), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, UpdateName));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position),
        typeof(double), typeof(ShelfPageIndicator), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, UpdateName));
    private static readonly DependencyProperty ToneProperty = DependencyProperty.Register("Tone",
        typeof(Brush), typeof(ShelfPageIndicator), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public int PageCount { get => (int)GetValue(PageCountProperty); set => SetValue(PageCountProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    public ShelfPageIndicator()
    {
        Width = 72; Height = 12; IsHitTestVisible = false; Focusable = false;
        SetResourceReference(ToneProperty, "MeowFilm.Brush.Text");
    }

    private static void UpdateName(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var indicator = (ShelfPageIndicator)value;
        var page = Math.Clamp((int)Math.Round(indicator.Position) + 1, 1, Math.Max(1, indicator.PageCount));
        AutomationProperties.SetName(indicator, $"第 {page} / {indicator.PageCount} 组");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == DesktopTheme.IsHighContrastProperty) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (PageCount < 2) return;
        var position = Math.Clamp(Position, 0, PageCount - 1);
        var contrast = SystemParameters.HighContrast || DesktopTheme.GetIsHighContrast(this);
        var brush = contrast ? SystemColors.WindowTextBrush : (Brush)GetValue(ToneProperty);
        // Only the nearby dots are drawn, regardless of the number of pages.
        // The rail supplies continuous progress; this control owns no animation clock.
        int first = Math.Max(0, (int)Math.Ceiling(position - 3));
        int last = Math.Min(PageCount - 1, (int)Math.Floor(position + 3));
        for (int page = first; page <= last; page++)
        {
            double distance = Math.Abs(page - position);
            double radius = 3 - Math.Min(2.5, distance) * .65;
            drawing.PushOpacity(contrast ? 1 : Math.Max(0, 1 - distance / 3) * (.3 + .7 * Math.Max(0, 1 - distance)));
            drawing.DrawEllipse(brush, null, new(ActualWidth / 2 + (page - position) * 10, ActualHeight / 2), radius, radius);
            drawing.Pop();
        }
    }
}
