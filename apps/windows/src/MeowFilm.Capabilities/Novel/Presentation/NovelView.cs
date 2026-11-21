using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class NovelView : UserControl, IDesktopHeaderSource, IDisposable
{
    private readonly NovelCapability module;
    private readonly Grid root = new();
    private readonly ContentControl body = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly NoticeOutlet notice = new();
    private readonly NovelReadingSurface surface;
    private readonly Grid reader = new();
    private readonly StackPanel readerControls = new();
    private readonly TextBlock pageStatus = new();
    private readonly DispatcherTimer auto;
    private readonly DispatcherTimer save;
    private readonly NovelWork saves = new();
    private readonly NovelPresentation.VisibilityWatch visibility;
    private bool automatic, advancing, disposed;
    private Task automaticOperation = Task.CompletedTask;
    private int autoTicks;
    private string renderedRoute = "";
    private NovelBookSession? renderedSession;
    private DesktopWindow? attachedWindow;
    private Action? updateBook;
    private readonly Dictionary<string, double> scrollPositions = [];
    private readonly Button purchase;
    private readonly Slider progress = new() { Minimum = 0, Maximum = 100, Width = 240, Margin = new(10) };
    public NovelView(NovelCapability module)
    {
        this.module = module; module.DiscoverBooks.CollectionChanged += DiscoveryBooksChanged; AddDiscoveryTheme(); surface = new(module.Sources) { ReportError = module.Notify };
        Content = root; root.Children.Add(body); root.Children.Add(notice);
        purchase = NovelUi.Button("购买本章", () => module.RunUi(PurchaseAsync));
        BuildReader();
        auto = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(50) };
        auto.Tick += AutoTick;
        save = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(700) };
        save.Tick += (_, _) => { save.Stop(); module.RunUi(_ => SavePositionAsync()); };
        surface.PositionChanged += (_, _) => { pageStatus.Text = surface.PageStatus; progress.Value = surface.CaptureAnchor().Ratio * 100; save.Stop(); save.Start(); };
        surface.LayoutCompleted += (_, _) => { pageStatus.Text = surface.PageStatus; progress.Value = surface.CaptureAnchor().Ratio * 100; };
        surface.ControlsRequested += (_, _) => { PauseReading(); readerControls.Visibility = readerControls.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
        surface.UserInteracted += (_, _) => PauseReading();
        surface.CrossChapterAsync = index => ChangeChapterAsync(index, automatic);
        surface.RunInteraction = module.RunUi;
        visibility = new(this, visible =>
        {
            if (disposed) return;
            if (!visible) { discoveryChannelBar?.Close(); module.CancelPresentation(); module.Session?.Cancel(); module.RunUi(_ => SavePositionAsync()); }
            else if (module.Route == NovelCapability.ReaderRoute)
                module.RefreshReaderSettings();
        });
        Loaded += (_, _) => { AttachWindow(); Present(); }; Unloaded += (_, _) => { PauseReading(); DetachWindow(); };
    }
    public DesktopHeaderContribution Header => new(module.Route == NovelCapability.HomeRoute ? "小说" : module.Route == NovelCapability.CategoriesRoute ? "分类" : "",
        SearchVisible: module.Route != NovelCapability.ReaderRoute) { SearchTargetId = "novel", SearchContextId = module.Route };
    public event EventHandler? HeaderChanged;
    internal void PostNotice(string text) => notice.Post(text);
    internal void Present()
    {
        if (disposed) return;
        PresentSidebar();
        bool reading = module.Route == NovelCapability.ReaderRoute && module.Session is not null;
        if (reading)
        {
            discoveryChannelBar?.Close();
            updateBook = null; updateDiscovery = null; discoveryCards.Clear(); discoveryChoices.Clear();
            body.Margin = new(0); body.Content = reader;
            purchase.Visibility = module.Session is { Chapter: { IsVip: true, IsPay: false } } session &&
                module.Sources.Sources.Any(s => s.InstallationId == session.Book.InstallationId && s.Enabled && s.Actions.Contains("payAction")) ? Visibility.Visible : Visibility.Collapsed;
            surface.Present(module.Session!, module.Library.Appearance);
            pageStatus.Text = surface.PageStatus;
            if (renderedRoute != module.Route || !ReferenceEquals(renderedSession, module.Session))
            { readerControls.Visibility = Visibility.Visible; module.RunUi(SavePositionAfterLayoutAsync); }
        }
        else
        {
            PauseReading(); body.Margin = module.Route == NovelCapability.BookRoute ? new(40, 20, 24, 20) : new(0);
            bool book = module.Route == NovelCapability.BookRoute;
            if (renderedRoute != module.Route || book && !ReferenceEquals(renderedSession, module.Session) || ReferenceEquals(body.Content, reader))
            {
                discoveryChannelBar?.Close(); discoveryChannelBar = null;
                updateBook = null; updateDiscovery = null; discoveryCards.Clear(); discoveryChoices.Clear();
                if (body.Content is ScrollViewer old) scrollPositions[renderedRoute] = old.VerticalOffset;
                body.Content = book ? BuildBook() : BuildDiscovery(module.Route == NovelCapability.CategoriesRoute);
                if (body.Content is ScrollViewer scroll && scrollPositions.TryGetValue(module.Route, out double position))
                    scroll.Loaded += (_, _) => scroll.ChangeView(null, position, null, true);
            }
            if (book) updateBook?.Invoke();
            else PresentDiscovery();
        }
        renderedRoute = module.Route; renderedSession = module.Session; HeaderChanged?.Invoke(this, EventArgs.Empty);
        PresentDiscoveryFailure();
    }
    private FrameworkElement BuildBook()
    {
        var panel = new StackPanel();
        updateBook = null;
        if (module.Session is not { } session) return panel;
        var summary = new DockPanel { Margin = new(0, 0, 0, 24) };
        var cover = new ContentControl { Width = 132, Height = 190, Margin = new(0, 0, 24, 0) };
        DockPanel.SetDock(cover, Dock.Left); summary.Children.Add(cover);
        var info = new StackPanel();
        var name = NovelUi.Text("", 26); var author = NovelUi.Text("", muted: true);
        var source = NovelUi.Text("", muted: true); var latest = NovelUi.Text("", muted: true);
        info.Children.Add(name); info.Children.Add(author); info.Children.Add(source); info.Children.Add(latest);
        var read = NovelUi.Button("开始阅读", () => module.RunUi(ct => module.StartReadingAsync(ct)));
        var shelf = NovelUi.Button("加入书架", () => module.RunUi(ct => session.ShelfAsync(ct)));
        info.Children.Add(NovelUi.Bar(read, shelf, NovelUi.Button("换源", () => module.RunUi(SwitchSourceAsync))));
        summary.Children.Add(info); panel.Children.Add(summary);
        var intro = NovelUi.Text(""); panel.Children.Add(intro); panel.Children.Add(NovelUi.Text("目录", 19));
        var cancel = NovelUi.Button("取消加载", session.Cancel);
        panel.Children.Add(NovelUi.Bar(NovelUi.Button("打开目录", () => module.RunUi(CatalogAsync)),
            NovelUi.Button("刷新目录", () => module.RunUi(ct => session.LoadMetadataAsync(ct))), cancel));
        var loading = NovelUi.Text("加载中…", muted: true); panel.Children.Add(loading);
        var chapters = new StackPanel(); panel.Children.Add(chapters);
        NovelBook? displayedBook = null;
        IReadOnlyList<NovelChapter>? displayedChapters = null;
        IReadOnlyList<NovelSource>? displayedSources = null;
        IReadOnlyList<NovelEntry>? displayedEntries = null;
        NovelEntry? displayedEntry = null;
        var chapterButtons = new Dictionary<(string Url, int Index), Button>();
        updateBook = () =>
        {
            var book = session.Book;
            bool bookChanged = displayedBook != book;
            if (bookChanged)
            {
                if (displayedBook is null || CoverIdentity(displayedBook) != CoverIdentity(book))
                    cover.Content = new NovelImage(module.Sources, book) { Width = 132, Height = 190 };
                name.Text = book.Name; author.Text = book.Author; latest.Text = book.LatestChapterTitle; intro.Text = book.Intro;
                displayedBook = book;
            }
            if (bookChanged || !ReferenceEquals(displayedSources, module.Sources.Sources))
            {
                source.Text = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == book.InstallationId)?.Name ?? "书源不可用";
                displayedSources = module.Sources.Sources;
            }
            if (!ReferenceEquals(displayedEntry, session.Entry))
            {
                read.Content = session.Entry.Anchor is null ? "开始阅读" : "继续阅读";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(read, (string)read.Content);
                displayedEntry = session.Entry;
            }
            read.IsEnabled = !session.Loading; cancel.IsEnabled = session.Loading;
            loading.Visibility = session.Loading ? Visibility.Visible : Visibility.Collapsed;
            if (!ReferenceEquals(displayedEntries, module.Library.Entries))
            {
                displayedEntries = module.Library.Entries;
                string label = displayedEntries.Any(e => e.BookId == session.Entry.BookId && e.OnShelf) ? "移出书架" : "加入书架";
                if (!Equals(shelf.Content, label))
                { shelf.Content = label; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shelf, label); }
            }
            if (!ReferenceEquals(displayedChapters, session.Chapters))
            {
                displayedChapters = session.Chapters;
                var desired = session.Chapters.Take(8).ToArray();
                var identities = desired.Select(c => (c.ChapterUrl, c.Index)).ToHashSet();
                foreach (var key in chapterButtons.Keys.Where(key => !identities.Contains(key)).ToArray())
                { chapters.Children.Remove(chapterButtons[key]); chapterButtons.Remove(key); }
                for (int index = 0; index < desired.Length; index++)
                {
                    var chapter = desired[index]; var key = (chapter.ChapterUrl, chapter.Index);
                    if (!chapterButtons.TryGetValue(key, out var button))
                    {
                        int chapterIndex = chapter.Index;
                        button = NovelUi.Button(chapter.Name, () => module.RunUi(async ct =>
                        { await session.ReadAsync(chapterIndex, token: ct); module.Request(NovelCapability.ReaderRoute); }));
                        chapterButtons.Add(key, button);
                    }
                    button.Content = chapter.Name;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, chapter.Name);
                    PlaceChild(chapters, button, index);
                }
            }
        };
        updateBook();
        return NovelUi.Scroll(panel);
    }
    private void BuildReader()
    {
        reader.Children.Add(surface);
        readerControls.VerticalAlignment = VerticalAlignment.Top; readerControls.Margin = new(16);
        readerControls.UseResource(Panel.BackgroundProperty, "MeowFilm.Brush.Surface");
        readerControls.Children.Add(NovelUi.Bar(NovelUi.Button("返回书籍", () => module.Request(NovelCapability.BookRoute)),
            NovelUi.Button("目录", () => module.RunUi(CatalogAsync)), NovelUi.Button("换源", () => module.RunUi(SwitchSourceAsync)),
            NovelUi.Button("加入／移出书架", () => module.RunUi(ct => module.Session!.ShelfAsync(ct))),
            NovelUi.Button("排版", () => module.RunUi(AppearanceAsync))));
        readerControls.Children.Add(NovelUi.Bar(NovelUi.Button("上一章", () => module.RunUi(ct => ChangeChapterAsync((module.Session?.Chapter?.Index ?? 0) - 1, token: ct))),
            NovelUi.Button("上一页", () => module.RunUi(async () => { await surface.TurnAsync(-1); })),
            NovelUi.Button("下一页", () => module.RunUi(async () => { await surface.TurnAsync(1); })),
            NovelUi.Button("下一章", () => module.RunUi(ct => ChangeChapterAsync((module.Session?.Chapter?.Index ?? 0) + 1, token: ct))),
            NovelUi.Button("自动阅读", () => { automatic = !automatic; autoTicks = 0; if (automatic) { readerControls.Visibility = Visibility.Collapsed; auto.Start(); } else PauseReading(); }),
            purchase));
        var slider = progress;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "章内进度");
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => surface.Seek(slider.Value / 100)), true);
        slider.KeyUp += (_, e) => { if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End) surface.Seek(slider.Value / 100); };
        readerControls.Children.Add(NovelUi.Bar(pageStatus, slider, NovelUi.Button("隐藏工具栏", () => readerControls.Visibility = Visibility.Collapsed)));
        reader.Children.Add(readerControls);
    }
    private async Task ChangeChapterAsync(int index, bool keepAutomatic = false, CancellationToken token = default)
    {
        if (!keepAutomatic) PauseReading();
        if (token == default) token = module.PresentationToken;
        if (module.Session is not { } session || index < 0 || index >= session.Chapters.Count) return;
        await SavePositionAsync(); await session.ReadAsync(index, token: token);
        await SavePositionAfterLayoutAsync();
    }
    private void AutoTick(object? sender, object e)
    {
        if (!automaticOperation.IsCompleted) return;
        automaticOperation = AdvanceAutomaticAsync();
    }
    private async Task AdvanceAutomaticAsync()
    {
        if (advancing || !automatic || !DesktopVisual.IsVisible(this) || attachedWindow?.IsActive != true || module.Route != NovelCapability.ReaderRoute || module.Session?.Loading == true) { if (attachedWindow?.IsActive != true) PauseReading(); return; }
        advancing = true;
        try
        {
            var settings = module.Library.Appearance.ForReading();
            if (settings.Mode == "scroll")
            {
                if (!surface.AdvanceScroll((12 + settings.AutoSpeed * 12) * .05) && !await surface.TurnAsync(1)) PauseReading();
            }
            else if (++autoTicks * .05 >= 25 - settings.AutoSpeed * 2)
            { autoTicks = 0; if (!await surface.TurnAsync(1)) PauseReading(); }
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
        var panel = new DockPanel(); var tools = NovelUi.Bar(); DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        var labels = chapters.ToArray();
        if (module.Library.Appearance.ReadingConversion != "none")
        {
            var result = await module.Rules.InvokeAsync(new { operation = "transform", title = "",
                blocks = labels.Select(c => new NovelBlock("text", c.Name)).ToArray(), rules = Array.Empty<NovelRule>(),
                conversion = module.Library.Appearance.ReadingConversion }, token);
            var names = result.GetProperty("blocks");
            for (int i = 0; i < labels.Length; i++) labels[i] = labels[i] with { Name = NovelJson.Text(names[i], "text") };
        }
        var list = new ListView { ItemsSource = labels, MaxHeight = Math.Max(200, DesktopWindow.GetWindow(this)?.Root.ActualHeight - 300 ?? 420),
            SelectedIndex = Math.Clamp(module.Session?.Chapter?.Index ?? 0, 0, Math.Max(0, chapters.Count - 1)) };
        tools.Children.Add(NovelUi.Button("顺序", () => list.ItemsSource = labels));
        tools.Children.Add(NovelUi.Button("倒序", () => list.ItemsSource = labels.Reverse().ToArray()));
        tools.Children.Add(NovelUi.Button("顶部", () => { if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]); }));
        tools.Children.Add(NovelUi.Button("底部", () => { if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]); }));
        tools.Children.Add(NovelUi.Button("当前章", () => { if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem); }));
        panel.Children.Add(list);
        bool selected = await NovelUi.DialogAsync(this, "选择章节", panel, "打开章节", _ => Task.FromResult(list.SelectedItem is NovelChapter), token);
        return selected && list.SelectedItem is NovelChapter chapter ? chapter.Index : null;
    }
    private async Task CatalogAsync(CancellationToken token)
    {
        if (module.Session is not { } session) return;
        if (session.Chapters.Count == 0) await session.LoadMetadataAsync(token);
        int? index = await SelectFromCatalogAsync(session.Chapters, token);
        if (index is null) return;
        await SavePositionAsync(); await session.ReadAsync(index.Value, token: token); module.Request(NovelCapability.ReaderRoute);
    }
    private async Task SwitchSourceAsync(CancellationToken token)
    {
        PauseReading(); if (module.Session is not { } session) return;
        await SavePositionAsync();
        var search = new NovelSearch(module.Sources, module.Rules);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panel = new DockPanel(); var tools = NovelUi.Bar(); DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        var list = new ListView { MinHeight = 200, MaxHeight = Math.Max(200, DesktopWindow.GetWindow(this)?.Root.ActualHeight - 300 ?? 420) };
        var group = new ComboBox { Width = 180, ItemsSource = module.Sources.Sources.Select(s => s.Group).Prepend("全部分组").Distinct().ToArray(), SelectedIndex = 0 };
        tools.Children.Add(group);
        void Refresh()
        {
            var candidates = search.Groups.SelectMany(g => g.GetProperty("candidates").EnumerateArray()).Where(g => module.Sources.Sources.Any(s => s.InstallationId == NovelJson.Text(g, "installationId"))).Select(g =>
            {
                var source = module.Sources.Sources.First(s => s.InstallationId == NovelJson.Text(g, "installationId")); return NovelBook.From(g, source);
            }).Where(b => group.SelectedIndex == 0 || module.Sources.Sources.First(s => s.InstallationId == b.InstallationId).Group == group.SelectedItem as string).ToArray();
            list.ItemsSource = candidates.Select(book => new SourceCandidate(book,
                module.Sources.Sources.First(s => s.InstallationId == book.InstallationId).Name +
                (book.InstallationId == session.Book.InstallationId ? " · 当前" : "") + " · " + book)).ToArray();
        }
        search.Changed += (_, _) => Refresh(); group.SelectionChanged += (_, _) => Refresh();
        tools.Children.Add(NovelUi.Button("刷新", () => module.RunUi(_ => search.StartAsync(session.Book.Name, cancel.Token, restart: true))));
        tools.Children.Add(NovelUi.Button("加载更多", () => module.RunUi(() => search.LoadMoreAsync(cancel.Token))));
        panel.Children.Add(list);
        var loading = search.StartAsync(session.Book.Name, cancel.Token);
        try
        {
        bool selected = await NovelUi.DialogAsync(this, "换源", panel, "切换书源", async ct =>
        {
            if (list.SelectedItem is not SourceCandidate candidateItem) return false;
            var book = candidateItem.Book;
            var target = await module.Rules.InvokeAsync(new { operation = "identity", book = session.Book.Json() }, ct);
            var resolved = new NovelBookSession(module.Sources, module.Library, module.Rules, new("candidate", "", book));
            await resolved.LoadMetadataAsync(ct);
            book = resolved.Book;
            var candidate = await module.Rules.InvokeAsync(new { operation = "identity", book = book.Json() }, ct);
            string author = NovelJson.Text(target, "author"), nextAuthor = NovelJson.Text(candidate, "author");
            if (NovelJson.Text(target, "name") != NovelJson.Text(candidate, "name") ||
                author.Length > 0 && nextAuthor.Length > 0 && author != nextAuthor) throw new InvalidDataException("BOOK_IDENTITY_MISMATCH");
            if ((author.Length == 0 || nextAuthor.Length == 0) && !await NovelUi.ConfirmAsync(panel, "确认换源", $"切换至《{book.Name}》 · {book.Author}？", "切换", ct)) return false;
            await session.SwitchSourceAsync(book, ct); return true;
        }, cancel.Token);
        if (selected) Present();
        }
        finally
        {
            cancel.Cancel(); search.Cancel(); try { await loading; } catch (OperationCanceledException) { }
            await search.PrepareShutdownAsync();
        }
    }
    private async Task PurchaseAsync(CancellationToken token)
    {
        PauseReading(); if (module.Session is not { Chapter: { IsVip: true, IsPay: false } chapter } session) return;
        var source = module.Sources.Require(session.Book);
        if (!source.Actions.Contains("payAction")) return;
        string bookIdentity = session.Book.SourceIdentity;
        if (await NovelUi.ConfirmAsync(this, "购买本章", $"确认通过“{source.Name}”购买《{session.Book.Name}》的“{chapter.Name}”？", "确认购买", token) &&
            ReferenceEquals(module.Session, session) && session.Book.SourceIdentity == bookIdentity && chapter == session.Chapter && module.Sources.IsCurrent(source))
            await session.PurchaseAsync(token);
    }
    private async Task AppearanceAsync(CancellationToken token)
    {
        PauseReading(); var editor = new NovelAppearanceEditor(module.Library.Appearance);
        await NovelUi.DialogAsync(this, "阅读排版", editor, "保存", async ct =>
        {
            await SavePositionAsync(); var next = editor.Read(); var prior = module.Library.Appearance;
            await module.Library.SaveAppearanceAsync(next, ct);
            if (next.Conversion != prior.Conversion && module.Session?.Chapter is not null)
                await module.Session.ReprocessAsync(ct);
            return true;
        }, token, applicationOverlay: true);
    }
    internal Task<object?> InteractAsync(System.Text.Json.JsonElement request, CancellationToken token)
    { PauseReading(); return NovelWebInteraction.ShowAsync(this, module.Rules, request, token); }
    private void AttachWindow()
    {
        DetachWindow(); attachedWindow = DesktopWindow.GetWindow(this);
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
    private sealed record SourceCandidate(NovelBook Book, string Label) { public override string ToString() => Label; }
    internal async Task DrainSavesAsync() { await automaticOperation; await saves.PauseAsync(); }
    internal Task DrainImagesAsync() => surface.DrainImagesAsync();
    internal string DebugReaderPageStatus => surface.PageStatus;
    internal Task<bool> DebugTurnReaderPageAsync(int delta) => surface.TurnAsync(delta);
    private void DiscoveryBooksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => discoveryBooksChanged = true;
    public void Dispose() { if (disposed) return; disposed = true; module.DiscoverBooks.CollectionChanged -= DiscoveryBooksChanged; visibility.Dispose(); discoveryChannelBar?.Close(); PauseReading(); save.Stop(); DetachWindow(); surface.Dispose(); }
}
