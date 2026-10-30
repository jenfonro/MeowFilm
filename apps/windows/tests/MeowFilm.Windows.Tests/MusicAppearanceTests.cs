using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
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
            await using var reopened = new MusicLibraryOwner(f.Store);
            var restored = await reopened.ExecuteAsync("music.appearance.get");
            Check(restored.GetProperty("ok").GetBoolean() &&
                restored.GetProperty("settings").GetProperty("revision").GetInt64() == revision);
            Check(restored.GetProperty("settings").GetProperty("colorPalette").GetString() == "sky");
            Check(f.Runtime.Calls.Count == 0);
        });
        yield return new("Music appearance invalid stale and cancelled writes preserve current settings", async () =>
        {
            await using var f = new QualityFixture();
            foreach (var bad in new[] { ("playlistView", "unknown"), ("colorMode", "unknown"),
                ("colorPalette", "unknown"), ("playerTheme", "downloaded-script") })
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

    private static async Task ImmediateConsumer()
    {
        await using var f = new QualityFixture();
        var output = new Output();
        var module = new MusicCapability(f.App, f.Store, output);
        try
        {
            await module.InitializeAsync(default); await Ready(module);
            Check(output.Volume == 100 && !module.PlaylistListView);
            var settings = module.SettingsView;
            settings.Measure(new Size(1100, 1000));
            settings.Arrange(new Rect(0, 0, 1100, 1000)); settings.UpdateLayout();
            var tabs = Visuals(settings).OfType<TabControl>()
                .Single(control => AutomationProperties.GetName(control) == "设置分类");
            tabs.SelectedIndex = 3; settings.UpdateLayout();
            var mode = Visuals(settings).OfType<ComboBox>()
                .Single(control => AutomationProperties.GetName(control) == "播放器配色");
            var palette = Visuals(settings).OfType<ComboBox>()
                .Single(control => AutomationProperties.GetName(control) == "固定配色");
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
            module.Volume = 37; await module.SaveVolumeAsync();
            Check((await f.Ok("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32() == 37);
            module.CatalogSections.Add(new("列表", [new("one", "合成歌单", "艺人", "", "playlist", "wy")]));
            var catalog = new MusicCatalogView { DataContext = module };
            catalog.Measure(new Size(1100, 700)); catalog.Arrange(new Rect(0, 0, 1100, 700)); catalog.UpdateLayout();
            Check(module.PlaylistListView && module.CatalogSections.Count == 1,
                "list preference publishes the current collection presentation");
            Check(output.Opens == 0 && f.Runtime.Calls.Count == 0);

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
