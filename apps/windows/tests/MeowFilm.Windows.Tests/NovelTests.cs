using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Media.Imaging;
using MeowFilm.Platform.Runtime;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel native library: independent shelf/history stable identity accepted write and corruption", Library);
        yield return new("novel native library: real atomic store reopens and second process restores reading", Durable);
        yield return new("novel source repository: no native source copy revision retire import and export", () => Sta(Sources));
        yield return new("novel multi-source search: unknown authors pagination partial failure and stale query", () => Sta(Search));
        yield return new("novel reading session: metadata creates no history source switch ambiguity and failure preserve book", () => Sta(Reading));
        yield return new("novel retained shelf: deleted history stays deleted until new reading is saved", () => Sta(RetainedShelfHistory));
        yield return new("novel catalog refresh: metadata and automatic reload commit one reading snapshot or preserve the old one", () => Sta(CatalogAtomic));
        yield return new("novel source switch identity: enriched author persists and third source keeps the same local book", () => Sta(SwitchIdentity));
        yield return new("novel chapter context: native toc content and purchase retain open JSON fields and volume text", () => Sta(ChapterContext));
        yield return new("novel cold shelf: saved book reads without home and disabled or removed sources stay retired", () => Sta(ColdShelf));
        yield return new("novel Reader rules: numeric import identity default regex and literal rules affect native content", () => Sta(ReaderRules));
        yield return new("novel retained reading: restore continue and start use latest raw cleanup conversion without downloads", () => Sta(RetainedProcessing));
        yield return new("novel retained cleanup: whole-batch failure cancellation and newer settings preserve committed content", () => Sta(ReprocessingAtomic));
        yield return new("novel settings return: retained reader refreshes without navigation replay and retries failed cleanup", () => Sta(SettingsReturnProcessing));
        yield return new("novel reader image anchors: text before long image resize and late pixels preserve relative position", () => Sta(ImageAnchor));
        yield return new("novel Web retirement: source disabling cancels browser and drains profile before invocation receipt", () => Sta(WebRetirement));
        yield return new("novel Web rule retirement: late remote URL match drains without committing source cookies", () => Sta(WebRuleRetirement));
        yield return new("novel Web cancellation: an unrelated active source cannot bypass the retired interaction drain", () => Sta(ConcurrentWebCancellation));
        yield return new("novel Web cookies: authoritative scoped seeds cross-origin changes and deletions retain cookie identity", () => Sta(WebCookies));
        yield return new("novel Web cookie owner: host-only and domain scope replacements survive both directions", () => Sta(WebCookieScopeOwner));
        yield return new("novel Web scalars: async false null number and __Host domain semantics survive native collection", () => Sta(WebScalars));
        yield return new("novel dialog closure: manual and automatic results wait for actual Closed", () => Sta(DialogClosure));
        yield return new("novel dialog cancellation: busy user token and owner close drain save before returning", () => Sta(DialogCancellation));
        yield return new("novel dialog save failure: open retry and closed failure or cancellation wait for the actual write", () => Sta(DialogSaveFailures));
        yield return new("novel dialog failure owners: closed save failures remain visible and cancellation stays silent", () => Sta(DialogFailureOwners));
        yield return new("novel dialog isolation: retired popup cannot cancel the next confirmation", () => Sta(DialogIsolation));
        yield return new("novel dialog presentation: application host keeps themed surface compact and outside clicks inert", () => Sta(DialogHostPresentation));
        yield return new("novel dialog isolation: retired application editor cannot close or refocus the next dialog", () => Sta(DialogHostIsolation));
        yield return new("novel Web lifecycle: actual environment exit releases profile before success cancellation and owner close", () => Sta(WebLifecycle));
        yield return new("novel session cancellation: continue selection and purchase refresh stay within one cancelled operation", () => Sta(SessionCancellation));
        yield return new("novel shared fixtures: real Node RPC ordered cleanup and final conversion", Fixtures);
        yield return new("novel runtime host: library import metadata and private values persist after process restart", () => Sta(RuntimeLibrary));
        yield return new("novel Web host: real dynamic page cookie envelope verification and cancelled profile cleanup", () => Sta(WebHost));
        yield return new("novel purchase components: confirmation cancellation navigation and source declared purchase", () => Sta(Purchase));
        yield return new("novel cleanup RPC: per-rule budget whole-batch failure and runtime recovery", Budget);
        yield return new("novel rules RPC: whole request snapshot remote errors cancellation drain and shared ownership", RuleConsumer);
        yield return new("novel reset: pending rule and accepted native writes drain before a fresh owner replaces navigation", () => Sta(ResetOwners));
        yield return new("novel source images: full-size trusted HTTP data and caller cancellation use the unbounded API", SourceImages);
        yield return new("novel reader images: viewport cancellation drains binary image leases", () => Sta(ImageLifecycle));
        yield return new("novel cover viewport: offscreen demand cancellation reacquisition and late pixels stay isolated", () => Sta(CoverViewport));
        yield return new("novel reader components: four modes resize UTF16 anchors image release and empty chapters", () => Sta(Layout));
        yield return new("novel reader labels: font resize and late images update pages without saving position", () => Sta(LayoutLabels));
        yield return new("novel shell: immersive reading hides navigation and exit restores retained book", () => Sta(Shell));
        yield return new("novel composition: native sidebar detail reading settings and local offline state", () => Sta(Composition));
    }
    internal static Task Sta(Func<Task> action) => MusicQualityConsumerTests.Sta(action);
    private static void Check(bool condition, string message = "Novel assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static NovelBook Book(string installation = "a", string url = "https://novel.invalid/book", string author = "作者") =>
        new(installation, 1, url, "合成小说", author, TocUrl: url + "/toc");
    internal static NovelSource Source(string id = "a") => new(id, 1, "书源" + id, "https://" + id + ".invalid", "合成", 0, true, true,
        "legado", ["search", "exploreKinds", "explore", "bookInfo", "toc", "content", "login", "payAction"]);
    private static async Task Library()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var entry = new NovelEntry("stable", "identity", Book(), true, DateTimeOffset.UtcNow);
        await library.SaveEntryAsync(entry); Check(library.Entries.Single().ReadAt is null);
        var read = entry with { Anchor = new("chapter", "第一章", 0, 1, 2, .4), ReadAt = DateTimeOffset.UtcNow };
        await library.SaveEntryAsync(read); await library.SetShelfAsync("stable", false);
        Check(!library.Entries.Single().OnShelf && library.Entries.Single().ReadAt is not null);
        await library.SetShelfAsync("stable", true); await library.DeleteHistoryAsync("stable");
        Check(library.Entries.Single().OnShelf && library.Entries.Single().Anchor is null);
        await library.PinAsync("stable"); Check(library.Entries.Single().PinnedAt is not null);
        var previous = store.Bytes!.ToArray(); store.FailWrite = true;
        try { await library.SaveAppearanceAsync(new() { FontSize = 27 }); throw new InvalidOperationException("Failed write published"); }
        catch (IOException) { }
        Check(library.Appearance.FontSize == 22 && store.Bytes.SequenceEqual(previous)); store.FailWrite = false;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously); store.Hold = hold;
        using var cancellation = new CancellationTokenSource();
        var save = library.SaveAppearanceAsync(new() { FontSize = 26 }, cancellation.Token);
        await store.Accepted.Task; cancellation.Cancel(); var drain = library.PrepareShutdownAsync();
        Check(!drain.IsCompleted); hold.TrySetResult(); await save; await drain;
        Check(library.Appearance.FontSize == 26);
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync(); Check(reopened.Appearance.FontSize == 26 && reopened.Entries.Single().BookId == "stable");
        store.Bytes = "broken"u8.ToArray(); var corrupt = new NovelLibrary(store);
        try { await corrupt.SaveAppearanceAsync(new()); throw new InvalidOperationException("Corrupt storage overwritten"); }
        catch (DocumentStoreException e) { Check(e.Error == DocumentStoreError.Corrupt); }
        Check(Encoding.UTF8.GetString(store.Bytes) == "broken");
    }
    private static async Task Durable()
    {
        string path = TestWorkspace.DataPath("novel-durable"); Directory.CreateDirectory(path);
        await using (var store = new LocalDocumentStore(path, [NovelLibrary.DocumentNamespace]))
        {
            var library = new NovelLibrary(store); await library.InitializeAsync();
            await library.SaveEntryAsync(new("stable-durable", "key", Book(), true, ReadAt: DateTimeOffset.UtcNow, Anchor: new("chapter", "第一章", 0, 3, 5, .5)));
            await library.SaveAppearanceAsync(new() { Mode = "scroll", FontSize = 25, Theme = 6 });
            await library.PrepareShutdownAsync();
        }
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--novel-restore"); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Restore child missing");
        string output = await process.StandardOutput.ReadToEndAsync(); string errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Check(process.ExitCode == 0 && output.Contains("NOVEL_RESTORED stable-durable"), errors);
        Console.Write(output);
    }
    internal static async Task<int> RestoreChild(string path)
    {
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, path);
        await using var store = new LocalDocumentStore(path, [NovelLibrary.DocumentNamespace]);
        var library = new NovelLibrary(store); await library.InitializeAsync();
        var entry = library.Entries.Single(); Check(entry.BookId == "stable-durable" && entry.OnShelf && entry.Anchor?.BlockIndex == 3 && entry.Anchor.Utf16Offset == 5 && library.Appearance.FontSize == 25 && library.Appearance.Mode == "scroll");
        Console.WriteLine("NOVEL_RESTORED " + entry.BookId); return 0;
    }
    private static async Task Sources()
    {
        var runtime = new Runtime(); var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        Check(sources.Sources.Count == 1); var source = sources.Sources[0];
        var get = await sources.GetAsync(source); Check(get.GetProperty("definition").GetString() == "{\"bookSourceName\":\"合成\"}");
        var exported = await sources.ExportAsync([source.InstallationId]); Check(exported.GetProperty("text").GetString() == "[]");
        await sources.ImportAsync(text: "[]");
        var import = runtime.Calls.Last(c => c.Method.EndsWith("library.import")).Payload;
        Check(import.TryGetProperty("text", out _) && !import.TryGetProperty("url", out _));
        var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = (_, action, _, _) => action == "search" ? late.Task : Task.FromResult(Json(new { }));
        var pending = sources.InvokeAsync(source, "search", new { keyword = "旧", page = 1 });
        await sources.UpdateAsync(source, new { enabled = false });
        late.TrySetResult(Json(new { books = new object[0] }));
        try { await pending; throw new InvalidOperationException("Retired source published"); } catch (OperationCanceledException) { }
        await sources.PrepareShutdownAsync();
    }
    private static async Task Search()
    {
        var runtime = new Runtime { Records = [Source("a"), Source("b"), Source("c")] };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync(); await using var rules = new NovelRuleProgram(ruleRuntime);
        var search = new NovelSearch(sources, rules);
        runtime.Read = (source, action, payload, _) =>
        {
            if (action != "search") return Task.FromResult(Json(new { }));
            if (source.InstallationId == "c") throw new BridgeException("NETWORK_ERROR");
            int page = payload.GetProperty("page").GetInt32();
            return Task.FromResult(Json(new { books = page == 1 ? new[] { Book(source.InstallationId, author: source.InstallationId == "a" ? "" : "作者").Json() } : Array.Empty<JsonElement>() }));
        };
        await search.StartAsync("合成"); Check(search.Books.Count == 2 && search.States.Count(s => s.Status == "失败") == 1 && search.CanLoadMore);
        await search.LoadMoreAsync(); Check(search.Books.Count == 2 && search.States.Count(s => s.Ended) == 2);
        var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = (_, _, payload, _) => payload.GetProperty("keyword").GetString() == "旧词" ? late.Task : Task.FromResult(Json(new { books = new[] { Book().Json() } }));
        var old = search.StartAsync("旧词"); await search.StartAsync("新词"); late.TrySetResult(Json(new { books = new[] { Book(url: "old").Json() } }));
        try { await old; } catch (OperationCanceledException) { }
        Check(search.Query == "新词" && search.Books.All(b => b.BookUrl != "old"));
        await search.PrepareShutdownAsync(); await sources.PrepareShutdownAsync();
    }
    private static async Task Reading()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync(); await using var rules = new NovelRuleProgram(ruleRuntime);
        var session = new NovelBookSession(sources, library, rules, new("stable", "key", Book()));
        await session.LoadMetadataAsync(); Check(library.Entries.Count == 0 && !session.HasContent);
        await session.ContinueAsync(); Check(session.HasContent && library.Entries.Count == 0);
        await session.SaveProgressAsync(new("chapter-1", "第一章", 0, 0, 2, .3));
        Check(library.Entries.Single().BookId == "stable" && !library.Entries.Single().OnShelf);
        session.SelectChapterAsync = (_, _) => Task.FromResult<int?>(null);
        runtime.DuplicateTitles = true;
        try { await session.SwitchSourceAsync(Book("b", "https://b.invalid/book")); throw new InvalidOperationException("Cancelled source selection completed"); } catch (OperationCanceledException) { }
        Check(session.Book.InstallationId == "a" && session.Entry.BookId == "stable");
        runtime.DuplicateTitles = false;
        await session.SwitchSourceAsync(Book("b", "https://b.invalid/book"));
        Check(session.Book.InstallationId == "b" && session.Entry.BookId == "stable" && session.Chapter?.Name == "第一章");
        runtime.FailContent = true;
        try { await session.SwitchSourceAsync(Book()); throw new InvalidOperationException("Failed switch committed"); } catch (BridgeException) { }
        Check(session.Book.InstallationId == "b");
        runtime.FailContent = false;
        await session.ReadAsync(1); await session.SaveProgressAsync(new("chapter-2", "第二章", 1));
        runtime.Read = (_, action, input, _) => Task.FromResult(action switch
        {
            "bookInfo" => Json(new { book = input.GetProperty("book") }),
            "toc" => Json(new { chapters = new[] { new NovelChapter("第二章", "chapter-2", 0) } }),
            "content" => Json(new { title = "第二章", blocks = new[] { new NovelBlock("text", "缩短目录后的正文") } }),
            _ => throw new InvalidOperationException("Unexpected shortened catalog action")
        });
        await session.LoadMetadataAsync(); await session.ContinueAsync();
        Check(session.Chapters.Count == 1 && session.Chapter?.Index == 0 && session.Entry.Anchor?.ChapterIndex == 0 &&
            session.Blocks.Single().Text == "缩短目录后的正文" && session.Entry.BookId == "stable");
        await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync();
    }
    private static async Task SwitchIdentity()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var sources = new NovelSourceService(new Runtime { Records = [Source("a"), Source("b")] });
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await sources.RefreshAsync(); await using var rules = new NovelRuleProgram(ruleRuntime);
        var unknown = Book(author: "");
        var session = new NovelBookSession(sources, library, rules, new("stable", await rules.IdentityAsync(unknown), unknown));
        await session.ContinueAsync(); await session.ShelfAsync(); await library.PinAsync("stable");
        await session.SaveProgressAsync(new("chapter-1", "第一章", 0, 0, 2, .3));
        var before = library.Entries.Single();
        await session.SwitchSourceAsync(Book("b", "https://b.invalid/book", "林"));
        var switched = library.Entries.Single();
        Check(switched.BookId == before.BookId && switched.Book.InstallationId == "b" && switched.Book.Author == "林");
        Check(switched.IdentityKey != before.IdentityKey && switched.IdentityKey == await rules.IdentityAsync(session.Book));
        Check(switched.OnShelf == before.OnShelf && switched.PinnedAt == before.PinnedAt &&
            switched.AddedAt == before.AddedAt && switched.ReadAt == before.ReadAt && switched.Anchor == session.Entry.Anchor);
        var reopened = new NovelLibrary(store); await reopened.InitializeAsync();
        var third = Book("c", "https://c.invalid/book", "林");
        Check(reopened.Entries.Count == 1 &&
            reopened.Find(third, await rules.IdentityAsync(third))?.BookId == before.BookId,
            "Author enrichment must persist its matching key, not create a new local book for a third source");
        await sources.PrepareShutdownAsync();
    }
    private static async Task Budget()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var original = new[] { new NovelBlock("text", new string('a', 200) + "!") };
        try
        {
            await rules.TransformAsync("正文", original, Book(), Source(), [new() { Pattern = "a", Replacement = "b", Order = 0 },
                new() { Pattern = "^(b+)+$", Replacement = "x", IsRegex = true, Order = 1, TimeoutMillisecond = 30 }], "none");
            throw new InvalidOperationException("Failed batch returned");
        }
        catch (BridgeException e) { Check(e.Code == "TIMEOUT"); }
        Check(original.Single().Text[0] == 'a');
        var clean = await rules.TransformAsync("标题", [new("text", "广告\n正文")], Book(), Source(), [new() { Pattern = "广告\n", Replacement = "" }], "none");
        Check(clean.GetProperty("blocks")[0].GetProperty("text").GetString() == "正文");
    }
    private static async Task Layout()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var runtime = new Runtime { Text = string.Concat(Enumerable.Repeat("正文😀组合字é和汉字。", 800)) };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync(); await using var rules = new NovelRuleProgram(ruleRuntime);
        var session = new NovelBookSession(sources, library, rules, new("layout", "key", Book())); await session.ContinueAsync();
        var surface = new NovelReadingSurface(sources);
        var host = new Window { Content = surface, Width = 900, Height = 720, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        NovelUi.Theme(host);
        try
        {
            host.Show();
            foreach (string mode in new[] { "cover", "slide", "none", "scroll" })
            {
                surface.Present(session, new() { Mode = mode }); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
                await surface.TurnAsync(1); var before = surface.CaptureAnchor();
                surface.Present(session, new() { Mode = mode, FontSize = 28, Margin = 48 });
                host.Width = 760; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
                var after = surface.CaptureAnchor();
                Check(before.ChapterUrl == after.ChapterUrl && before.BlockIndex == after.BlockIndex && Math.Abs(before.Utf16Offset - after.Utf16Offset) < 80);
                Check(!string.IsNullOrEmpty(surface.PageStatus));
            }
            Preview(surface, "novel-reading", 760, 650);
            runtime.Text = ""; await session.ReadAsync(0); surface.Present(session, new()); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            Check(surface.CaptureAnchor().Ratio == 0 && surface.PageStatus == "1 / 1");
        }
        finally { await surface.DrainImagesAsync(); surface.Dispose(); host.Close(); }
        await sources.PrepareShutdownAsync();
    }
    private static async Task Composition()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Records = [] };
        var sources = new NovelSourceService(runtime);
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
        await module.InitializeAsync(default);
        Check(module.Navigation.Where(n => n.ShowInSidebar).Select(n => n.Title).SequenceEqual(new[] { "小说", "书架" }));
        var host = GeneralAppearanceTests.Window(module.View); host.Width = 1080; host.Height = 780; host.Show();
        try
        {
            await module.NavigateAsync(NovelCapability.HomeRoute, default); await Dispatcher.Yield(DispatcherPriority.Render);
            Preview(module.View, "novel-home-empty", 1000, 700);
            Check(Find<Button>(module.View).Any(b => b.Content as string == "导入书源"));
            runtime.Records = [Source()]; await sources.RefreshAsync(); await module.LoadDiscoveryAsync(null, null, true);
            Preview(module.View, "novel-home-books", 1000, 700);
            Check(Find<TextBlock>(module.View).Any(b => b.Text == "合成小说"));
            await module.NavigateAsync(NovelCapability.ShelfRoute, default); Preview(module.View, "novel-shelf-empty", 1000, 700);
            await module.ReceiveBookAsync(Book(), default); await module.NavigateAsync(NovelCapability.BookRoute, default);
            Check(module.CaptureNavigationLocation().StartsWith("novel-book:") && module.Library.Entries.Count == 0);
            Preview(module.View, "novel-book", 1000, 700);
            await module.Session!.ShelfAsync(); await module.NavigateAsync(NovelCapability.ShelfRoute, default); Preview(module.View, "novel-shelf-books", 1000, 700);
            await module.Session.ContinueAsync(); await module.NavigateAsync(NovelCapability.ReaderRoute, default); Check(module.IsImmersive);
            module.View.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            Check(Find<NovelReadingSurface>(module.View).Any());
            await module.NavigateAsync(NovelCapability.ShelfRoute, default); Check(!module.IsImmersive);
            host.Content = module.SettingsView; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Preview(module.SettingsView, "novel-settings", 1000, 700);
            var labels = Find<Button>(module.SettingsView).Select(b => b.Content as string).ToArray(); Check(labels.Contains("书源管理") && labels.Contains("替换净化"));
            Find<Button>(module.SettingsView).Single(b => b.Content as string == "替换净化").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Preview(module.SettingsView, "novel-settings-rules", 1000, 700);
            await module.PrepareShutdownAsync(); await module.CancelShutdownPreparationAsync();
        }
        finally { host.Close(); }
    }
    internal static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal sealed class MemoryStore : IDocumentStore
    {
        internal byte[]? Bytes;
        internal bool FailWrite;
        internal Exception? WriteFailure;
        internal int Writes;
        internal TaskCompletionSource? Hold, Accepted;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) => Task.FromResult(new DocumentSnapshot(Bytes?.ToArray(), Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)));
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var copy = data.ToArray(); Writes++; Accepted?.TrySetResult(); if (Hold is not null) await Hold.Task;
            if (WriteFailure is { } failure) throw failure;
            if (FailWrite) throw new IOException("Synthetic atomic write failure");
            Check(expected == (Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes))); Bytes = copy; return DocumentFingerprint.FromBytes(copy);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Runtime : IRuntimeClient, IRuntimeImageClient
    {
        internal NovelSource[] Records = [Source()];
        internal readonly List<(string Method, JsonElement Payload)> Calls = [];
        internal Func<CancellationToken, Task>? Listing;
        internal Func<NovelSource, string, JsonElement, CancellationToken, Task<JsonElement>>? Read;
        internal bool DuplicateTitles, FailContent, Locked, Paid, IncludeImages;
        internal NovelBlock[]? Blocks;
        internal JsonElement[] PendingWeb = [];
        internal bool GradientImage;
        internal int Released, ImageReads;
        internal readonly HashSet<string> ImageReferences = [];
        internal TaskCompletionSource? ImageReadStarted, ImageReadHold;
        internal Func<string, CancellationToken, Task<byte[]>>? ReadImage;
        internal string Text = "正文段落\n下一段落";
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            var payload = BridgeCodec.Value(parameters ?? new { }); Calls.Add((method, payload)); JsonElement result;
            switch (method["source.runtime.".Length..])
            {
                case "library.list": if (Listing is not null) await Listing(token); result = Json(new { sources = Records }); break;
                case "web.pending": result = Json(new { requests = PendingWeb }); break;
                case "web.respond": result = Json(new { accepted = true }); break;
                case "library.get": result = Json(new { source = Records[0], definition = "{\"bookSourceName\":\"合成\"}" }); break;
                case "library.export": result = Json(new { text = "[]" }); break;
                case "library.import": result = Json(new { sources = Records, failures = new object[0] }); break;
                case "library.save": Records = [Records[0] with { Revision = Records[0].Revision + 1 }]; result = Json(new { source = Records[0] }); break;
                case "library.update": Records = [Records[0] with { Enabled = false, Revision = Records[0].Revision + 1 }]; PendingWeb = []; result = Json(new { source = Records[0] }); break;
                case "library.image":
                    string reference = "/v1/source/images/" + Guid.NewGuid().ToString("N"); ImageReferences.Add(reference); result = Json(new { reference }); break;
                case "library.releaseImage": Released++; ImageReferences.Remove(payload.GetProperty("reference").GetString()!); result = Json(new { released = true }); break;
                case "library.invoke":
                    var source = Records.Single(s => s.InstallationId == payload.GetProperty("installationId").GetString());
                    string action = payload.GetProperty("action").GetString()!; var input = payload.GetProperty("payload");
                    result = Read is not null ? await Read(source, action, input, token) : Default(source, action, input); break;
                default: result = Json(new { released = true }); break;
            }
            return Json(new { ok = true, contract = "source.runtime.v2", value = result });
        }
        private JsonElement Default(NovelSource source, string action, JsonElement input) => action switch
        {
            "bookInfo" => Json(new { book = input.GetProperty("book") }),
            "toc" => Json(new { chapters = DuplicateTitles ? new[] { new NovelChapter("第一章", "other-1", 0), new("第一章", "other-2", 1) } :
                new[] { new NovelChapter("第一章", source.InstallationId == "a" ? "chapter-1" : "b-chapter-1", 0, Locked, Paid), new("第二章", "chapter-2", 1) } }),
            "content" => FailContent ? throw new BridgeException("NETWORK_ERROR") : Json(new { title = "第一章", content = Text,
                blocks = Blocks ?? (IncludeImages ? new[] { new NovelBlock("image", Src: "https://a.invalid/full.png", Alt: "正文插图"), new("text", Text) } : Text.Length == 0 ? Array.Empty<NovelBlock>() : new[] { new NovelBlock("text", Text) }), imageStyle = "FULL" }),
            "payAction" => Pay(),
            "exploreKinds" => Json(new { kinds = new[] { new { title = "分类", url = "https://a.invalid/explore" } } }),
            "explore" or "search" => Json(new { books = new[] { Book(source.InstallationId).Json() } }),
            _ => Json(new { })
        };
        private JsonElement Pay() { Paid = true; return Json(new { paid = true }); }
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken token = default) =>
            throw new InvalidOperationException("Novel images must not use the card byte quota.");
        public async Task<byte[]> ReadImageAsync(string reference, CancellationToken token = default)
        {
            Check(ImageReferences.Contains(reference)); ImageReads++; ImageReadStarted?.TrySetResult();
            if (ReadImage is not null) return await ReadImage(reference, token);
            if (ImageReadHold is not null) await ImageReadHold.Task.WaitAsync(token);
            int width = GradientImage ? 400 : 1200, height = GradientImage ? 4000 : 1600;
            var pixels = new byte[width * height * 4];
            if (GradientImage)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int at = (y * width + x) * 4;
                        pixels[at] = pixels[at + 1] = pixels[at + 2] = (byte)(y * 255 / (height - 1)); pixels[at + 3] = 255;
                    }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        }
        public Task<JsonElement> UploadSourceAsync(string module, string id, long revision, ReadOnlyMemory<byte> bytes, string? name = null, CancellationToken token = default) => throw new InvalidOperationException("Novel does not upload a second source installation");
    }
}
