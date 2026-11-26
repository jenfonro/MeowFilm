using Microsoft.UI.Xaml;

namespace MeowFilm.Desktop;

/// <summary>Tree-local one-shot notification endpoint. The shell owns the receiver, never a global event bus.</summary>
public static class NoticeOutlet
{
    private static readonly DependencyProperty ReceiverProperty = DependencyProperty.RegisterAttached(
        "Receiver", typeof(Action<DesktopNoticeEventArgs>), typeof(NoticeOutlet), new PropertyMetadata(null));

    public static void SetReceiver(FrameworkElement root, Action<DesktopNoticeEventArgs>? receiver) =>
        root.SetValue(ReceiverProperty, receiver);

    public static void Post(FrameworkElement owner, string message, object? identity = null)
    {
        if (string.IsNullOrWhiteSpace(message) || !owner.IsLoaded || !DesktopVisual.IsVisible(owner)) return;
        if (owner.XamlRoot?.Content is FrameworkElement root &&
            root.GetValue(ReceiverProperty) is Action<DesktopNoticeEventArgs> receive)
            receive(new(identity ?? new object(), message));
    }
}

public sealed class DesktopNoticeEventArgs(object identity, string message) : EventArgs
{
    public object Identity { get; } = identity;
    public string Message { get; } = message;
}
