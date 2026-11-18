using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace MeowFilm.Desktop;

/// <summary>One in-content modal channel. No native popup, clipping copy or secondary HWND.</summary>
public sealed class PlayerDialogHost : Grid
{
    private static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached("Host", typeof(PlayerDialogHost), typeof(PlayerDialogHost), new PropertyMetadata(null));
    private FrameworkElement? panel;
    private Action? closed, closeRequested;
    private uint? dismissPointer;
    private IDisposable? visibility;
    private bool dismissOnOutside = true;
    public bool IsOpen => panel is not null;
    public bool HasOutsidePress => dismissPointer is not null;
    public PlayerDialogHost()
    {
        Visibility = Visibility.Collapsed;
        this.UseResource(StyleProperty, "MeowFilm.DialogHost");
        TabFocusNavigation = KeyboardNavigationMode.Cycle;
        SizeChanged += (_, _) => ConstrainPanel();
        Unloaded += (_, _) => Close();
        PointerPressed += Outside; PointerReleased += Outside;
        PointerCaptureLost += (_, _) => dismissPointer = null;
    }
    public void Attach(DesktopWindow window)
    {
        window.Root.SetValue(HostProperty, this);
        window.Deactivated += (_, _) => CancelPress();
        window.VisibilityChanged += (_, _) => { if (!window.IsVisible) Close(); };
        window.Closed += (_, _) => Close();
    }
    public static PlayerDialogHost? Find(DependencyObject owner) =>
        owner is FrameworkElement { XamlRoot.Content: FrameworkElement root } ? root.GetValue(HostProperty) as PlayerDialogHost : null;
    public void Show(FrameworkElement owner, FrameworkElement content, Action onClosed, bool dismissOnOutside = true, Action? onCloseRequested = null)
    {
        Close();
        panel = content; closed = onClosed; closeRequested = onCloseRequested; this.dismissOnOutside = dismissOnOutside;
        content.HorizontalAlignment = HorizontalAlignment.Center; content.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(content); Visibility = Visibility.Visible; ConstrainPanel();
        var observation = DesktopVisual.ObserveVisibility(owner, visible => { if (!visible) Close(); });
        if (IsOpen) visibility = observation; else observation.Dispose();
    }
    private void ConstrainPanel()
    {
        if (panel is null) return;
        panel.MaxHeight = Math.Max(0, ActualHeight - 32); panel.MaxWidth = Math.Max(0, ActualWidth - 32);
    }
    public bool HandleEscape(KeyRoutedEventArgs args)
    {
        bool handled = HandleEscape(args.Key);
        args.Handled |= handled; return handled;
    }
    public bool HandleEscape(VirtualKey key)
    {
        if (!IsOpen || key != VirtualKey.Escape) return false;
        RequestClose(); return true;
    }
    private void Outside(object sender, PointerRoutedEventArgs args)
    {
        if (panel is null || !HasOutsidePress && args.OriginalSource is DependencyObject node && DesktopVisual.Contains(panel, node)) return;
        HandleOutsidePointer(args, this);
    }
    public void HandleOutsidePointer(PointerRoutedEventArgs args, FrameworkElement bounds)
    {
        if (!IsOpen) return;
        args.Handled = true;
        if (!dismissOnOutside) return;
        var point = args.GetCurrentPoint(bounds);
        if (point.IsInContact)
        { dismissPointer = args.Pointer.PointerId; CapturePointer(args.Pointer); }
        else if (dismissPointer == args.Pointer.PointerId)
        {
            bool inside = new Rect(0, 0, bounds.ActualWidth, bounds.ActualHeight).Contains(point.Position);
            CancelPress(); if (inside) RequestClose();
        }
    }
    private void CancelPress() { dismissPointer = null; ReleasePointerCaptures(); }
    public void RequestClose() { if (IsOpen) { if (closeRequested is { } request) request(); else Close(); } }
    public void Close()
    {
        CancelPress();
        visibility?.Dispose(); visibility = null;
        if (panel is null) return;
        Children.Remove(panel); panel = null; closeRequested = null;
        Visibility = Visibility.Collapsed; dismissOnOutside = true;
        var callback = closed; closed = null; callback?.Invoke();
    }
}
