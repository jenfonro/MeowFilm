using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;

namespace MeowFilm.Desktop;

/// <summary>A nonvisual binding endpoint. Notifications belong to the window, not page layout.</summary>
public sealed class NoticeOutlet : FrameworkElement
{
    public static readonly RoutedEvent NoticeEvent = EventManager.RegisterRoutedEvent(
        "Notice", RoutingStrategy.Bubble, typeof(EventHandler<DesktopNoticeEventArgs>), typeof(NoticeOutlet));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(NoticeOutlet),
        new PropertyMetadata("", (d, _) => ((NoticeOutlet)d).Schedule()));
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    private FrameworkElement? parent;
    private UIElement? root;
    private long generation;
    public NoticeOutlet()
    {
        Visibility = Visibility.Collapsed;
        Focusable = IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            parent = Parent as FrameworkElement ?? VisualTreeHelper.GetParent(this) as FrameworkElement;
            root = this;
            for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element) root = element;
            // Popups have their own visual root, but their logical owner still
            // identifies this window. Never locate a global/current window.
            for (DependencyObject? node = this; node is not null;)
            {
                if (node is Window window) { root = window; break; }
                node = node is Popup popup ? popup.PlacementTarget :
                    LogicalTreeHelper.GetParent(node) ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null);
            }
            if (parent is not null) parent.IsVisibleChanged += ParentVisibilityChanged;
            Schedule();
        };
        Unloaded += (_, _) =>
        {
            generation++;
            if (parent is not null) parent.IsVisibleChanged -= ParentVisibilityChanged;
            Publish(""); parent = null; root = null;
        };
    }
    private void ParentVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Schedule();
    private void Schedule()
    {
        var ticket = ++generation;
        if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ticket == generation && IsLoaded) Publish(parent?.IsVisible == true ? Message : "");
        }));
    }
    private void Publish(string message) => root?.RaiseEvent(new DesktopNoticeEventArgs(this, message ?? ""));
}

public sealed class DesktopNoticeEventArgs(NoticeOutlet source, string message) : RoutedEventArgs(NoticeOutlet.NoticeEvent, source)
{
    public NoticeOutlet Outlet { get; } = source;
    public string Message { get; } = message;
}
