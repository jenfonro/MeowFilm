using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

internal sealed class UserScrollIntent
{
    private sealed class Gesture { internal bool Moved; }
    private readonly ConditionalWeakTable<ScrollViewer, Gesture> gestures = new();
    private Gesture? activeGesture;
    public bool Active { get; private set; }
    public bool Accepts(FrameworkElement owner, bool intermediate)
    {
        bool accepts = Active && owner.IsLoaded && owner.Visibility == Visibility.Visible;
        if (!intermediate) Clear();
        return accepts;
    }
    public void Attach(ScrollViewer scroll)
    {
        if (gestures.TryGetValue(scroll, out _)) return;
        var gesture = new Gesture(); gestures.Add(scroll, gesture);
        scroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            bool scrollbar = false;
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, scroll); node = VisualTreeHelper.GetParent(node))
                if (node is ScrollBar) { scrollbar = true; break; }
            if (!scrollbar && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
            gesture.Moved = false; activeGesture = gesture; Active = false;
        }), true);
        scroll.ViewChanging += (_, e) =>
        {
            if (!ReferenceEquals(activeGesture, gesture) || e.NextView.VerticalOffset == scroll.VerticalOffset) return;
            gesture.Moved = true;
            Active = e.NextView.VerticalOffset > scroll.VerticalOffset;
        };
        scroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { if (!gesture.Moved) Clear(); }), true);
        scroll.PointerCanceled += (_, _) => Clear();
        scroll.Unloaded += (_, _) => Clear();
    }
    public void Set(bool active) { activeGesture = null; Active = active; }
    public void Clear() { activeGesture = null; Active = false; }
}
