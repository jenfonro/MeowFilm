using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.App;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.SystemMedia;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmTrayPlaybackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("close prompt cancel and repeated Closing retain the actual typed Film session", CancelClose);
        foreach (bool otherModule in new[] { false, true })
            yield return Test($"tray restore preserves one occurrence but ordinary {(otherModule ? "module" : "route")} leave stops it",
                () => TrayAndLeave(otherModule));
        yield return Test("tray hiding without accepted output cancels preparation and drains its late result", PendingOutput);
        yield return Test("PiP still retains ordinary leave but explicit exit waits for actual native drain", PipExit);
        yield return Test("typed native clock and commands survive tray and exit joins output then receipt", NativeExit);
        yield return Test("failed exit preparation remains visible and cancellation keeps the typed Film owner", FailedExit);
        yield return Test("real system media settings fence disabled Film startup and queued requests across restart", SystemMediaSettings);
        yield return Test("system media re-enable publishes the current typed native Film clock", NativeSystemMediaClock);
        yield return Test("system pause and resume never start a page selection without accepted media", SystemTransport);
    }

    private static TestCase Test(string name, Func<Task> body) =>
        new("film-tray: " + name, () => MusicQualityConsumerTests.Sta(body));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task SystemTransport()
    {
        await using var scene = await Scene.Create();
        int calls = scene.Rig.Runtime.Calls.Count;
        await scene.Film.ResumePlaybackAsync();
        await scene.Film.PausePlaybackAsync();
        Check(scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Commits == 0 && !scene.Rig.Session.HasOutput,
            "system transport has no page-play fallback when no native media has been accepted");
        await scene.Play();
        var scope = scene.Rig.Native.Scope;
        scene.Rig.Runtime.Change(root =>
        {
            root["sources"] = JsonNode.Parse(FilmJson.Encode(new[]
            { new { id = "another-browsed-source", source = new { name = "另一个真实目录", key = "cat:fixture:site:1", detailId = 1 } } }).GetRawText());
            root["browsing"]!["sourceId"] = "another-browsed-source";
        });
        await Until(() => scene.Film.Playback?.SourceId == "another-browsed-source");
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        await Until(() => scene.Rig.Native.Paused);
        scene.Backend.Request(SystemPlaybackCommand.Play);
        await Until(() => !scene.Rig.Native.Paused);
        Check(scene.Rig.Native.Scope == scope && scene.Rig.Native.Commits == 1 && scene.Started == 1 &&
            !scene.Rig.Runtime.Calls.Any(call => call.Method == "film.session.command" &&
                FilmJson.Text(call.Parameters, "action") == "play"),
            "typed system commands resume only the current media after an unrelated browse selection");
    }

    private static async Task CancelClose()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        var scope = scene.Rig.Native.Scope;
        await scene.OpenChoice();
        scene.Shell.Close(); scene.Shell.Close();
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        await Until(() => scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: false });
        scene.Backend.Request(SystemPlaybackCommand.Seek, 81);
        await Until(() => scene.Backend.Last?.PositionSeconds == 81);
        Check(scene.Dialog.IsOpen && scene.Shell.IsVisible && scene.ShutdownCalls == 0 &&
            scene.Backend.Disabled == 0 && scene.Backend.Disposals == 0,
            "asking and repeated Closing preserve metadata, commands and the same actual attachment");
        Click(scene.DialogElement<Button>("CancelChoice"));
        scene.Backend.Request(SystemPlaybackCommand.Play);
        await Until(() => !scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: true });
        Check(!scene.Dialog.IsOpen && scene.Rig.Native.Scope == scope && scene.Rig.Native.Commits == 1 &&
            scene.Backend.Last is { OwnerId: "film", Identity: "tray-output", Kind: SystemPlaybackKind.Video } state &&
            state.Title == scene.Film.PlaybackMedia.Title &&
            scene.Rig.Native.Controls.All(call => call.Scope == scope) &&
            scene.Port.FallbackCalls.Count == 0 && scene.Backend.Subscribers == 1,
            "cancel keeps the original Film occurrence, typed video projection and business Play route");

        // Deliver a posted Film command only after the actual player owner has changed.
        int controls = scene.Rig.Native.Controls.Count;
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        scene.Port.PublishMusic();
        await scene.Rig.Native.TakeMusicAsync();
        await Pump();
        Check(scene.Rig.Native.Controls.Count == controls && scene.Port.FallbackCalls.Count == 0,
            "the captured old Film request cannot pause a replacement music owner");
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        await Until(() => scene.Port.FallbackCalls.Count == 1);
        Check(scene.Port.FallbackCalls.Single() == "pause" && scene.Backend.Last?.Identity == "music-output",
            "a fresh music request still uses the original native route, not a retired Film occurrence");
    }

    private static async Task TrayAndLeave(bool otherModule)
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Film.SeekPlaybackAsync(81);
        await scene.Film.PausePlaybackAsync();
        var scope = scene.Rig.Native.Scope;
        int calls = scene.Rig.Runtime.Calls.Count;
        Check(!scene.Rig.Session.CanRetainMedia, "ordinary native output has no PiP retention grant");
        await scene.HideToTray();
        Check(scene.Rig.Session.HasSession && scene.Rig.Session.HasOutput && scene.Rig.Native.Scope == scope &&
            scene.Rig.Native.Paused && scene.Rig.Native.Position == 81 && !scene.Rig.Session.CanRetainMedia &&
            scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Stops == 0 &&
            !scene.Rig.Runtime.Released.Contains("tray-output"),
            "only the exact tray hide keeps the same live attempt, scope, clock and media receipt");
        scene.Backend.Request(SystemPlaybackCommand.Play);
        await Until(() => !scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: true });
        scene.Backend.Request(SystemPlaybackCommand.Seek, 93);
        await Until(() => scene.Backend.Last?.PositionSeconds == 93);
        scene.Tray.ShowWindow();
        await Until(() => scene.Shell.IsVisible && scene.Film.View.IsVisible);
        Check(!scene.Shell.IsHiddenToTray && scene.Rig.Native.Scope == scope && scene.Rig.Native.Commits == 1 &&
            scene.Rig.Runtime.Calls.Count(call => call.Method == "film.session.open") == 1 &&
            scene.Started == 1 && scene.Backend.Disabled == 0 && scene.Backend.Disposals == 0,
            "restoration neither opens a new session nor starts another output or system attachment");
        scene.Leave(otherModule);
        await Until(() => !scene.Rig.Session.HasSession && !scene.Rig.Native.HasOutput);
        Check(scene.Rig.Native.Stops == 1 && scene.Rig.Runtime.Released.Contains("tray-output") &&
            scene.Backend.Last is { HasMedia: false } && scene.ShutdownCalls == 0,
            "ordinary route or module departure still stops and releases non-retained Film output");
    }

    private static async Task PendingOutput()
    {
        var entered = Gate(); var finish = Gate();
        await using var scene = await Scene.Create();
        CancellationToken preparation = default;
        scene.Rig.Native.Preparing = async token =>
        {
            preparation = token; entered.TrySetResult(); await finish.Task;
        };
        try
        {
            scene.Rig.Runtime.Attempt("pending-tray", Candidate("pending-tray"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!scene.Rig.Session.HasOutput, "preparation has not accepted a native output");
            await scene.HideToTray();
            await Until(() => preparation.IsCancellationRequested);
            Check(scene.Rig.Native.Commits == 0 && scene.Started == 0,
                "the tray is not a grant to keep an output-less attempt alive");
            finish.TrySetResult();
            await Until(() => !scene.Rig.Session.HasSession && scene.Rig.Runtime.Released.Contains("pending-tray"));
            scene.Tray.ShowWindow();
            await Pump();
            Check(scene.Rig.Native.Commits == 0 && scene.Started == 0 &&
                scene.Rig.Runtime.Calls.Count(call => call.Method == "film.session.open") == 1 &&
                scene.Backend.Last is { HasMedia: false },
                "a late prepared result is physically released and restoration does not revive it");
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task PipExit()
    {
        var entered = Gate(); var finish = Gate();
        await using var scene = await Scene.Create();
        try
        {
            await scene.Play();
            var view = (FilmPlaybackView)((FilmView)scene.Film.View).FindName("PlaybackView");
            view.UpdateLayout();
            var frame = (Grid)view.FindName("PlayerFrame");
            var button = (Button)view.FindName("PipButton");
            Check(button.IsVisible && button.IsEnabled, "PiP is an available control on the actual playback page");
            Click(button);
            var pip = scene.Shell.OwnedWindows.Cast<Window>().Single();
            Check(scene.Rig.Session.CanRetainMedia && frame.IsDescendantOf(pip), "PiP owns the existing native host");
            scene.Leave(otherModule: true);
            await Until(() => !scene.Film.View.IsVisible);
            Check(scene.Rig.Session.HasOutput && scene.Rig.Session.HasSession && pip.IsVisible &&
                scene.Rig.Native.Stops == 0, "ordinary module leave retains the explicit visible PiP owner");
            scene.Rig.Native.Stopping = async () => { entered.TrySetResult(); await finish.Task; };
            scene.Tray.ExitApplication();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!scene.IsClosed && !scene.Lifecycle.IsReleased && scene.Backend.Disposals == 0,
                "real exit does not use the PiP retention grant to bypass physical stop");
            finish.TrySetResult();
            await scene.WaitClosed();
            Check(scene.Shell.ShutdownSucceeded && scene.Shell.CleanupCompleted && scene.Rig.Native.Stops == 1 &&
                !pip.IsVisible && scene.Rig.Runtime.Released.Contains("tray-output") && scene.Backend.Disposals == 1,
                "explicit exit drains the same PiP output and receipt before closing owned windows");
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task NativeExit()
    {
        var outputEntered = Gate(); var outputFinish = Gate(); var receiptEntered = Gate(); var receiptFinish = Gate();
        await using var scene = await Scene.Create();
        try
        {
            await scene.Play();
            await scene.Film.SeekPlaybackAsync(81);
            await scene.Film.PausePlaybackAsync();
            var scope = scene.Rig.Native.Scope;
            Check(scene.Rig.Native.Position == 81 && scene.Rig.Native.Paused &&
                scene.Backend.Last is { PositionSeconds: 81, IsPlaying: false, Kind: SystemPlaybackKind.Video },
                "the actual App typed wrapper publishes the accepted native clock and pause state");
            await scene.HideToTray();
            scene.Backend.Request(SystemPlaybackCommand.Play);
            await Until(() => !scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: true });
            scene.Backend.Request(SystemPlaybackCommand.Seek, 93);
            await Until(() => scene.Rig.Native.Position == 93 && scene.Backend.Last?.PositionSeconds == 93);
            scene.Tray.ShowWindow();
            await Until(() => scene.Shell.IsVisible);
            Check(scene.Rig.Session.HasOutput && scene.Rig.Session.OwnsNativeMedia && scene.Rig.Native.Scope == scope &&
                scene.Rig.Native.Commits == 1 && scene.Started == 1 && scene.Rig.Native.Stops == 0 &&
                scene.Port.FallbackCalls.Count == 0,
                "typed Film commands retain the same native occurrence across tray hiding and restoration");
            await scene.HideToTray();
            scene.Rig.Native.Stopping = async () => { outputEntered.TrySetResult(); await outputFinish.Task; };
            scene.Rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.media.release")
                {
                    Check(FilmJson.Text(input, "attemptId") == "tray-output", "exit releases only the accepted attempt");
                    receiptEntered.TrySetResult(); await receiptFinish.Task;
                }
                return scene.Rig.Runtime.Handle(method, input);
            };
            var late = scene.Backend.Capture(SystemPlaybackCommand.Play);
            scene.Tray.ExitApplication();
            await outputEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scene.Shell.Close();
            Check(scene.Shell.IsVisible && !scene.IsClosed && scene.ShutdownCalls == 1 &&
                scene.Backend.Disabled == 1 && scene.Backend.Disposals == 0 && !receiptEntered.Task.IsCompleted,
                "tray exit restores an operable window and waits for native drain before releasing its receipt");
            // Shared preparation reversibly disables admission while the actual output and receipt still drain.
            outputFinish.TrySetResult();
            await receiptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!scene.Rig.Native.HasOutput && !scene.IsClosed && scene.Backend.Disabled == 1 && scene.Backend.Disposals == 0 &&
                !scene.Rig.Runtime.Released.Contains("tray-output"), "physical output stop is not a release acknowledgement");
            receiptFinish.TrySetResult();
            await scene.WaitClosed();
            int controls = scene.Rig.Native.Controls.Count;
            late(); scene.Backend.Request(SystemPlaybackCommand.Seek, 120);
            await Pump();
            await scene.Attachment.DisposeAsync();
            // Final suspension disables permanently; the backend, output and receipt are each released only once.
            Check(scene.Lifecycle.IsReleased && scene.Shell.ShutdownSucceeded && scene.Shell.CleanupCompleted &&
                scene.Rig.Native.Stops == 1 &&
                scene.Rig.Runtime.Released.Contains("tray-output") && scene.Backend.Disabled == 2 &&
                scene.Backend.Disposals == 1 && scene.Backend.Subscribers == 0 && scene.Rig.Native.Controls.Count == controls &&
                scene.Port.FallbackCalls.Count == 0 && scene.Tray.Disposals == 1 &&
                scene.Rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "tray-output") == 1,
                "successful final exit releases once; captured late system commands never reopen Film or touch music");
        }
        finally { outputFinish.TrySetResult(); receiptFinish.TrySetResult(); }
    }

    private static async Task FailedExit()
    {
        var receipt = new ShutdownReceipt();
        await using var scene = await Scene.Create(receipt);
        await scene.Play();
        await scene.HideToTray();
        scene.Tray.ExitApplication();
        await Until(() => scene.ShellElement<TextBlock>("StatusText").Text == "关闭失败");
        Check(scene.ShutdownWork is { IsFaulted: true } && !scene.Shell.ShutdownSucceeded &&
            !scene.Shell.CleanupCompleted && !scene.IsClosed && scene.Shell.IsVisible &&
            !scene.Lifecycle.IsReleased && scene.Rig.Session.HasOutput && scene.Backend.Disposals == 0,
            "a real App preparation failure never reports successful cleanup or retires the typed attachment");
        Click(scene.ShellElement<Button>("CancelShutdownButton"));
        await Until(() => scene.CancelCalls == 1 && scene.ShellElement<Button>("SettingsButton").IsEnabled &&
            scene.ShellElement<TextBlock>("StatusText").Text.Length == 0);
        scene.Backend.Request(SystemPlaybackCommand.Seek, 81);
        await Until(() => scene.Rig.Native.Position == 81);
        Check(scene.Rig.Native.Commits == 1 && scene.Rig.Native.Stops == 0 && scene.Backend.Subscribers == 1 &&
            scene.Backend.Last is { OwnerId: "film", Identity: "tray-output", PositionSeconds: 81 },
            "cancelling failed preparation preserves the same actual Film scope and typed commands");
        await scene.OpenChoice();
        Click(scene.DialogElement<Button>("CancelChoice"));
    }

    private static async Task SystemMediaSettings()
    {
        await using var scene = await Scene.Create(systemMediaEnabled: false);
        scene.Rig.Runtime.Attempt("tray-output", Candidate("tray-output"));
        await Until(() => scene.Rig.Native.SettledIdentity == "tray-output" && scene.Started == 1 &&
            scene.Rig.Session.HasOutput);
        await scene.Film.SeekPlaybackAsync(11);
        var scope = scene.Rig.Native.Scope;
        int controls = scene.Rig.Native.Controls.Count, calls = scene.Rig.Runtime.Calls.Count;
        foreach (var command in Enum.GetValues<SystemPlaybackCommand>()) scene.Backend.Request(command, 120);
        await Pump();
        Check(ReferenceEquals(scene.Shell.MediaControls, scene.MediaControls) && scene.MediaControls.IsAvailable &&
            !scene.MediaControls.Preferences.SystemMediaEnabled && scene.Backend.Last is null &&
            scene.Backend.Updates == 0 && scene.Backend.Disabled == 1 && scene.Backend.Subscribers == 1 &&
            scene.Rig.Native.Scope == scope && scene.Rig.Native.HasOutput && !scene.Rig.Native.Paused &&
            scene.Rig.Native.Position == 11 && scene.Rig.Native.Commits == 1 && scene.Rig.Native.Stops == 0 &&
            scene.Rig.Native.Controls.Count == controls && scene.Rig.Runtime.Calls.Count == calls &&
            scene.Port.FallbackCalls.Count == 0,
            "disabled startup plays Film normally without publishing metadata or admitting any system command");

        await scene.MediaControls.SaveAsync(scene.MediaControls.Preferences with { SystemMediaEnabled = true });
        Check(scene.MediaControls.Preferences.SystemMediaEnabled &&
            scene.Backend.Last is { OwnerId: "film", Identity: "tray-output", HasMedia: true, IsPlaying: true,
                PositionSeconds: 11, Kind: SystemPlaybackKind.Video, CanNext: true, CanPrevious: true } state &&
            state.Title == scene.Film.PlaybackMedia.Title && state.Title != scene.Port.State.Title &&
            state.DurationSeconds == scene.Rig.Session.Duration,
            "enabling the real setting immediately publishes the accepted Film identity, title, clock and controls");
        scene.Backend.Request(SystemPlaybackCommand.Seek, 81);
        await Until(() => scene.Rig.Native.Position == 81 && scene.Backend.Last?.PositionSeconds == 81);

        controls = scene.Rig.Native.Controls.Count; calls = scene.Rig.Runtime.Calls.Count;
        int updates = scene.Backend.Updates, disabled = scene.Backend.Disabled;
        var late = scene.Backend.Capture(SystemPlaybackCommand.Pause);
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        await scene.MediaControls.SaveAsync(scene.MediaControls.Preferences with { SystemMediaEnabled = false });
        Check(!scene.MediaControls.Preferences.SystemMediaEnabled && scene.Backend.Disabled == disabled + 1 &&
            scene.Backend.Updates == updates && scene.Rig.Native.Scope == scope && scene.Rig.Native.HasOutput &&
            !scene.Rig.Native.Paused && scene.Rig.Native.Position == 81 && scene.Rig.Native.Stops == 0,
            "switching off closes system admission synchronously while the same Film output keeps playing");
        late();
        foreach (var command in Enum.GetValues<SystemPlaybackCommand>()) scene.Backend.Request(command, 120);
        await scene.MediaControls.SaveAsync(scene.MediaControls.Preferences with { SystemMediaEnabled = true });
        await Pump();
        Check(scene.MediaControls.Preferences.SystemMediaEnabled && scene.Rig.Native.Controls.Count == controls &&
            scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Scope == scope && !scene.Rig.Native.Paused &&
            scene.Rig.Native.HasOutput && scene.Rig.Native.Position == 81 && scene.Rig.Native.Stops == 0 &&
            scene.Rig.Native.Commits == 1 && scene.Started == 1 && scene.Backend.Disposals == 0 &&
            scene.Backend.Subscribers == 1 && scene.Port.FallbackCalls.Count == 0 &&
            scene.Backend.Last is { OwnerId: "film", Identity: "tray-output", HasMedia: true, IsPlaying: true,
                PositionSeconds: 81, Kind: SystemPlaybackKind.Video },
            "re-enabling the same occurrence cannot revive the queued old request or requests captured while disabled");
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        await Until(() => scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: false });
        scene.Backend.Request(SystemPlaybackCommand.Play);
        await Until(() => !scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: true });
        Check(scene.Rig.Native.Scope == scope && scene.Rig.Native.Commits == 1 && scene.Started == 1 &&
            scene.Port.FallbackCalls.Count == 0, "fresh system requests regain the original typed Film route after re-enable");
    }

    private static async Task NativeSystemMediaClock()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Film.SeekPlaybackAsync(81);
        await scene.Film.PausePlaybackAsync();
        var scope = scene.Rig.Native.Scope;
        int updates = scene.Backend.Updates, disabled = scene.Backend.Disabled;
        Check(scene.Backend.Last is { OwnerId: "film", Identity: "tray-output", HasMedia: true, IsPlaying: false,
                PositionSeconds: 81, Kind: SystemPlaybackKind.Video } state &&
            state.Title == scene.Film.PlaybackMedia.Title && state.DurationSeconds == scene.Rig.Native.Duration &&
            scene.Rig.Native.Position == 81 && scene.Rig.Native.Paused,
            "the enabled setting exposes the current typed native Film clock");
        await scene.MediaControls.SaveAsync(scene.MediaControls.Preferences with { SystemMediaEnabled = false });
        await scene.Film.ResumePlaybackAsync();
        await scene.Film.SeekPlaybackAsync(93);
        Check(!scene.MediaControls.Preferences.SystemMediaEnabled && scene.Backend.Disabled == disabled + 1 &&
            scene.Backend.Updates == updates && scene.Rig.Native.HasOutput && !scene.Rig.Native.Paused &&
            scene.Rig.Native.Position == 93 && scene.Rig.Native.Stops == 0 && scene.Rig.Native.Scope == scope,
            "turning off system media preserves native Film playback without publishing its changing clock");
        await scene.MediaControls.SaveAsync(scene.MediaControls.Preferences with { SystemMediaEnabled = true });
        Check(scene.MediaControls.Preferences.SystemMediaEnabled && scene.Rig.Session.OwnsNativeMedia &&
            scene.Backend.Last is { OwnerId: "film", Identity: "tray-output", HasMedia: true, IsPlaying: true,
                PositionSeconds: 93, Kind: SystemPlaybackKind.Video, CanNext: true, CanPrevious: true } restored &&
            restored.Title == scene.Film.PlaybackMedia.Title && restored.Title != scene.Port.State.Title &&
            restored.DurationSeconds == scene.Rig.Native.Duration,
            "re-enable immediately publishes the current typed Film state and the clock changed while disabled");
        int controls = scene.Rig.Native.Controls.Count;
        scene.Backend.Request(SystemPlaybackCommand.Pause);
        await Until(() => scene.Rig.Native.Paused && scene.Backend.Last is { IsPlaying: false });
        Check(scene.Rig.Native.Controls.Count == controls + 1 && scene.Rig.Native.Controls.Last().Command == "pause" &&
            scene.Rig.Native.Controls.Last().Scope == scope && scene.Rig.Native.Stops == 0 &&
            scene.Rig.Native.Scope == scope && scene.Rig.Native.Position == 93 &&
            scene.Rig.Native.Commits == 1 && scene.Started == 1 && scene.Port.FallbackCalls.Count == 0,
            "a fresh re-enabled system request controls only the original native Film occurrence");
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmPlaybackRig Rig;
        internal readonly FilmCapability Film;
        internal readonly ShellWindow Shell;
        internal readonly TrayIcon Tray = new();
        internal readonly Backend Backend = new();
        internal readonly NativePort Port;
        internal readonly AppMediaWindowAttachment Attachment;
        internal readonly AppMediaControlSettings MediaControls;
        internal readonly AppShutdown Lifecycle;
        private readonly AppClosePreferences preferences;
        private readonly QualityFixture.DocumentStore store;
        private readonly TaskCompletionSource closed = Gate();
        internal int ShutdownCalls, CancelCalls;
        internal Task? ShutdownWork;
        internal bool IsClosed => closed.Task.IsCompleted;
        internal int Started => Rig.Runtime.Calls.Count(call => call.Method == "film.session.command" &&
            FilmJson.Text(call.Parameters, "action") == "media.started");
        internal CloseWindowDialog Dialog => ShellElement<CloseWindowDialog>("CloseDialog");

        private Scene(AppClosePreferences preferences, AppMediaControlSettings mediaControls,
            QualityFixture.DocumentStore store, ShutdownReceipt? receipt)
        {
            this.preferences = preferences; this.store = store; MediaControls = mediaControls;
            Rig = new();
            Film = new(new HomeSource(), Rig.Session, Rig.Library);
            Shell = new([Film, new SettingsNavigationTests.Module("other", "其他")], [],
                () => { ShutdownCalls++; return ShutdownWork = Lifecycle!.CloseAsync(); },
                () => Lifecycle!.ForceReleaseAsync(),
                () => { CancelCalls++; return Lifecycle!.CancelShutdownPreparationAsync(); },
                TimeProvider.System, appearance: null, preferences, (_, show, exit, _) =>
                {
                    Tray.ShowWindow = show; Tray.ExitApplication = exit; return Tray;
                }, mediaControls: mediaControls)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            Shell.Closed += (_, _) => closed.TrySetResult();
            Port = new(Rig.Native);
            Attachment = new(Shell, mediaControls);
            var systemMedia = new AppSystemMediaAttachment(Shell, Port, Film, Backend,
                [new("music"), new("film", _ => Film.StepAsync(true), _ => Film.StepAsync(false),
                    Kind: SystemPlaybackKind.Video, Play: _ => Film.ResumePlaybackAsync())],
                enabled: mediaControls.IsAvailable && mediaControls.Preferences.SystemMediaEnabled);
            Attachment.OwnSystemMedia(systemMedia, systemMedia.SetEnabled);
            Lifecycle = new([Film], [new(Attachment.DisposeAsync)],
                sharedParticipants: receipt is null ? [mediaControls, preferences] : [mediaControls, preferences, receipt]);
        }

        internal static async Task<Scene> Create(ShutdownReceipt? receipt = null, bool systemMediaEnabled = true)
        {
            var store = new QualityFixture.DocumentStore();
            var preferences = new AppClosePreferences(store);
            var mediaControls = new AppMediaControlSettings(store);
            await preferences.LoadAsync();
            await mediaControls.LoadAsync();
            await mediaControls.SaveAsync(mediaControls.Preferences with { SystemMediaEnabled = systemMediaEnabled });
            var scene = new Scene(preferences, mediaControls, store, receipt);
            try
            {
                scene.Shell.Show();
                await Until(() => scene.Film.Sections.Any(section => section.Items.Count > 0) &&
                    scene.Film.View.IsVisible && scene.ShellElement<TextBlock>("StatusText").Text.Length == 0);
                var item = Card();
                await scene.Film.OpenPlaybackAsync(item, item.PlaybackContext!);
                await Until(() => scene.Film.IsPlayback && scene.Film.Playback is not null && scene.Rig.Session.HasSession &&
                    ((FilmView)scene.Film.View).FindName("PlaybackView") is FilmPlaybackView { IsVisible: true } &&
                    scene.ShellElement<TextBlock>("StatusText").Text.Length == 0);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }

        internal async Task Play()
        {
            Rig.Runtime.Attempt("tray-output", Candidate("tray-output"));
            await Until(() => Rig.Native.SettledIdentity == "tray-output" && Started == 1 &&
                Backend.Last is { HasMedia: true, Identity: "tray-output" });
        }
        internal T ShellElement<T>(string name) where T : FrameworkElement =>
            Shell.FindName(name) as T ?? throw new InvalidOperationException("Missing shell element: " + name);
        internal T DialogElement<T>(string name) where T : FrameworkElement =>
            Dialog.FindName(name) as T ?? throw new InvalidOperationException("Missing dialog element: " + name);
        internal async Task OpenChoice()
        {
            Shell.Close();
            await Until(() => Dialog.IsOpen);
        }
        internal async Task HideToTray()
        {
            await OpenChoice();
            Click(DialogElement<Button>("MinimizeChoice"));
            await Until(() => Shell.IsHiddenToTray && !Shell.IsVisible);
        }
        internal void Leave(bool otherModule)
        {
            var list = ShellElement<ListBox>("NavigationList");
            list.SelectedItem = list.Items.OfType<ShellNavigationEntry>().Single(entry => otherModule
                ? entry.Owner.Module.Id == "other"
                : entry.Owner.Module.Id == "film" && entry.Route == FilmCapability.HomeRoute);
        }
        internal async Task WaitClosed()
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Pump();
        }
        public async ValueTask DisposeAsync()
        {
            if (!IsClosed) await Shell.AbortAsync();
            await WaitClosed();
            await Rig.DisposeAsync();
            await MediaControls.DisposeAsync();
            await preferences.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    private sealed class HomeSource : IFilmHomeSource
    {
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<FilmCardItem>>([Card()]);
        }
        public void Dispose() { }
    }

    private sealed class TrayIcon : IDesktopTrayIcon
    {
        internal Action ShowWindow = null!, ExitApplication = null!;
        internal int Disposals;
        public bool TryShow() => true;
        public void Dispose() => Disposals++;
    }

    private sealed class NativePort(FilmNativeProbe native) : ISystemPlayback
    {
        private SystemPlaybackState? music;
        private PropertyChangedEventHandler? changed;
        internal readonly List<string> FallbackCalls = [];
        public SystemPlaybackState State => music ?? new(native.Scope?.OwnerId, native.Identity, "native donor",
            native.HasOutput, native.HasOutput && !native.Paused, native.Position, native.Duration);
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { changed += value; native.PropertyChanged += value; }
            remove { changed -= value; native.PropertyChanged -= value; }
        }
        internal void PublishMusic()
        {
            music = new("music", "music-output", "other owner", true, true, 7, 120);
            changed?.Invoke(this, new(null));
        }
        public Task PlayAsync() => Fallback("play");
        public Task PauseAsync() => Fallback("pause");
        public Task StopAsync() => Fallback("stop");
        public Task SeekAsync(double seconds) => Fallback("seek");
        private Task Fallback(string command)
        {
            Check(music is not null, "unexpected direct native fallback for Film");
            FallbackCalls.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class Backend : ISystemMediaBackend
    {
        private EventHandler<SystemPlaybackRequest>? requested;
        internal SystemPlaybackState? Last;
        internal int Updates, Disabled, Disposals;
        internal int Subscribers => requested?.GetInvocationList().Length ?? 0;
        public event EventHandler<SystemPlaybackRequest>? Requested { add => requested += value; remove => requested -= value; }
        internal void Request(SystemPlaybackCommand command, double seconds = 0) => requested?.Invoke(this, new(command, seconds));
        internal Action Capture(SystemPlaybackCommand command)
        {
            var captured = requested;
            return () => captured?.Invoke(this, new(command));
        }
        public void Update(SystemPlaybackState state)
        {
            Check(Disposals == 0, "a retired backend must not be updated");
            Updates++;
            Last = state;
        }
        public void Disable() => Disabled++;
        public void Dispose() => Disposals++;
    }

    // A failing shared preparation receipt leaves Film alive; it is not a successful empty Film fixture.
    private sealed class ShutdownReceipt : IDesktopShutdownParticipant
    {
        public Task PrepareShutdownAsync() => Task.FromException(new InvalidOperationException("TRAY_PREPARATION_FAILED"));
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
