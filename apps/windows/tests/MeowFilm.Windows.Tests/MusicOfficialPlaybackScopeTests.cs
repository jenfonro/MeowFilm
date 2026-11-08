using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Real queue, policy, URL cache and complete-file owners; synthetic official and playback ports.
public static partial class MusicOfficialPlaybackScopeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("complete file on another known recording wins before any URL resolution or discovery", async () =>
        {
            await using var f = await Scene.Create(files: true);
            f.App.EnabledScript = true;
            var song = Song("first", alternate: true);
            await f.SeedFile(song, "tx");
            int transfers = f.Files.Requests;
            await f.Play(song);
            Check(f.Output.ReadOpens == 1 && f.Output.NetworkOpens == 0 &&
                f.Module.CurrentSource == "QQ 音乐", f.Module.Status);
            Check(f.App.Resolutions.Count == 0 && f.App.Plans.Count == 1 &&
                f.App.Completions.Count == 0);
            Check(f.Files.Requests == transfers && f.App.Authorizations == 0,
                "licensed complete bytes need neither another transfer nor online reauthorization");
            Check(JsonNode.DeepEquals(JsonNode.Parse(f.Module.QueueSurfaceRows.Single().Song.Raw.GetRawText()), song),
                "playback must preserve the queue song");
        });
        yield return Case("valid URL cache starts playback without catalog discovery or another media lookup", async () =>
        {
            await using var f = await Scene.Create();
            f.App.EnabledScript = true;
            var song = Song("first");
            var receipt = await f.App.Receipt(song, "wy");
            await f.App.Urls.PutAsync("fixture", LJ.S(song["songId"]), Signature("wy"), receipt, default);
            await f.Play(song);
            Check(f.Output.NetworkOpens == 1 && f.Output.LastUri!.AbsoluteUri == LJ.S(receipt["url"]));
            Check(f.App.MediaLookups == 0 && f.App.Resolutions.Count == 1 &&
                f.App.Resolutions.Single().GetProperty("refresh").ValueKind == JsonValueKind.False);
            Check(f.App.Plans.Count == 1 && f.App.Completions.Count == 0);
        });
        yield return Case("lower-quality official replies do not skip preferred tiers", async () =>
        {
            await using var f = await Scene.Create();
            await f.SetQuality("auto");
            f.App.MediaQuality = "320k";
            await f.Play(Song("first"));
            Check(f.Output.NetworkOpens == 1 && f.Module.CurrentQualityLabel == "高音质", f.Module.Status);
            Check(f.App.Resolutions.First().GetProperty("quality").GetString() == "master" &&
                f.App.Completions.Count == 1 && f.App.Resolutions.Count == 2,
                "known and completed candidates are tried at the preferred tier before accepting their lower reply");
        });
        yield return Case("another usable existing mapping is tried before catalog discovery", async () =>
        {
            await using var f = await Scene.Create();
            f.App.Failures["wy"] = "VIP_REQUIRED";
            await f.Play(Song("first", alternate: true));
            Check(f.Output.NetworkOpens == 1 && f.Module.CurrentSource == "QQ 音乐", f.Module.Status);
            Check(Platforms(f.App.Resolutions).SequenceEqual(["wy", "tx"]) && f.App.Plans.Count == 1,
                "rights denial must not force catalog work while an existing mapping is usable");
        });
        foreach (string failure in new[] { "NETWORK_ERROR", "AUTH_REQUIRED", "MEDIA_EXPIRED", "RECORDING_QUALITY_UNAVAILABLE" })
        {
            yield return Case("unavailable known source discovers once without repeating its media lookup: " + failure, async () =>
            {
                await using var f = await Scene.Create();
                var song = Song("first");
                f.App.Failures["wy"] = failure;
                await f.Play(song);
                Check(f.Output.NetworkOpens == 1 && f.Module.CurrentSource == "QQ 音乐", f.Module.Status);
                Check(Platforms(f.App.Resolutions).SequenceEqual(["wy", "tx"]) && f.App.Plans.Count == 2);
                Check(f.App.Completions.Count == 1 && f.App.Completions.Single().GetProperty("platforms")
                    .EnumerateArray().Any(item => item.GetString() == "tx"), "one shared completion for the missing platform");
                f.App.Failures.Clear();
                int plans = f.App.Plans.Count;
                await f.Replay();
                Check(f.Module.CurrentSource == "网易云音乐" && f.App.Plans.Count == plans + 1,
                    "transient source rejection is limited to the failed playback attempt");
            });
        }
        yield return Case("no existing mapping discovers without inventing an unavailable source", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HideKnown = true;
            await f.Play(Song("first"));
            Check(f.Output.NetworkOpens == 1 && f.App.Plans.Count == 2 &&
                f.App.Completions.Count == 1 &&
                Platforms(f.App.Resolutions).SequenceEqual(["tx"]), f.Module.Status);
        });
        yield return Case("changed quality during known resolution cannot open the stale result or discover", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.SetQuality("128k");
            f.App.ResolutionRelease.TrySetResult();
            await play.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 0 && f.App.Plans.Count == 1 && !f.Output.HasMedia,
                "current quality authority must be checked before accepting a known URL");
        });
        yield return Case("account revocation during known resolution cannot open its late result", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.App.Revoke("wy");
            f.App.ResolutionRelease.TrySetResult();
            await play.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 0 && !f.Output.HasMedia && f.App.Plans.Count == 1);
        });
        yield return Case("context menu mouse and keyboard openings rebuild current quality choices", async () =>
        {
            foreach (bool keyboard in new[] { false, true })
            {
                await using var f = await Scene.Create();
                await f.Play(Song("first"), Song("next"));
                using var surface = new PlayerSurface(f.Module);
                int resolutions = f.App.Resolutions.Count, plans = f.App.Plans.Count;
                await surface.OpenQuality(keyboard);
                Check(surface.Menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "lossless")).IsChecked);
                surface.Menu.IsOpen = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(f.App.Resolutions.Count == resolutions && f.App.Plans.Count == plans &&
                    f.App.Completions.Count == 0, "opening either context entry does not resolve or search");
                f.App.MediaQuality = "320k";
                await f.SetQuality("320k");
                await f.Module.StepAsync(true);
                Check(f.Module.CurrentQualityLabel == "高音质", "the fixture accepts new playback before checking its menu");
                resolutions = f.App.Resolutions.Count;
                int completions = f.App.Completions.Count;
                await surface.OpenQuality(keyboard);
                Check(surface.Menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "high")).IsChecked &&
                    !surface.Menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "lossless")).IsChecked,
                    "context reopen reflects the newly accepted quality, not the previous menu");
                Check(f.App.Resolutions.Count == resolutions && f.App.Completions.Count == completions);
            }
        });
        yield return Case("spatial popup registers and releases hooks and consumes dismissal through WPF blank input", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"));
            using var surface = new PlayerSurface(f.Module);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = (MusicPlayerView)f.Module.PlayerView;
            var window = Window.GetWindow(view) ?? throw new InvalidOperationException("player has no loaded window");
            var blank = (Grid)view.Content;
            var inputField = typeof(MusicPlayerView).GetField("surfaceInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var input = inputField.GetValue(view) as PlayerSurfaceInput ??
                throw new InvalidOperationException("loaded player has no input owner");
            var sources = (Dictionary<Visual, System.Windows.Interop.HwndSource>)typeof(PlayerSurfaceInput)
                .GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!;
            var activeKey = typeof(Window).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(DependencyPropertyKey))
                .Select(field => (DependencyPropertyKey)field.GetValue(null)!)
                .Single(key => key.DependencyProperty == Window.IsActiveProperty);
            int requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) =>
            { if (args.Surface == DesktopGlobalSurface.PlayerDetail) requests++; };
            var trace = new List<string>();
            var arrivals = new List<RoutedEvent>();
            var events = new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent, Mouse.PreviewMouseUpEvent,
                Mouse.MouseUpEvent, UIElement.PreviewMouseLeftButtonDownEvent, UIElement.MouseLeftButtonUpEvent };
            void Snapshot(string point, MouseButtonEventArgs mouse) => trace.Add(
                $"{point}/{mouse.RoutedEvent.Name}: Source={mouse.Source} OriginalSource={mouse.OriginalSource} " +
                $"Handled={mouse.Handled} IsActive={window.IsActive} requests={requests}");
            MouseButtonEventHandler observer = (_, mouse) =>
            { arrivals.Add(mouse.RoutedEvent); Snapshot("view", mouse); };
            foreach (var routed in events) blank.AddHandler(routed, observer, true);
            string phase = "load";
            async Task Gesture(string label, Action? close = null)
            {
                phase = label; trace.Clear(); arrivals.Clear();
                MusicUiTests.ProcessBlankInput(blank, Mouse.PreviewMouseDownEvent, Snapshot);
                close?.Invoke();
                MusicUiTests.ProcessBlankInput(blank, Mouse.PreviewMouseUpEvent, Snapshot);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(events.All(routed => arrivals.Count(value => value == routed) == 1),
                    "preview, bubbling and player Down/Up each arrive once");
            }
            async Task<(MenuItem Item, Popup Popup, Visual Child)> OpenSpatial()
            {
                var item = surface.Menu.Items.OfType<MenuItem>().Single(value => value.HasItems);
                item.IsSubmenuOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var popup = (Popup)item.Template.FindName("PART_Popup", item);
                Check(popup.IsOpen && popup.Child is Visual, "the real quality template opens its spatial HWND");
                var child = popup.Child as Visual ?? throw new InvalidOperationException("spatial popup has no visual child");
                Check(sources.Count == 3 && sources.TryGetValue(child, out var source) &&
                    ReferenceEquals(source, PresentationSource.FromVisual(child)),
                    "window, root menu and actual spatial popup HWND are registered");
                return (item, popup, child);
            }
            try
            {
                Check(sources.Count == 1, "loaded player initially observes only its window");
                await surface.OpenQuality();
                Check(sources.Count == 2, "OpenQuality closes its keyboard-tested submenu and releases that hook");
                var spatial = await OpenSpatial();
                bool spatialClosed = false;
                EventHandler onSpatialClosed = (_, _) => spatialClosed = true;
                spatial.Popup.Closed += onSpatialClosed;
                try
                {
                    spatial.Item.IsSubmenuOpen = false;
                    await Until(() => spatialClosed);
                }
                finally { spatial.Popup.Closed -= onSpatialClosed; }
                Check(!spatial.Popup.IsOpen && surface.Menu.IsOpen &&
                    !sources.ContainsKey(spatial.Child) && sources.Count == 2,
                    "closing only the spatial submenu unregisters its HWND but retains the parent");
                spatial = await OpenSpatial();
                // Offscreen readonly-DP injection; this is not desktop activation or capture ordering.
                window.SetValue(activeKey, true);
                int before = requests;
                spatialClosed = false;
                bool menuClosed = false;
                RoutedEventHandler onMenuClosed = (_, _) => menuClosed = true;
                spatial.Popup.Closed += onSpatialClosed;
                surface.Menu.Closed += onMenuClosed;
                try
                {
                    await Gesture("spatial/dismiss", () =>
                    { spatial.Item.IsSubmenuOpen = false; surface.Menu.IsOpen = false; });
                    await Until(() => menuClosed && spatialClosed);
                }
                finally
                {
                    spatial.Popup.Closed -= onSpatialClosed;
                    surface.Menu.Closed -= onMenuClosed;
                }
                Check(requests == before && !spatial.Popup.IsOpen && !surface.Menu.IsOpen &&
                    sources.Count == 1 && !sources.ContainsKey(spatial.Child),
                    "closing both menus in this blank gesture releases popup hooks without opening detail");
                await Gesture("spatial/next");
                Check(requests == before + 1, "the following normal blank gesture opens detail exactly once");
                await surface.OpenQuality();
                spatial = await OpenSpatial();
                phase = "spatial/unload";
                spatialClosed = menuClosed = false;
                bool unloaded = false;
                RoutedEventHandler onUnloaded = (_, _) => unloaded = true;
                view.Unloaded += onUnloaded;
                spatial.Popup.Closed += onSpatialClosed;
                surface.Menu.Closed += onMenuClosed;
                try
                {
                    window.Content = null;
                    await Until(() => unloaded && menuClosed && spatialClosed);
                }
                finally
                {
                    view.Unloaded -= onUnloaded;
                    spatial.Popup.Closed -= onSpatialClosed;
                    surface.Menu.Closed -= onMenuClosed;
                }
                Check(!view.IsLoaded && inputField.GetValue(view) is null && sources.Count == 0 &&
                    !spatial.Popup.IsOpen && !surface.Menu.IsOpen,
                    "real host detachment closes the spatial popup and releases every input hook");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"{phase}: {error.Message}\n" + string.Join("\n", trace), error);
            }
            finally
            {
                foreach (var routed in events) blank.RemoveHandler(routed, observer);
                window.SetValue(activeKey, false);
            }
        });
        yield return Case("quality and source text follows disabled and high contrast control states", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"));
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality();
            var current = surface.Menu.Items.OfType<MenuItem>().First(item => item.IsChecked);
            var other = surface.Menu.Items.OfType<MenuItem>().First(item => !item.IsChecked);
            var highlighted = (DependencyPropertyKey)typeof(MenuItem).GetField("IsHighlightedPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            DesktopTheme.SetIsHighContrast(surface.Menu, false);
            other.SetValue(highlighted, true);
            Check(current.Background == (Brush)current.FindResource("MeowFilm.Brush.ItemSelected") &&
                other.Background == (Brush)other.FindResource("MeowFilm.Brush.ItemHover") &&
                current.Background != other.Background, "current quality and hovered action have distinct backgrounds");
            current.SetValue(highlighted, true);
            Check(current.Background == (Brush)current.FindResource("MeowFilm.Brush.ItemSelected") &&
                current.FontWeight == FontWeights.SemiBold, "hovering current quality preserves its current identity");
            current.SetValue(highlighted, false); other.SetValue(highlighted, false);
            foreach (var item in surface.Menu.Items.OfType<MenuItem>().Where(item => item.IsChecked || item.HasItems).ToArray())
            {
                void TextMatches(Brush expected)
                {
                    Check(System.Windows.Documents.TextElement.GetForeground(
                        (ContentPresenter)item.Template.FindName("Header", item)) == expected,
                        "header presenter inherits the menu state color without restricting header content");
                    foreach (string name in new[] { "Check", "Arrow" })
                        Check(((TextBlock)item.Template.FindName(name, item)).Foreground == expected,
                            $"{name} inherits the actual menu item's state color");
                    Check(((TextBlock)item.Template.FindName("Check", item)).Visibility == Visibility.Collapsed,
                        "current quality is represented by background, never a check glyph");
                }
                DesktopTheme.SetIsHighContrast(surface.Menu, false);
                item.IsEnabled = false; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                TextMatches((Brush)item.FindResource("MeowFilm.Brush.Disabled"));
                item.IsEnabled = true;
                DesktopTheme.SetIsHighContrast(surface.Menu, true);
                var highlightKey = (DependencyPropertyKey)typeof(MenuItem).GetField("IsHighlightedPropertyKey",
                    BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                item.SetValue(highlightKey, true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(item.IsHighlighted, "the actual menu row is in the highlighted state for the color check");
                TextMatches(SystemColors.HighlightTextBrush);
                item.IsEnabled = false; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                TextMatches(SystemColors.GrayTextBrush);
                Check(item.Background == SystemColors.WindowBrush, "disabled high contrast uses the system window background");
                item.IsEnabled = true;
                item.SetValue(highlightKey, false);
            }
            DesktopTheme.SetIsHighContrast(surface.Menu, false);
            await surface.List();
            f.Module.SourceChoices.Add(new("failed-fixture", "不可用音源", "QQ", "无损", "不可用", false, false));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var rows = PlayerVisuals(surface.SourceDialog.Child).OfType<Button>()
                .Where(button => button.Tag is MusicSourceChoice).ToArray();
            Check(rows.Any(row => !row.IsEnabled), "the source view includes a genuinely unavailable candidate");
            foreach (bool contrast in new[] { false, true })
            {
                DesktopTheme.SetIsHighContrast(surface.SourceDialog.Child, contrast);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach (var row in rows)
                {
                    if (!row.IsEnabled)
                        Check(row.Foreground == (contrast ? SystemColors.GrayTextBrush :
                            (Brush)row.FindResource("MeowFilm.Brush.Disabled")));
                    Check(PlayerVisuals(row).OfType<TextBlock>().All(text => text.Foreground == row.Foreground),
                        "actual quality, provider and state text respects enabled/disabled contrast colors");
                }
            }
        });
        yield return Case("opening source list is cache-only and load more keeps playback and queue", async () =>
        {
            await using var f = await Scene.Create(files: true);
            var song = Song("first");
            await f.SeedFile(song, "wy");
            await f.Play(song, Song("next"));
            string? identity = f.Output.Identity;
            string queue = await f.Queue();
            int plans = f.App.Plans.Count, resolutions = f.App.Resolutions.Count, transfers = f.Files.Requests;
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality(); await surface.List();
            Check(f.App.Plans.Count == plans && f.App.Resolutions.Count == resolutions && f.App.Completions.Count == 0,
                "opening quality controls and source list never resolves or searches");
            surface.Search();
            await Until(() => f.Module.CanLoadMoreSources && f.App.Completions.Count == 1);
            Check(f.Module.SourceChoices.Any(row => row.Platform == "QQ") && f.Module.CurrentSource == "网易云音乐" &&
                f.Output.Identity == identity && f.Output.ReadOpens == 1 && f.Output.NetworkOpens == 0 && f.Files.Requests == transfers);
            Check(await f.Queue() == queue, "exploring sources does not mutate the queue");
            Check(await f.Files.Cache.ReadInfoAsync("fixture", LJ.S(song["songId"]), Signature("wy"), default) is not null);
        });
        yield return Case("closing the source dialog cancels discovery and discards a noncooperative late reply", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"));
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality(); await surface.List();
            f.App.HoldDiscovery = true;
            surface.Search();
            await f.App.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            surface.CloseSourcesWithEscape();
            // WPF destroys the popup window and raises Closed from its dispatcher, not inside the IsOpen setter.
            await Until(() => f.App.DiscoveryToken.IsCancellationRequested);
            f.App.DiscoveryRelease.TrySetResult();
            await Until(() => !f.Module.Busy);
            Check(!f.Module.SourceChoices.Any(row => row.Platform == "QQ") &&
                f.Output.NetworkOpens == 1 && f.Module.CurrentSource == "网易云音乐",
                "a retired search cannot publish candidates or replace the accepted input");
        });
        yield return Case("reopening sources can finish discovery cancelled by closing the dialog", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"), Song("next"));
            using var surface = new PlayerSurface(f.Module);
            string? identity = f.Output.Identity;
            string queue = await f.Queue();
            await surface.OpenSources();
            f.App.HoldDiscovery = true;
            surface.Search();
            await f.App.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelled = f.App.DiscoveryToken;
            surface.CloseSourcesWithEscape();
            await Until(() => cancelled.IsCancellationRequested);
            f.App.DiscoveryRelease.TrySetResult();
            await Until(() => f.App.DiscoveryReturns == 1);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!f.Module.SourceChoices.Any(row => row.Platform == "QQ"),
                "the cancelled late discovery must not populate the closed dialog");

            int resolutions = f.App.Resolutions.Count;
            await surface.OpenSources();
            Check(f.App.Completions.Count == 1 && f.App.Resolutions.Count == resolutions,
                "reopening is still cache-only");
            Check(f.Module.CanLoadMoreSources, "cancelled discovery does not exhaust the current tier");
            surface.Search();
            await Until(() => !f.Module.IsLoadingSources);
            Check(f.App.Completions.Count == 2 && f.Module.SourceChoices.Any(row => row.Platform == "QQ"),
                $"explicit reload must complete the cancelled discovery; requests={f.App.Completions.Count}, " +
                $"platforms={string.Join(',', f.Module.SourceChoices.Select(row => row.Platform))}");
            Check(f.Module.CanLoadMoreSources, "the fixture still has another tier to explore");
            surface.Search();
            await Until(() => !f.Module.IsLoadingSources);
            Check(f.App.Completions.Count == 2, "a successful discovery remains shared across subsequent tiers");
            Check(f.Output.Identity == identity && f.Output.NetworkOpens == 1 &&
                f.Module.CurrentSource == "网易云音乐" && await f.Queue() == queue,
                "resuming discovery neither replaces playback nor mutates the queue");
        });
        yield return Case("changing song cancels source search and prevents its old candidates reaching the next occurrence", async () =>
        {
            await using var f = await Scene.Create();
            var next = Song("next");
            await f.Play(Song("first"), next);
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality(); await surface.List();
            f.App.HoldDiscovery = true;
            var search = f.Module.LoadMoreSourcesAsync();
            await f.App.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var step = f.Module.StepAsync(true);
            Check(f.App.DiscoveryToken.IsCancellationRequested, "changing song must retire the discovery owner");
            f.App.DiscoveryRelease.TrySetResult();
            await Task.WhenAll(search, step).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Module.CurrentTitle == LJ.S(next["title"]) && f.Output.NetworkOpens == 2 &&
                !f.Module.SourceChoices.Any(row => row.Platform == "QQ"));
            Check(!surface.SourceDialog.IsOpen, "changing songs closes the old source dialog");
        });
        yield return Case("next-song admission cancels known resolution without starting catalog discovery for the retired song", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"), Song("next"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var step = f.Module.StepAsync(true);
            Check(f.App.ResolutionToken.IsCancellationRequested);
            f.App.HoldResolution = false;
            f.App.ResolutionRelease.TrySetResult();
            await Task.WhenAll(play, step).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 1 && f.Module.CurrentTitle == "Track next" &&
                f.App.Completions.Count == 0);
        });
        foreach (var item in SourcePresentationCases()) yield return item;
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("official playback scope: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static void Check(bool value, string message = "official playback scope assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static string[] Platforms(IEnumerable<JsonElement> calls) =>
        calls.Select(call => call.GetProperty("platform").GetString()!).ToArray();
    private static string Signature(string platform) => new(platform == "wy" ? 'b' : platform == "tx" ? 'c' : 'd', 64);
    private static string Key(JsonObject song, string platform) => song["sources"]!.AsArray()
        .OfType<JsonObject>().Single(row => LJ.S(row["source"]) == platform)["sourceKey"]!.GetValue<string>();
    private static JsonObject Song(string id, bool alternate = false)
    {
        var song = MusicSongFixture.Online("wy", id, "Track " + id, "Artist", duration: 1);
        if (alternate)
            song["sources"]!.AsArray().Add(MusicSongFixture.Online("tx", id + "Tx",
                LJ.S(song["title"]), "Artist", duration: 1)["sources"]![0]!.DeepClone());
        return NativeMusicSong.Normalize(song);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("official playback scope");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!predicate()) await Task.Delay(1);
        }
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MusicPlaybackCacheTests.Fixture Files = new();
        internal readonly Output Output = new();
        internal readonly OfficialApp App;
        internal readonly MusicCapability Module;
        private Scene(bool files)
        {
            Files.Clock.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            App = new(new MusicNativeApplication(Files.Store, new NoNode(), new NoHttp()), Files);
            Module = new(App, Files.Store, Output, files ? Files.Cache : null, new Dialogs());
        }
        internal static async Task<Scene> Create(bool files = false)
        {
            var scene = new Scene(files);
            await scene.Module.InitializeAsync(default);
            await scene.SetQuality("flac");
            await scene.App.Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
            await Until(() => !scene.Module.Busy);
            return scene;
        }
        internal async Task SetQuality(string request)
        {
            var current = await App.Ok("music.quality.settings.get");
            var policy = JsonNode.Parse(current.GetProperty("settings").GetRawText())!.AsObject();
            policy["ifRevision"] = policy["revision"]!.DeepClone();
            policy.Remove("schema"); policy.Remove("revision");
            policy["request"] = request;
            await App.Ok("music.quality.settings.save", policy);
        }
        internal async Task SeedFile(JsonObject song, string platform)
        {
            var info = await App.Receipt(song, platform);
            info["full"] = true;
            info["size"] = Files.Bytes.Length;
            info["licenseExpiresAt"] = Files.Clock.Now + 3600000;
            info["authorizationExpiresAt"] = info["licenseExpiresAt"]!.DeepClone();
            info["offlineAllowed"] = true;
            await Files.Cache.StorePlayedAsync("fixture", LJ.S(song["songId"]), Signature(platform),
                Element(info), () => true, default);
            Check(await Files.Cache.ReadInfoAsync("fixture", LJ.S(song["songId"]), Signature(platform), default) is not null);
        }
        internal async Task Play(params JsonObject[] songs)
        {
            await App.Ok("music.queue.replace", new { songs, index = 0 });
            await Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await Module.PlayQueueSurfaceItemAsync(Module.QueueSurfaceRows.First());
        }
        // A new playback attempt of the current occurrence through the module's own queue command.
        internal async Task Replay()
        {
            await Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default); // settles a pending row reload
            await Module.PlayQueueSurfaceItemAsync(Module.QueueSurfaceRows.First());
        }
        internal async Task<string> Queue() =>
            (await App.Ok("music.queue.get", new { offset = 0, limit = 10 })).GetRawText();
        public async ValueTask DisposeAsync()
        {
            App.DiscoveryRelease.TrySetResult(); App.ResolutionRelease.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Files.DisposeAsync(); }
        }
    }
    private sealed class OfficialApp(MusicNativeApplication inner, MusicPlaybackCacheTests.Fixture files)
        : MusicApplicationForwarder(inner)
    {
        internal readonly List<JsonElement> Plans = [], Resolutions = [], Completions = [];
        internal readonly Dictionary<string, string> Failures = [];
        internal readonly MusicSourceCache Urls = new(files.Store, files.Clock);
        internal readonly TaskCompletionSource DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DiscoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResolutionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResolutionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldDiscovery, HoldResolution, HideKnown, EnabledScript;
        internal string MediaQuality = "flac";
        internal bool VerifiedQuality = true;
        internal int MediaLookups, Authorizations, DiscoveryReturns;
        internal CancellationToken DiscoveryToken, ResolutionToken;
        private readonly HashSet<string> revoked = [];
        private event Action<string>? invalidated;
        public override event Action<string>? AccountDetailsInvalidated
        {
            add { invalidated += value; Inner.AccountDetailsInvalidated += value; }
            remove { invalidated -= value; Inner.AccountDetailsInvalidated -= value; }
        }
        internal void Revoke(string platform)
        {
            revoked.Add(platform);
            invalidated?.Invoke(platform);
        }
        public override bool IsOfficialPlaybackCurrent(string platform, string scope, long generation) =>
            scope == "fixture" && generation == 7 && !revoked.Contains(platform);
        internal async Task<JsonElement> Ok(string method, object? parameters = null)
        {
            var reply = await ExecuteAsync(method, parameters);
            Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
            return reply;
        }
        internal async Task<JsonObject> Receipt(JsonObject song, string platform)
        {
            var policy = await inner.ExecuteAsync("music.quality.settings.get");
            string request = policy.GetProperty("settings").GetProperty("request").GetString()!;
            return new()
            {
                ["ok"] = true, ["kind"] = "official", ["platform"] = platform,
                ["songId"] = song["songId"]!.DeepClone(), ["sourceKey"] = Key(song, platform),
                ["scope"] = "fixture", ["generation"] = 7, ["quality"] = request == "auto" ? MediaQuality : request,
                ["requestedQuality"] = request, ["actualQuality"] = MediaQuality, ["qualityVerified"] = VerifiedQuality,
                ["playbackVerified"] = false, ["qualityPolicySignature"] = policy.GetProperty("signature").GetString(),
                ["qualityCacheSignature"] = Signature(platform),
                ["url"] = new Uri(files.Origin, platform + ".wav").AbsoluteUri,
                ["headers"] = new JsonObject(), ["expiresAt"] = files.Clock.Now + 60000,
                ["duration"] = 1d, ["size"] = 0, ["full"] = false, ["licenseExpiresAt"] = null,
                ["delivery"] = platform + "-official", ["offlineAllowed"] = false, ["authorizationExpiresAt"] = null
            };
        }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            var input = Element(parameters ?? new { });
            if (method == "music.sources.list")
                return Element(new { ok = true, revision = 1, items = EnabledScript ? new object[] {
                    new { installationId = "higher-quality-script", name = "Script", enabled = true, executionHash = new string('a', 64),
                        sites = new { wy = new { actions = new[] { "musicUrl" }, qualitys = new[] { "master" } } } }
                } : [] });
            if (EnabledScript && method is "music.sources.detail" or "music.online.resolve")
                throw new InvalidOperationException("playable cache or known mapping must not probe scripts");
            if (method == "music.songs.complete")
            {
                Completions.Add(input); DiscoveryToken = cancellationToken; DiscoveryEntered.TrySetResult();
                if (HoldDiscovery) await DiscoveryRelease.Task;
                var raw = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                if (!raw["sources"]!.AsArray().OfType<JsonObject>().Any(row => LJ.S(row["source"]) == "tx"))
                    raw["sources"]!.AsArray().Add(MusicSongFixture.Online("tx", "Discovered", LJ.S(raw["title"]),
                        LJ.S(raw["artist"]), LJ.S(raw["album"]), LJ.N(raw["duration"]))["sources"]![0]!.DeepClone());
                DiscoveryReturns++;
                return Element(new { ok = true, song = NativeMusicSong.Normalize(raw) });
            }
            if (method == "music.official.candidates")
            {
                Plans.Add(input);
                var raw = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                var policy = await inner.ExecuteAsync("music.quality.settings.get");
                return Element(new { ok = true, song = raw,
                    qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    candidates = raw["sources"]!.AsArray().OfType<JsonObject>()
                        .Where(row => !(HideKnown && LJ.S(row["source"]) == "wy"))
                        .Select(row => new { platform = LJ.S(row["source"]), sourceKey = LJ.S(row["sourceKey"]),
                            scope = "fixture", generation = 7 }), authorizationRequired = false });
            }
            if (method == "music.official.cacheContext")
                return Element(new { ok = true, scope = "fixture", generation = 7,
                    sourceKey = input.GetProperty("sourceKey").GetString(),
                    qualityCacheSignature = Signature(input.GetProperty("platform").GetString()!) });
            if (method == "music.official.resolve")
            {
                string platform = input.GetProperty("platform").GetString()!;
                var song = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                var cached = await Urls.ReadAsync("fixture", LJ.S(song["songId"]), Signature(platform), cancellationToken);
                if (input.TryGetProperty("cacheOnly", out var cacheOnly) && cacheOnly.GetBoolean() && cached is null)
                    return Element(new { ok = false, code = "CACHE_MISS" });
                Resolutions.Add(input);
                var info = await Receipt(song, platform);
                info["quality"] = input.GetProperty("quality").GetString();
                info["requestedQuality"] = info["quality"]!.DeepClone();
                ResolutionToken = cancellationToken; ResolutionEntered.TrySetResult();
                if (HoldResolution) await ResolutionRelease.Task; // Exercise post-await account/policy/cancel checks.
                if (Failures.TryGetValue(platform, out var code)) return Element(new { ok = false, code });
                if (cached is not null) return Element(cached);
                MediaLookups++;
                return Element(info);
            }
            if (method == "music.official.authorize")
            { Authorizations++; return Element(new { ok = true }); }
            if (method == "music.official.confirm") return Element(new { ok = true });
            return await inner.ExecuteAsync(method, parameters, cancellationToken);
        }
    }
    private sealed class PlayerSurface : IDisposable
    {
        private readonly MusicPlayerView view;
        private readonly Window window;
        private readonly MusicCapability module;
        internal ContextMenu Menu => (ContextMenu)view.FindName("QualityMenu");
        internal Popup SourceDialog => (Popup)view.FindName("SourcePopup");
        internal PlayerSurface(MusicCapability module)
        {
            this.module = module;
            view = (MusicPlayerView)module.PlayerView;
            window = new Window { Content = view, Width = 1000, Height = 160, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show(); window.UpdateLayout();
        }
        internal async Task OpenQuality(bool? contextKeyboard = null)
        {
            var button = (Button)view.FindName("QualityButton");
            var badge = (Border)button.Content;
            Check(badge.Width == 72 && ((TextBlock)badge.Child).TextAlignment == TextAlignment.Center,
                "fixed-width quality badge centers short labels without changing playback state");
            if (contextKeyboard is { } keyboard)
            {
                // Raise WPF's actual routed request without injecting input into the desktop.
                double position = keyboard ? -1 : 12;
                var request = (ContextMenuEventArgs)typeof(ContextMenuEventArgs)
                    .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                        [typeof(object), typeof(bool), typeof(double), typeof(double)], null)!
                    .Invoke([button, true, position, position]);
                button.RaiseEvent(request);
                Check(request.Handled, "context requests use the same initialization owner as left-click");
            }
            else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Menu.IsOpen);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Menu.IsOpen, "actual quality menu must open");
            var choices = Menu.Items.OfType<MenuItem>().ToArray();
            Check(Equals(choices[0].Header, "查看更多") && choices.Skip(1).Where(item => !item.HasItems)
                .Select(item => item.Tag as string).SequenceEqual(module.QualityChoices().Select(item => item.Key)),
                "the existing category order is retained with more sources first");
            double widestLabel = choices.Max(item => new FormattedText(item.Header.ToString()!, CultureInfo.CurrentCulture,
                item.FlowDirection, new Typeface(item.FontFamily, item.FontStyle, item.FontWeight, item.FontStretch),
                item.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(item).PixelsPerDip).WidthIncludingTrailingWhitespace);
            var menuPanel = PlayerVisuals(Menu).OfType<Border>().Single(border =>
                ReferenceEquals(border.Style, Menu.FindResource("MeowFilm.FloatingSurface")));
            Check(double.IsNaN(Menu.Width) && menuPanel.ActualWidth >= widestLabel && menuPanel.ActualWidth - widestLabel < 80,
                "menu fits its labels with only padding, selection and submenu glyph slots");
            GeneralAppearanceTests.SavePreview(Menu, "refinement-quality-menu",
                (int)Math.Ceiling(Menu.ActualWidth), (int)Math.Ceiling(Menu.ActualHeight));
            var spatial = choices.SingleOrDefault(item => item.HasItems);
            if (spatial is not null)
            {
                Check(Equals(spatial.Header, "空间／全景") && spatial.Items.OfType<MenuItem>().Select(item => item.Header)
                    .SequenceEqual(module.SpatialChoices().Select(item => item.Label)), "spatial names agree with settings");
                spatial.Focus();
                spatial.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(spatial), 0, System.Windows.Input.Key.Right)
                { RoutedEvent = UIElement.KeyDownEvent });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(spatial.IsSubmenuOpen && Menu.IsOpen, "Right opens the submenu without closing its parent");
                var popup = (Popup)spatial.Template.FindName("PART_Popup", spatial);
                var submenu = (FrameworkElement)((Border)popup.Child).Child;
                var row = (Border)spatial.Template.FindName("Row", spatial);
                var rowStart = row.PointToScreen(new());
                var rowEnd = row.PointToScreen(new(row.ActualWidth, row.ActualHeight));
                var menuStart = submenu.PointToScreen(new());
                var menuEnd = submenu.PointToScreen(new(submenu.ActualWidth, submenu.ActualHeight));
                Check(Math.Min(Math.Abs(menuStart.X - rowEnd.X), Math.Abs(menuEnd.X - rowStart.X)) < 3 &&
                    menuStart.Y <= rowEnd.Y && menuEnd.Y >= rowStart.Y,
                    "the actual submenu joins the corresponding parent row on either side");
                GeneralAppearanceTests.SavePreview(submenu, "refinement-spatial-menu",
                    (int)Math.Ceiling(submenu.ActualWidth), (int)Math.Ceiling(submenu.ActualHeight));
                var first = (MenuItem)spatial.Items[0];
                first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), 0, System.Windows.Input.Key.Left)
                { RoutedEvent = UIElement.KeyDownEvent });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!spatial.IsSubmenuOpen && Menu.IsOpen, "Left returns to the still-open parent menu");
            }
        }
        internal async Task List()
        {
            var more = (MenuItem)view.FindName("SourceListItem");
            more.Focus();
            more.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(more), 0, System.Windows.Input.Key.Enter)
            { RoutedEvent = UIElement.KeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!Menu.IsOpen && SourceDialog.IsOpen && MeowFilm.Desktop.DialogPlacement.GetCentered(SourceDialog),
                "Enter on more sources leaves the menu for an independent centered dialog");
            SourceDialog.Child.UpdateLayout();
            var panel = (FrameworkElement)SourceDialog.Child;
            GeneralAppearanceTests.SavePreview(panel, "refinement-source-dialog",
                (int)Math.Ceiling(panel.ActualWidth + panel.Margin.Left + panel.Margin.Right),
                (int)Math.Ceiling(panel.ActualHeight + panel.Margin.Top + panel.Margin.Bottom));
        }
        internal async Task OpenSources()
        {
            var button = (Button)view.FindName("QualityButton");
            Check(button.IsVisible && button.IsEnabled, "the quality entry is available for source discovery");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Menu.IsOpen);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var more = (MenuItem)view.FindName("SourceListItem");
            Check(more.IsVisible && more.IsEnabled, "the actual more-sources menu action is available");
            // Source lifetime tests use the click route, independently of submenu keyboard/capture tests.
            more.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => SourceDialog.IsOpen && !Menu.IsOpen);
            Check(DialogPlacement.GetCentered(SourceDialog), "the source action opens the centered dialog");
        }
        internal void CloseSourcesWithEscape() =>
            SourceDialog.Child.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(SourceDialog.Child), 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = UIElement.KeyDownEvent });
        internal void Search()
        {
            var button = (Button)view.FindName("LoadMoreSourcesButton");
            Check(button.IsVisible && button.IsEnabled, "source search must be a usable UI action");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        public void Dispose()
        {
            Menu.IsOpen = false; SourceDialog.IsOpen = false;
            window.Content = null; window.Close();
        }
    }
    private static IEnumerable<DependencyObject> PlayerVisuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in PlayerVisuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed class Output : IMusicPlayback, INativePlaybackContinuation, INativePlaybackHandoff
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }
        public double PositionSeconds => 0;
        public double DurationSeconds => 1;
        public int Volume { get; set; } = 50;
        public bool PauseRequested => !IsPlaying;
        public string? LastErrorCode => null;
        internal int NetworkOpens, ReadOpens;
        internal Uri? LastUri;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started;
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            NetworkOpens++; LastUri = uri;
            return Open(owner, identity, token);
        }
        public async Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease,
            string title, CancellationToken token = default)
        {
            await using (lease) Check(lease.Length > 0 && lease.Content.ReadByte() >= 0);
            ReadOpens++;
            await Open(owner, identity, token);
        }
        private Task Open(string owner, string identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OwnerId = owner; Identity = identity; HasMedia = IsPlaying = true;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            Started?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            HasMedia = IsPlaying = false; OwnerId = Identity = null;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            return Task.CompletedTask;
        }
        public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
        public Task ResumeAsync() { IsPlaying = HasMedia; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public double PreparedHandoffDurationSeconds => 0;
        public Task StopOutputForHandoffAsync() => throw new InvalidOperationException("no handoff during exploration");
        public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
            throw new InvalidOperationException("exploration must not prepare playback");
        public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
            throw new InvalidOperationException("exploration must not prepare playback");
        public async Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease,
            CancellationToken token = default)
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException("no read continuation during exploration");
        }
        public Task CommitHandoffAsync(Action committed, CancellationToken token) => throw new InvalidOperationException("no handoff during exploration");
        public Task DiscardHandoffAsync() => Task.CompletedTask;
        public NativePlaybackContinuation? CaptureContinuation() => null;
        public Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
            IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
            throw new InvalidOperationException("source discovery must never reopen playback");
    }
    private sealed class NoNode : IRuntimeClient
    {
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { throw new InvalidOperationException("Native business must not upload script sources"); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("NO_NODE_ALLOWED");
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("NO_UPSTREAM_ALLOWED");
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
}
