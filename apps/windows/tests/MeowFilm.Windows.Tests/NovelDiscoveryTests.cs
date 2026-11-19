using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using FontWeights = Microsoft.UI.Text.FontWeights;
using Color = Windows.UI.Color;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class NovelDiscoveryTests
{
    private static readonly NovelKind[] Kinds =
    [
        new("男频全部", ""), new("都市", "https://catalog.invalid/urban"), new("奇幻", "https://catalog.invalid/fantasy"),
        new("男频榜单", ""), new("月票榜", "https://catalog.invalid/month"), new("畅销榜", "https://catalog.invalid/sales"),
        new("女频全部", ""), new("言情", "https://catalog.invalid/romance"),
        new("女频榜单", ""), new("完结榜", "https://catalog.invalid/completed")
    ];

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel discovery kinds: source groups preserve all entries and rankings require explicit evidence", Grouping);
        yield return new("novel discovery home: initial selection and return from categories load only real rankings",
            () => NovelTests.Sta(HomeSelection));
        yield return new("novel discovery home: a source without rankings never falls back to an ordinary category",
            () => NovelTests.Sta(NoRankings));
        yield return new("novel discovery categories: one channel row shares compact themed filter chrome and settings navigation",
            () => NovelTests.Sta(CategoryPresentation));
        yield return new("novel discovery channels: overflow floats all choices without moving content and keeps selected channels visible",
            () => NovelTests.Sta(ChannelOverflow));
        yield return new("novel discovery notices: import results emit once and do not replay on channel changes or page refresh",
            () => NovelTests.Sta(ImportNotice));
        yield return new("novel discovery response: required web interaction stays a visible failure rather than an empty ranking",
            () => NovelTests.Sta(WebRequired));
    }

    private static Task Grouping()
    {
        var groups = NovelDiscoveryKinds.Groups(Kinds);
        Check(groups.Select(group => group.Title).SequenceEqual(new[] { "男频全部", "男频榜单", "女频全部", "女频榜单" }));
        Check(groups.SelectMany(group => group.Kinds).SequenceEqual(Kinds.Where(kind => kind.Url.Length > 0)));
        Check(NovelDiscoveryKinds.Groups(Kinds, true).SelectMany(group => group.Kinds)
            .Select(kind => kind.Title).SequenceEqual(new[] { "月票榜", "畅销榜", "完结榜" }));
        NovelKind[] ungrouped =
        [
            new("最新", "https://rank.invalid/latest"), new("热门", "https://catalog.invalid/topic"),
            new("全部", "https://catalog.invalid/category?keyword=rank"),
            new("完结", "https://catalog.invalid/completed#ranking"), new("专题", "//rank.invalid/latest"),
            new("题材", "category?rank=week"),
            new("新书排行", "https://catalog.invalid/new"), new("Monthly ranking", "https://catalog.invalid/month"),
            new("订阅", "https://catalog.invalid/rankList/month"), new("周度", "/rankings/week?keyword=category"),
            new("日度", "charts/day"), new("题材", "")
        ];
        Check(NovelDiscoveryKinds.Groups(ungrouped, true).SelectMany(group => group.Kinds)
            .Select(kind => kind.Title).SequenceEqual(new[] { "新书排行", "Monthly ranking", "订阅", "周度", "日度" }));
        Check(NovelDiscoveryKinds.Select(Kinds, Kinds[1], true) == Kinds[4]);
        Check(NovelDiscoveryKinds.Select(Kinds, Kinds[9], true) == Kinds[9]);
        Check(NovelDiscoveryKinds.Select(Kinds, null, false) == Kinds[1]);
        Check(!NovelDiscoveryKinds.IsRanking(Kinds, Kinds[1]) && NovelDiscoveryKinds.IsRanking(Kinds, Kinds[9]));
        NovelKind[] repeated =
        [
            new("未分组", "first"), new("重复频道", ""), new("甲", "a"),
            new("空频道", ""), new("重复频道", ""), new("乙", "b")
        ];
        Check(NovelDiscoveryKinds.Groups(repeated).Select(group => group.Title)
            .SequenceEqual(new[] { "", "重复频道", "重复频道" }), "Source order or distinct groups were merged.");
        Check(NovelDiscoveryKinds.Groups(repeated, true).Count == 0);
        return Task.CompletedTask;
    }

    private static async Task HomeSelection()
    {
        await using var store = new NovelTests.MemoryStore();
        await using var configurationStore = new NovelTests.MemoryStore();
        var runtime = new DiscoveryRuntime(Kinds);
        await using var module = new NovelCapability(Sources(runtime, configurationStore), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        Check(module.Sources.Sources.Single().Actions.Length == 0 && runtime.Calls.Count == 0);
        await module.LoadDiscoveryAsync(module.Sources.Sources.Single(), null, true);
        await module.NavigateAsync(NovelCapability.HomeRoute, default);
        Check(module.ExploreKind == Kinds[4] && ExploreUrls(runtime).SequenceEqual(new[] { Kinds[4].Url }));
        int requests = runtime.Calls.Count;
        await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
        Check(runtime.Calls.Count == requests && module.ExploreKind == Kinds[4], "An allowed shared selection was unnecessarily reloaded.");
        await module.LoadDiscoveryAsync(module.ExploreSource, Kinds[1], false);
        Check(module.ExploreKind == Kinds[1] && module.DiscoverBooks.Single().BookUrl == Kinds[1].Url + "/book");
        await module.NavigateAsync(NovelCapability.HomeRoute, default);
        Check(module.ExploreKind == Kinds[4] && module.DiscoverBooks.Single().BookUrl == Kinds[4].Url + "/book");
        Check(module.Kinds.SequenceEqual(Kinds) && store.Writes == 0 && configurationStore.Writes == 0,
            "Filtering changed source definitions or wrote native preferences.");
        var channels = Channels(module);
        Check(!NovelTests.Find<TextBlock>(module.View).Any(text =>
            (text.Text == module.ExploreKind!.Title || text.Text.Contains("本作品")) &&
            !channels.Any(channel => DesktopVisual.Contains(channel, text))),
            "Home repeated the selected ranking title or result count above its books.");
    }

    private static async Task NoRankings()
    {
        await using var store = new NovelTests.MemoryStore();
        await using var configurationStore = new NovelTests.MemoryStore();
        NovelKind[] ordinary = [new("全部", "https://catalog.invalid/all"), new("完结", "https://catalog.invalid/completed")];
        var runtime = new DiscoveryRuntime(ordinary);
        await using var module = new NovelCapability(Sources(runtime, configurationStore), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.HomeRoute, default);
        Check(module.DiscoveryState == NovelDiscoveryState.Empty && module.ExploreKind is null &&
            module.DiscoverBooks.Count == 0 && ExploreUrls(runtime).Length == 0);
        var window = NovelTestUi.Window(module.View); window.Activate();
        try
        {
            await Render(window);
            Check(NovelTests.Find<TextBlock>(module.View).Any(text => text.Text == "暂无可用榜单"));
            Check(!NovelTests.Find<RadioButton>(module.View).Any(tab => tab.GroupName == "novel.discovery.channels"),
                "The home exposed an ordinary category as a ranking.");
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default); await Render(window);
            Check(module.ExploreKind == ordinary[0] && module.DiscoverBooks.Count == 1);
            Check(NovelTests.Find<RadioButton>(module.View).Where(tab => tab.GroupName == "novel.discovery.channels")
                .Select(tab => tab.Content).SequenceEqual(new[] { "全部", "完结" }));
            await module.NavigateAsync(NovelCapability.HomeRoute, default); await Render(window);
            Check(module.ExploreKind is null && module.DiscoverBooks.Count == 0 && ExploreUrls(runtime).Length == 1);
            Check(NovelTests.Find<NovelBookGrid>(module.View).Single().Children.Count == 0);
        }
        finally { window.Close(); }
    }

    private static async Task CategoryPresentation()
    {
        await using var store = new NovelTests.MemoryStore();
        await using var configurationStore = new NovelTests.MemoryStore();
        var runtime = new DiscoveryRuntime(Kinds);
        await using var module = new NovelCapability(Sources(runtime, configurationStore), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
        var root = new Grid { Width = 900, Height = 700 }; root.Children.Add(module.View);
        var window = NovelTestUi.Window(root, 900, 760); window.Activate();
        try
        {
            await Render(window);
            var row = NovelTests.Find<Grid>(module.View).Single(grid => AutomationProperties.GetName(grid) == "当前频道筛选");
            Check(NovelTests.Find<RadioButton>(row).Select(choice => choice.Content).SequenceEqual(new[] { "都市", "奇幻" }));
            Check(!NovelTests.Find<TextBlock>(module.View).Any(text => text.Text == "分类" || text.Text.Contains("本作品")));
            var channels = Channels(module);
            Check(channels.Select(tab => tab.Content).SequenceEqual(new[] { "男频全部", "男频榜单", "女频全部", "女频榜单" }));
            Check(channels.All(tab => ReferenceEquals(tab.Style.BasedOn, NovelTestUi.Resource(module.View, "MeowFilm.SettingsArea")) &&
                tab.Height == 36), "Channel navigation did not reuse the settings style.");
            var source = NovelTests.Find<ComboBox>(module.View).Single(box => AutomationProperties.GetAutomationId(box) == "novel.discovery.source");
            var refresh = NovelTests.Find<Button>(module.View).Single(button => AutomationProperties.GetName(button) == "刷新");
            double Center(FrameworkElement element) => element.TransformToVisual(module.View).TransformPoint(new(0, element.ActualHeight / 2)).Y;
            Check(Math.Abs(Center(channels[0]) - Center(source)) < 1 && Math.Abs(Center(source) - Center(refresh)) < 1);
            channels[1].IsChecked = true; await Until(() => module.ExploreKind == Kinds[4] && !module.LoadingDiscovery);
            await Render(window);
            row = NovelTests.Find<Grid>(module.View).Single(grid => AutomationProperties.GetName(grid) == "当前频道筛选");
            var choices = NovelTests.Find<RadioButton>(row).ToArray();
            Check(choices.Select(choice => choice.Content).SequenceEqual(new[] { "月票榜", "畅销榜" }));
            Check(choices.All(choice => ReferenceEquals(choice.Style.BasedOn, NovelTestUi.Resource(module.View, "MeowFilm.FilterChip")) &&
                choice.ActualHeight <= 30));
            var label = NovelTests.Find<TextBlock>(row).Single(text => text.Text == "男频榜单");
            Check(Math.Abs(Center(label) - Center(choices[0])) < 1, "The plain channel label is not centered with its choices.");
            var capsule = NovelTests.Find<Border>(row).Single(border => ReferenceEquals(border.Style, NovelTestUi.Resource(module.View, "MeowFilm.FilterGroup")));
            Check(capsule.CornerRadius == new CornerRadius(18));
            var filterScroll = NovelTests.Find<ScrollViewer>(row).Single();
            Check(filterScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
                filterScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled);
            Check(choices[1].Focus(FocusState.Programmatic), "The filter cannot receive keyboard focus.");
            Select(choices[1]);
            await Until(() => module.ExploreKind == Kinds[5] && !module.LoadingDiscovery); await Render(window);
            Check(DesktopVisual.ContainsFocus(NovelTests.Find<RadioButton>(module.View).Single(choice => Equals(choice.Content, "畅销榜"))));
            row = NovelTests.Find<Grid>(module.View).Single(grid => AutomationProperties.GetName(grid) == "当前频道筛选");
            capsule = NovelTests.Find<Border>(row).Single(border => ReferenceEquals(border.Style, NovelTestUi.Resource(module.View, "MeowFilm.FilterGroup")));
            source = NovelTests.Find<ComboBox>(module.View).Single(box => AutomationProperties.GetAutomationId(box) == "novel.discovery.source");
            filterScroll = NovelTests.Find<ScrollViewer>(row).Single();
            var custom = new SolidColorBrush(Color.FromArgb(255, 222, 235, 230));
            module.View.Resources["MeowFilm.Brush.FloatingSurface"] = custom; module.View.RequestedTheme = ElementTheme.Dark; await Render(window);
            Check(ReferenceEquals(capsule.Background, custom), "Filter chrome ignored a changed theme resource.");
            var soft = new SolidColorBrush(Color.FromArgb(40, 25, 160, 100));
            module.View.Resources["MeowFilm.Brush.AccentSoft"] = soft; module.View.RequestedTheme = ElementTheme.Light; await Render(window);
            Check(ReferenceEquals(NovelTestUi.Named<Border>(NovelTests.Find<RadioButton>(row).Single(choice => choice.IsChecked == true), "Surface").Background, soft),
                "The selected chip did not follow the soft theme accent.");
            var selected = NovelTests.Find<RadioButton>(row).Single(choice => choice.IsChecked == true);
            Check(NovelTestUi.Named<Border>(selected, "Surface").Background == soft);
            NovelTestUi.CheckContrastResources(module.View);
            root.Width = 520; window.Resize(520, 760); await Render(window);
            Check(row.ActualWidth <= module.View.ActualWidth && filterScroll.ActualWidth > 0 &&
                Math.Abs(Center(Channels(module)[0]) - Center(source)) < 1, "Narrow layout split the heading or lost its filter viewport.");
            Check(!ExploreUrls(runtime).Any(url => string.IsNullOrWhiteSpace(url)), "A group heading was sent to explore.");
        }
        finally { window.Close(); }
    }

    private static async Task ChannelOverflow()
    {
        await using var store = new NovelTests.MemoryStore();
        await using var configurationStore = new NovelTests.MemoryStore();
        var kinds = Enumerable.Range(1, 16).Select(index =>
            new NovelKind($"榜{(char)('A' + index - 1)}", $"https://catalog.invalid/rank/{index}")).ToArray();
        var runtime = new DiscoveryRuntime(kinds);
        await using var module = new NovelCapability(Sources(runtime, configurationStore), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.HomeRoute, default);
        var root = new Grid(); root.Children.Add(module.View);
        var window = NovelTestUi.Window(root, 860, 600); window.Activate();
        try
        {
            await Render(window);
            var bar = NovelTests.Find<NovelDiscoveryChannelBar>(module.View).Single();
            var more = NovelTests.Find<Button>(bar).Single(button => AutomationProperties.GetAutomationId(button) == "novel.discovery.channels.expand");
            var channels = Channels(module);
            Check(channels.Length == kinds.Length && channels.Count(tab => DesktopVisual.IsVisible(tab)) is > 0 and < 16 &&
                DesktopVisual.IsVisible(more) && bar.ActualHeight == 44);
            Check(NovelTests.Find<ScrollViewer>(bar).All(scroll => scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled),
                "The channel bar still exposes horizontal scrolling.");
            var books = NovelTests.Find<NovelBookGrid>(module.View).Single();
            var position = books.TransformToVisual(module.View).TransformPoint(new());
            int requests = runtime.Calls.Count;
            Invoke(more); await Render(window);
            var expansion = NovelTests.Find<Border>(bar).Single(border =>
                AutomationProperties.GetAutomationId(border) == "novel.discovery.channels.expanded");
            var choices = NovelTests.Find<RadioButton>(expansion).ToArray();
            Check(DesktopVisual.IsVisible(expansion) && choices.Select(tab => tab.Content).SequenceEqual(kinds.Select(kind => (object)kind.Title)) &&
                choices.All(tab => DesktopVisual.IsVisible(tab)) && expansion.ActualWidth <= bar.ActualWidth &&
                books.TransformToVisual(module.View).TransformPoint(new()) == position && runtime.Calls.Count == requests,
                $"Expansion: visible={DesktopVisual.IsVisible(expansion)}; choices={choices.Length}/{kinds.Length}, " +
                $"visibleChoices={choices.Count(tab => DesktopVisual.IsVisible(tab))}; width={expansion.ActualWidth}/{bar.ActualWidth}; " +
                $"books={books.TransformToVisual(module.View).TransformPoint(new())}/{position}; requests={runtime.Calls.Count}/{requests}.");
            var hit = VisualTreeHelper.FindElementsInHostCoordinates(choices[0].TransformToVisual(null).TransformPoint(
                new(choices[0].ActualWidth / 2, choices[0].ActualHeight / 2)), module.View).FirstOrDefault();
            Check(DesktopVisual.Contains(expansion, hit), "The expanded row is painted behind book content.");
            Invoke(more); await Render(window);
            Check(!DesktopVisual.IsVisible(expansion) && runtime.Calls.Count == requests, "The arrow did not collapse without a request.");
            Invoke(more); await Render(window);
            var current = Channels(module).Single(tab => tab.IsChecked == true);
            current.Focus(FocusState.Programmatic); NovelTestUi.Invoke(current); await Render(window);
            Check(!DesktopVisual.IsVisible(expansion) && runtime.Calls.Count == requests,
                "Choosing the current first-row channel left its expansion open or requested data.");
            Invoke(more); await Render(window);
            choices = NovelTests.Find<RadioButton>(expansion).ToArray();
            Select(choices[^1]);
            await Until(() => module.ExploreKind == kinds[^1] && !module.LoadingDiscovery); await Render(window);
            bar = NovelTests.Find<NovelDiscoveryChannelBar>(module.View).Single();
            expansion = NovelTests.Find<Border>(bar).Single(border =>
                AutomationProperties.GetAutomationId(border) == "novel.discovery.channels.expanded");
            var restored = Channels(module).Single(tab => tab.IsChecked == true);
            Check(!DesktopVisual.IsVisible(expansion) && DesktopVisual.IsVisible(restored) && DesktopVisual.ContainsFocus(restored),
                $"Overflow restore: closed={!DesktopVisual.IsVisible(expansion)}, selected={restored.Content}, visible={DesktopVisual.IsVisible(restored)}, " +
                $"focused={DesktopVisual.ContainsFocus(restored)}, actualFocus={DesktopVisual.Focused(module.View)}, id={AutomationProperties.GetAutomationId(restored)}.");
            bar = NovelTests.Find<NovelDiscoveryChannelBar>(module.View).Single();
            more = NovelTests.Find<Button>(bar).Single(button => AutomationProperties.GetAutomationId(button) == "novel.discovery.channels.expand");
            Invoke(more); await Render(window);
            expansion = NovelTests.Find<Border>(bar).Single(border =>
                AutomationProperties.GetAutomationId(border) == "novel.discovery.channels.expanded");
            NovelTestUi.CheckContrastResources(expansion);
            window.Resize(2000, 600); await Render(window);
            Check(!DesktopVisual.IsVisible(expansion) && !DesktopVisual.IsVisible(more) && Channels(module).All(tab => DesktopVisual.IsVisible(tab)),
                "A wider window did not restore one row and remove the overflow arrow.");
            window.Resize(860, 600); await Render(window);
            Invoke(more); await Render(window);
            Check(DesktopVisual.IsVisible(expansion));
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default); await Render(window);
            var category = Channels(module).Single(tab => tab.IsChecked == true);
            Check(!DesktopVisual.IsVisible(expansion) && DesktopVisual.IsVisible(category),
                $"Leaving home kept an old floating row or hid the current category (route={module.Route}, oldExpansion={DesktopVisual.IsVisible(expansion)}, category={category.Content}, categoryVisible={DesktopVisual.IsVisible(category)}, overlay={NovelTests.Find<Canvas>(module.View).Count(canvas => canvas.Visibility == Visibility.Visible)}).");
            bar = NovelTests.Find<NovelDiscoveryChannelBar>(module.View).Single();
            more = NovelTests.Find<Button>(bar).Single(button => AutomationProperties.GetAutomationId(button) == "novel.discovery.channels.expand");
            Invoke(more); await Render(window);
            expansion = NovelTests.Find<Border>(bar).Single(border =>
                AutomationProperties.GetAutomationId(border) == "novel.discovery.channels.expanded");
            module.View.Visibility = Visibility.Collapsed; await Render(window);
            Check(!DesktopVisual.IsVisible(expansion) &&
                !NovelTests.Find<Canvas>(bar).Any(canvas => canvas.Visibility == Visibility.Visible),
                "A hidden page kept its expansion owner alive.");
        }
        finally { window.Close(); }
    }

    private static void Select(RadioButton choice) =>
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(choice).GetPattern(PatternInterface.SelectionItem)).Select();
    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static NovelSourceService Sources(DiscoveryRuntime runtime, NovelTests.MemoryStore store)
    {
        var source = runtime.Source;
        var definition = NovelJson.Value(new { bookSourceName = source.Name, bookSourceUrl = source.SourceUrl,
            exploreUrl = "https://catalog.invalid/explore" }).GetRawText();
        store.Bytes = JsonSerializer.SerializeToUtf8Bytes(new NovelSourceConfigurationSnapshot(1, 1,
            [new(source.InstallationId, source.Revision, source.Name, source.SourceUrl, source.Group, source.Order,
                source.Enabled, source.EnabledExplore, source.Protocol, definition)]), NovelJson.Options);
        return new(runtime, store);
    }
    private static async Task ImportNotice()
    {
        await using var store = new NovelTests.MemoryStore();
        await using var configurationStore = new NovelTests.MemoryStore();
        var runtime = new DiscoveryRuntime(Kinds);
        await using var module = new NovelCapability(Sources(runtime, configurationStore), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        var root = new Grid(); root.Children.Add(module.View);
        var notices = new NoticeStack(); root.Children.Add(notices);
        var window = NovelTestUi.Window(root);
        var emitted = new List<DesktopNoticeEventArgs>();
        NoticeOutlet.SetReceiver(DesktopTestUi.WindowRoot(window), e =>
        { if (e.Message.Length > 0) emitted.Add(e); notices.Show(e.Identity, e.Message); });
        window.Activate();
        try
        {
            await Render(window);
            var definition = NovelJson.Value(new { bookSourceName = "导入书源", bookSourceUrl = runtime.Source.SourceUrl,
                ruleSearch = new { }, exploreUrl = "https://catalog.invalid/explore" }).GetRawText();
            await module.Sources.ImportAsync(text: definition);
            module.Notify("已导入 1 个书源"); await Render(window);
            Check(emitted.Count == 1 && notices.Children.Count == 1);
            NovelTestUi.Invoke(NovelTests.Find<Button>(notices).Single());
            await DesktopTestUi.DrainAsync();
            Check(notices.Children.Count == 0);
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
            await module.LoadDiscoveryAsync(module.ExploreSource, Kinds[5], false); await Render(window);
            Check(emitted.Count == 1 && notices.Children.Count == 0 && configurationStore.Writes == 1);
            Check(!runtime.Calls.Any(call => call.Method == "source.runtime.library.import"));
            await module.Sources.ImportAsync(text: definition);
            module.Notify("已导入 1 个书源"); await Render(window);
            Check(emitted.Count == 2 && notices.Children.Count == 1 && configurationStore.Writes == 2 &&
                !ReferenceEquals(emitted[0].Identity, emitted[1].Identity));
            module.Notify("书源已保存"); module.Notify("书源配置已保存，运行器暂未生效"); await Render(window);
            Check(emitted.Count == 4 && NovelTests.Find<TextBlock>(notices).Select(text => text.Text)
                .Contains("书源已保存") && NovelTests.Find<TextBlock>(notices).Any(text => text.Text.Contains("暂未生效")),
                "A runtime application failure replaced or was replaced by the storage receipt notice.");
        }
        finally { window.Close(); }
    }
    private static async Task WebRequired()
    {
        await using var store = new NovelTests.MemoryStore();
        await using var configurationStore = new NovelTests.MemoryStore();
        var runtime = new DiscoveryRuntime(Kinds) { ExploreError = "WEB_INTERACTION_REQUIRED" };
        await using var module = new NovelCapability(Sources(runtime, configurationStore), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.LoadDiscoveryAsync(null, null, true);
        Check(module.DiscoveryState == NovelDiscoveryState.Failed && module.DiscoveryError == "书源需要网页验证" &&
            module.DiscoverBooks.Count == 0 && !module.MoreDiscovery);
        var host = new PlayerDialogHost(); var root = new Grid();
        root.Children.Add(module.View); root.Children.Add(host);
        var window = NovelTestUi.Window(root); host.Attach(window); window.Activate();
        try
        {
            await Until(() => host.IsOpen);
            Check(NovelTests.Find<TextBlock>(host).Any(text => text.Text == "书源需要网页验证") &&
                !NovelTests.Find<TextBlock>(module.View).Any(text => text.Text == "暂无书籍"));
            host.Close(); runtime.ExploreError = null;
            await module.RetryDiscoveryAsync(default);
            Check(module.DiscoveryState == NovelDiscoveryState.Ready && module.DiscoveryError.Length == 0);
        }
        finally { host.Close(); window.Close(); }
    }
    private sealed class DiscoveryRuntime(NovelKind[] kinds) : IRuntimeClient
    {
        internal readonly NovelSource Source = NovelTests.Source();
        internal readonly List<(string Method, JsonElement Payload)> Calls = [];
        internal string? ExploreError;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var payload = NovelJson.Value(parameters ?? new { }); Calls.Add((method, payload));
            object value = method switch
            {
                "source.runtime.library.apply" => new { revision = payload.GetProperty("revision").GetInt64(),
                    sources = NovelJson.Decode<NovelSourceDefinition[]>(payload.GetProperty("sources")).Select(item => item.SourceFromDefinition()).ToArray() },
                "source.runtime.web.pending" => new { requests = Array.Empty<object>() },
                "source.runtime.library.invoke" when payload.GetProperty("action").GetString() == "exploreKinds" => new { kinds },
                "source.runtime.library.invoke" when ExploreError is { } code => throw new BridgeException(code),
                "source.runtime.library.invoke" => new { books = new[] { new { name = "源书", author = "源作者",
                    bookUrl = payload.GetProperty("payload").GetProperty("url").GetString() + "/book" } } },
                _ => throw new InvalidOperationException(method)
            };
            return Task.FromResult(NovelJson.Value(new { ok = true, contract = "source.runtime.v2", value }));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long revision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken token = default) =>
            throw new InvalidOperationException("Discovery configuration never uploads executable source.");
    }
    private static string[] ExploreUrls(DiscoveryRuntime runtime) => runtime.Calls
        .Where(call => call.Method == "source.runtime.library.invoke" && call.Payload.GetProperty("action").GetString() == "explore")
        .Select(call => call.Payload.GetProperty("payload").GetProperty("url").GetString()!).ToArray();
    private static RadioButton[] Channels(NovelCapability module) => NovelTests.Find<RadioButton>(module.View)
        .Where(tab => tab.GroupName == "novel.discovery.channels").ToArray();
    private static async Task Render(Window window)
    {
        await DesktopTestUi.DrainAsync(); DesktopTestUi.WindowRoot(window).UpdateLayout();
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) { timeout.Token.ThrowIfCancellationRequested(); await DesktopTestUi.DrainAsync(); }
    }
    private static void Check(bool condition, string message = "Novel discovery assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
