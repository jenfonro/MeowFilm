using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicDesktopLyricsTests
{
    public static IEnumerable<TestCase> Cases() =>
        MusicUiTests.DesktopLyricsCases().Concat(MusicLyricDisplayTests.DesktopConsumerCases());
}

public static partial class MusicLyricDisplayTests
{
    internal static IEnumerable<TestCase> DesktopConsumerCases()
    {
        foreach (var mutation in new[] { "disable", "update" })
            yield return new("Native desktop-only lyric consumer source " + mutation + " fences the prior flight", () => Sta(async () =>
            {
                LyricLookupApplication? application = null;
                await using var f = await Fixture.Create(allowScripts: true, decorate: inner => application = new(inner));
                var lookups = application!;
                var source = await f.InstallScript();
                await f.Module.SetSourceEnabledAsync(source, true);
                source = f.Module.Sources.Single(row => row.InstallationId == source.InstallationId);
                Check(source.Enabled);
                var owner = LyricConsumerOwner();
                var arrived = Signal(); var release = Signal();
                Task? opening = null;
                try
                {
                    owner.Show(); f.Module.AttachDesktopLyricsOwner(owner);
                    var published = new List<string>();
                    f.Module.Lyrics.CollectionChanged += (_, _) => published.AddRange(f.Module.Lyrics.Select(row => row.Text));
                    f.Http.Text = "OLD_DESKTOP_SOURCE";
                    f.Http.Block = async () => { arrived.TrySetResult(); await release.Task; };
                    opening = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                    await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!f.Module.IsLyrics && f.Module.NeedsLyrics);
                    // Let the accepted source mutation settle before dispatching replacement lookups.
                    // The real native owner still performs every query and invalidation.
                    lookups.HoldLookups = true;
                    f.Http.Text = "CURRENT_DESKTOP_SOURCE"; f.Http.Block = null;
                    if (mutation == "disable")
                    {
                        await f.Module.SetSourceEnabledAsync(source, false);
                        Check(!f.Module.Sources.Single(row => row.InstallationId == source.InstallationId).Enabled);
                    }
                    else
                    {
                        string previousHash = source.ExecutionHash;
                        await File.WriteAllTextAsync(f.ScriptPath, "// desktop changed source");
                        await f.Module.UpdateSourceAsync(source);
                        Check(f.Module.Sources.Single(row => row.InstallationId == source.InstallationId).ExecutionHash != previousHash);
                    }
                    await opening.WaitAsync(TimeSpan.FromSeconds(5));
                    Equal(0, f.Module.Lyrics.Count);
                    Check(!published.Contains("OLD_DESKTOP_SOURCE"));
                    lookups.ReleaseLookups();
                    await AwaitDesktopConsumer(() => f.Module.Lyrics.FirstOrDefault()?.Text == "CURRENT_DESKTOP_SOURCE" &&
                        !f.Module.CanCancelLyrics);
                    release.TrySetResult();
                    Check(!f.Module.IsLyrics && f.Module.IsDesktopLyricsOpen && f.Module.NeedsLyrics);
                    Equal(2, f.Module.Lyrics.Count);
                    Equal("CURRENT_DESKTOP_SOURCE", f.Module.Lyrics[0].Text);
                    Equal("second", f.Module.Lyrics[1].Text);
                    Check(!published.Contains("OLD_DESKTOP_SOURCE"));
                    Equal(2, f.Http.Calls); Equal(0, f.Runtime.Invocations); Equal(0, f.Playback.Opens);
                    var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
                    var saved = Ok(await f.Application.ExecuteAsync("music.lyrics.display.get",
                        new { song = queue.GetProperty("current").GetProperty("song") }));
                    Equal("CURRENT_DESKTOP_SOURCE", saved.GetProperty("lines")[0].GetProperty("text").GetString());
                }
                finally
                {
                    lookups.ReleaseLookups(); release.TrySetResult(); owner.Close();
                    if (opening is not null) await opening;
                }
            }));

        yield return new("Native desktop-only lyric consumer metadata policy reloads without a detail page", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            var owner = LyricConsumerOwner();
            try
            {
                owner.Show(); f.Module.AttachDesktopLyricsOwner(owner);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                Equal(2, f.Module.Lyrics.Count); Equal(1, f.Http.Calls);
                var platform = f.Module.MetadataPlatforms.Single(row => row.Platform == "wy");
                await f.Module.SetMetadataEnabledAsync(platform, false);
                Equal(0, f.Module.Lyrics.Count);
                Check(f.Module.NeedsLyrics && !f.Module.IsLyrics);
                await f.Module.SetMetadataEnabledAsync(platform, true);
                Equal(2, f.Module.Lyrics.Count);
                Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
            }
            finally { owner.Close(); }
        }));

        yield return new("Native desktop-only lyrics restore after successful account authority settles", () => Sta(async () =>
        {
            DesktopAccountApplication? account = null;
            await using var f = await Fixture.Create(decorate: inner => account = new(inner));
            var owner = LyricConsumerOwner();
            try
            {
                owner.Show(); f.Module.AttachDesktopLyricsOwner(owner);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var prior = f.Module.Lyrics[0];
                await f.Module.ReadAccountsAsync(); f.Module.SelectedAccountPlatform = "wy";
                int reads = account!.LyricReads;
                await f.Module.ValidateAccountAsync();
                await AwaitDesktopConsumer(() => account.LyricReads == reads + 1 && f.Module.Lyrics.Count == 2);
                Equal(reads + 1, account.LyricReads);
                Check(!ReferenceEquals(prior, f.Module.Lyrics[0]));
                Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
                Check(!f.Module.IsLyrics && f.Module.NeedsLyrics);
            }
            finally { owner.Close(); }
        }));

        foreach (bool stop in new[] { false, true })
            yield return new("Native desktop-only lyric account cancellation " + (stop ? "does not revive shutdown" : "fences the old account epoch"), () => Sta(async () =>
            {
                DesktopAccountApplication? account = null;
                await using var f = await Fixture.Create(decorate: inner => account = new(inner));
                var probe = account!;
                var owner = LyricConsumerOwner();
                Task? validation = null, cancellation = null, preparation = null;
                try
                {
                    owner.Show(); f.Module.AttachDesktopLyricsOwner(owner);
                    await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                    await f.Module.ReadAccountsAsync(); f.Module.SelectedAccountPlatform = "wy";
                    probe.BlockValidate = true; probe.BlockCancel = true;
                    int reads = probe.LyricReads;
                    validation = f.Module.ValidateAccountAsync();
                    await probe.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    cancellation = f.Module.CancelAccountAsync();
                    await probe.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Equal(reads, probe.LyricReads);
                    if (stop) preparation = f.Module.PrepareShutdownAsync();
                    probe.CancelRelease.TrySetResult();
                    await cancellation;
                    if (!stop)
                    {
                        await AwaitDesktopConsumer(() => probe.LyricReads == reads + 1 && f.Module.Lyrics.Count == 2);
                        Equal(reads + 1, probe.LyricReads);
                        Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
                    }
                    probe.ValidationRelease.TrySetResult();
                    await validation;
                    if (preparation is not null) await preparation;
                    Equal(reads + (stop ? 0 : 1), probe.LyricReads);
                    if (stop) Check(!f.Module.IsDesktopLyricsOpen && f.Module.Lyrics.Count == 0);
                    else Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
                }
                finally
                {
                    probe.CancelRelease.TrySetResult(); probe.ValidationRelease.TrySetResult();
                    owner.Close();
                    if (validation is not null) await validation;
                    if (cancellation is not null) await cancellation;
                    if (preparation is not null) await preparation;
                }
            }));
    }

    private static Window LyricConsumerOwner() => new()
    {
        Width = 1110, Height = 800, Left = -32000, Top = -32000,
        ShowActivated = false, ShowInTaskbar = false
    };

    private static async Task AwaitDesktopConsumer(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Automatic desktop lyric refresh did not complete");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!condition()) await Task.Delay(1);
        }
    }

    private sealed class DesktopAccountApplication(IMusicApplication inner) : MusicApplicationForwarder(inner)
    {
        internal int LyricReads;
        internal bool BlockValidate, BlockCancel;
        internal readonly TaskCompletionSource ValidationEntered = Signal(), ValidationRelease = Signal(),
            CancelEntered = Signal(), CancelRelease = Signal();
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "music.lyrics.local.get") LyricReads++;
            if (method == "music.accounts.list")
                return Reply(new { items = new[] { Account("wy"), Account("tx"), Account("kg") } });
            if (method == "music.accounts.validate")
            {
                ValidationEntered.TrySetResult();
                if (BlockValidate) await ValidationRelease.Task; // Intentionally noncooperative old authority.
                return Reply(new { account = Account("wy") });
            }
            if (method == "music.accounts.cancel")
            {
                CancelEntered.TrySetResult();
                if (BlockCancel) await CancelRelease.Task;
                return Reply(new { });
            }
            return await Inner.ExecuteAsync(method, parameters, cancellationToken);
        }
        private static object Account(string platform) => new { platform, state = "verified", generation = 1,
            profile = new { platform, userId = "synthetic-lyric-account", displayName = "歌词测试" } };
        private static JsonElement Reply(object value)
        {
            var result = JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();
            result["ok"] = true;
            return JsonSerializer.SerializeToElement(result);
        }
    }
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> DesktopLyricsCases()
    {
        yield return Case("desktop lyrics button follows the playlist button at both footer widths", () =>
            WithDesktopLyrics(async (f, owner) =>
            {
                await SaveDesktopLyricFixture(f);
                var player = (MusicPlayerView)f.Module.PlayerView;
                owner.Content = player;
                var options = (StackPanel)player.FindName("OptionsPanel");
                var playlist = (Button)player.FindName("PlaylistButton");
                var button = (Button)player.FindName("DesktopLyricsButton");
                Equal(options.Children.IndexOf(playlist) + 1, options.Children.IndexOf(button));
                Equal("VolumeButton", ((FrameworkElement)options.Children[options.Children.IndexOf(button) + 1]).Name);
                True(button.Content is TextBlock { Text: "桌" }, "the label is text, not an icon-font glyph");
                Equal(SystemFonts.MessageFontFamily, ((TextBlock)button.Content).FontFamily);
                foreach (int width in new[] { 1110, 860 })
                {
                    Render(player, width, 100); AssertPlayerBounds(player, width);
                    True(button.TranslatePoint(new(0, 0), player).X >=
                        playlist.TranslatePoint(new(playlist.ActualWidth, 0), player).X, "desktop control is to the right");
                    GeneralAppearanceTests.SavePreview(player, "media-control-footer-" + width, width, 100);
                }
                Click(button);
                await Until(() => f.Module.IsDesktopLyricsOpen && f.Module.Lyrics.Count == 2 && !f.Module.CanCancelLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                True(!f.Module.IsLyrics, "the ordinary footer does not open full player detail");
                Equal("关闭桌面歌词", AutomationProperties.GetName(button));
                True(window.IsVisible && window.Topmost && !window.ShowActivated && !window.ShowInTaskbar,
                    "a real topmost nonactivating tool window is shown");
                True(window.Owner is null && !window.IsActive, "no WPF owner minimization or activation relationship");
                var handle = new WindowInteropHelper(window).Handle;
                string shown = DescribeDesktopWindow(window);
                Console.WriteLine("EVIDENCE desktop-lyrics shown; " + shown);
                True(DesktopLyricNative.IsWindowVisible(handle) &&
                    (DesktopLyricNative.GetWindowLongPtr(handle, -20) & (nint)0x08000080) == (nint)0x08000080,
                    "NOACTIVATE and TOOLWINDOW remain set on the actual HWND; " + shown);
                Equal((nint)3, DesktopLyricNative.SendMessage(handle, 0x21, 0, 0));
                int closes = 0; window.Closed += (_, _) => closes++;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                Equal(1, closes);
                True(!f.Module.IsDesktopLyricsOpen && window.DataContext is null && !DesktopLyricNative.IsWindow(handle),
                    "shortcut destroys the button-owned HWND");
                Equal("打开桌面歌词", AutomationProperties.GetName(button));
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                True(!ReferenceEquals(window, f.Module.DesktopLyricsWindow), "closed HWND is not revived");
                Click((Button)f.Module.DesktopLyricsWindow!.FindName("CloseButton"));
                True(!f.Module.IsDesktopLyricsOpen, "native window close synchronizes the single state");
                Equal("打开桌面歌词", AutomationProperties.GetName(button));
            }));

        yield return Case("desktop lyrics footer keeps unified mode quality and desktop controls inside both widths", () =>
            WithDesktopLyrics((f, _) =>
            {
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var detailFooter = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                foreach (var player in new[] { (MusicPlayerView)f.Module.PlayerView, detailFooter })
                {
                    FrameworkElement surface = ReferenceEquals(player, detailFooter) ? detail : player;
                    // Render owns this viewport; the shown anchor Window must not re-layout the surface.
                    var mode = (Button)player.FindName("ModeButton");
                    var transport = (StackPanel)player.FindName("TransportPanel");
                    Equal(0, transport.Children.IndexOf(mode));
                    // Exercise the widest right-hand chrome without requiring an online account.
                    ((Button)player.FindName("QualityButton")).Visibility = Visibility.Visible;
                    foreach (int width in new[] { 1110, 860 })
                    {
                        Render(surface, width, ReferenceEquals(player, detailFooter) ? 600 : 80);
                        True(Math.Abs(player.ActualWidth - width) < .5, "the actual footer spans the requested viewport");
                        True(mode.Visibility == Visibility.Visible && mode.IsEnabled,
                            "the unified mode entry remains available in the detached render fixture");
                        AssertPlayerBounds(player, width);
                        var transportRight = transport.TranslatePoint(new(transport.ActualWidth, 0), player).X;
                        var progress = (FrameworkElement)player.FindName("Progress");
                        var progressBottom = progress.TranslatePoint(new(0, progress.ActualHeight), player).Y;
                        var names = new[] { "QualityButton",
                            "PlaylistButton", "DesktopLyricsButton", "VolumeButton", "MoreButton" };
                        var options = names.Select(name => (FrameworkElement)player.FindName(name))
                            .Select(control => new Rect(control.TranslatePoint(new(), player), control.RenderSize)).ToArray();
                        foreach (var rect in options)
                            True(rect.Left >= transportRight - .5, "right controls do not overlap the centered transport");
                        var bounds = new[] { new Rect(mode.TranslatePoint(new(), player), mode.RenderSize) }
                            .Concat(options).ToArray();
                        foreach (var rect in bounds)
                        {
                            True(rect.Width > 0 && rect.Height > 0, "all controls have a measured hit area");
                            True(rect.Top >= progressBottom - .5, "controls remain below the seek hit area");
                        }
                        for (int i = 0; i < bounds.Length; i++)
                            for (int j = i + 1; j < bounds.Length; j++)
                                True(bounds[i].Right <= bounds[j].Left + .5 || bounds[j].Right <= bounds[i].Left + .5 ||
                                    bounds[i].Bottom <= bounds[j].Top + .5 || bounds[j].Bottom <= bounds[i].Top + .5,
                                    "infinity quality and desktop controls have distinct hit areas");
                        GeneralAppearanceTests.SavePreview(player,
                            "media-control-footer-max-" + (ReferenceEquals(player, detailFooter) ? "detail-" : "ordinary-") + width,
                            width, 80);
                    }
                }
                return Task.CompletedTask;
            }));

        yield return Case("desktop lyrics follows playback time and shares source translation visibility with full detail", () =>
            WithDesktopLyrics(async (f, owner) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                var line = (MusicDesktopLyricLineView)window.FindName("TopLine");
                var original = (MusicDesktopLyricTextView)line.FindName("PlainText");
                var translation = (MusicDesktopLyricTextView)line.FindName("TranslationText");
                Equal("第一行原文", original.Text); Equal("first source translation", translation.Text);
                f.Playback.Tick(6); await Drain();
                Equal("第二行原文", original.Text); Equal("second source translation", translation.Text);
                var rows = f.Module.Lyrics.ToArray();
                f.Module.PreviewLyric(rows[0]);
                Equal("第二行原文", f.Module.DesktopLyricText);
                GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "media-control-desktop-lyrics",
                    (int)window.ActualWidth, (int)window.ActualHeight);

                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                owner.Content = detail;
                await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
                Render(detail, 1110, 800); await Drain();
                var detailFooter = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                var detailButton = (Button)detailFooter.FindName("DesktopLyricsButton");
                Equal("关闭桌面歌词", AutomationProperties.GetName(detailButton));
                var translated = Visuals(detail).OfType<TextBlock>().Where(text => text.Name == "LyricTranslation").ToArray();
                True(translated.Any(text => text.Text == rows[1].Translation && text.IsVisible), "full detail displays provider translation by default");
                int calls = f.Calls.Log.Count;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation); await Drain();
                True(!f.Module.ShowLyricTranslation && !translation.IsVisible, "desktop translation is collapsed");
                True(translated.All(text => !text.IsVisible), "full detail shares the same switch");
                Equal(calls, f.Calls.Log.Count);
                True(rows.SequenceEqual(f.Module.Lyrics) && ReferenceEquals(rows[0], f.Module.Lyrics[0]),
                    "visibility never rewrites source rows, timing, romanization or words");
                Equal("first source translation", rows[0].Translation);
                Equal(0d, rows[0].Time); Equal(5d, rows[1].Time);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation); await Drain();
                True(f.Module.ShowLyricTranslation && translation.IsVisible, "source translation reappears without a request");
                True(translated.Any(text => text.Text == rows[1].Translation && text.IsVisible), "full detail translation reappears");
                window.Close(); await Drain();
                Equal("打开桌面歌词", AutomationProperties.GetName(detailButton));
                True(f.Module.IsLyrics && f.Module.NeedsLyrics, "closing desktop lyrics leaves the complete player active");
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            }));

        yield return Case("desktop lyrics and full detail receive complete replacement lyric snapshots", () =>
            WithDesktopLyrics(async (f, owner) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                var line = (MusicDesktopLyricLineView)window.FindName("TopLine");
                var original = (MusicDesktopLyricTextView)line.FindName("PlainText");
                var translation = (MusicDesktopLyricTextView)line.FindName("TranslationText");
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                owner.Content = detail;
                await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
                Render(detail, 1110, 800); await Drain();
                f.Playback.Tick(6);
                await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw,
                    text = "[00:00]新第一行\n[00:05]新第二行" });
                f.Calls.TransformNext("music.lyrics.local.get", reply =>
                {
                    var result = JsonNode.Parse(reply.GetRawText())!;
                    result["lines"]![1]!["translation"] = "replacement source translation";
                    return JsonSerializer.SerializeToElement(result);
                });
                var snapshots = new List<string[]>();
                NotifyCollectionChangedEventHandler published = (_, _) =>
                {
                    if (f.Module.Lyrics.Count > 0)
                        snapshots.Add(f.Module.Lyrics.Select(row => row.Text).ToArray());
                };
                f.Module.Lyrics.CollectionChanged += published;
                try
                {
                    await f.Module.RefreshLyricsAsync(); await Drain();
                    Equal(1, snapshots.Count);
                    True(snapshots[0].SequenceEqual(["新第一行", "新第二行"]), "no consumer observes a partial replacement");
                    Equal("新第二行", original.Text);
                    Equal("replacement source translation", translation.Text);
                    True(Visuals(detail).OfType<TextBlock>().Any(text => text.Name == "LyricTranslation" &&
                        text.Text == "replacement source translation" && text.IsVisible),
                        "full detail receives the same provider translation as the desktop consumer");
                }
                finally
                {
                    f.Module.Lyrics.CollectionChanged -= published;
                    f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                }
            }));

        yield return Case("desktop lyrics reports empty and unplayed state without inventing a translation", async () =>
        {
            await WithDesktopLyrics(async (f, _) =>
            {
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                Equal("未在播放", f.Module.DesktopLyricText);
                Equal("", f.Module.DesktopLyricTranslation);
                Equal(0, f.Calls.Count("music.lyrics.lookup"));
            }, withCurrent: false);
            await WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var line = (MusicDesktopLyricLineView)f.Module.DesktopLyricsWindow!.FindName("TopLine");
                var translation = (MusicDesktopLyricTextView)line.FindName("TranslationText");
                True(!translation.IsVisible, "a source without translation leaves no empty second row");
                int calls = f.Calls.Log.Count;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
                Equal("", f.Module.Lyrics[0].Translation); True(!translation.IsVisible, "switching cannot manufacture translation");
                Equal(calls, f.Calls.Log.Count);
                f.Module.Lyrics.Clear(); await Drain();
                Equal("暂无歌词", f.Module.DesktopLyricText);
                await f.Playback.StopAsync(); await Drain();
                Equal("未在播放", f.Module.DesktopLyricText);
            });
        });

        yield return Case("desktop lyrics remains visible when its anchor minimizes and closes when the anchor exits", () =>
            WithDesktopLyrics(async (f, owner) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                var handle = new WindowInteropHelper(window).Handle;
                string before = DescribeDesktopWindow(window);
                owner.WindowState = WindowState.Minimized; await Drain();
                string visibility = "before=" + before + "; after=" + DescribeDesktopWindow(window) +
                    "; anchor=" + DescribeDesktopWindow(owner);
                Console.WriteLine("EVIDENCE desktop-lyrics minimize; " + visibility);
                True(window.IsVisible && DesktopLyricNative.IsWindowVisible(handle) && f.Module.IsDesktopLyricsOpen && window.Owner is null,
                    "minimizing the main window cannot hide independently owned lyrics; " + visibility);
                owner.Close(); await Drain();
                True(!window.IsVisible && !DesktopLyricNative.IsWindow(handle) && !f.Module.IsDesktopLyricsOpen && !f.Module.CanToggleDesktopLyrics,
                    "the explicit anchor Closed event releases the window and its registration");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                True(!f.Module.IsDesktopLyricsOpen && f.Module.Status.Length > 0, "a retired owner cannot silently reopen lyrics");
            }));

        yield return Case("desktop lyrics detaches a replaced owner Closed handler", () =>
            WithDesktopLyrics(async (f, owner) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                var replacement = DesktopLyricOwner();
                try
                {
                    replacement.Show();
                    f.Module.AttachDesktopLyricsOwner(replacement);
                    owner.Close(); await Drain();
                    True(window.IsVisible && ReferenceEquals(window, f.Module.DesktopLyricsWindow),
                        "the former anchor no longer owns a Closed subscription");
                    replacement.Close(); await Drain();
                    True(!f.Module.IsDesktopLyricsOpen && !f.Module.CanToggleDesktopLyrics, "the current owner is detached on exit");
                }
                finally { replacement.Close(); }
            }));

        foreach (bool closeDetail in new[] { false, true })
            yield return Case("desktop lyrics and detail share one read when closing " + (closeDetail ? "detail" : "desktop"), () =>
                WithDesktopLyrics(async (f, _) =>
                {
                    await SaveDesktopLyricFixture(f, translated: false);
                    var hold = f.Calls.HoldNext("music.lyrics.local.get");
                    var desktop = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                    await hold.Arrived.Task;
                    var request = f.Calls.Log.Last(call => call.Method == "music.lyrics.local.get");
                    using var detailRequest = new CancellationTokenSource();
                    var detail = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, detailRequest.Token);
                    try
                    {
                        Equal(1, f.Calls.Count("music.lyrics.local.get"));
                        if (closeDetail)
                        {
                            // Shell owns and retires the nonblocking activation independently of shared lyrics.
                            detailRequest.Cancel();
                            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                        }
                        else f.Module.DesktopLyricsWindow!.Close();
                        True(!request.Token.IsCancellationRequested && f.Module.NeedsLyrics,
                            "closing only one consumer cannot cancel the shared presentation request");
                    }
                    finally { hold.Release(); }
                    await desktop;
                    if (closeDetail) await Cancelled(detail); else await detail;
                    Equal(2, f.Module.Lyrics.Count); Equal(1, f.Calls.Count("music.lyrics.local.get"));
                    f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                }));

        yield return Case("desktop lyrics last consumer close cancels and fences a noncooperative late reply", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                var hold = f.Calls.HoldNext("music.lyrics.local.get");
                var open = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await hold.Arrived.Task;
                var request = f.Calls.Log.Last(call => call.Method == "music.lyrics.local.get");
                f.Module.DesktopLyricsWindow!.Close();
                True(request.Token.IsCancellationRequested && !f.Module.NeedsLyrics, "last visible consumer cancels");
                hold.Release(); await open; await Drain();
                Equal(0, f.Module.Lyrics.Count);
                True(!f.Module.CanCancelLyrics && !f.Module.IsLyrics, "late rows cannot resurrect a consumer");
            }));

        yield return Case("local lyric removal settled after the last consumer closes cannot restart lyric reads", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                True(f.Module.CanRemoveLocalLyrics && !f.Module.IsLyrics, "only the desktop consumer is open");
                int localReads = f.Calls.Count("music.lyrics.local.get");
                int displayReads = f.Calls.Count("music.lyrics.display.get");
                int lookups = f.Calls.Count("music.lyrics.lookup");
                var hold = f.Calls.HoldNext("music.lyrics.local.remove");
                var removal = f.Module.RemoveLocalLyricsAsync();
                try
                {
                    await hold.Arrived.Task;
                    True(!removal.IsCompleted, "the native removal is accepted while its reply is held");
                    f.Module.DesktopLyricsWindow!.Close();
                    True(!f.Module.NeedsLyrics, "the last consumer closes before removal settles");
                }
                finally { hold.Release(); }
                await removal; await Drain();
                Equal(1, f.Calls.Count("music.lyrics.local.remove"));
                Equal(localReads, f.Calls.Count("music.lyrics.local.get"));
                Equal(displayReads, f.Calls.Count("music.lyrics.display.get"));
                Equal(lookups, f.Calls.Count("music.lyrics.lookup"));
                Equal(0, f.Module.Lyrics.Count);
                True(!f.Module.IsDesktopLyricsOpen && !f.Module.CanCancelLyrics && !f.Module.CanRemoveLocalLyrics,
                    "settling removal neither resurrects a consumer nor starts a new lyric request");
            }));

        yield return Case("desktop-only lyrics reload after track and metadata policy changes", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await SaveDesktopNextLyricFixture(f, "[00:00]切歌后的歌词");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.Next);
                await Until(() => f.Module.Lyrics.Count == 1 && f.Module.Lyrics[0].Text == "切歌后的歌词");
                Equal("切歌后的歌词", f.Module.DesktopLyricText);
                True(!f.Module.IsLyrics && f.Module.NeedsLyrics, "desktop demand is distinct from the full-page flag");
                int reads = f.Calls.Count("music.lyrics.local.get");
                var platform = f.Module.MetadataPlatforms.Single(row => row.Platform == "wy");
                await f.Module.SetMetadataEnabledAsync(platform, !platform.Enabled);
                Equal(reads + 1, f.Calls.Count("music.lyrics.local.get"));
                Equal("切歌后的歌词", f.Module.DesktopLyricText);
            }));

        yield return Case("desktop-only track switch rejects the previous song late lyric reply", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await SaveDesktopNextLyricFixture(f, "[00:00]新的播放曲目");
                var published = new List<string>();
                f.Module.Lyrics.CollectionChanged += (_, _) => published.AddRange(f.Module.Lyrics.Select(row => row.Text));
                var hold = f.Calls.HoldNext("music.lyrics.local.get");
                var opening = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await hold.Arrived.Task;
                try
                {
                    await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.Next);
                    True(f.Module.Lyrics.Count == 0, "the old read cannot publish while the replacement waits for its drain");
                }
                finally { hold.Release(); }
                await opening;
                await Until(() => f.Module.Lyrics.FirstOrDefault()?.Text == "新的播放曲目" && !f.Module.CanCancelLyrics);
                Equal("新的播放曲目", f.Module.DesktopLyricText);
                True(!published.Contains("第一行原文") && !published.Contains("第二行原文"),
                    "the old song never publishes after the new occurrence has been selected");
                True(f.Module.IsDesktopLyricsOpen && !f.Module.IsLyrics, "the sole desktop consumer survives song replacement");
            }));

        yield return Case("desktop lyrics preparation closes then cancellation creates a fresh live window", () =>
            WithDesktopLyrics(async (f, owner) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
                var prior = f.Module.DesktopLyricsWindow!;
                prior.Left += 29; prior.Top += 17;
                prior.SetLocked(true); await Drain();
                var state = prior.CapturePresentationState();
                var unlockHandle = new WindowInteropHelper(prior.UnlockWindow!).Handle;
                int closed = 0; prior.Closed += (_, _) => closed++;
                await f.Module.PrepareShutdownAsync();
                Equal(1, closed);
                True(!f.Module.IsDesktopLyricsOpen && !f.Module.CanToggleDesktopLyrics && prior.DataContext is null,
                    "preparation releases the actual window without disposing the playback owner");
                True(!DesktopLyricNative.IsWindow(unlockHandle) && !prior.IsPointerObserverRunning,
                    "shutdown preparation also releases independent unlock and pointer observation");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                True(!f.Module.IsDesktopLyricsOpen, "sealed media admission stays sealed");
                await f.Module.CancelShutdownPreparationAsync();
                True(f.Module.IsDesktopLyricsOpen && !ReferenceEquals(prior, f.Module.DesktopLyricsWindow),
                    "cancel preparation creates a new window rather than reopening the closed resource");
                True(!f.Module.ShowLyricTranslation, "session display preference survives preparation");
                await Drain();
                var restored = f.Module.DesktopLyricsWindow!;
                Equal(state, restored.CapturePresentationState());
                True(restored.UnlockWindow is { IsVisible: true } && !restored.IsPointerObserverRunning,
                    "cancelled exit restores position and locking with a fresh usable unlock HWND");
                await f.Module.PrepareShutdownAsync();
                owner.Close();
                await f.Module.CancelShutdownPreparationAsync();
                True(!f.Module.IsDesktopLyricsOpen && !f.Module.CanToggleDesktopLyrics, "a closed owner revokes pending restoration");
            }));

        foreach (bool abort in new[] { false, true })
            yield return Case("desktop lyrics " + (abort ? "abort" : "dispose") + " releases HWND owner events and pending reads", () =>
                WithDesktopLyrics(async (f, owner) =>
                {
                    await SaveDesktopLyricFixture(f, translated: false);
                    var hold = f.Calls.HoldNext("music.lyrics.local.get");
                    var open = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                    await hold.Arrived.Task;
                    var window = f.Module.DesktopLyricsWindow!;
                    var handle = new WindowInteropHelper(window).Handle;
                    var request = f.Calls.Log.Last(call => call.Method == "music.lyrics.local.get");
                    var shutdown = abort ? f.Module.AbortAsync() : f.Module.DisposeAsync().AsTask();
                    True(!f.Module.IsDesktopLyricsOpen && request.Token.IsCancellationRequested,
                        "shutdown immediately closes lyrics and cancels the owned presentation request");
                    hold.Release();
                    await Task.WhenAll(open, shutdown); await Drain();
                    True(!window.IsVisible && !DesktopLyricNative.IsWindow(handle) &&
                        window.DataContext is null && !f.Module.CanToggleDesktopLyrics,
                        "shutdown cannot retain a live window or anchor");
                    Equal(0, f.Module.Lyrics.Count);
                    await Failure(() => f.Module.CancelShutdownPreparationAsync(), "CLOSED");
                    owner.Close(); await Drain();
                    True(!f.Module.IsDesktopLyricsOpen, "a former owner event cannot revive disposed resources");
                }));
    }

    private static Window DesktopLyricOwner() => new()
    {
        Width = 1110, Height = 800, Left = -32000, Top = -32000,
        ShowActivated = false, ShowInTaskbar = false, Content = new Grid()
    };

    private static async Task WithDesktopLyrics(Func<Fixture, Window, Task> body, bool withCurrent = true)
    {
        await using var f = await Fixture.Create();
        if (withCurrent)
        {
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            f.Playback.Start(); await Until(() => !f.Module.Busy);
        }
        var owner = DesktopLyricOwner();
        try
        {
            owner.Show();
            f.Module.AttachDesktopLyricsOwner(owner);
            await body(f, owner);
        }
        finally { owner.Close(); await Drain(); }
    }

    private static async Task SaveDesktopLyricFixture(Fixture f, bool translated = true)
    {
        await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw,
            text = "[00:00]第一行原文\n[00:05]第二行原文" });
        if (translated)
            f.Calls.TransformNext("music.lyrics.local.get", reply =>
            {
                // Supply an existing provider translation at the public timed-row boundary.
                var result = JsonNode.Parse(reply.GetRawText())!;
                result["lines"]![0]!["translation"] = "first source translation";
                result["lines"]![1]!["translation"] = "second source translation";
                return JsonSerializer.SerializeToElement(result);
            });
    }

    private static async Task SaveDesktopNextLyricFixture(Fixture f, string text)
    {
        // Local text is admitted only for the actual current queue occurrence.
        await f.Module.StepAsync(true);
        await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[1].Raw, text });
        await f.Module.StepAsync(false);
    }

    private static string DescribeDesktopWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Marshal.SetLastPInvokeError(0);
        var style = DesktopLyricNative.GetWindowLongPtr(handle, -16);
        int styleError = Marshal.GetLastPInvokeError();
        Marshal.SetLastPInvokeError(0);
        var extended = DesktopLyricNative.GetWindowLongPtr(handle, -20);
        int extendedError = Marshal.GetLastPInvokeError();
        uint thread = DesktopLyricNative.GetWindowThreadProcessId(handle, out uint process);
        return $"HWND=0x{handle:X}, real={DesktopLyricNative.IsWindow(handle)}, " +
            $"visible={DesktopLyricNative.IsWindowVisible(handle)}, style=0x{style:X}/{styleError}, " +
            $"ex=0x{extended:X}/{extendedError}, owner=0x{DesktopLyricNative.GetWindow(handle, 4):X}, " +
            $"thread={thread}/{DesktopLyricNative.GetCurrentThreadId()}, process={process}/{Environment.ProcessId}, " +
            $"WPF={window.Visibility}/{window.IsVisible}/{window.WindowState}, active={window.IsActive}, " +
            $"dispatcher={window.Dispatcher.HasShutdownStarted}/{window.Dispatcher.HasShutdownFinished}";
    }

    private static class DesktopLyricNative
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, int command);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);
    }
}
