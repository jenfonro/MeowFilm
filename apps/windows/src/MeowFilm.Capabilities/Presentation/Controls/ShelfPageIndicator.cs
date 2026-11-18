using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MeowFilm.Capabilities;

public sealed class ShelfPageIndicator : Canvas
{
    public static readonly DependencyProperty PageCountProperty = DependencyProperty.Register(nameof(PageCount), typeof(int), typeof(ShelfPageIndicator), new PropertyMetadata(0, Changed));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(ShelfPageIndicator), new PropertyMetadata(0d, Changed));
    public int PageCount { get => (int)GetValue(PageCountProperty); set => SetValue(PageCountProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ShelfPageIndicator() { Width = 72; Height = 12; IsHitTestVisible = false; SizeChanged += (_, _) => Refresh(); ActualThemeChanged += (_, _) => Refresh(); }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs _) => ((ShelfPageIndicator)owner).Refresh();
    private void Refresh()
    {
        Children.Clear(); double position = Math.Clamp(Position, 0, Math.Max(0, PageCount - 1));
        AutomationProperties.SetName(this, $"第 {(int)Math.Round(position) + 1} / {PageCount} 组");
        if (PageCount < 2) return;
        for (int page = Math.Max(0, (int)Math.Ceiling(position - 3)); page <= Math.Min(PageCount - 1, (int)Math.Floor(position + 3)); page++)
        {
            double distance = Math.Abs(page - position), radius = 3 - Math.Min(2.5, distance) * .65;
            var dot = new Ellipse { Width = radius * 2, Height = radius * 2,
                Fill = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                Opacity = Math.Max(0, 1 - distance / 3) * (.3 + .7 * Math.Max(0, 1 - distance)) };
            SetLeft(dot, ActualWidth / 2 + (page - position) * 10 - radius); SetTop(dot, ActualHeight / 2 - radius); Children.Add(dot);
        }
    }
}
