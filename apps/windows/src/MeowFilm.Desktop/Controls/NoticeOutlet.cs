using Microsoft.UI.Xaml;

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
            visibility = DesktopVisual.ObserveVisibility(this, _ => Schedule());
        };
        Unloaded += (_, _) =>
        {
            generation++;
            visibility?.Dispose(); visibility = null;
            Send(""); root = null;
        };
    }
    public void Post(string message) { if (root is not null) Publish(root, new(this, message, true)); }
    private void Queue() { pending = true; Schedule(); }
    private void Schedule()
    {
        long ticket = ++generation;
        if (!IsLoaded) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ticket != generation || !IsLoaded) return;
            if (!DesktopVisual.IsVisible(this)) { Send(""); return; }
            if (!pending) return;
            pending = false; Send(Message);
        });
    }
    private void Send(string message) { if (root is not null) Publish(root, new(this, message)); }
}
public sealed class DesktopNoticeEventArgs(NoticeOutlet source, string message, bool transient = false) : EventArgs
{
    public NoticeOutlet Outlet { get; } = source;
    public object Identity { get; } = transient ? new object() : source;
    public string Message { get; } = message;
}
