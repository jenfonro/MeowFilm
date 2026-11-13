using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MeowFilm.Desktop;

/// <summary>The shell's in-content channel dialog. No native popup or desktop-wide input subscription.</summary>
public sealed class PlayerDialogHost : Grid
{
    private static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(PlayerDialogHost), typeof(PlayerDialogHost));
    private FrameworkElement? anchor, panel;
    private Action? closed;
    private MouseButton? dismissButton;
    private bool dismissOnOutside = true;
    public bool IsOpen => panel is not null;
    public bool HasOutsidePress => dismissButton is not null;

    public PlayerDialogHost()
    {
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
        ClipToBounds = true;
        FocusManager.SetIsFocusScope(this, true);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        SizeChanged += (_, _) => ConstrainPanel();
        Unloaded += (_, _) => Close();
    }

    public void Attach(Window window)
    {
        window.SetValue(HostProperty, this);
        window.Deactivated += (_, _) => CancelPress();
        window.IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) Close(); };
        window.Closed += (_, _) => Close();
    }

    public static PlayerDialogHost? Find(DependencyObject owner) =>
        Window.GetWindow(owner)?.GetValue(HostProperty) as PlayerDialogHost;

    public void Show(FrameworkElement owner, FrameworkElement content, Action onClosed, bool dismissOnOutside = true)
    {
        Close();
        anchor = owner; panel = content; closed = onClosed; this.dismissOnOutside = dismissOnOutside;
        owner.Unloaded += OwnerUnloaded;
        owner.IsVisibleChanged += OwnerVisibilityChanged;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(content);
        Visibility = Visibility.Visible;
        ConstrainPanel();
    }

    private void ConstrainPanel()
    {
        if (panel is null) return;
        panel.MaxHeight = Math.Max(0, ActualHeight - 32);
        panel.MaxWidth = Math.Max(0, ActualWidth - 32);
    }
    private void OwnerUnloaded(object sender, RoutedEventArgs e) => Close();
    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (!(bool)e.NewValue) Close(); }

    public bool HandleEscape(KeyEventArgs e)
    {
        if (!IsOpen || e.Key != Key.Escape) return false;
        e.Handled = true;
        Close();
        return true;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (panel is null ||
            e.OriginalSource is DependencyObject node && (ReferenceEquals(node, panel) || panel.IsAncestorOf(node))) return;
        if (!dismissOnOutside)
        {
            e.Handled = true;
            return;
        }
        HandleOutsidePointer(e, this);
    }
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (!dismissOnOutside && panel is not null &&
            e.OriginalSource is DependencyObject node && (ReferenceEquals(node, panel) || panel.IsAncestorOf(node))) return;
        HandleOutsidePointer(e, this);
    }
    // The shell also routes application header controls here; native caption/drag areas remain untouched.
    public void HandleOutsidePointer(MouseButtonEventArgs e, FrameworkElement bounds)
    {
        if (!IsOpen) return;
        if (!dismissOnOutside)
        {
            e.Handled = true;
            return;
        }
        if (e.RoutedEvent == Mouse.PreviewMouseDownEvent)
        {
            // Keep the barrier until release so neither half of the gesture reaches underlying controls.
            e.Handled = true;
            dismissButton = e.ChangedButton;
            Mouse.Capture(this, CaptureMode.SubTree);
        }
        else if (e.RoutedEvent == Mouse.PreviewMouseUpEvent && dismissButton == e.ChangedButton)
        {
            e.Handled = true;
            bool inside = new Rect(bounds.RenderSize).Contains(e.GetPosition(bounds));
            CancelPress();
            if (inside) Close();
        }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); dismissButton = null; }
    private void CancelPress()
    {
        dismissButton = null;
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }
    public void Close()
    {
        CancelPress();
        if (anchor is not null)
        {
            anchor.Unloaded -= OwnerUnloaded;
            anchor.IsVisibleChanged -= OwnerVisibilityChanged;
        }
        anchor = null;
        if (panel is null) return;
        Children.Remove(panel);
        panel = null;
        Visibility = Visibility.Collapsed;
        dismissOnOutside = true;
        FocusManager.SetFocusedElement(this, null);
        var callback = closed; closed = null;
        callback?.Invoke();
    }
}
