using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private static JsonElement DiscoveryKinds(string prefix = "") => Json(new { kinds = new[]
    {
        new { title = prefix + "源站分组", url = "" },
        new { title = prefix + "源站月榜", url = "https://catalog.invalid/month" },
        new { title = prefix + "源站新作", url = "https://catalog.invalid/new" },
        new { title = prefix + "源站题材", url = "" },
        new { title = prefix + "原生分类", url = "https://catalog.invalid/category" }
    } });
    private static JsonElement DiscoveryBook(string name, string url) => Json(new
    {
        name, bookUrl = url, author = "源作者", kind = "源题材", wordCount = "源字数",
        intro = "书源返回的简介", latestChapterTitle = "源章节", originName = "来源字段"
    });
    private static async Task DiscoveryData()
    {
        await using var store = new MemoryStore();
        var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        runtime.Read = (source, action, input, _) => Task.FromResult(action == "exploreKinds" ? DiscoveryKinds(source.InstallationId) :
            Json(new { books = input.GetProperty("page").GetInt32() switch
            {
                1 => new[] { DiscoveryBook("首本", "book-1"), DiscoveryBook("次本", "book-2") },
                2 => new[] { DiscoveryBook("次本", "book-2"), DiscoveryBook("第三本", "book-3") },
                _ => Array.Empty<JsonElement>()
            } }));
        await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        await module.NavigateAsync(NovelCapability.HomeRoute, default);
        Check(module.ExploreSource?.InstallationId == "a" && module.ExploreKind?.Title == "a源站月榜" &&
            module.Kinds.Select(k => k.Title).SequenceEqual(new[] { "a源站分组", "a源站月榜", "a源站新作", "a源站题材", "a原生分类" }));
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "首本", "次本" }) &&
            module.DiscoverBooks.All(b => b.InstallationId == "a" && b.Author == "源作者" &&
                NovelJson.Text(b.Data, "kind") == "源题材" && NovelJson.Text(b.Data, "wordCount") == "源字数"));
        int calls = runtime.Calls.Count;
        await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
        Check(runtime.Calls.Count == calls && module.CaptureNavigationLocation() == NovelCapability.CategoriesRoute,
            "Home and categories must share the active source and loaded page.");
        await module.LoadDiscoveryAsync(module.ExploreSource, module.Kinds[0], false);
        Check(runtime.Calls.Count == calls && module.ExploreKind?.Title == "a源站月榜", "A source group heading was invoked as a category.");
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "首本", "次本", "第三本" }) && module.MoreDiscovery);
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        Check(module.DiscoverBooks.Count == 3 && !module.MoreDiscovery && module.DiscoveryState == NovelDiscoveryState.Ready);
        Check(runtime.Calls.Where(c => IsAction(c, "explore")).Select(c => c.Payload.GetProperty("payload").GetProperty("page").GetInt32())
            .SequenceEqual(new[] { 1, 2, 3 }), "More did not use the source's page sequence.");
        await module.LoadDiscoveryAsync(module.ExploreSource, module.Kinds[2], false);
        Check(module.ExploreKind?.Title == "a源站新作" && module.DiscoverBooks.Count == 2 &&
            runtime.Calls.Last(c => IsAction(c, "explore")).Payload.GetProperty("payload").GetProperty("page").GetInt32() == 1);
        await module.LoadDiscoveryAsync(runtime.Records[1], null, true);
        await module.NavigateAsync(NovelCapability.HomeRoute, default);
        Check(module.ExploreSource?.InstallationId == "b" && module.DiscoverBooks.All(b => b.InstallationId == "b") &&
            module.Kinds[1].Title == "b源站月榜" && module.Library.Entries.Count == 0);
    }
    private static async Task DiscoveryIsolation()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(runtime));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = async (source, action, _, _) =>
        {
            if (source.InstallationId == "a" && action == "exploreKinds") { accepted.TrySetResult(); return await late.Task; }
            return action == "exploreKinds" ? DiscoveryKinds() : Json(new { books = new[] { DiscoveryBook("当前源", "new") } });
        };
        var old = module.LoadDiscoveryAsync(runtime.Records[0], null, true);
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await module.LoadDiscoveryAsync(runtime.Records[1], null, true);
            Check(module.ExploreSource?.InstallationId == "b" && module.DiscoverBooks.Single().Name == "当前源");
            late.TrySetResult(DiscoveryKinds("退休"));
            try { await old; throw new InvalidOperationException("Retired discovery was accepted."); } catch (OperationCanceledException) { }
            Check(module.Kinds[1].Title == "源站月榜" && module.DiscoveryState == NovelDiscoveryState.Ready && !module.LoadingDiscovery);

            var pageAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Read = async (_, action, input, _) =>
            {
                if (action == "exploreKinds") return DiscoveryKinds();
                if (input.GetProperty("url").GetString()!.EndsWith("/new")) { pageAccepted.TrySetResult(); return await page.Task; }
                return Json(new { books = new[] { DiscoveryBook("新分类", "category") } });
            };
            var retired = module.LoadDiscoveryAsync(module.ExploreSource, module.Kinds[2], false);
            try
            {
                await pageAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(module.DiscoverBooks.Count == 0 && module.LoadingDiscovery && module.ExploreKind?.Title == "源站新作");
                await module.LoadDiscoveryAsync(module.ExploreSource, module.Kinds[4], false);
                page.TrySetResult(Json(new { books = new[] { DiscoveryBook("迟到分类", "late") } }));
                try { await retired; throw new InvalidOperationException("Retired category was accepted."); } catch (OperationCanceledException) { }
                Check(module.ExploreKind?.Title == "原生分类" && module.DiscoverBooks.Single().Name == "新分类" && !module.LoadingDiscovery);
            }
            finally { page.TrySetResult(Json(new { books = Array.Empty<object>() })); }
        }
        finally { late.TrySetResult(DiscoveryKinds()); }
    }
    private static async Task DiscoveryRetirement()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        var sources = new NovelSourceService(runtime);
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.LoadDiscoveryAsync(null, null, true);
        var before = module.DiscoverBooks.ToArray();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = async (_, _, _, _) => { accepted.TrySetResult(); await release.Task; return Json(new { books = new[] { DiscoveryBook("退休结果", "late") } }); };
        var pending = module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            module.CancelDiscovery();
            Check(module.LoadingDiscovery && module.DiscoveryState == NovelDiscoveryState.Cancelling && !pending.IsCompleted,
                "Cancel must remain pending until the actual source invocation has drained.");
            release.TrySetResult();
            try { await pending; throw new InvalidOperationException("Cancellation receipt was lost."); } catch (OperationCanceledException) { }
            Check(!module.LoadingDiscovery && module.DiscoveryState == NovelDiscoveryState.Cancelled && module.DiscoverBooks.SequenceEqual(before));
        }
        finally { release.TrySetResult(); }

        var stale = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = (_, _, _, _) => { staleAccepted.TrySetResult(); return stale.Task; };
        var retired = module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        try
        {
            await staleAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.Records = [Source("a") with { Revision = 2 }, Source("b")]; await sources.RefreshAsync();
            Check(module.ExploreSource is { InstallationId: "a", Revision: 2 } && module.DiscoverBooks.Count == 0 &&
                module.Kinds.Count == 0 && module.ExploreKind is null);
            stale.TrySetResult(Json(new { books = new[] { DiscoveryBook("旧版本", "old-revision") } }));
            try { await retired; throw new InvalidOperationException("Revised source accepted its old page."); } catch (OperationCanceledException) { }
            runtime.Read = (_, action, _, _) => Task.FromResult(action == "exploreKinds" ? DiscoveryKinds() :
                Json(new { books = new[] { DiscoveryBook("新版本", "revision-2") } }));
            await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            Check(module.DiscoverBooks.Single().Revision == 2 && module.DiscoverBooks.Single().Name == "新版本");
            runtime.Records = [Source("a") with { Revision = 3, EnabledExplore = false }, Source("b")]; await sources.RefreshAsync();
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
            Check(module.ExploreSource?.InstallationId == "a" && module.DiscoverBooks.Count == 0 &&
                module.DiscoveryState == NovelDiscoveryState.Unavailable, "An unavailable selection silently fell back to another enabled source.");
            runtime.Records = [Source("b")]; await sources.RefreshAsync();
            await module.LoadDiscoveryAsync(null, null, true);
            Check(module.ExploreSource?.InstallationId == "a" && module.DiscoveryState == NovelDiscoveryState.Unavailable);
            await module.LoadDiscoveryAsync(runtime.Records[0], null, true);
            Check(module.ExploreSource?.InstallationId == "b" && module.DiscoverBooks.Single().InstallationId == "b");
            await module.PrepareShutdownAsync();
            var state = module.DiscoveryState; int calls = runtime.Calls.Count;
            try { await module.LoadDiscoveryAsync(module.ExploreSource, null, true); throw new InvalidOperationException("A paused discovery owner admitted new work."); }
            catch (InvalidOperationException error) when (error.Message == "SHUTTING_DOWN") { }
            Check(!module.LoadingDiscovery && module.DiscoveryState == state && runtime.Calls.Count == calls,
                "Rejected discovery left a phantom loading state.");
            await module.CancelShutdownPreparationAsync();
            await module.LoadDiscoveryAsync(module.ExploreSource, null, true);
            Check(module.DiscoveryState == NovelDiscoveryState.Ready && !module.LoadingDiscovery);
        }
        finally { stale.TrySetResult(Json(new { books = Array.Empty<object>() })); }
    }
    private static async Task DiscoveryFailures()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(runtime));
        bool failKinds = true, failPage = false, empty = false;
        runtime.Read = (_, action, input, _) =>
        {
            if (action == "exploreKinds") return failKinds ? throw new BridgeException("NETWORK_ERROR") : Task.FromResult(DiscoveryKinds());
            if (failPage) throw new BridgeException("NETWORK_ERROR");
            int page = input.GetProperty("page").GetInt32();
            return Task.FromResult(Json(new { books = empty ? Array.Empty<JsonElement>() : new[] { DiscoveryBook("第" + page + "页", "page-" + page) } }));
        };
        await module.LoadDiscoveryAsync(runtime.Records[1], null, true);
        Check(module.ExploreSource?.InstallationId == "b" && module.DiscoveryState == NovelDiscoveryState.Failed &&
            module.Notice.Contains("发现加载失败") && !module.LoadingDiscovery && module.DiscoverBooks.Count == 0);
        failKinds = false; await module.RetryDiscoveryAsync(default);
        Check(module.ExploreSource?.InstallationId == "b" && module.DiscoverBooks.Single().Name == "第1页");
        failPage = true; await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        Check(module.DiscoveryState == NovelDiscoveryState.Failed && module.DiscoverBooks.Single().Name == "第1页" && module.MoreDiscovery);
        failPage = false; await module.RetryDiscoveryAsync(default);
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "第1页", "第2页" }) &&
            runtime.Calls.Where(c => IsAction(c, "explore")).Select(c => c.Payload.GetProperty("payload").GetProperty("page").GetInt32())
                .SequenceEqual(new[] { 1, 2, 2 }), "Retry skipped the failed page.");
        failPage = true; await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "第1页", "第2页" }) &&
            module.MoreDiscovery && module.ExploreSource?.InstallationId == "b" && module.DiscoveryState == NovelDiscoveryState.Failed,
            "A failed same-target refresh discarded the previously successful pages.");
        failPage = false; empty = true; await module.RetryDiscoveryAsync(default);
        Check(module.DiscoveryState == NovelDiscoveryState.Empty && !module.MoreDiscovery && module.ExploreSource?.InstallationId == "b");
    }
    private static async Task DiscoveryRefreshAtomic()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        string response = "original";
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = async (_, action, input, _) =>
        {
            if (action == "exploreKinds")
            {
                if (response == "fail-kinds") throw new BridgeException("NETWORK_ERROR");
                return DiscoveryKinds(response == "original" ? "" : "刷新");
            }
            if (response == "fail-page") throw new BridgeException("NETWORK_ERROR");
            if (response == "held") { accepted.TrySetResult(); await release.Task; }
            int page = input.GetProperty("page").GetInt32();
            return Json(new { books = response switch
            {
                "original" => page == 1
                    ? new[] { DiscoveryBook("原首本", "original-1"), DiscoveryBook("原次本", "original-2") }
                    : new[] { DiscoveryBook("原第三本", "original-3") },
                "fresh" => page == 1
                    ? new[] { DiscoveryBook("刷新首本", "fresh-1"), DiscoveryBook("刷新次本", "fresh-2") }
                    : new[] { DiscoveryBook("刷新更多", "fresh-more") },
                _ => new[] { DiscoveryBook("追加" + page, "more-" + page) }
            } });
        };
        await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.LoadDiscoveryAsync(runtime.Records[0], null, true);
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        var original = module.DiscoverBooks.ToArray(); var kinds = module.Kinds.ToArray(); var kind = module.ExploreKind;
        response = "fail-kinds"; await module.LoadDiscoveryAsync(module.ExploreSource, kind, true);
        Check(module.DiscoverBooks.SequenceEqual(original) && module.Kinds.SequenceEqual(kinds) && module.ExploreKind == kind &&
            module.MoreDiscovery && module.DiscoveryState == NovelDiscoveryState.Failed);
        response = "fail-page"; await module.LoadDiscoveryAsync(module.ExploreSource, kind, true);
        Check(module.DiscoverBooks.SequenceEqual(original) && module.Kinds.SequenceEqual(kinds) && module.ExploreKind == kind &&
            module.MoreDiscovery && module.DiscoveryState == NovelDiscoveryState.Failed,
            "Refresh committed new entry metadata before its first page succeeded.");
        response = "later"; await module.LoadDiscoveryAsync(module.ExploreSource, kind, false);
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "原首本", "原次本", "原第三本", "追加3" }) &&
            runtime.Calls.Last(c => IsAction(c, "explore")).Payload.GetProperty("payload").GetProperty("page").GetInt32() == 3,
            "A failed refresh reset the already committed paging position.");

        var beforeCancellation = module.DiscoverBooks.ToArray();
        response = "held"; var refreshing = module.LoadDiscoveryAsync(module.ExploreSource, kind, true);
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(module.DiscoverBooks.SequenceEqual(beforeCancellation) && module.Kinds.SequenceEqual(kinds) &&
                module.ExploreKind == kind && module.LoadingDiscovery && module.MoreDiscovery,
                "An in-flight same-target refresh removed its last successful content.");
            module.CancelDiscovery();
            Check(module.LoadingDiscovery && !refreshing.IsCompleted && module.DiscoverBooks.SequenceEqual(beforeCancellation));
            release.TrySetResult();
            try { await refreshing; throw new InvalidOperationException("The refresh cancellation receipt was lost."); } catch (OperationCanceledException) { }
            Check(module.DiscoverBooks.SequenceEqual(beforeCancellation) && module.Kinds.SequenceEqual(kinds) &&
                module.ExploreKind == kind && module.MoreDiscovery && module.DiscoveryState == NovelDiscoveryState.Cancelled);
        }
        finally { release.TrySetResult(); }
        response = "later"; await module.LoadDiscoveryAsync(module.ExploreSource, kind, false);
        Check(module.DiscoverBooks.Last().Name == "追加4" &&
            runtime.Calls.Last(c => IsAction(c, "explore")).Payload.GetProperty("payload").GetProperty("page").GetInt32() == 4,
            "Cancelling refresh discarded its committed page counter.");
        response = "fresh"; await module.LoadDiscoveryAsync(module.ExploreSource, kind, true);
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "刷新首本", "刷新次本" }) &&
            module.ExploreKind?.Title == "刷新源站月榜" && module.Kinds[0].Title == "刷新源站分组");
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        Check(module.DiscoverBooks.Select(b => b.Name).SequenceEqual(new[] { "刷新首本", "刷新次本", "刷新更多" }) &&
            runtime.Calls.Last(c => IsAction(c, "explore")).Payload.GetProperty("payload").GetProperty("page").GetInt32() == 2);
        response = "fail-page"; await module.LoadDiscoveryAsync(module.ExploreSource, module.Kinds[2], false);
        Check(module.DiscoverBooks.Count == 0 && module.ExploreKind?.Url.EndsWith("/new") == true && !module.MoreDiscovery,
            "A new category kept unrelated books after failure.");
        response = "fail-kinds"; await module.LoadDiscoveryAsync(runtime.Records[1], null, true);
        Check(module.DiscoverBooks.Count == 0 && module.Kinds.Count == 0 && module.ExploreSource?.InstallationId == "b",
            "A new source kept the previous source's successful content after failure.");
    }
    private static async Task DiscoveryRetryDialog()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime();
        bool fail = true, hold = false;
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = async (_, action, _, _) =>
        {
            if (action == "exploreKinds")
            {
                if (hold) { accepted.TrySetResult(); await release.Task; }
                if (fail) throw new BridgeException("NETWORK_ERROR");
                return DiscoveryKinds();
            }
            return Json(new { books = new[] { DiscoveryBook("重试结果", "retry") } });
        };
        await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        var root = new Grid(); root.Children.Add(module.View);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = GeneralAppearanceTests.Window(root); dialogs.Attach(window); window.Show();
        try
        {
            await module.NavigateAsync(NovelCapability.HomeRoute, default);
            await Until(() => dialogs.IsOpen);
            var surface = Find<Border>(dialogs).Single(b => AutomationProperties.GetName(b) == "发现加载失败");
            Check(module.Notice.Contains("发现加载失败") &&
                !Find<TextBlock>(module.View).Any(t => t.Text.Contains("发现加载失败")),
                "Discovery inserted an error explanation into its permanent page layout.");
            Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => Find<TextBlock>(dialogs).Any(t => t.Text == "发现加载失败，请重试") &&
                Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")).IsEnabled);
            Check(dialogs.IsOpen && ReferenceEquals(surface.Parent, dialogs), "A failed retry replaced or closed its error dialog.");
            fail = false;
            Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !dialogs.IsOpen && !module.LoadingDiscovery);
            Check(module.DiscoverBooks.Single().Name == "重试结果" && module.ExploreSource?.InstallationId == "a");

            fail = true; await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            await Until(() => dialogs.IsOpen);
            hold = true;
            Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Find<Button>(dialogs).Single(b => Equals(b.Content, "取消")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!dialogs.IsOpen && module.LoadingDiscovery, "Closing a busy retry acknowledged an undrained source invocation.");
            release.TrySetResult();
            await Until(() => !module.LoadingDiscovery);
            Check(module.DiscoveryState == NovelDiscoveryState.Cancelled && module.DiscoverBooks.Single().Name == "重试结果" &&
                !dialogs.IsOpen && module.ExploreSource?.InstallationId == "a");
        }
        finally { release.TrySetResult(); dialogs.Close(); window.Close(); }
    }
    private static async Task DiscoveryShelf()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        runtime.Read = (source, action, _, _) => Task.FromResult(action == "exploreKinds" ? DiscoveryKinds() :
            Json(new { books = new[] { Book(source.InstallationId, author: "林舟").Json() } }));
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
        await module.InitializeAsync(default);
        var book = Book(author: "林舟");
        var saved = new NovelEntry("canonical", await module.Rules.IdentityAsync(book),
            book, OnShelf: true, ReadAt: DateTimeOffset.UtcNow, Anchor: new("chapter", "已读章", Ratio: .4));
        await module.Library.SaveEntryAsync(saved);
        await module.LoadDiscoveryAsync(runtime.Records[1], null, true);
        Check(saved.IdentityKey.Length > 0 && saved.IdentityKey == await module.Rules.IdentityAsync(module.DiscoverBooks.Single()),
            "The cross-source shelf fixture must represent the same known-author book.");
        var window = GeneralAppearanceTests.Window(module.View); window.Show();
        try
        {
            Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "加入书架")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => module.Notice == "已加入书架");
            Check(module.Library.Entries.Single() == saved && module.Session is null,
                "Adding an alternate source removed or rewrote the canonical shelf/history entry.");
            await module.LoadDiscoveryAsync(runtime.Records[0], null, true);
            await Until(() => Find<Button>(module.View).Any(b =>
                b.IsLoaded && b.IsVisible && AutomationProperties.GetName(b) == "移出书架"));
            Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "移出书架")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => module.Notice == "已移出书架");
            Check(module.Library.Entries.Single() is { BookId: "canonical", OnShelf: false } retained &&
                retained.ReadAt == saved.ReadAt && retained.Anchor == saved.Anchor && module.Session is null);
        }
        finally { window.Close(); }
    }
    private static async Task DiscoveryContinuation()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        foreach (bool sameSource in new[] { true, false })
        {
            await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
            await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
            await module.InitializeAsync(default);
            var book = Book(author: "林舟");
            string key = await module.Rules.IdentityAsync(book);
            var older = new NovelEntry("older", key, book, ReadAt: DateTimeOffset.UtcNow.AddDays(-2),
                Anchor: new("chapter-1", "第一章", 0, 0, 1, .2));
            var latest = new NovelEntry("latest", key, sameSource ? book : Book("b", author: "林舟"), ReadAt: DateTimeOffset.UtcNow.AddHours(-1),
                Anchor: new("chapter-2", "第二章", 1, 0, 2, .6));
            Check(key.Length > 0 && key == await module.Rules.IdentityAsync(latest.Book),
                "The continuation fixture must retain distinct book ids with a shared matching identity.");
            await module.Library.SaveEntryAsync(older); await module.Library.SaveEntryAsync(latest);
            int writes = store.Writes;
            string requested = ""; module.NavigationRequested += (_, e) => requested = e.Route;
            await module.OpenBookAsync(older.Book);
            Check(requested == NovelCapability.BookRoute && module.Session?.Entry.BookId == older.BookId);
            await module.NavigateAsync(NovelCapability.HomeRoute, default);
            var window = GeneralAppearanceTests.Window(module.View); window.Show();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                Check(store.Writes == writes && module.Library.Entries.Single(e => e.BookId == latest.BookId).Anchor == latest.Anchor,
                    "Opening a book or displaying continuation wrote reading history.");
                Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "接着读")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => requested == NovelCapability.ReaderRoute);
                Check(module.Session is { HasContent: true } session && session.Entry.BookId == latest.BookId &&
                    session.Entry.Anchor == latest.Anchor && session.Chapter?.ChapterUrl == latest.Anchor!.ChapterUrl,
                    "Continuation reselected a similar identity instead of restoring the latest saved book id and anchor.");
                var saved = module.Library.Entries.Single(e => e.BookId == latest.BookId);
                Check(module.Library.Entries.Count == 2 && module.Library.Entries.Single(e => e.BookId == older.BookId) == older &&
                    saved.ReadAt == latest.ReadAt && saved.Anchor == latest.Anchor,
                    "Restoring content rewrote saved history before actual reading was saved.");
                if (!sameSource)
                {
                    runtime.Read = (source, action, _, _) => Task.FromResult(action == "exploreKinds" ? DiscoveryKinds() :
                        Json(new { books = new[] { Book(source.InstallationId, author: "林舟").Json() } }));
                    try { await module.LoadDiscoveryAsync(runtime.Records[1], null, true); }
                    finally { runtime.Read = null; }
                    Check(key == await module.Rules.IdentityAsync(module.DiscoverBooks.Single()),
                        "The discovered book must match the retained canonical identity.");
                    requested = "";
                    Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "开始阅读")
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => requested == NovelCapability.ReaderRoute);
                    Check(module.Session is { HasContent: true } canonical && canonical.Entry.BookId == older.BookId,
                        "Starting a discovered book rejected its existing canonical session from another source.");
                }
            }
            finally { window.Close(); }
        }
    }
    private sealed class DiscoveryImportRuntime : IRuntimeClient
    {
        internal readonly Runtime Inner = new();
        internal JsonElement PreviewPayload, ImportPayload;
        internal int Previews, Imports;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "source.runtime.library.previewImport")
            {
                Previews++; PreviewPayload = NovelJson.Value(parameters!);
                return Task.FromResult(Json(new { ok = true, contract = "source.runtime.v2", value = new
                {
                    items = new[] { "b", "c" }.Select(id => new
                    {
                        name = Source(id).Name, protocol = "legado", group = "", exists = false,
                        definition = Json(new { bookSourceName = Source(id).Name, bookSourceUrl = Source(id).SourceUrl, enabled = false }).GetRawText()
                    }).ToArray(), failures = Array.Empty<object>()
                } }));
            }
            if (method == "source.runtime.library.import")
            {
                Imports++; ImportPayload = NovelJson.Value(parameters!);
                Inner.Records = [Source("a"), Source("b") with { Enabled = false }];
            }
            return Inner.CallAsync(method, parameters, cancellationToken);
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Discovery import never uploads scripts.");
    }
    private static async Task DiscoveryImport()
    {
        await using var store = new MemoryStore(); var runtime = new DiscoveryImportRuntime();
        await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.HomeRoute, default);
        var root = new Grid(); root.Children.Add(module.SidebarView);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = GeneralAppearanceTests.Window(root); dialogs.Attach(window); window.Show();
        try
        {
            var import = Find<Button>(module.SidebarView).Single(b => AutomationProperties.GetName(b) == "导入书源");
            import.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => dialogs.IsOpen && Find<TextBox>(dialogs).Any(t =>
                AutomationProperties.GetName(t) == "书源地址或内容" && t.IsLoaded && t.IsVisible));
            var input = Find<TextBox>(dialogs).Single();
            input.Text = "https://sources.invalid/list.json";
            Check(runtime.Previews == 0 && runtime.Imports == 0);
            Find<Button>(dialogs).Single(b => Equals(b.Content, "预览")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => Find<ManagementImportSelection>(dialogs).Any() &&
                Find<CheckBox>(dialogs).Any(c => AutomationProperties.GetName(c) == "选择 书源c" && c.IsLoaded && c.IsVisible) &&
                Find<Button>(dialogs).Any(b => Equals(b.Content, "确定导入") && b.IsLoaded && b.IsVisible));
            Check(runtime.Previews == 1 && runtime.Imports == 0 && module.Sources.Sources.Count == 1 &&
                runtime.PreviewPayload.GetProperty("url").GetString() == input.Text && !runtime.PreviewPayload.TryGetProperty("text", out _));
            var second = Find<CheckBox>(dialogs).Single(c => AutomationProperties.GetName(c) == "选择 书源c");
            second.IsChecked = false; second.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Find<Button>(dialogs).Single(b => Equals(b.Content, "确定导入")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !dialogs.IsOpen && runtime.Imports == 1);
            using var selected = JsonDocument.Parse(runtime.ImportPayload.GetProperty("text").GetString()!);
            Check(selected.RootElement.GetArrayLength() == 1 &&
                selected.RootElement[0].GetProperty("bookSourceName").GetString() == "书源b" &&
                !selected.RootElement[0].GetProperty("enabled").GetBoolean() &&
                !runtime.ImportPayload.TryGetProperty("url", out _) &&
                module.Sources.Sources.Single(s => s.InstallationId == "b").Enabled == false &&
                !Find<Button>(module.SidebarView).Any(b => AutomationProperties.GetName(b) == "书源b"));

            Find<Button>(module.SidebarView).Single(b => AutomationProperties.GetName(b) == "导入书源")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => dialogs.IsOpen && Find<Button>(dialogs).Any(b =>
                Equals(b.Content, "取消") && b.IsLoaded && b.IsVisible));
            Find<Button>(dialogs).Single(b => Equals(b.Content, "取消")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !dialogs.IsOpen);
            Check(runtime.Previews == 1 && runtime.Imports == 1, "Cancelling the source input started a preview or permanent import.");
        }
        finally { dialogs.Close(); window.Close(); }
    }
    private static async Task DiscoveryPresentation()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b"), Source("c") with { Enabled = false }] };
        runtime.Read = (_, action, _, _) => Task.FromResult(action == "exploreKinds" ? DiscoveryKinds() :
            Json(new { books = Enumerable.Range(1, 6).Select(i => DiscoveryBook("真实书名" + i, "book-" + i)).ToArray() }));
        await using var module = new NovelCapability(new NovelSourceService(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.HomeRoute, default);
        // Component dimensions must not depend on the native window's frame or monitor tracking limits.
        var root = new Grid { Width = 1440, Height = 900 };
        root.ColumnDefinitions.Add(new() { Width = new(216) }); root.ColumnDefinitions.Add(new());
        var sidebar = module.SidebarView; root.Children.Add(sidebar); Grid.SetColumn(module.View, 1); root.Children.Add(module.View);
        var host = GeneralAppearanceTests.Window(root); host.Width = 1440; host.Height = 900;
        host.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render); root.UpdateLayout();
            Check(Math.Abs(root.ActualWidth - 1440) < 1, $"Unexpected component width: {root.ActualWidth}.");
            var grid = Find<Panel>(module.View).Single(p => AutomationProperties.GetName(p) == "发现书籍");
            var tiles = grid.Children.Cast<FrameworkElement>().ToArray();
            var positions = tiles.Select(t => t.TranslatePoint(new Point(), grid)).ToArray();
            Check(tiles.Length == 6 && positions.All(p => Math.Abs(p.Y - positions[0].Y) < 1) &&
                positions.Zip(positions.Skip(1)).All(p => p.Second.X > p.First.X),
                $"The home covers lost their six column row: view={module.View.ActualWidth}, grid={grid.ActualWidth}, positions={string.Join("; ", positions)}.");
            Check(Find<TextBlock>(grid).Count(t => t.Text == "暂无封面") == 6 &&
                Find<Image>(grid).All(image => image.Source is null),
                "Missing source covers need a truthful, proportioned empty state rather than invented artwork.");
            Check(Find<TextBlock>(module.View).Any(t => t.Text == "暂无阅读历史") &&
                !Find<Button>(module.View).Any(b => AutomationProperties.GetName(b) == "接着读"),
                "The home invented a continuing book without local reading history.");
            Check(Find<TextBlock>(module.View).Any(t => t.Text.Contains("源作者")) &&
                Find<TextBlock>(module.View).Any(t => t.Text.Contains("源题材")));
            Check(ReferenceEquals(sidebar, module.SidebarView) && Find<Button>(sidebar).Any(b => AutomationProperties.GetName(b) == "书源a") &&
                Find<Button>(sidebar).Any(b => AutomationProperties.GetName(b) == "书源b") &&
                !Find<Button>(sidebar).Any(b => AutomationProperties.GetName(b) == "书源c"));
            int settings = 0; module.SettingsRequested += (_, _) => settings++;
            Find<Button>(sidebar).Single(b => AutomationProperties.GetName(b) == "书源管理").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(settings == 1, "The source management icon did not request the existing settings owner.");
            Find<Button>(sidebar).Single(b => AutomationProperties.GetName(b) == "书源b").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !module.LoadingDiscovery && module.ExploreSource?.InstallationId == "b");
            Check(module.DiscoverBooks.All(b => b.InstallationId == "b") && ReferenceEquals(sidebar, module.SidebarView));
            var sourcePicker = Find<ComboBox>(module.View).Single(c => AutomationProperties.GetAutomationId(c) == "novel.discovery.source");
            Check(sourcePicker.Focus(), "The source selector could not receive keyboard focus.");
            sourcePicker.SelectedItem = module.Sources.Sources.Single(s => s.InstallationId == "a");
            await Until(() => !module.LoadingDiscovery && module.ExploreSource?.InstallationId == "a");
            await Dispatcher.Yield(DispatcherPriority.Input);
            Check(Find<ComboBox>(module.View).Single(c => AutomationProperties.GetAutomationId(c) == "novel.discovery.source").IsKeyboardFocusWithin,
                "Changing source lost the source selector's keyboard focus.");
            var kindPicker = Find<RadioButton>(module.View).Single(r => Equals(r.Content, "源站新作"));
            Check(kindPicker.Focus(), "A source category could not receive keyboard focus."); kindPicker.IsChecked = true;
            await Until(() => !module.LoadingDiscovery && module.ExploreKind?.Title == "源站新作");
            await Dispatcher.Yield(DispatcherPriority.Input);
            Check(Find<RadioButton>(module.View).Single(r => Equals(r.Content, "源站新作")).IsKeyboardFocusWithin,
                "Changing category lost the category control's keyboard focus.");
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
            await Dispatcher.Yield(DispatcherPriority.Render); root.UpdateLayout();
            var catalog = Find<Panel>(module.View).Single(p => AutomationProperties.GetName(p) == "分类结果");
            var cards = catalog.Children.Cast<FrameworkElement>().ToArray();
            var points = cards.Select(c => c.TranslatePoint(new Point(), catalog)).ToArray();
            Check(cards.Length == 6 && Math.Abs(points[0].Y - points[2].Y) < 1 && points[3].Y > points[0].Y &&
                Math.Abs(points[0].X - points[3].X) < 1, "The catalog did not keep three image/text columns in source order.");
            Check(Find<RadioButton>(module.View).Any(r => Equals(r.Content, "源站分组")) &&
                Find<RadioButton>(module.View).Any(r => Equals(r.Content, "原生分类")) &&
                Find<TextBlock>(module.View).Any(t => t.Text == "源题材 · 源字数"));
            root.Width = host.Width = 780; await Dispatcher.Yield(DispatcherPriority.Render); root.UpdateLayout();
            Check(Math.Abs(root.ActualWidth - 780) < 1, $"Unexpected narrow component width: {root.ActualWidth}.");
            foreach (var card in catalog.Children.Cast<FrameworkElement>())
            {
                var bounds = card.TransformToAncestor(module.View).TransformBounds(new Rect(card.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= module.View.ActualWidth + 1, "The narrow catalog overflowed its content column.");
            }
            await module.NavigateAsync(NovelCapability.HomeRoute, default);
            await Dispatcher.Yield(DispatcherPriority.Render); root.UpdateLayout();
            var selected = Find<Border>(module.View).Single(b => AutomationProperties.GetName(b) == "选读");
            var continuing = Find<Border>(module.View).Single(b => AutomationProperties.GetName(b) == "继续阅读");
            Check(continuing.TranslatePoint(new Point(), module.View).Y >= selected.TranslatePoint(new Point(), module.View).Y + selected.ActualHeight,
                "The narrow home squeezed continuation beside its selected book.");
            DesktopTheme.SetIsHighContrast(root, true); root.UpdateLayout();
            Check(selected.Background == SystemColors.WindowBrush && continuing.Background == SystemColors.WindowBrush &&
                Find<TextBlock>(selected).Single(t => t.Text == "真实书名1").Foreground == SystemColors.WindowTextBrush,
                "The discovery cards ignored the inherited high contrast canvas or text colors.");
            Check(Find<TextBlock>(module.View).Where(t => t.Text == "暂无封面")
                .All(t => t.Foreground == SystemColors.WindowTextBrush),
                "Missing-cover status must retain system text contrast.");
            var search = new TextBox { Width = 160, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(search, 1); root.Children.Add(search); Check(search.Focus());
            await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            await Dispatcher.Yield(DispatcherPriority.Input);
            Check(search.IsKeyboardFocusWithin, "Discovery presentation stole focus from the surrounding search control.");
        }
        finally { host.Close(); }
    }
}
