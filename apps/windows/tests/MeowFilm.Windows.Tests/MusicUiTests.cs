using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Native application runs ordinary business. Only authored LX scripts use the
// script-only process. Local-store restart and synthetic-store UI races are
// separate evidence; no production storage fallback exists.
public static partial class MusicUiTests
{
    private static async Task AssertOpenedSongMenuStyles(MenuFlyout menu, FrameworkElement host)
    {
        await Drain();
        True(menu.IsOpen && ReferenceEquals(menu.XamlRoot, host.XamlRoot), "song menu is in the host's native XAML root");
        foreach (var item in menu.Items.Where(item => item is not MenuFlyoutSeparator))
        {
            item.ApplyTemplate();
            True(item.Template is not null && item.FontWeight.Weight == 400, "menu item uses a native template and regular body text");
            if (item is not MenuFlyoutSubItem submenu) continue;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(submenu)!;
            var expansion = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
            expansion.Expand(); await Drain();
            foreach (var child in submenu.Items)
            {
                child.ApplyTemplate();
                True(child.Template is not null && child.FontWeight.Weight == 400,
                    "dynamic playlist submenu retains its native template and regular text");
            }
            expansion.Collapse();
        }
    }

    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in PlayerChromeCases()) yield return test;
        foreach (var test in SeekHitAreaCases()) yield return test;
        foreach (var test in LyricInteractionCases()) yield return test;
        foreach (var test in PlaybackResponsivenessCases()) yield return test;
        yield return Case("music settings expose current native groups and lyrics use the same playback", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            await RenderAsync(f.Module.SettingsView, 1080, 680);
            var navigation = (Panel)f.Module.SettingsView.FindName("SettingsNavigation");
            var anchors = navigation.Children.OfType<RadioButton>().ToArray();
            Equal(5, anchors.Length);
            True(anchors.Select(anchor => (string)anchor.Content)
                .SequenceEqual(new[] { "账号与平台", "搜索", "音质", "歌词与封面", "音源" }),
                "reference settings groups are all present");
            await f.Module.ActivateSettingsAsync();
            await Until(() => !f.Module.Busy && f.Module.Accounts.All(account => account.State == "none"));
            True(f.Module.SelectedAccount is null, "account selection belongs to an explicit login action");
            Equal(3, f.Module.PlatformSettings.Count);
            True(f.Module.PlatformSettings.Select(row => row.Name)
                .SequenceEqual(new[] { "网易云音乐", "QQ 音乐", "酷狗音乐" }), "fixed platform cards");
            True(f.Module.PlatformSettings.All(row => !row.IsLoggedIn && row.ShowLogin),
                "all platform capsules publish their logged-out state");
            anchors[^1].IsChecked = true;
            f.Module.SettingsView.UpdateLayout();
            await Until(() => !f.Module.Busy);
            var sources = Visuals(f.Module.SettingsView).OfType<MusicSourcesSettingsView>().Single();
            var sourceLabels = Visuals(sources).OfType<Button>()
                .Select(button => button.Content as string).Where(value => value is not null).ToArray();
            True(sourceLabels.Contains("添加本地文件"), "source import action is available");
            await f.Module.PlaySelectionAsync(); var opens = f.Playback.Opens.Count;
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            Equal(opens, f.Playback.Opens.Count);
            True(f.Module.IsLyrics, "detail is the existing lyric route");
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            await RenderAsync(detail, 1080, 680);
            var record = (MusicRecordView)detail.FindName("DetailRecord");
            Equal(f.Module.CurrentArtwork, record.Address); Equal(f.Module.IsPlaying, record.Playing);
            True(record.ActualWidth > 0, "native disc mounted");
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
        });
        yield return Case("record swipe previews without switching and commits one stable queue step", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            var view = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var window = MusicTestUi.Window(view, 1080, 720);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var record = (MusicRecordView)view.FindName("DetailRecord");
                await Until(() => record.SwipeEnabled);
                var nextBefore = f.Calls.Count("music.queue.next");
                var opensBefore = f.Playback.Opens.Count;
                record.BeginSwipe(record.ActualWidth / 2);
                record.UpdateSwipe(record.ActualWidth / 2 + 20);
                record.EndSwipe(commit: true);
                await Until(() => Math.Abs(record.SwipeOffset) < .5);
                Equal(nextBefore, f.Calls.Count("music.queue.next"));
                Equal(opensBefore, f.Playback.Opens.Count);

                var identity = f.Module.PlaybackPresentationKey;
                record.BeginSwipe(record.ActualWidth / 2);
                record.UpdateSwipe(0);
                record.EndSwipe(commit: true);
                await Until(() => f.Calls.Count("music.queue.next") == nextBefore + 1 &&
                    f.Playback.Opens.Count == opensBefore + 1 && f.Module.PlaybackPresentationKey != identity && !f.Module.Busy);
                await Task.Delay(320); await Drain();
                Equal(nextBefore + 1, f.Calls.Count("music.queue.next"));
                Equal(opensBefore + 1, f.Playback.Opens.Count);
                Equal(0d, record.SwipeOffset);

                var previousBefore = f.Calls.Count("music.queue.previous");
                identity = f.Module.PlaybackPresentationKey;
                record.BeginSwipe(record.ActualWidth / 2);
                record.UpdateSwipe(record.ActualWidth);
                record.EndSwipe(commit: true);
                await Until(() => f.Calls.Count("music.queue.previous") == previousBefore + 1 &&
                    f.Playback.Opens.Count == opensBefore + 2 && f.Module.PlaybackPresentationKey != identity && !f.Module.Busy);
                await Task.Delay(320); await Drain();
                Equal(previousBefore + 1, f.Calls.Count("music.queue.previous"));
                Equal(0d, record.SwipeOffset);
            }
            finally
            {
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                window.Content = null;
                window.Close();
            }
        });
        yield return Case("record posture survives same-track artwork pause visibility and canceled swipes", async () =>
        {
            foreach (var size in new[] { 60, 300 })
            {
                var record = new MusicRecordView
                {
                    Width = size, Height = size, Address = "fixture://same-cover", TrackIdentity = "song:1",
                    SwipeIdentity = "attempt:1", SwipeEnabled = true
                };
                ArtworkScope.SetLoader(record, new PlayerArtworkFixture());
                using var host = new DesktopTestUi.Host(record, size + 40, size + 40);
                await RenderAsync(record, size, size); await Drain();
                var rotation = (RotateTransform)record.FindName("DiscRotation");
                Equal(0d, rotation.Angle);
                rotation.Angle = 63;
                record.Visibility = Visibility.Collapsed; await Drain();
                record.Visibility = Visibility.Visible; await Drain();
                record.Address = "fixture://updated-cover"; await Drain();
                Equal(63d, rotation.Angle);
                record.Address = ""; await Drain();
                Equal(63d, rotation.Angle);
                record.SwipeIdentity = "attempt:2"; await Drain();
                Equal(63d, rotation.Angle);
                record.Playing = true; await Task.Delay(200); await Drain();
                record.Playing = false; await Task.Delay(40); await Drain();
                var paused = rotation.Angle;
                if (MusicVisuals.CanAnimate(record))
                    True(paused > 63 && paused < 75, "playing continues from the existing posture");
                await Task.Delay(160); await Drain();
                True(Math.Abs(rotation.Angle - paused) < .01, "pause freezes the disc");
                record.Address = "fixture://same-cover";
                record.SetValue(MusicRecordView.PaletteBrushProperty, new SolidColorBrush(Microsoft.UI.Colors.Teal));
                await Drain();
                True(Math.Abs(rotation.Angle - paused) < .01, "same-track cover and theme retain the paused angle");
                record.Visibility = Visibility.Collapsed; await Drain();
                record.Visibility = Visibility.Visible; await Drain();
                True(Math.Abs(rotation.Angle - paused) < .01, "hide and show retain the paused posture");
                host.Window.Content = null; await Drain();
                host.Window.Content = record; await RenderAsync(record, size, size); await Drain();
                True(Math.Abs(rotation.Angle - paused) < .01, "unload and reload retain the same track posture");
                record.BeginSwipe(size / 2); record.UpdateSwipe(0); record.EndSwipe(commit: false);
                await Until(() => Math.Abs(record.SwipeOffset) < .5);
                True(Math.Abs(rotation.Angle - paused) < .01, "canceling a swipe does not reset the disc");
                record.Playing = true; await Task.Delay(200); await Drain();
                record.Playing = false; await Task.Delay(40); await Drain();
                if (MusicVisuals.CanAnimate(record))
                    True(rotation.Angle > paused && rotation.Angle < paused + 12, "resume continues from the retained posture");
                record.Playing = true; await Task.Delay(160); await Drain();
                record.Visibility = Visibility.Collapsed; await Drain();
                var hidden = rotation.Angle;
                await Task.Delay(160); await Drain();
                True(Math.Abs(rotation.Angle - hidden) < .01, "hidden discs retain their last visible posture");
                record.Playing = false;
                record.Visibility = Visibility.Visible; await Drain();
                True(Math.Abs(rotation.Angle - hidden) < .01, "pausing while hidden returns at the retained angle");
                paused = hidden;

                var commits = 0;
                record.SwipeCommitted += (_, e) =>
                {
                    True(Math.Abs(record.SwipeOffset) >= size - .5, "the old disc leaves before the queue commit");
                    True(Math.Abs(rotation.Angle - paused) < .01, "the visible outgoing disc never rewinds");
                    record.TrackIdentity = "song:2";
                    Equal("fixture://same-cover", record.Address);
                    Equal(0d, rotation.Angle);
                    commits++;
                    record.ResolveSwipe(e.Identity);
                };
                record.BeginSwipe(size / 2); record.UpdateSwipe(0);
                // Compact discs also require the product's 48-DIP commit threshold.
                record.UpdateSwipe(-size); record.EndSwipe(commit: true);
                await Until(() => commits == 1 && Math.Abs(record.SwipeOffset) < .5);
                rotation.Angle = 91;
                record.Address = ""; Equal(91d, rotation.Angle);
                record.TrackIdentity = "song:3"; Equal(0d, rotation.Angle);
                rotation.Angle = 17;
                record.TrackIdentity = "song:4"; Equal(0d, rotation.Angle);
                record.TrackIdentity = "song:1"; Equal(0d, rotation.Angle);
                host.Window.Content = null;
            }
        });
        yield return Case("footer and detail records follow the same track identity independently of media attempts", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            await RenderAsync(f.Module.PlayerView, 860, 92); await RenderAsync(detail, 860, 600); await Drain();
            var footer = Visuals(f.Module.PlayerView).OfType<MusicRecordView>().Single();
            var full = (MusicRecordView)detail.FindName("DetailRecord");
            await Drain();
            Equal(f.Module.CurrentTrackIdentity, footer.TrackIdentity);
            Equal(footer.TrackIdentity, full.TrackIdentity);
            var identity = footer.TrackIdentity;
            var attempt = f.Module.PlaybackPresentationKey;
            var rotations = new[] { footer, full }.Select(record => (RotateTransform)record.FindName("DiscRotation")).ToArray();
            foreach (var rotation in rotations) rotation.Angle = 63;
            await f.Playback.StopAsync();
            await f.Module.TogglePlaybackAsync(); await Drain();
            True(attempt != f.Module.PlaybackPresentationKey, "reopening media advances its request identity");
            Equal(identity, f.Module.CurrentTrackIdentity);
            foreach (var rotation in rotations) Equal(63d, rotation.Angle);
            await f.Module.StepAsync(true); await Drain();
            True(identity != f.Module.CurrentTrackIdentity, "a queue step changes the track identity");
            Equal(f.Module.CurrentTrackIdentity, footer.TrackIdentity);
            Equal(footer.TrackIdentity, full.TrackIdentity);
            foreach (var rotation in rotations) Equal(0d, rotation.Angle);
        });
        yield return Case("record swipe cancellation fences identity visibility resize and interrupted return", async () =>
        {
            var record = new MusicRecordView
            {
                Width = 300, Height = 300, SwipeEnabled = true, SwipeIdentity = "song:1"
            };
            var window = MusicTestUi.Window(record, 340, 340);
            var commits = 0;
            var directions = new List<int>();
            record.SwipeCommitted += (_, e) =>
            {
                commits++;
                directions.Add(e.Direction);
                record.ResolveSwipe(e.Identity);
            };
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                record.BeginSwipe(150); record.UpdateSwipe(170); record.EndSwipe(commit: true);
                record.BeginSwipe(150); record.EndSwipe(commit: true);
                await Until(() => Math.Abs(record.SwipeOffset) < .5);
                Equal(0, commits);

                record.BeginSwipe(150); record.UpdateSwipe(0);
                record.SwipeIdentity = "song:2";
                record.EndSwipe(commit: true);
                await Drain();
                Equal(0, commits); Equal(0d, record.SwipeOffset);

                record.BeginSwipe(150); record.UpdateSwipe(0);
                record.Visibility = Visibility.Collapsed;
                record.EndSwipe(commit: true);
                await Drain();
                Equal(0, commits); Equal(0d, record.SwipeOffset);
                record.Visibility = Visibility.Visible; await Drain();

                record.BeginSwipe(150); record.UpdateSwipe(0);
                record.Width = 320; DesktopTestUi.WindowRoot(window).UpdateLayout();
                record.EndSwipe(commit: true);
                await Until(() => Math.Abs(record.SwipeOffset) < .5);
                Equal(0, commits);

                True(record.Navigate(VirtualKey.Right), "right-key command owns one next preview");
                await Until(() => commits == 1 && Math.Abs(record.SwipeOffset) < .5);
                Equal(1, directions[0]);
                True(record.Navigate(VirtualKey.Left), "left-key command owns one previous preview");
                await Until(() => commits == 2 && Math.Abs(record.SwipeOffset) < .5);
                Equal(-1, directions[1]);
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("music sidebar keeps only primary entries and library collections; footer owns current identity", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            var primary = f.Module.Navigation.Where(n => n.ShowInSidebar && n.Group is null).ToArray();
            Equal(2, primary.Length); Equal("推荐", primary[0].Title); Equal("频道", primary[1].Title);
            True(f.Module.Navigation.Any(n => n.Route == "favorites" && n.Group == "我的" && n.ShowInSidebar), "favorites remains visible");
            var playing = f.Module.SelectedSong!; await f.Module.PlaySelectionAsync();
            f.Module.SelectedSong = f.Module.Songs.First(s => s.Id != playing.Id);
            await f.Module.AddCurrentToPlaylistAsync("favorites");
            await f.Module.NavigateAsync("favorites", default);
            Equal(1, f.Module.Songs.Count); Equal(playing.Id, f.Module.Songs[0].Id);
            await RenderAsync(f.Module.PlayerView, 860, 92);
            foreach (int width in new[] { 1110, 2560 })
            {
                var footer = f.Module.PlayerView;
                await RenderAsync(footer, width, 80);
                var play = (Button)footer.FindName("PlayButton");
                var favorite = (Button)footer.FindName("FavoriteButton");
                var center = play.TransformToVisual(footer).TransformPoint(new Point(play.ActualWidth / 2, 0)).X;
                var heart = favorite.TransformToVisual(footer).TransformPoint(new Point(favorite.ActualWidth / 2, 0)).X;
                True(Math.Abs(center - width / 2d) < 1, "transport center is the window center");
                var text = (StackPanel)footer.FindName("MetadataText");
                var textEnd = text.TransformToVisual(footer).TransformPoint(new Point(text.ActualWidth, 0)).X;
                True(Math.Abs(heart - textEnd - text.Margin.Right - favorite.ActualWidth / 2) < 1, "favorite follows intrinsic metadata width");
                AssertPlayerBounds(footer, width);
            }
        });
        foreach (var online in OnlineCases()) yield return online;
        yield return Case("production constructor selects only native owners without script startup", async () =>
        {
            await using var store = new DocumentProbe();
            await using var runtime = new NativeMusicScriptTestRuntime();
            var output = new PlaybackProbe();
            await using var module = new MusicCapability(runtime, store, MusicTestStores.For(store), output, new Dialogs());
            Equal("0.4.0", module.Version);
            await module.InitializeAsync(default);
            await module.CreatePlaylistAsync("原生构造");
            Equal(1, module.Playlists.Count(p => p.Kind == "playlist"));
            await module.ReadAccountsAsync(); Equal("none", module.Accounts[0].State);
            await module.ChangeModeAsync(2); await module.PrepareShutdownAsync();
            Equal(0, runtime.Calls.Count); True(!runtime.HasStarted, "ordinary business never starts the script runtime");
            True((await MusicTestStores.For(store).ReadAsync("local")).Revision.Generation > 0, "native CAS persisted");
            Equal(0, runtime.DisposeCalls);
        });
        yield return Case("local playlist creation uses the sidebar action and an in-view modal", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicView)f.Module.View;
            var favorite = f.Module.Navigation.Single(item => item.Route == "favorites");
            var action = favorite.GroupAction;
            True(action is not null && action.CanExecute(null), "local collection group exposes creation");
            var header = ((IDesktopHeaderSource)view).Header;
            Equal(0, header.HeaderActions.Count);
            True(!Visuals(view).OfType<ComboBox>().Any(box =>
                AutomationProperties.GetName(box) == "指定歌曲录音"), "collection page does not expose recording identity");

            using var host = new DesktopTestUi.Host(view, 900, 650); await Drain();
            action!.Execute(null); await Drain();
            var dialog = (ContentDialog)typeof(MusicView).GetField("playlistDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var input = Visuals(dialog).OfType<TextBox>().Single();
            var create = Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "创建"));
            True(MusicTestUi.IsDialogOpen(dialog) && ReferenceEquals(dialog.XamlRoot, view.XamlRoot),
                "playlist modal is composed in the existing page root");
            True(DesktopVisual.ContainsFocus(dialog), "modal owns focus instead of the underlying page");
            True(!create.IsEnabled && input.MaxLength == 200, "empty local name is not submitted");
            input.Text = "恢复失败后创建";
            Equal("7 / 200", Visuals(dialog.Content as DependencyObject ?? dialog).OfType<TextBlock>().Single().Text);
            True(create.IsEnabled, "valid local name enables creation");
            var panel = (FrameworkElement)dialog.Content;
            True(panel.ActualWidth > 0 && DesktopTestUi.Bounds(dialog, view).Width <= view.ActualWidth,
                "dialog fits inside the current page");
            True(Math.Abs(panel.TransformToVisual(view).TransformPoint(new Point(panel.ActualWidth / 2, 0)).X - view.ActualWidth / 2) < 1,
                "dialog content is centered inside the existing view");

            f.Calls.FailNext("music.library.create", "STORAGE_UNAVAILABLE");
            Click(create);
            await Until(() => MusicTestUi.IsDialogOpen(dialog) && !f.Module.Busy && create.IsEnabled);
            Equal(1, f.Module.Playlists.Count);
            Equal("操作失败，请重试", f.Module.Status);
            Click(create);
            await Until(() => !view.IsPlaylistDialogOpen && !f.Module.Busy &&
                f.Module.Playlists.Any(item => item.Name == "恢复失败后创建"));
            Equal("恢复失败后创建", f.Module.Heading);
            header = ((IDesktopHeaderSource)view).Header;
            True(header.HeaderActions.Select(item => item.Id).SequenceEqual(
                new[] { "music.playlist.rename", "music.playlist.delete" }), "playlist header keeps only detail actions");
            Equal(18d, ((Border)view.FindName("DetailArtworkFrame")).CornerRadius.TopLeft);
            var playAll = (Button)view.FindName("PlayAllButton");
            True(playAll.BorderBrush is not null && playAll.Background is SolidColorBrush { Color.A: 0 },
                "play-all uses the theme-aware outlined treatment");

            favorite.GroupAction!.Execute(null); await Drain();
            dialog = (ContentDialog)typeof(MusicView).GetField("playlistDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            Visuals(dialog).OfType<TextBox>().Single().Text = "取消创建";
            Click(Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "取消")));
            await Until(() => !view.IsPlaylistDialogOpen);
            True(!f.Module.Playlists.Any(item => item.Name == "取消创建"), "native modal cancellation does not create");

            await f.PlayableCollection();
            True(playAll.IsEnabled, "play-all enables for a populated local playlist");
            Equal(((SolidColorBrush)DesktopVisual.Resource(view, "MeowFilm.Brush.Accent")).Color,
                ((SolidColorBrush)playAll.BorderBrush!).Color);
            DesktopTheme.SetIsHighContrast(view, true);
            view.UpdateLayout();
            Equal(new UISettings().UIElementColor(UIElementType.WindowText), ((SolidColorBrush)playAll.BorderBrush!).Color);
            Equal(new UISettings().UIElementColor(UIElementType.WindowText), ((SolidColorBrush)playAll.Foreground).Color);
            DesktopTheme.SetIsHighContrast(view, false);
        });
        yield return Case("real library CRUD, file grant isolation and cancellation", async () =>
        {
            await using var f = await Fixture.Create(persistent: true);
            Equal(1, f.Module.Playlists.Count); Equal("我的收藏", f.Module.Heading);
            Equal(0, ((IDesktopHeaderSource)f.Module.View).Header.HeaderActions.Count);
            True(f.Module.Navigation.Single(item => item.Route == "favorites").GroupAction is not null,
                "creation belongs to the local sidebar group");
            await f.Module.CreatePlaylistAsync("本机测试歌单");
            var created = f.Module.Playlists.Single(item => item.Kind == "playlist");
            Equal(created.Name, f.Module.Heading);
            f.Dialogs.Paths = null; await f.Module.ImportFilesAsync(); Equal(0, f.Module.Songs.Count);
            var paths = f.MakeFiles(2); f.Dialogs.Paths = paths;
            await f.Module.ImportFilesAsync(); Equal(2, f.Module.Songs.Count);
            var song = f.Module.Songs[0];
            True(f.Calls.Log.All(call => !call.Parameters.GetRawText().Contains("file_path", StringComparison.Ordinal)), "no file_path in RPC");
            True(f.Calls.Log.All(call => !call.Parameters.GetRawText().Contains("music.files.v2", StringComparison.Ordinal)), "grant namespace native-only");
            True(f.Calls.Log.All(call => !call.Parameters.GetRawText().Contains(JsonSerializer.Serialize(paths[0]).Trim('"'), StringComparison.Ordinal)), "native path never in RPC");
            var grants = new GrantedMediaFiles(f.Store, [".wav"]);
            Equal(2, (await grants.ListAsync()).Count);
            Equal(new Uri(paths[0]), await grants.ResolveAsync(song.Raw.GetProperty("localFileId").GetString()!));
            await f.Module.SearchAsync(song.Title, default); Equal("曲库搜索", f.Module.Heading);
            f.Module.SelectedSong = f.Module.Songs[0]; await f.Module.FavoriteAsync();
            await f.Module.NavigateAsync("favorites", default); Equal(1, f.Module.Songs.Count);
            await f.Module.NavigateAsync("playlist/" + created.Id, default);
            await f.Module.RenamePlaylistAsync("重命名测试"); Equal("重命名测试", f.Module.Heading);
            f.Module.SelectedSong = f.Module.Songs[1]; await f.Module.RemoveSongAsync(); Equal(1, f.Module.Songs.Count);
            f.Dialogs.Confirmation = false; await f.Module.DeletePlaylistAsync(); Equal(2, f.Module.Playlists.Count);
            f.Dialogs.Confirmation = true; await f.Module.DeletePlaylistAsync(); Equal(1, f.Module.Playlists.Count);
            True(f.Module.IsCollectionPage && f.Module.Heading == "我的收藏", "deletion returns to the existing favorites");
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("busy mode change retains exactly one native Ended and records only Started", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            await f.Module.PlaySelectionAsync(); Equal(0, f.Calls.Count("music.history.recordPlayed"));
            f.Playback.Start(); f.Playback.Start(); await Until(() => f.Calls.Count("music.history.recordPlayed") == 1);
            var hold = f.Calls.HoldNext("music.queue.mode");
            var manual = f.Module.ChangeModeAsync(1); await hold.Arrived.Task;
            f.Playback.End(); f.Playback.End(); True(f.Module.Busy, "manual operation still busy");
            Equal(0, f.Calls.Count("music.queue.next"));
            hold.Release(); await manual; await Until(() => !f.Module.Busy && f.Playback.Opens.Count == 2);
            Equal(1, f.Calls.Count("music.queue.next"));
            Equal("ended", f.Calls.Last("music.queue.next").GetProperty("reason").GetString());
            Equal(1, f.Calls.Count("music.history.recordPlayed"));
            f.Playback.Start(); await Until(() => f.Calls.Count("music.history.recordPlayed") == 2);
        });
        yield return Case("queued old Ended cannot skip a manual replacement session", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var row = f.Module.QueueSurfaceRows[2];
            var selectedTitle = row.Title;
            var hold = f.Calls.HoldNext("music.queue.select");
            var selection = f.Module.PlayQueueSurfaceItemAsync(row); await hold.Arrived.Task;
            f.Playback.End(); hold.Release(); await selection; await Until(() => !f.Module.Busy);
            Equal(2, f.Playback.Opens.Count); Equal(0, f.Calls.Count("music.queue.next")); Equal(selectedTitle, f.Module.CurrentTitle);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("player mode failure reverts and retired queue surface reads cannot overwrite current rows", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            using var host = new DesktopTestUi.Host(f.Module.PlayerView, 860, 140);
            await RenderAsync(f.Module.PlayerView, 860, 92); await Drain();
            var button = FindButton(f.Module.PlayerView, f.Module.ModeText);
            var menu = (MenuFlyout)((MusicPlayerView)f.Module.PlayerView).FindName("ModeMenu");
            f.Calls.FailNext("music.queue.mode", "STORAGE_UNAVAILABLE");
            Click(button); await Drain();
            Equal(0, f.Calls.Count("music.queue.mode"));
            MusicTestUi.Invoke(menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "随机播放"));
            await Until(() => f.Calls.Count("music.queue.mode") == 1 && !f.Module.Busy);
            Equal(1, f.Module.Mode); Equal("列表循环", AutomationProperties.GetName(button));
            Equal(1, f.Calls.Count("music.queue.mode"));
            Click(button); await Drain();
            True(menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "列表循环").IsChecked, "failed change keeps the saved selection");
            MusicTestUi.Invoke(menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "随机播放"));
            await Until(() => f.Calls.Count("music.queue.mode") == 2 && !f.Module.Busy);
            Equal(2, f.Module.Mode); Equal("随机播放", AutomationProperties.GetName(button));
            await Task.WhenAll(f.Module.ChangeModeAsync(2), f.Module.ChangeModeAsync(1)); Equal(1, f.Module.Mode);

            var hold = f.Calls.HoldNext("music.queue.get", p =>
                p.GetProperty("offset").GetInt32() == 0 && p.GetProperty("limit").GetInt32() == 100);
            var retired = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await hold.Arrived.Task;
            await f.Rpc("music.queue.replace", new { songs = new[] { Song("surface:new", "新播放列表") }, index = 0 });
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            Equal(1, f.Module.QueueSurfaceRows.Count); Equal("新播放列表", f.Module.QueueSurfaceRows[0].Title);
            hold.Release(); await Cancelled(retired); await Drain();
            Equal(1, f.Module.QueueSurfaceRows.Count); Equal("新播放列表", f.Module.QueueSurfaceRows[0].Title);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("repeated queue activation shares one physical first-page read", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.queue.replace", new
            {
                songs = new[] { Song("queue:a", "队列 A"), Song("queue:b", "队列 B") },
                index = 0
            });
            var hold = f.Calls.HoldNext("music.queue.get", p =>
                p.GetProperty("offset").GetInt32() == 0 && p.GetProperty("limit").GetInt32() == 100);
            var first = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await hold.Arrived.Task;
            var second = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await Drain();
            Equal(1, f.Calls.Log.Count(call => call.Method == "music.queue.get" &&
                call.Parameters.GetProperty("offset").GetInt32() == 0 &&
                call.Parameters.GetProperty("limit").GetInt32() == 100));
            hold.Release();
            await Task.WhenAll(first, second);
            Equal(2, f.Module.QueueSurfaceRows.Count);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("queue pagination version drift performs one first-page reload without self-cancellation", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.queue.replace", new
            {
                songs = Enumerable.Range(0, 210).Select(i => Song("queue:" + i, $"队列 {i:000}")).ToArray(),
                index = 0
            });
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            Equal(100, f.Module.QueueSurfaceRows.Count);
            var start = f.Calls.Log.Count;
            f.Calls.TransformNext("music.queue.get", reply =>
            {
                var changed = JsonNode.Parse(reply.GetRawText())!.AsObject();
                changed["version"] = reply.GetProperty("version").GetInt64() + 1;
                return JsonSerializer.SerializeToElement(changed);
            });
            await f.Module.LoadMoreQueueSurfaceAsync();
            var reads = f.Calls.Log.Skip(start).Where(call => call.Method == "music.queue.get")
                .Select(call => call.Parameters.GetProperty("offset").GetInt32()).ToArray();
            True(reads.SequenceEqual(new[] { 100, 0 }), "version drift reloads the first page exactly once");
            Equal(100, f.Module.QueueSurfaceRows.Count);
            Equal("210", f.Module.QueueSurfaceCountText);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("queue view paginates only from user scroll intent and retries one failed page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.queue.replace", new
            {
                songs = Enumerable.Range(0, 210).Select(i => Song("queue:" + i, $"队列 {i:000}")).ToArray(),
                index = 0
            });
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var view = (MusicQueueView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
            var unrelatedScroll = new ScrollViewer { Height = 20, Width = 20, Content = new Border { Height = 100 }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid(); root.Children.Add(view); root.Children.Add(unrelatedScroll);
            using var host = new DesktopTestUi.Host(root, 430, 520); await Drain();
            try
            {
                var list = (ListView)view.FindName("QueueList");
                var scroll = Visuals(list).OfType<ScrollViewer>().Single();
                int Reads(int offset) => f.Calls.Log.Count(call => call.Method == "music.queue.get" && call.Parameters.GetProperty("offset").GetInt32() == offset);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain();
                Equal(0, Reads(100)); // Layout and programmatic scrolling are not request entrances.
                scroll.ChangeView(null, 0, null, true); await Drain();
                var captures = list.PointerCaptures.Count;
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(unrelatedScroll)!;
                ((IScrollProvider)peer.GetPattern(PatternInterface.Scroll)).Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain();
                Equal(0, Reads(100)); // Unrelated control scrolling does not grant queue scroll intent.
                Equal(captures, list.PointerCaptures.Count);
                f.Calls.FailNext("music.queue.get", "STORAGE_UNAVAILABLE");
                view.BrowseQueue(VirtualKey.End);
                await Until(() => f.Module.QueueSurfaceStatus == "操作失败，请重试");
                Equal(1, Reads(100));
                True(f.Module.CanLoadMoreQueueSurface, "failed page leaves one explicit retry available");
                view.BrowseQueue(VirtualKey.End);
                await Until(() => f.Module.QueueSurfaceRows.Count == 200);
                Equal(2, Reads(100));
                scroll.ChangeView(null, 0, null, true); await Drain();
                view.BrowseQueue(VirtualKey.End);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await Until(() => f.Module.QueueSurfaceRows.Count == 210);
                Equal(1, Reads(200));
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain();
                view.BrowseQueue(VirtualKey.End); await Drain();
                Equal(1, Reads(200));
                True(!f.Module.CanLoadMoreQueueSurface, "final page terminates queue pagination");
            }
            finally { f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue); }
        });
        yield return Case("shutdown drains a retired noncooperative queue surface read", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Calls.HoldNext("music.queue.get", p =>
                p.GetProperty("offset").GetInt32() == 0 && p.GetProperty("limit").GetInt32() == 100);
            var opening = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await hold.Arrived.Task;
            var closing = f.Module.PrepareShutdownAsync();
            await Drain();
            True(!closing.IsCompleted, "shutdown must wait for the physical queue read");
            hold.Release();
            await Cancelled(opening);
            await closing;
            True(!f.Module.CanCatalogBrowse, "module remains sealed after preparation");
        });
        yield return Case("late navigation/search responses and cancellation never replace current page", async () =>
        {
            await using var f = await Fixture.Create(); var id = await f.Collection("查找测试", 210); await f.Module.NavigateAsync("playlist/" + id, default);
            var retired = f.Module.Songs[0];
            var search = f.Calls.HoldNext("music.library.search"); var old = f.Module.SearchAsync("曲目000", default); await search.Arrived.Task;
            await f.Module.SearchAsync("曲目019", default); await Cancelled(old); search.Release(); await Drain();
            Equal("曲目0190", f.Module.Songs[0].Title);
            var page = f.Calls.HoldNext("music.library.detail", p => p.GetProperty("offset").GetInt32() == 100);
            await f.Module.NavigateAsync("playlist/" + id, default); var oldPage = f.Module.LoadMoreCollectionAsync(); await page.Arrived.Task;
            await f.Module.NavigateAsync("favorites", default); await oldPage; page.Release(); await Drain();
            Equal("我的收藏", f.Module.Heading); Equal(0, f.Module.Songs.Count);
            f.Module.SelectedSong = retired; Equal<SongRow?>(null, f.Module.SelectedSong);
            using var cancel = new CancellationTokenSource(); var held = f.Calls.HoldNext("music.library.search");
            var cancelled = f.Module.SearchAsync("曲目", cancel.Token); await held.Arrived.Task; cancel.Cancel(); await Cancelled(cancelled);
            held.Release(); await Drain(); Equal("我的收藏", f.Module.Heading); True(!f.Module.Busy, "cancel clears busy");
        });
        yield return Case("failed collection extension keeps rows; back restores row, scroll and search page", async () =>
        {
            await using var f = await Fixture.Create(); var id = await f.Collection("返回状态", 210); var route = "playlist/" + id;
            await f.Module.NavigateAsync(route, default); f.Calls.FailNext("music.library.detail", "STORAGE_UNAVAILABLE");
            await f.Module.LoadMoreCollectionAsync();
            Equal(100, f.Module.Songs.Count); Equal("210", f.Module.DetailCountText);
            await f.Module.LoadMoreCollectionAsync(); Equal(200, f.Module.Songs.Count);
            f.Module.SelectedSong = f.Module.Songs[12]; var selected = f.Module.SelectedSong.Id;
            await RenderAsync(f.Module.View, 640, 420);
            await Drain(); // Let the successful navigation's initial restore complete first.
            var scroll = Visuals((ListView)f.Module.View.FindName("SongsGrid")).OfType<ScrollViewer>().First();
            scroll.ChangeView(null, 10, null, true); await Drain(); var offset = scroll.VerticalOffset; True(offset > 0, "real grid scroll");
            await f.Module.NavigateAsync("favorites", default); await f.Module.NavigateAsync(route, default); await Drain();
            Equal(100, f.Module.Songs.Count); Equal("210", f.Module.DetailCountText);
            Equal(selected, f.Module.SelectedSong?.Id); Equal(offset, scroll.VerticalOffset);
            await f.Module.SearchAsync("曲目", default); await f.Module.PageAsync(1); f.Module.SelectedSong = f.Module.Songs[4]; selected = f.Module.SelectedSong.Id;
            await f.Module.NavigateAsync("favorites", default); await f.Module.SearchAsync("曲目", default);
            Equal("101–200 / 210", f.Module.PageText); Equal(selected, f.Module.SelectedSong?.Id);
        });
        yield return Case("retired library refresh cannot erase new cards; internal navigation uses shell contract", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Calls.HoldNext("music.library.list"); var old = f.Module.RefreshAsync(); await hold.Arrived.Task;
            var id = await f.Collection("新卡片", 1); await f.Module.InitializeAsync(default); Equal(2, f.Module.Playlists.Count);
            hold.Release(); await old; Equal(2, f.Module.Playlists.Count);
            var routes = new List<string>(); f.Module.NavigationRequested += (_, e) => routes.Add(e.Route);
            var surfaces = new List<DesktopGlobalSurface>();
            f.Module.GlobalSurfaceRequested += (_, e) => surfaces.Add(e.Surface);
            await f.Module.OpenPlaylistAsync(id); await f.Module.OpenPlayQueueAsync(); await f.Module.OpenPlaylistAsync("favorites");
            True(routes.SequenceEqual(new[] { "playlist/" + id, "favorites" }), "pages use shell navigation");
            True(surfaces.SequenceEqual(new[] { DesktopGlobalSurface.PlayQueue }), "queue is a single App surface");
            True(f.Module.IsCollectionPage && f.Module.Heading == "我的收藏", "shell owns commit/history");
        });
        yield return Case("large collection uses server operations and queue current outside first page", async () =>
        {
            await using var f = await Fixture.Create(); var id = await f.Collection("大歌单", 1000);
            await f.Module.NavigateAsync("playlist/" + id, default); f.Dialogs.Paths = f.MakeFiles(3); await f.Module.ImportFilesAsync();
            Equal(1003, f.Module.Playlists.Single(p => p.Id == id).Count);
            for (var i = 0; i < 10; i++) await f.Module.LoadMoreCollectionAsync();
            Equal(1003, f.Module.Songs.Count); Equal("1003", f.Module.DetailCountText);
            Equal("", f.Module.PageText);
            f.Module.SelectedSong = f.Module.Songs[1002]; var title = f.Module.SelectedSong.Title;
            await f.Module.PlaySelectionAsync(); Equal(1, f.Playback.Opens.Count);
            Equal(1002, f.Calls.Last("music.queue.playCollection").GetProperty("index").GetInt32());
            Equal(0, f.Calls.Count("music.queue.replace")); Equal(title, f.Module.CurrentTitle);
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            Equal(100, f.Module.QueueSurfaceRows.Count); Equal("1003", f.Module.QueueSurfaceCountText); Equal(title, f.Module.CurrentTitle);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
            await f.Module.NavigateAsync("playlist/" + id, default); f.Module.SelectedSong = f.Module.Songs[1]; await f.Module.RemoveSongAsync();
            Equal(1002, f.Module.Playlists.Single(p => p.Id == id).Count); Equal(1, f.Calls.Count("music.library.removeSong"));
        });
        yield return Case("history presents seven date groups and five queues with working restore/remove", async () =>
        {
            await using var f = await Fixture.Create();
            for (var i = 0; i < 8; i++)
            {
                var queue = await f.Rpc("music.queue.replace", new { songs = new[] { Song("history-a"), Song("history-b") }, index = 0,
                    context = new { sourceTitle = "测试队列" + i, queueSource = new { id = "fixture:" + i, title = "测试队列" + i } } });
                await f.Rpc("music.history.recordPlayed", new { entryId = queue.GetProperty("currentId").GetString(),
                    at = new DateTimeOffset(DateTime.Today.AddDays(i - 8).AddHours(12)).ToUnixTimeMilliseconds() });
            }
            await f.Module.NavigateAsync("history", default); Equal(7, f.Module.HistoryDates.Count); Equal(1, f.Module.Songs.Count);
            var day = f.Module.HistoryDates[^1]; var picker = (ComboBox)f.Module.View.FindName("HistoryPicker"); picker.SelectedItem = day;
            await Until(() => !f.Module.Busy); Equal(day, f.Module.HistoryDate); Equal(day, picker.SelectedItem);
            f.Calls.FailNext("music.history.get", "STORAGE_UNAVAILABLE"); picker.SelectedItem = f.Module.HistoryDates[0];
            await Until(() => !f.Module.Busy); Equal(day, f.Module.HistoryDate); Equal(day, picker.SelectedItem);
            await f.Module.NavigateAsync("archives", default); Equal(5, f.Module.Archives.Count);
            var id = f.Module.Archives[0].Id; await f.Module.DeleteArchiveAsync(id); Equal(4, f.Module.Archives.Count);
            var surfaces = new List<DesktopGlobalSurface>();
            f.Module.GlobalSurfaceRequested += (_, e) => surfaces.Add(e.Surface);
            await f.Module.RestoreArchiveAsync(f.Module.Archives[0].Id);
            True(surfaces.SequenceEqual(new[] { DesktopGlobalSurface.PlayQueue }), "restore requests the global queue");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            Equal(2, f.Module.QueueSurfaceRows.Count); Equal(0, f.Playback.Opens.Count); // restore never autoplays
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
            await f.Module.NavigateAsync("history", default); Equal(day, f.Module.HistoryDate);
        });
        yield return Case("song row menu captures right clicked target and rejects blank and retired actions", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            using var host = new DesktopTestUi.Host(f.Module.View, 900, 700); await Drain();
            var page = (MusicView)f.Module.View;
            var grid = (ListView)page.FindName("SongsGrid");
            f.Module.SelectedSong = f.Module.Songs[0];
            var second = f.Module.Songs[1];
            var row = Visuals((ListViewItem)grid.ContainerFromIndex(1)).OfType<Grid>().First(panel => panel.DataContext is SongRow);
            var menu = page.OpenSongMenu(row)!; await Drain();
            True(ReferenceEquals(second, f.Module.SelectedSong), "row menu selects its actual target");
            var favorite = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "收藏");
            var invoke = (IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(favorite)!.GetPattern(PatternInterface.Invoke);
            f.Module.SelectedSong = f.Module.Songs[0];
            invoke.Invoke(); await Until(() => !f.Module.Busy);
            await f.Module.NavigateAsync("favorites", default);
            Equal(second.Id, f.Module.Songs.Single().Id);
            int writes = f.Calls.Count("music.library.addSongs");
            invoke.Invoke(); await Drain();
            Equal(writes, f.Calls.Count("music.library.addSongs"));
            True(page.OpenSongMenu(grid) is null && f.Module.SelectedSong is null,
                "empty area cannot reopen the previous target menu");
        });
        yield return Case("row actions, transport, seek, volume and global queue confirmations", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            var page = (MusicView)f.Module.View;
            var grid = (ListView)page.FindName("SongsGrid");
            using var host = new DesktopTestUi.Host(page, 900, 700); await Drain();
            await RenderAsync(f.Module.PlayerView, 860, 92);
            var second = (ListViewItem)grid.ContainerFromIndex(1);
            var secondBody = Visuals(second).OfType<Grid>().First(panel => panel.DataContext is SongRow);
            var menu = page.OpenSongMenu(secondBody)!; await Drain();
            True(menu.Items.OfType<MenuFlyoutItem>().Take(3).Select(item => item.Text).SequenceEqual(
                new[] { "播放", "下一首播放", "收藏" }), "song menu exposes the primary row actions");
            True(grid.Items.Cast<SongRow>().SequenceEqual(f.Module.Songs), "presentation retains owner row order");
            MusicTestUi.Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "收藏")); await Until(() => !f.Module.Busy);
            Equal(1, f.Module.Playlists.Single(p => p.Id == "favorites").Count);
            menu = page.OpenSongMenu(secondBody)!; await Drain();
            MusicTestUi.Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "下一首播放")); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.append"));
            var row = (ListViewItem)grid.ContainerFromIndex(0);
            var rowButtons = Visuals(row).OfType<Button>().ToDictionary(AutomationProperties.GetName);
            Equal(Visibility.Visible, rowButtons["收藏"].Visibility);
            True(rowButtons["下一首播放"].Opacity == 0 && rowButtons["更多"].Opacity == 0,
                "secondary row actions stay visually quiet without hover or focus");
            rowButtons["下一首播放"].Focus(FocusState.Keyboard); await Task.Delay(200); await Drain();
            True(rowButtons["下一首播放"].Opacity > .99 && rowButtons["更多"].Opacity > .99,
                "real row keyboard focus reveals secondary actions");
            Click(rowButtons["下一首播放"]); await Until(() => !f.Module.Busy);
            True(ReferenceEquals(f.Module.Songs[0], f.Module.SelectedSong), "inline action selects its own song");
            Equal(2, f.Calls.Count("music.queue.append"));
            menu = page.OpenSongMenu(rowButtons["更多"])!; await Drain();
            True(menu.IsOpen && ReferenceEquals(menu.XamlRoot, rowButtons["更多"].XamlRoot), "more action opens the row context menu in the shared root");
            await AssertOpenedSongMenuStyles(menu, rowButtons["更多"]); menu.Hide();
            f.Module.SelectedSong = f.Module.Songs[0];
            True(await page.ExecuteSongKeyAsync(VirtualKey.Enter), "Enter command plays selected row"); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Opens.Count); f.Playback.Start();
            Click(FindButton(f.Module.PlayerView, "播放或暂停")); await Until(() => !f.Module.Busy); Equal(1, f.Playback.Pauses);
            Click(FindButton(f.Module.PlayerView, "播放或暂停")); await Until(() => !f.Module.Busy); Equal(1, f.Playback.Resumes);
            Click(FindButton(f.Module.PlayerView, "下一首")); await Until(() => !f.Module.Busy); Equal(2, f.Playback.Opens.Count);
            Click(FindButton(f.Module.PlayerView, "上一首")); await Until(() => !f.Module.Busy); Equal(3, f.Playback.Opens.Count);
            f.Playback.Start(); await f.Module.SeekAsync(42); Equal(42d, f.Playback.Seek);
            var volume = (Slider)f.Module.PlayerView.FindName("VolumeSlider");
            Equal("音量", AutomationProperties.GetName(volume));
            volume.Value = 31; Equal(31, f.Playback.Volume);
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            f.Dialogs.Confirmation = false; await f.Module.ClearQueueSurfaceAsync();
            Equal(3, f.Module.QueueSurfaceRows.Count);
            await f.Module.RemoveQueueSurfaceItemAsync(f.Module.QueueSurfaceRows[1]);
            await Until(() => !f.Module.Busy && f.Module.QueueSurfaceRows.Count == 2);
            f.Dialogs.Confirmation = true; await f.Module.ClearQueueSurfaceAsync();
            await Until(() => !f.Module.Busy && f.Module.QueueSurfaceRows.Count == 0);
            True(!f.Module.CanPlay, "empty transport disabled");
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("ungranted replacement stops old audio and cannot seek the old session", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start();
            var id = await f.Collection("未授权文件", 1); await f.Module.NavigateAsync("playlist/" + id, default);
            f.Module.SelectedSong = f.Module.Songs[0]; await f.Module.PlaySelectionAsync();
            Equal(1, f.Playback.Opens.Count); True(!f.Playback.HasMedia, "old audio stopped before resolving replacement");
            True(!f.Module.CanSeek, "new metadata does not authorize old media"); Equal("文件不可用", f.Module.Status);
        });
        yield return Case("failed queue commit never starts media and the same action retries without a manual reload", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            f.Calls.FailNext("music.queue.playCollection", "STORAGE_UNAVAILABLE"); await f.Module.PlaySelectionAsync();
            Equal(0, f.Playback.Opens.Count); True(!f.Module.CanPlay, "failed mutation blocks media");
            await f.Module.PlaySelectionAsync();
            Equal(2, f.Calls.Count("music.queue.playCollection"));
            Equal(1, f.Playback.Opens.Count); True(f.Module.CanPlay, "successful retry restores the current queue");
        });
        yield return Case("playlist navigation accepts a larger unused total without request expansion", async () =>
        {
            await using var f = await Fixture.Create();
            var before = f.Module.Playlists.ToArray();
            var start = f.Calls.Count("music.library.list");
            f.Calls.TransformNext("music.library.list", reply =>
            {
                var changed = JsonNode.Parse(reply.GetRawText())!.AsObject();
                changed["total"] = 100_001;
                return JsonSerializer.SerializeToElement(changed);
            });
            await f.Module.RefreshAsync();
            Equal(start + 1, f.Calls.Count("music.library.list"));
            True(before.Select(row => row.Id).SequenceEqual(f.Module.Playlists.Select(row => row.Id)),
                "a larger total does not discard the actual returned navigation rows");
            Equal("", f.Module.Status);
        });
        yield return Case("malformed success preserves old page; failed runtime shutdown is asserted", async () =>
        {
            await using (var f = await Fixture.Create())
            {
                var id = await f.Collection("保持页面", 1); await f.Module.NavigateAsync("playlist/" + id, default);
                f.Calls.TransformNext("music.library.detail", _ => BridgeCodec.Value(new { ok = true, songs = Array.Empty<object>() }));
                await f.Module.RefreshAsync(); Equal(1, f.Module.Songs.Count); Equal("操作失败，请重试", f.Module.Status);
                Equal(0, f.Playback.Opens.Count);
            }
            var root = NewRoot(); await using var store = new NativeDatabase(root);
            await using var native = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
            var runtime = new FailedApplication(); var module = new MusicCapability(runtime, store, native);
            await Failure(() => module.InitializeAsync(default), "STORAGE_UNAVAILABLE");
            Equal(0, module.Playlists.Count); Equal("", module.Status); True(!native.HasMedia, "no phantom media");
            // A shutdown flush failure is a failure, not an unconditional catch in await using.
            await Failure(() => module.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE");
            await Failure(() => module.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE"); Equal(1, runtime.Prepares);
        });
        yield return Case("prepare seals admission, drains accepted writes, then dispose shares one flush", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var history = f.Calls.HoldNext("music.history.recordPlayed"); f.Playback.Start(); await history.Arrived.Task;
            var mode = f.Calls.HoldNext("music.queue.mode"); var first = f.Module.ChangeModeAsync(2); await mode.Arrived.Task;
            var second = f.Module.ChangeModeAsync(3); // Already accepted, must drain rather than be dropped.
            var search = f.Calls.HoldNext("music.library.search"); var loading = f.Module.SearchAsync("本地", default); await search.Arrived.Task;
            var preparation = f.Module.PrepareShutdownAsync();
            True(!f.Module.CanEdit, "admission closes synchronously");
            True(ReferenceEquals(preparation, f.Module.PrepareShutdownAsync()), "prepare callers share the same task");
            await Cancelled(loading);
            Equal(0, f.Playback.Subscribers);
            True(!preparation.IsCompleted, "admitted writes still draining");
            await f.Module.ChangeModeAsync(1); await f.Module.RefreshAsync();
            await f.Module.InitializeAsync(default); await f.Module.NavigateAsync("favorites", default); await f.Module.SearchAsync("ignored", default);
            await f.Module.OpenPlaylistAsync("favorites"); await f.Module.OpenPlayQueueAsync();
            f.Playback.End(); f.Playback.Start(); f.Playback.Fail();
            Equal(1, f.Calls.Count("music.history.recordPlayed")); Equal(0, f.Calls.Count("music.queue.next"));
            Equal(1, f.Calls.Count("music.library.search")); Equal(0, f.Calls.Count("application.prepare"));
            mode.Release(); await first; await second;
            Equal(2, f.Calls.Count("music.queue.mode")); Equal(0, f.Calls.Count("application.prepare"));
            True(!preparation.IsCompleted, "Started write is not cancelled to finish shutdown");
            history.Release(); await preparation; search.Release(); await Drain();
            Equal(1, f.Calls.Count("application.prepare"));
            Equal("application.prepare", f.Calls.Log[^1].Method);
            var token = f.Calls.Log.Last(call => call.Method == "music.queue.mode").Token;
            True(!token.IsCancellationRequested, "prepare retains the module lifetime");
            True(!token.WaitHandle.WaitOne(0), "prepare has not disposed the lifetime");
            await f.Module.DisposeAsync(); await f.Module.DisposeAsync();
            Equal(1, f.Calls.Count("application.prepare")); True(token.IsCancellationRequested, "final disposal cancels lifetime");
            Equal(1, f.Calls.DisposeCalls); Equal(0, f.Runtime.DisposeCalls);
            Equal(3, await f.SavedMode());
            True((await MusicTestStores.For(f.Store).ReadAsync("local")).Revision.Generation > 0, "product still owns the store");
        });
        yield return Case("failed prepare preserves runtime/store and final disposal observes its cached failure", async () =>
        {
            var f = await Fixture.Create();
            f.Calls.FailNext("application.prepare", "STORAGE_UNAVAILABLE");
            try
            {
                await f.Module.ChangeModeAsync(2);
                var token = f.Calls.Log.Last(call => call.Method == "music.queue.mode").Token;
                var before = (await MusicTestStores.For(f.Store).ReadAsync("local")).Revision;
                var preparation = f.Module.PrepareShutdownAsync();
                await Failure(() => preparation, "STORAGE_UNAVAILABLE");
                await Failure(() => f.Module.PrepareShutdownAsync(), "STORAGE_UNAVAILABLE");
                True(ReferenceEquals(preparation, f.Module.PrepareShutdownAsync()), "failed preparation is not silently retried");
                True(!token.IsCancellationRequested && !token.WaitHandle.WaitOne(0), "failed prepare retains a live lifetime");
                Equal(0, f.Playback.Subscribers); Equal(0, f.Calls.DisposeCalls);
                True(!f.Module.CanEdit, "failure does not reopen business admission");
                var count = f.Calls.Log.Count;
                await f.Module.ChangeModeAsync(1);
                await f.Module.InitializeAsync(default); await f.Module.NavigateAsync("favorites", default); await f.Module.SearchAsync("ignored", default);
                await f.Module.OpenPlayQueueAsync(); f.Playback.Start(); f.Playback.End();
                Equal(count, f.Calls.Log.Count);
                Equal(before, (await MusicTestStores.For(f.Store).ReadAsync("local")).Revision);
                Equal(2, await f.SavedMode());
                // The host may now offer explicit force-exit. Final cleanup must
                // observe the same failed task and still release module resources.
                await Failure(() => f.Module.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE");
                await Failure(() => f.Module.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE");
                True(token.IsCancellationRequested, "failed finalization still cleans up lifetime");
                Equal(1, f.Calls.Count("application.prepare")); Equal(1, f.Calls.DisposeCalls); Equal(0, f.Runtime.DisposeCalls);
                Equal(before, (await MusicTestStores.For(f.Store).ReadAsync("local")).Revision);
                Equal(2, await f.SavedMode());
            }
            finally { await Failure(() => f.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE"); }
        });
        yield return Case("direct dispose cancels late navigation, drains history and flushes only once", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var history = f.Calls.HoldNext("music.history.recordPlayed"); f.Playback.Start(); await history.Arrived.Task;
            var search = f.Calls.HoldNext("music.library.search"); var pending = f.Module.SearchAsync("本地", default); await search.Arrived.Task;
            var title = f.Module.Heading; var closed = f.Module.DisposeAsync().AsTask();
            await Cancelled(pending); True(!closed.IsCompleted, "direct dispose also drains admitted history");
            history.Release(); await closed; await f.Module.DisposeAsync(); search.Release(); await Drain();
            Equal(title, f.Module.Heading); Equal(1, f.Calls.Count("application.prepare"));
            f.Playback.End(); f.Playback.Start(); await Drain(); Equal(0, f.Calls.Count("music.queue.next"));
        });
        yield return Case("two-phase cancel restores Native application and subscriptions once without replaying accepted writes", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start();
            await Until(() => f.Calls.Count("music.history.recordPlayed") == 1);
            var prepare = f.Module.PrepareShutdownAsync(); await prepare;
            Equal(0, f.Playback.Subscribers); True(!f.Module.CanEdit, "paused UI");
            await ResumeAndDrain(f);
            await f.Module.CancelShutdownPreparationAsync();
            Equal(4, f.Playback.Subscribers); True(f.Module.CanEdit, "actual UI admission resumed");
            f.Playback.Start(); await Drain(); Equal(1, f.Calls.Count("music.history.recordPlayed"));
            await f.Module.ChangeModeAsync(2); Equal(2, f.Module.Mode);
            var again = f.Module.PrepareShutdownAsync(); True(!ReferenceEquals(prepare, again), "fresh preparation generation");
            await again; Equal(2, f.Calls.Count("application.prepare")); Equal(0, f.Playback.Subscribers);
            await ResumeAndDrain(f);
            Equal(4, f.Playback.Subscribers); Equal(2, await f.SavedMode());
        });
        yield return Case("failed UI preparation can resume explicitly without disposing application runtime or store", async () =>
        {
            await using var f = await Fixture.Create();
            f.Calls.FailNext("application.prepare", "STORAGE_UNAVAILABLE");
            await Failure(() => f.Module.PrepareShutdownAsync(), "STORAGE_UNAVAILABLE");
            await ResumeAndDrain(f);
            True(f.Module.CanEdit, "failed product preparation is recoverable");
            Equal(4, f.Playback.Subscribers); Equal(0, f.Calls.DisposeCalls); Equal(0, f.Runtime.DisposeCalls);
            await f.Module.ChangeModeAsync(3); Equal(3, f.Module.Mode);
            await f.Module.PrepareShutdownAsync(); Equal(2, f.Calls.Count("application.prepare"));
        });
        yield return Case("local document restart restores library/mode/current without starting media", async () =>
        {
            var root = NewRoot(); string id, title;
            await using (var first = await Fixture.Create(root, persistent: true))
            {
                id = await first.PlayableCollection(); await first.Module.PlaySelectionAsync(); await first.Module.ChangeModeAsync(2);
                title = first.Module.CurrentTitle;
            }
            await using var second = await Fixture.Create(root, persistent: true);
            Equal(2, second.Module.Mode); Equal(title, second.Module.CurrentTitle); Equal(0, second.Playback.Opens.Count);
            await second.Module.NavigateAsync("playlist/" + id, default); Equal(3, second.Module.Songs.Count);
            await second.Module.TogglePlaybackAsync(); Equal(1, second.Playback.Opens.Count);
        });
        yield return Case("native shell 860/1260 public theme and keyboard focus", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.NavigateAsync("favorites", default);
            int closeCount = 0;
            var shell = new ShellWindow([f.Module], async () => { closeCount++; await f.Module.DisposeAsync(); });
            var closed = new TaskCompletionSource(); shell.Closed += (_, _) => closed.TrySetResult();
            try
            {
                int before = f.Calls.Log.Count; shell.PrepareForRender(); shell.PrepareForRender(); shell.Activate(); await Drain();
                Equal(before, f.Calls.Log.Count);
                var root = DesktopTestUi.WindowRoot(shell);
                foreach (var size in new[] { new Size(860, 600), new Size(1260, 790) })
                {
                    shell.Resize(size.Width, size.Height); await Drain(); root.UpdateLayout();
                    AssertPlayerBounds(f.Module.PlayerView, f.Module.PlayerView.ActualWidth);
                    True(f.Module.View.ActualWidth > 500, "minimum main content usable");
                }
                var textbox = (TextBox)root.FindName("SearchBox");
                True(ReferenceEquals(textbox.Style, DesktopVisual.Resource(textbox, "MeowFilm.TextBox")),
                    "the one App search box consumes the shared TextBox style");
                var mainHost = Visuals(root).OfType<ContentControl>().Single(host => ReferenceEquals(host.Content, f.Module.View));
                var surface = (Panel)VisualTreeHelper.GetParent(mainHost);
                var settingsHost = surface.Children.OfType<ContentControl>().Single(host => ReferenceEquals(host.Content, f.Module.SettingsView));
                mainHost.Visibility = Visibility.Collapsed; settingsHost.Visibility = Visibility.Visible;
                foreach (var size in new[] { new Size(860, 600), new Size(1260, 790) })
                {
                    shell.Resize(size.Width, size.Height); await Drain(); root.UpdateLayout();
                    AssertPlayerBounds(f.Module.PlayerView, f.Module.PlayerView.ActualWidth);
                }
                DesktopTheme.SetIsHighContrast(root, true);
                var navigation = (Panel)f.Module.SettingsView.FindName("SettingsNavigation");
                navigation.Children.OfType<RadioButton>().ElementAt(2).IsChecked = true; await Drain();
                var picker = Visuals(f.Module.SettingsView).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "起播目标音质");
                True(picker.Focus(FocusState.Keyboard) && DesktopVisual.ContainsFocus(picker), "native keyboard focus enters the settings picker");
                True(picker.UseSystemFocusVisuals, "picker supplies the system high-contrast focus visual");
                settingsHost.Visibility = Visibility.Collapsed; mainHost.Visibility = Visibility.Visible; await Drain();
                var mode = FindButton(f.Module.PlayerView, f.Module.ModeText);
                True(mode.Focus(FocusState.Keyboard) && mode.UseSystemFocusVisuals, "shared keyboard focus visual supplied");
                var playerView = (UserControl)f.Module.PlayerView;
                True(playerView.Background is null && ((Grid)playerView.Content).Background is SolidColorBrush { Color.A: 0 },
                    "high-contrast footer shows the window backdrop instead of painting its own");
                var progress = (SeekBar)f.Module.PlayerView.FindName("Progress");
                True(progress.Focus(FocusState.Keyboard) && !MusicVisuals.CanAnimate(progress),
                    "continuous seek rail honors the common root high-contrast state");
                f.Playback.Start(); await Drain();
                True(Visuals(root).OfType<Button>().Where(DesktopVisual.IsVisible).All(button => button.UseSystemFocusVisuals),
                    "visible shell and page actions retain system focus visuals");
                Equal(before + 1, f.Calls.Log.Count);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain(); }
            Equal(1, closeCount); True(shell.ShutdownSucceeded, "successful once-only shell shutdown");
        });
    }

    private static TestCase Case(string name, Func<Task> body) => new("music-ui: " + name, () => OnSta(body));
    private static object Song(string id, string? title = null) =>
        MusicSongFixture.Local(id, title ?? id, "测试歌手", "测试专辑", 180);
    private static string NewRoot()
    {
        var parent = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Missing isolated test output");
        var root = Path.Combine(parent, "music-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root;
    }
    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Music assertion at line {line}: expected {expected}, actual {actual}");
    }
    private static void True(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static async Task Cancelled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }
    private static async Task Failure(Func<Task> action, string code)
    {
        try { await action(); } catch (Exception error) when (error.Message == code) { return; }
        throw new InvalidOperationException("Expected bounded failure " + code);
    }
    private static async Task ResumeAndDrain(Fixture fixture)
    {
        await fixture.Module.CancelShutdownPreparationAsync();
        Equal(4, fixture.Playback.Subscribers);
        await Until(() => fixture.Module.CanEdit);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("UI condition did not settle");
            await MusicTestUi.Drain(); if (!predicate()) await Task.Delay(1);
        }
        await Drain();
    }
    private static Task Drain() => MusicTestUi.Drain();
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static Button FindButton(FrameworkElement view, string label) => Visuals(view).OfType<Button>()
        .Single(b => AutomationProperties.GetName(b) == label || b.Content is string text && text == label);
    private static void Click(Button button) { True(button.IsEnabled, "clickable action enabled"); DesktopTestUi.Invoke(button); }
    private static void AssertPlayerBounds(FrameworkElement view, double width)
    {
        var panels = new[] { (FrameworkElement)view.FindName("MetadataPanel"),
            (FrameworkElement)((Button)view.FindName("ModeButton")).Parent,
            (FrameworkElement)((Button)view.FindName("QualityButton")).Parent };
        var bounds = panels.Select(panel => DesktopTestUi.Bounds(panel, view)).ToArray();
        True(width >= 820, "player spans shell width");
        for (var i = 0; i < bounds.Length; i++)
        {
            True(bounds[i].Left >= 0 && bounds[i].Right <= width + 0.5,
                $"player panel within minimum window (panel={i}, bounds={bounds[i]}, width={width}, actual={view.ActualWidth})");
            if (i > 0) True(bounds[i - 1].Right <= bounds[i].Left + 0.5, "player columns do not overlap");
        }
        foreach (var control in Visuals(view).OfType<Control>().Where(c => c is Button or Slider))
        {
            var rect = DesktopTestUi.Bounds(control, view);
            double upperEnvelope = control is SeekBar { RailAtTop: true } ? 43 : 0;
            True(rect.Left >= 0 && rect.Right <= width + 0.5 && rect.Top >= -upperEnvelope &&
                rect.Bottom <= view.ActualHeight + 0.5, "player control not clipped: " + AutomationProperties.GetName(control) + " " + rect);
        }
    }
    private static readonly AsyncLocal<List<DesktopWindow>?> renderWindows = new();
    private static void Render(FrameworkElement view, int width, int height)
    {
        if (view.XamlRoot is null)
        {
            var windows = renderWindows.Value ?? throw new InvalidOperationException("Render must run inside a UI case");
            var window = MusicTestUi.Window(view, width, height);
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.SetBorderAndTitleBar(false, false);
            windows.Add(window); window.Activate();
        }
        else if (DesktopWindow.GetWindow(view) is { } window && ReferenceEquals(window.Content, view)) window.Resize(width, height);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
    }
    private static async Task RenderAsync(FrameworkElement view, int width, int height)
    {
        Render(view, width, height); await Drain();
        var pixels = await DesktopTestUi.Pixels(view, width, height);
        True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 1000, "nonempty native controls/text");
    }
    private sealed class FailedApplication : MusicApplicationTestDouble
    {
        public int Prepares;
        public override Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BridgeCodec.Value(new { ok = false, code = "STORAGE_UNAVAILABLE" }));
        }
        public override Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
        { Prepares++; return Task.FromException(new InvalidOperationException("STORAGE_UNAVAILABLE")); }
    }
    private sealed record Invocation(string Method, JsonElement Parameters, CancellationToken Token);
    private sealed class HeldReply
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<JsonElement, JsonElement>? Transform;
        public void Release() => release.TrySetResult();
        public async Task<JsonElement> Hold(JsonElement reply)
        { Arrived.TrySetResult(); await release.Task; return Transform?.Invoke(reply) ?? reply; }
    }
    private sealed class ObservedApplication(IMusicApplication inner, IMusicPlaybackFeedback? feedback = null) : MusicApplicationForwarder(inner)
    {
        public override IMusicPlaybackFeedback Feedback => feedback ?? base.Feedback;
        private sealed record Rule(string Method, Func<JsonElement, bool> Match, Func<JsonElement, Task<JsonElement>> Apply, bool Before);
        private readonly List<Rule> rules = [];
        private readonly List<HeldReply> holds = [];
        public List<Invocation> Log { get; } = [];
        public int DisposeCalls { get; private set; }
        public int Count(string method) => Log.Count(call => call.Method == method);
        public JsonElement Last(string method) => Log.Last(call => call.Method == method).Parameters;
        public HeldReply HoldNext(string method, Func<JsonElement, bool>? match = null, string? failure = null)
        {
            var hold = new HeldReply(); holds.Add(hold);
            rules.Add(new(method, match ?? (_ => true), failure is null ? hold.Hold :
                _ => hold.Hold(BridgeCodec.Value(new { ok = false, code = failure })), failure is not null));
            return hold;
        }
        public void FailNext(string method, string code) => rules.Add(new(method, _ => true,
            _ => Task.FromResult(BridgeCodec.Value(new { ok = false, code })), true));
        public void TransformNext(string method, Func<JsonElement, JsonElement> transform) => rules.Add(new(method, _ => true,
            reply => Task.FromResult(transform(reply)), false));
        public void ReleaseAll() { foreach (var hold in holds) hold.Release(); }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var dto = BridgeCodec.Value(parameters ?? new { }); Log.Add(new(method, dto, cancellationToken));
            var rule = rules.FirstOrDefault(rule => rule.Method == method && rule.Match(dto));
            if (rule is not null) rules.Remove(rule);
            if (rule?.Before == true) return await rule.Apply(dto);
            var result = await Inner.ExecuteAsync(method, parameters, cancellationToken);
            return rule is null ? result : await rule.Apply(result); // Intentionally noncooperative late reply.
        }
        public override async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
        {
            Log.Add(new("application.prepare", BridgeCodec.Value(new { }), cancellationToken));
            var rule = rules.FirstOrDefault(rule => rule.Method == "application.prepare");
            if (rule is not null)
            {
                rules.Remove(rule);
                var reply = await rule.Apply(BridgeCodec.Value(new { }));
                if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                    throw new InvalidOperationException(reply.GetProperty("code").GetString());
            }
            await Inner.PrepareShutdownAsync(cancellationToken);
        }
        public override Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
            => Inner.CancelShutdownPreparationAsync(cancellationToken);
        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await Inner.DisposeAsync();
        }
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public string[]? Paths; public bool Confirmation = true;
        public string? ScriptPath; public string[]? ScriptPaths;
        public bool Confirm(string message, string title) => Confirmation;
        public string[]? SelectAudioFiles() => Paths;
        public string[]? SelectScriptFiles() => ScriptPaths ?? (ScriptPath is null ? null : [ScriptPath]);
        public string? SelectScriptFile() => ScriptPath;
    }
    private class PlaybackProbe : IMusicPlayback
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }
        public double PositionSeconds => Seek;
        public double DurationSeconds => 180;
        public int Volume { get; set; } = 80;
        public double Seek; public int SeekCalls, Pauses, Resumes, Stops;
        public List<(string Entry, Uri Uri)> Opens { get; } = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started, Ended, Failed;
        public int Subscribers => (PropertyChanged?.GetInvocationList().Length ?? 0) +
            (Started?.GetInvocationList().Length ?? 0) + (Ended?.GetInvocationList().Length ?? 0) + (Failed?.GetInvocationList().Length ?? 0);
        private void Changed() => PropertyChanged?.Invoke(this, new(""));
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { token.ThrowIfCancellationRequested(); OwnerId = owner; Identity = identity; HasMedia = true; IsPlaying = false; Opens.Add((identity, uri)); Changed(); return Task.CompletedTask; }
        public void Start() { IsPlaying = true; Started?.Invoke(this, EventArgs.Empty); Changed(); }
        public void End() { HasMedia = false; IsPlaying = false; Ended?.Invoke(this, EventArgs.Empty); Changed(); }
        public void Fail() { HasMedia = false; IsPlaying = false; Failed?.Invoke(this, EventArgs.Empty); Changed(); }
        public Task PauseAsync() { Pauses++; IsPlaying = false; Changed(); return Task.CompletedTask; }
        public Task ResumeAsync() { Resumes++; IsPlaying = true; Changed(); return Task.CompletedTask; }
        public Task StopAsync() { Stops++; HasMedia = false; IsPlaying = false; Changed(); return Task.CompletedTask; }
        public Task SeekAsync(double seconds) { SeekCalls++; Seek = seconds; Changed(); return Task.CompletedTask; }
        public void Tick(double seconds)
        { Seek = seconds; PropertyChanged?.Invoke(this, new(nameof(PositionSeconds))); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root;
        public required IStorageDatabase Store;
        public required IStorageDatabase CacheBacking;
        public required NativeTestDatabase Cache;
        public required NativeMusicScriptTestRuntime Runtime;
        public required MusicNativeApplication Application;
        public required ObservedApplication Calls;
        public required MusicCapability Module;
        public readonly PlaybackProbe Playback;
        public readonly Dialogs Dialogs;
        private Fixture(PlaybackProbe playback, Dialogs dialogs) { Playback = playback; Dialogs = dialogs; }
        public static async Task<Fixture> Create(string? root = null, bool persistent = false, NativeAudioPlayback? native = null,
            IMusicPlayback? output = null, bool withPlaybackCache = false, IMusicPlaybackFeedback? feedback = null)
        {
            root ??= NewRoot();
            IStorageDatabase store = persistent
                ? new NativeDatabase(root) : new DocumentProbe();
            string cacheRoot = Path.Combine(root, "cache-documents");
            Directory.CreateDirectory(cacheRoot);
            IStorageDatabase cacheBacking = persistent
                ? new NativeDatabase(cacheRoot)
                : new DocumentProbe();
            var cache = new NativeTestDatabase(cacheBacking);
            var runtime = new NativeMusicScriptTestRuntime(); MusicCapability? module = null;
            if (persistent) MusicTestStores.Use(store, root);
            var application = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            try
            {
                var calls = new ObservedApplication(application, feedback); var playback = new PlaybackProbe(); var dialogs = new Dialogs();
                module = native is null
                    ? new MusicCapability(calls, store, output ?? playback, withPlaybackCache ?
                        new MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache(Path.Combine(root, "playback-cache"), cache) : null, dialogs)
                    : new MusicCapability(calls, store, native, dialogs);
                await module.InitializeAsync(default);
                Equal("wy", module.CatalogSourcePlatform);
                True(module.CanEditCatalogSource && !module.HasCatalogSourceError, "shared UI fixture restores the native catalog source");
                Equal(1, calls.Count("music.catalogSource.settings.get"));
                Equal(0, calls.Count("music.catalogSource.settings.save"));
                await module.NavigateAsync("favorites", default);
                True(!runtime.HasStarted, "native initialization never starts Node");
                return new(playback, dialogs) { Root = root, Store = store, CacheBacking = cacheBacking, Cache = cache, Runtime = runtime, Application = application, Calls = calls, Module = module };
            }
            catch
            {
                try { if (module is not null) await module.DisposeAsync(); else await application.DisposeAsync(); }
                finally { await cache.DisposeAsync(); await cacheBacking.DisposeAsync(); await runtime.DisposeAsync(); await store.DisposeAsync(); }
                throw;
            }
        }
        public async Task<JsonElement> Rpc(string method, object? parameters = null)
        {
            var result = await Application.ExecuteAsync(method, parameters ?? new { });
            True(result.GetProperty("ok").GetBoolean(), "fixture native command failed: " + method + ": " + result.GetRawText()); return result;
        }
        public Task<int> SavedMode() => Task.FromResult(
            MusicTestStores.For(Store).Snapshot().Areas.TryGetValue("playback-settings", out var state)
                ? state!["mode"]!.GetValue<int>() : 1);
        public async Task<string> Collection(string name, int count)
        {
            var result = await Rpc("music.library.create", new { name, songs = Enumerable.Range(0, count).Select(i => Song("fixture:" + i, $"曲目{i:0000}")).ToArray() });
            return result.GetProperty("collection").GetProperty("id").GetString()!;
        }
        public string[] MakeFiles(int count) => Enumerable.Range(0, count).Select(i =>
        {
            var path = Path.Combine(Root, $"本地音频测试{i}.wav"); File.WriteAllBytes(path, [0, 1, 2, 3]); return path;
        }).ToArray(); // File grants only; deliberately not decoded by the probe.
        public async Task<string> PlayableCollection()
        {
            var grants = new GrantedMediaFiles(Store, [".wav"]);
            var files = await grants.GrantAsync(MakeFiles(3));
            var songs = files.Select(file =>
                MusicSongFixture.Local(file.Id, file.Title, "测试歌手", "测试专辑", 180)).ToArray();
            var result = await Rpc("music.library.create", new { name = "本地测试", songs });
            var id = result.GetProperty("collection").GetProperty("id").GetString()!;
            await Module.NavigateAsync("playlist/" + id, default); Module.SelectedSong = Module.Songs[0]; return id;
        }
        public async ValueTask DisposeAsync()
        {
            Calls.ReleaseAll();
            try { await Module.DisposeAsync(); }
            finally { try { await Cache.DisposeAsync(); await CacheBacking.DisposeAsync(); await Runtime.DisposeAsync(); } finally { await MusicTestStores.For(Store).DisposeAsync(); await Store.DisposeAsync(); } }
        }
    }
    private sealed class DocumentProbe : NativeTestDatabase {
    }
    private static Task OnSta(Func<Task> body) => TestUiHost.InvokeAsync(async () =>
    {
        var windows = new List<DesktopWindow>(); renderWindows.Value = windows;
        try { await body(); }
        finally
        {
            foreach (var window in windows) { window.Content = null; window.Close(); }
            renderWindows.Value = null; await Drain();
        }
    });
}
