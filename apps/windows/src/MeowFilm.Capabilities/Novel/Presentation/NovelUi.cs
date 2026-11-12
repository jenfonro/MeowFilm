using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal static class NovelUi
{
    internal static void Theme(FrameworkElement element) => element.Resources.MergedDictionaries.Add(new ResourceDictionary
    { Source = new Uri("/MeowFilm.Capabilities;component/Presentation/CapabilityTheme.xaml", UriKind.Relative) });
    internal static TextBlock Text(string text, double size = 14, bool muted = false)
    {
        var item = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        item.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MeowFilm.Brush.Muted" : "MeowFilm.Brush.Text"); return item;
    }
    internal static Button Button(string name, Action action, bool enabled = true)
    {
        var button = new Button { Content = name, Margin = new(0, 0, 10, 8), IsEnabled = enabled, Padding = new(12, 7, 12, 7) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.QuietButton");
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
        var input = new TextBox { Text = value, MinWidth = 90, Width = width, Margin = new(0, 0, 10, 8), Padding = new(8, 6, 8, 6) };
        AutomationProperties.SetName(input, label); input.ToolTip = label; return input;
    }
    internal static Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message, string action, CancellationToken token = default)
    {
        var body = new StackPanel(); body.Children.Add(Text(message));
        return DialogAsync(owner, title, body, action, _ => Task.FromResult(true), token);
    }
    internal static Task<bool> DialogAsync(FrameworkElement owner, string title, FrameworkElement body, string action,
        Func<CancellationToken, Task<bool>> save, CancellationToken token = default, bool autoSubmit = false)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        var popup = new Popup { PlacementTarget = owner, AllowsTransparency = true, StaysOpen = true };
        DialogPlacement.SetCentered(popup, true);
        var surface = new Border { Padding = new(0), CornerRadius = new(20),
            Width = Math.Min(640, Math.Max(320, Window.GetWindow(owner)?.ActualWidth - 64 ?? 640)) };
        Theme(surface); surface.SetResourceReference(Border.BackgroundProperty, "MeowFilm.Brush.Surface");
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(surface, title);
        var grid = new DockPanel(); var heading = Text(title, 17); heading.Margin = new(0); heading.VerticalAlignment = VerticalAlignment.Center;
        var header = new Border { Child = heading, Height = 52, Padding = new(20, 0, 20, 0), BorderThickness = new(0, 0, 0, 1) }; header.SetResourceReference(Border.BorderBrushProperty, "MeowFilm.Brush.Border"); DockPanel.SetDock(header, Dock.Top); grid.Children.Add(header);
        var notices = new NoticeStack();
        var buttons = Bar(); buttons.HorizontalAlignment = HorizontalAlignment.Right; buttons.Margin = new(20, 14, 10, 12); DockPanel.SetDock(buttons, Dock.Bottom);
        bool busy = false, accepted = false, closed = false, finished = false;
        Exception? failure = null;
        CancellationTokenRegistration registration = default;
        var cancel = Button("取消", () => popup.IsOpen = false);
        var accept = Button(action, () => { });
        cancel.Background = new SolidColorBrush(Color.FromArgb(20, 118, 118, 128)); accept.Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)); accept.Foreground = Brushes.White;
        foreach (var button in new[] { cancel, accept }) { button.MinWidth = 66; button.Height = 36; button.MinHeight = 36; }
        if (autoSubmit) accept.Visibility = Visibility.Collapsed;
        accept.Click += async (_, _) => await AcceptAsync();
        async Task AcceptAsync()
        {
            if (busy || finished || !popup.IsOpen) return;
            busy = true; accept.IsEnabled = false;
            try { if (await save(pending.Token) && !pending.IsCancellationRequested) { accepted = true; popup.IsOpen = false; } }
            catch (OperationCanceledException) { popup.IsOpen = false; }
            catch (Exception error)
            {
                if (autoSubmit || !popup.IsOpen || pending.IsCancellationRequested) { failure = error; popup.IsOpen = false; }
                else notices.Show(popup, error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
            }
            finally { busy = false; accept.IsEnabled = true; Complete(); }
        }
        buttons.Children.Add(cancel); buttons.Children.Add(accept); grid.Children.Add(buttons);
        DockPanel.SetDock(notices, Dock.Top); grid.Children.Add(notices);
        grid.Children.Add(new ScrollViewer { Content = body, Margin = new(20, 20, 20, 0), MaxHeight = Math.Max(200, Window.GetWindow(owner)?.ActualHeight - 220 ?? 520),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        surface.Child = grid; popup.Child = surface;
        registration = token.Register(() => owner.Dispatcher.BeginInvoke(new Action(() => { if (!finished) popup.IsOpen = false; })));
        popup.Opened += Opened;
        popup.Closed += Closed;
        async void Opened(object? sender, EventArgs e)
        {
            if (!body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) cancel.Focus();
            if (autoSubmit) await AcceptAsync();
        }
        void Closed(object? sender, EventArgs e)
        {
            pending.Cancel();
            closed = true; Complete();
        }
        surface.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.IsOpen = true; return completion.Task;
        void Complete()
        {
            if (finished || !closed || busy) return; finished = true;
            popup.Opened -= Opened; popup.Closed -= Closed;
            registration.Dispose(); pending.Dispose(); DialogPlacement.SetCentered(popup, false);
            popup.Child = null; owner.Focus();
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
