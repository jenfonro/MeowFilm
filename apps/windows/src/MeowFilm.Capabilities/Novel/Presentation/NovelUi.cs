using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal static class NovelUi
{
    private const string ThemeUri = "/MeowFilm.Capabilities;component/Presentation/CapabilityTheme.xaml";

    internal static void Theme(FrameworkElement element)
    {
        if (!element.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString == ThemeUri))
            element.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri(ThemeUri, UriKind.Relative) });
    }

    internal static TextBlock Text(string text, double size = 14, bool muted = false)
    {
        var item = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        item.SetResourceReference(FrameworkElement.StyleProperty, muted ? "Capability.Muted" : "Capability.Text");
        return item;
    }

    internal static Button Button(string name, Action action, bool enabled = true, string style = "MeowFilm.QuietButton")
    {
        var button = new Button { Content = name, Margin = new(0, 0, 10, 8), IsEnabled = enabled, Padding = new(12, 7, 12, 7) };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button;
    }

    internal static WrapPanel Bar(params UIElement[] items)
    {
        var panel = new WrapPanel();
        foreach (var item in items) panel.Children.Add(item); return panel;
    }

    internal static ScrollViewer Scroll(UIElement content)
    {
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.OverlayScrollViewer"); return scroll;
    }

    internal static TextBox Input(string label, string value = "", double width = 280)
    {
        var input = new TextBox
        {
            Text = value, MinWidth = 90, Width = width, MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 0)
        };
        input.SetResourceReference(FrameworkElement.StyleProperty, "Novel.Input");
        AutomationProperties.SetName(input, label); input.ToolTip = label; return input;
    }

    internal static Border Row(UIElement content)
    {
        var row = new Border { Child = content };
        row.SetResourceReference(FrameworkElement.StyleProperty, "Novel.Row");
        return row;
    }

    internal static Border Field(string label, UIElement editor)
    {
        var content = new StackPanel();
        content.Children.Add(Text(label));
        content.Children.Add(editor);
        return Row(content);
    }

    internal static Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message, string action,
        CancellationToken token = default, bool applicationOverlay = false)
    {
        var body = new StackPanel(); body.Children.Add(Text(message));
        return DialogAsync(owner, title, body, action, _ => Task.FromResult(true), token, applicationOverlay: applicationOverlay);
    }
    internal static Task<bool> DialogAsync(FrameworkElement owner, string title, FrameworkElement body, string action,
        Func<CancellationToken, Task<bool>> save, CancellationToken token = default, bool autoSubmit = false,
        bool applicationOverlay = false)
    {
        token.ThrowIfCancellationRequested();
        var host = applicationOverlay
            ? PlayerDialogHost.Find(owner) ?? throw new InvalidOperationException("The novel dialog requires the application overlay.")
            : null;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        Popup? popup = applicationOverlay ? null : new Popup { PlacementTarget = owner, AllowsTransparency = true, StaysOpen = true };
        if (popup is not null) DialogPlacement.SetCentered(popup, true);
        var window = Window.GetWindow(owner);
        double availableWidth = host?.ActualWidth > 0 ? host.ActualWidth :
            window?.ActualWidth > 0 ? window.ActualWidth : 616;
        double overlayMaxWidth = Math.Min(520, Math.Max(0, availableWidth - 32));
        double dialogWidth = applicationOverlay
            ? Math.Min(520, Math.Max(0, availableWidth - 96))
            : window?.ActualWidth > 0 ? Math.Min(640, Math.Max(320, window.ActualWidth - 64)) : 640;
        var surface = new Border
        {
            Padding = applicationOverlay ? new Thickness(20) : new Thickness(0),
            MinWidth = applicationOverlay ? 0 : 320, MaxWidth = applicationOverlay ? overlayMaxWidth : 640,
            Width = dialogWidth, Focusable = true
        };
        if (!applicationOverlay) surface.CornerRadius = new(20);
        Theme(surface);
        if (applicationOverlay) surface.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        else surface.SetResourceReference(Border.BackgroundProperty, "MeowFilm.Brush.Surface");
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        if (applicationOverlay) KeyboardNavigation.SetControlTabNavigation(surface, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(surface, title);
        var grid = new DockPanel();
        var heading = Text(title, applicationOverlay ? 18 : 17);
        if (applicationOverlay)
        {
            heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 0, 0, 12);
            DockPanel.SetDock(heading, Dock.Top); grid.Children.Add(heading);
        }
        else
        {
            heading.Margin = new(0); heading.VerticalAlignment = VerticalAlignment.Center;
            var header = new Border { Child = heading, Height = 52, Padding = new(20, 0, 20, 0), BorderThickness = new(0, 0, 0, 1) };
            header.SetResourceReference(Border.BorderBrushProperty, "MeowFilm.Brush.Border");
            DockPanel.SetDock(header, Dock.Top); grid.Children.Add(header);
        }
        var notices = new NoticeStack();
        var buttons = Bar(); buttons.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(buttons, Dock.Bottom);
        bool busy = false, accepted = false, closed = false, finished = false;
        Exception? failure = null;
        CancellationTokenRegistration registration = default;
        var cancel = Button("取消", CloseDialog);
        var accept = Button(action, () => { }, style: applicationOverlay ? "MeowFilm.PrimaryButton" : "MeowFilm.QuietButton");
        if (!applicationOverlay)
        {
            cancel.Background = new SolidColorBrush(Color.FromArgb(20, 118, 118, 128));
            accept.Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)); accept.Foreground = Brushes.White;
            foreach (var button in new[] { cancel, accept }) { button.MinWidth = 66; button.Height = 36; button.MinHeight = 36; }
        }
        if (autoSubmit) accept.Visibility = Visibility.Collapsed;
        accept.Click += async (_, _) => await AcceptAsync();
        async Task AcceptAsync()
        {
            if (busy || finished || !IsOpen()) return;
            busy = true; accept.IsEnabled = false;
            try { if (await save(pending.Token) && !pending.IsCancellationRequested) { accepted = true; CloseDialog(); } }
            catch (OperationCanceledException) { CloseDialog(); }
            catch (Exception error)
            {
                if (autoSubmit || !IsOpen() || pending.IsCancellationRequested) { failure = error; CloseDialog(); }
                else
                {
                    object noticeOwner = host is not null ? host : popup is not null ? popup : owner;
                    notices.Show(noticeOwner, error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
                }
            }
            finally { busy = false; accept.IsEnabled = true; Complete(); }
        }
        if (applicationOverlay)
        {
            buttons.Children.Add(accept); buttons.Children.Add(cancel);
        }
        else
        {
            buttons.Margin = new(20, 14, 10, 12);
            buttons.Children.Add(cancel); buttons.Children.Add(accept);
        }
        grid.Children.Add(buttons);
        DockPanel.SetDock(notices, Dock.Top); grid.Children.Add(notices);
        grid.Children.Add(new ScrollViewer { Content = body,
            Margin = applicationOverlay ? new Thickness(0, 2, 0, 4) : new Thickness(20, 20, 20, 0),
            MaxHeight = applicationOverlay
                ? Math.Max(180, window?.ActualHeight - 220 ?? 520)
                : Math.Max(200, window?.ActualHeight - 220 ?? 520),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        surface.Child = grid;
        registration = token.Register(() => owner.Dispatcher.BeginInvoke(new Action(() => { if (!finished) CloseDialog(); })));
        if (popup is not null)
        {
            popup.Child = surface;
            popup.Opened += Opened;
            popup.Closed += PopupClosed;
            popup.IsOpen = true;
        }
        else
        {
            host!.Show(owner, surface, DialogClosed, dismissOnOutside: false);
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _ = FocusDialog(); }));
        }
        surface.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseDialog(); e.Handled = true; } };
        return completion.Task;

        bool IsOpen() => host is not null ? ReferenceEquals(surface.Parent, host) : popup?.IsOpen == true;
        void CloseDialog()
        {
            if (!IsOpen()) return;
            if (host is not null) host.Close();
            else if (popup is not null) popup.IsOpen = false;
        }
        async void Opened(object? sender, EventArgs e) => await FocusDialog();
        async Task FocusDialog()
        {
            if (!IsOpen()) return;
            if (!body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) cancel.Focus();
            if (autoSubmit) await AcceptAsync();
        }
        void PopupClosed(object? sender, EventArgs e) => DialogClosed();
        void DialogClosed()
        {
            closed = true;
            pending.Cancel();
            owner.Focus();
            Complete();
        }
        void Complete()
        {
            if (finished || !closed || busy) return; finished = true;
            if (popup is not null)
            {
                popup.Opened -= Opened; popup.Closed -= PopupClosed;
                DialogPlacement.SetCentered(popup, false); popup.Child = null;
            }
            registration.Dispose(); pending.Dispose();
            if (failure is not null) completion.TrySetException(failure); else completion.TrySetResult(accepted && !token.IsCancellationRequested);
        }
    }
}

internal sealed class NovelImage : Image
{
    private readonly NovelSourceService sources;
    private readonly NovelBook book;
    private CancellationTokenSource? request;
    private bool loaded, observingLayout, demanded;
    internal NovelImage(NovelSourceService sources, NovelBook book)
    {
        this.sources = sources; this.book = book; Width = 96; Height = 136; Stretch = Stretch.UniformToFill;
        AutomationProperties.SetName(this, book.Name + "封面");
        Loaded += (_, _) => { loaded = true; UpdateVisibility(); };
        Unloaded += (_, _) => { loaded = false; UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    // Image otherwise arranges to zero without pixels, preventing its first viewport demand.
    protected override Size ArrangeOverride(Size arrangeSize) => Source is null ? arrangeSize : base.ArrangeOverride(arrangeSize);
    private bool InViewport()
    {
        if (!loaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            Rect visible = new(0, 0, ActualWidth, ActualHeight);
            Visual child = this;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not Visual visual) continue;
                visible = child.TransformToAncestor(visual).TransformBounds(visible); child = visual;
                if (visual is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter))
                {
                    visible.Intersect(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
                }
            }
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void OnLayout(object? sender, EventArgs e) => UpdateVisibility();
    private void OnResumed(object? sender, EventArgs e) { Stop(); demanded = false; UpdateVisibility(); }
    private void UpdateVisibility()
    {
        bool observe = loaded && IsVisible;
        if (observe != observingLayout)
        {
            if (observe) { LayoutUpdated += OnLayout; sources.Resumed += OnResumed; }
            else { LayoutUpdated -= OnLayout; sources.Resumed -= OnResumed; }
            observingLayout = observe;
        }
        bool next = InViewport();
        if (next == demanded) return;
        demanded = next;
        if (demanded) Start(); else Stop();
    }
    private void Start()
    {
        if (request is not null || book.CoverUrl.Length == 0) return;
        var owner = request = new();
        _ = Load();
        async Task Load()
        {
            try
            {
                await sources.VisualAsync(book, async ct =>
                {
                    string? reference = null;
                    try
                    {
                        var source = await sources.RequireAsync(book, ct);
                        reference = await sources.ImageAsync(source, book.CoverUrl, book.BookUrl, "cover", ct);
                        var bytes = await sources.ReadImageAsync(reference, ct);
                        var bitmap = await Task.Run(() => NovelReadingSurface.Decode(bytes, 240), ct);
                        ct.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(request, owner) || !InViewport()) return;
                        Source = bitmap;
                        // Keep the lease only while this cover intersects its viewport.
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    finally { if (reference is not null) await sources.ReleaseImageAsync(reference); }
                }, owner.Token);
            }
            catch (OperationCanceledException) { if (ReferenceEquals(request, owner)) Source = null; }
            catch { if (ReferenceEquals(request, owner)) Source = null; }
            finally { if (ReferenceEquals(request, owner)) request = null; owner.Dispose(); }
        }
    }
    private void Stop() { var old = request; request = null; old?.Cancel(); Source = null; }
}
