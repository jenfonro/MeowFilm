using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

public sealed class MusicWrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, height = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize); var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { width = Math.Max(width, x); y += height; x = height = 0; }
            x += size.Width; height = Math.Max(height, size.Height);
        }
        return new(Math.Max(width, x), y + height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, height = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += height; x = height = 0; }
            child.Arrange(new Rect(x, y, size.Width, size.Height)); x += size.Width; height = Math.Max(height, size.Height);
        }
        return finalSize;
    }
}
