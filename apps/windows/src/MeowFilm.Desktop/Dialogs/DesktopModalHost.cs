using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace MeowFilm.Desktop;

public enum DesktopDialogResult { None, Primary, Secondary }

/// <summary>
/// Single application-wide in-window modal channel. Perfectly centered in the window with zero
/// secondary HWND, zero WinUI ContentDialog offset, and zero nested chrome frames.
/// </summary>
public sealed class DesktopModalHost : Grid
{
    private static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(DesktopModalHost), typeof(DesktopModalHost), new PropertyMetadata(null));

    private sealed class ActiveModal
    {
        public required FrameworkElement Owner;
        public required FrameworkElement Content;
        public Action? Closed;
        public Action? CloseRequested;
        public Func<bool>? CanRestoreFocus;
        public UIElement? PriorFocus;
        public IDisposable? VisibilityObserver;
        public bool DismissOnOutside;
        public bool PropagatedDataContext;
        public object? OriginalMaxWidth;
        public object? OriginalMaxHeight;
    }

    private readonly List<ActiveModal> modals = [];
    private ActiveModal? active => modals.Count > 0 ? modals[^1] : null;
    private uint? dismissPointer;

    public bool IsOpen => active is not null;
    public int StackDepth => modals.Count;
    public bool HasOutsidePress => dismissPointer is not null;
    public FrameworkElement? ActiveContent => active?.Content;
    public event EventHandler? OpenStateChanged;

    public DesktopModalHost()
    {
        Visibility = Visibility.Collapsed;
        this.UseResource(StyleProperty, "MeowFilm.DialogHost");
        TabFocusNavigation = KeyboardNavigationMode.Cycle;
        SizeChanged += (_, _) => ConstrainPanel();
        Unloaded += (_, _) => CloseAll();
        PointerPressed += (_, args) => Outside(args, pressed: true);
        PointerReleased += (_, args) => Outside(args, pressed: false);
        PointerCaptureLost += (_, _) => dismissPointer = null;
    }

    public void Attach(DesktopWindow window)
    {
        window.Root.SetValue(HostProperty, this);
        window.Deactivated += (_, _) => CancelPress();
        window.VisibilityChanged += (_, _) => { if (!window.IsVisible) CloseAll(); };
        window.Closed += (_, _) => CloseAll();
    }

    public static DesktopModalHost? Find(DependencyObject owner)
    {
        if (owner is FrameworkElement { XamlRoot.Content: FrameworkElement root } &&
            root.GetValue(HostProperty) is DesktopModalHost host)
            return host;
        return DesktopWindow.GetWindow(owner)?.Root.GetValue(HostProperty) as DesktopModalHost;
    }

    public void Show(
        FrameworkElement owner,
        FrameworkElement content,
        Action? onClosed = null,
        bool dismissOnOutside = true,
        Action? onCloseRequested = null,
        Func<bool>? canRestoreFocus = null,
        bool stack = false)
    {
        if (!stack) CloseAll();
        else if (active is { } previous)
        {
            previous.Content.IsHitTestVisible = false;
            previous.Content.Opacity = 0.45;
        }
        bool propagated = false;
        if (content.ReadLocalValue(DataContextProperty) == DependencyProperty.UnsetValue && owner.DataContext is not null)
        {
            content.DataContext = owner.DataContext;
            propagated = true;
        }
        var modal = new ActiveModal
        {
            Owner = owner,
            Content = content,
            Closed = onClosed,
            CloseRequested = onCloseRequested,
            CanRestoreFocus = canRestoreFocus,
            PriorFocus = DesktopVisual.Focused(owner) as UIElement,
            DismissOnOutside = dismissOnOutside,
            PropagatedDataContext = propagated,
            OriginalMaxWidth = content.ReadLocalValue(MaxWidthProperty),
            OriginalMaxHeight = content.ReadLocalValue(MaxHeightProperty)
        };
        modals.Add(modal);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(content);
        Visibility = Visibility.Visible;
        ConstrainPanel();
        var observation = DesktopVisual.ObserveVisibility(owner, visible =>
        {
            if (!visible && modals.Contains(modal)) Close(content);
        });
        if (modals.Contains(modal))
            modal.VisibilityObserver = observation;
        else
            observation.Dispose();
        OpenStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static Task ShowSurfaceAsync(
        FrameworkElement owner,
        FrameworkElement content,
        bool dismissOnOutside = true,
        Action? onOpened = null,
        Func<bool>? canRestoreFocus = null,
        CancellationToken cancellationToken = default,
        bool stack = false)
    {
        var host = Find(owner) ?? throw new InvalidOperationException("MODAL_HOST_UNAVAILABLE");
        return host.PresentSurfaceAsync(owner, content, dismissOnOutside, onOpened, canRestoreFocus, cancellationToken, stack);
    }

    public Task PresentSurfaceAsync(
        FrameworkElement owner,
        FrameworkElement content,
        bool dismissOnOutside = true,
        Action? onOpened = null,
        Func<bool>? canRestoreFocus = null,
        CancellationToken cancellationToken = default,
        bool stack = false)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Show(owner, content, onClosed: () =>
        {
            registration.Dispose();
            completion.TrySetResult();
        }, dismissOnOutside: dismissOnOutside, canRestoreFocus: canRestoreFocus, stack: stack);
        if (cancellationToken.CanBeCanceled)
            registration = cancellationToken.Register(() => DispatcherQueue?.TryEnqueue(() => Close(content)));
        if (onOpened is not null)
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (ReferenceEquals(active?.Content, content)) onOpened();
            });
        return completion.Task;
    }

    public static Task<DesktopDialogResult> ShowDialogAsync(
        FrameworkElement owner,
        DesktopDialogRequest request,
        CancellationToken cancellationToken = default,
        bool stack = false)
    {
        var host = Find(owner) ?? throw new InvalidOperationException("MODAL_HOST_UNAVAILABLE");
        return host.PresentDialogAsync(owner, request, cancellationToken, stack);
    }

    public async Task<DesktopDialogResult> PresentDialogAsync(
        FrameworkElement owner,
        DesktopDialogRequest request,
        CancellationToken cancellationToken = default,
        bool stack = false)
    {
        var built = DesktopModalDialog.Build(request, Close);
        await PresentSurfaceAsync(
            owner,
            built.Surface,
            dismissOnOutside: request.DismissOnOutside,
            onOpened: () =>
            {
                request.OnOpened?.Invoke();
                if (request.OnOpened is null)
                    built.FocusInitial();
            },
            canRestoreFocus: request.CanRestoreFocus,
            cancellationToken: cancellationToken,
            stack: stack);
        return built.Result;
    }

    private void ConstrainPanel()
    {
        double availHeight = Math.Max(0, ActualHeight - 32);
        double availWidth = Math.Max(0, ActualWidth - 32);
        foreach (var current in modals)
        {
            double maxHeight = availHeight;
            double maxWidth = availWidth;
            if (current.OriginalMaxHeight is double origH && !double.IsNaN(origH) && !double.IsPositiveInfinity(origH))
                maxHeight = Math.Min(maxHeight, origH);
            if (current.OriginalMaxWidth is double origW && !double.IsNaN(origW) && !double.IsPositiveInfinity(origW))
                maxWidth = Math.Min(maxWidth, origW);
            current.Content.MaxHeight = maxHeight;
            current.Content.MaxWidth = maxWidth;
        }
    }

    public bool HandleEscape(KeyRoutedEventArgs args)
    {
        bool handled = HandleEscape(args.Key);
        args.Handled |= handled;
        return handled;
    }

    public bool HandleEscape(VirtualKey key)
    {
        if (!IsOpen || key != VirtualKey.Escape) return false;
        RequestClose();
        return true;
    }

    private void Outside(PointerRoutedEventArgs args, bool pressed)
    {
        if (active is not { Content: { } panel } ||
            (!HasOutsidePress && args.OriginalSource is DependencyObject node && DesktopVisual.Contains(panel, node)))
            return;
        HandleOutsidePointer(args, this, pressed);
    }

    public void HandleOutsidePointer(PointerRoutedEventArgs args, FrameworkElement coordinateSpace, bool pressed)
    {
        if (active is not { Content: { } panel }) return;
        var pointer = args.GetCurrentPoint(coordinateSpace);
        if (pressed)
        {
            if (!pointer.Properties.IsLeftButtonPressed || Inside(panel, coordinateSpace, pointer.Position)) return;
            dismissPointer = pointer.PointerId;
            args.Handled = true;
            return;
        }
        if (dismissPointer != pointer.PointerId) return;
        dismissPointer = null;
        args.Handled = true;
        if (active.DismissOnOutside && !Inside(panel, coordinateSpace, pointer.Position))
            RequestClose();
    }

    private static bool Inside(FrameworkElement panel, FrameworkElement coordinateSpace, Point point) =>
        panel.IsLoaded && panel.TransformToVisual(coordinateSpace)
            .TransformBounds(new(0, 0, panel.ActualWidth, panel.ActualHeight)).Contains(point);

    private void CancelPress() => dismissPointer = null;

    public void RequestClose()
    {
        if (active is null) return;
        if (active.CloseRequested is { } request) request();
        else Close();
    }

    public void Close(FrameworkElement content)
    {
        int index = modals.FindIndex(item => ReferenceEquals(item.Content, content));
        if (index < 0) return;
        CloseAt(index);
    }

    public static void CloseFor(DependencyObject owner, FrameworkElement? content = null)
    {
        if (Find(owner) is not { } host) return;
        if (content is null)
        {
            if (ReferenceEquals(host.active?.Owner, owner)) host.Close();
        }
        else host.Close(content);
    }

    public void CloseAll()
    {
        while (modals.Count > 0) CloseAt(modals.Count - 1);
    }

    public void Close()
    {
        if (modals.Count == 0) return;
        CloseAt(modals.Count - 1);
    }

    private void CloseAt(int index)
    {
        dismissPointer = null;
        var current = modals[index];
        modals.RemoveAt(index);
        current.VisibilityObserver?.Dispose();
        Children.Remove(current.Content);
        current.Content.IsHitTestVisible = true;
        current.Content.Opacity = 1;
        if (current.OriginalMaxWidth == DependencyProperty.UnsetValue)
            current.Content.ClearValue(MaxWidthProperty);
        else if (current.OriginalMaxWidth is double restoredW)
            current.Content.MaxWidth = restoredW;
        if (current.OriginalMaxHeight == DependencyProperty.UnsetValue)
            current.Content.ClearValue(MaxHeightProperty);
        else if (current.OriginalMaxHeight is double restoredH)
            current.Content.MaxHeight = restoredH;
        if (current.PropagatedDataContext)
            current.Content.ClearValue(DataContextProperty);
        if (modals.Count > 0)
        {
            var top = modals[^1];
            top.Content.IsHitTestVisible = true;
            top.Content.Opacity = 1;
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
        if (current.PriorFocus is FrameworkElement focusTarget &&
            focusTarget.IsLoaded &&
            DesktopVisual.IsVisible(current.Owner) &&
            current.CanRestoreFocus?.Invoke() != false)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (focusTarget.IsLoaded && DesktopVisual.IsVisible(focusTarget))
                    focusTarget.Focus(FocusState.Programmatic);
            });
        }
        OpenStateChanged?.Invoke(this, EventArgs.Empty);
        current.Closed?.Invoke();
    }
}
