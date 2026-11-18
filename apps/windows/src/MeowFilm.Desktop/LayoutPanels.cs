using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MeowFilm.Desktop;

public enum Dock { Left, Top, Right, Bottom }

/// <summary>Edge layout for existing management forms; all children remain WinUI visuals.</summary>
public class DockPanel : Panel
{
    public bool LastChildFill { get; set; } = true;
    public static readonly DependencyProperty DockProperty = DependencyProperty.RegisterAttached("Dock", typeof(Dock), typeof(DockPanel),
        new PropertyMetadata(Dock.Left, (owner, _) => { if (owner is FrameworkElement { Parent: DockPanel panel }) panel.InvalidateMeasure(); }));
    public static Dock GetDock(DependencyObject item) => (Dock)item.GetValue(DockProperty);
    public static void SetDock(DependencyObject item, Dock value) => item.SetValue(DockProperty, value);
    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0, usedWidth = 0, usedHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(Math.Max(0, available.Width - usedWidth), Math.Max(0, available.Height - usedHeight)));
            var size = child.DesiredSize;
            if (GetDock(child) is Dock.Left or Dock.Right)
            { height = Math.Max(height, usedHeight + size.Height); usedWidth += size.Width; }
            else { width = Math.Max(width, usedWidth + size.Width); usedHeight += size.Height; }
        }
        return new Size(Math.Max(width, usedWidth), Math.Max(height, usedHeight));
    }
    protected override Size ArrangeOverride(Size final)
    {
        double left = 0, top = 0, right = final.Width, bottom = final.Height;
        for (int index = 0; index < Children.Count; index++)
        {
            var child = Children[index]; var slot = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            if (!LastChildFill || index != Children.Count - 1)
                switch (GetDock(child))
                {
                    case Dock.Left: slot.Width = Math.Min(slot.Width, child.DesiredSize.Width); left += slot.Width; break;
                    case Dock.Right: slot.Width = Math.Min(slot.Width, child.DesiredSize.Width); right -= slot.Width; slot.X = right; break;
                    case Dock.Top: slot.Height = Math.Min(slot.Height, child.DesiredSize.Height); top += slot.Height; break;
                    case Dock.Bottom: slot.Height = Math.Min(slot.Height, child.DesiredSize.Height); bottom -= slot.Height; slot.Y = bottom; break;
                }
            child.Arrange(slot);
        }
        return final;
    }
}
public sealed class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        foreach (var child in Children) child.Measure(available);
        return Layout(available.Width, false);
    }
    protected override Size ArrangeOverride(Size final) { Layout(final.Width, true); return final; }
    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, row = 0, measured = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width) { measured = Math.Max(measured, x); y += row; x = row = 0; }
            if (arrange) child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width; row = Math.Max(row, size.Height);
        }
        return new Size(Math.Max(measured, x), y + row);
    }
}
