using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

[ContentProperty(Name = nameof(Child))]
public sealed class AspectRatioDecorator : Panel
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(Size), typeof(AspectRatioDecorator),
        new PropertyMetadata(new Size(1, 1), (owner, _) => ((UIElement)owner).InvalidateMeasure()));

    public Size AspectRatio { get => (Size)GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }

    public UIElement? Child
    {
        get => Children.Count > 0 ? Children[0] : null;
        set
        {
            if (ReferenceEquals(Child, value)) return;
            Children.Clear();
            if (value is not null) Children.Add(value);
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsInfinity(constraint.Width) ? AspectRatio.Width : constraint.Width;
        var size = new Size(width, width * AspectRatio.Height / AspectRatio.Width);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }
}
