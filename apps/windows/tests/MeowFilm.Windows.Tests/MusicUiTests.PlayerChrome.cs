using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using FontWeights = Microsoft.UI.Text.FontWeights;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Data;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> PlayerChromeCases()
    {
        yield return Case("mounted player metadata artwork and quality badge follow playback with complete geometry", async () =>
        {
            await using var f = await Fixture.Create();
            await SetupOnline(f);
            var collection = await f.Rpc("music.library.create", new { name = "播放器在线元数据", songs = new[] {
                MusicSongFixture.Online("wy", "chrome-first", "第一首在线歌曲", "第一位艺人", cover: "https://images.example.test/chrome-first.png"),
                MusicSongFixture.Online("wy", "chrome-second", "第二首在线歌曲", "第二位艺人", cover: "https://images.example.test/chrome-second.png") } });
            await f.Module.NavigateAsync("playlist/" + collection.GetProperty("collection").GetProperty("id").GetString(), default);
            f.Module.SelectedSong = f.Module.Songs[0];
            var player = (MusicPlayerView)f.Module.PlayerView;
            var record = (MusicRecordView)((Button)player.FindName("ArtworkButton")).Content;
            var artwork = new PlayerArtworkFixture(); ArtworkScope.SetLoader(record, artwork);
            player.Width = 1110; player.Height = 80;
            using var host = new DesktopTestUi.Host(player, 1126, 160); await Drain();
            await Until(() => player.IsLoaded && player.XamlRoot is not null);
            await f.Module.PlaySelectionAsync(); await Drain(); player.UpdateLayout();
            async Task VerifyCurrentSong()
            {
                var song = (await f.Rpc("music.queue.get")).GetProperty("current").GetProperty("song");
                var normalized = SongRow.Read(song);
                Equal(song.GetProperty("title").GetString(), normalized.Title); Equal(normalized.Title, f.Module.CurrentTitle);
                Equal(song.GetProperty("artist").GetString(), normalized.Artist); Equal(normalized.Artist, f.Module.CurrentArtist);
                Equal(song.GetProperty("cover_url").GetString(), normalized.Cover); Equal(normalized.Cover, f.Module.CurrentArtwork);
                Equal(normalized.Id, f.Module.CurrentSongId);
                True(normalized.Cover.Length > 0 && normalized.IsOnline, "the real queue retains admitted online metadata and artwork");
            }
            await VerifyCurrentSong();
            var metadata = ((StackPanel)player.FindName("MetadataText")).Children.OfType<TextBlock>().ToArray();
            True(f.Module.CurrentTitle.Length > 0 && f.Module.CurrentArtist.Length > 0, "playing owner has metadata");
            Equal(f.Module.CurrentTitle, metadata[0].Text); Equal(f.Module.CurrentArtist, metadata[1].Text);
            True(metadata.All(text => text.ActualWidth > 0 && text.ActualHeight > 0), "mounted footer realizes title and artist");
            Equal(f.Module.CurrentArtwork, record.Address); Equal(f.Module.CurrentArtworkIdentity, record.ArtworkIdentity);
            await Until(() => ((ArtworkImage)record.FindName("Cover")).Source is not null);
            var quality = (Button)player.FindName("QualityButton"); var badge = (Border)quality.Content;
            True(f.Module.HasQualityButton && quality.Visibility == Visibility.Visible && quality.ActualWidth > 0 && quality.ActualHeight > 0,
                "accepted online playback realizes its quality control");
            var bounds = DesktopTestUi.Bounds(badge, quality);
            True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= quality.ActualWidth && bounds.Bottom <= quality.ActualHeight,
                $"all four quality border edges fit the actual button: badge={bounds}, button={quality.ActualWidth}x{quality.ActualHeight}");
            var queue = (Button)player.FindName("QueueButton");
            var queueGlyph = (Microsoft.UI.Xaml.Shapes.Path)queue.Content;
            Equal(19d, queueGlyph.Width); Equal(19d, queueGlyph.Height);
            True(Math.Abs(queueGlyph.StrokeThickness - 1.3) < .000001,
                $"queue uses the original stroke thickness: {queueGlyph.StrokeThickness:R}");
            Color BrushColor(Brush brush) => ((SolidColorBrush)brush).Color;
            void VerifyQueuePalette(Button button)
            {
                Equal(BrushColor(button.Foreground), BrushColor(((Microsoft.UI.Xaml.Shapes.Path)button.Content).Stroke));
                Equal(BrushColor((Brush)DesktopVisual.Resource(button, "Capability.Player.Foreground")), BrushColor(button.Foreground));
            }
            VerifyQueuePalette(queue);
            var volume = (Button)player.FindName("VolumeButton");
            Equal(BrushColor(volume.Foreground), BrushColor(((FontIcon)volume.Content).Foreground));
            await NativeDesktopCapture.SaveAsync(player, "presentation-player-footer");
            string priorTitle = f.Module.CurrentTitle, priorArtwork = f.Module.CurrentArtwork;
            await f.Module.StepAsync(true); await Drain(); await VerifyCurrentSong();
            True(f.Module.CurrentTitle != priorTitle && f.Module.CurrentArtwork != priorArtwork, "stepping adopts the next online song's metadata and artwork");
            Equal(f.Module.CurrentTitle, metadata[0].Text); Equal(f.Module.CurrentArtist, metadata[1].Text);
            Equal(f.Module.CurrentArtwork, record.Address);
            host.Window.Content = null; await Drain();
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var detailRecord = (MusicRecordView)detail.FindName("DetailRecord");
            ArtworkScope.SetLoader(detailRecord, artwork);
            using var detailHost = new DesktopTestUi.Host(detail, 1110, 800); await Drain(); detail.UpdateLayout();
            await Until(() => detail.IsLoaded && detail.XamlRoot is not null);
            Equal(f.Module.CurrentTitle, ((TextBlock)detail.FindName("DetailTitle")).Text);
            True(Visuals((FrameworkElement)detail.FindName("DetailMetadata")).OfType<TextBlock>().Any(text => text.Text == f.Module.CurrentArtist),
                "full player artist binding receives the same current owner");
            Equal(f.Module.CurrentArtwork, detailRecord.Address); Equal(f.Module.CurrentArtworkIdentity, detailRecord.ArtworkIdentity);
            await Until(() => ((ArtworkImage)detailRecord.FindName("Cover")).Source is not null);
            var detailFooter = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
            var detailFooterText = ((StackPanel)detailFooter.FindName("MetadataText")).Children.OfType<TextBlock>().ToArray();
            Equal(f.Module.CurrentTitle, detailFooterText[0].Text); Equal(f.Module.CurrentArtist, detailFooterText[1].Text);
            var detailQueue = (Button)detailFooter.FindName("QueueButton"); var detailVolume = (Button)detailFooter.FindName("VolumeButton");
            VerifyQueuePalette(detailQueue);
            Equal(BrushColor(detailVolume.Foreground), BrushColor(((FontIcon)detailVolume.Content).Foreground));
            await NativeDesktopCapture.SaveAsync(detail, "presentation-player-detail");
        });
        yield return Case("player native controls and flyouts use system regular text without replacing icons", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var player = (MusicPlayerView)f.Module.PlayerView;
            using var host = new DesktopTestUi.Host(player, 1110, 80); await Drain();
            var quality = (TextBlock)((Border)((Button)player.FindName("QualityButton")).Content).Child;
            var desktop = (TextBlock)((Button)player.FindName("DesktopLyricsButton")).Content;
            Equal(10d, quality.FontSize); Equal(15d, desktop.FontSize);
            foreach (var name in new[] { "PlaylistButton", "VolumeButton", "MoreButton" })
                True(((Button)player.FindName(name)).Content is FontIcon { FontFamily.Source: "Segoe Fluent Icons" },
                    "native symbol font remains separate from ordinary text");
            var playlist = (Flyout)player.FindName("PlaylistPopup");
            Click((Button)player.FindName("PlaylistButton")); await Until(() => playlist.IsOpen); await Drain();
            var playlistButtons = Visuals((FrameworkElement)playlist.Content).OfType<Button>().ToArray();
            True(playlistButtons.Length > 0, "real playlist flyout has body-text choices");
            foreach (var text in playlistButtons.SelectMany(Visuals).OfType<TextBlock>().Append(quality).Append(desktop))
            {
                Equal(FontWeights.Normal, text.FontWeight);
                True(text.FontFamily.Source != "Segoe Fluent Icons", "ordinary labels do not use the symbol font");
            }
            PlayerMenuInput.For(player).CloseCurrent(); await Until(() => !playlist.IsOpen);
            Click((Button)player.FindName("MoreButton"));
            var more = (MenuFlyout)player.FindName("MoreMenu");
            await Until(() => more.IsOpen); MusicTestUi.Invoke(more.Items.OfType<MenuFlyoutItem>().First());
            var rate = (Flyout)player.FindName("RatePopup"); await Until(() => rate.IsOpen); await Drain();
            var buttons = Visuals((FrameworkElement)rate.Content).OfType<Button>().ToArray();
            True(buttons.Select(button => (string)button.Content).SequenceEqual(new[] { "−", "1.0×", "+" }),
                "the three rate actions retain their real labels");
            Equal(12d, buttons.Single(button => Equals(button.Content, "1.0×")).FontSize);
            True(buttons.Where(button => !Equals(button.Content, "1.0×")).All(button => button.FontSize == 16 && button.FontWeight == FontWeights.Normal),
                "rate adjustment symbols retain their sizes and regular text");
            player.Deactivate(); await Until(() => !rate.IsOpen);
        });
        yield return Case("player mode dismissal cannot open detail and real unload retires its gesture owner", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var player = (MusicPlayerView)f.Module.PlayerView;
            using var host = new DesktopTestUi.Host(player, 1110, 80); await Drain();
            int requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) => { if (args.Surface == DesktopGlobalSurface.PlayerDetail) requests++; };
            var hit = DesktopTestUi.Hit(player, new Point(300, 10)) ?? throw new InvalidOperationException("No native blank hit");
            var menu = (MenuFlyout)player.FindName("ModeMenu");
            Click((Button)player.FindName("ModeButton")); await Until(() => menu.IsOpen);
            player.BeginSurfaceGesture(hit); menu.Hide(); await Until(() => !menu.IsOpen);
            var dismiss = player.EndSurfaceGesture(hit); await dismiss.Work;
            True(!dismiss.Handled, "a gesture which dismisses the native menu cannot open detail"); Equal(0, requests);
            player.BeginSurfaceGesture(hit);
            var next = player.EndSurfaceGesture(hit); await next.Work;
            True(next.Handled, "the next independent blank gesture is admitted"); Equal(1, requests);
            player.BeginSurfaceGesture(hit); host.Window.Content = null; await Drain();
            var retired = player.EndSurfaceGesture(hit); await retired.Work;
            True(!retired.Handled && !player.IsLoaded, "real unload retires the gesture"); Equal(1, requests);
            True(PlayerInput(player) is null, "view drops its window-scoped input owner");
        });
        yield return Case("player native menu and queue dismissal fence blank gestures without fabricated input", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var player = (MusicPlayerView)f.Module.PlayerView;
            using var host = new DesktopTestUi.Host(player, 1110, 80); await Drain();
            int requests = 0, dismissed = 0;
            f.Module.GlobalSurfaceRequested += (_, args) => { if (args.Surface == DesktopGlobalSurface.PlayerDetail) requests++; };
            PlayerSurfaceInput.AddDismissRequestedHandler(player, (_, _) => dismissed++);
            var blank = DesktopTestUi.Hit(player, new Point(300, 10))!;
            foreach (var pair in new[] { ("PlaylistButton", "PlaylistPopup"), ("VolumeButton", "VolumePopup"), ("MoreButton", "MoreMenu"), ("ModeButton", "ModeMenu") })
            {
                Click((Button)player.FindName(pair.Item1));
                var flyout = (FlyoutBase)player.FindName(pair.Item2); await Until(() => flyout.IsOpen);
                player.BeginSurfaceGesture(blank); PlayerMenuInput.For(player).CloseCurrent(); await Until(() => !flyout.IsOpen);
                await player.EndSurfaceGesture(blank).Work; Equal(0, requests);
            }
            PlayerSurfaceInput.SetQueueOpen(player, true);
            player.BeginSurfaceGesture(blank); Equal(1, dismissed);
            PlayerSurfaceInput.SetQueueOpen(player, false);
            await player.EndSurfaceGesture(blank).Work; Equal(0, requests);
            player.BeginSurfaceGesture((Button)player.FindName("PlayButton"));
            await player.EndSurfaceGesture(blank).Work; Equal(0, requests);
            player.BeginSurfaceGesture(blank);
            var consumed = player.EndSurfaceGesture(blank, handled: true); await consumed.Work;
            True(!consumed.Handled, "another native control's consumed release cannot open detail"); Equal(0, requests);
            player.BeginSurfaceGesture(blank); await player.EndSurfaceGesture(blank).Work; Equal(1, requests);
            player.BeginSurfaceGesture(blank); player.Visibility = Visibility.Collapsed; await Drain();
            await player.EndSurfaceGesture(blank).Work; Equal(1, requests);
        });
        yield return Case("queue whole-row hover focus and high contrast retain native recycling and real actions", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var queue = (MusicQueueView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
            using var host = new DesktopTestUi.Host(queue, 386, 420); await Drain();
            var list = (ListView)queue.FindName("QueueList");
            await Until(() => list.ContainerFromIndex(1) is ListViewItem);
            var item = (ListViewItem)list.ContainerFromIndex(1);
            var row = Visuals(item).OfType<MusicQueueRowView>().Single();
            var play = row.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 0);
            var remove = row.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 1);
            var rowContent = (Grid)play.Content;
            var coverSlot = rowContent.Children.OfType<Grid>().Single();
            var overlay = coverSlot.Children.OfType<Border>().Single();
            var boundRow = f.Module.QueueSurfaceRows[1];
            True(boundRow.Title.Length > 0 && boundRow.Artist.Length > 0 && boundRow.Duration != "—",
                "the queue owner publishes complete repository song metadata");
            var title = rowContent.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().First();
            await Until(() => title.Text == boundRow.Title);
            var rowLabels = Visuals(row).OfType<TextBlock>().Select(text => text.Text).ToArray();
            True(rowLabels.Contains(boundRow.Artist) && rowLabels.Contains(boundRow.Duration),
                "the native row binds its artist and duration rather than showing only a default dash");
            var artwork = coverSlot.Children.OfType<ArtworkImage>().Single();
            Equal(boundRow.Cover, artwork.Address); Equal(boundRow.Song.Id, artwork.DisplayIdentity);
            await NativeDesktopCapture.SaveAsync(queue, "music-queue-metadata");
            Equal(0d, overlay.Opacity); CardMotion.SetProgress(row, 1); await Drain(); Equal(1d, overlay.Opacity);
            True(((SolidColorBrush)row.Background).Color == ((SolidColorBrush)DesktopVisual.Resource(queue, "MeowFilm.Brush.ItemHover")).Color,
                "whole row including remove column uses the hover color");
            var bounds = DesktopTestUi.Bounds(remove, row);
            True(bounds.X >= 0 && bounds.Y >= 0 && bounds.Right <= row.ActualWidth && bounds.Bottom <= row.ActualHeight,
                "delete hit geometry stays in the whole-row highlight");
            True(Visuals(list).OfType<ItemsStackPanel>().Any(), "native list recycling remains installed");
            CardMotion.SetProgress(row, 0); await Drain(); Equal(0d, overlay.Opacity);
            True(play.Focus(FocusState.Programmatic) && DesktopTestUi.HasFocus(row), "actual native focus is within the row");
            await Until(() => overlay.Opacity > .99);
            DesktopTheme.SetIsHighContrast(queue, true); CardMotion.SetProgress(row, 0); await Drain();
            var settings = new global::Windows.UI.ViewManagement.UISettings();
            var texts = Visuals(row).OfType<TextBlock>().ToArray();
            True(texts.Length >= 3 && texts.All(text => ((SolidColorBrush)text.Foreground).Color == settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)),
                "unhighlighted high contrast uses real system window text");
            CardMotion.SetProgress(row, 1); await Drain();
            True(((SolidColorBrush)row.Background).Color == settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Highlight) &&
                texts.All(text => ((SolidColorBrush)text.Foreground).Color == settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText)) &&
                ((SolidColorBrush)remove.Foreground).Color == settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText),
                "focus palette includes title artist duration and removal");
            var current = Visuals((ListViewItem)list.ContainerFromIndex(0)).OfType<MusicQueueRowView>().Single();
            True(current.DataContext is MusicQueueSurfaceRow { IsCurrent: true } currentRow &&
                ReferenceEquals(currentRow, f.Module.QueueSurfaceRows[0]), "the current queue occurrence remains the row's real owner");
            var currentContent = (Grid)current.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 0).Content;
            Equal(1d, currentContent.Children.OfType<Grid>().Single().Children.OfType<Border>().Single().Opacity);
            True(Visuals(current).OfType<TextBlock>().All(text => text.FontWeight == FontWeights.Normal),
                "current row has a non-color playing indicator and regular text");
            DesktopTheme.SetIsHighContrast(queue, false);
            string entry = ((MusicQueueSurfaceRow)play.Tag).EntryId;
            int opens = f.Playback.Opens.Count;
            Click(play); await SettleQueue(f); Equal(opens + 1, f.Playback.Opens.Count);
            var playedRow = f.Module.QueueSurfaceRows.Single(value => value.EntryId == entry);
            await Until(() => list.ContainerFromItem(playedRow) is ListViewItem);
            var playedView = Visuals((ListViewItem)list.ContainerFromItem(playedRow)).OfType<MusicQueueRowView>().Single();
            var currentRemove = playedView.Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 1);
            True(ReferenceEquals(currentRemove.Tag, playedRow), "current owner row remains the real action target");
            int count = f.Module.QueueSurfaceRows.Count, removals = f.Calls.Count("music.queue.remove");
            CardMotion.SetProgress(playedView, 1);
            await Drain(); Click(currentRemove); await SettleQueue(f);
            Equal(removals + 1, f.Calls.Count("music.queue.remove"));
            Equal(count - 1, f.Module.QueueSurfaceRows.Count);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("player real unload releases root hooks and queue dismissal reaches the same shell root", async () =>
        {
            await using var f = await Fixture.Create();
            var player = (MusicPlayerView)f.Module.PlayerView;
            using var host = new DesktopTestUi.Host(player, 1110, 80); await Drain();
            var input = PlayerInput(player) ?? throw new InvalidOperationException("No real root input owner");
            int dismiss = 0; PlayerSurfaceInput.AddDismissRequestedHandler(player, (_, _) => dismiss++);
            PlayerSurfaceInput.SetQueueOpen(player, true); True(PlayerSurfaceInput.GetQueueOpen(player), "queue fact belongs to actual XamlRoot");
            input.Press(false); Equal(0, dismiss); input.Press(true); Equal(1, dismiss);
            True(!input.Release(true), "queue dismissal cannot open detail");
            host.Window.Content = null; await Drain();
            True(!player.IsLoaded && PlayerInput(player) is null, "real unload drops the view subscription");
            input.Press(true); True(!input.Release(true), "retained retired owner cannot reopen"); Equal(1, dismiss);
        });
        yield return Case("player ordinary mode and heartbeat eligibility follow the actual recording and reject retired menu actions", async () =>
        {
            await using var f = await Fixture.Create();
            var footer = (MusicPlayerView)f.Module.PlayerView;
            using var host = new DesktopTestUi.Host(footer, 1110, 80); await Drain();
            var mode = (Button)footer.FindName("ModeButton"); var menu = (MenuFlyout)footer.FindName("ModeMenu");
            async Task Open()
            {
                PlayerMenuInput.For(footer).CloseCurrent(); await Until(() => !menu.IsOpen); await Drain();
                Click(mode); await Until(() => menu.IsOpen); await Drain();
            }
            Equal(f.Module.PlaybackModeText, Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(mode));
            True(!f.Module.HasPlaybackFm && !f.Module.CanHeartbeatCurrent, "no recording implies no current-song heartbeat");
            await Open(); Equal(0, f.Calls.Count("music.queue.mode"));
            True(menu.Items.OfType<ToggleMenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[] { "列表循环", "单曲循环", "随机播放" }),
                "native ordinary menu exposes exactly the global loop choices");
            True(menu.Items.All(item => item.IsEnabled), "global loop choices do not depend on a source");
            var random = menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "随机播放");
            MusicTestUi.Invoke(random); await Until(() => f.Module.Mode == 2 && !f.Module.Busy && !menu.IsOpen);
            Equal(1, f.Calls.Count("music.queue.mode")); await Open();
            True(menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "随机播放").IsChecked, "saved choice is selected");
            MusicTestUi.Invoke(random); await Drain(); Equal(1, f.Calls.Count("music.queue.mode"));
            MusicTestUi.Invoke(menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "单曲循环"));
            await Until(() => f.Module.Mode == 3 && !f.Module.Busy);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); await Open();
            True(!f.Module.CanHeartbeatCurrent && menu.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.Text is not ("心动模式" or "推荐模式")),
                "local recording cannot be sent to platform heartbeat or recommendation");
            var retired = menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "列表循环");
            host.Window.Content = null; await Drain(); True(!menu.IsOpen, "unload closes the real menu");
            MusicTestUi.Invoke(retired); await Drain(); Equal(2, f.Calls.Count("music.queue.mode"));
        });
        yield return Case("player first opening presents its base while lyrics and artwork load independently", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var text = string.Join('\n', Enumerable.Range(0, 16).Select(i => $"[{i * 5 / 60:00}:{i * 5 % 60:00}]Prepared lyric {i}"));
            await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw, text });
            await f.Playback.SeekAsync(40);
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            try
            {
                shell.PrepareForRender();
                shell.Resize(1110, 800); shell.Show(); await Drain();
                var root = DesktopTestUi.WindowRoot(shell);
                var pendingLyrics = f.Calls.HoldNext("music.lyrics.local.get");
                var playerSurface = (Grid)DesktopTestUi.WindowRoot(shell).FindName("PlayerDetailSurface");
                await f.Module.ShowLyricsAsync();
                await pendingLyrics.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(Visibility.Visible, playerSurface.Visibility);
                var detail = (MusicPlayerDetailView)((ContentControl)DesktopTestUi.WindowRoot(shell).FindName("PlayerDetailHost")).Content;
                var title = (TextBlock)detail.FindName("DetailTitle");
                Equal(f.Module.CurrentTitle, title.Text);
                Equal(26d, title.FontSize);
                Equal(FontWeights.Normal, title.FontWeight);
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                var record = (MusicRecordView)detail.FindName("DetailRecord");
                var cover = (ArtworkImage)record.FindName("Cover");
                root.UpdateLayout(); await Drain();
                True(cover.IsLoaded && DesktopVisual.IsVisible(cover) && cover.ActualWidth > 0 && cover.ActualHeight > 0,
                    "newly presented artwork has completed its real host layout");
                Equal("加载中…", ((TextBlock)detail.FindName("LyricState")).Text);
                True(!Visuals(detail).OfType<NoticeOutlet>().Any(), "normal lyric states stay in the lyric region");
                var loader = new HeldPlayerArtwork(); ArtworkScope.SetLoader(record, loader);
                record.Address = "fixture://prepared-cover";
                await Until(() => loader.Calls.Count == 1);
                True(((Image)cover.Children[0]).Source is null, "missing cover leaves the base visible");
                var lease = await new PlayerArtworkFixture().AcquireAsync("fixture://prepared-cover");
                loader.Calls[0].Completion.SetResult(lease);
                await Until(() => ReferenceEquals(lease.Image.GetSource(cover.DispatcherQueue), ((Image)cover.Children[0]).Source));
                Equal("加载中…", f.Module.LyricStatus);
                pendingLyrics.Release();
                await Until(() => f.Module.Lyrics.Count == 16 && !f.Module.CanCancelLyrics);
                root.UpdateLayout(); await Drain();
                Equal("", ((TextBlock)detail.FindName("LyricState")).Text);
                True(ReferenceEquals(lease.Image.GetSource(cover.DispatcherQueue), ((Image)cover.Children[0]).Source), "independent artwork is not refetched after lyrics");
                Equal(1, loader.Calls.Count);
                Equal(0, Visuals(footer).OfType<MusicRecordView>().Count());
                Equal(1, Visuals(f.Module.PlayerView).OfType<MusicRecordView>().Count());
                var metadata = (StackPanel)footer.FindName("MetadataText");
                var panel = (Grid)footer.FindName("MetadataPanel");
                True(Math.Abs(metadata.TransformToVisual(panel).TransformPoint(new Point()).X) < .5, "removed disc leaves no reserved column");
                var lateLyrics = f.Calls.HoldNext("music.lyrics.local.get");
                var reading = f.Module.RefreshLyricsAsync();
                await lateLyrics.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                record.Address = "fixture://late-cover";
                await Until(() => loader.Calls.Count == 2);
                await f.Module.OpenPlayerDetailAsync();
                await Until(() => playerSurface.Visibility == Visibility.Collapsed);
                True(((PlayerArtworkLease)lease).Disposed, "closing releases the image on actual unmount");
                True(loader.Calls[1].Token.IsCancellationRequested, "closing cancels pending artwork without awaiting it");
                var lateImage = await new PlayerArtworkFixture().AcquireAsync("fixture://late-cover");
                loader.Calls[1].Completion.SetResult(lateImage);
                lateLyrics.Release(); await reading; await Drain();
                Equal(Visibility.Collapsed, playerSurface.Visibility); Equal(0, f.Module.Lyrics.Count);
                True(((PlayerArtworkLease)lateImage).Disposed, "late artwork never acquires a closed presentation");
                record.Address = "";
                await f.Module.ShowLyricsAsync();
                await Until(() => playerSurface.Visibility == Visibility.Visible && f.Module.Lyrics.Count == 16);
                await f.Module.OpenPlayerDetailAsync();
                await Until(() => playerSurface.Visibility == Visibility.Collapsed);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
        yield return Case("player artwork replacement and cancellation stay independent of pending lyrics", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var record = (MusicRecordView)detail.FindName("DetailRecord");
            var cover = (ArtworkImage)record.FindName("Cover");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArtworkScope.SetLoader(record, new CallbackArtworkLoader((address, token) =>
            {
                if (address == "fixture://first") return new PlayerArtworkFixture().AcquireAsync(address, token);
                Equal("fixture://second", address); entered.TrySetResult(); return completed.Task;
            }));
            record.Address = "fixture://first";
            using var source = new DesktopTestUi.Host(new Grid(), 1110, 800);
            source.Mount(detail);
            detail.Measure(new(1110, 800)); detail.Arrange(new(0, 0, 1110, 800)); detail.UpdateLayout();
            var lyrics = f.Calls.HoldNext("music.lyrics.local.get");
            var preparing = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            await lyrics.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            record.Address = "fixture://second";
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            True(!preparing.IsCompleted, "current image starts while lyrics are pending");
            lyrics.Release(); await f.Module.RestoreLyricsAsync();
            True(((Image)cover.Children[0]).Source is null, "lyric completion does not wait for the current cover");
            var image = await new PlayerArtworkFixture().AcquireAsync("fixture://second");
            completed.SetResult(image);
            await preparing;
            await Until(() => ReferenceEquals(image.Image.GetSource(cover.DispatcherQueue), ((Image)cover.Children[0]).Source));
            True(ReferenceEquals(image.Image.GetSource(cover.DispatcherQueue), ((Image)cover.Children[0]).Source), "prepared state uses the current cover");
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); source.Window.Content = null;
            await Drain();
            True(((PlayerArtworkLease)image).Disposed, "retirement releases the current cover");
        });
        yield return Case("ordinary player footer reveals changes to its host backdrop", async () =>
        {
            await using var f = await Fixture.Create();
            var host = new Border { Child = f.Module.PlayerView };
            DesktopTheme.SetIsHighContrast(host, false);
            foreach (var backdrop in new[] { (new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray)), (new SolidColorBrush(Microsoft.UI.Colors.Linen)) })
            {
                host.Background = backdrop;
                Render(host, 1110, 80);
                var bitmap = await DesktopTestUi.Render(host, 1);
                foreach (var x in new[] { 1, 1108 })
                {
                    var pixel = new byte[4];
                    Array.Copy(bitmap.Pixels, ((int)(40) * bitmap.Width + (int)(x)) * 4, pixel, 0, 4);
                    True(pixel.SequenceEqual(new[] { backdrop.Color.B, backdrop.Color.G, backdrop.Color.R, backdrop.Color.A }),
                        "footer blank areas expose the current host color");
                }
            }
        });
        yield return Case("footer rail touches top edge without a second rule", async () =>
        {
            await using var f = await Fixture.Create();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var progress = (SeekBar)footer.FindName("Progress");
            Equal(0d, progress.TransformToVisual(footer).TransformPoint(new Point()).Y);
            True(progress.RailAtTop, "no inset track");
            var bar = new SeekBar { Maximum = 100, Value = 50, Height = 16, RailAtTop = true };
            var host = new Border { Background = (new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray)), Child = bar };
            async Task<byte[]> Pixels()
            {
                host.Measure(new(400, 16)); host.Arrange(new(0, 0, 400, 16)); host.UpdateLayout();
                var bitmap = await DesktopTestUi.Render(host, 1); var bytes = new byte[400 * 16 * 4]; bitmap.Pixels.CopyTo(bytes, 0); return bytes;
            }
            bar.PlayedBrush = (new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue));
            var first = await Pixels();
            True(!first.AsSpan(300 * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 300) * 4, 4)), "ordinary faint track at top");
            True(!first.AsSpan(100 * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 100) * 4, 4)), "played part paints at y zero");
            bar.PlayedBrush = (new SolidColorBrush(Microsoft.UI.Colors.SandyBrown));
            var second = await Pixels();
            True(!first.AsSpan(100 * 4, 4).SequenceEqual(second.AsSpan(100 * 4, 4)), "cover-driven brush is consumed");
            True(first.AsSpan(300 * 4, 4).SequenceEqual(second.AsSpan(300 * 4, 4)), "unplayed stays neutral");
            True(first.AsSpan((4 * 400 + 300) * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 300) * 4, 4)), "no second lower line");
        });
        yield return Case("seek rail is continuous at rest active disabled and high contrast across DPI", async () =>
        {
            await using var f = await Fixture.Create();
            var bar = new SeekBar { Maximum = 180, Value = 90 };
            var host = new Border { Background = (new SolidColorBrush(Microsoft.UI.Colors.White)), Child = bar, Width = 400, Height = 8 };
            Render(host, 400, 8); await Drain();
            foreach (var highContrast in new[] { false, true })
            foreach (var enabled in new[] { false, true })
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                DesktopTheme.SetIsHighContrast(host, highContrast); bar.IsEnabled = enabled;
                host.Background = (new SolidColorBrush(Microsoft.UI.Colors.White));
                host.Measure(new(400, 8)); host.Arrange(new(0, 0, 400, 8)); host.UpdateLayout();
                var bitmap = await DesktopTestUi.Render(host, (96 * scale) / 96d); int width = bitmap.Width, stride = width * 4;
                var pixels = new byte[stride * bitmap.Height]; bitmap.Pixels.CopyTo(pixels, 0);
                // A hidden Thumb formerly left 8 DIP uncovered. Both rail segments
                // must meet at exactly 50%, including their 0% and 100% endpoints.
                for (int x = 1; x < width - 1; x++)
                {
                    int i = (int)(4 * scale) * stride + x * 4, sample = (int)(4 * scale) * stride + (x < width / 2 ? 1 : width - 2) * 4;
                    True(pixels.AsSpan(i, 4).SequenceEqual(pixels.AsSpan(sample, 4)), "continuous rail without thumb hole or black seam");
                }
                var rail = Visuals(bar).OfType<Canvas>().Single();
                Equal(7, rail.Children.Count);
                True(Visuals(bar).OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().All(rectangle => ReferenceEquals(VisualTreeHelper.GetParent(rectangle), rail)),
                    "the single native rail canvas owns every painted segment");
                Equal(0d, bar.ValueAt(-20)); Equal(180d, bar.ValueAt(420)); Equal(90d, bar.ValueAt(200));
            }
            // Explicit active gesture drives a real animation clock; verify the
            // settled rail is 8 DIP rather than checking a style setter only.
            bar.IsEnabled = true; DesktopTheme.SetIsHighContrast(host, false);
            bar.BeginScrub(); await Task.Delay(170); await Drain();
            Render(host, 400, 8);
            bar.CancelScrub(); await Task.Delay(170); await Drain();
            Render(host, 400, 8);
        });
        yield return Case("footer hover paints a thicker rail and upper shade while its white capsule follows playback", async () =>
        {
            var bar = new SeekBar { Maximum = 100, Value = 25, Height = 16, RailAtTop = true,
                PlayedBrush = (new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)), Margin = new Thickness(0, 32, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { Width = 400, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = (new SolidColorBrush(Microsoft.UI.Colors.White)) }; root.Children.Add(bar);
            root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MeowFilm.Capabilities/Presentation/CapabilityTheme.xaml", UriKind.Absolute) });
            using var source = new DesktopTestUi.Host(root, 480, 200);
            await Until(() => root.IsLoaded && root.XamlRoot is not null);
            root.UpdateLayout(); await Drain();
            var barBounds = bar.TransformToVisual(root).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
            True(barBounds.Top >= 0 && barBounds.Bottom <= root.ActualHeight, "the full seek surface fits inside the rendered host");
            var railCenter = bar.TransformToVisual(root).TransformPoint(new Point(0, bar.RailOrigin + 1));
            var resting = await CaptureSeekFrame(root, 1);
            bar.SetPointerWithin(true);
            await Task.Delay(160); await Drain(); root.UpdateLayout();
            var active = await CaptureSeekFrame(root, 1);
            // Inspect the actual capsule and update its local input coordinate, never the system pointer.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var tip = (Border)typeof(SeekBar).GetField("preview", flags)!.GetValue(bar)!;
            var tipPosition = (TranslateTransform)tip.RenderTransform;
            double TipLeft() => Canvas.GetLeft(tip) + tipPosition.X;
            var tipText = (TextBlock)tip.Child;
            var pointer = typeof(SeekBar).GetField("pointerX", flags)!;
            var update = typeof(SeekBar).GetMethod("UpdatePreview", flags)!;
            double HoverAt(double x) { pointer.SetValue(bar, x); update.Invoke(bar, new object?[] { null }); return TipLeft() + tip.DesiredSize.Width / 2; }
            double left = HoverAt(40), right = HoverAt(360);
            True(tip.Visibility == Visibility.Visible && Math.Abs(left - 100) < 1 && Math.Abs(right - 100) < 1,
                "hovering at either end of a paused track keeps the capsule centered on the 25% playback head");
            Equal("00:25 / 01:40", tipText.Text);
            Equal(Colors.White, ((SolidColorBrush)tip.Background).Color); Equal(Colors.Black, ((SolidColorBrush)tipText.Foreground).Color);
            True(tip.CornerRadius.TopLeft >= 16, "actual retained visual is a white capsule");
            double fixedTop = Canvas.GetTop(tip);
            for (int offset = -3; offset <= 3; offset++)
                True(SeekPixel(active, railCenter.X + 50, railCenter.Y + offset).R < 240, "expanded played rail is visibly thicker than five DIP");
            Equal(Colors.White, SeekPixel(resting, railCenter.X + 50, railCenter.Y + 3));
            True(SeekPixel(active, railCenter.X + 200, railCenter.Y - 9).R < SeekPixel(resting, railCenter.X + 200, railCenter.Y - 9).R,
                "shade paints above the rail, not only inside its hit area");
            True(SeekPixel(active, railCenter.X + 200, railCenter.Y - 21).R > SeekPixel(active, railCenter.X + 200, railCenter.Y - 9).R,
                "upper shade fades smoothly away from the rail");
            bar.PresentationKey = "hover-track"; bar.PlaybackRate = 1; bar.IsPlaying = true;
            double before = tipPosition.X; await Task.Delay(180); await Drain();
            if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled) True(tipPosition.X > before, "the native presentation clock moves both head and capsule without new media ticks");
            Equal(25d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(75); update.Invoke(bar, new object?[] { null });
            Equal("01:15 / 01:40", tipText.Text);
            True(Math.Abs(TipLeft() + tip.DesiredSize.Width / 2 - 300) < 1, "scrubbing uses the target head");
            Equal(fixedTop, Canvas.GetTop(tip));
            True(ToolTipService.GetToolTip(bar) is null, "capsule has no competing native tooltip placement clock");
            bar.CancelScrub(); source.Window.Content = null; await Drain(); True(tip.Visibility == Visibility.Collapsed, "unmount closes the capsule");
        });
        yield return Case("footer shade stays light and continuous with a larger white head across colors and DPI", async () =>
        {
            var bar = new SeekBar { Maximum = 100, Value = 25, Height = 16, RailAtTop = true,
                Margin = new Thickness(0, 56, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { Background = (new SolidColorBrush(Microsoft.UI.Colors.White)) }; root.Children.Add(bar); DesktopTheme.SetIsHighContrast(root, false);
            using var source = new DesktopTestUi.Host(new Grid(), 400, 96);
            source.Mount(root); Render(root, 400, 96); await Drain();
            var frames = new List<DesktopTestUi.RenderFrame>();
            foreach (var backdrop in new[] { (new SolidColorBrush(Microsoft.UI.Colors.White)), (new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray)) })
            foreach (var played in new[] { (new SolidColorBrush(Microsoft.UI.Colors.DimGray)), (new SolidColorBrush(Microsoft.UI.Colors.Crimson)) })
            {
                root.Background = backdrop; bar.PlayedBrush = played; bar.Value = 25;
                Render(root, 400, 96); frames.Add(await CaptureSeekFrame(root, 1));
                bar.SetPointerWithin(true);
                await Task.Delay(170); await Drain(); Render(root, 400, 96);
                frames.Add(await CaptureSeekFrame(root, 1));
                foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    var frame = await CaptureSeekFrame(root, scale);
                    // Infer composited coverage from the most distinct channel, not
                    // from the control's gradient stops or internal animation value.
                    byte[] background = [backdrop.Color.R, backdrop.Color.G, backdrop.Color.B];
                    byte[] foreground = [played.Color.R, played.Color.G, played.Color.B];
                    int channel = Enumerable.Range(0, 3).MaxBy(i => Math.Abs(foreground[i] - background[i]));
                    double quantization = 1d / Math.Abs(foreground[channel] - background[channel]);
                    double Coverage(double y)
                    {
                        var pixel = SeekPixel(frame, 300, y);
                        byte[] rgb = [pixel.R, pixel.G, pixel.B];
                        return (rgb[channel] - background[channel]) / (double)(foreground[channel] - background[channel]);
                    }
                    Equal(backdrop.Color, SeekPixel(frame, 300, 8));
                    True(Coverage(20) > .005 && Coverage(50) is > .08 and < .20,
                        $"at {scale}x the shade reaches beyond 32 DIP but its base stays below 20% coverage");
                    double previous = Coverage(14);
                    for (int y = 15; y <= 51; y++)
                    {
                        double next = Coverage(y);
                        // Compositing an 8-bit alpha gradient can round one channel
                        // down by one byte. A visual step must exceed that precision.
                        True(next >= previous - quantization - 1e-6 && next - previous < .015 + quantization,
                            $"continuous shade: background={backdrop.Color}, played={played.Color}, scale={scale}, y={y}, previous={previous:F4}, next={next:F4}");
                        previous = next;
                    }
                    True(Coverage(50) - Coverage(36) > .02, "the lower shade keeps fading instead of forming a flat strip");
                    foreach (double position in new[] { 0d, 25d, 100d })
                    {
                        bar.Value = position; Render(root, 400, 96);
                        var head = await CaptureSeekFrame(root, scale);
                        double center = position == 0 ? 7.5 : position == 100 ? 392.5 : 100;
                        Equal(Colors.White, SeekPixel(head, center, 57));
                        int start = (int)(Math.Max(0, center - 12) * scale);
                        int end = (int)(Math.Min(400, center + 12) * scale);
                        int white = Enumerable.Range(start, end - start).Count(x =>
                        {
                            var pixel = SeekPixel(head, (x + .5) / scale, 57);
                            return pixel.R >= 250 && pixel.G >= 250 && pixel.B >= 250;
                        });
                        True(white / scale is >= 12 and <= 14.5,
                            $"at {scale}x and {position}% the white head is larger than the rail and not clipped at an endpoint");
                    }
                    bar.Value = 25; Render(root, 400, 96);
                }
                bar.SetPointerWithin(false);
                await Task.Delay(170); await Drain(); Render(root, 400, 96);
                var resting = await CaptureSeekFrame(root, 1);
                for (int y = 8; y <= 53; y++)
                    Equal(backdrop.Color, SeekPixel(resting, 300, y));
            }
            if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_SEEK_VISUALS") == "1")
            {
                var pixels = new byte[800 * 384 * 4];
                for (int i = 0; i < frames.Count; i++)
                    for (int y = 0; y < frames[i].Height; y++)
                        Array.Copy(frames[i].Pixels, y * frames[i].Width * 4, pixels,
                            ((i / 2 * 96 + y) * 800 + i % 2 * 400) * 4, frames[i].Width * 4);
                await SaveSeekFrame(new DesktopTestUi.RenderFrame(800, 384, 1, pixels), "seekbar-states.png");
            }
            source.Window.Content = null;
        });
        yield return Case("footer shade retires after pointer exit despite focus and clears on disabled hidden unload and high contrast", async () =>
        {
            var bar = new SeekBar { Maximum = 100, Value = 25, Height = 16, RailAtTop = true,
                PlayedBrush = (new SolidColorBrush(Microsoft.UI.Colors.DimGray)), Margin = new Thickness(0, 56, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { Background = (new SolidColorBrush(Microsoft.UI.Colors.White)) }; root.Children.Add(bar);
            DesktopTheme.SetIsHighContrast(root, false);
            var window = DesktopTestUi.CreateWindow(root, 400, 96);
            try
            {
                window.Show(); Render(root, 400, 96); await Drain();
                True(root.XamlRoot is not null, "seek root is actually mounted");
                async Task Shade(bool visible, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
                {
                    await Task.Delay(170); await Drain(); Render(root, 400, 96);
                    var pixel = SeekPixel(await CaptureSeekFrame(root, 1), 300, 40);
                    True(visible ? pixel.R < 250 : pixel == Colors.White,
                        $"shade at line {line}: expectedVisible={visible}, pixel={pixel}, loaded={bar.IsLoaded}, visible={DesktopVisual.IsVisible(bar)}, enabled={bar.IsEnabled}, focus={DesktopTestUi.HasFocus(bar)}, scrubbing={bar.IsScrubbing}");
                }
                void Hover(bool within) => bar.SetPointerWithin(within);
                Hover(true); await Shade(true);
                True(ReferenceEquals(DesktopTestUi.Hit(root, new Point(300, 40)), bar) &&
                    ReferenceEquals(DesktopTestUi.Hit(root, new Point(300, 60)), bar),
                    "activated shade and track share the seek hit area");
                True(bar.Focus(FocusState.Programmatic) && DesktopTestUi.HasFocus(bar), "offscreen HWND supplies real focus without moving the system pointer");
                Hover(false); await Shade(false);
                Equal(Colors.White, SeekPixel(await CaptureSeekFrame(root, 1), 100, 57));
                True(SeekPixel(await CaptureSeekFrame(root, 1), 50, 60).R < 200,
                    "keyboard focus retains the expanded rail without the decorative shade");
                bar.Value = 35; await Shade(false);
                bar.Value = 25; Hover(true); bar.BeginScrub(); Hover(false);
                bar.UpdateScrub(50); await Shade(true);
                bar.EndScrub(true); await Shade(false);
                bar.HandleSeekKey(VirtualKey.Right, true);
                await Shade(true);
                bar.HandleSeekKey(VirtualKey.Right, false);
                await Shade(false);
                Equal(55d, bar.Value);
                bar.Value = 50;
                Hover(true); DesktopTheme.SetIsHighContrast(root, true); await Shade(false);
                Equal(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText), SeekPixel(await CaptureSeekFrame(root, 1), 200, 57));
                DesktopTheme.SetIsHighContrast(root, false); await Shade(true);
                bar.IsEnabled = false; await Shade(false);
                True(!bar.HasActiveAnimation, "disabled seek surface releases highlight clocks");
                bar.IsEnabled = true; await Shade(true);
                bar.Visibility = Visibility.Collapsed; await Shade(false);
                bar.Visibility = Visibility.Visible; await Shade(false);
                Hover(true); await Shade(true);
                window.Content = null; await Drain();
                True(!bar.HasActiveAnimation && !bar.IsScrubbing, "unmount removes presentation clocks and active gestures");
                window.Content = root; Render(root, 400, 96); await Drain(); await Shade(false);
            }
            finally { window.Close(); }
        });
        yield return Case("small record sampling retains antialiased cover edges at fractional DPI and detail grooves", async () =>
        {
            var record = new MusicRecordView { Address = "fixture://record", Width = 60, Height = 60 };
            ArtworkScope.SetLoader(record, new PlayerArtworkFixture());
            using var source = new DesktopTestUi.Host(new Grid(), 400, 400);
            source.Mount(record); await TestUiHost.LoadedAsync(record); Render(record, 60, 60); await Drain();
            var cover = (ArtworkImage)record.FindName("Cover");
            await Until(() => ((Image)cover.Children[0]).Source is not null);
            var rotation = (RotateTransform)record.FindName("DiscRotation");
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            foreach (double angle in new[] { 0d, 17d, 43d })
            {
                rotation.Angle = angle;
                var bitmap = await DesktopTestUi.Render(record, (96 * scale) / 96d);
                var pixels = new byte[bitmap.Width * bitmap.Height * 4]; bitmap.Pixels.CopyTo(pixels, 0);
                True(pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0 && alpha < 255), "rotated circular edge retains antialiased coverage");
                True(((Image)cover.Children[0]).Stretch == Stretch.UniformToFill, "native cover preserves its actual image sampling allocation");
            }
            var grooves = (Grid)record.FindName("Grooves");
            var layers = grooves.Children.ToArray();
            var compact = grooves.Children.OfType<Grid>().Single(layer => layer.Visibility == Visibility.Visible);
            var dense = grooves.Children.OfType<Grid>().Single(layer => layer.Visibility == Visibility.Collapsed);
            True(compact.Children.Count <= 5 && compact.Children.OfType<Ellipse>().All(ring => ring.StrokeThickness == 4),
                "small discs use sparse wide grooves instead of dense subpixel rings");
            record.Width = record.Height = 388; Render(record, 388, 388); await Drain();
            True(compact.Visibility == Visibility.Collapsed && dense.Visibility == Visibility.Visible &&
                dense.Children.Count > 20 && dense.Children.OfType<Ellipse>().All(ring => Math.Abs(ring.StrokeThickness - .65) < .0001),
                $"full-size detail uses the original dense fine grooves: actual={record.ActualWidth}x{record.ActualHeight}; compact={compact.Visibility}; detail={dense.Visibility}; count={dense.Children.Count}; stroke={((Ellipse)dense.Children[0]).StrokeThickness:R}");
            record.Width = record.Height = 60; Render(record, 60, 60); await Drain();
            True(compact.Visibility == Visibility.Visible && dense.Visibility == Visibility.Collapsed &&
                grooves.Children.SequenceEqual(layers),
                "resizing back restores compact texture without replacing the retained visual tree during layout");
            source.Window.Content = null;
        });
        yield return Case("seek gesture isolates live ticks commits once and rejects a retired track", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView; var bar = (SeekBar)footer.FindName("Progress");
            using var inputSource = new DesktopTestUi.Host(footer, 1110, 80);
            await Until(() => footer.IsLoaded && footer.XamlRoot is not null);
            footer.Measure(new(1110, 80)); footer.Arrange(new(0, 0, 1110, 80)); footer.UpdateLayout(); await Drain();
            bar.BeginScrub(); bar.UpdateScrub(74);
            await f.Playback.SeekAsync(12); await Drain();
            Equal(74d, bar.Value); Equal(12d, f.Playback.Seek);
            bar.EndScrub(true); await Until(() => !f.Module.Busy);
            Equal(74d, f.Playback.Seek); True(!bar.IsScrubbing, "released");
            bar.BeginScrub(); bar.UpdateScrub(95); bar.CancelScrub(); await Drain();
            Equal(74d, f.Playback.Seek); Equal(74d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(120);
            await f.Module.StepAsync(true); await Drain(); double replacementPosition = f.Playback.Seek;
            True(!bar.IsScrubbing, "track replacement immediately retires gesture");
            bar.EndScrub(true); await Drain(); Equal(replacementPosition, f.Playback.Seek);
            // UI Automation uses the same transaction and real media port.
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(bar)!;
            ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)).SetValue(36);
            await Until(() => !f.Module.Busy); Equal(36d, f.Playback.Seek);
            void Key(VirtualKey key, bool down) => bar.HandleSeekKey(key, down);
            Key(VirtualKey.Right, true);
            Equal(41d, bar.Value);
            await f.Playback.SeekAsync(37); await Drain(); Equal(41d, bar.Value);
            Key(VirtualKey.Right, false);
            await Until(() => !f.Module.Busy); Equal(41d, f.Playback.Seek);
            Key(VirtualKey.End, true); Equal(180d, bar.Value);
            Key(VirtualKey.Escape, true);
            Key(VirtualKey.End, false);
            await Drain(); Equal(41d, f.Playback.Seek); Equal(41d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(50); inputSource.Window.Content = null; await Until(() => !footer.IsLoaded);
            True(!bar.IsScrubbing, "unload cancels"); Equal(41d, f.Playback.Seek);
        });
        yield return Case("skip arrows animate direction without duplicate queue calls and clear on unload", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var next = (SkipButton)footer.FindName("NextButton");
            var previous = (SkipButton)footer.FindName("PreviousButton");
            True(next.Next && !previous.Next, "mirrored directions");
            next.SetPointerWithin(true);
            await Task.Delay(70); await Drain();
            if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled) True(next.HasActiveAnimation, "native hover motion active");
            Equal(0, f.Calls.Count("music.queue.next"));
            DesktopTestUi.Invoke(next); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.next"));
            DesktopTestUi.Invoke(previous); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.previous"));
            DesktopTestUi.Unmount(next);
            True(!next.HasActiveAnimation, "no retained clock after unload");
        });
        yield return Case("footer favorite toggles native membership for playing song not highlighted row", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            var song = f.Module.SelectedSong!; await f.Module.PlaySelectionAsync();
            f.Module.SelectedSong = f.Module.Songs.First(s => s.Id != song.Id);
            await f.Module.ToggleCurrentFavoriteAsync();
            True(f.Module.IsCurrentFavorite, "saved membership drives filled heart");
            Equal("取消收藏当前歌曲", f.Module.CurrentFavoriteLabel);
            await f.Module.NavigateAsync("favorites", default);
            Equal(1, f.Module.Songs.Count); Equal(song.Id, f.Module.Songs[0].Id);
            await f.Module.ToggleCurrentFavoriteAsync();
            True(!f.Module.IsCurrentFavorite, "remove updates heart without restarting playback");
            Equal(0, f.Module.Songs.Count);
            Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("footer favorite chrome matches the optional 43 by 32 badge geometry without song data", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            await f.Module.PlaySelectionAsync();
            await f.Module.ToggleCurrentFavoriteAsync();
            var footer = f.Module.PlayerView;
            // The footer is transparent; reference pixels are composited on the window backdrop it sits on.
            var host = new Border { Background = (Brush)DesktopVisual.Resource(footer, "MeowFilm.Brush.Window"), Child = footer };
            Render(host, 1110, 80);
            var button = (Button)footer.FindName("FavoriteButton");
            Equal(32d, button.ActualWidth);
            var badge = (TextBlock)DesktopTestUi.Part(button, "FavoriteBadge");
            Equal("", badge.Text);
            badge.Text = "10w+";
            Render(host, 1110, 80);
            Equal(43d, button.ActualWidth); Equal(32d, button.ActualHeight);
            var surface = (Border)DesktopTestUi.Part(button, "FavoriteSurface");
            Equal(new CornerRadius(16), surface.CornerRadius);
            var heart = Visuals(button).OfType<Microsoft.UI.Xaml.Shapes.Path>().Single();
            Equal(20d, heart.ActualWidth); Equal(18d, heart.ActualHeight);
            Equal(Color.FromArgb(255, 0xFC, 0x3D, 0x49), ((SolidColorBrush)heart.Fill).Color);
            Equal(Color.FromArgb(255, 0xFC, 0x3D, 0x49), ((SolidColorBrush)badge.Foreground).Color);

            var bitmap = await DesktopTestUi.Render(host, 1);
            var origin = button.TransformToVisual(host).TransformPoint(new Point());
            var crop = DesktopTestUi.Crop(bitmap, new Rect(origin, new Size(43, 32)));

            var pixels = new byte[43 * 32 * 4]; crop.Pixels.CopyTo(pixels, 0);
            var points = Enumerable.Range(0, 43 * 32).Where(pixel =>
            {
                var offset = pixel * 4;
                var blue = pixels[offset]; var green = pixels[offset + 1];
                var red = pixels[offset + 2]; var alpha = pixels[offset + 3];
                return alpha > 0 && red > 240 && green < 220 && blue < 225 && red - green > 30;
            }).Select(pixel => new Point(pixel % 43, pixel / 43)).ToArray();
            True(points.Length > 250, "favorite reference paints the heart and badge");
            var bounds = new Rect(new Point(points.Min(point => point.X), points.Min(point => point.Y)),
                new Point(points.Max(point => point.X) + 1, points.Max(point => point.Y) + 1));
            True(Math.Abs(bounds.Left - 3) <= 1 && Math.Abs(bounds.Top - 7) <= 1 &&
                Math.Abs(bounds.Right - 40) <= 1 && Math.Abs(bounds.Bottom - 25) <= 1,
                $"favorite ink bounds match reference: {bounds}");
        });
        yield return Case("transport glyph painted centroid stays centered at 100 150 and 200 percent DPI", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var play = (Button)footer.FindName("PlayButton");
            foreach (var playing in new[] { false, true })
            {
                if (playing) f.Playback.Start();
                await Drain(); footer.UpdateLayout();
                foreach (double scale in new[] { 1d, 1.5, 2 })
                {
                    var size = (int)(40 * scale);
                    var full = await DesktopTestUi.Render(footer, (96 * scale) / 96d);
                    var origin = play.TransformToVisual(footer).TransformPoint(new Point());
                    var bitmap = DesktopTestUi.Crop(full, new Rect(origin, new Size(40, 40)));

                    var bytes = new byte[size * size * 4]; bitmap.Pixels.CopyTo(bytes, 0);
                    double xSum = 0, ySum = 0, count = 0;
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int i = (y * size + x) * 4;
                            // Only the glyph inside the disc; a cropped footer also includes
                            // white page corners outside the circular red button.
                            double dx = (x + .5) / scale - 20, dy = (y + .5) / scale - 20;
                            if (dx * dx + dy * dy >= 225) continue;
                            if (bytes[i] < 240 || bytes[i + 1] < 240 || bytes[i + 2] < 240 || bytes[i + 3] < 240) continue;
                            xSum += (x + .5) / scale; ySum += (y + .5) / scale; count++;
                        }
                    True(count > 50, "glyph actually paints white pixels");
                    True(Math.Abs(xSum / count - 20) < .6 && Math.Abs(ySum / count - 20) < .6,
                        $"painted {(playing ? "pause" : "play")} centroid at {scale}: {xSum / count},{ySum / count}");
                }
            }
        });
        yield return Case("independent player footer shares playback and retires gestures and popups on exit", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var ordinary = (MusicPlayerView)f.Module.PlayerView;
            Render(ordinary, 1110, 80); await Drain();
            var ordinaryProgress = (SeekBar)ordinary.FindName("Progress");
            ordinaryProgress.BeginScrub(); ordinaryProgress.UpdateScrub(70);
            var opens = f.Playback.Opens.Count;
            var subscribers = f.Playback.Subscribers;
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            True(!ordinaryProgress.IsScrubbing, "covered ordinary footer retires its gesture without seeking");
            Equal(0d, f.Playback.Seek); Equal(opens, f.Playback.Opens.Count);
            var page = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var footer = (MusicPlayerView)((ContentControl)page.FindName("DetailPlayerHost")).Content;
            True(!ReferenceEquals(ordinary, footer), "player page owns its footer visual");
            True(ReferenceEquals(f.Module, footer.DataContext) && ReferenceEquals(ordinary.DataContext, footer.DataContext),
                "both visual controls consume the sole playback owner");
            using var source = new DesktopTestUi.Host(new Grid(), 1110, 800);
            source.Mount(page);
            Render(page, 1110, 800); await Drain();
            True(Visuals(page).OfType<FrameworkElement>().All(element => ToolTipService.GetToolTip(element) is null) &&
                Visuals(ordinary).OfType<FrameworkElement>().All(element => ToolTipService.GetToolTip(element) is null),
                "player titles and controls have no ordinary hover hints");
            Equal(subscribers, f.Playback.Subscribers);
            f.Playback.Start(); await Drain();
            var play = (Button)footer.FindName("PlayButton");
            Click(play); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Pauses);
            Click(play); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Resumes);
            var next = f.Calls.Count("music.queue.next");
            Click((Button)footer.FindName("NextButton")); await Until(() => !f.Module.Busy);
            Equal(next + 1, f.Calls.Count("music.queue.next")); Equal(opens + 1, f.Playback.Opens.Count);
            var progress = (SeekBar)footer.FindName("Progress");
            progress.BeginScrub(); progress.UpdateScrub(36); progress.EndScrub(true);
            await Until(() => !f.Module.Busy); Equal(36d, f.Playback.Seek);
            var requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) =>
            { if (args.Surface == DesktopGlobalSurface.PlayQueue) requests++; };
            Click(Visuals(footer).OfType<Button>().Single(button =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "播放队列"));
            Equal(1, requests);
            var more = (Button)footer.FindName("MoreButton");
            Click(more);
            var moreMenu = (MenuFlyout)footer.FindName("MoreMenu");
            await Until(() => moreMenu.IsOpen); MusicTestUi.Invoke(moreMenu.Items[0]);
            var rate = (Flyout)footer.FindName("RatePopup");
            await Until(() => rate.IsOpen); True(rate.IsOpen, "page footer popup opens");
            progress.BeginScrub(); progress.UpdateScrub(90);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            True(!rate.IsOpen && !progress.IsScrubbing, "page exit closes its popup and cancels its own gesture");
            Equal(36d, f.Playback.Seek); Equal(opens + 1, f.Playback.Opens.Count);
            Equal(subscribers, f.Playback.Subscribers);
            source.Window.Content = null;
            DesktopTestUi.Unmount(ordinary);
        });
        yield return Case("complete player page preserves the underlying shell layout and lyrics at narrow bounds", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            try
            {
                shell.PrepareForRender();
                shell.Resize(1243, 848); shell.Show(); await Drain();
                var root = DesktopTestUi.WindowRoot(shell);
                var frame = (Grid)root;
                var applicationBackground = (Border)root.FindName("ApplicationBackground");
                var frameBackground = frame.Background;
                var shellBackground = applicationBackground.Background;
                var ordinaryFooter = f.Module.PlayerView;
                var ordinaryFooterHeight = ordinaryFooter.ActualHeight;
                var ordinarySidebarWidth = ((Grid)DesktopTestUi.WindowRoot(shell).FindName("RootLayout")).ColumnDefinitions[0].ActualWidth;

                await f.Module.ShowLyricsAsync();
                await Until(() => f.Module.IsLyrics);
                await Until(() => ((TranslateTransform)((Grid)root.FindName("PlayerDetailSurface")).RenderTransform).Y == 0);
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                True(!ReferenceEquals(ordinaryFooter, footer), "complete player page has its own footer");
                async Task AssertDetailBackdrop(Size size)
                {
                    var width = (int)size.Width; var height = (int)size.Height;
                    var actual = await DesktopTestUi.Render(detail);
                    var canvas = new Border { Background = detail.Background };
                    Render(canvas, width, height); await Drain();
                    var expected = await DesktopTestUi.Render(canvas);
                    var y = footer.TransformToVisual(detail).TransformPoint(new Point(0, footer.ActualHeight / 2)).Y;
                    var shellPixels = await DesktopTestUi.Render(root);
                    foreach (var x in new[] { 1, width - 2 })
                    {
                        Equal(expected.Pixel(x, y), actual.Pixel(x, y));
                        Equal(actual.Pixel(x, y), shellPixels.Pixel(x, y));
                    }
                }
                var record = (MusicRecordView)detail.FindName("DetailRecord");
                var cover = (ArtworkImage)record.FindName("Cover");
                ArtworkScope.SetLoader(record, new PlayerArtworkFixture());
                record.Address = "fixture://cover";
                f.Module.Lyrics.Clear();
                for (var i = 0; i < 16; i++) f.Module.Lyrics.Add(new(i * 5, "原生歌词排版测试 " + i, i == 2 ? "Translation test" : ""));
                var opens = f.Playback.Opens.Count;
                foreach (var requested in new[] { new Size(1243, 848), new Size(860, 600) })
                {
                    shell.Resize(requested.Width, requested.Height); await Drain(); root.UpdateLayout();
                    var size = new Size(root.ActualWidth, root.ActualHeight);
                    await Drain(); root.UpdateLayout();
                    var grid = (Grid)DesktopTestUi.WindowRoot(shell).FindName("RootLayout");
                    True(grid.Background is null, "player overlay does not repaint the shell");
                    Equal(Visibility.Visible, ((ScrollViewer)DesktopTestUi.WindowRoot(shell).FindName("SidebarScroll")).Visibility);
                    True(Math.Abs(grid.ColumnDefinitions[0].ActualWidth - ordinarySidebarWidth) < .5, "underlying sidebar keeps its layout");
                    Equal(new Thickness(0), applicationBackground.BorderThickness);
                    True(ReferenceEquals(frameBackground, frame.Background) && ReferenceEquals(shellBackground, applicationBackground.Background),
                        "the page overlay preserves the underlying native shell background owners");
                    var title = Visuals(detail).OfType<TextBlock>()
                        .First(block => block.Text == f.Module.CurrentTitle);
                    var lyrics = (ListView)detail.FindName("LyricList");




                    var lyricText = Visuals(lyrics).OfType<TextBlock>().ToArray();
                    True(lyricText.Length > 0 && lyricText.All(block =>
                        block.FontWeight == FontWeights.Normal),
                        "the actual lyric text uses native automatic rendering");
                    var viewport = Visuals(lyrics).OfType<ScrollViewer>().First();
                    True(Math.Abs(viewport.ViewportHeight - lyrics.ActualHeight) < 1,
                        "lyric edge spacing belongs to scrolling rows, not the visible viewport");
                    True(title.TransformToVisual(root).TransformPoint(new Point()).X >= size.Width * .5, "title on right");
                    True(record.ActualWidth > 200 && record.ActualHeight > 200, "full disc not a thumbnail");
                    True(lyrics.ActualHeight >= 160, "usable lyric viewport at minimum window");
                    True(lyrics.TransformToVisual(root).TransformPoint(new Point(lyrics.ActualWidth, 0)).X < size.Width, "lyric column fits");
                    Equal(size, new Size(detail.ActualWidth, detail.ActualHeight));
                    Equal(ordinaryFooterHeight, ordinaryFooter.ActualHeight);
                    True(ReferenceEquals(ordinaryFooter, ((ContentControl)DesktopTestUi.WindowRoot(shell).FindName("PlayerHost")).Content),
                        "ordinary footer is not moved into the player page");
                    True(Math.Abs(footer.TransformToVisual(root).TransformPoint(new Point(0, footer.ActualHeight)).Y - size.Height) < .5,
                        "page footer reaches the bottom of the whole window");
                    var play = (Button)footer.FindName("PlayButton");
                    True(Math.Abs(play.TransformToVisual(root).TransformPoint(new Point(20, 0)).X - size.Width / 2) < 1, "play remains window-centered in detail");
                    True(DesktopTestUi.Hit(root, play.TransformToVisual(root).TransformPoint(new Point(20, 20))) is DependencyObject hit &&
                        Visuals(play).Contains(hit), "window chrome does not intercept the page footer");
                    Render(root, (int)size.Width, (int)size.Height);
                    DesktopTheme.SetIsHighContrast(grid, false); root.UpdateLayout();
                    Equal(Color.FromArgb(255, 34, 197, 94), ((SolidColorBrush)play.Background).Color);
                    True(ReferenceEquals(f.Module.PlayerBackground, detail.Background), "player page root owns the cover backdrop");
                    True(footer.Background is null, "page footer paints no backdrop of its own");
                    TextBlock Title(FrameworkElement owner) => Visuals(owner).OfType<TextBlock>().First(block => block.Text == f.Module.CurrentTitle);
                    True(((SolidColorBrush)Title(footer).Foreground).Color != ((SolidColorBrush)Title(ordinaryFooter).Foreground).Color,
                        "page footer adapts its controls to the page backdrop");
                    foreach (var playerView in new[] { ordinaryFooter, footer })
                    {
                        var quality = (Button)playerView.FindName("QualityButton");
                        var label = Visuals(quality).OfType<TextBlock>().Single();
                        True(ReferenceEquals(quality.Foreground, label.Foreground), "quality label follows the control palette");
                        var artist = ((StackPanel)playerView.FindName("MetadataText")).Children.OfType<TextBlock>().Last();
                        True(ReferenceEquals(DesktopVisual.Resource(playerView, "Capability.Player.Muted"), artist.Foreground),
                            "artist text follows the footer's secondary text palette");
                    }
                    await AssertDetailBackdrop(size);
                    True(f.Module.IsLyrics, "page footer consumes the existing active player state");
                    DesktopTheme.SetIsHighContrast(grid, true); root.UpdateLayout();
                    True(((SolidColorBrush)detail.Background).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window), "high contrast replaces the page backdrop");
                    var detailArtist = ((StackPanel)footer.FindName("MetadataText")).Children.OfType<TextBlock>().Last();
                    Equal(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText), ((SolidColorBrush)detailArtist.Foreground).Color);
                    await AssertDetailBackdrop(size);
                }
                True(((Image)cover.Children[0]).Source is not null && record.PaletteBrush is SolidColorBrush palette && palette.Color != Colors.DimGray,
                    "cover is loaded through public loader and its actual palette is sampled");
                Equal(opens, f.Playback.Opens.Count);
                var lyricList = (ListView)detail.FindName("LyricList");
                var lyricScroll = Visuals(lyricList).OfType<ScrollViewer>().First();
                await f.Playback.SeekAsync(40); await Task.Delay(550); await Drain();
                var centered = lyricScroll.VerticalOffset;
                True(centered > 100, "actual playback position smoothly centers a later lyric");
                var row = lyricList.ContainerFromIndex(8) as FrameworkElement;
                if (row is null) throw new InvalidOperationException("active lyric row is not realized after automatic centering");
                var rowCenter = row.TransformToVisual(lyricScroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y;
                var readingBand = lyricScroll.ViewportHeight / 2;
                True(Math.Abs(rowCenter - readingBand) < 2,
                    $"active line reaches reference reading band: actual={rowCenter}, target={readingBand}, offset={centered}, " +
                    $"scrollable={lyricScroll.ScrollableHeight}, extent={lyricScroll.ExtentHeight}, viewport={lyricScroll.ViewportHeight}, row={row.ActualHeight}");
                bool wheelHandled = detail.BrowseLyricWheel(-120); await Task.Delay(250); await Drain();
                True(wheelHandled && lyricScroll.VerticalOffset > centered, "wheel over actual lyrics scrolls");
                await f.Playback.SeekAsync(45); await Task.Delay(550); await Drain();
                True(Math.Abs(lyricScroll.VerticalOffset - centered) > 5, "manual scrolling is not immediately overwritten");
                // No new playback tick after this point: paused lyrics must still recover.
                // Synchronize with the timer plus animation settlement, not the
                // SelectedIndex bring-into-view behavior suppressed during manual scrolling.
                FrameworkElement? recoveredRow = null;
                try
                {
                    await Until(() =>
                    {
                        recoveredRow = lyricList.ContainerFromIndex(9) as FrameworkElement;
                        return recoveredRow is not null &&
                            Math.Abs(recoveredRow.TransformToVisual(lyricScroll).TransformPoint(new Point(0, recoveredRow.ActualHeight / 2)).Y -
                                lyricScroll.ViewportHeight / 2) < 2;
                    });
                }
                catch (TimeoutException error)
                {
                    var actual = recoveredRow?.TransformToVisual(lyricScroll).TransformPoint(new Point(0, recoveredRow.ActualHeight / 2)).Y;
                    throw new TimeoutException($"Idle lyric recovery did not settle: font={detail.FontFamily.Source}, " +
                        $"index={f.Module.LyricDisplayIndex}, playing={f.Module.IsPlaying}, actual={actual}, " +
                        $"target={lyricScroll.ViewportHeight / 2}, row={recoveredRow?.ActualHeight}, " +
                        $"root={new Size(root.ActualWidth, root.ActualHeight)}, detail={new Size(detail.ActualWidth, detail.ActualHeight)}, lyrics={new Size(lyricList.ActualWidth, lyricList.ActualHeight)}, " +
                        $"offset={lyricScroll.VerticalOffset}, extent={lyricScroll.ExtentHeight}, " +
                        $"viewport={lyricScroll.ViewportHeight}, scrollable={lyricScroll.ScrollableHeight}", error);
                }
                row = recoveredRow ?? throw new InvalidOperationException(
                    "active lyric row is not realized after manual-scroll recovery");
                True(Math.Abs(row.TransformToVisual(lyricScroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y - lyricScroll.ViewportHeight / 2) < 2,
                    "idle timeout resumes lyric following without a new media tick");
                var playLine = (Button)detail.FindName("ConfirmLyric");
                True(detail.BrowseLyricWheel(-120), "lyric wheel owner accepts manual browsing");
                await Task.Delay(250); await Drain();
                True(f.Module.HasLyricPreview, "wheel chooses the line under the middle without clicking text");
                var previewTime = f.Module.Lyrics[f.Module.LyricDisplayIndex].Time;
                DesktopTestUi.Invoke(playLine); await Drain();
                Equal(previewTime, f.Playback.PositionSeconds);
                True(f.Playback.IsPlaying, "middle-line control seeks and resumes real playback port");
                Equal(opens, f.Playback.Opens.Count);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
        yield return Case("lyric centering settles under continuous 200ms positions and keyboard preview keeps its hold", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            f.Module.Lyrics.Clear();
            for (var i = 0; i < 80; i++)
                f.Module.Lyrics.Add(new(i * 2, "连续进度与长行排版 " + i,
                    i == 40 ? "A translated line that wraps with the lyric viewport width." : ""));
            using var source = new DesktopTestUi.Host(detail, 860, 600);
            try
            {
                await TestUiHost.LoadedAsync(detail);
                await Drain();
                var list = (MusicLyricList)detail.FindName("LyricList");
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                Equal(f.Module.AcceptedPlaybackRate, list.PlaybackRate);
                Equal(f.Module.PlaybackPresentationKey, list.PresentationKey);
                double Error(int index)
                {
                    var row = list.ContainerFromIndex(index) as FrameworkElement;
                    return row is null ? double.PositiveInfinity : Math.Abs(
                        row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y -
                        scroll.ViewportHeight / 2);
                }
                await f.Playback.SeekAsync(80);
                for (var i = 1; i <= 3; i++)
                {
                    await Task.Delay(200);
                    await f.Playback.SeekAsync(80 + i * .2);
                    await Drain();
                }
                True(Error(40) < 2, "same target finishes its 420ms motion while real positions continue every 200ms");
                var settled = scroll.VerticalOffset;
                await Task.Delay(200); await f.Playback.SeekAsync(80.8); await Drain();
                True(Math.Abs(scroll.VerticalOffset - settled) < .5, "another position on the same line leaves centering settled");
                await f.Playback.SeekAsync(82);
                for (var i = 1; i <= 3; i++)
                {
                    await Task.Delay(200); await f.Playback.SeekAsync(82 + i * .2); await Drain();
                }
                True(Error(41) < 2, "a different line receives its own completed motion");
                var activeRow = (FrameworkElement)list.ContainerFromIndex(41);
                Visuals(activeRow).OfType<TextBlock>().Single(text => text.Text == f.Module.Lyrics[41].Text).FontSize = 28;
                await Task.Delay(550); await Drain();
                True(Error(41) < 2, "changed row text metrics recenter without a new playback position");

                var hold = System.Diagnostics.Stopwatch.StartNew();
                True(detail.BrowseLyric(VirtualKey.PageDown), "lyric key owner accepts page navigation");
                await Until(() => f.Module.HasLyricPreview && !MusicLyricScroll.IsMoving(scroll) &&
                    Error(f.Module.LyricDisplayIndex) < 2);
                var previewIndex = f.Module.LyricDisplayIndex;
                True(f.Module.HasLyricPreview, "keyboard scrolling chooses a middle-line preview");
                var held = scroll.VerticalOffset;
                await f.Playback.SeekAsync(20); await Task.Delay(650); await Drain();
                True(f.Module.HasLyricPreview && f.Module.LyricDisplayIndex == previewIndex, "keyboard preview survives real ticks");
                True(Math.Abs(scroll.VerticalOffset - held) < 2, "preview and focus do not restart automatic centering");
                var remainingHold = TimeSpan.FromSeconds(3.4) - hold.Elapsed;
                if (remainingHold > TimeSpan.Zero) await Task.Delay(remainingHold);
                await Drain();
                True(f.Module.HasLyricPreview, "the manual hold lasts four seconds");
                await Until(() => !f.Module.HasLyricPreview && Error(10) < 2);
                True(hold.Elapsed >= TimeSpan.FromSeconds(4) && hold.Elapsed < TimeSpan.FromSeconds(6),
                    "four-second hold plus centering settles within the interaction deadline");
                True(Error(10) < 2, "four-second recovery follows the actual line without another media tick");

                await f.Module.StepAsync(true);
                await f.Module.RestoreLyricsAsync();
                await Drain();
                Equal(f.Module.PlaybackPresentationKey, list.PresentationKey);
                f.Module.Lyrics.Clear();
                for (var i = 0; i < 20; i++) f.Module.Lyrics.Add(new(i * 2, "替换身份 " + i, ""));
                await f.Playback.SeekAsync(6); await Task.Delay(550); await Drain();
                True(Error(3) < 2, "track replacement retires the previous target and hold");
            }
            finally
            {
                source.Window.Content = null;
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                await Drain();
            }
        });
        yield return Case("lyric edges follow actual items through recycling snapshot changes and playback", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = new DesktopTestUi.Host(new Grid(), 860, 600);
            var list = (ListView)detail.FindName("LyricList");
            var seen = new Dictionary<ListViewItem, object>(ReferenceEqualityComparer.Instance);
            var recycled = false;
            void Layout()
            { detail.Measure(new(860, 600)); detail.Arrange(new Rect(0, 0, 860, 600)); detail.UpdateLayout(); }
            void AssertEdges(int count)
            {
                var rows = Visuals(list).OfType<ListViewItem>()
                    .Where(row => list.IndexFromContainer(row) >= 0).ToArray();
                True(rows.Length > 0 && rows.Length < Math.Min(count, 64),
                    $"realized lyric containers stay viewport bounded: {rows.Length}/{count}");
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                foreach (var row in rows)
                {
                    var first = ReferenceEquals(row.DataContext, f.Module.Lyrics[0]);
                    var last = ReferenceEquals(row.DataContext, f.Module.Lyrics[^1]);
                    var edgeSpace = Math.Max(0, (scroll.ViewportHeight - row.ActualHeight) / 2);
                    var top = first ? edgeSpace : 0;
                    var bottom = last ? edgeSpace : 0;
                    True(Math.Abs(row.Margin.Top - top) < .01 && Math.Abs(row.Margin.Bottom - bottom) < .01,
                        $"only actual edge items have space: index={list.IndexFromContainer(row)}, " +
                        $"actual={row.Margin}, expected={top}/{bottom}");
                    if (seen.TryGetValue(row, out var prior) && !ReferenceEquals(prior, row.DataContext)) recycled = true;
                    seen[row] = row.DataContext;
                }
                True(Math.Abs(scroll.ViewportHeight - list.ActualHeight) < 1, "edge space never shrinks the viewport");
            }
            async Task Visit(int index)
            {
                list.ScrollIntoView(f.Module.Lyrics[index]); Layout(); await Drain(); Layout();
                try
                {
                    await Until(() =>
                    {
                        Layout();
                        return list.ContainerFromIndex(index) is FrameworkElement;
                    });
                }
                catch (TimeoutException error)
                {
                    var scroll = Visuals(list).OfType<ScrollViewer>().First();
                    var rows = Visuals(list).OfType<ListViewItem>()
                        .Select(row => list.IndexFromContainer(row)).ToArray();
                    throw new TimeoutException($"Lyric visit did not settle: count={list.Items.Count}, index={index}, " +
                        $"realized={string.Join(',', rows)}, offset={scroll.VerticalOffset}, " +
                        $"scrollable={scroll.ScrollableHeight}", error);
                }
                Layout(); AssertEdges(f.Module.Lyrics.Count);
            }
            async Task AssertCentered(int index)
            {
                Layout();
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                try
                {
                    await Until(() =>
                    {
                        Layout();
                        var row = list.ContainerFromIndex(index) as FrameworkElement;
                        return f.Module.LyricDisplayIndex == index && row is not null &&
                            Math.Abs(row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y -
                                scroll.ViewportHeight / 2) < 2;
                    });
                }
                catch (TimeoutException error)
                {
                    var row = list.ContainerFromIndex(index) as FrameworkElement;
                    throw new TimeoutException($"Lyric edge follow did not settle: count={list.Items.Count}, " +
                        $"index={index}, active={f.Module.LyricDisplayIndex}, playing={f.Module.IsPlaying}, " +
                        $"actual={row?.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y}, " +
                        $"target={scroll.ViewportHeight / 2}, margin={row?.Margin}, " +
                        $"offset={scroll.VerticalOffset}, scrollable={scroll.ScrollableHeight}", error);
                }
                AssertEdges(f.Module.Lyrics.Count);
            }
            try
            {
                source.Mount(detail); Layout(); await Drain();
                foreach (var count in new[] { 60, 120, 1000 })
                {
                    seen.Clear(); recycled = false;
                    f.Module.Lyrics.Clear();
                    for (var index = 0; index < count; index++)
                        f.Module.Lyrics.Add(new(index * .1, "歌词行 " + index, ""));
                    f.Module.PreviewLyric(f.Module.Lyrics[0]); Layout(); await Drain();
                    foreach (var index in new[] { 0, count - 1, count / 2, 0 }) await Visit(index);
                    True(recycled, "scrolling one snapshot reuses actual containers for different lyric items");
                    f.Module.Lyrics.Move(0, count / 2); Layout(); await Drain();
                    foreach (var index in new[] { 0, count / 2, count - 1 }) await Visit(index);
                    f.Module.Lyrics.RemoveAt(count - 1); Layout(); await Drain(); await Visit(count - 2);
                    f.Module.Lyrics.RemoveAt(0); Layout(); await Drain(); await Visit(0);
                    f.Module.Lyrics.Insert(0, new(0, "插入首行", ""));
                    f.Module.Lyrics.Add(new((count - 1) * .1, "替换末行", ""));
                    // Equal row values still represent new item identities.
                    f.Module.Lyrics[0] = f.Module.Lyrics[0] with { };
                    f.Module.Lyrics[^1] = f.Module.Lyrics[^1] with { };
                    Layout(); await Drain();
                    foreach (var index in new[] { 0, count - 1 }) await Visit(index);
                    await f.Module.StepAsync(true);
                    await f.Module.RestoreLyricsAsync();
                    f.Module.Lyrics.Clear();
                    for (var index = 0; index < count; index++)
                        f.Module.Lyrics.Add(new(index * .1, "换歌歌词行 " + index, ""));
                    f.Module.PreviewLyric(f.Module.Lyrics[0]); Layout(); await Drain();
                    foreach (var index in new[] { count - 1, count / 2, 0 }) await Visit(index);
                    f.Module.CancelLyricPreview();
                    await f.Playback.SeekAsync(f.Module.Lyrics[^1].Time); await AssertCentered(count - 1);
                    await f.Playback.ResumeAsync(); await AssertCentered(count - 1);
                    await f.Playback.PauseAsync(); await AssertCentered(count - 1);
                    await f.Playback.SeekAsync(0); await AssertCentered(0);
                }
                f.Module.Lyrics.Clear();
                var only = new MusicLyricRow(0, "单行歌词", "");
                f.Module.Lyrics.Add(only); f.Module.PreviewLyric(only); Layout(); await Drain();
                await Until(() => list.ContainerFromIndex(0) is ListViewItem);
                var single = (ListViewItem)list.ContainerFromIndex(0);
                True(single.Margin.Top > 0 && single.Margin.Bottom > 0, "one actual lyric is both boundaries");
                f.Module.Lyrics.Clear(); Layout(); await Drain();
                True(f.Module.FirstLyric is null && f.Module.LastLyric is null, "empty snapshots retire both boundaries");
            }
            finally
            {
                source.Window.Content = null;
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); await Drain();
            }
        });
    }
    private static PlayerSurfaceInput? PlayerInput(MusicPlayerView player) =>
        (PlayerSurfaceInput?)typeof(MusicPlayerView).GetField("surfaceInput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(player);
    private static async Task SettleQueue(Fixture fixture)
    {
        await Until(() => !fixture.Module.Busy);
        if (typeof(MusicCapability).GetField("queueSurfacePending",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Module) is Task refresh)
            await refresh;
        await Drain();
    }
    private static Task<DesktopTestUi.RenderFrame> CaptureSeekFrame(FrameworkElement root, double scale) =>
        DesktopTestUi.Render(root, scale);
    private static Color SeekPixel(DesktopTestUi.RenderFrame frame, double x, double y) => frame.Pixel(x, y);
    private static async Task SaveSeekFrame(DesktopTestUi.RenderFrame frame, string fileName)
    {
        string path = TestWorkspace.ValidatePath(TestWorkspace.ResultsRoot,
            Path.Combine(TestWorkspace.ResultsRoot, "windows-native", fileName));
        await DesktopTestUi.Save(frame, path);
        Console.WriteLine("VISUAL " + path);
    }
    private sealed class CallbackArtworkLoader(Func<string, CancellationToken, Task<IArtworkLease>> acquire) : IArtworkLoader
    {
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default) =>
            acquire(address, cancellationToken);
    }
    private sealed class HeldPlayerArtwork : IArtworkLoader
    {
        internal readonly List<(TaskCompletionSource<IArtworkLease> Completion, CancellationToken Token)> Calls = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((completion, cancellationToken)); return completion.Task;
        }
    }
    // Explicit deterministic artwork fixture, never packaged into the product or fetched from a platform.
    private sealed class PlayerArtworkFixture : IArtworkLoader
    {
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = MusicTestUi.Pixels(256, 256, (x, y) =>
            {
                if ((x - 168) * (x - 168) + (y - 86) * (y - 86) < 38 * 38) return Colors.LightGoldenrodYellow;
                if (y > 205 - 70 * Math.Sin(x * Math.PI / 128)) return Colors.DarkSlateBlue;
                double amount = (x + y) / 510d;
                byte Mix(byte first, byte second) => (byte)Math.Round(first + (second - first) * amount);
                return Color.FromArgb(255, Mix(70, 233), Mix(130, 150), Mix(180, 122));
            });
            return Task.FromResult<IArtworkLease>(new PlayerArtworkLease(image));
        }
    }
    private sealed class PlayerArtworkLease(ArtworkPixels image) : IArtworkLease
    {
        public ArtworkPixels Image { get; } = image;
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
