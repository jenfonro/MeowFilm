using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MeowFilm.Desktop;

/// <summary>One root's blank-surface gesture, fenced by activation and actual pointer lifetime.</summary>
public sealed class PlayerSurfaceInput : IDisposable
{
    public static readonly DependencyProperty QueueOpenProperty = DependencyProperty.RegisterAttached("QueueOpen", typeof(bool), typeof(PlayerSurfaceInput), new PropertyMetadata(false));
    private static readonly DependencyProperty DismissProperty = DependencyProperty.RegisterAttached("Dismiss", typeof(RoutedEventHandler), typeof(PlayerSurfaceInput), new PropertyMetadata(null));
    public static bool GetQueueOpen(DependencyObject value) =>
        value is FrameworkElement { XamlRoot.Content: FrameworkElement root } ? (bool)root.GetValue(QueueOpenProperty) : (bool)value.GetValue(QueueOpenProperty);
    public static void SetQueueOpen(DependencyObject value, bool open) => value.SetValue(QueueOpenProperty, open);
    public static void AddDismissRequestedHandler(DependencyObject value, RoutedEventHandler handler) => value.SetValue(DismissProperty, (RoutedEventHandler?)value.GetValue(DismissProperty) + handler);
    public static void RemoveDismissRequestedHandler(DependencyObject value, RoutedEventHandler handler) => value.SetValue(DismissProperty, (RoutedEventHandler?)value.GetValue(DismissProperty) - handler);
    private readonly DesktopWindow window;
    private readonly FrameworkElement root;
    private readonly FrameworkElement surface;
    private readonly Func<bool> layersOpen;
    private readonly IDisposable visibility;
    private readonly KeyEventHandler keyDownHandler;
    private readonly PointerEventHandler pointerReleasedHandler;
    private readonly PointerEventHandler pointerCanceledHandler;
    private readonly SurfaceGesture gesture = new();
    private bool pressed, disposed, surfaceVisible;
    public PlayerSurfaceInput(DesktopWindow window, FrameworkElement surface, Func<bool> layersOpen)
    {
        this.window = window; root = window.Root; this.surface = surface; this.layersOpen = layersOpen;
        surfaceVisible = DesktopVisual.IsVisible(surface);
        keyDownHandler = KeyDown;
        pointerReleasedHandler = Released;
        pointerCanceledHandler = Cancelled;
        window.NativeMessage += Message; window.Deactivated += Deactivated;
        root.AddHandler(UIElement.KeyDownEvent, keyDownHandler, true);
        root.AddHandler(UIElement.PointerReleasedEvent, pointerReleasedHandler, true);
        root.AddHandler(UIElement.PointerCanceledEvent, pointerCanceledHandler, true);
        visibility = DesktopVisual.ObserveVisibility(surface, visible =>
        {
            surfaceVisible = visible;
            if (!visible) Cancel();
        });
    }
    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Fast-path filter: ignore high-frequency messages (WM_MOVE, WM_WINDOWPOSCHANGING, WM_MOUSEMOVE, etc.)
        // before touching visual tree visibility or layer state.
        if (message is not (0x21 or 0x201 or 0x203 or 0x100 or 0x104 or 0x1f or 0x6)) return 0;
        if (!surfaceVisible) return 0;
        bool blocked = message is 0x201 or 0x203 && (layersOpen() || GetQueueOpen(surface));
        gesture.Message(message, wParam, lParam, window.IsActive, blocked);
        if (message is 0x201 or 0x203) pressed = true;
        return 0;
    }
    private void KeyDown(object sender, KeyRoutedEventArgs args) => gesture.Keyboard();
    private void Released(object sender, PointerRoutedEventArgs args) => window.DispatcherQueue.TryEnqueue(Cancel);
    private void Cancelled(object sender, PointerRoutedEventArgs args) => Cancel();
    public void Press(bool blank)
    {
        if (disposed || !DesktopVisual.IsVisible(surface)) { Cancel(); return; }
        if (!pressed) gesture.Down(window.IsActive, layersOpen() || GetQueueOpen(surface));
        pressed = true; gesture.Arm(blank);
        if (blank && gesture.CanDismiss && GetQueueOpen(surface))
        {
            gesture.Block();
            (root.GetValue(DismissProperty) as RoutedEventHandler)?.Invoke(surface, new RoutedEventArgs());
        }
    }
    public bool Release(bool blank)
    {
        pressed = false;
        return gesture.Up(!disposed && blank && window.IsActive && DesktopVisual.IsVisible(surface) && !layersOpen() && !GetQueueOpen(surface));
    }
    public void LayerClosed() { if (pressed) gesture.Block(); }
    private void Deactivated(object? sender, EventArgs args) => Cancel();
    public void Cancel() { gesture.Cancel(); pressed = false; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; visibility.Dispose(); Cancel(); window.NativeMessage -= Message; window.Deactivated -= Deactivated;
        root.RemoveHandler(UIElement.KeyDownEvent, keyDownHandler);
        root.RemoveHandler(UIElement.PointerReleasedEvent, pointerReleasedHandler);
        root.RemoveHandler(UIElement.PointerCanceledEvent, pointerCanceledHandler);
    }
}

internal sealed class SurfaceGesture
{
    private bool mouseActivation;
    private bool down;
    private bool allowed;
    private bool armed;
    private bool windowEligible;
    internal bool CanDismiss => down && windowEligible;
    internal void Message(int message, nint wParam, nint lParam, bool active, bool blocked)
    {
        if (message == 0x21)
        {
            int trigger = (int)((lParam.ToInt64() >> 16) & 0xffff);
            mouseActivation = !active && (trigger is 0x201 or 0x203); // left down/double-click only
        }
        else if (message is 0x201 or 0x203) Down(active, blocked); // left down/double-click, including popup capture
        else if (message is 0x100 or 0x104) Keyboard();
        else if (message == 0x1f || message == 0x6 && (wParam.ToInt64() & 0xffff) == 0) Cancel();
    }
    internal void Keyboard() => Cancel();
    internal void Down(bool active, bool blocked)
    {
        down = true; armed = false; windowEligible = active && !mouseActivation;
        allowed = windowEligible && !blocked; mouseActivation = false;
    }
    internal void Arm(bool blank) => armed = down && blank;
    internal void Block() => allowed = false;
    internal bool Up(bool blank)
    { bool result = down && armed && allowed && blank; Cancel(); return result; }
    internal void Cancel() { down = false; allowed = false; armed = false; windowEligible = false; mouseActivation = false; }
}
