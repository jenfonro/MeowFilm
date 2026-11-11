using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private sealed class RuleProbe : IRuntimeClient, IAsyncDisposable
    {
        private readonly NovelRuleRuntimeFixture runtime = new();
        internal int Transforms;
        internal Func<Task>? AfterTransform;
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            bool transform = NovelJson.Value(parameters!).GetProperty("request").GetProperty("operation").GetString() == "transform";
            if (transform) Transforms++;
            var result = await runtime.CallAsync(method, parameters, token);
            if (transform && AfterTransform is { } after) await after();
            return result;
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure rules never upload source.");
        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }
    private static async Task RetainedProcessing()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store);
        var runtime = new Runtime { Text = "广告漢語" }; var sources = new NovelSourceService(runtime);
        await using var engine = new RuleProbe();
        await using var module = new NovelCapability(sources, library, new NovelRuleProgram(engine));
        await module.InitializeAsync(default);
        var rule = new NovelRule { Pattern = "广告", Replacement = "初", IsRegex = false };
        await library.SaveRulesAsync([rule]);
        var aBook = Book(url: "https://novel.invalid/a") with { Name = "甲书" };
        var bBook = Book(url: "https://novel.invalid/b") with { Name = "乙书" };
        var a = await Open(aBook, .3); string aLocation = module.CaptureNavigationLocation();
        var b = await Open(bBook, .6);
        string aId = a.Entry.BookId, bId = b.Entry.BookId;
        var aAnchor = a.Entry.Anchor; var bAnchor = b.Entry.Anchor; var aChapter = a.Chapter; var bChapter = b.Chapter;
        Check(aId != bId && a.Blocks.Single().Text == "初漢語" && b.Blocks.Single().Text == "初漢語");
        int reads = runtime.Calls.Count(c => IsAction(c, "content"));
        await library.SaveRulesAsync([rule with { Replacement = "新" }]);
        await library.SaveAppearanceAsync(library.Appearance with { Conversion = "simplified" });
        await module.RestoreNavigationAsync(aLocation, default);
        Check(ReferenceEquals(module.Session, a) && a.Blocks.Single().Text == "新汉语" && b.Blocks.Single().Text == "初漢語");
        Check(a.Entry.BookId == aId && a.Entry.Anchor == aAnchor && ReferenceEquals(a.Chapter, aChapter));
        await module.ReceiveBookAsync(bBook, default); await module.NavigateAsync(NovelCapability.BookRoute, default);
        await module.StartReadingAsync(); await Until(() => module.Route == NovelCapability.ReaderRoute);
        Check(ReferenceEquals(module.Session, b) && b.Blocks.Single().Text == "新汉语" &&
            b.Entry.BookId == bId && b.Entry.Anchor == bAnchor && ReferenceEquals(b.Chapter, bChapter));
        int processed = engine.Transforms;
        await library.SaveAppearanceAsync(library.Appearance with { FontSize = 25 });
        await library.SaveRulesAsync(library.Rules.Select(item => item with { }).ToArray());
        await b.ContinueAsync(); await module.NavigateAsync(NovelCapability.ReaderRoute, default);
        await module.RestoreNavigationAsync(aLocation, default);
        await module.NavigateAsync(NovelCapability.BookRoute, default); await module.StartReadingAsync();
        await Until(() => module.Route == NovelCapability.ReaderRoute);
        Check(engine.Transforms == processed, "Unchanged cleanup/conversion reprocessed retained raw content");
        await library.SaveAppearanceAsync(library.Appearance with { Conversion = "traditional" });
        await module.NavigateAsync(NovelCapability.ReaderRoute, default);
        Check(a.Blocks.Single().Text == "新漢語" && a.Entry.BookId == aId && a.Entry.Anchor == aAnchor);
        await library.SaveRulesAsync([new() { Pattern = "[", IsRegex = true }]);
        var oldB = b.Blocks;
        try
        {
            await module.RestoreNavigationAsync(NovelCapability.ReaderRoute + ":" + bId, default);
            throw new InvalidOperationException("Failed retained target replaced the current reader");
        }
        catch (BridgeException error) { Check(error.Code == "INVALID_REQUEST", "Invalid retained cleanup must report the rules RPC's invalid-request contract."); }
        Check(ReferenceEquals(module.Session, a) && module.CaptureNavigationLocation() == aLocation &&
            ReferenceEquals(b.Blocks, oldB) && b.Entry.Anchor == bAnchor);
        Check(runtime.Calls.Count(c => IsAction(c, "content")) == reads, "Restoring retained content downloaded it again");

        async Task<NovelBookSession> Open(NovelBook book, double ratio)
        {
            await module.ReceiveBookAsync(book, default); await module.NavigateAsync(NovelCapability.BookRoute, default);
            var session = module.Session!; await session.ContinueAsync();
            await session.SaveProgressAsync(new("chapter-1", "第一章", 0, 0, 1, ratio));
            await module.NavigateAsync(NovelCapability.ReaderRoute, default); return session;
        }
    }
    private static async Task ReprocessingAtomic()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var runtime = new Runtime { Text = "广告漢語" }; var sources = new NovelSourceService(runtime);
        await using var engine = new RuleProbe(); await using var rules = new NovelRuleProgram(engine);
        var rule = new NovelRule { Pattern = "广告", Replacement = "旧", IsRegex = false };
        await library.SaveRulesAsync([rule]);
        var session = new NovelBookSession(sources, library, rules, new("retained-atomic", "key", Book()));
        try
        {
            await session.ContinueAsync(); await session.SaveProgressAsync(new("chapter-1", "第一章", 0, 0, 1, .4));
            var old = session.Blocks; var oldEntry = session.Entry; string oldTitle = session.ContentTitle;
            await library.SaveRulesAsync([rule with { Replacement = "部分" },
                new() { Pattern = "[", Replacement = "", IsRegex = true, Order = 1 }]);
            try { await session.ContinueAsync(); throw new InvalidOperationException("Failed cleanup batch committed"); }
            catch (BridgeException error) { Check(error.Code == "INVALID_REQUEST", "Invalid cleanup must report the rules RPC's invalid-request contract."); }
            Check(ReferenceEquals(old, session.Blocks) && session.Entry == oldEntry && session.ContentTitle == oldTitle);

            await library.SaveRulesAsync([rule with { Replacement = "取消" }]);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.AfterTransform = async () => { engine.AfterTransform = null; entered.TrySetResult(); await release.Task; };
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = session.ContinueAsync(cancellation.Token);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                    Check(!pending.IsCompleted && ReferenceEquals(old, session.Blocks) && session.Entry == oldEntry);
                }
                finally { release.TrySetResult(); }
                try { await pending; throw new InvalidOperationException("Cancelled cleanup batch committed"); }
                catch (OperationCanceledException) { }
            }
            Check(ReferenceEquals(old, session.Blocks) && session.Entry == oldEntry);
            await session.ReprocessAsync();
            Check(session.Blocks.Single().Text == "取消漢語" && session.Entry.Anchor == oldEntry.Anchor);

            await library.SaveRulesAsync([rule with { Replacement = "过时" }]);
            await library.SaveAppearanceAsync(library.Appearance with { Conversion = "simplified" });
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.AfterTransform = async () => { engine.AfterTransform = null; entered.TrySetResult(); await release.Task; };
            var observed = new List<string>(); session.Changed += Observe;
            var processing = session.ReprocessAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await library.SaveRulesAsync([rule with { Replacement = "最新" }]);
                await library.SaveAppearanceAsync(library.Appearance with { Conversion = "traditional" });
                Check(session.Blocks.Single().Text == "取消漢語");
            }
            finally { release.TrySetResult(); }
            await processing; session.Changed -= Observe;
            Check(session.Blocks.Single().Text == "最新漢語" && observed.All(text => !text.Contains("过时")));
            Check(session.Entry.BookId == oldEntry.BookId && session.Entry.Anchor == oldEntry.Anchor &&
                runtime.Calls.Count(c => IsAction(c, "content")) == 1);
            var committed = session.Blocks; var committedEntry = session.Entry;
            await library.SaveRulesAsync([rule with { Replacement = "退休" }]);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.AfterTransform = async () => { engine.AfterTransform = null; entered.TrySetResult(); await release.Task; };
            var retiring = session.ReprocessAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await sources.UpdateAsync(sources.Sources.Single(), new { enabled = false });
            }
            finally { release.TrySetResult(); }
            try { await retiring; throw new InvalidOperationException("Retired source cleanup committed"); }
            catch (OperationCanceledException) { }
            Check(ReferenceEquals(session.Blocks, committed) && session.Entry == committedEntry);
            void Observe(object? sender, EventArgs e) => observed.Add(session.Blocks.Single().Text);
        }
        finally { await session.PrepareShutdownAsync(); await sources.PrepareShutdownAsync(); }
    }
    private static async Task SettingsReturnProcessing()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Text = "广告漢語" };
        var sources = new NovelSourceService(runtime); await using var engine = new RuleProbe();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(engine));
        var shell = new ShellWindow([module], [], () => module.PrepareShutdownAsync(), () => module.AbortAsync(), module.CancelShutdownPreparationAsync)
        { Width = 1050, Height = 760, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            shell.Show(); await Until(() => module.ExploreSource is not null && !module.LoadingDiscovery);
            await module.ReceiveBookAsync(Book(), default); module.Request(NovelCapability.BookRoute);
            await Until(() => module.Route == NovelCapability.BookRoute && module.Session!.Chapters.Count > 0);
            await module.StartReadingAsync(); await Until(() => module.Route == NovelCapability.ReaderRoute && module.View.IsVisible);
            var session = module.Session!; string location = module.CaptureNavigationLocation(); var chapter = session.Chapter;
            int reads = runtime.Calls.Count(c => IsAction(c, "content"));
            await OpenSettings();
            await module.Library.SaveRulesAsync([new() { Pattern = "[", IsRegex = true }]);
            await module.Library.SaveAppearanceAsync(module.Library.Appearance with { Conversion = "simplified" });
            Back(); await Until(() => module.View.IsVisible && !session.Loading && module.Notice.Contains("失败"));
            Check(session.Blocks.Single().Text == "广告漢語" && module.CaptureNavigationLocation() == location);
            await OpenSettings();
            await module.Library.SaveRulesAsync([new() { Pattern = "广告", Replacement = "返回", IsRegex = false }]);
            Back(); await Until(() => !session.Loading && session.Blocks.Single().Text == "返回汉语");
            Check(ReferenceEquals(module.Session, session) && ReferenceEquals(session.Chapter, chapter) &&
                module.CaptureNavigationLocation() == location && runtime.Calls.Count(c => IsAction(c, "content")) == reads);
            int processed = engine.Transforms;
            await OpenSettings(); Back(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(module.View.IsVisible && engine.Transforms == processed, "Unchanged settings return reprocessed content");
        }
        finally { shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10)); }
        async Task OpenSettings()
        {
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !module.View.IsVisible);
            var header = (DesktopPageHeader)shell.FindName("PageHeaderHost");
            var slot = (ContentControl)header.FindName("LeadingSlot");
            ((Panel)slot.Content).Children.OfType<RadioButton>().Single(tab => tab.Content as string == "小说").IsChecked = true;
            await Until(() => module.SettingsView.IsVisible);
        }
        void Back() => ((Button)shell.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static async Task LayoutLabels()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store);
        var runtime = new Runtime { GradientImage = true, Blocks =
            [new("image", Src: "https://a.invalid/tall.png"), new("text", string.Concat(Enumerable.Repeat("字号与窗口重排后的正文。\n", 300)))],
            ImageReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously), ImageReadHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var module = new NovelCapability(new NovelSourceService(runtime), library, new NovelRuleProgram(ruleRuntime));
        await module.InitializeAsync(default); await library.SaveAppearanceAsync(new() { Mode = "none" });
        await module.ReceiveBookAsync(Book(), default); await module.NavigateAsync(NovelCapability.BookRoute, default);
        await module.Session!.ContinueAsync();
        var host = GeneralAppearanceTests.Window(module.View); host.Width = 900; host.Height = 700; host.Show();
        try
        {
            await module.NavigateAsync(NovelCapability.ReaderRoute, default);
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            await runtime.ImageReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => library.Entries.SingleOrDefault()?.ReadAt is not null);
            var surface = Find<NovelReadingSurface>(module.View).Single();
            var label = Find<TextBlock>(module.View).Single(t => t.Text == surface.PageStatus);
            var anchor = surface.CaptureAnchor(); var savedAnchor = library.Entries.Single().Anchor;
            var readAt = library.Entries.Single().ReadAt;
            int positionChanges = 0, layouts = 0;
            surface.PositionChanged += (_, _) => positionChanges++;
            surface.LayoutCompleted += (_, _) => layouts++;

            string before = surface.PageStatus;
            await library.SaveAppearanceAsync(library.Appearance with { FontSize = 32 });
            int expectedWrites = store.Writes;
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            await Until(() => surface.PageStatus != before);
            Check(label.Text == surface.PageStatus && layouts > 0, "Font reflow did not refresh the visible page label.");
            await NoProgressSave(expectedWrites);

            before = surface.PageStatus; int previousLayouts = layouts;
            host.Width = 560; host.Height = 520; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            await Until(() => surface.PageStatus != before);
            Check(label.Text == surface.PageStatus && layouts > previousLayouts, "Window reflow left a stale page label.");
            await NoProgressSave(expectedWrites);

            before = surface.PageStatus; previousLayouts = layouts;
            runtime.ImageReadHold.TrySetResult();
            await Until(() => surface.PageStatus != before && layouts > previousLayouts);
            Check(label.Text == surface.PageStatus, "Late image dimensions left a stale page label.");
            await NoProgressSave(expectedWrites);
            Check(surface.CaptureAnchor() == anchor, "Layout-only changes altered the logical reading anchor.");
            async Task NoProgressSave(int writes)
            {
                // The view's real position-save timer is 700 ms.
                await Task.Delay(850); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(positionChanges == 0 && store.Writes == writes && library.Entries.Single().ReadAt == readAt &&
                    library.Entries.Single().Anchor == savedAnchor, "Layout notification masqueraded as a persisted position change.");
            }
        }
        finally { runtime.ImageReadHold.TrySetResult(); host.Close(); await module.PrepareShutdownAsync(); }
    }
}
