using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MeowFilm.Windows.Tests;

// Uses a real hidden HWND and the actual root hit-test/input routes. No SendInput or global hooks.
internal sealed class PlaybackPointer : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly FrameworkElement root;
    private readonly HwndSource source;
    private readonly FieldInfo inputSource = typeof(MouseDevice).GetField("_inputSource", PrivateInstance)!;
    private readonly FieldInfo lastPosition = typeof(MouseDevice).GetField("_lastPosition", PrivateInstance)!;
    private readonly object? previousSource, previousPosition;
    private readonly object provider;
    private readonly FieldInfo active;
    private readonly object? previousActive;
    private readonly MethodInfo changeOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", PrivateInstance,
        null, [typeof(IInputElement), typeof(int)], null)!;
    internal PlaybackPointer(FrameworkElement root, HwndSource source)
    {
        this.root = root; this.source = source;
        previousSource = inputSource.GetValue(Mouse.PrimaryDevice);
        previousPosition = lastPosition.GetValue(Mouse.PrimaryDevice);
        provider = typeof(HwndSource).GetMethod("GetInputProvider", PrivateInstance,
            null, [typeof(Type)], null)!.Invoke(source, [typeof(MouseDevice)])!;
        active = provider.GetType().GetField("_active", PrivateInstance)!;
        previousActive = active.GetValue(provider);
    }
    internal Point Center(FrameworkElement element) => element.TranslatePoint(new(element.ActualWidth / 2, element.ActualHeight / 2), root);
    internal IInputElement? Hit(Point point) => root.InputHitTest(point);
    internal bool Button(Point point, bool down, bool sourceReady = true)
    {
        active.SetValue(provider, true);
        inputSource.SetValue(Mouse.PrimaryDevice, source);
        var client = source.CompositionTarget.TransformToDevice.Transform(point);
        client = new Point(Math.Round(client.X), Math.Round(client.Y));
        lastPosition.SetValue(Mouse.PrimaryDevice, client);
        if ((Mouse.PrimaryDevice.GetPosition(root) - source.CompositionTarget.TransformFromDevice.Transform(client)).Length > .1)
            throw new InvalidOperationException("Synthetic pointer must expose its root coordinates before input routing");
        var hit = Hit(point);
        changeOver.Invoke(Mouse.PrimaryDevice, [hit, Environment.TickCount]);
        var target = Mouse.Captured ?? hit ?? root;
        var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent, Source = sourceReady ? target : null };
        var promoted = down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;
        var key = new object();
        void Promote(object sender, PreProcessInputEventArgs e)
        {
            if (ReferenceEquals(e.StagingItem.Input, input)) e.StagingItem.SetData(key, input);
            else if (ReferenceEquals(e.StagingItem.GetData(key), input) &&
                e.StagingItem.Input is MouseButtonEventArgs mouse && mouse.RoutedEvent == promoted) mouse.Source = target;
        }
        var manager = InputManager.Current;
        manager.PreProcessInput += Promote;
        try { manager.ProcessInput(input); }
        finally { manager.PreProcessInput -= Promote; }
        return input.Handled;
    }
    internal void Click(Point point) { Button(point, true); Button(point, false); }
    public void Dispose()
    {
        if (Mouse.Captured is DependencyObject captured && (ReferenceEquals(captured, root) || root.IsAncestorOf(captured)))
            Mouse.Capture(null);
        changeOver.Invoke(Mouse.PrimaryDevice, [null, Environment.TickCount]);
        inputSource.SetValue(Mouse.PrimaryDevice, previousSource);
        lastPosition.SetValue(Mouse.PrimaryDevice, previousPosition);
        active.SetValue(provider, previousActive);
    }
}
