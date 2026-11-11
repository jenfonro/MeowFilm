using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class NovelView : UserControl, IDesktopHeaderSource, IDisposable
{
    private readonly NovelCapability module;
    private readonly Grid root = new();
    private readonly ContentControl body = new();
    private readonly NoticeOutlet notice = new();
    private readonly NovelReadingSurface surface;
    private readonly Grid reader = new();
    private readonly StackPanel readerControls = new();
    private readonly TextBlock pageStatus = new();
    private readonly DispatcherTimer auto;
    private readonly DispatcherTimer save;
    private readonly NovelWork saves = new();
    private bool automatic, advancing, disposed;
    private Task automaticOperation = Task.CompletedTask;
    private int autoTicks;
    private string filter = "";
    private bool history;
    private string renderedRoute = "";
    private NovelBookSession? renderedSession;
    private Window? attachedWindow;
    private object? presentationStamp;
    private readonly Dictionary<string, double> scrollPositions = [];
    private readonly Button purchase;
    private readonly Slider progress = new() { Minimum = 0, Maximum = 100, Width = 240, Margin = new(10) };
    public NovelView(NovelCapability module)
    {
        this.module = module; NovelUi.Theme(this); surface = new(module.Sources) { ReportError = module.Notify };
        Content = root; root.Children.Add(body); root.Children.Add(notice);
        purchase = NovelUi.Button("购买本章", () => module.RunUi(PurchaseAsync));
        BuildReader();
        auto = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        auto.Tick += AutoTick;
        save = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        save.Tick += (_, _) => { save.Stop(); module.RunUi(_ => SavePositionAsync()); };
        surface.PositionChanged += (_, _) => { pageStatus.Text = surface.PageStatus; progress.Value = surface.CaptureAnchor().Ratio * 100; save.Stop(); save.Start(); };
        surface.LayoutCompleted += (_, _) => { pageStatus.Text = surface.PageStatus; progress.Value = surface.CaptureAnchor().Ratio * 100; };
        surface.ControlsRequested += (_, _) => { PauseReading(); readerControls.Visibility = readerControls.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
        surface.UserInteracted += (_, _) => PauseReading();
        surface.CrossChapterAsync = index => ChangeChapterAsync(index, automatic);
        surface.RunInteraction = module.RunUi;
        IsVisibleChanged += (_, _) =>
        {
            if (disposed) return;
            if (!IsVisible) { module.CancelPresentation(); module.Session?.Cancel(); module.RunUi(_ => SavePositionAsync()); }
            else if (module.Route == NovelCapability.ReaderRoute)
                module.RunUi(module.ReprocessCurrentAsync);
        };
        Loaded += (_, _) => AttachWindow(); Unloaded += (_, _) => { PauseReading(); DetachWindow(); };
    }
    public DesktopHeaderContribution Header => new(module.Route == NovelCapability.HomeRoute ? "小说" : module.Route == NovelCapability.ShelfRoute ? "书架" : "",
        SearchVisible: module.Route != NovelCapability.ReaderRoute) { SearchTargetId = "novel", SearchContextId = module.Route };
    public event EventHandler? HeaderChanged;
    internal void Present()
    {
        if (disposed) return;
        notice.Message = module.Notice;
        bool reading = module.Route == NovelCapability.ReaderRoute && module.Session is not null;
        if (reading)
        {
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
            PauseReading(); body.Margin = new(40, 20, 24, 20);
            object stamp = module.Route switch
            {
                NovelCapability.ShelfRoute => (module.Route, module.Library.Entries, history),
                NovelCapability.BookRoute => (module.Route, module.Session, module.Session?.Book, module.Session?.Chapters,
                    module.Session?.Loading, module.Library.Entries.Any(e => e.BookId == module.Session?.Entry.BookId && e.OnShelf)),
                _ => (module.Route, module.Sources.Sources, module.Kinds, module.ExploreKind, module.DiscoverBooks.Count,
                    module.LoadingDiscovery, module.MoreDiscovery, module.DiscoveryError,
                    module.Library.Entries.Where(e => e.ReadAt is not null).OrderByDescending(e => e.ReadAt).FirstOrDefault())
            };
            if (!Equals(stamp, presentationStamp) || ReferenceEquals(body.Content, reader))
            {
                if (body.Content is ScrollViewer old) scrollPositions[renderedRoute] = old.VerticalOffset;
                body.Content = module.Route switch
                {
                    NovelCapability.ShelfRoute => BuildShelf(), NovelCapability.BookRoute => BuildBook(), _ => BuildHome()
                };
                presentationStamp = stamp;
                if (body.Content is ScrollViewer scroll && scrollPositions.TryGetValue(module.Route, out double position))
                    scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(position);
            }
        }
        renderedRoute = module.Route; renderedSession = module.Session; HeaderChanged?.Invoke(this, EventArgs.Empty);
    }
    private FrameworkElement BuildHome()
    {
        var panel = new StackPanel();
        var toolbar = NovelUi.Bar();
        var sources = new ComboBox { ItemsSource = module.Sources.Sources.Where(s => s.Enabled && s.EnabledExplore && s.Actions.Contains("explore")).ToArray(),
            SelectedItem = module.ExploreSource, MinWidth = 180, Margin = new(0, 0, 12, 12) };
        System.Windows.Automation.AutomationProperties.SetName(sources, "发现书源");
        sources.SelectionChanged += (_, _) => { if (sources.SelectedItem is NovelSource source) module.RunUi(ct => module.LoadDiscoveryAsync(source, null, true, ct)); };
        toolbar.Children.Add(sources);
        var kinds = new ComboBox { ItemsSource = module.Kinds, SelectedItem = module.ExploreKind, MinWidth = 180, Margin = new(0, 0, 12, 12) };
        System.Windows.Automation.AutomationProperties.SetName(kinds, "发现分类");
        kinds.SelectionChanged += (_, _) => { if (kinds.SelectedItem is NovelKind kind && kind.Url.Length > 0) module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, kind, false, ct)); };
        toolbar.Children.Add(kinds); toolbar.Children.Add(NovelUi.Button("刷新", () => module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true, ct)), !module.LoadingDiscovery));
        toolbar.Children.Add(NovelUi.Button("取消", module.CancelDiscovery, module.LoadingDiscovery));
        panel.Children.Add(toolbar);
        var last = module.Library.Entries.Where(e => e.ReadAt is not null).OrderByDescending(e => e.ReadAt).FirstOrDefault();
        if (last is not null)
        {
            panel.Children.Add(NovelUi.Text("继续阅读", 19)); panel.Children.Add(BookCard(last.Book, last.Progress));
        }
        panel.Children.Add(NovelUi.Text(module.ExploreKind?.Title ?? "发现", 19));
        var books = new WrapPanel(); foreach (var book in module.DiscoverBooks) books.Children.Add(BookCard(book)); panel.Children.Add(books);
        if (module.DiscoverBooks.Count == 0)
        {
            panel.Children.Add(NovelUi.Text(module.LoadingDiscovery ? "加载中…" : module.DiscoveryError.Length > 0 ? module.DiscoveryError : module.ExploreSource is null ? "无可用发现书源" : "暂无书籍", muted: true));
            if (!module.LoadingDiscovery) panel.Children.Add(module.DiscoveryError.Length > 0 ?
                NovelUi.Button("重试", () => module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true, ct))) :
                NovelUi.Button("导入书源", () => module.RunUi(ct => module.ImportSourcesAsync(this, ct))));
        }
        if (module.MoreDiscovery) panel.Children.Add(NovelUi.Button("加载更多", () => module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false, ct)), !module.LoadingDiscovery));
        return NovelUi.Scroll(panel);
    }
    private FrameworkElement BuildShelf()
    {
        var panel = new DockPanel();
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var tabs = NovelUi.Bar(NovelUi.Button(history ? "书架" : "书架 ✓", () => { history = false; presentationStamp = null; Present(); }),
            NovelUi.Button(history ? "历史 ✓" : "历史", () => { history = true; presentationStamp = null; Present(); })); header.Children.Add(tabs);
        var input = NovelUi.Input("筛选书架", filter); header.Children.Add(input);
        var books = new WrapPanel();
        void Fill()
        {
            books.Children.Clear();
            var entries = module.Library.Entries.Where(e => history ? e.ReadAt is not null : e.OnShelf)
                .Where(e => (e.Book.Name + " " + e.Book.Author + " " + e.Book.InstallationId + " " + e.Progress).Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(e => !history && e.PinnedAt is not null).ThenByDescending(e => history ? e.ReadAt : e.PinnedAt ?? e.ReadAt ?? e.AddedAt).ToArray();
            foreach (var entry in entries)
            {
                var card = BookCard(entry.Book, entry.Progress); var menu = new ContextMenu();
                void Item(string label, Func<CancellationToken, Task> action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => module.RunUi(action); menu.Items.Add(item); }
                if (history)
                {
                    Item(entry.OnShelf ? "移出书架" : "加入书架", ct => module.Library.SetShelfAsync(entry.BookId, !entry.OnShelf, ct));
                    Item("删除历史", ct => module.Library.DeleteHistoryAsync(entry.BookId, ct));
                }
                else
                {
                    Item(entry.PinnedAt is null ? "置顶" : "取消置顶", ct => module.Library.PinAsync(entry.BookId, ct));
                    Item("移出书架", ct => module.Library.SetShelfAsync(entry.BookId, false, ct));
                }
                card.ContextMenu = menu; books.Children.Add(card);
            }
            if (entries.Length == 0) {
                var empty = new StackPanel(); empty.Children.Add(NovelUi.Text(history ? "暂无阅读历史" : "书架为空", muted: true));
                empty.Children.Add(NovelUi.Button("发现小说", () => module.Request(NovelCapability.HomeRoute))); books.Children.Add(empty);
            }
        }
        input.TextChanged += (_, _) => { filter = input.Text; Fill(); }; Fill();
        panel.Children.Add(NovelUi.Scroll(books)); return panel;
    }
    private FrameworkElement BuildBook()
    {
        var panel = new StackPanel();
        if (module.Session is not { } session) return panel;
        var book = session.Book;
        var summary = new DockPanel { Margin = new(0, 0, 0, 24) };
        var cover = new NovelImage(module.Sources, book) { Width = 132, Height = 190, Margin = new(0, 0, 24, 0) };
        DockPanel.SetDock(cover, Dock.Left); summary.Children.Add(cover);
        var info = new StackPanel(); info.Children.Add(NovelUi.Text(book.Name, 26)); info.Children.Add(NovelUi.Text(book.Author, muted: true));
        info.Children.Add(NovelUi.Text(module.Sources.Sources.FirstOrDefault(s => s.InstallationId == book.InstallationId)?.Name ?? "书源不可用", muted: true));
        info.Children.Add(NovelUi.Text(book.LatestChapterTitle, muted: true));
        info.Children.Add(NovelUi.Bar(NovelUi.Button(session.Entry.Anchor is null ? "开始阅读" : "继续阅读", () => module.RunUi(ct => module.StartReadingAsync(ct)), !session.Loading),
            NovelUi.Button(module.Library.Entries.Any(e => e.BookId == session.Entry.BookId && e.OnShelf) ? "移出书架" : "加入书架", () => module.RunUi(ct => session.ShelfAsync(ct))),
            NovelUi.Button("换源", () => module.RunUi(SwitchSourceAsync)))); summary.Children.Add(info); panel.Children.Add(summary);
        panel.Children.Add(NovelUi.Text(book.Intro)); panel.Children.Add(NovelUi.Text("目录", 19));
        panel.Children.Add(NovelUi.Bar(NovelUi.Button("打开目录", () => module.RunUi(CatalogAsync)),
            NovelUi.Button("刷新目录", () => module.RunUi(ct => session.LoadMetadataAsync(ct))),
            NovelUi.Button("取消加载", session.Cancel, session.Loading)));
        if (session.Loading) panel.Children.Add(NovelUi.Text("加载中…", muted: true));
        foreach (var chapter in session.Chapters.Take(8)) panel.Children.Add(NovelUi.Button(chapter.Name, () => module.RunUi(async ct =>
        { await session.ReadAsync(chapter.Index, token: ct); module.Request(NovelCapability.ReaderRoute); })));
        return NovelUi.Scroll(panel);
    }
    internal FrameworkElement BookCard(NovelBook book, string subtitle = "")
    {
        var panel = new StackPanel { Width = 148, Margin = new(0, 0, 22, 24) };
        panel.Children.Add(new NovelImage(module.Sources, book) { Width = 148, Height = 200, Margin = new(0, 0, 0, 10) });
        var title = NovelUi.Text(book.Name, 15); title.MaxHeight = 44; panel.Children.Add(title);
        var author = NovelUi.Text(book.Author, 12, true); panel.Children.Add(author);
        var detail = NovelUi.Text(subtitle, 12, true); detail.MaxHeight = 36; panel.Children.Add(detail);
        var button = NovelUi.Button(book.Name, () => module.RunUi(ct => module.OpenBookAsync(book, ct)));
        button.Content = panel; button.Padding = new(0); button.Margin = new(0); button.HorizontalContentAlignment = HorizontalAlignment.Left;
        return button;
    }
    private void BuildReader()
    {
        reader.Children.Add(surface);
        readerControls.VerticalAlignment = VerticalAlignment.Top; readerControls.Margin = new(16);
        readerControls.SetResourceReference(Panel.BackgroundProperty, "MeowFilm.Brush.Surface");
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
        System.Windows.Automation.AutomationProperties.SetName(slider, "章内进度");
        slider.PreviewMouseLeftButtonUp += (_, _) => surface.Seek(slider.Value / 100);
        slider.PreviewKeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) surface.Seek(slider.Value / 100); };
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
    private void AutoTick(object? sender, EventArgs e)
    {
        if (!automaticOperation.IsCompleted) return;
        automaticOperation = AdvanceAutomaticAsync();
    }
    private async Task AdvanceAutomaticAsync()
    {
        if (advancing || !automatic || !IsVisible || attachedWindow?.IsActive != true || module.Route != NovelCapability.ReaderRoute || module.Session?.Loading == true) { if (attachedWindow?.IsActive != true) PauseReading(); return; }
        advancing = true;
        try
        {
            var settings = module.Library.Appearance;
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
        await Dispatcher.Yield(DispatcherPriority.Render);
        if (IsVisible && module.Route == NovelCapability.ReaderRoute) { surface.UpdateLayout(); await SavePositionAsync(); }
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
        if (module.Library.Appearance.Conversion != "none")
        {
            var result = await module.Rules.InvokeAsync(new { operation = "transform", title = "",
                blocks = labels.Select(c => new NovelBlock("text", c.Name)).ToArray(), rules = Array.Empty<NovelRule>(),
                conversion = module.Library.Appearance.Conversion }, token);
            var names = result.GetProperty("blocks");
            for (int i = 0; i < labels.Length; i++) labels[i] = labels[i] with { Name = NovelJson.Text(names[i], "text") };
        }
        var list = new ListBox { ItemsSource = labels, MaxHeight = Math.Max(200, Window.GetWindow(this)?.ActualHeight - 300 ?? 420),
            SelectedIndex = Math.Clamp(module.Session?.Chapter?.Index ?? 0, 0, Math.Max(0, chapters.Count - 1)) };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
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
        var list = new ListBox { MinHeight = 200, MaxHeight = Math.Max(200, Window.GetWindow(this)?.ActualHeight - 300 ?? 420) };
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
        if (selected) { presentationStamp = null; Present(); }
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
        await NovelUi.DialogAsync(this, "阅读排版", NovelUi.Scroll(editor), "保存", async ct =>
        {
            await SavePositionAsync(); var next = editor.Read(); var prior = module.Library.Appearance;
            await module.Library.SaveAppearanceAsync(next, ct);
            if (next.Conversion != prior.Conversion && module.Session?.Chapter is not null)
                await module.Session.ReprocessAsync(ct);
            return true;
        }, token);
    }
    internal Task<object?> InteractAsync(System.Text.Json.JsonElement request, CancellationToken token)
    { PauseReading(); return NovelWebInteraction.ShowAsync(this, module.Rules, request, token); }
    private void AttachWindow()
    {
        DetachWindow(); attachedWindow = Window.GetWindow(this);
        if (attachedWindow is not null) { attachedWindow.Deactivated += WindowDeactivated; attachedWindow.StateChanged += WindowDeactivated; }
    }
    private void WindowDeactivated(object? sender, EventArgs e) { PauseReading(); module.RunUi(_ => SavePositionAsync()); }
    private void DetachWindow()
    {
        if (attachedWindow is not null) { attachedWindow.Deactivated -= WindowDeactivated; attachedWindow.StateChanged -= WindowDeactivated; attachedWindow = null; }
    }
    private sealed record SourceCandidate(NovelBook Book, string Label) { public override string ToString() => Label; }
    internal async Task DrainSavesAsync() { await automaticOperation; await saves.PauseAsync(); }
    internal void Resume() { saves.Resume(); surface.ResumeImages(); }
    internal Task DrainImagesAsync() => surface.DrainImagesAsync();
    public void Dispose() { if (disposed) return; disposed = true; PauseReading(); save.Stop(); DetachWindow(); surface.Dispose(); }
}
