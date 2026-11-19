using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;
using Windows.Foundation;
using global::Windows.Graphics.Imaging;
using Windows.Storage;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;
using static MeowFilm.Windows.Tests.FilmUiTest;

namespace MeowFilm.Windows.Tests;

public static partial class FilmPlayerOverlayTests
{
    private static IEnumerable<TestCase> NativeShellCases()
    {
        yield return Test("actual Shell menus music detail queue and confirmation cover idle prepared and decoded pictures", NativeShellCoverage);
        yield return Test("actual presented frames survive continuous Shell resize movement and viewport scrolling", NativeShellLayout);
        yield return Test("actual decoded picture and controls retain one owner through full screen and PiP", NativeShellWindows);
    }

    private static async Task NativeShellCoverage()
    {
        await using var scene = await NativeShellScene.Create();
        await Cover("idle", output: false);
        await scene.Prepare();
        Check(scene.Media.Prepared.Task.IsCompletedSuccessfully && !scene.Session.HasOutput &&
            NativeMediaFixtures.Field(scene.Fixture.Native, "pending") is not null,
            "the preparation scene contains a real decoded silent candidate, not a changed stage or surface probe");
        await Cover("prepared", output: false);
        await scene.Play();
        await Cover("picture", output: true);

        async Task Cover(string phase, bool output)
        {
            var page = scene.Page; var directory = page.EpisodeDirectory;
            var presentation = directory?.Presentation;
            var snapshot = scene.Session.SnapshotFor(page)!;
            var scope = scene.Fixture.Native.Scope;
            var pending = NativeMediaFixtures.Field(scene.Fixture.Native, "pending");
            var overlay = scene.Element<Grid>("PlayerOverlay");
            var scroll = scene.Element<ScrollViewer>("PageScroll");
            double offset = scroll.VerticalOffset;
            int calls = scene.Fixture.Traffic.BusinessCalls;
            AssertPicture(await scene.Frame(phase + "-before"), scene, output);

            var picker = scene.Element<FilmSourcePicker>("SourcePicker");
            var items = picker.ItemsSource; var selected = picker.SelectedItem;
            picker.IsDropDownOpen = true;
            await Until(() => picker.SourcePopup is { IsOpen: true, Child: FrameworkElement { IsLoaded: true } });
            var popup = picker.SourcePopup!;
            var menu = (FrameworkElement)popup.Child;
            var footer = picker.SearchMoreSourcesButton!;
            Check(DesktopVisual.Contains(menu, footer) && !picker.Items.Contains(footer) &&
                ReferenceEquals(picker.ItemsSource, items) && ReferenceEquals(picker.SelectedItem, selected) &&
                footer.IsEnabled == page.CanSearchMore &&
                Math.Abs(menu.ActualWidth - picker.ActualWidth) <= 1,
                "the native source popup keeps its independent nonselectable continuation footer and trigger width");
            await scene.Frame(phase + "-source-menu", menu);
            var openMenuFrame = await scene.Frame(phase + "-source-picture");
            AssertPicture(openMenuFrame, scene, output, Bounds(menu, scene.Shell.Root));
            picker.IsDropDownOpen = false;
            await Until(() => !popup.IsOpen);
            Stable("closing the source menu");

            await scene.Music.OpenPlayerDetailAsync();
            var detailSurface = scene.ShellElement<Grid>("PlayerDetailSurface");
            await Until(() => Visible(detailSurface) && scene.ShellElement<ContentControl>("PlayerDetailHost").Content is MusicPlayerDetailView);
            await Until(() => Field<TranslateTransform>(scene.Shell, "PlayerDetailTransform").Y == 0);
            var detail = (MusicPlayerDetailView)scene.ShellElement<ContentControl>("PlayerDetailHost").Content;
            await Until(() => ((SolidColorBrush)((TextBlock)detail.FindName("DetailTitle")).Foreground).Color ==
                ((SolidColorBrush)detail.Foreground).Color);
            Check(ReferenceEquals(detail.DataContext, scene.Music) && ReferenceEquals(detail.XamlRoot, scene.Surface.XamlRoot),
                "the real music full-screen consumer shares the Shell composition root with Film");
            var detailInk = ((SolidColorBrush)detail.Foreground).Color;
            Check(detailInk == global::Windows.UI.Color.FromArgb(255, 235, 234, 237),
                "the normal music detail retains its existing light foreground on the dark artwork backdrop");
            Check(((SolidColorBrush)((TextBlock)detail.FindName("DetailTitle")).Foreground).Color == detailInk &&
                ((SolidColorBrush)((TextBlock)detail.FindName("LyricState")).Foreground).Color == detailInk,
                "the actual detail title and lyric state use the detail foreground above app text styles");
            foreach (string tabName in new[] { "LyricsTab", "SimilarTab" })
            {
                var tab = (RadioButton)detail.FindName(tabName);
                var label = DesktopTestUi.Visuals(tab).OfType<ContentPresenter>().Single();
                var expected = tab.IsChecked == true ? global::Windows.UI.Color.FromArgb(255, 243, 241, 244) :
                    global::Windows.UI.Color.FromArgb(255, 155, 150, 155);
                Check(((SolidColorBrush)tab.Foreground).Color == expected &&
                    ((SolidColorBrush)label.Foreground).Color == expected,
                    "the current detail tab template paints its selected or inactive palette");
            }
            var detailFooter = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
            var transportInk = global::Windows.UI.Color.FromArgb(255, 197, 195, 200);
            Check(((SolidColorBrush)((Button)detailFooter.FindName("ModeButton")).Foreground).Color == transportInk &&
                ((SolidColorBrush)((SkipButton)detailFooter.FindName("PreviousButton")).Foreground).Color == transportInk &&
                ((SolidColorBrush)((StackPanel)detailFooter.FindName("MetadataText")).Children.OfType<TextBlock>().First().Foreground).Color == detailInk,
                "the current detail footer palette distinguishes readable metadata and transport controls");
            var musicFrame = await scene.Frame(phase + "-music-detail");
            AssertCovered(musicFrame, Intersect(Bounds(detailSurface, scene.Shell.Root), scene.PictureBounds),
                "music full screen covers the actual picture and its control area");
            Check(Visible(scene.Surface), "ordinary music coverage does not hide or unmount the film surface");
            Click(scene.ShellElement<Button>("PlayerDetailBackButton"));
            await Until(() => !Visible(detailSurface));
            Stable("closing real music full screen");
            AssertPicture(await scene.Frame(phase + "-detail-return"), scene, output);

            await scene.Music.OpenPlayQueueAsync();
            var queue = scene.ShellElement<Border>("QueueSurface");
            await Until(() => Visible(queue) && scene.Music.QueueSurfaceRows.Count == 2);
            await Until(() => Field<TranslateTransform>(scene.Shell, "QueueTransform").X == 0);
            Check(scene.ShellElement<ContentControl>("QueueHost").Content is MusicQueueView queueView &&
                ReferenceEquals(queueView.DataContext, scene.Music) && ReferenceEquals(queueView.XamlRoot, scene.Surface.XamlRoot),
                "the queue is the real music consumer, not a synthetic Shell cover");
            var queueFrame = await scene.Frame(phase + "-queue");
            AssertCovered(queueFrame, Intersect(scene.PictureBounds, Bounds(queue, scene.Shell.Root)),
                "the actual queue background and rows compose above the film picture");
            Check(Visible(scene.Surface), "queue coverage leaves the same film surface mounted");
            Click(scene.ShellElement<Button>("QueueDismissSurface"));
            await Until(() => !Visible(queue));
            Stable("closing the real queue");
            AssertPicture(await scene.Frame(phase + "-queue-return"), scene, output);

            scene.Shell.Close();
            var confirmation = scene.ShellElement<CloseWindowDialog>("CloseDialog");
            await Until(() => confirmation.IsOpen);
            var confirmFrame = await scene.Frame(phase + "-confirmation");
            AssertCovered(confirmFrame, Intersect(scene.PictureBounds, Bounds(Named<Border>(confirmation, "DialogSurface"), scene.Shell.Root)),
                "the actual close confirmation composes above the film in the same XAML tree");
            Check(Visible(scene.Surface) && !scene.Shell.ShutdownCompletion.IsCompleted,
                "asking for close preserves the actual media and page owners");
            Click(Named<Button>(confirmation, "CancelChoice"));
            await Until(() => !confirmation.IsOpen);
            Stable("cancelling close confirmation");
            AssertPicture(await scene.Frame(phase + "-confirmation-return"), scene, output);

            void Stable(string operation)
            {
                var current = scene.Session.SnapshotFor(page)!;
                Check(ReferenceEquals(scene.Film.Playback, page) && ReferenceEquals(page.EpisodeDirectory, directory) &&
                    ReferenceEquals(directory?.Presentation, presentation), operation + " retains the same page directory and presentation");
                Check(Math.Abs(scroll.VerticalOffset - offset) < 1,
                    operation + $" retains the page position: before={offset}; after={scroll.VerticalOffset}");
                Check(ReferenceEquals(scene.Surface.Content, overlay) && ReferenceEquals(overlay.XamlRoot, scene.Shell.Root.XamlRoot),
                    operation + " retains the mounted picture and controls in the same Shell composition root");
                Check(scene.Fixture.Native.Scope == scope && ReferenceEquals(NativeMediaFixtures.Field(scene.Fixture.Native, "pending"), pending),
                    operation + $" retains the committed and pending native owners: scopeBefore={scope}; scopeAfter={scene.Fixture.Native.Scope}; pendingBefore={pending is not null}; pendingAfter={NativeMediaFixtures.Field(scene.Fixture.Native, "pending") is not null}");
                Check(current.SessionId == snapshot.SessionId && current.AttemptId == snapshot.AttemptId && current.CurrentId == snapshot.CurrentId,
                    operation + $" retains the exact business occurrence: session={current.SessionId == snapshot.SessionId}; attempt={current.AttemptId == snapshot.AttemptId}; current={current.CurrentId == snapshot.CurrentId}");
                Check(scene.Session.HasOutput == output, operation + $" preserves actual native output: expected={output}; actual={scene.Session.HasOutput}");
                Check(scene.Fixture.Traffic.BusinessCalls == calls,
                    operation + $" performs no Film RPC: before={calls}; after={scene.Fixture.Traffic.BusinessCalls}; progress={scene.Fixture.Traffic.Commands("media.progress").Length}");
            }
        }
    }

    private static async Task NativeShellLayout()
    {
        await using var scene = await NativeShellScene.Create(); await scene.Prepare(); await scene.Play(paused: false);
        var allocation = scene.Fixture.CurrentAllocation();
        var scope = scene.Fixture.Native.Scope; var overlay = scene.Element<Grid>("PlayerOverlay");
        var directory = scene.Page.EpisodeDirectory; var presentation = directory?.Presentation;
        var swapchain = Field<nint>(scene.Surface, "swapchain");
        ulong pictures = scene.Fixture.Native.Output!.SubmittedPictures;
        int reads = scene.ReadCommands;
        int unloads = 0; overlay.Unloaded += (_, _) => unloads++;
        for (int frame = 0; frame < 24; frame++)
        {
            // Adjacent sizes, not two settled endpoints. Capture each actual desktop/compositor frame.
            scene.Shell.Resize(1060 + (frame < 12 ? frame : 23 - frame) * 12, 800 + frame % 3 * 8);
            DesktopTestUi.MoveWindow(scene.Shell, 40 + frame % 4 * 7, 40 + frame % 3 * 5);
            var scroll = scene.Element<ScrollViewer>("PageScroll");
            scroll.ChangeView(null, frame < 12 ? frame * 5 : (23 - frame) * 5, null, true);
            var capture = await scene.Frame("layout-" + frame.ToString("D2"));
            var picture = scene.Element<Grid>("VideoPicture");
            Check(Math.Abs(picture.ActualHeight - picture.ActualWidth * 9 / 16) < 1,
                "each presented layout frame measures the picture at sixteen-to-nine");
            AssertPicture(capture, scene, output: true);
            Check(scene.Fixture.CurrentAllocation().Matches(allocation) && scene.Fixture.Native.Scope == scope &&
                Field<nint>(scene.Surface, "swapchain") == swapchain && unloads == 0 &&
                ReferenceEquals(scene.Surface.Content, overlay) && ReferenceEquals(scene.Page.EpisodeDirectory, directory) &&
                ReferenceEquals(directory?.Presentation, presentation) && scene.ReadCommands == reads,
                "each resize/scroll frame keeps the actual native allocation COM identity controls and successful directory");
            AssertViewport(capture, scene);
        }
        Check(scene.Fixture.Native.Output!.SubmittedPictures > pictures && allocation.IsAlive,
            "continuous layout evidence includes advancing decoded/Present frames, not a static native placeholder");
    }

    private static async Task NativeShellWindows()
    {
        await using var scene = await NativeShellScene.Create(); await scene.Prepare(); await scene.Play();
        var allocation = scene.Fixture.CurrentAllocation(); var scope = scene.Fixture.Native.Scope;
        var frame = scene.Element<Grid>("PlayerFrame"); var overlay = scene.Element<Grid>("PlayerOverlay");
        var directory = scene.Page.EpisodeDirectory; int calls = scene.Fixture.Traffic.BusinessCalls;
        double offset = scene.Element<ScrollViewer>("PageScroll").VerticalOffset;
        for (int round = 0; round < 2; round++)
            foreach (string action in new[] { "FullscreenButton", "PipButton" })
            {
                Click(scene.Element<Button>(action));
                await Until(() => scene.PlayerWindow is { IsVisible: true } && overlay.IsLoaded);
                var window = scene.PlayerWindow!;
                Check(ReferenceEquals(overlay.XamlRoot, window.Root.XamlRoot) && DesktopVisual.Contains(window.Root, frame) &&
                    ReferenceEquals(scene.Surface.Content, overlay) && scene.Session.CanRetainMedia == (action == "PipButton"),
                    "the actual picture and its controls move together and only explicit PiP retains media");
                var capture = await scene.Frame(action + "-" + round, window.Root);
                AssertVideoPixels(capture, Bounds(scene.Element<Grid>("VideoPicture"), window.Root),
                    "full screen/PiP actually presents the decoded blue fixture");
                window.Resize(action == "PipButton" ? 460 : 1060, action == "PipButton" ? 280 : 800);
                capture = await scene.Frame(action + "-resized-" + round, window.Root);
                AssertVideoPixels(capture, Bounds(scene.Element<Grid>("VideoPicture"), window.Root),
                    "resized full screen/PiP presents pixels, not just a terminal owner state");
                window.Close();
                await Until(() => scene.PlayerWindow is null && frame.IsLoaded && overlay.IsLoaded && scene.Surface.IsLoaded &&
                    ReferenceEquals(overlay.XamlRoot, scene.Shell.Root.XamlRoot) && scene.Surface.Describe().Visible == 1);
                DesktopTestUi.WindowRoot(scene.Shell).UpdateLayout();
                await Until(() => !Field<bool>(scene.Fixture.Native, "updatingSurface") &&
                    NativeMediaFixtures.Field(scene.Fixture.Native, "presentedSurface") is FilmKernelNative.Surface presented &&
                    presented.Equals(scene.Surface.Describe()));
                AssertPicture(await scene.Frame(action + "-returned-" + round), scene, output: true);
                Check(Math.Abs(scene.Element<ScrollViewer>("PageScroll").VerticalOffset - offset) < 1,
                    "returning from the player window preserves the page viewport with its measured picture slot");
                Check(scene.Fixture.CurrentAllocation().Matches(allocation) && scene.Fixture.Native.Scope == scope &&
                    ReferenceEquals(scene.Page.EpisodeDirectory, directory) && !scene.Session.CanRetainMedia &&
                    scene.Fixture.Traffic.BusinessCalls == calls,
                    "returning to the actual Shell keeps the same decoded owner and directory without reopening or replaying RPC");
            }
    }

    private static Rect Intersect(Rect left, Rect right) => new(Math.Max(left.Left, right.Left), Math.Max(left.Top, right.Top),
        Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left)),
        Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top)));

    private static double Fraction(NativeDesktopCapture.Capture image, Rect rect, Func<byte, byte, byte, bool> accepts)
    {
        Check(rect.Width > 8 && rect.Height > 8, "the asserted desktop pixel area must actually be on screen");
        int matched = 0;
        for (int y = 1; y <= 9; y++)
            for (int x = 1; x <= 9; x++)
            {
                int column = (int)((rect.Left + rect.Width * x / 10) * image.Scale);
                int row = (int)((rect.Top + rect.Height * y / 10) * image.Scale);
                Check(column >= 0 && column < image.Width && row >= 0 && row < image.Height,
                    "the actual captured pixel is inside the displayed target");
                int at = (row * image.Width + column) * 4;
                if (accepts(image.Pixels[at + 2], image.Pixels[at + 1], image.Pixels[at])) matched++;
            }
        return matched / 81d;
    }
    private static bool Blue(byte red, byte green, byte blue) => blue > 75 && blue > green + 45 && blue > red + 10;
    private static Rect Interior(Rect rect) => new(rect.X + 8, rect.Y + 8, rect.Width - 16, rect.Height - 16);
    private static Rect PicturePatch(Rect rect) => new(rect.X + rect.Width * .12, rect.Y + rect.Height * .12,
        rect.Width * .24, rect.Height * .16);
    private static void AssertVideoPixels(NativeDesktopCapture.Capture image, Rect rect, string message) =>
        Check(Fraction(image, PicturePatch(rect), Blue) > .95, message + "; no sampled grey/white/black clear frame");
    private static void AssertCovered(NativeDesktopCapture.Capture image, Rect rect, string message) =>
        Check(Fraction(image, Interior(rect), Blue) < .02, message + "; no decoded picture leaks through");
    private static void AssertPicture(NativeDesktopCapture.Capture image, NativeShellScene scene, bool output, Rect? popup = null)
    {
        var patch = PicturePatch(scene.PictureBounds);
        if (popup is { } menu) Check(Intersect(patch, menu).Width == 0 || Intersect(patch, menu).Height == 0,
            "the retained-picture sample must be outside the source popup");
        if (output)
        {
            var actual = scene.Surface.Describe();
            var presented = NativeMediaFixtures.Field(scene.Fixture.Native, "presentedSurface") as FilmKernelNative.Surface?;
            AssertVideoPixels(image, scene.PictureBounds, "the actual Shell still displays the accepted decoded picture; " +
                $"actual={actual.Width}x{actual.Height}/{actual.Visible}, " +
                $"presented={presented?.Width}x{presented?.Height}/{presented?.Visible}, " +
                $"sameWindow={actual.DisplayWindow == presented?.DisplayWindow}, " +
                $"updating={Field<bool>(scene.Fixture.Native, "updatingSurface")}, " +
                $"pictures={scene.Fixture.Native.Output?.SubmittedPictures}, picture={scene.PictureBounds}");
        }
        else Check(Fraction(image, patch, (red, green, blue) => red < 12 && green < 12 && blue < 12) > .95,
            "idle and silent preparation keep the actual black picture canvas without grey/white clearing");
    }
    private static void AssertViewport(NativeDesktopCapture.Capture image, NativeShellScene scene)
    {
        var picture = Bounds(scene.Element<Grid>("VideoPicture"), scene.Shell.Root);
        var viewport = Bounds(scene.Element<ScrollViewer>("PageScroll"), scene.Shell.Root);
        if (picture.Top >= viewport.Top) return;
        var above = new Rect(picture.Left + 24, viewport.Top - 10, Math.Min(120, picture.Width - 48), 9);
        Check(Fraction(image, above, Blue) < .02, "native pixels are clipped by the real page viewport rather than copied regions");
    }

    private sealed class NativeShellScene : IAsyncDisposable
    {
        internal readonly FilmPlaybackNativeFixture Fixture;
        internal readonly PreparedFilmMedia Media;
        internal readonly FilmPlaybackSession Session;
        internal readonly FilmCapability Film;
        internal readonly MusicCapability Music;
        private readonly NativeAudioPlayback audio = new();
        private readonly NativeTestDatabase store = new();
        private readonly NativeMusicScriptTestRuntime musicRuntime = new();
        private readonly MusicNativeApplication musicApplication;
        private readonly AppClosePreferences closePreferences;
        private ShellWindow? shell;
        private Grid? frame;
        private Guid sessionId;
        internal ShellWindow Shell => shell!;
        internal FilmPlaybackPage Page => Film.Playback!;
        internal FilmPlaybackView View => Visuals(Film.View).OfType<FilmPlaybackView>().Single();
        internal FilmVideoSurface Surface => (FilmVideoSurface)Fixture.Native.VideoView;
        internal Rect PictureBounds => Intersect(Bounds(Element<Grid>("VideoPicture"), DesktopTestUi.WindowRoot(Shell)),
            Bounds(Element<ScrollViewer>("PageScroll"), DesktopTestUi.WindowRoot(Shell)));
        internal DesktopWindow? PlayerWindow => frame is not null && DesktopWindow.GetWindow(frame) is { } window &&
            !ReferenceEquals(window, Shell) ? window : null;
        internal int ReadCommands => new[] { "browse", "browse.metadata", "browse.season", "browse.refresh", "prepare" }
            .Sum(action => Fixture.Traffic.Commands(action).Length);
        private NativeShellScene(FilmPlaybackNativeFixture fixture)
        {
            Fixture = fixture; Media = new(new FilmNativeMedia(fixture.Native));
            Session = new(async token => (await fixture.Runtime.CaptureConnectionAsync(token)) with { Client = fixture.Traffic }, Media);
            Film = new(new EmptyHome(), Session, new FilmLibrary(new FilmMemoryStore()));
            musicApplication = new(store, MusicTestStores.For(store), musicRuntime);
            Music = new(musicApplication, store, audio);
            closePreferences = new(store);
        }
        internal static async Task<NativeShellScene> Create()
        {
            Check(TestDesktopContext.Verify(requireTarget: true), "native Shell cases require the authorized interactive desktop");
            var fixture = await FilmPlaybackNativeFixture.CreateAsync(longVideo: true);
            var scene = new NativeShellScene(fixture);
            try
            {
                // The shared fixture supplies the actual aggregate/Cat/kernel only. Its ordinary
                // window is unmounted and hidden; all acceptance below uses the production Shell.
                fixture.Window.Content = null; fixture.Window.AppWindow.Hide();
                await scene.closePreferences.LoadAsync();
                await scene.Music.InitializeAsync(default);
                var songs = new[] { MusicSongFixture.Local("shell-cover-one", "覆盖测试曲目一"),
                    MusicSongFixture.Local("shell-cover-two", "覆盖测试曲目二") };
                var added = await scene.musicApplication.ExecuteAsync("music.queue.append", new { songs });
                Check(FilmJson.Bool(added, "ok"), "the real native music queue accepts its local synthetic fixture rows");
                await scene.Music.NavigateAsync("favorites", default);
                scene.shell = new([scene.Music, scene.Film], [], async () =>
                {
                    await scene.Film.DisposeAsync(); await scene.Music.DisposeAsync();
                }, () => Task.CompletedTask, closePreferences: scene.closePreferences);
                scene.Shell.Resize(1120, 820); DesktopTestUi.MoveWindow(scene.Shell, 40, 40);
                ((Microsoft.UI.Windowing.OverlappedPresenter)scene.Shell.AppWindow.Presenter).IsAlwaysOnTop = true;
                scene.Shell.Activate();
                await Until(() => scene.Music.PlayerView.IsLoaded && scene.Shell.Root.IsLoaded);
                var modules = scene.ShellElement<ListView>("ModuleNavigationList");
                var filmModule = modules.Items.OfType<ModuleSession>().Single(item => item.Module == scene.Film);
                modules.ScrollIntoView(filmModule);
                await Until(() => modules.ContainerFromItem(filmModule) is ListViewItem { IsLoaded: true, IsEnabled: true } item &&
                    Visible(item) && item.ActualWidth > 0);
                var navigation = FrameworkElementAutomationPeer.CreatePeerForElement(modules) ?? new ListViewAutomationPeer(modules);
                var row = navigation.GetChildren().Single(peer => peer.GetName() == scene.Film.Title);
                var selection = row.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider ??
                    throw new InvalidOperationException("The mounted Film navigation row has no selection automation pattern.");
                selection.Select();
                await Until(() => scene.Film.View.IsLoaded && scene.Film.IsHome);
                Check(ReferenceEquals(modules.SelectedItem, filmModule) && Visible(scene.Film.View),
                    "the actual Film navigation selection activates its visible home route");
                Check(scene.Music.PlayerView.IsLoaded, "entering Film retains the music player that owns the global surfaces");
                var reply = await fixture.Runtime.CallAsync("cat.script.status", new { });
                Check(FilmJson.Bool(reply, "ok"), "read source declarations from the actual installed Cat owner");
                var instance = FilmJson.Array(FilmJson.Member(reply, "value"), "instances").Single(row =>
                    FilmJson.Text(row, "installationId") == FilmPlaybackNativeFixture.Installation);
                Check(FilmJson.Integer(instance, "generation") == fixture.Generation && FilmJson.Text(instance, "state") == "ready",
                    "the Shell input belongs to the exact ready source generation");
                var sources = FilmJson.Array(instance, "sites").Select(site =>
                {
                    string id = FilmJson.RequiredText(site, "key");
                    return FilmJson.Encode(new { key = FilmPlaybackNativeFixture.SourceKey(id), engine = "cat",
                        installationId = FilmPlaybackNativeFixture.Installation, siteId = id, generation = fixture.Generation,
                        name = FilmJson.RequiredText(site, "name") });
                }).ToArray();
                var target = new FilmPlaybackTarget("film:shell-native-fixture", "movie", FilmPlaybackNativeFixture.Title,
                    MetadataProvider: "tmdb", MetadataId: "1");
                var request = new FilmPlaybackRequest(target, sources);
                await scene.Film.OpenPlaybackAsync(new(target.Id, target.Title, "", "") { PlaybackContext = request }, request);
                scene.sessionId = fixture.Traffic.Opened(target.Id);
                await Until(() => scene.Film.IsPlayback && scene.View.IsLoaded && scene.Surface.IsLoaded &&
                    !scene.Page.ListLoading && scene.Page.Sources.Count > 0);
                var admitted = await scene.Media.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var snapshot = scene.Session.SnapshotFor(scene.Page)!;
                Check(scene.Page.CanPlay && snapshot.AttemptId == admitted.Identity &&
                    !FilmJson.Bool(snapshot.Attempt, "prepareOnly") && !scene.Session.HasOutput &&
                    NativeMediaFixtures.Field(fixture.Native, "pending") is null,
                    "the real Windows automatic attempt is admitted before the idle scene, without starting native preparation");
                scene.frame = Named<Grid>(scene.View, "PlayerFrame");
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal T Element<T>(string name) where T : FrameworkElement => View.FindName(name) as T ??
            (frame is null ? null : Visuals(frame).OfType<T>().FirstOrDefault(element => element.Name == name)) ?? Named<T>(View, name);
        internal T ShellElement<T>(string name) where T : FrameworkElement => Named<T>(DesktopTestUi.WindowRoot(Shell), name);
        internal async Task Prepare()
        {
            Media.BeginPreparation();
            await Media.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        internal async Task Play(bool paused = true)
        {
            Media.Release();
            var owner = new FilmPlaybackNativeFixture.PageOwner(Session, Page,
                sessionId, Page.EntryTarget.Id);
            await Fixture.OutputAsync(owner, pauseAfterOutput: paused);
        }
        internal async Task<NativeDesktopCapture.Capture> Frame(string name, FrameworkElement? target = null)
        {
            target ??= DesktopTestUi.WindowRoot(Shell);
            var window = DesktopWindow.GetWindow(target) ?? throw new InvalidOperationException("The captured tree has no window owner.");
            if (!ReferenceEquals(window, Shell))
                ((Microsoft.UI.Windowing.OverlappedPresenter)Shell.AppWindow.Presenter).IsAlwaysOnTop = false;
            Check(SetWindowPos(window.Handle, -1, 0, 0, 0, 0, 0x13),
                "the test-owned picture window is visible above unrelated desktop windows");
            var image = await NativeDesktopCapture.CaptureAsync(target);
            string folder = TestWorkspace.DataPath("desktop-shots"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "film-shell-" + name + ".png");
            using (File.Create(path)) { }
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height,
                96 * image.Scale, 96 * image.Scale, image.Pixels);
            await encoder.FlushAsync();
            Console.WriteLine($"FILM_SHELL_FRAME {path}; submittedPictures={Fixture.Native.Output?.SubmittedPictures ?? 0}; method=actual-desktop; SDR-composition-only");
            return image;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        public async ValueTask DisposeAsync()
        {
            Media.Release();
            try
            {
                PlayerWindow?.Close();
                if (shell is not null)
                {
                    var dialog = ShellElement<CloseWindowDialog>("CloseDialog");
                    if (!dialog.IsOpen) Shell.Close();
                    await Until(() => dialog.IsOpen || Shell.ShutdownCompletion.IsCompleted);
                    if (dialog.IsOpen) Click(Named<Button>(dialog, "ExitChoice"));
                    await Shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
                }
                else { await Film.DisposeAsync(); await Music.DisposeAsync(); }
            }
            finally
            {
                try { await Fixture.DisposeAsync(); }
                finally
                {
                    await audio.DisposeAsync(); await musicRuntime.DisposeAsync();
                    await closePreferences.DisposeAsync(); await store.DisposeAsync();
                }
            }
        }
    }

    // Scheduling gates before and after the production native prepare.
    // All decoding, first-frame work, output, controls, cancellation and retirement stay real.
    private sealed class PreparedFilmMedia(IFilmNativeMedia inner) : IFilmNativeMedia
    {
        internal readonly TaskCompletionSource<RuntimeMediaPlaybackIntent> Admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<RuntimeMediaPlaybackIntent> Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource prepare = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void BeginPreparation() => prepare.TrySetResult();
        internal void Release() { prepare.TrySetResult(); release.TrySetResult(); }
        public FrameworkElement? View => inner.View;
        public NativePlaybackScope? Scope => inner.Scope;
        public string? Identity => inner.Identity;
        public bool HasOutput => inner.HasOutput;
        public bool Buffering => inner.Buffering;
        public string? FailureCode => inner.FailureCode;
        public bool Paused => inner.Paused;
        public double Position => inner.Position;
        public double Duration => inner.Duration;
        public int Volume { get => inner.Volume; set => inner.Volume = value; }
        public event PropertyChangedEventHandler? PropertyChanged { add => inner.PropertyChanged += value; remove => inner.PropertyChanged -= value; }
        public event EventHandler? Failed { add => inner.Failed += value; remove => inner.Failed -= value; }
        public event EventHandler? Ended { add => inner.Ended += value; remove => inner.Ended -= value; }
        public async Task PrepareAsync(RuntimeMediaPlaybackIntent intent, FilmMediaReceipt receipt, INativeMediaResourceLease? lease, CancellationToken token)
        {
            Admitted.TrySetResult(intent); await prepare.Task.WaitAsync(token);
            await inner.PrepareAsync(intent, receipt, lease, token);
            Prepared.TrySetResult(intent); await release.Task.WaitAsync(token);
        }
        public Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent intent, Func<bool> canCommit, Action committed, CancellationToken token) =>
            inner.TryCommitAsync(intent, canCommit, committed, token);
        public Task DiscardAsync(RuntimeMediaPlaybackIntent intent) => inner.DiscardAsync(intent);
        public Task PauseAsync(NativePlaybackScope scope, bool paused) => inner.PauseAsync(scope, paused);
        public Task SeekAsync(NativePlaybackScope scope, double position) => inner.SeekAsync(scope, position);
        public Task RateAsync(NativePlaybackScope scope, double rate) => inner.RateAsync(scope, rate);
        public Task AspectAsync(NativePlaybackScope scope, string? aspect) => inner.AspectAsync(scope, aspect);
        public Task StopAsync(NativePlaybackScope scope) => inner.StopAsync(scope);
    }

    private sealed class EmptyHome : IFilmHomeSource
    {
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FilmCardItem>>([]);
        public void Dispose() { }
    }
}
