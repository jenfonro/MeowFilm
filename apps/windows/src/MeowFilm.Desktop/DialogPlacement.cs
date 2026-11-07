using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MeowFilm.Desktop;

/// <summary>Centers app dialogs on the owning window, while retaining the anchor's context and focus.</summary>
public static class DialogPlacement
{
    public static readonly DependencyProperty CenteredProperty = DependencyProperty.RegisterAttached(
        "Centered", typeof(bool), typeof(DialogPlacement), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(DialogPlacement));
    public static bool GetCentered(DependencyObject target) => (bool)target.GetValue(CenteredProperty);
    public static void SetCentered(DependencyObject target, bool value) => target.SetValue(CenteredProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Popup popup) return;
        if (popup.GetValue(StateProperty) is State old) old.Dispose();
        popup.SetValue(StateProperty, (bool)e.NewValue ? new State(popup) : null);
    }

    private sealed class State : IDisposable
    {
        private readonly Popup popup;
        private FrameworkElement? anchor, root;
        private Window? window;
        private UIElement? child;

        internal State(Popup popup)
        {
            this.popup = popup;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = Place;
            popup.Opened += Opened;
            popup.Closed += Closed;
        }

        private CustomPopupPlacement[] Place(Size size, Size targetSize, Point offset)
        {
            var target = popup.PlacementTarget as FrameworkElement;
            var surface = target is null ? null : Window.GetWindow(target)?.Content as FrameworkElement ??
                PresentationSource.FromVisual(target)?.RootVisual as FrameworkElement;
            var point = surface is null || target is null
                ? new Point((targetSize.Width - size.Width) / 2, (targetSize.Height - size.Height) / 2)
                : surface.TranslatePoint(new Point((surface.ActualWidth - size.Width) / 2,
                    (surface.ActualHeight - size.Height) / 2), target);
            return [new(point, PopupPrimaryAxis.None)];
        }

        private void Opened(object? sender, EventArgs e)
        {
            Detach();
            anchor = popup.PlacementTarget as FrameworkElement;
            if (anchor is not null)
            {
                anchor.Unloaded += OwnerUnloaded;
                anchor.IsVisibleChanged += OwnerVisibilityChanged;
                window = Window.GetWindow(anchor);
                root = window?.Content as FrameworkElement ??
                    PresentationSource.FromVisual(anchor)?.RootVisual as FrameworkElement;
            }
            if (root is not null) root.SizeChanged += Resized;
            if (window is not null)
            {
                window.LocationChanged += Moved;
                window.Closed += OwnerClosed;
            }
            child = popup.Child;
            if (child is not null) child.KeyDown += KeyDown;
        }

        private void Moved(object? sender, EventArgs e) => Reposition();
        private void Resized(object sender, SizeChangedEventArgs e) => Reposition();
        private void Reposition()
        {
            // Popup has no public reposition method. Changing its offset invalidates native placement.
            double offset = popup.HorizontalOffset;
            popup.SetCurrentValue(Popup.HorizontalOffsetProperty, offset + 1);
            popup.SetCurrentValue(Popup.HorizontalOffsetProperty, offset);
        }
        private void OwnerUnloaded(object sender, RoutedEventArgs e) => popup.IsOpen = false;
        private void OwnerClosed(object? sender, EventArgs e) => popup.IsOpen = false;
        private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        { if (!(bool)e.NewValue) popup.IsOpen = false; }
        private void KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            popup.IsOpen = false;
        }
        private void Closed(object? sender, EventArgs e) => Detach();
        private void Detach()
        {
            if (anchor is not null)
            {
                anchor.Unloaded -= OwnerUnloaded;
                anchor.IsVisibleChanged -= OwnerVisibilityChanged;
            }
            if (root is not null) root.SizeChanged -= Resized;
            if (window is not null)
            {
                window.LocationChanged -= Moved;
                window.Closed -= OwnerClosed;
            }
            if (child is not null) child.KeyDown -= KeyDown;
            anchor = root = null;
            window = null;
            child = null;
        }
        public void Dispose()
        {
            Detach();
            popup.Opened -= Opened;
            popup.Closed -= Closed;
            popup.CustomPopupPlacementCallback = null;
            popup.ClearValue(Popup.PlacementProperty);
        }
    }
}
