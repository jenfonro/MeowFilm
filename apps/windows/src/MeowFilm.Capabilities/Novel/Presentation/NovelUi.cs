using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal static class NovelUi
{
    private static readonly DependencyProperty DialogLayerProperty = DependencyProperty.RegisterAttached("DialogLayer", typeof(Grid), typeof(NovelUi), new PropertyMetadata(null));
    private const string ThemeUri = "ms-appx:///MeowFilm.Capabilities/Presentation/CapabilityTheme.xaml";

    internal static void Theme(FrameworkElement element)
    {
        if (!element.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString == ThemeUri))
            element.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri(ThemeUri) });
    }

    internal static TextBlock Text(string text, double size = 14, bool muted = false)
    {
        var item = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        item.UseResource(FrameworkElement.StyleProperty, muted ? "Capability.Muted" : "Capability.Text");
        return item;
    }

    internal static Button Button(string name, Action action, bool enabled = true, string style = "MeowFilm.QuietButton")
    {
        var button = new Button { Content = name, Margin = new(0, 0, 10, 8), IsEnabled = enabled, Padding = new(12, 7, 12, 7) };
        button.UseResource(FrameworkElement.StyleProperty, style);
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
        scroll.UseResource(FrameworkElement.StyleProperty, "MeowFilm.OverlayScrollViewer"); return scroll;
    }

    internal static TextBox Input(string label, string value = "", double width = 280)
    {
        var input = new TextBox
        {
            Text = value, MinWidth = 90, Width = width, MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 0)
        };
        input.UseResource(FrameworkElement.StyleProperty, "Novel.Input");
        AutomationProperties.SetName(input, label); ToolTipService.SetToolTip(input, label); return input;
    }

    internal static Border Row(UIElement content)
    {
        var row = new Border { Child = content };
        row.UseResource(FrameworkElement.StyleProperty, "Novel.Row");
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
        CancellationToken token = default, bool applicationOverlay = false, string cancelAction = "取消", Grid? parent = null)
    {
        var body = new StackPanel(); body.Children.Add(Text(message));
        return DialogAsync(owner, title, body, action, _ => Task.FromResult(true), token,
            applicationOverlay: applicationOverlay, parent: parent, cancelAction: cancelAction);
    }
    internal static Task<bool> DialogAsync(FrameworkElement owner, string title, FrameworkElement body, string action,
        Func<CancellationToken, Task<bool>> save, CancellationToken token = default, bool autoSubmit = false,
        bool applicationOverlay = false, NovelDialogEditing? editing = null, Grid? parent = null, string cancelAction = "取消")
    {
        token.ThrowIfCancellationRequested();
        for (DependencyObject? node = owner; parent is null && node is not null; node = VisualTreeHelper.GetParent(node))
            parent = node.GetValue(DialogLayerProperty) as Grid;
        var host = parent is null
            ? PlayerDialogHost.Find(owner) ?? throw new InvalidOperationException("The novel dialog requires the application overlay.")
            : null;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        NovelPresentation.VisibilityWatch? visibility = null;
        var window = DesktopWindow.GetWindow(owner);
        double availableWidth = parent?.ActualWidth > 0 ? parent.ActualWidth : host?.ActualWidth > 0 ? host.ActualWidth :
            window?.Root.ActualWidth > 0 ? window.Root.ActualWidth : 616;
        double overlayMaxWidth = Math.Min(editing is null ? 520 : 760, Math.Max(0, availableWidth - 32));
        double dialogWidth = editing is not null ? overlayMaxWidth : applicationOverlay
            ? Math.Min(520, Math.Max(0, availableWidth - 96))
            : window?.Root.ActualWidth > 0 ? Math.Min(640, Math.Max(320, window.Root.ActualWidth - 64)) : 640;
        var surface = new Border
        {
            Padding = applicationOverlay && editing is null ? new Thickness(20) : new Thickness(0),
            MinWidth = applicationOverlay ? 0 : 320, MaxWidth = applicationOverlay ? overlayMaxWidth : 640,
            Width = dialogWidth
        };
        if (!applicationOverlay) surface.CornerRadius = new(20);
        Theme(surface);
        if (applicationOverlay) surface.UseResource(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        else surface.UseResource(Border.BackgroundProperty, "MeowFilm.Brush.Surface");
        if (editing is not null)
            surface.Height = Math.Min(720, Math.Max(0, (host?.ActualHeight > 0 ? host.ActualHeight : window?.Root.ActualHeight ?? 752) - 32));
        surface.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        AutomationProperties.SetName(surface, title);
        var dialogLayer = new Grid(); surface.SetValue(DialogLayerProperty, dialogLayer);
        var grid = new DockPanel();
        var bodyOwner = new ContentControl { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var toolsOwner = new ContentControl { Content = editing?.Tools, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var gridOwner = new ContentControl { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var heading = Text(title, applicationOverlay ? 18 : 17);
        if (editing is not null)
        {
            heading.Margin = new(0); heading.VerticalAlignment = VerticalAlignment.Center;
            heading.TextWrapping = TextWrapping.NoWrap; heading.TextTrimming = TextTrimming.CharacterEllipsis;
            var header = new Grid();
            header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(heading); Grid.SetColumn(toolsOwner, 1); header.Children.Add(toolsOwner);
            var chrome = new Border { Child = header, MinHeight = 52, Padding = new(16, 8, 16, 8) };
            DockPanel.SetDock(chrome, Dock.Top); grid.Children.Add(chrome);
        }
        else if (applicationOverlay)
        {
            heading.Margin = new(0, 0, 0, 12);
            DockPanel.SetDock(heading, Dock.Top); grid.Children.Add(heading);
        }
        else
        {
            heading.Margin = new(0); heading.VerticalAlignment = VerticalAlignment.Center;
            var header = new Border { Child = heading, Height = 52, Padding = new(20, 0, 20, 0), BorderThickness = new(0, 0, 0, 1) };
            header.UseResource(Border.BorderBrushProperty, "MeowFilm.Brush.Border");
            DockPanel.SetDock(header, Dock.Top); grid.Children.Add(header);
        }
        var notices = new NoticeStack();
        var buttons = Bar(); buttons.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(buttons, Dock.Bottom);
        bool busy = false, continuing = false, accepted = false, closed = false, finished = false;
        Exception? failure = null;
        CancellationTokenRegistration registration = default;
        Grid? nestedLayer = null;
        var cancel = Button(cancelAction, () => { });
        var accept = Button(action, () => { }, style: applicationOverlay ? "MeowFilm.PrimaryButton" : "MeowFilm.QuietButton");
        if (!applicationOverlay)
        {
            cancel.Background = new SolidColorBrush(Color.FromArgb(20, 118, 118, 128));
            accept.Background = new SolidColorBrush(Color.FromArgb(255, 0, 122, 255)); accept.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            foreach (var button in new[] { cancel, accept }) { button.MinWidth = 66; button.Height = 36; button.MinHeight = 36; }
        }
        if (autoSubmit) accept.Visibility = Visibility.Collapsed;
        accept.Click += async (_, _) => await AcceptAsync();
        async Task<bool> AcceptAsync(bool keepOpen = false)
        {
            if (busy || continuing && !keepOpen || finished || !IsOpen()) return false;
            busy = true; accept.IsEnabled = false;
            if (editing is not null) { bodyOwner.IsEnabled = false; toolsOwner.IsEnabled = false; cancel.IsEnabled = false; }
            try
            {
                bool closeAfterSave = await save(pending.Token);
                if (pending.IsCancellationRequested || !IsOpen()) return false;
                if (closeAfterSave && !keepOpen) { accepted = true; CloseDialog(); }
                return true;
            }
            catch (OperationCanceledException) { CloseDialog(); return false; }
            catch (Exception error)
            {
                ReportFailure(error); return false;
            }
            finally
            {
                busy = false; accept.IsEnabled = true;
                if (editing is not null) { bodyOwner.IsEnabled = true; toolsOwner.IsEnabled = true; cancel.IsEnabled = true; }
                Complete();
            }
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
        if (editing is null) grid.Children.Add(buttons);
        else
        {
            var footer = new Border { Child = buttons, BorderThickness = new(0, 1, 0, 0), Padding = new(16, 12, 6, 4) };
            footer.UseResource(FrameworkElement.StyleProperty, "MeowFilm.DialogFooter");
            AutomationProperties.SetAutomationId(footer, "novel.dialog.footer");
            DockPanel.SetDock(footer, Dock.Bottom); grid.Children.Add(footer);
        }
        DockPanel.SetDock(notices, Dock.Top); grid.Children.Add(notices);
        if (editing is not null)
        {
            bodyOwner.Margin = new(12, 0, 12, 12); grid.Children.Add(bodyOwner);
        }
        else
        {
            var scroll = Scroll(bodyOwner);
            scroll.Margin = applicationOverlay ? new Thickness(0, 2, 0, 4) : new Thickness(20, 20, 20, 0);
            scroll.MaxHeight = Math.Max(applicationOverlay ? 180 : 200, window?.Root.ActualHeight - 220 ?? 520);
            grid.Children.Add(scroll);
        }
        dialogLayer.Children.Add(gridOwner); surface.Child = dialogLayer;
        cancel.Click += (_, _) => RequestClose();
        if (editing is not null)
        {
            var close = Button("\uE711", RequestClose, style: "MeowFilm.IconButton");
            close.Margin = new(4, 0, 0, 0); close.Padding = new(0); close.Width = close.Height = close.MinHeight = 32;
            ToolTipService.SetToolTip(close, "关闭"); AutomationProperties.SetName(close, "关闭"); AutomationProperties.SetAutomationId(close, "novel.dialog.close");
            editing.Tools.Children.Add(close); editing.ContinueAsync = ContinueAsync;
        }
        registration = token.Register(() => owner.DispatcherQueue.TryEnqueue(() => { if (!finished) CloseDialog(); }));
        surface.Loaded += async (_, _) => await FocusDialog();
        if (parent is not null)
        {
            nestedLayer = new Grid(); nestedLayer.UseResource(FrameworkElement.StyleProperty, "MeowFilm.DialogHost");
            surface.HorizontalAlignment = HorizontalAlignment.Center; surface.VerticalAlignment = VerticalAlignment.Center;
            surface.MaxWidth = Math.Max(0, parent.ActualWidth - 24);
            surface.MaxHeight = Math.Max(0, parent.ActualHeight - 24);
            nestedLayer.Children.Add(surface); parent.Children.Add(nestedLayer);
        }
        else
        {
            host!.Show(owner, surface, DialogClosed, dismissOnOutside: editing is not null, onCloseRequested: RequestClose);
        }
        visibility = new(owner, visible => { if (!visible) CloseDialog(); });
        if (finished) visibility.Dispose();
        surface.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { if (parent is null) RequestClose(); e.Handled = true; } };
        return completion.Task;

        bool IsOpen() => parent is not null ? ReferenceEquals(nestedLayer?.Parent, parent) :
            host is not null && ReferenceEquals(surface.Parent, host);
        void RequestClose()
        {
            if (editing is null) CloseDialog(); else _ = ContinueAsync(CloseDialog);
        }
        async Task ContinueAsync(Action continuation)
        {
            if (continuing || busy || finished || !IsOpen()) return;
            continuing = true; gridOwner.IsEnabled = false;
            try
            {
                if (editing!.HasInput())
                {
                    bool saveChanges = await ConfirmAsync(surface, "保存更改", "当前更改未保存，是否保存", "是",
                        pending.Token, applicationOverlay: true, cancelAction: "否", parent: dialogLayer);
                    if (pending.IsCancellationRequested || !IsOpen()) return;
                    if (saveChanges && !await AcceptAsync(keepOpen: true)) return;
                }
                if (!pending.IsCancellationRequested && IsOpen()) continuation();
            }
            catch (OperationCanceledException) { CloseDialog(); }
            catch (Exception error) { ReportFailure(error); }
            finally { continuing = false; gridOwner.IsEnabled = true; Complete(); }
        }
        void ReportFailure(Exception error)
        {
            if (autoSubmit || !IsOpen() || pending.IsCancellationRequested) { failure = error; CloseDialog(); }
            else
            {
                object noticeOwner = (object?)host ?? owner;
                notices.Show(noticeOwner, error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
            }
        }
        void CloseDialog()
        {
            if (!IsOpen()) return;
            if (parent is not null) { parent.Children.Remove(nestedLayer!); DialogClosed(); }
            else if (host is not null) host.Close();
        }
        async Task FocusDialog()
        {
            if (!IsOpen()) return;
            if (FocusManager.FindFirstFocusableElement(body) is UIElement first) first.Focus(FocusState.Programmatic);
            else cancel.Focus(FocusState.Programmatic);
            if (autoSubmit) await AcceptAsync();
        }
        void DialogClosed()
        {
            closed = true;
            pending.Cancel();
            if (DesktopVisual.IsVisible(owner)) owner.Focus(FocusState.Programmatic);
            Complete();
        }
        void Complete()
        {
            if (finished || !closed || busy || continuing) return; finished = true;
            visibility?.Dispose();
            registration.Dispose(); pending.Dispose();
            if (failure is not null) completion.TrySetException(failure); else completion.TrySetResult(accepted && !token.IsCancellationRequested);
        }
    }
}

internal sealed record NovelDialogEditing(Panel Tools, Func<bool> HasInput)
{
    internal Func<Action, Task> ContinueAsync { get; set; } = null!;
}

internal sealed class NovelImage : Grid
{
    private readonly Image image = new();
    internal ImageSource? Source { get => image.Source; private set => image.Source = value; }
    internal Stretch Stretch { get => image.Stretch; set => image.Stretch = value; }
    private readonly NovelSourceService sources;
    private readonly NovelBook book;
    private CancellationTokenSource? request;
    private bool loaded, observingLayout, demanded;
    internal NovelImage(NovelSourceService sources, NovelBook book)
    {
        this.sources = sources; this.book = book; Children.Add(image); Width = 96; Height = 136; Stretch = Stretch.UniformToFill;
        AutomationProperties.SetName(this, book.Name + "封面");
        Loaded += (_, _) => { loaded = true; UpdateVisibility(); };
        Unloaded += (_, _) => { loaded = false; UpdateVisibility(); };
        _ = new NovelPresentation.VisibilityWatch(this, _ => UpdateVisibility());
        EffectiveViewportChanged += (_, _) => UpdateVisibility();
    }
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer(this);
    private bool InViewport()
    {
        if (!loaded || !DesktopVisual.IsVisible(this) || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            Rect visible = new(0, 0, ActualWidth, ActualHeight);
            UIElement child = this;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not UIElement visual) continue;
                visible = child.TransformToVisual(visual).TransformBounds(visible); child = visual;
                if (visual is FrameworkElement element && (element.Clip is not null || element is ScrollViewer || ReferenceEquals(element, XamlRoot?.Content)))
                {
                    visible.Intersect(element.Clip?.Rect ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
                }
            }
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void OnLayout(object? sender, object e) => UpdateVisibility();
    private void OnResumed(object? sender, EventArgs e) { Stop(); demanded = false; UpdateVisibility(); }
    private void UpdateVisibility()
    {
        bool observe = loaded && DesktopVisual.IsVisible(this);
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
                        Source = NovelPresentation.Bitmap(bitmap);
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
