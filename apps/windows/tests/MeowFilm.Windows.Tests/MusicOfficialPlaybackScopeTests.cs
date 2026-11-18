using FontWeights = Microsoft.UI.Text.FontWeights;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using System.Globalization;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Real queue, policy, URL cache and complete-file owners; synthetic official and playback ports.
public static partial class MusicOfficialPlaybackScopeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in NativeNextTrackCases()) yield return test;
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
        yield return Case("background account revocation defers playback cancellation until dispatcher cleanup", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"));
            var playbackToken = f.Output.PlaybackToken;
            int uiThread = Environment.CurrentManagedThreadId, cancelledOn = 0, stops = f.Output.Stops;
            using var registration = playbackToken.Register(() =>
                Volatile.Write(ref cancelledOn, Environment.CurrentManagedThreadId));
            await WithDelayedAccountNotification(f, "wy", () =>
            {
                Check(!f.App.IsOfficialPlaybackCurrent("wy", "fixture", 7),
                    "authority is revoked before its UI notification is dispatched");
                Check(!playbackToken.IsCancellationRequested && Volatile.Read(ref cancelledOn) == 0 &&
                    f.Output.HasMedia && f.Output.Stops == stops,
                    "the publishing worker must not cancel or mutate UI-owned playback");
                return Task.CompletedTask;
            });
            await Until(() => !f.Output.HasMedia);
            Check(playbackToken.IsCancellationRequested && Volatile.Read(ref cancelledOn) == uiThread &&
                f.Output.StopThread == uiThread && f.Output.Stops == stops + 1,
                "dispatcher cleanup cancels and stops the revoked playback once");
        });
        yield return Case("delayed account revocation preserves another platform playback", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"), Song("other", alternate: true));
            CancellationToken accepted = default;
            int stops = 0;
            await WithDelayedAccountNotification(f, "wy", async () =>
            {
                f.App.Failures["wy"] = "AUTH_REQUIRED";
                await f.Module.StepAsync(true);
                Check(f.Output.HasMedia && f.Module.CurrentSource == "QQ 音乐" &&
                    f.Module.CurrentTitle == "Track other" &&
                    f.Output.Identity == f.Module.QueueSurfaceRows.Single(row => row.IsCurrent).EntryId, f.Module.Status);
                accepted = f.Output.PlaybackToken; stops = f.Output.Stops;
            });
            Check(f.Output.HasMedia && !accepted.IsCancellationRequested && f.Output.Stops == stops &&
                f.Module.CurrentSource == "QQ 音乐" && f.Module.CurrentTitle == "Track other",
                "a delayed notification for NetEase must preserve the accepted QQ authority");
        });
        yield return Case("delayed account revocation preserves a renewed authority", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"), Song("renewed"));
            CancellationToken accepted = default;
            int stops = 0;
            await WithDelayedAccountNotification(f, "wy", async () =>
            {
                f.App.Renew("wy");
                await f.Module.StepAsync(true);
                Check(f.App.IsOfficialPlaybackCurrent("wy", "fixture", 8) &&
                    !f.App.IsOfficialPlaybackCurrent("wy", "fixture", 7) &&
                    f.Output.HasMedia && f.Module.CurrentSource == "网易云音乐" &&
                    f.Module.CurrentTitle == "Track renewed" &&
                    f.Output.Identity == f.Module.QueueSurfaceRows.Single(row => row.IsCurrent).EntryId,
                    "playback has accepted a distinct current generation before the old event is delivered");
                accepted = f.Output.PlaybackToken; stops = f.Output.Stops;
            });
            Check(f.Output.HasMedia && !accepted.IsCancellationRequested && f.Output.Stops == stops &&
                f.Module.CurrentTitle == "Track renewed",
                "a delayed revocation must not cancel the replacement authorization on the same platform");
        });
        yield return Case("revoked known resolution is rejected before delayed dispatcher cleanup", async () =>
        {
            await using var f = await Scene.Create();
            f.App.HoldResolution = true;
            var play = f.Play(Song("first"));
            await f.App.ResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WithDelayedAccountNotification(f, "wy", async () =>
            {
                f.App.ResolutionRelease.TrySetResult();
                await play.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Output.NetworkOpens == 0 && !f.Output.HasMedia && f.App.Plans.Count == 1,
                    "native authority rejects the late known result while UI cleanup is still held");
            });
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
                Check(surface.Menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => Equals(item.Tag, "lossless")).IsChecked);
                surface.Menu.Hide();
                await MusicTestUi.Drain();
                Check(f.App.Resolutions.Count == resolutions && f.App.Plans.Count == plans &&
                    f.App.Completions.Count == 0, "opening either context entry does not resolve or search");
                f.App.MediaQuality = "320k";
                await f.SetQuality("320k");
                await f.Module.StepAsync(true);
                Check(f.Module.CurrentQualityLabel == "高音质", "the fixture accepts new playback before checking its menu");
                resolutions = f.App.Resolutions.Count;
                int completions = f.App.Completions.Count;
                await surface.OpenQuality(keyboard);
                Check(surface.Menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => Equals(item.Tag, "high")).IsChecked &&
                    !surface.Menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => Equals(item.Tag, "lossless")).IsChecked,
                    "context reopen reflects the newly accepted quality, not the previous menu");
                Check(f.App.Resolutions.Count == resolutions && f.App.Completions.Count == completions);
            }
        });
        yield return Case("native spatial submenu closes with its parent and fences the blank gesture without fabricated input", async () =>
        {
            await using var f = await Scene.Create(); await f.Play(Song("first"));
            using var surface = new PlayerSurface(f.Module);
            await MusicTestUi.Drain();
            var view = (MusicPlayerView)f.Module.PlayerView;
            int requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) => { if (args.Surface == DesktopGlobalSurface.PlayerDetail) requests++; };
            var blank = DesktopTestUi.Hit(view, new Point(300, 12))!;
            await surface.OpenQuality();
            var spatial = surface.Menu.Items.OfType<MenuFlyoutSubItem>().Single();
            var expansion = (Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider)
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(spatial)!
                    .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse);
            expansion.Expand(); await MusicTestUi.Drain();
            Check(expansion.ExpandCollapseState == Microsoft.UI.Xaml.Automation.ExpandCollapseState.Expanded && surface.Menu.IsOpen,
                "actual native submenu opens while its parent remains");
            expansion.Collapse(); await MusicTestUi.Drain();
            Check(expansion.ExpandCollapseState == Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed && surface.Menu.IsOpen,
                "collapsing the submenu retains its real parent");
            expansion.Expand(); await MusicTestUi.Drain();
            view.BeginSurfaceGesture(blank); surface.Menu.Hide(); await Until(() => !surface.Menu.IsOpen);
            await view.EndSurfaceGesture(blank).Work; Check(requests == 0, "menu dismissal cannot open the detail");
            view.BeginSurfaceGesture(blank); await view.EndSurfaceGesture(blank).Work;
            Check(requests == 1, "the next independent blank gesture opens exactly once");
            await surface.OpenQuality();
            surface.Unmount(); await MusicTestUi.Drain();
            Check(!view.IsLoaded && !surface.Menu.IsOpen, "real unmount closes the parent and submenus");
            await view.EndSurfaceGesture(blank).Work; Check(requests == 1, "retirement prevents a late release");
        });
        yield return Case("quality and source text follows disabled and high contrast control states", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"));
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenQuality();
            var current = surface.Menu.Items.OfType<ToggleMenuFlyoutItem>().First(item => item.IsChecked);
            var other = surface.Menu.Items.OfType<ToggleMenuFlyoutItem>().First(item => !item.IsChecked);
            var submenu = surface.Menu.Items.OfType<MenuFlyoutSubItem>().Single();
            other.Focus(FocusState.Programmatic); await MusicTestUi.Drain();
            Check(DesktopTestUi.HasFocus(other), "the native unselected quality receives actual focus");
            Check(SameBrush(current.Background, (Brush)DesktopVisual.Resource(current, "MeowFilm.Brush.ItemSelected")) &&
                SameBrush(other.Background, (Brush)DesktopVisual.Resource(other, "MeowFilm.Brush.ItemHover")) &&
                !SameBrush(current.Background, other.Background), "current quality and focused action have distinct backgrounds");
            current.Focus(FocusState.Programmatic); await MusicTestUi.Drain();
            Check(SameBrush(current.Background, (Brush)DesktopVisual.Resource(current, "MeowFilm.Brush.ItemSelected")) &&
                current.FontWeight == FontWeights.Normal, "focusing current quality preserves its current identity");
            foreach (var item in new Control[] { current, submenu })
            {
                void TextMatches(global::Windows.UI.Color expected)
                {
                    var label = PlayerVisuals(item).OfType<TextBlock>().Single(text => text.Text ==
                        (item is ToggleMenuFlyoutItem toggle ? toggle.Text : ((MenuFlyoutSubItem)item).Text));
                    Check(label.Foreground is SolidColorBrush ink && ink.Color == expected,
                        "actual native menu text follows its enabled and accessibility states");
                    Check(label.FontWeight == FontWeights.Normal);
                    foreach (var icon in PlayerVisuals(item).OfType<FontIcon>())
                        Check(icon.Foreground is SolidColorBrush glyph && glyph.Color == expected,
                            "the native submenu chevron uses the same state color");
                    Check(!PlayerVisuals(item).OfType<FontIcon>().Any(icon => icon.Name == "CheckGlyph" && icon.Opacity > 0),
                        "current quality is represented by its background, not a check glyph");
                }
                item.IsEnabled = false; await MusicTestUi.Drain();
                TextMatches(((SolidColorBrush)DesktopVisual.Resource(item, item is MenuFlyoutSubItem ?
                    "MenuFlyoutSubItemForegroundDisabled" : "MeowFilm.Brush.Disabled")).Color);
                item.IsEnabled = true;
                if (item is MusicPlayerMenuChoice choice)
                {
                    DesktopTheme.SetIsHighContrast(choice, true);
                    choice.Focus(FocusState.Programmatic); await MusicTestUi.Drain();
                    Check(DesktopTestUi.HasFocus(choice), "high contrast observes actual focus");
                    TextMatches(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText));
                    choice.IsEnabled = false; await MusicTestUi.Drain();
                    TextMatches(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.GrayText));
                    Check(SameBrush(choice.Background, new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window))),
                        "disabled high contrast uses the system window background");
                    choice.IsEnabled = true; DesktopTheme.SetIsHighContrast(choice, false);
                }
                else if (new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                {
                    item.IsEnabled = false; await MusicTestUi.Drain();
                    TextMatches(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.GrayText));
                    item.IsEnabled = true;
                }
            }
            await surface.List();
            f.Module.SourceChoices.Add(new("failed-fixture", "不可用音源", "QQ", "无损", "不可用", false, false));
            await MusicTestUi.Drain();
            var rows = PlayerVisuals(surface.SourcePanel).OfType<Button>()
                .Where(button => button.Tag is MusicSourceChoice).ToArray();
            Check(rows.Any(row => !row.IsEnabled), "the source view includes a genuinely unavailable candidate");
            foreach (bool contrast in new[] { false, true })
            {
                DesktopTheme.SetIsHighContrast(surface.SourcePanel, contrast);
                await MusicTestUi.Drain();
                foreach (var row in rows)
                {
                    if (!row.IsEnabled)
                        Check(SameBrush(row.Foreground, contrast ? new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.GrayText)) :
                            (Brush)DesktopVisual.Resource(row, "MeowFilm.Brush.Disabled")));
                    Check(PlayerVisuals(row).OfType<TextBlock>().All(text => SameBrush(text.Foreground, row.Foreground)),
                        "actual quality, provider and state text respects enabled/disabled contrast colors");
                }
            }
        });
        yield return Case("opening source list is cache-only and load more keeps playback and queue", async () =>
        {
            await using var f = await Scene.Create(files: true);
            // Isolate source exploration from the independent near-end next-track preload.
            await f.Module.ChangeModeAsync(3);
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
                f.Output.Identity == identity && f.Output.ReadOpens == 1 && f.Output.NetworkOpens == 0 && f.Files.Requests == transfers,
                $"source={f.Module.CurrentSource}; qq={f.Module.SourceChoices.Any(row => row.Platform == "QQ")}; " +
                $"identity={f.Output.Identity}/{identity}; opens={f.Output.ReadOpens}/{f.Output.NetworkOpens}; transfers={f.Files.Requests}/{transfers}");
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
            surface.CloseSources();
            // Native flyout closure releases the discovery owner after its actual Closed boundary.
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
            surface.CloseSources();
            await Until(() => cancelled.IsCancellationRequested);
            f.App.DiscoveryRelease.TrySetResult();
            await Until(() => f.App.DiscoveryReturns == 1);
            await MusicTestUi.Drain();
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
        foreach (var item in ArtworkCases()) yield return item;
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
            await MusicTestUi.Drain();
            if (!predicate()) await Task.Delay(1);
        }
    }
    private static async Task WithDelayedAccountNotification(Scene scene, string platform, Func<Task> whileQueued)
    {
        var dispatcher = scene.Module.Presentation.Dispatcher;
        var queued = new List<DispatcherOperation>();
        int publishingThread = 0;
        void Hold(object? sender, DispatcherHookEventArgs args)
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref publishingThread)) return;
            args.Operation.Priority = DispatcherPriority.Inactive;
            queued.Add(args.Operation);
        }
        try
        {
            // Pause dispatch while the worker posts, then retain its callbacks at
            // Inactive priority so new playback can settle before the old notice.
            using (dispatcher.DisableProcessing())
            {
                dispatcher.Hooks.OperationPosted += Hold;
                try
                {
                    Task.Run(() =>
                    {
                        Check(!dispatcher.CheckAccess(), "the notification must originate off-dispatcher");
                        Volatile.Write(ref publishingThread, Environment.CurrentManagedThreadId);
                        try { scene.App.Revoke(platform); }
                        finally { Volatile.Write(ref publishingThread, 0); }
                    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                finally { dispatcher.Hooks.OperationPosted -= Hold; }
            }
            Check(queued.Count > 0 && queued.All(operation => operation.Status == DispatcherOperationStatus.Pending),
                "the real notification callbacks remain queued during the observed action");
            await whileQueued();
        }
        finally
        {
            foreach (var operation in queued)
                if (operation.Status == DispatcherOperationStatus.Pending) operation.Priority = DispatcherPriority.Normal;
        }
        await MusicTestUi.Drain();
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MusicPlaybackCacheTests.Fixture Files;
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly Output Output = new();
        internal readonly OfficialApp App;
        internal readonly MusicCapability Module;
        private Scene(bool files, IMusicPlayback? playback = null, byte[]? bytes = null)
        {
            Files = new(bytes);
            Files.Clock.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            App = new(new MusicNativeApplication(Files.Store, MusicTestStores.For(Files.Store), Runtime, Files.CacheStore), Files);
            Runtime.OnRequest = App.ReadPlatform;
            Module = new(App, Files.Store, playback ?? Output, files ? Files.Cache : null, new Dialogs(), cacheStore: Files.CacheStore);
        }
        internal static async Task<Scene> Create(bool files = false, IMusicPlayback? playback = null, byte[]? bytes = null)
        {
            var scene = new Scene(files, playback, bytes);
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
        internal async Task<string> Queue()
        {
            var queue = JsonNode.Parse((await App.Ok("music.queue.get", new { offset = 0, limit = 10 })).GetRawText())!.AsObject();
            // Metadata changes are observable through the repository, not queue mutations.
            foreach (var entry in queue["entries"]!.AsArray().OfType<JsonObject>()
                .Concat(queue["current"] is JsonObject current ? new[] { current } : []))
                entry["song"] = new JsonObject { ["songId"] = entry["song"]!["songId"]!.DeepClone() };
            return queue.ToJsonString();
        }
        public async ValueTask DisposeAsync()
        {
            App.DiscoveryRelease.TrySetResult(); App.ResolutionRelease.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await Files.DisposeAsync(); }
        }
    }
    private sealed class OfficialApp(MusicNativeApplication inner, MusicPlaybackCacheTests.Fixture files)
        : MusicApplicationForwarder(inner)
    {
        private readonly Dictionary<(string Platform, string Id), JsonElement> platformSongs = [];
        internal Task<JsonElement> ReadPlatform(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(call.Operation == "song"
                ? platformSongs[(call.Platform, call.Parameters.GetProperty("id").GetString()!)]
                : call.Operation == "chorus"
                    ? Element(new { platform = call.Platform, id = call.Parameters.GetProperty("id").GetString(), point = (object?)null })
                    : throw new InvalidOperationException("unexpected canonical playback operation: " + call.Operation));
        }
        private async Task AdoptSong(JsonObject song, CancellationToken token)
        {
            string knownId = LJ.S(song["songId"]);
            JsonElement adopted = default;
            foreach (var source in song["sources"]!.AsArray().OfType<JsonObject>().ToArray())
            {
                string platform = LJ.S(source["source"]), id = NativeMusicSong.NativeId(source);
                var display = NativeMusicSong.Display(source);
                platformSongs[(platform, id)] = Element(NativeMusicSong.CreateOnline(platform, NativeMusicSong.SourceData(source),
                    display.Title, display.Artist, display.Album, display.Duration, display.Cover));
                adopted = (await inner.ExecuteAsync("music.catalog.song", new { platform, id }, token)).GetProperty("song").Clone();
            }
            var current = JsonNode.Parse((inner.SongSnapshot(knownId) ?? adopted).GetRawText())!.AsObject();
            song.Clear();
            foreach (var field in current) song[field.Key] = field.Value?.DeepClone();
        }
        internal readonly List<JsonElement> Plans = [], Resolutions = [], Completions = [];
        internal readonly Dictionary<string, string> Failures = [];
        internal readonly MusicSourceCache Urls = new(files.CacheStore, files.Clock);
        internal readonly TaskCompletionSource DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DiscoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResolutionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ResolutionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldDiscovery, HoldResolution, HideKnown, EnabledScript;
        internal string MediaQuality = "flac";
        internal string DiscoveryArtwork = "";
        internal bool VerifiedQuality = true;
        internal int MediaLookups, Authorizations, DiscoveryReturns;
        internal CancellationToken DiscoveryToken, ResolutionToken;
        private readonly object authority = new();
        private readonly HashSet<string> revoked = [];
        private readonly Dictionary<string, long> generations = [];
        private event Action<string>? invalidated;
        public override event Action<string>? AccountDetailsInvalidated
        {
            add { invalidated += value; Inner.AccountDetailsInvalidated += value; }
            remove { invalidated -= value; Inner.AccountDetailsInvalidated -= value; }
        }
        internal void Revoke(string platform)
        {
            lock (authority) revoked.Add(platform);
            invalidated?.Invoke(platform);
        }
        internal void Renew(string platform)
        {
            lock (authority)
            {
                generations[platform] = generations.GetValueOrDefault(platform, 7) + 1;
                revoked.Remove(platform);
            }
        }
        private long Generation(string platform) { lock (authority) return generations.GetValueOrDefault(platform, 7); }
        public override bool IsOfficialPlaybackCurrent(string platform, string scope, long generation)
        {
            lock (authority)
                return scope == "fixture" && generation == generations.GetValueOrDefault(platform, 7) &&
                    !revoked.Contains(platform);
        }
        internal async Task<JsonElement> Ok(string method, object? parameters = null)
        {
            var reply = await ExecuteAsync(method, parameters);
            Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
            return reply;
        }
        internal async Task<JsonObject> Receipt(JsonObject song, string platform)
        {
            if (LJ.S(song["songId"]).Length == 0) await AdoptSong(song, default);
            var policy = await inner.ExecuteAsync("music.quality.settings.get");
            string request = policy.GetProperty("settings").GetProperty("request").GetString()!;
            return new()
            {
                ["ok"] = true, ["kind"] = "official", ["platform"] = platform,
                ["songId"] = song["songId"]!.DeepClone(), ["sourceKey"] = Key(song, platform),
                ["scope"] = "fixture", ["generation"] = Generation(platform), ["quality"] = request == "auto" ? MediaQuality : request,
                ["requestedQuality"] = request, ["actualQuality"] = MediaQuality, ["qualityVerified"] = VerifiedQuality,
                ["playbackVerified"] = false, ["qualityPolicySignature"] = policy.GetProperty("signature").GetString(),
                ["qualityCacheSignature"] = Signature(platform),
                ["url"] = new Uri(files.Origin, platform + ".wav").AbsoluteUri,
                ["headers"] = new JsonObject(), ["expiresAt"] = files.Clock.Now + 60000,
                ["duration"] = song["duration"]!.DeepClone(), ["size"] = 0, ["full"] = false,
                ["delivery"] = platform + "-official"
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
                        LJ.S(raw["artist"]), LJ.S(raw["album"]), LJ.N(raw["duration"]), DiscoveryArtwork)["sources"]![0]!.DeepClone());
                DiscoveryReturns++;
                cancellationToken.ThrowIfCancellationRequested();
                await AdoptSong(raw, cancellationToken);
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
                            scope = "fixture", generation = Generation(LJ.S(row["source"])) }), authorizationRequired = false });
            }
            if (method == "music.official.cacheContext")
                return Element(new { ok = true, scope = "fixture", generation = Generation(input.GetProperty("platform").GetString()!),
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
        internal MenuFlyout Menu => (MenuFlyout)view.FindName("QualityMenu");
        internal Flyout SourceDialog => (Flyout)view.FindName("SourcePopup");
        internal FrameworkElement SourcePanel => (FrameworkElement)SourceDialog.Content;
        internal PlayerSurface(MusicCapability module)
        {
            this.module = module; view = (MusicPlayerView)module.PlayerView;
            window = MusicTestUi.Window(view, 1110, 160); window.Activate();
        }
        internal async Task OpenQuality(bool? contextKeyboard = null)
        {
            await MusicTestUi.Drain();
            var button = (Button)view.FindName("QualityButton");
            var badge = (Border)button.Content;
            Check(badge.Width == 72 && ((TextBlock)badge.Child).TextAlignment == TextAlignment.Center,
                "fixed-width quality badge retains its label geometry");
            PlayerMenuInput.For(view).CloseCurrent(); await Until(() => !Menu.IsOpen);
            if (contextKeyboard is not null) view.OpenQualityMenu();
            else DesktopTestUi.Invoke(button);
            await Until(() => Menu.IsOpen); await MusicTestUi.Drain();
            Check(Menu.Items[0] is MenuFlyoutItem { Text: "查看更多" } && Menu.Items.OfType<ToggleMenuFlyoutItem>()
                .Select(item => item.Tag as string).SequenceEqual(module.QualityChoices().Select(item => item.Key)),
                "more sources remains first, followed by the existing quality order");
            var root = MenuPresenter(Menu.Items[0]);
            Check(root.ActualWidth > 0 && root.ActualHeight > 0 && root.FontWeight == FontWeights.Normal,
                "the actual native menu presenter is arranged with regular text");
            foreach (var item in Menu.Items.Where(item => item is not MenuFlyoutSeparator))
                Check(item.FontWeight == FontWeights.Normal, "native quality and submenu text is regular");
            if (Menu.Items.OfType<MenuFlyoutSubItem>().SingleOrDefault() is { } spatial)
            {
                Check(spatial.Text == "空间／全景" && spatial.Items.OfType<ToggleMenuFlyoutItem>().Select(item => item.Text)
                    .SequenceEqual(module.SpatialChoices().Select(item => item.Label)), "spatial labels agree with the actual settings");
                var expansion = (Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider)
                    Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(spatial)!
                        .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse);
                expansion.Expand(); await MusicTestUi.Drain();
                var submenu = MenuPresenter(spatial.Items[0]);
                var bounds = DesktopTestUi.Bounds(submenu, view.XamlRoot.Content);
                var row = DesktopTestUi.Bounds(spatial, view.XamlRoot.Content);
                Check(Math.Min(Math.Abs(bounds.X - row.Right), Math.Abs(bounds.Right - row.X)) < 4 &&
                    bounds.Y <= row.Bottom && bounds.Bottom >= row.Y, "the native submenu joins its real parent row");
                expansion.Collapse(); await MusicTestUi.Drain();
                Check(Menu.IsOpen && expansion.ExpandCollapseState == Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed,
                    "collapsing returns to the still-open native parent");
            }
        }
        private MenuFlyoutPresenter MenuPresenter(MenuFlyoutItemBase item) =>
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                .SelectMany(popup => DesktopTestUi.Visuals(popup.Child)).OfType<MenuFlyoutPresenter>()
                .First(presenter => DesktopTestUi.Visuals(presenter).Contains(item));
        internal async Task List()
        {
            var more = Menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "查看更多");
            Check(more.IsEnabled, "real more-sources action is enabled");
            MusicTestUi.Invoke(more); await Until(() => !Menu.IsOpen && SourceDialog.IsOpen); await MusicTestUi.Drain();
            Check(SourceDialog.Placement == Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Full && SourcePanel.IsLoaded,
                "more sources opens the independent native in-root centered flyout");
        }
        internal async Task OpenSources() { await OpenQuality(); await List(); }
        internal void CloseSources() => DesktopTestUi.Invoke((Button)view.FindName("CloseSourcesButton"));
        internal void Search() => DesktopTestUi.Invoke((Button)view.FindName("LoadMoreSourcesButton"));
        internal void Unmount() { window.Content = null; }
        public void Dispose()
        {
            view.Deactivate(); window.Content = null; window.Close();
        }
    }
    private static bool SameBrush(Brush? first, Brush? second) => ReferenceEquals(first, second) ||
        first is SolidColorBrush a && second is SolidColorBrush b && a.Color == b.Color;
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
        internal int NetworkOpens, ReadOpens, Stops, StopThread;
        internal Uri? LastUri;
        internal CancellationToken PlaybackToken;
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
            PlaybackToken = token;
            OwnerId = owner; Identity = identity; HasMedia = IsPlaying = true;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            Started?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Stops++; StopThread = Environment.CurrentManagedThreadId;
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
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
}
