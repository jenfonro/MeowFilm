using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private static async Task ChapterContext()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var runtime = new Runtime(); var sources = new NovelSourceService(runtime); await using var rules = new NovelRuleProgram(ruleRuntime);
        bool paid = false; int bodyReads = 0;
        runtime.Read = (_, action, input, _) =>
        {
            JsonElement value;
            if (action == "bookInfo") value = Json(new { book = input.GetProperty("book") });
            else if (action == "toc") value = Json(new { chapters = new[] { Catalog(0), Catalog(1) } });
            else if (action is "content" or "payAction")
            {
                var chapter = input.GetProperty("chapter"); int index = chapter.GetProperty("index").GetInt32();
                foreach (string field in new[] { "title", "url", "bookUrl", "baseUrl", "updateTime", "time", "tag", "ruleExtension" })
                    Check(JsonElement.DeepEquals(chapter.GetProperty(field), Catalog(index).GetProperty(field)), "Chapter field lost: " + field);
                Check(JsonElement.DeepEquals(input.GetProperty("chapters")[index].GetProperty("ruleExtension"), Catalog(index).GetProperty("ruleExtension")));
                if (action == "payAction")
                {
                    Check(chapter.GetProperty("variable").GetProperty("stage").GetString() == "content");
                    Check(chapter.GetProperty("contentExtension").GetString() == "retained");
                    paid = true; value = Json(new { chapter = new { variable = new { stage = "purchase" }, payExtension = 7 } });
                }
                else
                {
                    if (index == 1 && ++bodyReads == 2)
                    {
                        Check(chapter.GetProperty("variable").GetProperty("stage").GetString() == "purchase");
                        Check(chapter.GetProperty("contentExtension").GetString() == "retained" && chapter.GetProperty("payExtension").GetInt32() == 7);
                    }
                    string text = index == 0 ? chapter.GetProperty("updateTime").GetString()! : "正文";
                    value = Json(new { title = chapter.GetProperty("title"), blocks = new[] { new NovelBlock("text", text) },
                        chapter = new { variable = new { stage = "content" }, contentExtension = "retained" } });
                }
            }
            else throw new InvalidOperationException("Unexpected action: " + action);
            return Task.FromResult(value);
        };
        var session = new NovelBookSession(sources, library, rules, new("context", "key", Book()));
        try
        {
            await session.LoadMetadataAsync(); await session.ReadAsync(0);
            Check(session.ContentTitle == "卷一" && session.Blocks.Single().Text == "卷内说明，不能丢失");
            await session.ReadAsync(1); await session.PurchaseAsync();
            Check(session.Chapter?.IsPay == true && bodyReads == 2);
            Check(runtime.Calls.Count(c => IsAction(c, "payAction")) == 1);
        }
        finally { await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync(); }
        JsonElement Catalog(int index) => Json(new
        {
            name = index == 0 ? "卷一" : "第一章", title = index == 0 ? "卷一" : "第一章",
            chapterUrl = index == 0 ? "卷一0" : "https://a.invalid/c/1", url = index == 0 ? "卷一0" : "https://a.invalid/c/1",
            index, isVolume = index == 0, isVip = index == 1, isPay = index == 1 && paid,
            bookUrl = Book().BookUrl, baseUrl = "https://a.invalid/toc", updateTime = "卷内说明，不能丢失", time = "原始时间", tag = "规则标签",
            variable = new { stage = "catalog" }, ruleExtension = new { nested = new object[] { "catalog", 7, true } }
        });
    }
    private static async Task ColdShelf()
    {
        foreach (string state in new[] { "enabled", "disabled", "removed" })
        {
            await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
            await library.SaveEntryAsync(new("cold-shelf", "key", Book(), OnShelf: true));
            var runtime = new Runtime { Records = state == "removed" ? [] : [Source() with { Revision = 2, Enabled = state == "enabled" }] };
            var sources = new NovelSourceService(runtime);
            await using var ruleRuntime = new NovelRuleRuntimeFixture();
            await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
            await module.InitializeAsync(default); await module.NavigateAsync(NovelCapability.ShelfRoute, default);
            Check(module.Library.Entries.Single().BookId == "cold-shelf" && runtime.Calls.Count == 0);
            await module.ReceiveBookAsync(module.Library.Entries.Single().Book, default);
            try
            {
                await module.NavigateAsync(NovelCapability.BookRoute, default); await module.Session!.ContinueAsync();
                Check(state == "enabled" && module.Session.HasContent && module.Session.Book.Revision == 2 && module.Session.Entry.BookId == "cold-shelf");
            }
            catch (BridgeException error) { Check(state != "enabled" && error.Code == "SOURCE_DISABLED"); }
            Check(module.ExploreSource is null && !runtime.Calls.Any(c => c.Method.Contains("source.load") || IsAction(c, "explore")));
            if (state != "enabled") Check(!runtime.Calls.Any(c => c.Method == "source.runtime.library.invoke"));
            Check(runtime.Calls.Count(c => c.Method == "source.runtime.library.list") == 1);
        }
    }
    private static async Task ReaderRules()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Text = "广告123\n[x]正文" };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var sources = new NovelSourceService(runtime); await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
        await module.InitializeAsync(default); await module.ReceiveBookAsync(Book(), default); await module.Session!.ContinueAsync();
        var settings = (NovelSettingsView)module.SettingsView;
        await settings.ImportRulesAsync("""
            [{"id":1700000000000,"name":"Reader默认正则","group":null,"pattern":"广告\\d+\\n","replacement":"","scope":null,"order":0,"isEnabled":true,"scopeContent":true},
             {"id":1700000000001,"name":"字面量","pattern":"[x]","replacement":"","isRegex":false,"order":1},
             {"id":1700000000002,"name":"非法","pattern":"[","replacement":""}]
            """, default);
        Check(module.Library.Rules.Count == 2 && module.Library.Rules[0].Id == "1700000000000" && module.Library.Rules[0].IsRegex);
        Check(!module.Library.Rules[1].IsRegex && module.Library.Rules.All(r => r.TimeoutMillisecond == 3000));
        Check(module.Session.Blocks.Single().Text == "正文" && module.Notice.Contains("规则 3 无效"));
        await settings.ImportRulesAsync("""[{"id":1700000000001,"pattern":"[x]","replacement":"保留","isRegex":false,"order":1}]""", default);
        Check(module.Library.Rules.Count == 2 && module.Session.Blocks.Single().Text == "保留正文");
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        Check(reopened.Rules.Select(r => r.Id).SequenceEqual(module.Library.Rules.Select(r => r.Id)));
    }
    private static async Task SessionCancellation()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var runtime = new Runtime { Locked = true }; var sources = new NovelSourceService(runtime);
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var session = new NovelBookSession(sources, library, rules, new("cancel", "key", Book()));
        await session.ContinueAsync(); await session.SaveProgressAsync(new("chapter-1", "第一章", 0));
        runtime.DuplicateTitles = true; runtime.Records = [Source() with { Revision = 2 }]; await sources.RefreshAsync();
        var choosing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SelectChapterAsync = async (_, token) => { choosing.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
        var continuation = session.ContinueAsync(); await choosing.Task; session.Cancel();
        try { await continuation; throw new InvalidOperationException("Continue cancellation lost"); } catch (OperationCanceledException) { }
        Check(session.HasContent && !session.Loading);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = async (_, action, input, token) =>
        {
            if (action == "payAction") { held.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { await drain.Task; } }
            throw new InvalidOperationException("Unexpected cancelled continuation");
        };
        var purchase = session.PurchaseAsync(); await held.Task; session.Cancel(); var shutdown = session.PrepareShutdownAsync();
        Check(!purchase.IsCompleted && !shutdown.IsCompleted); drain.TrySetResult();
        try { await purchase; throw new InvalidOperationException("Purchase cancellation lost"); } catch (OperationCanceledException) { }
        await shutdown; Check(session.HasContent && !session.Loading);
        await sources.PrepareShutdownAsync();
    }
    private static async Task RetainedShelfHistory()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        foreach (bool keepShelf in new[] { false, true })
        {
            await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
            var runtime = new Runtime(); var sources = new NovelSourceService(runtime); await using var rules = new NovelRuleProgram(ruleRuntime);
            var session = new NovelBookSession(sources, library, rules, new("retained-history", "key", Book()));
            try
            {
                await session.ContinueAsync();
                var anchor = new NovelAnchor("chapter-1", "第一章", 0, 0, 2, .3);
                await session.SaveProgressAsync(anchor);
                if (keepShelf) { await session.ShelfAsync(); await library.PinAsync(session.Entry.BookId); }
                var blocks = session.Blocks; var chapter = session.Chapter; var book = session.Book;
                await library.DeleteHistoryAsync(session.Entry.BookId);
                Check(keepShelf ? library.Entries.Single() is { OnShelf: true, ReadAt: null, Anchor: null } : library.Entries.Count == 0);
                int calls = runtime.Calls.Count;
                if (keepShelf)
                {
                    await session.ShelfAsync();
                    Check(library.Entries.Count == 0, "Removing the retained shelf member revived deleted history.");
                }
                await session.ShelfAsync();
                var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
                var saved = reopened.Entries.Single();
                Check(saved is { OnShelf: true, ReadAt: null, Anchor: null, PinnedAt: null } &&
                    saved.BookId == "retained-history" && saved.Book.Revision == book.Revision &&
                    JsonElement.DeepEquals(saved.Book.Json(), book.Json()),
                    "Adding a retained session restored deleted history or changed its local identity.");
                Check(ReferenceEquals(blocks, session.Blocks) && ReferenceEquals(chapter, session.Chapter) &&
                    session.Entry.Anchor == anchor && session.HasContent && runtime.Calls.Count == calls);
                await session.ReadAsync(1);
                var nextAnchor = new NovelAnchor("chapter-2", "第二章", 1, 0, 1, .5);
                await session.SaveProgressAsync(nextAnchor);
                reopened = new NovelLibrary(store); await reopened.InitializeAsync(); saved = reopened.Entries.Single();
                Check(saved.OnShelf && saved.ReadAt is not null && saved.Anchor == nextAnchor && saved.BookId == "retained-history");
            }
            finally { await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync(); }
        }
    }
    private static async Task CatalogAtomic()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        foreach (string route in new[] { "refresh", "continue", "read" })
        {
            await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
            var runtime = new Runtime(); var sources = new NovelSourceService(runtime); await using var rules = new NovelRuleProgram(ruleRuntime);
            var session = new NovelBookSession(sources, library, rules, new("catalog-atomic", "key", Book()));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await session.LoadMetadataAsync();
                Check(library.Entries.Count == 0 && !session.HasContent && session.Entry.ReadAt is null && session.Entry.Anchor is null);
                await session.ReadAsync(1); await session.SaveProgressAsync(new("chapter-2", "第二章", 1, 0, 2, .4));
                var oldEntry = session.Entry; var oldChapters = session.Chapters; var oldChapter = session.Chapter; var oldBlocks = session.Blocks;
                string oldTitle = session.ContentTitle, oldStyle = session.ImageStyle;
                byte[] oldBytes = store.Bytes!.ToArray(); int oldWrites = store.Writes;
                NovelChapter[] catalog = route == "refresh"
                    ? [new("第二章", "chapter-2", 0)]
                    : [new("第二章", "chapter-2", 0), new("第一章", "chapter-1", 1), new("第三章", "chapter-3", 2)];
                var candidate = Book(author: "补全作者") with { Intro = "刷新后的详情" };
                string stage = "failure";
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.Read = async (_, action, input, _) =>
                {
                    if (action == "bookInfo") return Json(new { book = candidate.Json() });
                    if (action == "toc") return Json(new { book = candidate.Json(), chapters = catalog });
                    Check(action == "content");
                    if (stage == "failure") throw new BridgeException("NETWORK_ERROR");
                    if (stage == "cancel") { entered.TrySetResult(); await release.Task; }
                    var chapter = input.GetProperty("chapter");
                    return Json(new { title = chapter.GetProperty("name"), blocks = new[]
                        { new NovelBlock("text", "新正文：" + chapter.GetProperty("chapterUrl").GetString()) }, imageStyle = "FULL" });
                };
                if (route != "refresh")
                {
                    runtime.Records = [Source() with { Revision = 2 }];
                    await sources.RefreshAsync();
                }
                var observed = new List<NovelEntry>();
                session.Changed += (_, _) => observed.Add(session.Entry);
                try { await Reload(); throw new InvalidOperationException(route + ": failed content committed."); }
                catch (BridgeException error) { Check(error.Code == "NETWORK_ERROR"); }
                Unchanged();
                stage = "cancel";
                using (var cancellation = new CancellationTokenSource())
                {
                    var pending = Reload(cancellation.Token);
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                        Check(!pending.IsCompleted, route + ": cancelled content did not wait for its physical receipt.");
                        Unchanged();
                    }
                    finally { release.TrySetResult(); }
                    try { await pending; throw new InvalidOperationException(route + ": late cancelled content committed."); }
                    catch (OperationCanceledException) { }
                }
                Unchanged();
                Check(observed.All(value => value == oldEntry), route + ": a partial reading snapshot was published.");
                stage = "success";
                await Reload();
                int expectedIndex = route == "read" ? 1 : 0;
                string expectedUrl = route == "read" ? "chapter-1" : "chapter-2";
                var chapter = session.Chapter ?? throw new InvalidOperationException("Committed refresh has no chapter.");
                var anchor = session.Entry.Anchor ?? throw new InvalidOperationException("Committed refresh has no anchor.");
                Check(session.Entry.BookId == oldEntry.BookId && session.Book.Author == candidate.Author &&
                    session.Book.Intro == candidate.Intro && session.Book.Revision == (route == "refresh" ? 1 : 2) &&
                    session.Entry.IdentityKey == await rules.IdentityAsync(session.Book) && session.Entry.IdentityKey != oldEntry.IdentityKey);
                Check(session.Chapters.Count == catalog.Length && session.Entry.ChapterCount == catalog.Length &&
                    chapter.ChapterUrl == expectedUrl && chapter.Index == expectedIndex &&
                    ReferenceEquals(chapter, session.Chapters[expectedIndex]) &&
                    anchor.ChapterUrl == expectedUrl && anchor.ChapterIndex == expectedIndex &&
                    session.Blocks.Single().Text == "新正文：" + expectedUrl && session.ContentTitle == chapter.Name && session.ImageStyle == "FULL");
                if (route != "read")
                    Check(anchor.BlockIndex == oldEntry.Anchor!.BlockIndex &&
                        anchor.Utf16Offset == oldEntry.Anchor.Utf16Offset && anchor.Ratio == oldEntry.Anchor.Ratio);
                else Check(anchor.BlockIndex == 0 && anchor.Utf16Offset == 0 && anchor.Ratio == 0);
                var persisted = library.Entries.Single();
                Check(persisted.Book == session.Book && persisted.IdentityKey == session.Entry.IdentityKey &&
                    persisted.ReadAt == oldEntry.ReadAt && persisted.Anchor == oldEntry.Anchor &&
                    persisted.ContentSignature == oldEntry.ContentSignature);
                // Navigation uses the newly committed index, including the reordered catalog's next chapter.
                if (route == "continue")
                {
                    await session.ReadAsync(session.Chapter!.Index + 1);
                    Check(session.Chapter?.ChapterUrl == "chapter-1" && session.Blocks.Single().Text == "新正文：chapter-1");
                }
                else if (route == "read")
                {
                    await session.ReadAsync(session.Chapter!.Index - 1);
                    Check(session.Chapter?.ChapterUrl == "chapter-2" && session.Blocks.Single().Text == "新正文：chapter-2");
                }
                Task Reload(CancellationToken token = default) => route switch
                {
                    "refresh" => session.LoadMetadataAsync(token),
                    "continue" => session.ContinueAsync(token),
                    _ => session.ReadAsync(0, token: token)
                };
                void Unchanged() => Check(session.Entry == oldEntry && ReferenceEquals(session.Book, oldEntry.Book) &&
                    ReferenceEquals(session.Chapters, oldChapters) && ReferenceEquals(session.Chapter, oldChapter) &&
                    ReferenceEquals(session.Blocks, oldBlocks) && session.ContentTitle == oldTitle && session.ImageStyle == oldStyle &&
                    session.HasContent && store.Writes == oldWrites && store.Bytes!.SequenceEqual(oldBytes),
                    route + ": failed or cancelled refresh replaced part of the active reading state.");
            }
            finally { release.TrySetResult(); await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync(); }
        }
    }
    private static async Task ImageAnchor()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        string before = string.Concat(Enumerable.Repeat("图前段落😀，窗口重排不改变图片位置。\n", 60));
        string after = string.Concat(Enumerable.Repeat("图后正文。\n", 150));
        var runtime = new Runtime { GradientImage = true, Blocks = [new("text", before), new("image", Src: "https://a.invalid/tall.png"), new("text", after)] };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var sources = new NovelSourceService(runtime); await using var rules = new NovelRuleProgram(ruleRuntime);
        var session = new NovelBookSession(sources, library, rules, new("image-anchor", "key", Book())); await session.ContinueAsync();
        var surface = new NovelReadingSurface(sources); var host = GeneralAppearanceTests.Window(surface); host.Width = 880; host.Height = 680;
        try
        {
            host.Show(); surface.Present(session, new() { Mode = "scroll" }); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            // The logical midpoint of the image is independent of preceding text height.
            double ratio = (before.Length + .5) / (before.Length + 1 + after.Length);
            surface.Seek(ratio); await Until(() => MidpointVisible(surface));
            var anchor = surface.CaptureAnchor(); Check(anchor.BlockIndex == 1 && Math.Abs(anchor.Ratio - ratio) < .00001);
            surface.Present(session, new() { Mode = "scroll", FontSize = 32, Margin = 48 });
            host.Width = 720; host.Height = 560; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            Check(MidpointVisible(surface) && surface.CaptureAnchor().BlockIndex == 1);
            int reads = runtime.ImageReads;
            host.Width = 980; host.Height = 700; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            await Until(() => runtime.ImageReads > reads && MidpointVisible(surface));
            Check(surface.CaptureAnchor().BlockIndex == 1 && Math.Abs(surface.CaptureAnchor().Ratio - ratio) < .00001);
            await surface.DrainImagesAsync();
            runtime.ImageReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); runtime.ImageReadHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await session.ReadAsync(0, anchor); surface.Present(session, new() { Mode = "scroll", FontSize = 28, Margin = 48 }); surface.ResumeImages();
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render); await runtime.ImageReadStarted.Task;
            host.Width = 660; host.Height = 620; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            runtime.ImageReadHold.TrySetResult(); await Until(() => MidpointVisible(surface));
            Check(surface.CaptureAnchor().BlockIndex == 1 && Math.Abs(surface.CaptureAnchor().Ratio - ratio) < .00001);
            Preview(surface, "novel-reading-image-anchor", 660, 570);
        }
        finally { await surface.DrainImagesAsync(); surface.Dispose(); host.Close(); await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync(); }
        static bool MidpointVisible(NovelReadingSurface surface)
        {
            if (surface.ActualWidth < 1 || surface.ActualHeight < 1) return false;
            var frame = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            frame.Render(surface); byte[] pixel = new byte[4];
            frame.CopyPixels(new Int32Rect((int)surface.ActualWidth / 2, 68, 1, 1), pixel, 4, 0);
            return pixel[0] is >= 120 and <= 140 && pixel[1] == pixel[0] && pixel[2] == pixel[0] && pixel[3] == 255;
        }
    }
}
