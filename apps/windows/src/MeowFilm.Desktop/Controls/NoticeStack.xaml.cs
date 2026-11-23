using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeowFilm.Desktop;

internal sealed class NoticeEntry(object source, string message, DispatcherTimer? timer, bool persistent)
{
    public object Source { get; } = source;
    public string Message { get; } = message;
    public DispatcherTimer? Timer { get; } = timer;
    public bool Persistent { get; } = persistent;
    public Visibility DismissVisibility => Persistent ? Visibility.Collapsed : Visibility.Visible;
    public bool Hovered { get; set; }
    public Border? Card { get; set; }
}

/// <summary>Per-window, bounded notifications inside the common content composition.</summary>
public sealed partial class NoticeStack : UserControl
{
    private readonly ObservableCollection<NoticeEntry> items = [];

    public NoticeStack()
    {
        InitializeComponent();
        NoticeItems.ItemsSource = items;
        Unloaded += (_, _) => { if (!IsLoaded) Clear(); };
    }

    public void Show(object source, string message, bool persistent = false)
    {
        if (items.Any(item => ReferenceEquals(item.Source, source) && item.Message == message)) return;
        foreach (var old in items.Where(item => ReferenceEquals(item.Source, source) || !persistent && !item.Persistent && item.Message == message).ToArray())
            Remove(old);
        if (string.IsNullOrWhiteSpace(message)) return;
        while (!persistent && items.Count(item => !item.Persistent) >= 3)
            Remove(items.First(item => !item.Persistent));

        var timer = persistent ? null : new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        var entry = new NoticeEntry(source, message, timer, persistent);
        if (timer is not null)
        {
            timer.Tick += (_, _) =>
            {
                if (!entry.Hovered && (entry.Card is null || !DesktopVisual.ContainsFocus(entry.Card)))
                    Remove(entry);
            };
        }
        items.Add(entry);
        timer?.Start();
    }

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { DataContext: NoticeEntry entry } card) return;
        entry.Card = card;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged) &&
            DesktopVisual.Descendant<TextBlock>(card) is { } text)
            FrameworkElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { DataContext: NoticeEntry entry }) entry.Hovered = true;
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { DataContext: NoticeEntry entry }) entry.Hovered = false;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: NoticeEntry entry }) Remove(entry);
    }

    private void Remove(NoticeEntry entry)
    {
        entry.Timer?.Stop();
        entry.Card = null;
        items.Remove(entry);
    }

    internal void Clear()
    {
        foreach (var entry in items.ToArray()) Remove(entry);
    }
}
