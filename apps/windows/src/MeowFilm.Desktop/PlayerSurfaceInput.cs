using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MeowFilm.Desktop;

/// <summary>One window's blank-surface gesture; never consumes a control's native input.</summary>
public sealed class PlayerSurfaceInput : IDisposable
{
    public static readonly DependencyProperty QueueOpenProperty = DependencyProperty.RegisterAttached(
        "QueueOpen", typeof(bool), typeof(PlayerSurfaceInput), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetQueueOpen(DependencyObject value) => (bool)value.GetValue(QueueOpenProperty);
    public static void SetQueueOpen(DependencyObject value, bool open) => value.SetValue(QueueOpenProperty, open);
    public static readonly RoutedEvent DismissRequestedEvent = EventManager.RegisterRoutedEvent(
        "DismissRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PlayerSurfaceInput));
    public static void AddDismissRequestedHandler(DependencyObject value, RoutedEventHandler handler) => ((UIElement)value).AddHandler(DismissRequestedEvent, handler);
    public static void RemoveDismissRequestedHandler(DependencyObject value, RoutedEventHandler handler) => ((UIElement)value).RemoveHandler(DismissRequestedEvent, handler);

    private readonly Window window;
    private readonly FrameworkElement surface;
    private readonly Func<bool> layersOpen;
    private readonly Dictionary<Visual, HwndSource> sources = [];
    private readonly SurfaceGesture gesture = new();
    private bool nativeDown;
    private bool disposed;

    public PlayerSurfaceInput(Window window, FrameworkElement surface, Func<bool> layersOpen)
    {
        this.window = window; this.surface = surface; this.layersOpen = layersOpen;
        window.SourceInitialized += SourceInitialized;
        window.Deactivated += Deactivated;
        InputManager.Current.PreProcessInput += BeforeInput;
        InputManager.Current.PostProcessInput += AfterInput;
        Observe(window);
    }
    private void SourceInitialized(object? sender, EventArgs e) => Observe(window);
    public void Observe(Visual visual)
    {
        if (disposed) return;
        Forget(visual);
        if (PresentationSource.FromVisual(visual) is HwndSource source)
        { sources.Add(visual, source); source.AddHook(Message); }
    }
    public void Forget(Visual visual)
    {
        if (sources.Remove(visual, out var source) && !source.IsDisposed) source.RemoveHook(Message);
    }
    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!surface.IsVisible) return 0;
        if ((message is 0x201 or 0x203) && sources.Values.FirstOrDefault(value => value.Handle == hwnd) is { RootVisual: Visual root } source)
        {
            var point = new Point((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff));
            var logical = source.CompositionTarget.TransformFromDevice.Transform(point);
            PlayerMenuInput.For(window).NativeDown(root.PointToScreen(logical));
        }
        gesture.Message(message, wParam, lParam, window.IsActive, layersOpen() || GetQueueOpen(surface));
        if (message is 0x201 or 0x203) nativeDown = true;
        else if (message == 0x1f || message == 0x6 && (wParam.ToInt64() & 0xffff) == 0) nativeDown = false;
        return 0;
    }
    private void BeforeInput(object sender, PreProcessInputEventArgs e)
    {
        if (disposed || !surface.IsVisible) return;
        if (e.StagingItem.Input is MouseButtonEventArgs menuMouse &&
            (PlayerMenuInput.For(window).HasPendingGesture ||
            (menuMouse.Source as Visual ?? menuMouse.MouseDevice.DirectlyOver as Visual) is { } visual &&
            PresentationSource.FromVisual(visual) is HwndSource inputSource &&
            (sources.Values.Contains(inputSource) || ReferenceEquals(inputSource, PresentationSource.FromVisual(surface)))))
            PlayerMenuInput.For(window).Preview(menuMouse);
        if (e.StagingItem.Input is MouseButtonEventArgs mouse && mouse.ChangedButton == MouseButton.Left &&
            mouse.RoutedEvent == Mouse.PreviewMouseDownEvent)
        {
            if (!nativeDown) gesture.Down(window.IsActive, layersOpen() || GetQueueOpen(surface));
            nativeDown = false;
        }
    }
    private void AfterInput(object sender, ProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is MouseButtonEventArgs mouse && mouse.ChangedButton == MouseButton.Left &&
            mouse.RoutedEvent == Mouse.MouseUpEvent) Cancel();
    }
    public void Press(bool blank)
    {
        gesture.Arm(blank);
        if (blank && gesture.CanDismiss && GetQueueOpen(surface))
        {
            gesture.Block();
            surface.RaiseEvent(new RoutedEventArgs(DismissRequestedEvent));
        }
    }
    public bool Release(bool blank) => gesture.Up(blank && window.IsActive && surface.IsVisible &&
        !layersOpen() && !GetQueueOpen(surface));
    public void LayerClosed()
    {
        // A close during a pressed gesture cannot grant admission, including synthetic WPF input.
        if (Mouse.LeftButton == MouseButtonState.Pressed) gesture.Block();
    }
    private void Deactivated(object? sender, EventArgs e) => Cancel();
    public void Cancel() { gesture.Cancel(); nativeDown = false; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Cancel();
        window.SourceInitialized -= SourceInitialized; window.Deactivated -= Deactivated;
        InputManager.Current.PreProcessInput -= BeforeInput;
        InputManager.Current.PostProcessInput -= AfterInput;
        foreach (var source in sources.Values) if (!source.IsDisposed) source.RemoveHook(Message);
        sources.Clear();
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
