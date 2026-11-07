using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Native application runs ordinary business. Only authored LX scripts use the
// script-only process. Local-store restart and synthetic-store UI races are
// separate evidence; no production storage fallback exists.
public static partial class MusicUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in PlayerChromeCases()) yield return test;
        yield return Case("test document stores authorize exact native namespaces and complete script sources", async () =>
        {
            const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            byte[] payload = [1, 2, 3];
            foreach (bool persistent in new[] { false, true })
            {
                await using IDocumentStore store = persistent ? ServiceStore() : new DocumentProbe();
                var names = AppIdentity.NativeNamespaces.Concat(AppIdentity.NativeCollections.Select(collection =>
                    $"{collection}.{hash}"));
                foreach (string ns in names)
                {
                    Equal(DocumentFingerprint.Missing, (await store.ReadAsync(ns)).Fingerprint);
                    var fingerprint = await store.WriteAsync(ns, payload, DocumentFingerprint.Missing);
                    Equal(DocumentFingerprint.FromBytes(payload), fingerprint);
                    var saved = await store.ReadAsync(ns);
                    Equal(fingerprint, saved.Fingerprint);
                    True(saved.Data is not null && saved.Data.SequenceEqual(payload), "authorized namespace roundtrip: " + ns);
                }
            }
        });
        yield return Case("test document stores reject unknown namespaces and malformed script source names", async () =>
        {
            const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            foreach (bool persistent in new[] { false, true })
            {
                await using IDocumentStore store = persistent ? ServiceStore() : new DocumentProbe();
                foreach (string collection in AppIdentity.NativeCollections)
                {
                    (string Namespace, DocumentStoreError Error)[] denied =
                    [
                        ("music.unregistered.v1", DocumentStoreError.Unavailable),
                        ($"unknown.collection.v1.{hash}", DocumentStoreError.Unavailable),
                        (collection, DocumentStoreError.Unavailable),
                        ($"{collection}.", DocumentStoreError.Unavailable),
                        ($"{collection}x.{hash}", DocumentStoreError.Unavailable),
                        ($"{collection.ToUpperInvariant()}.{hash}", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash.ToUpperInvariant()}", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash[..^1]}", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}0", DocumentStoreError.Unavailable),
                        ($"{collection}.{new string('g', 64)}", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.0", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.1", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.2147483647", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.0.extra", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.-1", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.01", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.2147483648", DocumentStoreError.Unavailable),
                        ($"{collection}.{hash}.+1", DocumentStoreError.InvalidRequest),
                        ($"{collection}.{hash}.1 ", DocumentStoreError.InvalidRequest),
                        ($"{collection}..0", DocumentStoreError.InvalidRequest)
                    ];
                    foreach (var item in denied)
                    foreach (var operation in new Func<Task>[]
                    {
                        () => store.ReadAsync(item.Namespace),
                        () => store.WriteAsync(item.Namespace, new byte[] { 1 }, DocumentFingerprint.Missing)
                    })
                    {
                        try
                        {
                            await operation();
                            throw new InvalidOperationException("Expected namespace rejection: " + item.Namespace);
                        }
                        catch (DocumentStoreException error)
                        {
                            Equal(persistent ? item.Error : DocumentStoreError.InvalidRequest, error.Error);
                        }
                    }
                }
            }
        });
        yield return Case("music settings expose current native groups and lyrics use the same playback", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            Render(f.Module.SettingsView, 1080, 680);
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
            True(sourceLabels.Contains("导入音源"), "source import action is available");
            await f.Module.PlaySelectionAsync(); var opens = f.Playback.Opens.Count;
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            Equal(opens, f.Playback.Opens.Count);
            True(f.Module.IsLyrics, "detail is the existing lyric route");
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            Render(detail, 1080, 680);
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
            var window = new Window
            {
                Content = view, Width = 1080, Height = 720, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
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
        yield return Case("record swipe cancellation fences identity visibility resize and interrupted return", async () =>
        {
            var record = new MusicRecordView
            {
                Width = 300, Height = 300, SwipeEnabled = true, SwipeIdentity = "song:1"
            };
            var window = new Window
            {
                Content = record, Width = 340, Height = 340, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
            };
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
                window.Show(); window.UpdateLayout(); await Drain();
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
                record.Width = 320; window.UpdateLayout();
                record.EndSwipe(commit: true);
                await Until(() => Math.Abs(record.SwipeOffset) < .5);
                Equal(0, commits);

                var right = RaiseKey(record, Key.Right);
                True(right.Handled, "right key owns one next preview");
                await Until(() => commits == 1 && Math.Abs(record.SwipeOffset) < .5);
                Equal(1, directions[0]);
                var left = RaiseKey(record, Key.Left);
                True(left.Handled, "left key owns one previous preview");
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
            Render(f.Module.PlayerView, 860, 92);
            foreach (int width in new[] { 1110, 2560 })
            {
                var footer = f.Module.PlayerView;
                Render(footer, width, 80);
                var play = (Button)footer.FindName("PlayButton");
                var favorite = (Button)footer.FindName("FavoriteButton");
                var center = play.TranslatePoint(new Point(play.ActualWidth / 2, 0), footer).X;
                var heart = favorite.TranslatePoint(new Point(favorite.ActualWidth / 2, 0), footer).X;
                True(Math.Abs(center - width / 2d) < 1, "transport center is the window center");
                var text = (StackPanel)footer.FindName("MetadataText");
                var textEnd = text.TranslatePoint(new Point(text.ActualWidth, 0), footer).X;
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
            await using var module = new MusicCapability(runtime, store, output, new Dialogs());
            Equal("0.4.0", module.Version);
            await module.InitializeAsync(default);
            await module.CreatePlaylistAsync("原生构造");
            Equal(1, module.Playlists.Count(p => p.Kind == "playlist"));
            await module.ReadAccountsAsync(); Equal("none", module.Accounts[0].State);
            await module.ChangeModeAsync(2); await module.PrepareShutdownAsync();
            Equal(0, runtime.Calls.Count); True(!runtime.HasStarted, "ordinary business never starts the script runtime");
            True((await store.ReadAsync("music.library.v1")).Fingerprint.Exists, "native CAS persisted");
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

            action!.Execute(null);
            var overlay = (Grid)view.FindName("PlaylistDialogOverlay");
            var panel = (Border)view.FindName("PlaylistDialogPanel");
            var input = (TextBox)view.FindName("PlaylistNameInput");
            var create = (Button)view.FindName("CreatePlaylistButton");
            Equal(Visibility.Visible, overlay.Visibility);
            True(!((FrameworkElement)view.FindName("PageContent")).IsHitTestVisible,
                "modal blocks the underlying page");
            True(!create.IsEnabled && input.MaxLength == 200, "empty local name is not submitted");
            input.Text = "恢复失败后创建";
            Equal("7 / 200", ((TextBlock)view.FindName("PlaylistNameCount")).Text);
            True(create.IsEnabled, "valid local name enables creation");
            Render(view, 900, 650);
            Equal(420d, panel.ActualWidth);
            True(Math.Abs(panel.TranslatePoint(new Point(panel.ActualWidth / 2, panel.ActualHeight / 2), view).X -
                view.ActualWidth / 2) < 1, "dialog is centered inside the existing view");

            f.Calls.FailNext("music.library.create", "STORAGE_UNAVAILABLE");
            Click(create);
            await Until(() => overlay.Visibility == Visibility.Visible && !f.Module.Busy && create.IsEnabled);
            Equal(1, f.Module.Playlists.Count);
            Equal("操作失败，请重试", f.Module.Status);

            Click(create);
            await Until(() => overlay.Visibility == Visibility.Collapsed && !f.Module.Busy &&
                f.Module.Playlists.Any(item => item.Name == "恢复失败后创建"));
            Equal("恢复失败后创建", f.Module.Heading);
            header = ((IDesktopHeaderSource)view).Header;
            True(header.HeaderActions.Select(item => item.Id).SequenceEqual(
                new[] { "music.playlist.rename", "music.playlist.delete" }), "playlist header keeps only detail actions");
            Equal(18d, ((Border)view.FindName("DetailArtworkFrame")).CornerRadius.TopLeft);
            var playAll = (Button)view.FindName("PlayAllButton");
            True(playAll.BorderBrush is not null && playAll.Background is SolidColorBrush { Color.A: 0 },
                "play-all uses the theme-aware outlined treatment");

            favorite.GroupAction!.Execute(null);
            input.Text = "取消创建";
            var escape = RaiseKey(overlay, Key.Escape);
            True(escape.Handled && overlay.Visibility == Visibility.Collapsed, "escape closes the in-view modal");
            True(!f.Module.Playlists.Any(item => item.Name == "取消创建"), "cancellation does not create");

            await f.PlayableCollection();
            True(playAll.IsEnabled, "play-all enables for a populated local playlist");
            Equal(((SolidColorBrush)view.FindResource("MeowFilm.Brush.Accent")).Color,
                ((SolidColorBrush)playAll.BorderBrush!).Color);
            DesktopTheme.SetIsHighContrast(view, true);
            view.UpdateLayout();
            Equal(SystemColors.WindowTextColor, ((SolidColorBrush)playAll.BorderBrush!).Color);
            Equal(SystemColors.WindowTextColor, ((SolidColorBrush)playAll.Foreground).Color);
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
            True(f.Calls.Log.All(call => !call.Parameters.GetRawText().Contains("music.files.v1", StringComparison.Ordinal)), "grant namespace native-only");
            True(f.Calls.Log.All(call => !call.Parameters.GetRawText().Contains(JsonSerializer.Serialize(paths[0]).Trim('"'), StringComparison.Ordinal)), "native path never in RPC");
            var grants = new GrantedMediaFiles(f.Store, "music.files.v1", [".wav"]);
            Equal(2, (await grants.ListAsync()).Count); Equal(new Uri(paths[0]), await grants.ResolveAsync(song.Id));
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
            Render(f.Module.PlayerView, 860, 92);
            var button = FindButton(f.Module.PlayerView, f.Module.ModeText);
            f.Calls.FailNext("music.queue.mode", "STORAGE_UNAVAILABLE");
            Click(button); await Until(() => !f.Module.Busy);
            Equal(1, f.Module.Mode); Equal("列表循环", AutomationProperties.GetName(button));
            Equal(1, f.Calls.Count("music.queue.mode"));
            Click(button); await Until(() => !f.Module.Busy);
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
            var unrelatedThumb = new Thumb
            {
                Width = 8, Height = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            var root = new Grid();
            root.Children.Add(view);
            root.Children.Add(unrelatedThumb);
            var window = new Window
            {
                Content = root, Width = 430, Height = 520, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var list = (ListBox)view.FindName("QueueList");
                var scroll = Visuals(list).OfType<ScrollViewer>().Single();
                int Reads(int offset) => f.Calls.Log.Count(call => call.Method == "music.queue.get" &&
                    call.Parameters.GetProperty("offset").GetInt32() == offset);

                scroll.ScrollToEnd(); await Drain();
                Equal(0, Reads(100)); // Layout and programmatic scrolling are not request entrances.
                scroll.ScrollToTop(); await Drain();
                var captured = Mouse.Captured;
                unrelatedThumb.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                    { RoutedEvent = Mouse.GotMouseCaptureEvent });
                scroll.ScrollToEnd(); await Drain();
                Equal(0, Reads(100)); // An unrelated capture notification does not grant scroll intent.
                True(ReferenceEquals(captured, Mouse.Captured), "the routed test leaves OS capture unchanged");
                f.Calls.FailNext("music.queue.get", "STORAGE_UNAVAILABLE");
                RaiseKey(list, Key.End);
                await Until(() => f.Module.QueueSurfaceStatus == "操作失败，请重试");
                Equal(1, Reads(100));
                True(f.Module.CanLoadMoreQueueSurface, "failed page leaves one explicit retry available");

                RaiseKey(list, Key.End);
                await Until(() => f.Module.QueueSurfaceRows.Count == 200);
                Equal(2, Reads(100));
                window.UpdateLayout(); scroll.ScrollToTop(); await Drain();
                var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                bar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, scroll.ScrollableHeight)
                    { RoutedEvent = ScrollBar.ScrollEvent });
                try
                {
                    scroll.ScrollToEnd();
                    await Until(() => f.Module.QueueSurfaceRows.Count == 210);
                }
                finally
                {
                    bar.RaiseEvent(new ScrollEventArgs(ScrollEventType.EndScroll, scroll.ScrollableHeight)
                        { RoutedEvent = ScrollBar.ScrollEvent });
                }
                Equal(1, Reads(200));
                window.UpdateLayout(); scroll.ScrollToEnd(); await Drain();
                list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                await Drain();
                Equal(1, Reads(200));
                True(!f.Module.CanLoadMoreQueueSurface, "final page terminates queue pagination");
            }
            finally
            {
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
                window.Content = null;
                window.Close();
            }
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
            Render(f.Module.View, 640, 420);
            await Drain(); // Let the successful navigation's initial restore complete first.
            var scroll = Visuals((DataGrid)f.Module.View.FindName("SongsGrid")).OfType<ScrollViewer>().First();
            scroll.ScrollToVerticalOffset(10); await Drain(); var offset = scroll.VerticalOffset; True(offset > 0, "real grid scroll");
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
        yield return Case("row actions, transport, seek, volume and global queue confirmations", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            var grid = (DataGrid)f.Module.View.FindName("SongsGrid");
            Render(f.Module.View, 640, 480);
            Render(f.Module.PlayerView, 860, 92);
            grid.ContextMenu.PlacementTarget = grid; grid.ContextMenu.GetBindingExpression(FrameworkElement.DataContextProperty)!.UpdateTarget();
            Equal(f.Module, grid.ContextMenu.DataContext); True(!grid.CanUserSortColumns, "presentation columns retain core order");
            True(grid.ContextMenu.Items.OfType<MenuItem>().Take(3).Select(item => item.Header).SequenceEqual(
                new object[] { "播放", "下一首播放", "收藏" }), "song menu exposes the primary row actions");
            f.Module.SelectedSong = f.Module.Songs[1];
            ((MenuItem)grid.ContextMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Until(() => !f.Module.Busy);
            Equal(1, f.Module.Playlists.Single(p => p.Id == "favorites").Count);
            ((MenuItem)grid.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.append"));
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            var rowButtons = Visuals(row).OfType<Button>().ToDictionary(AutomationProperties.GetName);
            Equal(Visibility.Visible, rowButtons["收藏"].Visibility);
            Equal(Visibility.Collapsed, rowButtons["下一首播放"].Visibility);
            Equal(Visibility.Collapsed, rowButtons["更多"].Visibility);
            SimulateFocus(row, UIElement.IsKeyboardFocusWithinProperty, true); row.UpdateLayout();
            Equal(Visibility.Visible, rowButtons["下一首播放"].Visibility);
            Equal(Visibility.Visible, rowButtons["更多"].Visibility);
            Click(rowButtons["下一首播放"]); await Until(() => !f.Module.Busy);
            True(ReferenceEquals(f.Module.Songs[0], f.Module.SelectedSong), "inline action selects its own song");
            Equal(2, f.Calls.Count("music.queue.append"));
            Click(rowButtons["更多"]);
            True(grid.ContextMenu.IsOpen && ReferenceEquals(rowButtons["更多"], grid.ContextMenu.PlacementTarget),
                "more action opens the shared row context menu");
            grid.ContextMenu.IsOpen = false;
            f.Module.SelectedSong = f.Module.Songs[0];
            var enter = RaiseKey(grid, Key.Enter); True(enter.Handled, "Enter plays selected row"); await Until(() => !f.Module.Busy);
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
        yield return Case("playlist navigation rejects an oversized local total without request expansion", async () =>
        {
            await using var f = await Fixture.Create();
            var before = f.Module.Playlists.ToArray();
            var start = f.Calls.Count("music.library.list");
            f.Calls.TransformNext("music.library.list", reply =>
            {
                var changed = JsonNode.Parse(reply.GetRawText())!.AsObject();
                changed["total"] = 10_001;
                return JsonSerializer.SerializeToElement(changed);
            });
            await f.Module.RefreshAsync();
            Equal(start + 1, f.Calls.Count("music.library.list"));
            True(before.SequenceEqual(f.Module.Playlists), "invalid total must preserve the current navigation");
            Equal("操作失败，请重试", f.Module.Status);
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
            var root = NewRoot(); await using var store = new LocalDocumentStore(root, ["music.files.v1"]);
            await using var native = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
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
            await f.Module.ChangeModeAsync(0); await f.Module.RefreshAsync();
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
            True((await f.Store.ReadAsync("music.library.v1")).Fingerprint.Exists, "product still owns the store");
        });
        yield return Case("failed prepare preserves runtime/store and final disposal observes its cached failure", async () =>
        {
            var f = await Fixture.Create();
            f.Calls.FailNext("application.prepare", "STORAGE_UNAVAILABLE");
            try
            {
                await f.Module.ChangeModeAsync(2);
                var token = f.Calls.Log.Last(call => call.Method == "music.queue.mode").Token;
                var before = (await f.Store.ReadAsync("music.library.v1")).Fingerprint;
                var preparation = f.Module.PrepareShutdownAsync();
                await Failure(() => preparation, "STORAGE_UNAVAILABLE");
                await Failure(() => f.Module.PrepareShutdownAsync(), "STORAGE_UNAVAILABLE");
                True(ReferenceEquals(preparation, f.Module.PrepareShutdownAsync()), "failed preparation is not silently retried");
                True(!token.IsCancellationRequested && !token.WaitHandle.WaitOne(0), "failed prepare retains a live lifetime");
                Equal(0, f.Playback.Subscribers); Equal(0, f.Calls.DisposeCalls);
                True(!f.Module.CanEdit, "failure does not reopen business admission");
                var count = f.Calls.Log.Count;
                await f.Module.ChangeModeAsync(0);
                await f.Module.InitializeAsync(default); await f.Module.NavigateAsync("favorites", default); await f.Module.SearchAsync("ignored", default);
                await f.Module.OpenPlayQueueAsync(); f.Playback.Start(); f.Playback.End();
                Equal(count, f.Calls.Log.Count);
                Equal(before, (await f.Store.ReadAsync("music.library.v1")).Fingerprint);
                Equal(2, await f.SavedMode());
                // The host may now offer explicit force-exit. Final cleanup must
                // observe the same failed task and still release module resources.
                await Failure(() => f.Module.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE");
                await Failure(() => f.Module.DisposeAsync().AsTask(), "STORAGE_UNAVAILABLE");
                True(token.IsCancellationRequested, "failed finalization still cleans up lifetime");
                Equal(1, f.Calls.Count("application.prepare")); Equal(1, f.Calls.DisposeCalls); Equal(0, f.Runtime.DisposeCalls);
                Equal(before, (await f.Store.ReadAsync("music.library.v1")).Fingerprint);
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
        yield return Case("offscreen shell 860/1260, public theme and simulated high-contrast/focus", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.NavigateAsync("favorites", default);
            Render(f.Module.View, 640, 480);
            Render(f.Module.SettingsView, 640, 480);
            Render(f.Module.PlayerView, 860, 92);
            AssertPlayerBounds(f.Module.PlayerView, 860);
            var closeCount = 0;
            var shell = new ShellWindow([f.Module], async () => { closeCount++; await f.Module.DisposeAsync(); });
            var closed = new TaskCompletionSource(); shell.Closed += (_, _) => closed.TrySetResult();
            try
            {
                var before = f.Calls.Log.Count; shell.PrepareForRender(); shell.PrepareForRender(); await Drain();
                Equal(before, f.Calls.Log.Count);
                var root = (FrameworkElement)shell.Content;
                shell.Content = null; root.Resources.MergedDictionaries.Add(shell.Resources); root.DataContext = shell.DataContext;
                TextElement.SetForeground(root, shell.Foreground); TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
                foreach (var size in new[] { new Size(860, 600), new Size(1260, 790) })
                {
                    Render(root, (int)size.Width, (int)size.Height);
                    AssertPlayerBounds(f.Module.PlayerView, f.Module.PlayerView.ActualWidth);
                    True(f.Module.View.ActualWidth > 500, "minimum main content usable");
                }
                var textbox = (TextBox)shell.FindName("SearchBox");
                True(ReferenceEquals(textbox.Style.BasedOn, textbox.FindResource("MeowFilm.TextBox")),
                    "the one App search box consumes the shared TextBox style");
                var mainHost = (ContentControl)LogicalTreeHelper.GetParent(f.Module.View);
                var surface = (Grid)mainHost.Parent;
                var settingsHost = surface.Children.OfType<ContentControl>().Single(host => !ReferenceEquals(host, mainHost));
                // Only switch retained presentation hosts for a render. Clicking
                // shell settings on an uninitialized session would start business work.
                mainHost.Content = null; settingsHost.Content = f.Module.SettingsView;
                mainHost.Visibility = Visibility.Collapsed; settingsHost.Visibility = Visibility.Visible;
                foreach (var size in new[] { new Size(860, 600), new Size(1260, 790) })
                {
                    Render(root, (int)size.Width, (int)size.Height);
                    AssertPlayerBounds(f.Module.PlayerView, f.Module.PlayerView.ActualWidth);
                }
                DesktopTheme.SetIsHighContrast(root, true);
                var settingsNavigation = (Panel)f.Module.SettingsView.FindName("SettingsNavigation");
                settingsNavigation.Children.OfType<RadioButton>().ElementAt(2).IsChecked = true;
                await Drain();
                Render(root, 860, 600);
                var picker = Visuals(f.Module.SettingsView).OfType<ComboBox>()
                    .Single(c => AutomationProperties.GetName(c) == "起播目标音质");
                SimulateFocus(picker, UIElement.IsKeyboardFocusWithinProperty, true);
                Render(root, 860, 600);
                Equal(SystemColors.HighlightColor, ((SolidColorBrush)picker.BorderBrush).Color);
                SimulateFocus(picker, UIElement.IsKeyboardFocusWithinProperty, false);
                settingsHost.Content = null; mainHost.Content = f.Module.View;
                settingsHost.Visibility = Visibility.Collapsed; mainHost.Visibility = Visibility.Visible;
                Render(root, 860, 600);
                var mode = FindButton(f.Module.PlayerView, f.Module.ModeText);
                Equal(SystemColors.WindowTextColor, ((SolidColorBrush)mode.Foreground).Color);
                var playerView = (UserControl)f.Module.PlayerView;
                True(playerView.Background is null && ((Grid)playerView.Content).Background is SolidColorBrush { Color.A: 0 },
                    "high-contrast footer shows the window backdrop instead of painting its own");
                SimulateFocus(mode, UIElement.IsKeyboardFocusedProperty, true); await Drain();
                True(mode.FocusVisualStyle is not null, "shared keyboard focus visual supplied");
                var progress = (Slider)f.Module.PlayerView.FindName("Progress");
                SimulateFocus(progress, UIElement.IsKeyboardFocusWithinProperty, true); await Drain();
                True(progress is SeekBar && DesktopTheme.GetIsHighContrast(progress),
                    "native continuous seek rail consumes inherited high-contrast state without an extra rectangular focus border");
                f.Playback.Start(); await Drain(); // Exercise the pause path, not a font glyph.
                Render(f.Module.PlayerView, 860, 92);
                SimulateFocus(progress, UIElement.IsKeyboardFocusWithinProperty, false); SimulateFocus(mode, UIElement.IsKeyboardFocusedProperty, false);
                True(Visuals(root).OfType<Button>().Where(b => b.IsVisible).All(b => b.FocusVisualStyle is not null),
                    "visible shell and page actions retain focus visual");
                Equal(before + 1, f.Calls.Log.Count); // The single explicit Started records history.
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
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); if (!predicate()) await Task.Delay(1);
        }
        await Drain();
    }
    private static async Task Drain() { for (var i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static Button FindButton(FrameworkElement view, string label) => Visuals(view).OfType<Button>()
        .Single(b => AutomationProperties.GetName(b) == label || b.Content is string text && text == label);
    private static void Click(Button button) { True(button.IsEnabled, "clickable action enabled"); button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
    private sealed class KeyboardProbe : KeyboardDevice
    {
        public KeyboardProbe() : base(InputManager.Current) { }
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }
    private sealed class PresentationProbe : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static KeyEventArgs RaiseKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(new KeyboardProbe(), new PresentationProbe(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args); return args;
    }
    private static void SimulateFocus(UIElement element, DependencyProperty property, bool value)
    {
        // Offscreen style-state test, not OS keyboard focus on an invisible HWND.
        var field = typeof(UIElement).GetField(property.Name + "PropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Focus-state test seam unavailable");
        element.SetValue((DependencyPropertyKey)field.GetValue(null)!, value);
    }
    private static void AssertPlayerBounds(FrameworkElement view, double width)
    {
        var panels = new[] { "MetadataPanel", "TransportPanel", "OptionsPanel" }.Select(name => (FrameworkElement)view.FindName(name)).ToArray();
        var bounds = panels.Select(panel => new Rect(panel.TranslatePoint(new Point(), view), panel.RenderSize)).ToArray();
        True(width >= 820, "player spans shell width");
        for (var i = 0; i < bounds.Length; i++)
        {
            True(bounds[i].Left >= 0 && bounds[i].Right <= width + 0.5, "player panel within minimum window");
            if (i > 0) True(bounds[i - 1].Right <= bounds[i].Left + 0.5, "player columns do not overlap");
        }
        foreach (var control in Visuals(view).OfType<Control>().Where(c => c is Button or Slider))
        {
            var rect = new Rect(control.TranslatePoint(new Point(), view), control.RenderSize);
            True(rect.Left >= 0 && rect.Right <= width + 0.5 && rect.Top >= 0 && rect.Bottom <= view.ActualHeight + 0.5, "player control not clipped: " + AutomationProperties.GetName(control) + " " + rect);
        }
    }
    private static void Render(FrameworkElement view, int width, int height)
    {
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        // Standalone settings has a transparent background; assert actual painted
        // controls/text, not an arbitrary percentage of a shell-owned background.
        True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 1000, "nonempty offscreen controls/text");

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
        public void Release() => release.TrySetResult();
        public async Task<JsonElement> Hold(JsonElement reply) { Arrived.TrySetResult(); await release.Task; return reply; }
    }
    private sealed class ObservedApplication(IMusicApplication inner) : MusicApplicationForwarder(inner)
    {
        private sealed record Rule(string Method, Func<JsonElement, bool> Match, Func<JsonElement, Task<JsonElement>> Apply, bool Before);
        private readonly List<Rule> rules = [];
        private readonly List<HeldReply> holds = [];
        public List<Invocation> Log { get; } = [];
        public int DisposeCalls { get; private set; }
        public int Count(string method) => Log.Count(call => call.Method == method);
        public JsonElement Last(string method) => Log.Last(call => call.Method == method).Parameters;
        public HeldReply HoldNext(string method, Func<JsonElement, bool>? match = null)
        {
            var hold = new HeldReply(); holds.Add(hold); rules.Add(new(method, match ?? (_ => true), hold.Hold, false)); return hold;
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
        public double Seek; public int Pauses, Resumes, Stops;
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
        public Task SeekAsync(double seconds) { Seek = seconds; Changed(); return Task.CompletedTask; }
        public void Tick(double seconds)
        { Seek = seconds; PropertyChanged?.Invoke(this, new(nameof(PositionSeconds))); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root;
        public required IDocumentStore Store;
        public required NativeMusicScriptTestRuntime Runtime;
        public required MusicNativeApplication Application;
        public required ObservedApplication Calls;
        public required MusicCapability Module;
        public readonly PlaybackProbe Playback;
        public readonly Dialogs Dialogs;
        private Fixture(PlaybackProbe playback, Dialogs dialogs) { Playback = playback; Dialogs = dialogs; }
        public static async Task<Fixture> Create(string? root = null, bool persistent = false, NativePlayback? native = null,
            IMusicPlayback? output = null, bool withPlaybackCache = false)
        {
            root ??= NewRoot();
            IDocumentStore store = persistent
                ? new LocalDocumentStore(root, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces) : new DocumentProbe();
            var runtime = new NativeMusicScriptTestRuntime(); MusicCapability? module = null;
            var application = new MusicNativeApplication(store, runtime, new NoUpstream());
            try
            {
                var calls = new ObservedApplication(application); var playback = new PlaybackProbe(); var dialogs = new Dialogs();
                module = native is null
                    ? new MusicCapability(calls, store, output ?? playback, withPlaybackCache ?
                        new MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache(Path.Combine(root, "playback-cache"), store) : null, dialogs)
                    : new MusicCapability(calls, store, native, dialogs);
                await module.InitializeAsync(default);
                Equal("wy", module.CatalogSourcePlatform);
                True(module.CanEditCatalogSource && !module.HasCatalogSourceError, "shared UI fixture restores the native catalog source");
                Equal(1, calls.Count("music.catalogSource.settings.get"));
                Equal(0, calls.Count("music.catalogSource.settings.save"));
                await module.NavigateAsync("favorites", default);
                True(!runtime.HasStarted, "native initialization never starts Node");
                return new(playback, dialogs) { Root = root, Store = store, Runtime = runtime, Application = application, Calls = calls, Module = module };
            }
            catch
            {
                try { if (module is not null) await module.DisposeAsync(); else await application.DisposeAsync(); }
                finally { await runtime.DisposeAsync(); await store.DisposeAsync(); }
                throw;
            }
        }
        public async Task<JsonElement> Rpc(string method, object? parameters = null)
        {
            var result = await Application.ExecuteAsync(method, parameters ?? new { });
            True(result.GetProperty("ok").GetBoolean(), "fixture native command failed: " + method + ": " + result.GetRawText()); return result;
        }
        public async Task<int> SavedMode()
        {
            var saved = await Store.ReadAsync("music.library.v1");
            using var document = JsonDocument.Parse(saved.Data!);
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                using var key = JsonDocument.Parse(entry.GetProperty("key").GetString()!);
                if (key.RootElement.EnumerateArray().Last().GetString() == "playback-settings")
                    return entry.GetProperty("value").GetProperty("mode").GetInt32();
            }
            return 1;
        }
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
            var grants = new GrantedMediaFiles(Store, "music.files.v1", [".wav"]);
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
            finally { try { await Runtime.DisposeAsync(); } finally { await Store.DisposeAsync(); } }
        }
    }
    private sealed class NoUpstream : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No upstream request allowed in local UI fixture");
    }
    private sealed class DocumentProbe : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> values = [];
        private readonly object gate = new();
        private bool closed;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                Check(documentNamespace, cancellationToken);
                var bytes = values.GetValueOrDefault(documentNamespace)?.ToArray();
                return Task.FromResult(new DocumentSnapshot(bytes, bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
            }
        }
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                Check(documentNamespace, cancellationToken);
                var old = values.GetValueOrDefault(documentNamespace);
                var actual = old is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(old);
                if (actual != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
                values[documentNamespace] = data.ToArray();
                return Task.FromResult(DocumentFingerprint.FromBytes(data.Span));
            }
        }
        private void Check(string ns, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
            bool content = AppIdentity.NativeCollections.Any(collection =>
            {
                if (!ns.StartsWith(collection + ".", StringComparison.Ordinal)) return false;
                string hash = ns[(collection.Length + 1)..];
                return hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
            });
            if (!AppIdentity.NativeNamespaces.Contains(ns, StringComparer.Ordinal) && !content)
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        }
        public ValueTask DisposeAsync() { lock (gate) { closed = true; values.Clear(); } return ValueTask.CompletedTask; }
    }
    private static async Task OnSta(Func<Task> body)
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Dispatcher? owner = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Volatile.Write(ref owner, dispatcher);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var errors = new List<Exception>(); dispatcher.UnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
            dispatcher.BeginInvoke(async () =>
            {
                Exception? failure = null;
                try { await body(); await Drain(); if (errors.Count > 0) throw new AggregateException("Unhandled UI event", errors); }
                catch (Exception error) { failure = error; }
                finally { completed.TrySetResult(failure); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "music-ui-code-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? result;
        try { result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { Volatile.Read(ref owner)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (result is not null) ExceptionDispatchInfo.Capture(result).Throw();
    }
}
