using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;
using MeowFilm.AppHost;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class MusicAppearanceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music appearance defaults are read only and current palettes round trip", async () =>
        {
            await using var f = new QualityFixture();
            var saved = (await f.Ok("music.appearance.get")).GetProperty("settings");
            Check(saved.GetProperty("colorMode").GetString() == "song" &&
                saved.GetProperty("playlistView").GetString() == "grid");
            Check(f.Store.Writes == 0 && f.Runtime.Calls.Count == 0);
            long revision = 0;
            foreach (var palette in MusicAppearancePalette.Options)
                await f.Ok("music.appearance.save", Draft(revision++, palette.Key));
            await f.Ok("music.appearance.save", Draft(revision++, "#12ABEF"));
            await using var reopened = new MusicLibraryOwner(f.Store);
            var restored = await reopened.ExecuteAsync("music.appearance.get");
            Check(restored.GetProperty("ok").GetBoolean() &&
                restored.GetProperty("settings").GetProperty("revision").GetInt64() == revision);
            Check(restored.GetProperty("settings").GetProperty("colorPalette").GetString() == "#12ABEF");
            Check(f.Runtime.Calls.Count == 0);
        });
        yield return new("Music appearance invalid stale and cancelled writes preserve current settings", async () =>
        {
            await using var f = new QualityFixture();
            foreach (var bad in new[] { ("playlistView", "unknown"), ("colorMode", "unknown"),
                ("colorPalette", "unknown"), ("colorPalette", "#123"), ("colorPalette", "#FF112233"),
                ("colorPalette", "#11223Z"), ("playerTheme", "downloaded-script") })
            {
                var request = Draft(); request[bad.Item1] = bad.Item2;
                await f.Error("music.appearance.save", request, "INVALID_REQUEST");
            }
            Check(f.Store.Writes == 0);
            await f.Ok("music.appearance.save", Draft());
            await f.Error("music.appearance.save", Draft(), "REVISION_CONFLICT");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Code(await f.App.ExecuteAsync("music.appearance.save", Draft(1), cancellation.Token), "CANCELLED");
            Check(f.Store.Writes == 1);
        });
        yield return new("Music appearance ambiguous CAS reconciles on the next current command without a public reconcile route", async () =>
        {
            await using var f = new QualityFixture();
            f.Store.Fault = true; f.Store.FaultAfter = true;
            f.Store.FaultNamespace = MusicLibraryOwner.DocumentNamespace;
            await f.Error("music.appearance.save", Draft(), "COMMIT_UNKNOWN");
            var current = await f.Ok("music.appearance.get");
            Check(current.GetProperty("settings").GetProperty("revision").GetInt64() == 1 &&
                current.GetProperty("settings").GetProperty("colorPalette").GetString() == "mint");
            Check(f.Store.Writes == 1);
            await f.Error("music.appearance.reconcile", null, "CAPABILITY_UNAVAILABLE");

            var doc = JsonNode.Parse(f.Store.Data[MusicLibraryOwner.DocumentNamespace])!.AsObject();
            var entry = doc["entries"]!.AsArray().OfType<JsonObject>().Single();
            entry["value"]!["colorMode"] = "corrupt";
            f.Store.Put(MusicLibraryOwner.DocumentNamespace, doc);
            await using var owner = new MusicLibraryOwner(f.Store);
            Code(await owner.ExecuteAsync("music.appearance.get"), "CORRUPT_STORAGE");
            entry["value"]!["colorMode"] = "fixed";
            f.Store.Put(MusicLibraryOwner.DocumentNamespace, doc);
        });
        yield return new("Music device volume persists separately and cannot overwrite appearance", async () =>
        {
            await using var f = new QualityFixture();
            Check((await f.Ok("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32() == 100);
            Check(f.Store.Writes == 0);
            await f.Ok("music.appearance.save", Draft());
            await f.Ok("music.device.settings.save", new { ifRevision = 0, volume = 37 });
            await f.Error("music.device.settings.save", new { ifRevision = 1, volume = 101 }, "INVALID_REQUEST");
            await f.Error("music.device.settings.save", new { ifRevision = 0, volume = 10 }, "REVISION_CONFLICT");
            int writes = f.Store.Writes;
            await f.Ok("music.device.settings.save", new { ifRevision = 1, volume = 37 });
            Check(f.Store.Writes == writes);
            await using var reopened = new MusicLibraryOwner(f.Store);
            var volume = await reopened.ExecuteAsync("music.device.settings.get");
            Check(volume.GetProperty("settings").GetProperty("volume").GetInt32() == 37);
            Check((await reopened.ExecuteAsync("music.appearance.get")).GetProperty("settings")
                .GetProperty("colorPalette").GetString() == "mint");
        });
        yield return new("Music appearance controls persist each selection immediately and expose no draft actions",
            () => MusicQualityConsumerTests.Sta(ImmediateConsumer));
        yield return new("Music appearance consumer resolves ambiguous writes internally and restores authority after a failed write",
            () => MusicQualityConsumerTests.Sta(AutomaticRecoveryConsumer));
        yield return new("Music appearance reload button reads newer authority after a revision conflict without replaying saves",
            () => MusicQualityConsumerTests.Sta(ReloadAfterConflict));
        yield return new("Music appearance backdrop fades from visible colors and retains an unchanged target",
            () => MusicQualityConsumerTests.Sta(BackdropTransitions));
        yield return new("Music appearance fixed palette retains the latest cover sample for song mode",
            () => MusicQualityConsumerTests.Sta(FixedPaletteKeepsLatestSample));
    }

    private static JsonObject Draft(long revision = 0, string palette = "mint") =>
        O(new { ifRevision = revision, playlistView = "list", playerTheme = "starlight", colorMode = "fixed", colorPalette = palette });

    private static async Task Ready(MusicCapability module)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!module.CanEditAppearance)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(1, timeout.Token);
        }
    }

    private static async Task BackdropTransitions()
    {
        Check(SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast,
            "backdrop motion regression requires normal system animation settings");
        await using var f = new QualityFixture();
        var module = new MusicCapability(f.App, f.Store, new Output());
        var surface = new Border { DataContext = module, Child = module.PlayerView };
        surface.SetBinding(Border.BackgroundProperty, nameof(MusicCapability.PlayerBackground));
        var window = GeneralAppearanceTests.Window(surface);
        try
        {
            await module.InitializeAsync(default); await Ready(module);
            module.RefreshPlayerAppearance(Brushes.Red);
            var initial = (GradientBrush)module.PlayerBackground;
            var red = ((GradientBrush)MusicPlayerArtwork.Backdrop(Brushes.Red)).GradientStops[0].Color;
            Check(initial.GradientStops.All(stop => !stop.HasAnimatedProperties) &&
                initial.GradientStops[0].Color == red, "first offscreen color is assigned directly");
            module.RefreshPlayerAppearance(new SolidColorBrush(Colors.Red));
            Check(ReferenceEquals(initial, module.PlayerBackground), "equal colors do not replace the initial brush");
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(module.PlayerView.IsVisible && ReferenceEquals(surface.Background, module.PlayerBackground));

            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                module.RefreshPlayerAppearance(Brushes.Lime);
                var moving = (GradientBrush)module.PlayerBackground;
                var green = ((GradientBrush)MusicPlayerArtwork.Backdrop(Brushes.Lime)).GradientStops[0].Color;
                Check(!ReferenceEquals(initial, moving) && moving.GradientStops.All(stop => stop.HasAnimatedProperties),
                    "a new visible target starts a native color transition");
                var movingClocks = BackdropClocks(moving);
                SeekBackdrop(movingClocks, 0);
                Check(moving.GradientStops[0].Color == red,
                    "the transition starts from the previously visible color");
                SeekBackdrop(movingClocks, 150);
                var visible = moving.GradientStops[0].Color;
                Check(ColorDistance(visible, red) > 55 && ColorDistance(visible, green) > 55,
                    "the native clock displays an intermediate color");
                Check((Color)moving.GradientStops[0].GetAnimationBaseValue(GradientStop.ColorProperty) == green,
                    "the brush retains its real target while showing an intermediate color");
                for (int i = 0; i < 3; i++)
                    module.RefreshPlayerAppearance(new SolidColorBrush(Colors.Lime));
                Check(ReferenceEquals(moving, module.PlayerBackground) && moving.GradientStops[0].Color == visible,
                    "same-target updates retain both the brush and its in-flight clock");
                Check(BackdropClocks(moving).SequenceEqual(movingClocks), "same-target updates keep the actual native clocks");

                module.RefreshPlayerAppearance(Brushes.Blue);
                var redirected = (GradientBrush)module.PlayerBackground;
                var redirectedClocks = BackdropClocks(redirected);
                SeekBackdrop(redirectedClocks, 0);
                Check(redirected.GradientStops[0].Color == visible,
                    "redirecting starts from the visible color, not the previous green target");
                module.RefreshPlayerAppearance(Brushes.Magenta);
                var supersededClocks = BackdropClocks((GradientBrush)module.PlayerBackground);
                SeekBackdrop(supersededClocks, 0);
                module.RefreshPlayerAppearance(Brushes.Yellow);
                var final = (GradientBrush)module.PlayerBackground;
                var finalClocks = BackdropClocks(final);
                SeekBackdrop(finalClocks, 0);
                SeekBackdrop(movingClocks, 300);
                SeekBackdrop(redirectedClocks, 300);
                SeekBackdrop(supersededClocks, 300);
                Check(ReferenceEquals(final, module.PlayerBackground) && final.GradientStops[0].Color == visible,
                    "retired native clocks cannot write back over the latest visible color");
                var expected = (GradientBrush)MusicPlayerArtwork.Backdrop(Brushes.Yellow);
                SeekBackdrop(finalClocks, 300);
                Check(ReferenceEquals(final, module.PlayerBackground) &&
                    final.GradientStops.Select(stop => stop.Color)
                        .SequenceEqual(expected.GradientStops.Select(stop => stop.Color)),
                    "rapid redirects finish at the newest target without an old writeback");
            }
        }
        finally { window.Close(); await module.AbortAsync(); }
    }

    private static async Task FixedPaletteKeepsLatestSample()
    {
        await using var f = new QualityFixture();
        var module = new MusicCapability(f.App, f.Store, new Output());
        try
        {
            await module.InitializeAsync(default); await Ready(module);
            await module.SetAppearanceAsync("colorMode", "fixed"); await Ready(module);
            var fixedBackground = module.PlayerBackground;
            module.RefreshPlayerAppearance(Brushes.Red);
            module.RefreshPlayerAppearance(Brushes.Lime);
            Check(ReferenceEquals(fixedBackground, module.PlayerBackground),
                "different samples do not replace the same fixed target");
            await module.SetAppearanceAsync("colorMode", "song"); await Ready(module);
            var expected = (GradientBrush)MusicPlayerArtwork.Backdrop(Brushes.Lime);
            Check(((GradientBrush)module.PlayerBackground).GradientStops.Select(stop => stop.Color)
                .SequenceEqual(expected.GradientStops.Select(stop => stop.Color)),
                "song mode uses the newest sample even when fixed-mode colors were deduplicated");
        }
        finally { await module.AbortAsync(); }
    }

    private static AnimationClock[] BackdropClocks(GradientBrush brush)
    {
        // WPF exposes no clock getter for BeginAnimation. Read the attached clocks
        // only; keep the product timelines and sample their real animated DP values.
        var storageType = typeof(AnimationClock).Assembly.GetType("System.Windows.Media.Animation.AnimationStorage", true)!;
        var getStorage = storageType.GetMethod("GetStorage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clocksField = storageType.GetField("_animationClocks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return brush.GradientStops.Select(stop =>
        {
            var storage = getStorage.Invoke(null, [stop, GradientStop.ColorProperty])!;
            var clocks = clocksField.GetValue(storage)!;
            Check((int)clocks.GetType().GetProperty("Count")!.GetValue(clocks)! == 1,
                "each backdrop color has one native product clock");
            return (AnimationClock)clocks.GetType().GetProperty("Item")!.GetValue(clocks, [0])!;
        }).ToArray();
    }

    private static void SeekBackdrop(AnimationClock[] clocks, int milliseconds)
    {
        foreach (var clock in clocks)
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime);
        Check(clocks.All(clock => clock.CurrentTime == TimeSpan.FromMilliseconds(milliseconds) ||
            milliseconds == 300 && clock.CurrentState == ClockState.Stopped),
            "the actual native clocks reached the selected sample time");
    }

    private static int ColorDistance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static async Task ImmediateConsumer()
    {
        await using var f = new QualityFixture();
        var output = new Output();
        var module = new MusicCapability(f.App, f.Store, output);
        try
        {
            await module.InitializeAsync(default); await Ready(module);
            Check(output.Volume == 100 && !module.PlaylistListView);
            var settings = module.AppearanceSettingsView;
            settings.Measure(new Size(1100, 1000));
            settings.Arrange(new Rect(0, 0, 1100, 1000)); settings.UpdateLayout();
            var mode = Visuals(settings).OfType<ComboBox>()
                .Single(control => AutomationProperties.GetName(control) == "播放器配色");
            var palette = Visuals(settings).OfType<ColorPicker>().Single();
            Check(!palette.IsEnabled && (string?)mode.SelectedValue == "song");

            int writes = f.Store.Writes;
            await module.SetAppearanceAsync("colorMode", "fixed"); await Ready(module);
            await module.SetAppearanceAsync("colorPalette", "mint"); await Ready(module);
            await module.SetAppearanceAsync("playlistView", "list"); await Ready(module);
            settings.UpdateLayout();
            Check(module.AppearanceColorMode == "fixed" && module.AppearanceColorPalette == "mint" &&
                module.PlaylistListView && palette.IsEnabled && module.AppearanceStatus.Length == 0);
            Check(f.Store.Writes == writes + 3, "each changed selection is one immediate durable write");
            Check(!Visuals(settings).OfType<Button>().Select(button => button.Content as string).Any(label =>
                label is "保存设置" or "保存外观设置" or "重新读取设置" or "放弃更改"));

            var expected = (GradientBrush)MusicPlayerArtwork.Backdrop(MusicAppearancePalette.Brush("mint"));
            Check(module.PlayerBackground is GradientBrush actual &&
                actual.GradientStops[0].Color == expected.GradientStops[0].Color,
                "player overlay consumes the current fixed palette");
            await module.SetAppearanceAsync("colorPalette", "#21ABCD"); await Ready(module);
            expected = (GradientBrush)MusicPlayerArtwork.Backdrop(MusicAppearancePalette.Brush("#21ABCD"));
            Check(module.AppearanceColorPalette == "#21ABCD" && module.PlayerBackground is GradientBrush custom &&
                custom.GradientStops[0].Color == expected.GradientStops[0].Color,
                "custom fixed color reaches the existing player composition");
            // Restore the existing preset so the original restart preference assertion remains covered.
            await module.SetAppearanceAsync("colorPalette", "mint"); await Ready(module);
            module.Volume = 37; await module.SaveVolumeAsync();
            Check((await f.Ok("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32() == 37);
            module.CatalogSections.Add(new("列表", [new("one", "合成歌单", "艺人", "", "playlist", "wy")]));
            var catalog = new MusicCatalogView { DataContext = module };
            catalog.Measure(new Size(1100, 700)); catalog.Arrange(new Rect(0, 0, 1100, 700)); catalog.UpdateLayout();
            Check(module.PlaylistListView && module.CatalogSections.Count == 1,
                "list preference publishes the current collection presentation");
            Check(output.Opens == 0 && f.Runtime.Calls.Count == 0);

            await using var appearance = new AppAppearance(f.Store);
            await appearance.LoadAsync();
            var general = new GeneralSettingsView(appearance, [module]);
            var preview = new Border { Background = AppearanceColors.DefaultBackground, Child = general };
            GeneralAppearanceTests.SavePreview(preview, "general-wide", 900, 720);
            GeneralAppearanceTests.SavePreview(preview, "general-narrow", 600, 720);

            await module.DisposeAsync();
            await using var next = new QualityFixture(f.Store);
            var restored = new MusicCapability(next.App, next.Store, new Output());
            try
            {
                await restored.InitializeAsync(default); await Ready(restored);
                Check(restored.Volume == 37 && restored.PlaylistListView &&
                    restored.AppearanceColorPalette == "mint");
            }
            finally { await restored.AbortAsync(); }
        }
        finally { await module.AbortAsync(); }
    }

    private static async Task AutomaticRecoveryConsumer()
    {
        await using var f = new QualityFixture();
        var module = new MusicCapability(f.App, f.Store, new Output());
        try
        {
            await module.InitializeAsync(default); await Ready(module);
            await module.SetAppearanceAsync("colorMode", "fixed"); await Ready(module);
            int writes = f.Store.Writes;

            f.Store.Fault = true; f.Store.FaultAfter = true;
            f.Store.FaultNamespace = MusicLibraryOwner.DocumentNamespace;
            await module.SetAppearanceAsync("colorPalette", "rose"); await Ready(module);
            Check(module.AppearanceColorPalette == "rose" && module.AppearanceStatus.Length == 0 &&
                f.Store.Writes == writes + 1, "lost receipt is reconciled without replay");
            writes = f.Store.Writes;
            await module.SetAppearanceAsync("colorPalette", "rose");
            Check(f.Store.Writes == writes, "accepted current value is not resent");

            f.Store.Fault = true; f.Store.FaultAfter = false;
            await module.SetAppearanceAsync("colorPalette", "sky"); await Ready(module);
            Check(module.AppearanceColorPalette == "rose" &&
                module.AppearanceStatus == "外观设置保存失败" && f.Store.Writes == writes + 1,
                "failed CAS restores the sole durable value and reports failure");
        }
        finally { await module.AbortAsync(); }
    }

    private static async Task ReloadAfterConflict()
    {
        await using var f = new QualityFixture();
        var module = new MusicCapability(f.App, f.Store, new Output());
        var settings = module.AppearanceSettingsView;
        var window = GeneralAppearanceTests.Window(settings);
        try
        {
            await module.InitializeAsync(default); await Ready(module);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Ready(module);
            var reload = Visuals(settings).OfType<Button>()
                .Single(button => Equals(button.Content, "重新读取"));
            var palette = Visuals(settings).OfType<ColorSelection>().Single();

            await f.Ok("music.appearance.save", Draft(0, "mint"));
            int writes = f.Store.Writes;
            await module.SetAppearanceAsync("colorPalette", "rose"); await Ready(module);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(module.AppearanceColorPalette == "mint" && palette.Value == "mint" &&
                module.AppearanceStatus == "外观设置已更新" && reload.IsVisible && reload.IsEnabled,
                "revision conflict reads the authority and exposes the actual reload button");
            Check(f.Store.Writes == writes, "revision conflict does not replay the rejected draft");

            await f.Ok("music.appearance.save", Draft(1, "sky"));
            writes = f.Store.Writes;
            Check(module.AppearanceColorPalette == "mint", "the newer authority has not yet been read");
            reload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Ready(module);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(module.AppearanceColorPalette == "sky" && palette.Value == "sky" &&
                module.AppearanceStatus.Length == 0 && !reload.IsVisible,
                "explicit reload reads the latest saved value and clears the conflict notice");
            Check(f.Store.Writes == writes, "reload performs no save");
            Check((await f.Ok("music.appearance.get")).GetProperty("settings")
                .GetProperty("revision").GetInt64() == 2, "reload leaves the authority revision unchanged");
        }
        finally
        {
            window.Close();
            await module.AbortAsync();
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var next in Visuals(VisualTreeHelper.GetChild(root, i))) yield return next;
    }

    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId => null;
        public string? Identity => null;
        public bool HasMedia => false;
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public int Volume { get; set; } = 50;
        internal int Opens;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { Opens++; return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
}
