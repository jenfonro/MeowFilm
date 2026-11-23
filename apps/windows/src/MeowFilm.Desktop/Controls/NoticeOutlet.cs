using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

/// <summary>A tree-local notification endpoint. The shell owns the receiver, never a global event bus.</summary>
public sealed class NoticeOutlet : FrameworkElement
{
    private static readonly DependencyProperty ReceiverProperty = DependencyProperty.RegisterAttached("Receiver", typeof(Action<DesktopNoticeEventArgs>), typeof(NoticeOutlet), new PropertyMetadata(null));
    public static void SetReceiver(FrameworkElement root, Action<DesktopNoticeEventArgs>? receiver) => root.SetValue(ReceiverProperty, receiver);
    public static void Publish(FrameworkElement owner, DesktopNoticeEventArgs notice)
    {
        if (owner.XamlRoot?.Content is FrameworkElement root && root.GetValue(ReceiverProperty) is Action<DesktopNoticeEventArgs> receive) receive(notice);
    }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(NoticeOutlet), new PropertyMetadata("", (value, _) => ((NoticeOutlet)value).Queue()));
    public string Message { get => (string)GetValue(MessageProperty); set { if (Message == value) Queue(); else SetValue(MessageProperty, value); } }
    private FrameworkElement? root;
    private IDisposable? visibility;
    private long generation;
    private bool pending;
    public NoticeOutlet()
    {
        IsHitTestVisible = false;
        Width = Height = 0;
        Loaded += (_, _) =>
        {
            root = XamlRoot?.Content as FrameworkElement;
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, _ => Schedule());
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded)
            {
                root = XamlRoot?.Content as FrameworkElement;
                visibility?.Dispose();
                visibility = DesktopVisual.ObserveVisibility(this, _ => Schedule());
                return;
            }
            generation++;
            visibility?.Dispose(); visibility = null;
            Send(""); root = null;
        };
    }
    public void Post(string message)
    {
        if (root is not null && IsVisibleInViewport())
            Publish(root, new(this, message, true));
    }
    private void Queue() { pending = true; Schedule(); }
    private void Schedule()
    {
        long ticket = ++generation;
        if (!IsLoaded) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ticket != generation || !IsLoaded) return;
            if (!IsVisibleInViewport()) { Send(""); return; }
            if (!pending) return;
            pending = false; Send(Message);
        });
    }
    private void Send(string message) { if (root is not null) Publish(root, new(this, message)); }

    /// <summary>
    /// On-demand ancestor visibility and ScrollViewer viewport intersection check.
    /// Prevents scrolled-off sections from pushing stale notices into the global overlay
    /// without attaching any ScrollViewer.ViewChanged listener.
    /// </summary>
    private bool IsVisibleInViewport()
    {
        if (!DesktopVisual.IsVisible(this)) return false;
        var parent = VisualTreeHelper.GetParent(this) as FrameworkElement;
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Opacity <= 0.001) return false;
            if (current is ScrollViewer scroller)
            {
                if (scroller.ActualWidth <= 0 || scroller.ActualHeight <= 0) return false;
                try
                {
                    FrameworkElement anchor = parent is not null &&
                        !ReferenceEquals(parent, scroller.Content) &&
                        !ReferenceEquals(parent, scroller) &&
                        (parent.ActualWidth > 0 || parent.ActualHeight > 0)
                        ? parent
                        : this;
                    Rect bounds = anchor.TransformToVisual(scroller).TransformBounds(new Rect(
                        0, 0, Math.Max(1, anchor.ActualWidth), Math.Max(1, anchor.ActualHeight)));
                    if (bounds.Bottom < 0 || bounds.Top > scroller.ActualHeight ||
                        bounds.Right < 0 || bounds.Left > scroller.ActualWidth)
                        return false;
                }
                catch { return false; }
            }
        }
        return true;
    }
}
public sealed class DesktopNoticeEventArgs(NoticeOutlet source, string message, bool transient = false) : EventArgs
{
    public NoticeOutlet Outlet { get; } = source;
    public object Identity { get; } = transient ? new object() : source;
    public string Message { get; } = message;
}
