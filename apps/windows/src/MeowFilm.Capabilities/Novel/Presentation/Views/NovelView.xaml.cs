using System.IO;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Capabilities;

public sealed partial class NovelView : UserControl, IDesktopHeaderSource, IDisposable
{
    private readonly NovelCapability module;
    private readonly NovelSidebarView sidebar;
    private readonly NovelReadingSurface surface;
    private readonly DispatcherTimer auto;
    private readonly DispatcherTimer save;
    private readonly NovelWork saves = new();
    private readonly NovelPresentation.VisibilityWatch visibility;
    private NovelDiscoveryView? homeDiscoveryView;
    private NovelDiscoveryView? categoriesDiscoveryView;
    private NovelShelfView? shelfView;
    private NovelHistoryView? historyView;
    private NovelBookView? bookView;
    private bool automatic, advancing, disposed;
    private Task automaticOperation = Task.CompletedTask;
    private int autoTicks;
    private string renderedRoute = "";
    private NovelBookSession? renderedSession;
    private DesktopWindow? attachedWindow;
    private readonly Dictionary<string, double> scrollPositions = [];

    public NovelView(NovelCapability module)
    {
        this.module = module;
        sidebar = new NovelSidebarView(module);
        surface = new NovelReadingSurface(module.Sources) { ReportError = module.Notify };
        InitializeComponent();
        ReaderSurfaceHost.Content = surface;
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => surface.Seek(ProgressSlider.Value / 100)), true);

        auto = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        auto.Tick += AutoTick;
        save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        save.Tick += (_, _) => { save.Stop(); module.RunUi(_ => SavePositionAsync()); };
        surface.PositionChanged += (_, _) => { PageStatusText.Text = surface.PageStatus; ProgressSlider.Value = surface.CaptureAnchor().Ratio * 100; save.Stop(); save.Start(); };
        surface.LayoutCompleted += (_, _) => { PageStatusText.Text = surface.PageStatus; ProgressSlider.Value = surface.CaptureAnchor().Ratio * 100; };
        surface.ControlsRequested += (_, _) => { PauseReading(); ReaderControls.Visibility = ReaderControls.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
        surface.UserInteracted += (_, _) => PauseReading();
        surface.CrossChapterAsync = index => ChangeChapterAsync(index, automatic);
        surface.RunInteraction = module.RunUi;
        visibility = new(this, visible =>
        {
            if (disposed) return;
            if (!visible)
            {
                homeDiscoveryView?.ClosePopups();
                categoriesDiscoveryView?.ClosePopups();
                module.CancelPresentation();
                module.Session?.Cancel();
                module.RunUi(_ => SavePositionAsync());
            }
            else if (module.Route == NovelCapability.ReaderRoute)
                module.RefreshReaderSettings();
        });
        Loaded += (_, _) => { AttachWindow(); Present(); };
        Unloaded += (_, _) => { PauseReading(); DetachWindow(); };
    }

    internal FrameworkElement SidebarView => sidebar;

    public DesktopHeaderContribution Header => new(
        module.Route switch
        {
            NovelCapability.HomeRoute => "小说",
            NovelCapability.CategoriesRoute => "分类",
            NovelCapability.ShelfRoute => "书架",
            NovelCapability.HistoryRoute => "阅读历史",
            _ => ""
        },
        SearchVisible: module.Route != NovelCapability.ReaderRoute)
    {
        SearchTargetId = "novel",
        SearchContextId = module.Route
    };

    public event EventHandler? HeaderChanged;
    internal void PostNotice(string text) => NoticeHost.Post(text);

    private void SaveCurrentScrollPosition()
    {
        if (string.IsNullOrEmpty(renderedRoute)) return;
        if (BodyHost.Content is NovelDiscoveryView oldDiscovery)
            scrollPositions[renderedRoute] = oldDiscovery.ScrollHost.VerticalOffset;
        else if (BodyHost.Content is NovelBookView oldBook)
            scrollPositions[renderedRoute] = oldBook.ScrollHost.VerticalOffset;
    }

    private void RestoreScrollPosition(ScrollViewer scroller, string routeKey)
    {
        if (!scrollPositions.TryGetValue(routeKey, out double position)) return;
        void Apply(object s, RoutedEventArgs e)
        {
            scroller.Loaded -= Apply;
            scroller.ChangeView(null, position, null, true);
        }
        if (scroller.IsLoaded) scroller.ChangeView(null, position, null, true);
        else scroller.Loaded += Apply;
    }

    internal void Present()
    {
        if (disposed) return;
        sidebar.Present();
        bool reading = module.Route == NovelCapability.ReaderRoute && module.Session is not null;
        if (reading)
        {
            homeDiscoveryView?.ClosePopups();
            categoriesDiscoveryView?.ClosePopups();
            SaveCurrentScrollPosition();
            BodyHost.Content = null;
            BodyHost.Visibility = Visibility.Collapsed;
            ReaderSurface.Visibility = Visibility.Visible;
            PurchaseButton.Visibility = module.Session is { Chapter: { IsVip: true, IsPay: false } } session &&
                module.Sources.Sources.Any(s => s.InstallationId == session.Book.InstallationId && s.Enabled && s.Actions.Contains("payAction")) ? Visibility.Visible : Visibility.Collapsed;
            surface.Present(module.Session!, module.Library.Appearance);
            PageStatusText.Text = surface.PageStatus;
            if (renderedRoute != module.Route || !ReferenceEquals(renderedSession, module.Session))
            { ReaderControls.Visibility = Visibility.Visible; module.RunUi(SavePositionAfterLayoutAsync); }
        }
        else
        {
            PauseReading();
            ReaderSurface.Visibility = Visibility.Collapsed;
            BodyHost.Visibility = Visibility.Visible;
            BodyHost.Margin = module.Route == NovelCapability.BookRoute ? new(40, 20, 24, 20) : new(0);
            bool routeChanged = renderedRoute != module.Route || BodyHost.Content is null;
            if (routeChanged)
            {
                homeDiscoveryView?.ClosePopups();
                categoriesDiscoveryView?.ClosePopups();
                SaveCurrentScrollPosition();
                switch (module.Route)
                {
                    case NovelCapability.BookRoute:
                        bookView ??= new NovelBookView(module, SwitchSourceAsync, CatalogAsync);
                        BodyHost.Content = bookView;
                        RestoreScrollPosition(bookView.ScrollHost, module.Route);
                        break;
                    case NovelCapability.ShelfRoute:
                        shelfView ??= new NovelShelfView(module);
                        BodyHost.Content = shelfView;
                        break;
                    case NovelCapability.HistoryRoute:
                        historyView ??= new NovelHistoryView(module);
                        BodyHost.Content = historyView;
                        break;
                    case NovelCapability.CategoriesRoute:
                        categoriesDiscoveryView ??= new NovelDiscoveryView(module, categories: true);
                        BodyHost.Content = categoriesDiscoveryView;
                        RestoreScrollPosition(categoriesDiscoveryView.ScrollHost, module.Route);
                        break;
                    default:
                        homeDiscoveryView ??= new NovelDiscoveryView(module, categories: false);
                        BodyHost.Content = homeDiscoveryView;
                        RestoreScrollPosition(homeDiscoveryView.ScrollHost, module.Route);
                        break;
                }
            }

            switch (module.Route)
            {
                case NovelCapability.BookRoute:
                    bookView?.Present(module.Session);
                    break;
                case NovelCapability.ShelfRoute:
                    shelfView?.Present();
                    break;
                case NovelCapability.HistoryRoute:
                    historyView?.Present();
                    break;
                case NovelCapability.CategoriesRoute:
                    categoriesDiscoveryView?.Present();
                    categoriesDiscoveryView?.PresentFailureIfNeeded();
                    break;
                default:
                    homeDiscoveryView?.Present();
                    homeDiscoveryView?.PresentFailureIfNeeded();
                    break;
            }
        }
        renderedRoute = module.Route;
        renderedSession = module.Session;
        HeaderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BackToBook_Click(object sender, RoutedEventArgs e) => module.Request(NovelCapability.BookRoute);
    private void Catalog_Click(object sender, RoutedEventArgs e) => module.RunUi(CatalogAsync);
    private void SwitchSource_Click(object sender, RoutedEventArgs e) => module.RunUi(SwitchSourceAsync);
    private void ToggleShelf_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.Session!.ShelfAsync(ct));
    private void Appearance_Click(object sender, RoutedEventArgs e) => module.RunUi(AppearanceAsync);
    private void PreviousChapter_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => ChangeChapterAsync((module.Session?.Chapter?.Index ?? 0) - 1, token: ct));
    private void PreviousPage_Click(object sender, RoutedEventArgs e) => module.RunUi(async () => { await surface.TurnAsync(-1); });
    private void NextPage_Click(object sender, RoutedEventArgs e) => module.RunUi(async () => { await surface.TurnAsync(1); });
    private void NextChapter_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => ChangeChapterAsync((module.Session?.Chapter?.Index ?? 0) + 1, token: ct));
    private void ToggleAutomatic_Click(object sender, RoutedEventArgs e)
    {
        automatic = !automatic;
        autoTicks = 0;
        if (automatic) { ReaderControls.Visibility = Visibility.Collapsed; auto.Start(); }
        else PauseReading();
    }
    private void Purchase_Click(object sender, RoutedEventArgs e) => module.RunUi(PurchaseAsync);
    private void HideControls_Click(object sender, RoutedEventArgs e) => ReaderControls.Visibility = Visibility.Collapsed;
    private void ProgressSlider_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End)
            surface.Seek(ProgressSlider.Value / 100);
    }

    private async Task ChangeChapterAsync(int index, bool keepAutomatic = false, CancellationToken token = default)
    {
        if (!keepAutomatic) PauseReading();
        if (token == default) token = module.PresentationToken;
        if (module.Session is not { } session || index < 0 || index >= session.Chapters.Count) return;
        await SavePositionAsync();
        await session.ReadAsync(index, token: token);
        await SavePositionAfterLayoutAsync();
    }

    private void AutoTick(object? sender, object e)
    {
        if (!automaticOperation.IsCompleted) return;
        automaticOperation = AdvanceAutomaticAsync();
    }

    private async Task AdvanceAutomaticAsync()
    {
        if (advancing || !automatic || !DesktopVisual.IsVisible(this) || attachedWindow?.IsActive != true || module.Route != NovelCapability.ReaderRoute || module.Session?.Loading == true)
        {
            if (attachedWindow?.IsActive != true) PauseReading();
            return;
        }
        advancing = true;
        try
        {
            var settings = module.Library.Appearance.ForReading();
            if (settings.Mode == "scroll")
            {
                if (!surface.AdvanceScroll((12 + settings.AutoSpeed * 12) * .05) && !await surface.TurnAsync(1)) PauseReading();
            }
            else if (++autoTicks * .05 >= 25 - settings.AutoSpeed * 2)
            {
                autoTicks = 0;
                if (!await surface.TurnAsync(1)) PauseReading();
            }
        }
        catch { PauseReading(); module.Notify("自动阅读已暂停"); }
        finally { advancing = false; }
    }

    internal void PauseReading() { automatic = false; auto.Stop(); autoTicks = 0; }

    private async Task SavePositionAfterLayoutAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ready.TrySetResult())) return;
        await ready.Task;
        if (DesktopVisual.IsVisible(this) && module.Route == NovelCapability.ReaderRoute) { surface.UpdateReadingLayout(); await SavePositionAsync(); }
    }

    private Task SavePositionAsync()
    {
        if (renderedSession is not { HasContent: true } session || renderedRoute != NovelCapability.ReaderRoute) return Task.CompletedTask;
        var anchor = surface.CaptureAnchor();
        return saves.Run(async token => { await session.SaveProgressAsync(anchor, token); return true; });
    }

    internal async Task SaveAndPauseAsync() { PauseReading(); save.Stop(); await SavePositionAsync(); }

    internal Task<int?> SelectChapterAsync(IReadOnlyList<NovelChapter> chapters, CancellationToken token) => SelectFromCatalogAsync(chapters, token);

    private async Task<int?> SelectFromCatalogAsync(IReadOnlyList<NovelChapter> chapters, CancellationToken token)
    {
        PauseReading();
        var labels = chapters.ToArray();
        if (module.Library.Appearance.ReadingConversion != "none")
        {
            var result = await module.Rules.InvokeAsync(new
            {
                operation = "transform",
                title = "",
                blocks = labels.Select(c => new NovelBlock("text", c.Name)).ToArray(),
                rules = Array.Empty<NovelRule>(),
                conversion = module.Library.Appearance.ReadingConversion
            }, token);
            var names = result.GetProperty("blocks");
            for (int i = 0; i < labels.Length; i++) labels[i] = labels[i] with { Name = NovelJson.Text(names[i], "text") };
        }
        return await NovelCatalogDialogView.ShowAsync(this, labels, module.Session?.Chapter?.Index ?? 0, token);
    }

    private async Task CatalogAsync(CancellationToken token)
    {
        if (module.Session is not { } session) return;
        if (session.Chapters.Count == 0) await session.LoadMetadataAsync(token);
        int? index = await SelectFromCatalogAsync(session.Chapters, token);
        if (index is null) return;
        await SavePositionAsync();
        await session.ReadAsync(index.Value, token: token);
        module.Request(NovelCapability.ReaderRoute);
    }

    private async Task SwitchSourceAsync(CancellationToken token)
    {
        PauseReading();
        if (module.Session is not { } session) return;
        await SavePositionAsync();
        if (await NovelSwitchSourceDialogView.ShowAsync(this, module, session, token))
            Present();
    }

    private async Task PurchaseAsync(CancellationToken token)
    {
        PauseReading();
        if (module.Session is not { Chapter: { IsVip: true, IsPay: false } chapter } session) return;
        var source = module.Sources.Require(session.Book);
        if (!source.Actions.Contains("payAction")) return;
        string bookIdentity = session.Book.SourceIdentity;
        var request = new DesktopDialogRequest
        {
            Title = "购买本章",
            Message = $"确认通过“{source.Name}”购买《{session.Book.Name}》的“{chapter.Name}”？",
            PrimaryButtonText = "确认购买",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Primary
        };
        if (await DesktopModalHost.ShowDialogAsync(this, request, token) == DesktopDialogResult.Primary &&
            ReferenceEquals(module.Session, session) && session.Book.SourceIdentity == bookIdentity &&
            chapter == session.Chapter && module.Sources.IsCurrent(source))
            await session.PurchaseAsync(token);
    }

    private async Task AppearanceAsync(CancellationToken token)
    {
        PauseReading();
        var editor = new NovelAppearanceEditor(module.Library.Appearance);
        var request = new DesktopDialogRequest
        {
            Title = "阅读排版",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            MaxWidth = 520,
            DefaultButton = DesktopDialogDefaultButton.Primary,
            OnPrimaryAsync = async () =>
            {
                try
                {
                    await SavePositionAsync();
                    var next = editor.Read();
                    var prior = module.Library.Appearance;
                    await module.Library.SaveAppearanceAsync(next, token);
                    if (next.Conversion != prior.Conversion && module.Session?.Chapter is not null)
                        await module.Session.ReprocessAsync(token);
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    editor.ShowError(error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
                    return false;
                }
            }
        };
        await DesktopModalHost.ShowDialogAsync(this, request, token);
    }

    internal Task<object?> InteractAsync(System.Text.Json.JsonElement request, CancellationToken token)
    {
        PauseReading();
        return NovelWebInteraction.ShowAsync(this, module.Rules, request, token);
    }

    private void AttachWindow()
    {
        DetachWindow();
        attachedWindow = DesktopWindow.GetWindow(this);
        if (attachedWindow is not null)
        {
            attachedWindow.Deactivated += WindowDeactivated;
            attachedWindow.WindowStateChanged += WindowDeactivated;
            attachedWindow.Closed += WindowClosed;
        }
    }

    private void WindowDeactivated(object? sender, EventArgs e) { PauseReading(); module.RunUi(_ => SavePositionAsync()); }
    private void WindowClosed(object sender, WindowEventArgs e) { PauseReading(); DetachWindow(); }

    private void DetachWindow()
    {
        if (attachedWindow is null) return;
        attachedWindow.Deactivated -= WindowDeactivated;
        attachedWindow.WindowStateChanged -= WindowDeactivated;
        attachedWindow.Closed -= WindowClosed;
        attachedWindow = null;
    }

    internal async Task DrainSavesAsync() { await automaticOperation; await saves.PauseAsync(); }
    internal Task DrainImagesAsync() => surface.DrainImagesAsync();
    internal string DebugReaderPageStatus => surface.PageStatus;
    internal Task<bool> DebugTurnReaderPageAsync(int delta) => surface.TurnAsync(delta);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        homeDiscoveryView?.Detach();
        categoriesDiscoveryView?.Detach();
        visibility.Dispose();
        PauseReading();
        save.Stop();
        DetachWindow();
        surface.Dispose();
    }
}
