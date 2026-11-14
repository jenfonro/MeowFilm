using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public sealed class AspectRatioDecorator : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(Size), typeof(AspectRatioDecorator),
        new FrameworkPropertyMetadata(new Size(1, 1), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Size AspectRatio { get => (Size)GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsInfinity(constraint.Width) ? AspectRatio.Width : constraint.Width;
        var size = new Size(width, width * AspectRatio.Height / AspectRatio.Width);
        Child?.Measure(size);
        return size;
    }
}
