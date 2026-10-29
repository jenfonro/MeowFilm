using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

internal sealed class UserScrollIntent(Dispatcher dispatcher)
{
    private long generation;

    public bool Active { get; private set; }

    public bool Accepts(Visual owner)
    {
        if (Active) return true;
        if (Mouse.Captured is not Thumb thumb) return false;
        var scrollBar = OwningScrollBar(thumb);
        return scrollBar?.Orientation == Orientation.Vertical && owner.IsAncestorOf(scrollBar);
    }

    public void Set(bool active)
    {
        var current = ++generation;
        Active = active && !dispatcher.HasShutdownStarted;
        if (!Active) return;
        _ = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (current == generation) Active = false;
        }));
    }

    public void Clear()
    {
        generation++;
        Active = false;
    }

    private static ScrollBar? OwningScrollBar(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is ScrollBar scrollBar) return scrollBar;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
