using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

/// <summary>One window's footer menu entries, sharing one pressed gesture across native popup dismissal.</summary>
public sealed class PlayerMenuInput
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(PlayerMenuInput), typeof(PlayerMenuInput));
    private readonly FrameworkElement window;
    private readonly List<Entry> entries = [];
    private Entry? current, pressed;
    private Action? queueClose;
    private bool closeOnly, captured;
    private MouseButtonEventArgs? lastInput;
    private (Entry Entry, bool CloseOnly)? nativePress;
    private long menuRequest;

    private sealed record Entry(Button Button, Func<bool> IsOpen, Action Open, Action Close, bool Queue);
    private PlayerMenuInput(FrameworkElement window)
    {
        this.window = window;
        if (window is Window owner)
        {
            owner.Deactivated += (_, _) =>
            {
                CancelPress();
                if (current?.Queue != true) CloseCurrent();
            };
            owner.Closed += (_, _) => { CancelPress(); entries.Clear(); current = null; queueClose = null; lastInput = null; };
        }
    }
    public static PlayerMenuInput For(FrameworkElement window)
    {
        if (window.GetValue(StateProperty) is PlayerMenuInput input) return input;
        input = new(window); window.SetValue(StateProperty, input); return input;
    }
    public IDisposable Register(Button button, Func<bool> isOpen, Action open, Action close, bool queue = false)
    {
        var entry = new Entry(button, isOpen, open, close, queue);
        entries.Add(entry);
        return new Registration(this, entry);
    }
    private sealed class Registration(PlayerMenuInput owner, Entry entry) : IDisposable
    {
        public void Dispose()
        {
            owner.entries.Remove(entry);
            if (ReferenceEquals(owner.current, entry)) owner.CloseCurrent();
            if (ReferenceEquals(owner.pressed, entry)) owner.CancelPress();
            if (ReferenceEquals(owner.nativePress?.Entry, entry)) owner.nativePress = null;
        }
    }
    private FrameworkElement PointerRoot => window is Window { Content: FrameworkElement content } ? content : window;
    private Entry? Hit(Point point) => entries.LastOrDefault(entry => entry.Button.IsVisible && entry.Button.IsEnabled &&
        new Rect(entry.Button.TranslatePoint(new Point(), PointerRoot), entry.Button.RenderSize).Contains(point));

    // Called by the existing player HWND observer, before WPF can close a captured native menu.
    internal void NativeDown(Point screen)
    {
        if (PlayerDialogHost.Find(window)?.IsOpen == true) return;
        var entry = Hit(PointerRoot.PointFromScreen(screen));
        nativePress = entry is null ? null : (entry, ReferenceEquals(current, entry) || entry.IsOpen() || entry.Queue && queueClose is not null);
    }
    internal bool HasPendingGesture => nativePress is not null || pressed is not null;
    internal void Preview(MouseButtonEventArgs e)
    {
        if (ReferenceEquals(lastInput, e) || e.ChangedButton != MouseButton.Left || PlayerDialogHost.Find(window)?.IsOpen == true) return;
        lastInput = e;
        if (e.RoutedEvent == Mouse.PreviewMouseDownEvent)
        {
            var target = nativePress?.Entry ?? Hit(e.GetPosition(PointerRoot));
            if (target is null) { nativePress = null; return; }
            closeOnly = nativePress?.CloseOnly ?? (ReferenceEquals(current, target) || target.IsOpen() || target.Queue && queueClose is not null);
            nativePress = null;
            pressed = target;
            e.Handled = true;
            CloseCurrent();
            // Capture only this in-app pointer gesture, never while the menu is idle.
            PointerRoot.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(LostCapture), true);
            captured = Mouse.Capture(PointerRoot, CaptureMode.SubTree);
            target.Button.Focus();
        }
        else if (e.RoutedEvent == Mouse.PreviewMouseUpEvent && pressed is { } target)
        {
            e.Handled = true;
            bool open = !closeOnly && ReferenceEquals(Hit(e.GetPosition(PointerRoot)), target);
            CancelPress();
            if (open)
            {
                // Finish routing this release before a new native menu captures the mouse.
                // Otherwise WPF can deliver the opening release to the new menu as an outside click.
                current = target;
                long request = ++menuRequest;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (request == menuRequest && ReferenceEquals(current, target) && target.Button.IsVisible && target.Button.IsEnabled &&
                        PlayerDialogHost.Find(window)?.IsOpen != true) target.Open();
                }));
            }
        }
    }
    public void Toggle(Button button)
    {
        if (PlayerDialogHost.Find(window)?.IsOpen == true) { PlayerDialogHost.Find(window)!.Close(); return; }
        var target = entries.Single(entry => ReferenceEquals(entry.Button, button));
        bool close = ReferenceEquals(current, target) || target.IsOpen() || target.Queue && queueClose is not null;
        CloseCurrent();
        if (!close) Open(target);
    }
    public void Show(Button button)
    {
        if (PlayerDialogHost.Find(window)?.IsOpen == true) return;
        var target = entries.Single(entry => ReferenceEquals(entry.Button, button));
        // ContextMenuOpening is a request to show, not a second left-button toggle.
        if (ReferenceEquals(current, target)) return;
        CloseCurrent();
        Open(target);
    }
    public bool IsCurrent(Button button) => ReferenceEquals(current?.Button, button);
    public bool HasCurrent => current is not null;
    private void Open(Entry entry) { current = entry; entry.Open(); }
    public void Closed(Button button)
    { if (ReferenceEquals(current?.Button, button)) { current = null; menuRequest++; } }
    public void CloseCurrent()
    {
        menuRequest++;
        var previous = current; current = null;
        var queue = queueClose; queueClose = null;
        previous?.Close();
        queue?.Invoke();
    }
    public void QueueOpened(Action close)
    {
        if (current?.Queue != true) CloseCurrent();
        queueClose = close;
    }
    public void QueueClosed()
    { queueClose = null; if (current?.Queue == true) current = null; }
    public void CancelPress()
    {
        pressed = null; nativePress = null;
        PointerRoot.RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(LostCapture));
        bool release = captured && ReferenceEquals(Mouse.Captured, PointerRoot);
        captured = false;
        if (release) Mouse.Capture(null);
    }
    private void LostCapture(object sender, MouseEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, PointerRoot)) CancelPress(); }
}
