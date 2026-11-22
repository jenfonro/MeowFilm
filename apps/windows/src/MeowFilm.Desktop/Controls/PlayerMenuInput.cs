using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

/// <summary>Window-scoped ownership of player menus and queue. WinUI owns pointer routing.</summary>
public sealed class PlayerMenuInput
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(PlayerMenuInput), typeof(PlayerMenuInput), new PropertyMetadata(null));
    private readonly FrameworkElement root;
    private readonly List<Entry> entries = [];
    private Entry? current;
    private Action? queueClose;
    public long Generation { get; private set; }
    private sealed record Entry(Button Button, Func<bool> IsOpen, Action Open, Action Close, bool Queue);
    private PlayerMenuInput(FrameworkElement root)
    {
        this.root = root;
        if (DesktopWindow.GetWindow(root) is { } window)
        {
            window.Deactivated += (_, _) => { if (current?.Queue != true) CloseCurrent(); };
            window.Closed += (_, _) => { CloseCurrent(); entries.Clear(); };
        }
    }
    public static PlayerMenuInput For(FrameworkElement owner)
    {
        var root = owner.XamlRoot?.Content as FrameworkElement ?? owner;
        if (root.GetValue(StateProperty) is PlayerMenuInput input) return input;
        input = new(root); root.SetValue(StateProperty, input); return input;
    }
    public IDisposable Register(Button button, Func<bool> isOpen, Action open, Action close, bool queue = false)
    { var entry = new Entry(button, isOpen, open, close, queue); entries.Add(entry); return new Registration(this, entry); }
    private sealed class Registration(PlayerMenuInput owner, Entry entry) : IDisposable
    {
        public void Dispose() { owner.entries.Remove(entry); if (ReferenceEquals(owner.current, entry)) owner.CloseCurrent(); }
    }
    public void Toggle(Button button)
    {
        if (DesktopModalHost.Find(root) is { IsOpen: true } dialog) { dialog.RequestClose(); return; }
        var entry = entries.Single(item => ReferenceEquals(item.Button, button));
        bool close = ReferenceEquals(current, entry) || entry.IsOpen() || entry.Queue && queueClose is not null;
        CloseCurrent(); if (!close) Open(entry);
    }
    public void Show(Button button)
    {
        if (DesktopModalHost.Find(root)?.IsOpen == true) return;
        var entry = entries.Single(item => ReferenceEquals(item.Button, button));
        if (ReferenceEquals(current, entry)) return;
        CloseCurrent(); Open(entry);
    }
    public bool IsCurrent(Button button) => ReferenceEquals(current?.Button, button);
    public bool IsCurrent(Button button, long generation) => Generation == generation && IsCurrent(button);
    public bool HasCurrent => current is not null;
    private void Open(Entry entry) { current = entry; Generation++; entry.Open(); }
    public void Closed(Button button) { if (ReferenceEquals(current?.Button, button)) { current = null; Generation++; } }
    public void CloseCurrent()
    {
        var previous = current; current = null; Generation++; var queue = queueClose; queueClose = null;
        previous?.Close(); queue?.Invoke();
    }
    public void QueueOpened(Action close) { if (current?.Queue != true) CloseCurrent(); queueClose = close; }
    public void QueueClosed() { queueClose = null; if (current?.Queue == true) { current = null; Generation++; } }
}
