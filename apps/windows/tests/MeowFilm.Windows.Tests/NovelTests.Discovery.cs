using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
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
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
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
        var sources = NovelTestSources.Create(runtime); await sources.RefreshAsync();
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

            await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
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
    private static async Task DiscoveryResponseIsolation()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime();
        var kinds = JsonSerializer.Deserialize<JsonElement>("""
            {"kinds":[null,{"title":"源站分组","url":""},{"title":{},"url":"bad"},
              {"title":"健康分类","url":"https://catalog.invalid/live","extra":false},17,
              {"title":"无地址"},{"title":"第二分类","url":"https://catalog.invalid/next"},{"title":"坏地址","url":[]}]}
            """);
        var books = JsonSerializer.Deserialize<JsonElement>("""
            {"books":[null,{"name":"缺身份"},{"name":"首本","bookUrl":"first","kind":"原始题材"},
              {"bookUrl":{},"name":"坏身份"},false,{"bookUrl":"second","name":"次本","extra":{"keep":true}},
              {"bookUrl":"","name":"空身份"},{"bookUrl":"missing-name"}]}
            """);
        runtime.Read = (_, action, _, _) => Task.FromResult(action == "exploreKinds" ? kinds : books);
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
        Check(module.DiscoveryState == NovelDiscoveryState.Ready && module.MoreDiscovery &&
            module.Kinds.Select(kind => kind.Title).SequenceEqual(["源站分组", "健康分类", "第二分类"]) &&
            module.ExploreKind?.Title == "健康分类");
        Check(module.DiscoverBooks.Select(book => book.BookUrl).SequenceEqual(["first", "second"]) &&
            NovelJson.Text(module.DiscoverBooks[0].Data, "kind") == "原始题材" &&
            module.DiscoverBooks[1].Data.GetProperty("extra").GetProperty("keep").GetBoolean());
        int calls = runtime.Calls.Count;
        await module.LoadDiscoveryAsync(module.ExploreSource, module.Kinds[0], false);
        Check(runtime.Calls.Count == calls, "A valid empty-URL group became an explore request.");
        var originalBooks = module.DiscoverBooks.ToArray(); var originalKinds = module.Kinds; var originalKind = module.ExploreKind;
        var healthyKinds = kinds; var healthyBooks = books;
        foreach (string invalid in new[]
        {
            "null", "[]", "{}", """{"kinds":null}""", """{"kinds":{}}""",
            """{"kinds":[null,false,{"title":"缺地址"},{"title":[],"url":"bad"}]}"""
        })
        {
            kinds = JsonSerializer.Deserialize<JsonElement>(invalid);
            await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            Unchanged();
        }
        kinds = DiscoveryKinds("候选");
        foreach (string invalid in new[]
        {
            "null", "[]", "{}", """{"books":false}""", """{"books":{}}""",
            """{"books":[{"name":"没有 bookUrl"}]}""",
            """{"books":[null,17,{"bookUrl":"","name":"空身份"},{"bookUrl":"  ","name":"空白身份"},{"bookUrl":[],"name":"坏身份"},{"bookUrl":"valid","name":{}}]}"""
        })
        {
            books = JsonSerializer.Deserialize<JsonElement>(invalid);
            await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            Unchanged();
        }
        kinds = healthyKinds; books = healthyBooks; runtime.Calls.Clear();
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
        originalBooks = module.DiscoverBooks.ToArray(); originalKinds = module.Kinds; originalKind = module.ExploreKind;
        books = JsonSerializer.Deserialize<JsonElement>("""{"books":[{"name":"坏第二页"}]}""");
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        Unchanged();
        books = Json(new { books = new[] { DiscoveryBook("第三本", "third") } });
        await module.RetryDiscoveryAsync(default);
        Check(module.DiscoveryState == NovelDiscoveryState.Ready && module.MoreDiscovery &&
            module.DiscoverBooks.Select(book => book.BookUrl).SequenceEqual(["first", "second", "third"]) &&
            runtime.Calls.Where(call => IsAction(call, "explore")).Select(call => call.Payload.GetProperty("payload").GetProperty("page").GetInt32())
                .SequenceEqual([1, 2, 2]), "An unusable page advanced the committed page counter.");
        books = Json(new { books = Array.Empty<object>() });
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false);
        Check(module.DiscoveryState == NovelDiscoveryState.Ready && !module.MoreDiscovery && module.DiscoverBooks.Count == 3);
        await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
        Check(module.DiscoveryState == NovelDiscoveryState.Empty && !module.MoreDiscovery && module.DiscoverBooks.Count == 0);
        kinds = JsonSerializer.Deserialize<JsonElement>("""{"kinds":[{"title":"只有分组","url":""}]}""");
        calls = runtime.Calls.Count(call => IsAction(call, "explore"));
        await module.LoadDiscoveryAsync(module.ExploreSource, null, true);
        Check(module.DiscoveryState == NovelDiscoveryState.Empty && module.Kinds.Single().Title == "只有分组" &&
            module.ExploreKind is null && runtime.Calls.Count(call => IsAction(call, "explore")) == calls);
        kinds = healthyKinds; books = JsonSerializer.Deserialize<JsonElement>("""{"books":[{"name":"没有 bookUrl"}]}""");
        await module.LoadDiscoveryAsync(module.ExploreSource, null, true);
        Check(module.DiscoveryState == NovelDiscoveryState.Failed && module.DiscoverBooks.Count == 0 && !module.MoreDiscovery &&
            module.Kinds.Single().Title == "只有分组" && module.ExploreKind is null,
            "An unusable first page became Ready or published its candidate categories.");
        kinds = Json(new { kinds = Array.Empty<object>() });
        await module.LoadDiscoveryAsync(module.ExploreSource, null, true);
        Check(module.DiscoveryState == NovelDiscoveryState.Empty && module.Kinds.Count == 0 &&
            module.ExploreKind is null && !module.MoreDiscovery);
        void Unchanged() => Check(module.DiscoveryState == NovelDiscoveryState.Failed && !module.LoadingDiscovery &&
            module.DiscoverBooks.SequenceEqual(originalBooks) && module.Kinds.SequenceEqual(originalKinds) &&
            module.ExploreKind == originalKind && module.MoreDiscovery,
            "An unusable response committed a partial refresh, erased prior books, or ended pagination.");
    }
    private static async Task DiscoveryRetirement()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        var sources = NovelTestSources.Create(runtime);
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
        var before = module.DiscoverBooks.ToArray();
        Check(before.Length == 1 && module.MoreDiscovery && module.ExploreKind?.Title == "榜单");
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
            int disabledInvocations = runtime.Calls.Count(call => call.Method == "source.runtime.library.invoke");
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
            Check(module.ExploreSource?.InstallationId == "a" && module.DiscoverBooks.Count == 0 &&
                module.DiscoveryState == NovelDiscoveryState.Unavailable, "An unavailable selection silently fell back to another enabled source.");
            Check(!module.LoadingDiscovery &&
                runtime.Calls.Count(call => call.Method == "source.runtime.library.invoke") == disabledInvocations,
                "Publishing the same unavailable source cancelled its availability read or invoked a disabled source.");
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
        var sources = NovelTestSources.Create(runtime); await sources.RefreshAsync();
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
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
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
        await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
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
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        var root = new Grid(); root.Children.Add(module.View);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = GeneralAppearanceTests.Window(root); dialogs.Attach(window); window.Show();
        try
        {
            await module.NavigateAsync(NovelCapability.HomeRoute, default);
            await Until(() => Find<Button>(dialogs).Any(button => Equals(button.Content, "重试") && button.IsLoaded));
            var surface = Find<Border>(dialogs).Single(b => AutomationProperties.GetName(b) == "发现加载失败");
            Check(module.Notice.Contains("发现加载失败") &&
                !Find<TextBlock>(module.View).Any(t => t.Text.Contains("发现加载失败")),
                "Discovery inserted an error explanation into its permanent page layout.");
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")));
            await Until(() => Find<TextBlock>(dialogs).Any(t => t.Text == "发现加载失败，请重试") &&
                Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")).IsEnabled);
            Check(dialogs.IsOpen && ReferenceEquals(surface.Parent, dialogs), "A failed retry replaced or closed its error dialog.");
            fail = false;
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")));
            await Until(() => !dialogs.IsOpen && !module.LoadingDiscovery);
            Check(module.DiscoverBooks.Single().Name == "重试结果" && module.ExploreSource?.InstallationId == "a");

            fail = true; await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            await Until(() => Find<Button>(dialogs).Any(button => Equals(button.Content, "重试") && button.IsLoaded));
            hold = true;
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "重试")));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "取消")));
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
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
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
            await NovelTestUi.LoadedAsync(module.View);
            await Until(() => Find<Button>(module.View).Any(button => AutomationProperties.GetName(button) == "加入书架" && button.IsLoaded));
            DesktopTestUi.Invoke(Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "加入书架"));
            await Until(() => module.Notice == "已加入书架");
            Check(module.Library.Entries.Single() == saved && module.Session is null,
                "Adding an alternate source removed or rewrote the canonical shelf/history entry.");
            await module.LoadDiscoveryAsync(runtime.Records[0], null, true);
            await Until(() => Find<Button>(module.View).Any(b =>
                b.IsLoaded && DesktopVisual.IsVisible(b) && AutomationProperties.GetName(b) == "移出书架"));
            DesktopTestUi.Invoke(Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "移出书架"));
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
            await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
            await module.InitializeAsync(default);
            var book = Book(author: "林舟");
            string key = await module.Rules.IdentityAsync(book);
            var older = new NovelEntry("older", key, book, ReadAt: DateTimeOffset.UtcNow.AddDays(-2),
                Anchor: new("chapter-1", "第一章", 0, 0, 1, .2));
            var latest = new NovelEntry("latest", key, sameSource ? book : Book("b", author: "林舟"), ReadAt: DateTimeOffset.UtcNow.AddHours(-1),
                Anchor: new("chapter-2", "第二章", 1, 0, 2, .6));
            Check(key.Length > 0 && key == await module.Rules.IdentityAsync(latest.Book),
                "The continuation fixture must retain distinct book ids with a shared matching identity.");
            await module.Library.SaveEntryAsync(older);
            int writes = store.Writes;
            string requested = ""; module.NavigationRequested += (_, e) => requested = e.Route;
            await module.OpenBookAsync(older.Book);
            Check(requested == NovelCapability.BookRoute && module.Session?.Entry.BookId == older.BookId && store.Writes == writes);
            await module.Library.SaveEntryAsync(latest); writes = store.Writes;
            await module.NavigateAsync(NovelCapability.HomeRoute, default);
            var window = GeneralAppearanceTests.Window(module.View); window.Show();
            try
            {
                await DesktopTestUi.DrainAsync();
                Check(store.Writes == writes && module.Library.Entries.Single(e => e.BookId == latest.BookId).Anchor == latest.Anchor,
                    "Opening a book or displaying continuation wrote reading history.");
                DesktopTestUi.Invoke(Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "接着读"));
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
                    var retainedSession = module.Session; int retainedWrites = store.Writes;
                    try
                    {
                        await module.OpenBookAsync(module.DiscoverBooks.Single());
                        throw new InvalidOperationException("Opening a discovered book guessed an ambiguous saved identity.");
                    }
                    catch (System.IO.InvalidDataException error) { Check(error.Message == "AMBIGUOUS_NOVEL_IDENTITY"); }
                    Check(ReferenceEquals(module.Session, retainedSession) && requested.Length == 0 &&
                        store.Writes == retainedWrites && module.Library.Entries.Count == 2,
                        "Ambiguous discovery navigation changed the retained session or saved records.");
                    await module.Library.DeleteHistoryAsync(latest.BookId);
                    await Until(() => Find<Button>(module.View).Any(b =>
                        b.IsLoaded && DesktopVisual.IsVisible(b) && AutomationProperties.GetName(b) == "开始阅读"));
                    DesktopTestUi.Invoke(Find<Button>(module.View).Single(b => AutomationProperties.GetName(b) == "开始阅读"));
                    await Until(() => requested == NovelCapability.ReaderRoute);
                    Check(module.Session is { HasContent: true } canonical && canonical.Entry.BookId == older.BookId,
                        "Starting a discovered book rejected its existing canonical session from another source.");
                }
            }
            finally { window.Close(); }
        }
    }
    private static async Task DiscoveryImport()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime();
        await using var server = new RuntimeHttpClientTests.Server();
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.HomeRoute, default);
        var before = runtime.SourceStore.Bytes!.ToArray();
        var root = new Grid();
        root.ColumnDefinitions.Add(new() { Width = new(216) }); root.ColumnDefinitions.Add(new());
        root.Children.Add(module.SidebarView); Grid.SetColumn(module.View, 1); root.Children.Add(module.View);
        var dialogs = new PlayerDialogHost(); Grid.SetColumnSpan(dialogs, 2); root.Children.Add(dialogs);
        var emitted = new List<DesktopNoticeEventArgs>();
        var window = GeneralAppearanceTests.Window(root); dialogs.Attach(window);
        NoticeOutlet.SetReceiver(DesktopTestUi.WindowRoot(window), e =>
        { if (e.Message.Length > 0) emitted.Add(e); });
        window.Show();
        try
        {
            await NovelTestUi.LoadedAsync(module.SidebarView);
            var import = Find<Button>(module.SidebarView).Single(b => AutomationProperties.GetName(b) == "导入书源");
            DesktopTestUi.Invoke(import);
            await Until(() => dialogs.IsOpen && Find<TextBox>(dialogs).Any(t =>
                AutomationProperties.GetName(t) == "书源地址或内容" && t.IsLoaded && DesktopVisual.IsVisible(t)));
            var input = Find<TextBox>(dialogs).Single(t => AutomationProperties.GetName(t) == "书源地址或内容");
            input.Text = server.BaseUrl + "/list.json";
            Check(!server.HasConnection && runtime.SourceStore.Writes == 0 && runtime.SourceStore.Bytes!.SequenceEqual(before));
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "预览")));
            await using (var request = await server.AcceptAsync())
            {
                Check(request.Method == "GET" && request.Path == "/list.json" && request.Body.Length == 0);
                await request.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new[] { "b", "c" }.Select(id => new
                {
                    bookSourceName = Source(id).Name, bookSourceUrl = Source(id).SourceUrl,
                    enabled = false, enabledExplore = true, ruleSearch = new { },
                    exploreUrl = "榜单::https://catalog.invalid/month"
                }).ToArray()));
            }
            await Until(() => Find<ManagementImportSelection>(dialogs).Any() &&
                Find<CheckBox>(dialogs).Any(c => AutomationProperties.GetName(c) == "选择 书源c" && c.IsLoaded && DesktopVisual.IsVisible(c)) &&
                Find<Button>(dialogs).Any(b => Equals(b.Content, "确定导入") && b.IsLoaded && DesktopVisual.IsVisible(b)));
            Check(runtime.SourceStore.Writes == 0 && runtime.SourceStore.Bytes!.SequenceEqual(before) &&
                module.Sources.Sources.Single().InstallationId == "a" && emitted.Count == 0 && !server.HasConnection,
                "Preview changed the native configuration or emitted an import receipt before confirmation.");
            var second = Find<CheckBox>(dialogs).Single(c => AutomationProperties.GetName(c) == "选择 书源c");
            DesktopTestUi.Invoke(second); Check(second.IsChecked == false);
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "确定导入")));
            await Until(() => !dialogs.IsOpen && runtime.SourceStore.Writes == 1 && emitted.Count == 1);
            var saved = JsonSerializer.Deserialize<NovelSourceConfigurationSnapshot>(runtime.SourceStore.Bytes!, NovelJson.Options)!;
            var previous = JsonSerializer.Deserialize<NovelSourceConfigurationSnapshot>(before, NovelJson.Options)!;
            var selected = saved.Sources.Single(s => s.SourceUrl == Source("b").SourceUrl);
            using var definition = JsonDocument.Parse(selected.Definition);
            Check(saved.Revision == previous.Revision + 1 && saved.Sources.Length == 2 &&
                saved.Sources.Single(s => s.InstallationId == "a") == previous.Sources.Single() &&
                selected.Name == "书源b" && selected.Protocol == "legado" && !selected.Enabled && selected.EnabledExplore &&
                definition.RootElement.GetProperty("bookSourceName").GetString() == "书源b" &&
                !definition.RootElement.GetProperty("enabled").GetBoolean() &&
                module.Sources.Sources.Single(s => s.InstallationId == selected.InstallationId).Enabled == false &&
                !Find<Button>(module.SidebarView).Any(b => AutomationProperties.GetName(b) is "书源b" or "书源c") &&
                emitted.Single().Message == "已导入 1 个书源" && !server.HasConnection);

            var reopenedRuntime = new Runtime { Records = [] };
            var reopened = new NovelSourceService(reopenedRuntime, runtime.SourceStore);
            try
            {
                await reopened.RefreshAsync();
                var restored = reopened.Sources.Single(s => s.InstallationId == selected.InstallationId);
                Check(reopened.Sources.Count == 2 && restored.Name == selected.Name && restored.SourceUrl == selected.SourceUrl &&
                    restored.Revision == selected.Revision && !restored.Enabled && restored.EnabledExplore &&
                    reopenedRuntime.Calls.Count == 0 && runtime.SourceStore.Writes == 1,
                    "Confirmed import did not survive a native configuration reload without the runtime list.");
            }
            finally { await reopened.PrepareShutdownAsync(); }

            var committed = runtime.SourceStore.Bytes!.ToArray();
            DesktopTestUi.Invoke(Find<Button>(module.SidebarView).Single(b => AutomationProperties.GetName(b) == "导入书源"));
            await Until(() => dialogs.IsOpen && Find<Button>(dialogs).Any(b =>
                Equals(b.Content, "取消") && b.IsLoaded && DesktopVisual.IsVisible(b)));
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(b => Equals(b.Content, "取消")));
            await Until(() => !dialogs.IsOpen);
            Check(runtime.SourceStore.Writes == 1 && runtime.SourceStore.Bytes!.SequenceEqual(committed) &&
                !server.HasConnection && emitted.Count == 1,
                "Cancelling the source input started a preview, permanent import or result notification.");
            Check(!runtime.Calls.Any(call => call.Method is "source.runtime.library.list" or
                "source.runtime.library.previewImport" or "source.runtime.library.import"),
                "Discovery import delegated native configuration ownership back to the runtime.");
        }
        finally { dialogs.Close(); window.Close(); }
    }
    private static async Task DiscoveryPresentation()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [Source("a"), Source("b"), Source("c") with { Enabled = false }] };
        runtime.Read = (_, action, _, _) => Task.FromResult(action == "exploreKinds" ? DiscoveryKinds() :
            Json(new { books = Enumerable.Range(1, 6).Select(i => DiscoveryBook("真实书名" + i, "book-" + i)).ToArray() }));
        await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.HomeRoute, default);
        // Component dimensions must not depend on the native window's frame or monitor tracking limits.
        var root = new Grid { Width = 1440, Height = 900 };
        root.ColumnDefinitions.Add(new() { Width = new(216) }); root.ColumnDefinitions.Add(new());
        var sidebar = module.SidebarView; root.Children.Add(sidebar); Grid.SetColumn(module.View, 1); root.Children.Add(module.View);
        var host = GeneralAppearanceTests.Window(root); DesktopTestUi.ResizeWindow(host, 1440, DesktopTestUi.WindowSize(host).Height); DesktopTestUi.ResizeWindow(host, DesktopTestUi.WindowSize(host).Width, 900);
        host.Show();
        try
        {
            await DesktopTestUi.DrainAsync(); root.UpdateLayout();
            Check(Math.Abs(root.ActualWidth - 1440) < 1, $"Unexpected component width: {root.ActualWidth}.");
            var grid = Find<Panel>(module.View).Single(p => AutomationProperties.GetName(p) == "发现书籍");
            var tiles = grid.Children.Cast<FrameworkElement>().ToArray();
            var positions = tiles.Select(t => t.TransformToVisual(grid).TransformPoint(new Point())).ToArray();
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
            DesktopTestUi.Invoke(Find<Button>(sidebar).Single(b => AutomationProperties.GetName(b) == "书源管理"));
            Check(settings == 1, "The source management icon did not request the existing settings owner.");
            DesktopTestUi.Invoke(Find<Button>(sidebar).Single(b => AutomationProperties.GetName(b) == "书源b"));
            await Until(() => !module.LoadingDiscovery && module.ExploreSource?.InstallationId == "b");
            Check(module.DiscoverBooks.All(b => b.InstallationId == "b") && ReferenceEquals(sidebar, module.SidebarView));
            var sourcePicker = Find<ComboBox>(module.View).Single(c => AutomationProperties.GetAutomationId(c) == "novel.discovery.source");
            Check(sourcePicker.Focus(FocusState.Programmatic), "The source selector could not receive keyboard focus.");
            await Until(() => DesktopTestUi.HasFocus(sourcePicker));
            sourcePicker.SelectedItem = module.Sources.Sources.Single(s => s.InstallationId == "a");
            await Until(() => !module.LoadingDiscovery && module.ExploreSource?.InstallationId == "a");
            var replacementSource = Find<ComboBox>(module.View).Single(c => AutomationProperties.GetAutomationId(c) == "novel.discovery.source");
            await TestUiHost.LoadedAsync(replacementSource);
            await Until(() => DesktopTestUi.HasFocus(replacementSource));
            Check(DesktopTestUi.HasFocus(Find<ComboBox>(module.View).Single(c => AutomationProperties.GetAutomationId(c) == "novel.discovery.source")),
                "Changing source lost the source selector's keyboard focus.");
            Check(Find<RadioButton>(module.View).Where(r => r.GroupName == "novel.discovery.channels")
                .Select(r => r.Content).SequenceEqual(new[] { "源站月榜" }),
                "Home exposed a non-ranking category.");
            await module.NavigateAsync(NovelCapability.CategoriesRoute, default);
            await DesktopTestUi.DrainAsync(); root.UpdateLayout();
            var filter = Find<Grid>(module.View).Single(g => AutomationProperties.GetName(g) == "当前频道筛选");
            var kindPicker = Find<RadioButton>(filter).Single(r =>
                AutomationProperties.GetAutomationId(r) == "novel.discovery.kind:https://catalog.invalid/new");
            Check(kindPicker.Focus(FocusState.Programmatic), "A source category could not receive keyboard focus.");
            await Until(() => DesktopTestUi.HasFocus(kindPicker));
            kindPicker.IsChecked = true;
            await Until(() => !module.LoadingDiscovery && module.ExploreKind?.Title == "源站新作");
            var replacementKind = Find<RadioButton>(module.View).Single(r => Equals(r.Content, "源站新作"));
            await TestUiHost.LoadedAsync(replacementKind);
            await Until(() => DesktopTestUi.HasFocus(replacementKind));
            Check(DesktopTestUi.HasFocus(Find<RadioButton>(module.View).Single(r => Equals(r.Content, "源站新作"))),
                "Changing category lost the category control's keyboard focus.");
            await DesktopTestUi.DrainAsync(); root.UpdateLayout();
            var catalog = Find<Panel>(module.View).Single(p => AutomationProperties.GetName(p) == "分类结果");
            var cards = catalog.Children.Cast<FrameworkElement>().ToArray();
            var points = cards.Select(c => c.TransformToVisual(catalog).TransformPoint(new Point())).ToArray();
            Check(cards.Length == 6 && Math.Abs(points[0].Y - points[2].Y) < 1 && points[3].Y > points[0].Y &&
                Math.Abs(points[0].X - points[3].X) < 1, "The catalog did not keep three image/text columns in source order.");
            var channels = Find<RadioButton>(module.View).Where(r => r.GroupName == "novel.discovery.channels").ToArray();
            Check(channels.Select(r => r.Content).SequenceEqual(new[] { "源站分组", "源站题材" }) &&
                channels.All(r => ReferenceEquals(r.Style.BasedOn, DesktopVisual.Resource(module.View, "MeowFilm.SettingsArea"))) &&
                Find<TextBlock>(module.View).Any(t => t.Text == "源题材 · 源字数"));
            filter = Find<Grid>(module.View).Single(g => AutomationProperties.GetName(g) == "当前频道筛选");
            Check(Find<RadioButton>(filter).Select(r => r.Content).SequenceEqual(new[] { "源站月榜", "源站新作" }) &&
                !Find<RadioButton>(module.View).Any(r => Equals(r.Content, "原生分类")));
            channels[1].IsChecked = true;
            await Until(() => !module.LoadingDiscovery && module.ExploreKind?.Title == "原生分类");
            await DesktopTestUi.DrainAsync(); root.UpdateLayout();
            filter = Find<Grid>(module.View).Single(g => AutomationProperties.GetName(g) == "当前频道筛选");
            Check(Equals(Find<RadioButton>(filter).Single().Content, "原生分类") &&
                !Find<RadioButton>(module.View).Any(r => Equals(r.Content, "源站新作")),
                "Changing channel did not replace the single current filter row.");
            catalog = Find<Panel>(module.View).Single(p => AutomationProperties.GetName(p) == "分类结果");
            root.Width = 780; DesktopTestUi.ResizeWindow(host, 780, DesktopTestUi.WindowSize(host).Height); await DesktopTestUi.DrainAsync(); root.UpdateLayout();
            Check(Math.Abs(root.ActualWidth - 780) < 1, $"Unexpected narrow component width: {root.ActualWidth}.");
            foreach (var card in catalog.Children.Cast<FrameworkElement>())
            {
                var bounds = card.TransformToVisual(module.View).TransformBounds(new Rect(new Point(), card.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= module.View.ActualWidth + 1, "The narrow catalog overflowed its content column.");
            }
            await module.NavigateAsync(NovelCapability.HomeRoute, default);
            await DesktopTestUi.DrainAsync(); root.UpdateLayout();
            var selected = Find<Border>(module.View).Single(b => AutomationProperties.GetName(b) == "选读");
            var continuing = Find<Border>(module.View).Single(b => AutomationProperties.GetName(b) == "继续阅读");
            Check(continuing.TransformToVisual(module.View).TransformPoint(new Point()).Y >= selected.TransformToVisual(module.View).TransformPoint(new Point()).Y + selected.ActualHeight,
                "The narrow home squeezed continuation beside its selected book.");
            DesktopTheme.SetIsHighContrast(root, true); root.UpdateLayout();
            Check(((SolidColorBrush)selected.Background).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window) && ((SolidColorBrush)continuing.Background).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window) &&
                ((SolidColorBrush)Find<TextBlock>(selected).Single(t => t.Text == "真实书名1").Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText),
                "The discovery cards ignored the inherited high contrast canvas or text colors.");
            Check(Find<TextBlock>(module.View).Where(t => t.Text == "暂无封面")
                .All(t => ((SolidColorBrush)t.Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)),
                "Missing-cover status must retain system text contrast.");
            var search = new TextBox { Width = 160, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(search, 1); root.Children.Add(search); Check(search.Focus(FocusState.Programmatic));
            await module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true);
            await OwnerDispatcher.YieldAsync(DispatcherQueuePriority.Low);
            Check(DesktopTestUi.HasFocus(search), "Discovery presentation stole focus from the surrounding search control.");
        }
        finally { host.Close(); }
    }
}
