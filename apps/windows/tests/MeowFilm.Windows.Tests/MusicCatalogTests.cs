using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Platform.Storage;
using MeowFilm.Platform.Files;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using System.Windows.Media.Imaging;

namespace MeowFilm.Windows.Tests;

// Real WPF + Native application/catalog/platform protocols + DPAPI. Synthetic
// HttpMessageHandler replaces upstream; only explicit authored LX needs Node.
public static partial class MusicCatalogTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in NextRecoveryCases()) yield return test;
        foreach (var test in NetworkReferenceCases()) yield return test;
        yield return Case("playlist card next stages the complete list and never starts playback", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
            await f.Configure(new { total = 1205, nextDetail = 0 });
            await f.Module.QueueCatalogCardAsync(card);
            var queue = await f.Rpc("music.queue.get", new { limit = 1 });
            Equal(1205, queue.GetProperty("total").GetInt32());
            Equal(0, f.Playback.Opens.Count);
            var commit = f.Client.Log.Single(call => call.Method == "music.catalog.playlist.commit").Parameters;
            Equal("next", commit.GetProperty("action").GetString());
            True(commit.EnumerateObject().Select(p => p.Name).OrderBy(n => n)
                .SequenceEqual(new[] { "action", "expectedVersion", "token" }), "next sends only stage token and CAS version");
            Equal(1, f.Client.Count("music.catalog.playlist.discard"));
            Equal(0, f.Client.Count("music.queue.append"));
        });
        yield return Case("playlist card next preserves current occurrence mode and complete insertion order", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
            await f.Rpc("music.queue.replace", new { songs = Enumerable.Range(1, 3)
                .Select(i => MusicSongFixture.Online("wy", i.ToString())).ToArray(), index = 0 });
            var before = await f.Rpc("music.queue.get", new { limit = 3 });
            await f.Configure(new { nextDetail = 0 });
            await f.Module.QueueCatalogCardAsync(card);
            var after = await f.Rpc("music.queue.get", new { limit = 100 });
            Equal(before.GetProperty("currentId").GetString(), after.GetProperty("currentId").GetString());
            Equal(before.GetProperty("mode").GetRawText(), after.GetProperty("mode").GetRawText());
            Equal(before.GetProperty("version").GetInt64() + 1, after.GetProperty("version").GetInt64());
            var ids = new List<string>();
            for (int offset = 0; offset < 208; offset += 100)
            {
                var page = await f.Rpc("music.queue.get", new { offset, limit = 100 });
                ids.AddRange(page.GetProperty("entries").EnumerateArray()
                    .Select(item => MusicSongFixture.NativeId(item.GetProperty("song"))));
            }
            True(ids.SequenceEqual(new[] { "1" }.Concat(Enumerable.Range(1001, 205).Select(i => i.ToString()))
                .Concat(new[] { "2", "3" })), "entire playlist is one ordered segment after the unchanged current occurrence");
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("playlist card favorite saves one remote reference without loading members", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
            int playlistReads = f.Client.Count("music.catalog.playlist");
            await f.Module.FavoriteCatalogCardAsync(card);
            await f.Module.FavoriteCatalogCardAsync(card);
            Equal(0, f.Module.Playlists.Count(p => p.Kind == "playlist"));
            var reference = f.Module.NetworkPlaylists.Single();
            Equal(card.Platform, reference.Platform); Equal(card.ActionId, reference.RemoteId);
            Equal(card.Title, reference.Name);
            Equal(playlistReads, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            var detail = await f.Client.ExecuteAsync("music.library.detail", new { id = reference.Id });
            Equal("READ_ONLY", detail.GetProperty("code").GetString());
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("playlist card next retires preparation on navigation pause and queue conflict", async () =>
        {
            foreach (string change in new[] { "navigate", "pause", "queue" })
            {
                await using var f = await Fixture.Create();
                await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
                await f.Configure(new { nextDetail = 0 });
                var hold = f.Client.Hold("music.catalog.playlist.prepare");
                var pending = f.Module.QueueCatalogCardAsync(card);
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (change == "navigate") await f.Module.NavigateAsync("favorites", default);
                else if (change == "pause") await f.Module.TogglePlaybackAsync();
                else await f.Rpc("music.queue.clear", new { });
                hold.Release(); await pending;
                Equal(change == "queue" ? 1 : 0, f.Client.Count("music.catalog.playlist.commit"));
                Equal(1, f.Client.Count("music.catalog.playlist.discard"));
                Equal(0, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
                Equal(0, f.Playback.Opens.Count);
            }
        });
        yield return Case("playlist template menu targets current card and rejects recycled or retired targets", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var cards = f.Module.CatalogSections.SelectMany(s => s.Items).Where(c => c.Kind == "playlist").ToArray();
            var first = cards[0]; var second = cards.Last();
            var view = new MusicCatalogView { DataContext = f.Module };
            using var source = new HwndSource(new HwndSourceParameters("catalog-target-menu-fixture")
                { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                    Width = 700, Height = 400, PositionX = -32000, PositionY = -32000 });
            source.RootVisual = view;
            foreach (string key in new[] { "Catalog.PlaylistCard", "Catalog.PlaylistRow" })
            {
                var host = (Button)((DataTemplate)view.FindResource(key)).LoadContent();
                view.Content = host; host.DataContext = first;
                view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400));
                view.UpdateLayout(); await Drain();
                True(host.IsLoaded && host.IsVisible, "playlist menu target is mounted and visible");
                int calls = f.Client.Log.Count;
                OpenContentMenu(host);
                True(host.ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(
                    new object[] { "查看歌单", "播放", "下一首播放", "收藏" }), "four business actions only");
                Equal(calls, f.Client.Log.Count);
                var stale = (MenuItem)host.ContextMenu.Items[3];
                host.DataContext = second;
                stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
                Equal(calls, f.Client.Log.Count);
                OpenContentMenu(host);
                int saves = f.Client.Count("music.catalog.playlist.save");
                ((MenuItem)host.ContextMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => f.Client.Count("music.catalog.playlist.save") > saves && !second.IsPreparing &&
                    f.Module.NetworkPlaylists.Any(p => p.Platform == second.Platform && p.RemoteId == second.ActionId));
                var saved = f.Client.Last("music.catalog.playlist.save");
                Equal(second.ActionId, saved.GetProperty("id").GetString());
                OpenContentMenu(host);
                Equal("取消收藏", ((MenuItem)host.ContextMenu.Items[3]).Header);
                int removes = f.Client.Count("music.library.network.remove");
                ((MenuItem)host.ContextMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Until(() => f.Client.Count("music.library.network.remove") == removes + 1 && !f.Module.Busy &&
                    !f.Module.NetworkPlaylists.Any(p => p.Platform == second.Platform && p.RemoteId == second.ActionId));
                OpenContentMenu(host);
                stale = (MenuItem)host.ContextMenu.Items[2];
                await f.Module.NavigateAsync("favorites", default);
                calls = f.Client.Log.Count;
                stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
                Equal(calls, f.Client.Log.Count);
                await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                first = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
                second = f.Module.CatalogSections.SelectMany(s => s.Items).Last(c => c.Kind == "playlist");
            }
            var entry = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.Entry")).LoadContent();
            True(entry.ContextMenu is null, "header entries do not gain content menus");
        });
        yield return Case("catalog song menu captures its row and keeps section playback and discovery actions", async () =>
        {
            await using var f = await Fixture.Create(); await f.SetupSource();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            await f.Module.CatalogEntryInitialization;
            var cards = Enumerable.Range(1001, 3).Select(id => new MusicCatalogCard(id.ToString(), "歌曲" + id,
                "歌手", "", "song", "wy", SongRow.Read(MusicSongFixture.Element(MusicSongFixture.Online("wy", id.ToString()))),
                ActionId: id.ToString())).ToArray();
            f.Module.CatalogSections.Add(new MusicCatalogSection("菜单歌曲栏目", cards, Id: "menu-song-section", Type: "songs"));
            foreach (var item in cards) f.Module.Songs.Add(item.Song!);
            var card = cards[1]; f.Module.SelectedSong = cards[0].Song;
            var view = new MusicCatalogView { DataContext = f.Module };
            var row = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.SongRow")).LoadContent();
            view.Content = row; row.DataContext = card;
            using var source = new HwndSource(new HwndSourceParameters("catalog-song-menu-fixture")
                { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                    Width = 700, Height = 400, PositionX = -32000, PositionY = -32000 });
            source.RootVisual = view;
            view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400));
            view.UpdateLayout(); await Drain();
            True(row.IsLoaded && row.IsVisible, "song menu target is mounted and visible");
            int calls = f.Client.Log.Count;
            OpenContentMenu(row);
            Equal(calls, f.Client.Log.Count);
            True(row.ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(new object[]
                { "播放", "下一首播放", "收藏", "添加到歌单", "相似歌曲", "专辑" }),
                "catalog rows share all search actions and preserve their discovery extensions");
            True(!row.ContextMenu.Items.OfType<MenuItem>().Any(i => i.Header as string == "心动推荐"),
                "an ordinary home song row is not an eligible heartbeat context");
            var similar = row.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header as string == "相似歌曲");
            True(similar.IsEnabled, "an identified home song keeps its valid recommendation action");
            True(!row.ContextMenu.Items.OfType<MenuItem>().Any(i => i.Header as string == "移除"),
                "catalog content does not expose collection member removal");
            row.DataContext = cards[0]; await Drain();
            calls = f.Client.Log.Count;
            similar.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
            Equal(calls, f.Client.Log.Count);
            Equal(0, f.Playback.Opens.Count);
            row.DataContext = card; await Drain(); OpenContentMenu(row);
            ((MenuItem)row.ContextMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => !f.Module.Busy && f.Client.Count("music.library.addSongs") == 1, "song.favorite");
            var favorite = await f.Rpc("music.library.detail", new { id = "favorites" });
            Equal(card.Song!.Id, SongRow.Read(favorite.GetProperty("songs")[0]).Id);
            OpenContentMenu(row);
            ((MenuItem)row.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => f.Playback.Opens.Count == 1 && !f.Module.Busy && !card.IsPreparing, "song.play");
            var replace = f.Client.Log.Last(c => c.Method == "music.queue.replace").Parameters;
            Equal(cards.Length, replace.GetProperty("songs").GetArrayLength());
            Equal(1, replace.GetProperty("index").GetInt32());
            OpenContentMenu(row);
            similar = row.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header as string == "相似歌曲");
            True(similar.IsEnabled, "the valid recommendation remains available before leaving its source");
            await f.Module.NavigateAsync("favorites", default);
            calls = f.Client.Log.Count;
            similar.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
            Equal(calls, f.Client.Log.Count);
            Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("channel ordinary playlist cards expose four actions without adding tab menus", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channel", reply => Edit(reply, node =>
                node["sections"] = JsonSerializer.SerializeToNode(new[] {
                    new { id = "menu-playlists", title = "频道歌单", type = "playlists", items = new[] {
                        new { id = "500", name = "频道歌单A", kind = "playlist", action = new { type = "playlist", id = "500" } },
                        new { id = "501", name = "频道歌单B", kind = "playlist", action = new { type = "playlist", id = "501" } }
                    } } })));
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            using var source = new HwndSource(new HwndSourceParameters("channel-menu-fixture")
                { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                    Width = 1100, Height = 760, PositionX = -32000, PositionY = -32000 });
            source.RootVisual = f.Module.View;
            f.Module.View.Measure(new(1100, 760)); f.Module.View.Arrange(new Rect(0, 0, 1100, 760));
            f.Module.View.UpdateLayout(); await Drain();
            var second = f.Module.ChannelSections.Single().Items[1];
            var host = Descendants<Button>(f.Module.View).Single(button => ReferenceEquals(button.Tag, second) &&
                button.IsVisible && button.IsEnabled && button.IsHitTestVisible && button.ContextMenu is not null);
            int reads = f.Client.Log.Count;
            OpenContentMenu(host);
            Equal(reads, f.Client.Log.Count);
            True(host.ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(
                new object[] { "查看歌单", "播放", "下一首播放", "收藏" }), "ordinary channel playlist has four content actions");
            var routes = new List<string>(); f.Module.NavigationRequested += (_, e) => routes.Add(e.Route);
            ((MenuItem)host.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => routes.Count == 1); Equal("catalog-playlist/wy/501", routes.Single());
            True(Descendants<Button>(f.Module.View).Where(button => button.Tag is MusicCatalogCard card &&
                f.Module.ChannelTabs.Any(tab => ReferenceEquals(tab, card))).All(button => button.ContextMenu is null),
                "channel navigation tabs retain their existing actions");
        });
        yield return Case("opened catalog more and playlist menus apply shared templates including destination children", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            await f.Module.CatalogEntryInitialization;
            var song = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Song is not null);
            var playlist = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
            var view = new MusicCatalogView { DataContext = f.Module };
            using var source = new HwndSource(new HwndSourceParameters("catalog-open-menu-fixture")
                { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                    Width = 700, Height = 400, PositionX = -32000, PositionY = -32000 });
            source.RootVisual = view;
            foreach (bool songMenu in new[] { true, false })
            {
                var card = (FrameworkElement)((DataTemplate)view.FindResource(songMenu ? "Catalog.SongRow" : "Catalog.PlaylistCard")).LoadContent();
                card.DataContext = songMenu ? song : playlist; view.Content = card;
                view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400)); view.UpdateLayout(); await Drain();
                ContextMenu menu; FrameworkElement target;
                if (songMenu)
                {
                    // Synthetic WPF focus updates the CLR cache and DP before notifying CardMotion; no OS input.
                    var focusKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsKeyboardFocusWithinPropertyKey",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
                    var instanceFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var writeFlag = typeof(UIElement).GetMethod("WriteFlag", instanceFlags)!;
                    var focusFlag = Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsKeyboardFocusWithinCache");
                    True(!card.IsKeyboardFocusWithin && !(bool)card.GetValue(UIElement.IsKeyboardFocusWithinProperty),
                        "offscreen song row starts without keyboard focus");
                    int focusChanges = 0;
                    card.IsKeyboardFocusWithinChanged += (_, _) => focusChanges++;
                    writeFlag.Invoke(card, [focusFlag, true]);
                    card.SetValue(focusKey, true);
                    True(card.IsKeyboardFocusWithin && (bool)card.GetValue(UIElement.IsKeyboardFocusWithinProperty),
                        "synthetic focus synchronizes the WPF CLR cache and dependency property");
                    typeof(UIElement).GetMethod("RaiseIsKeyboardFocusWithinChanged", instanceFlags)!.Invoke(card,
                        [new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusWithinProperty, false, true)]);
                    Equal(1, focusChanges);
                    var more = Descendants<Button>(card).Single(b => AutomationProperties.GetName(b) == "歌曲操作");
                    await Until(() => more.IsVisible, "templates.more-visible", () =>
                        $"loaded={card.IsLoaded}; visible={card.IsVisible}; enabled={card.IsEnabled}; " +
                        $"progress={CardMotion.GetProgress(card)}; " +
                        $"moreVisible={more.IsVisible}; moreVisibility={more.Visibility}; " +
                        $"parentVisibility={((FrameworkElement)VisualTreeHelper.GetParent(more)).Visibility}");
                    more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); menu = more.ContextMenu; target = more;
                }
                else
                {
                    OpenContentMenu(card); menu = card.ContextMenu; target = card; menu.IsOpen = true;
                }
                try
                {
                    await Until(() => menu.IsOpen, "templates.open", () =>
                        $"songMenu={songMenu}; isOpen={menu.IsOpen}; targetVisible={target.IsVisible}; " +
                        $"placementTarget={menu.PlacementTarget?.GetType().Name ?? "<null>"}; " +
                        $"placementMatches={ReferenceEquals(menu.PlacementTarget, target)}");
                    await Drain(); menu.ApplyTemplate(); menu.UpdateLayout();
                    True(ReferenceEquals(menu.Style, target.FindResource("MeowFilm.ContextMenu")), "opened menu resolves the host shared style");
                    var expected = new ContextMenu { Style = (Style)target.FindResource("MeowFilm.ContextMenu") };
                    True(menu.Template is not null && ReferenceEquals(menu.Template, expected.Template), "opened menu uses the actual shared control template");
                    True(Descendants<Border>(menu).Any(border => ReferenceEquals(border.Background,
                        target.FindResource("MeowFilm.Brush.FloatingSurface"))), "opened panel resolves the host floating brush");
                    foreach (var item in menu.Items.OfType<MenuItem>())
                    {
                        item.ApplyTemplate();
                        var styled = new MenuItem { Style = (Style)target.FindResource("MeowFilm.MenuItem") };
                        True(ReferenceEquals(item.Style, styled.Style) && item.Template is not null && ReferenceEquals(item.Template, styled.Template),
                            "each actual menu item uses the shared style and template");
                        if (item.Items.Count == 0) continue;
                        item.IsSubmenuOpen = true; await Drain();
                        foreach (var child in item.Items.OfType<MenuItem>())
                        {
                            child.ApplyTemplate();
                            True(ReferenceEquals(child.Style, styled.Style) && ReferenceEquals(child.Template, styled.Template),
                                "opened dynamic playlist children use the shared style and template");
                        }
                        item.IsSubmenuOpen = false;
                    }
                    if (songMenu) True(menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "添加到歌单").Items.Count > 0,
                        "dynamic playlist style coverage is not vacuous");
                }
                finally { menu.IsOpen = false; }
            }
        });
        yield return Case("cancel closing leaves mounted hidden settings and discovery inactive", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.FailHistory = true; // Keep activation reads observable instead of satisfying them from history cache.
            var host = new Grid(); host.Children.Add(f.Module.View); host.Children.Add(f.Module.SettingsView);
            var window = new Window { Content = host, Width = 906, Height = 650,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); await f.Module.ActivateSettingsAsync();
                await f.Module.NavigateAsync("catalog-discovery", default); await Drain();
                f.Module.View.Visibility = f.Module.SettingsView.Visibility = Visibility.Collapsed;
                await Task.WhenAll(f.Module.DeactivateSettingsAsync(), f.Module.DeactivateDiscoveryViewAsync());
                True(f.Module.View.IsLoaded && f.Module.SettingsView.IsLoaded, "hidden surfaces remain mounted");
                await f.Module.PrepareShutdownAsync();
                int reads = f.Client.Count("music.search.history.get");
                await f.Module.CancelShutdownPreparationAsync(); await Drain();
                Equal(reads, f.Client.Count("music.search.history.get"));
                f.Module.SettingsView.Visibility = Visibility.Visible;
                await f.Module.ActivateSettingsAsync();
                Equal(reads + 1, f.Client.Count("music.search.history.get"));
                f.Module.View.Visibility = Visibility.Visible;
                await f.Module.ActivateDiscoveryViewAsync();
                True(f.Client.Count("music.search.history.get") > reads + 1, "visible discovery resumes normally");
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("retained home keeps scroller containers and decoded artwork across channels", async () =>
        {
            await using var f = await Fixture.Create();
            var loader = new CatalogArtworkLoader();
            ArtworkScope.SetLoader(f.Module.View, loader);
            var window = new Window { Content = f.Module.View, Width = 906, Height = 650,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; window.UpdateLayout();
                await Until(() => Descendants<Image>(f.Module.View).Any(image => image.Source is not null), "home pixels load");
                Equal(0, f.Client.Count("music.catalog.channels"));
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var scroll = (ScrollViewer)catalog.FindName("HomeScroll");
                scroll.ScrollToVerticalOffset(Math.Min(100, scroll.ScrollableHeight)); await Drain();
                double offset = scroll.VerticalOffset;
                var controls = Descendants<ArtworkImage>(scroll).ToArray();
                var images = Descendants<Image>(scroll).Where(image => image.Source is not null)
                    .Select(image => (Control: image, Pixels: image.Source)).ToArray();
                True(images.Length > 0, "real decoded images establish the retention assertion");
                string homeLocation = f.Module.CaptureNavigationLocation();
                await f.Module.NavigateAsync("catalog-channels", default); await Drain(); window.UpdateLayout();
                True(controls.All(control => !control.IsVisible), "home remains mounted but inactive");
                True(images.All(image => ReferenceEquals(image.Pixels, image.Control.Source)), "hiding preserves decoded pixels");
                int reads = loader.Addresses.Count;
                await f.Module.RestoreNavigationAsync(homeLocation, default); await Drain(); window.UpdateLayout();
                Equal(2, f.Client.Count("music.catalog.home"));
                Equal(reads, loader.Addresses.Count);
                True(controls.SequenceEqual(Descendants<ArtworkImage>(scroll)), "home keeps its artwork containers");
                True(images.All(image => ReferenceEquals(image.Pixels, image.Control.Source)), "restoring preserves decoded pixels");
                True(Math.Abs(offset - scroll.VerticalOffset) < 1, "home retains the actual scroll offset");
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("retained overview templates and pixels do not follow the active catalog route", async () =>
        {
            await using var f = await Fixture.Create();
            var loader = new CatalogArtworkLoader(); ArtworkScope.SetLoader(f.Module.View, loader);
            await f.Module.NavigateAsync("catalog-home", default);
            await f.SearchPolicy("wy", playlists: true);
            await f.Module.SearchAsync("固定宿主", default); await WaitForSearch(f.Module);
            var window = new Window { Content = f.Module.View, Width = 906, Height = 900,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var overview = (ScrollViewer)catalog.FindName("SearchOverviewScroll");
                var shelves = Descendants<Shelf>(overview).ToArray();
                True(shelves.Length > 0, "overview contains a real playlist shelf");
                await Until(() => Descendants<Image>(overview).Any(image => image.Source is not null),
                    "search playlist artwork did not load into the visible overview");
                var templates = shelves.Select(shelf => shelf.CardTemplate).ToArray();
                var controls = Descendants<ArtworkImage>(overview).ToArray();
                var images = Descendants<Image>(overview).Where(image => image.Source is not null)
                    .Select(image => (Control: image, Pixels: image.Source)).ToArray();
                True(images.Length > 0, "overview owns acquired image pixels");
                var palettes = controls.Select(image => image.GetValue(ArtworkImage.RawPaletteBrushProperty)).ToArray();
                string position = f.Module.CaptureNavigationLocation();
                foreach (string route in new[] { "catalog-home", "catalog-channels" })
                {
                    await f.Module.NavigateAsync(route, default); await Drain(); window.UpdateLayout();
                    AssertRetained();
                    await f.Module.RestoreNavigationAsync(position, default); await Drain(); window.UpdateLayout();
                    AssertRetained();
                }
                void AssertRetained()
                {
                    True(shelves.SequenceEqual(Descendants<Shelf>(overview)), "overview keeps its shelf controls");
                    True(templates.SequenceEqual(shelves.Select(shelf => shelf.CardTemplate)), "overview template belongs to its host");
                    True(controls.SequenceEqual(Descendants<ArtworkImage>(overview)), "overview retains artwork controls");
                    True(images.All(image => ReferenceEquals(image.Pixels, image.Control.Source)), "overview retains decoded pixels");
                    True(palettes.SequenceEqual(controls.Select(image => image.GetValue(ArtworkImage.RawPaletteBrushProperty))), "overview retains palettes");
                }
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("initial home skeleton ends on completion and does not replace refresh content", async () =>
        {
            await using var f = await Fixture.Create();
            var loader = new CatalogArtworkLoader(); ArtworkScope.SetLoader(f.Module.View, loader);
            var window = new Window { Content = f.Module.View, Width = 906, Height = 650,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            var hold = f.Client.Hold("music.catalog.home");
            try
            {
                window.Show();
                var loading = f.Module.NavigateAsync("catalog-home", default);
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); window.UpdateLayout(); await Drain();
                True(f.Module.ShowCatalogSkeleton, "initial empty home has a loading skeleton");
                True(Descendants<LoadingSkeleton>(f.Module.View).Any(skeleton => skeleton.IsVisible), "skeleton is in the visible tree");
                Equal(0, loader.Addresses.Count);
                hold.Release(); await loading; await Drain();
                True(!f.Module.ShowCatalogSkeleton, "real content replaces the skeleton");
                var refreshHold = f.Client.Hold("music.catalog.home");
                var refresh = f.Module.RefreshCatalogAsync();
                await refreshHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!f.Module.ShowCatalogSkeleton && f.Module.HomeSections.Count > 0, "refresh retains real content");
                refreshHold.Release(); await refresh;
            }
            finally { hold.Release(); f.Client.ReleaseAll(); window.Content = null; window.Close(); }
        });
        yield return Case("channel owner selects ordered first openable item and preserves page on return", async () =>
        {
            foreach (string first in new[] { "artist", "rock" })
            {
                await using var f = await Fixture.Create();
                Equal(0, f.Client.Count("music.catalog.channels"));
                Equal(0, f.Client.Count("music.catalog.channel"));
                f.Client.Transform("music.catalog.channels", value =>
                {
                    var json = JsonNode.Parse(value.GetRawText())!.AsObject();
                    var items = json["items"]!.AsArray();
                    // Raw first is in More; the direct visible group precedes More.
                    var wanted = first == "artist" ? new[] { "rock", "artist" } : new[] { "rock" };
                    var selected = wanted.Select(id => items.Single(item => item!["id"]!.GetValue<string>() == id)!.DeepClone()).ToArray();
                    items.Clear(); foreach (var item in selected) items.Add(item);
                    return JsonSerializer.SerializeToElement(json);
                });
                await f.Module.NavigateAsync("catalog-channels", default);
                Equal("wy/" + first, f.Module.CurrentChannelKey);
                Equal(first, f.Module.VisibleChannelTabs.Concat(f.Module.MoreChannelTabs).First(card => card.CanOpen).ActionId);
                var sections = f.Module.CatalogSections;
                var existing = sections.ToArray();
                int changes = 0;
                sections.CollectionChanged += (_, _) => changes++;
                int reads = f.Client.Count("music.catalog.channel");
                string channelLocation = f.Module.CaptureNavigationLocation();
                await f.Module.NavigateAsync("catalog-home", default);
                await f.Module.HomeInitialization;
                var home = f.Module.HomeSections.ToArray();
                await f.Module.RestoreNavigationAsync(channelLocation, default);
                Equal(reads, f.Client.Count("music.catalog.channel"));
                Equal(0, changes);
                True(existing.SequenceEqual(f.Module.CatalogSections, ReferenceEqualityComparer.Instance), "channel sections keep identity");
                await f.Module.NavigateAsync("catalog-home", default);
                True(home.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance), "home sections keep identity");
                Equal(2, f.Client.Count("music.catalog.home"));
                f.Module.RetireCatalogSession();
                True(home.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance),
                    "account retirement keeps the last successful home display");
                Equal(0, sections.Count);
                await f.Module.NavigateAsync("catalog-channels", default);
                True(!existing.Intersect(f.Module.CatalogSections, ReferenceEqualityComparer.Instance).Any(), "invalidated sections never revive");
            }
        });
        yield return Case("channel directory with no displayed openable item settles empty", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channels", value => Edit(value, json =>
            {
                foreach (var item in json["items"]!.AsArray()) item!["available"] = false;
            }));
            await f.Module.NavigateAsync("catalog-channels", default);
            Equal(0, f.Client.Count("music.catalog.channel"));
            True(f.Module.IsChannelDirectory && f.Module.CatalogSections.Count == 0 && !f.Module.ShowCatalogSkeleton,
                "no openable channel ends with an empty page");
            Equal("暂无内容", f.Module.Status);
            string channelLocation = f.Module.CaptureNavigationLocation();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.RestoreNavigationAsync(channelLocation, default);
            Equal(1, f.Client.Count("music.catalog.channels"));
            Equal(0, f.Client.Count("music.catalog.channel"));
        });
        yield return Case("default channel skips a hidden channel and selects the first displayed openable item", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channels", value => Edit(value, json =>
                json["items"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "feature")!["display"] = false));
            await f.Module.NavigateAsync("catalog-channels", default);
            Equal("wy/playlist", f.Module.CurrentChannelKey);
            True(f.Module.ChannelTabs.All(card => card.Display),
                "the channel surface contains only displayed entries");
        });
        yield return Case("default channel failure is stable until explicit refresh", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channel", _ => JsonSerializer.SerializeToElement(new { ok = false, code = "NETWORK_ERROR" }));
            await f.Module.NavigateAsync("catalog-channels", default);
            True(f.Module.Status.Length > 0 && !f.Module.ShowCatalogSkeleton, "failure ends loading with an error");
            int reads = f.Client.Count("music.catalog.channel");
            string channelLocation = f.Module.CaptureNavigationLocation();
            await Drain();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.RestoreNavigationAsync(channelLocation, default);
            Equal(reads, f.Client.Count("music.catalog.channel"));
            await f.Module.RefreshCatalogAsync();
            Equal(reads + 1, f.Client.Count("music.catalog.channel"));
        });
        yield return Case("entry presentation contract separates preview overlay animated and static behavior", async () =>
        {
            var preview = new MusicCatalogCard("playlist", "歌单", "真实预览", "", "playlist", "wy", ActionId: "1",
                Presentation: MusicCatalogEntryPresentation.Preview);
            var overlay = new MusicCatalogCard("similar", "相似歌曲", "遮罩播放", "", "similar", "wy", ActionId: "similar",
                Presentation: MusicCatalogEntryPresentation.Overlay);
            var animated = new MusicCatalogCard("fm", "私人漫游", "动态入口", "", "fm", "wy", ActionId: "fm",
                Presentation: MusicCatalogEntryPresentation.Animated);
            var card = new MusicCatalogCard("artist-entry", "相似艺人", "静态入口", "",
                "unsupported", "wy", Available: false, ErrorCode: "UNSUPPORTED",
                Presentation: MusicCatalogEntryPresentation.Static);
            True(preview.IsPreviewEntry && preview.CanRequestPreview && !preview.IsOverlayEntry && !preview.IsAnimatedEntry,
                "preview contract");
            True(overlay.IsOverlayEntry && !overlay.IsPreviewEntry && !overlay.IsAnimatedEntry && !overlay.CanRequestPreview,
                "overlay contract");
            True(animated.IsAnimatedEntry && !animated.IsPreviewEntry && !animated.IsOverlayEntry && !animated.CanRequestPreview,
                "animated contract");
            True(!card.IsPreviewEntry && !card.IsOverlayEntry && !card.IsAnimatedEntry && !card.CanRequestPreview,
                "static contract");
            var previewEntry = new EntryCard { DataContext = preview };
            var previewOpens = 0; previewEntry.OpenRequested += (_, _) => previewOpens++;
            previewEntry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Equal(0, previewOpens);
            var previewHost = new Window { Content = previewEntry, Width = 168, Height = 230,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                previewHost.Show(); previewHost.UpdateLayout(); await Drain();
                previewEntry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Equal(0, previewOpens);
                foreach (var passive in new[] { overlay, animated, card })
                {
                    var passiveEntry = new EntryCard { DataContext = passive };
                    var requests = 0; passiveEntry.OpenRequested += (_, _) => requests++;
                    previewHost.Content = passiveEntry; previewHost.UpdateLayout(); await Drain();
                    passiveEntry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    Equal(0, requests);
                }
            }
            finally { previewHost.Content = null; previewHost.Close(); }
            var entry = new EntryCard { DataContext = card };
            var window = new Window { Content = entry, Width = 152, Height = 200, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var surface = (Grid)entry.FindName("Surface");
                True(entry.IsEnabled && surface.IsEnabled, "unsupported action must not disable the visual hover surface");
                True(!((Button)entry.FindName("OpenButton")).IsEnabled && !((Button)entry.FindName("PlayButton")).IsEnabled,
                    "unsupported open and play remain denied; artwork retry is a separate control");
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Equal(0, ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count);
                CardMotion.Transition(surface, true, false); await Drain();
                var caption = (FrameworkElement)entry.FindName("ContentBlock");
                True(caption.RenderTransform.Value.IsIdentity, "an unsupported entry without songs has no content to lift");
                CardMotion.Transition(surface, false, false); await Drain();
                True(caption.RenderTransform.Value.IsIdentity, "an unsupported entry retains the resting footer position");
                Equal(Visibility.Collapsed, ((Button)entry.FindName("PlayButton")).Visibility);
            }
            finally { window.Close(); }
        });
        yield return Case("entry palette conversion uses decoded artwork without changing shared image tones", () =>
        {
            var converter = new MusicCatalogPaletteConverter();
            var tones = new List<Color>();
            foreach (var color in new[] { Color.FromRgb(83, 74, 85), Color.FromRgb(169, 149, 142), Colors.White })
            {
                var image = MusicEntryPaletteTests.Image((x, y) => color);
                var raw = (SolidColorBrush)ArtworkImage.SamplePalette(image).Raw;
                var brush = (SolidColorBrush)converter.Convert(image, typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);
                True(brush.IsFrozen, "sampled palettes are shared immutable render input");
                Equal(color, raw.Color);
                tones.Add(brush.Color);
            }
            True(tones[2].G > 150 && tones[2].G > tones[0].G + 50 && tones[1].R > tones[1].B,
                "light and dark images retain distinct roles, while a warm image retains its color family");
            return Task.CompletedTask;
        });
        yield return Case("home headings use system typography and unclipped refresh without extra options", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var view = new MusicCatalogView { DataContext = f.Module, Width = 900, Height = 700 };
            var window = new DesktopWindow
            {
                Content = view, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
                Left = -32000, Top = -32000, Width = 900, Height = 700
            };
            try
            {
                window.Show();
                view.Measure(new(900, 700)); view.Arrange(new Rect(0, 0, 900, 700)); view.UpdateLayout(); await Drain();
                var buttons = Descendants<Button>(view).Where(b => b.IsVisible).ToArray();
                var channelMore = (System.Windows.Controls.Primitives.Popup)view.FindName("ChannelMorePopup");
                Equal(System.Windows.Controls.Primitives.PopupAnimation.None, channelMore.PopupAnimation);
                Equal(System.Windows.Controls.Primitives.PlacementMode.Custom, channelMore.Placement);
                Equal(572d, ((Border)channelMore.Child).Width);
                True(view.FindName("ChannelMoreClip") is RectangleGeometry,
                    "more channels use a top-centred clip reveal instead of popup scaling");
                var refreshes = buttons.Where(b => AutomationProperties.GetName(b) == "刷新推荐").ToArray();
                True(refreshes.Length > 0, "real headings have refresh controls");
                foreach (var refresh in refreshes)
                {
                    Equal(24d, refresh.ActualHeight); Equal(0d, refresh.MinHeight);
                    Equal(Color.FromRgb(139,144,150), ((SolidColorBrush)refresh.Foreground).Color);
                    True(Descendants<System.Windows.Shapes.Path>(refresh).Any(), "vector refresh, not clipped font glyph");
                    var canvas = Descendants<Canvas>(refresh).Single();
                    True(canvas.TranslatePoint(new Point(), refresh).Y >= 0 && canvas.ActualHeight <= refresh.ActualHeight,
                        "complete icon fits within actual button bounds");
                }
                var body = SystemFonts.MessageFontFamily;
                var title = Descendants<TextBlock>(view).First(text => text.IsVisible &&
                    !string.IsNullOrWhiteSpace(text.Text) && text.Style == view.FindResource("Catalog.SectionTitle"));
                Equal(body, title.FontFamily); Equal(18d, title.FontSize);
                Equal(FontWeights.Normal, title.FontWeight);
                var playlist = Descendants<TextBlock>(view).First(text => text.IsVisible &&
                    !string.IsNullOrWhiteSpace(text.Text) && text.Style == view.FindResource("Catalog.PlaylistTitle"));
                Equal(body, playlist.FontFamily); Equal(13d, playlist.FontSize);
                Equal(FontWeights.Normal, playlist.FontWeight); Equal(20d, playlist.LineHeight);
                Equal(Color.FromRgb(75,79,88), ((SolidColorBrush)playlist.Foreground).Color);
                var catalogText = Descendants<TextBlock>(view).ToArray();
                AssertDisplayAutoText(catalogText, "actual catalog headings and captions");
                foreach (var text in new[] { title, playlist })
                foreach (var mode in new[] { TextRenderingMode.Grayscale, TextRenderingMode.ClearType, TextRenderingMode.Aliased })
                {
                    var originalMode = TextOptions.GetTextRenderingMode(text);
                    text.SetCurrentValue(TextOptions.TextRenderingModeProperty, mode);
                    try
                    {
                        Equal(mode, TextOptions.GetTextRenderingMode(text));
                        Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(view));
                        var rejected = false;
                        try { AssertDisplayAutoText(catalogText, "actual catalog headings and captions"); }
                        catch (InvalidOperationException) { rejected = true; }
                        True(rejected, $"the text-node contract must reject {mode} on '{text.Text}' even with an Auto parent");
                    }
                    finally { text.SetCurrentValue(TextOptions.TextRenderingModeProperty, originalMode); }
                    AssertDisplayAutoText(catalogText, "actual catalog headings and captions");
                }
                foreach (var text in new[] { title, playlist })
                {
                    var originalMode = TextOptions.GetTextFormattingMode(text);
                    text.SetCurrentValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Ideal);
                    try
                    {
                        Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(text));
                        Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(view));
                        var rejected = false;
                        try { AssertDisplayAutoText(catalogText, "actual catalog headings and captions"); }
                        catch (InvalidOperationException) { rejected = true; }
                        True(rejected, $"the text-node contract must reject Ideal on '{text.Text}' even with a Display parent");
                    }
                    finally { text.SetCurrentValue(TextOptions.TextFormattingModeProperty, originalMode); }
                    AssertDisplayAutoText(catalogText, "actual catalog headings and captions");
                }
            }
            finally { window.Content = null; window.Close(); await Drain(); }
        });
        yield return Case("home text follows the system font and entry captions stay pixel aligned after resize", async () =>
        {
            var card = new MusicCatalogCard("500", "每日推荐", "推荐歌曲", "", "playlist", "wy", ActionId: "500",
                Presentation: MusicCatalogEntryPresentation.Preview);
            var view = new MusicCatalogView { Width = 240, Height = 300 };
            card.InitializePreview(new[] { "第一首", "第二首", "第三首" }.Select((title, i) => new MusicRankPreview(title, "", "", i + 1, null)).ToArray());
            var entry = new EntryCard { DataContext = card };
            view.Content = entry;
            var window = new DesktopWindow
            {
                Content = view, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
                Left = -32000, Top = -32000, Width = 240, Height = 300
            };
            void Layout()
            { view.Measure(new(240, 300)); view.Arrange(new Rect(0, 0, 240, 300)); view.UpdateLayout(); }
            try
            {
                window.Show(); Layout(); await Drain();
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(view));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(view));
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(entry));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(entry));
                Equal(SystemFonts.MessageFontFamily, view.FontFamily);
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(entry));
                True(view.UseLayoutRounding && entry.UseLayoutRounding && entry.SnapsToDevicePixels,
                    "text and cover layout align to the current display pixels");
                var alternate = new FontFamily("Arial");
                window.Resources[SystemFonts.MessageFontFamilyKey] = alternate;
                await Drain();
                Equal(alternate, view.FontFamily); Equal(alternate, TextElement.GetFontFamily(entry));
                var surface = (Grid)entry.FindName("Surface");
                var caption = (TextBlock)entry.FindName("Caption");
                var subtitle = caption;
                Equal(alternate, subtitle.FontFamily);
                var entryText = Descendants<TextBlock>(entry).ToArray();
                Equal(3, ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count);
                True(((MusicCatalogCard)entry.DataContext).PreviewTitles.All(preview => entryText.Any(text => text.Text == preview)),
                    "entry typography covers the actual generated preview text");
                True(((Button)entry.FindName("OpenButton")).FocusVisualStyle is not null,
                    "entry actions keep the shared rounded keyboard focus");
                foreach (var width in new[] { 152.4, 180.8, 200.6 })
                {
                    entry.Width = width;
                    Layout(); await Drain();
                    CardMotion.Transition(surface, false, false);
                    await Drain();
                    AssertDisplayAutoText(Descendants<TextBlock>(entry), "resting entry title caption and preview");
                    True(((FrameworkElement)entry.FindName("ContentBlock")).RenderTransform.Value.IsIdentity,
                        "the content starts in its two-line footer");
                    var y = caption.TranslatePoint(new Point(), surface).Y;
                    var pixelY = y * VisualTreeHelper.GetDpi(surface).DpiScaleY;
                    True(Math.Abs(pixelY - Math.Round(pixelY)) < .01, "caption baseline starts on a display pixel");
                    var cover = (ArtworkImage)entry.FindName("Cover");
                    True(y >= cover.ActualHeight && y + subtitle.ActualHeight <= surface.ActualHeight,
                        "the resting caption fits in its footer after resize");
                    var clip = (RectangleGeometry)surface.Clip;
                    True(clip.RadiusX > 0 && clip.RadiusY > 0, "the complete entry has rounded corners");
                    Equal(surface.RenderSize, clip.Rect.Size);
                    CardMotion.Transition(surface, true, false);
                    await Drain();
                    AssertDisplayAutoText(Descendants<TextBlock>(entry), "raised entry title caption and preview");
                    True(caption.TranslatePoint(new Point(), surface).Y < y,
                        "the caption rises with its preview instead of remaining underneath it");
                    var raisedY = caption.TranslatePoint(new Point(), surface).Y * VisualTreeHelper.GetDpi(surface).DpiScaleY;
                    True(Math.Abs(raisedY - Math.Round(raisedY)) < .01, "settled preview also starts on a display pixel");
                    var layoutClip = LayoutInformation.GetLayoutClip(caption);
                    True(layoutClip is null || layoutClip.Bounds.Height >= caption.DesiredSize.Height,
                        "all preview rows remain outside the footer's layout constraint");
                }
            }
            finally { window.Content = null; window.Close(); await Drain(); }
        });
        yield return Case("catalog system typography uses Display Auto text and paints neutral glyphs at bitmap DPI", () =>
        {
            var view = new MusicCatalogView();
            var host = new Grid { Background = Brushes.White };
            var text = new TextBlock { Text = "二次元赛高", Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0) };
            host.Children.Add(text); view.Content = host;
            var window = new DesktopWindow { Content = view };
            try
            {
                Equal(SystemFonts.MessageFontFamily, view.FontFamily);
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(view));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(view));
                var preview = new ToolTip { Style = (Style)view.FindResource("Capability.SeekPreview") };
                Equal(SystemFonts.MessageFontFamily, preview.FontFamily);
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(preview));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(preview));
                True(preview.UseLayoutRounding && preview.SnapsToDevicePixels, "detached seek previews retain pixel-aligned text");
                foreach (var role in new[] { "Catalog.SectionTitle", "Catalog.SongTitle", "Catalog.PlaylistTitle" })
                {
                    text.Style = (Style)view.FindResource(role);
                    view.Measure(new(200, 52)); view.Arrange(new Rect(0, 0, 200, 52)); view.UpdateLayout();
                    Equal(SystemFonts.MessageFontFamily, text.FontFamily);
                    AssertDisplayAutoText([text], role);
                    // RenderTargetBitmap has no HWND: scaled neutral pixels prove glyph drawing,
                    // not OS subpixel rendering at a monitor's DPI.
                    foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                    {
                        var bitmap = new RenderTargetBitmap((int)(200 * scale), (int)(52 * scale),
                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(view);
                        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
                        var ink = 0;
                        for (var index = 0; index < bytes.Length; index += 4)
                        {
                            True(Math.Abs(bytes[index] - bytes[index + 1]) <= 1 &&
                                Math.Abs(bytes[index + 1] - bytes[index + 2]) <= 1,
                                "offscreen neutral glyph pixels keep matching RGB channels");
                            if (bytes[index + 3] > 240 && bytes[index] < 200) ink++;
                        }
                        True(ink > 50, "offscreen drawing evidence covers actual painted glyphs, not empty output");
                    }
                }
            }
            finally { window.Content = null; window.Close(); }
            return Task.CompletedTask;
        });
        yield return Case("home controls execute through automation patterns on a hidden nonactivating source", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3, total = 3, homePages = 2 });
            await f.SetupSource();
            await f.SearchPolicy("wy", playlists: true);
            f.Module.CatalogSearchKind = "歌单";
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            shell.PrepareForRender();
            var root = (Border)shell.Content; shell.Content = null;
            root.Resources.MergedDictionaries.Add(shell.Resources);
            root.DataContext = shell.DataContext;
            root.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Window.Background)) { Source = shell });
            TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
            TextElement.SetForeground(root, shell.Foreground);
            TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(shell));
            TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(shell));
            root.UseLayoutRounding = true; root.SnapsToDevicePixels = true;
            var parameters = new HwndSourceParameters("catalog-headless-control-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            };
            using var source = new HwndSource(parameters);
            void Layout()
            { root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout(); }
            void Invoke(Button button)
            {
                True(button.IsEnabled, "headless automation invokes only enabled controls");
                var peer = UIElementAutomationPeer.CreatePeerForElement(button)
                    ?? throw new InvalidOperationException("Missing button automation peer");
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            }
            try
            {
                source.RootVisual = root; Layout();
                Equal(SystemFonts.MessageFontFamily, shell.FontFamily);
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(f.Module.SettingsView));
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(f.Module.PlayerView));
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(f.Module.SettingsView));
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(f.Module.PlayerView));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(f.Module.SettingsView));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(f.Module.PlayerView));
                var detail = f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var queue = f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(detail));
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(queue));
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(detail));
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(queue));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(detail));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(queue));
                shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Until(() => f.Module.IsCatalogHome && f.Client.Count("music.catalog.home") >= 1 &&
                    !f.Module.Busy && ((ListBox)shell.FindName("NavigationList")).SelectedItem is
                        ShellNavigationEntry { Route: "catalog-home" }, "headless home startup did not settle");
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                Layout(); await Drain();
                Equal(IntPtr.Zero, new WindowInteropHelper(shell).Handle);
                True((parameters.WindowStyle & 0x10000000) == 0 && (parameters.ExtendedWindowStyle & 0x08000000) != 0,
                    "the isolated presentation source is neither visible nor activatable");
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var search = (TextBox)shell.FindName("SearchBox");
                var searchPeer = UIElementAutomationPeer.CreatePeerForElement(search)!;
                var value = (IValueProvider)searchPeer.GetPattern(PatternInterface.Value);
                var searchButton = (Button)shell.FindName("SearchButton");
                var back = (Button)shell.FindName("BackButton");
                var player = f.Module.PlayerView;
                value.SetValue("   "); Invoke(searchButton); await Drain();
                Equal(0, f.Client.Count("music.search.start"));
                var refresh = Descendants<Button>(catalog).First(button =>
                    button.IsVisible && AutomationProperties.GetName(button) == "刷新推荐");
                int homeReads = f.Client.Count("music.catalog.home");
                Invoke(refresh);
                await Until(() => f.Client.Count("music.catalog.home") > homeReads && !f.Module.Busy,
                    "headless refresh did not settle");
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                Layout(); await Drain();
                var entry = Descendants<EntryCard>(catalog).First(control =>
                    control.DataContext is MusicCatalogCard { CanRequestPreview: true });
                var surface = (Grid)entry.FindName("Surface");
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                await Until(() => ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count == 3, "headless preview did not settle");
                CardMotion.Transition(surface, true, false); await Drain();
                Equal(4, f.Client.Count("music.catalog.playlist"));
                True(Descendants<TextBlock>(entry).Any(text => text.Text.StartsWith("1 合成曲", StringComparison.Ordinal)),
                    "preview controls publish the actual native preview receipt");
                Invoke((Button)entry.FindName("PlayButton"));
                await Until(() => f.Playback.Opens.Count == 1 && !f.Module.Busy, "headless play did not settle");
                Layout(); await Drain();
                var playingTitle = Descendants<TextBlock>(player).Single(text => text.Text == f.Module.CurrentTitle);
                var playingArtist = Descendants<TextBlock>(player).Single(text => text.Text == f.Module.CurrentArtist);
                AssertDisplayAutoText([playingTitle, playingArtist], "actual playing title and artist");
                Equal(1, f.Client.Count("music.catalog.playlist.prepare"));
                Equal(1, f.Client.Count("music.catalog.playlist.commit"));
                True(f.Module.IsCatalogHome, "the entry play action does not navigate");
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                CardMotion.Transition(surface, false, false);
                value.SetValue("  云歌单  "); Invoke(searchButton);
                await Until(() => f.Client.Count("music.search.start") == 2 && f.Module.IsSearchPage,
                    "headless search did not settle");
                await WaitForSearch(f.Module); Layout();
                Equal("云歌单", f.Client.Last("music.search.start").GetProperty("keyword").GetString());
                Equal("playlists", f.Client.Last("music.search.start").GetProperty("kind").GetString());
                True(f.Module.CatalogSections.SelectMany(section => section.Items).Any(card => card.CanOpen),
                    "the real search handler publishes actionable native results");
                Invoke(back);
                await Until(() => f.Module.IsCatalogHome && !f.Module.Busy, "headless search back did not settle");
                Layout(); await Drain();
                True(ReferenceEquals(player, ((ContentControl)shell.FindName("PlayerHost")).Content),
                    "search and back retain the same player controls");
                entry = Descendants<EntryCard>(catalog).First(control =>
                    control.DataContext is MusicCatalogCard { CanRequestPreview: true });
                Invoke((Button)entry.FindName("OpenButton"));
                await Until(() => f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 3 && !f.Module.Busy,
                    "headless entry open did not settle");
                Equal(1, f.Playback.Opens.Count);
                Invoke(back);
                await Until(() => f.Module.IsCatalogHome && !f.Module.Busy, "headless detail back did not settle");
                Layout(); await Drain();
                var scroll = (ScrollViewer)catalog.FindName("HomeScroll");
                var pages = f.Client.Count("music.catalog.home");
                scroll.ScrollToEnd(); Layout(); await Drain();
                Equal(pages, f.Client.Count("music.catalog.home"));
                scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.PageDown)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Drain(); Equal(pages, f.Client.Count("music.catalog.home"));
                True(!f.Module.CanCatalogNext, "scrolling cannot restart completed column initialization");
                foreach (var size in new[] { new Size(860, 600), new Size(1110, 800), new Size(1440, 900) })
                {
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    var content = (Grid)shell.FindName("ContentRegion");
                    var footer = (Border)shell.FindName("PlayerFrame");
                    True(content.TranslatePoint(new Point(0, content.ActualHeight), root).Y <=
                        footer.TranslatePoint(new Point(), root).Y + .5, "the footer does not cover the content");
                    var hit = root.InputHitTest(searchButton.TranslatePoint(new Point(16, 16), root));
                    True(hit is DependencyObject target && Descendants<DependencyObject>(searchButton).Contains(target),
                        "the search icon occupies its actual input hit target");
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var pixels = new byte[(int)size.Width * (int)size.Height * 4];
                    bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
                    True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 240) >
                        size.Width * size.Height * .8, "the hidden source paints real opaque app content");
                }
                True(Mouse.Captured is null, "the headless interaction sequence never captures the pointer");
                Equal(IntPtr.Zero, new WindowInteropHelper(shell).Handle);
                Console.WriteLine("Headless home evidence: hidden NOACTIVATE source; Value/Invoke providers; " +
                    "refresh, preview, play, search, back, open and paging; no product Window.Show or native input.");
            }
            finally
            {
                source.RootVisual = null;
                shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(8)); await Drain();
            }
        });
        yield return Case("each catalog section selects exactly one presenter", async () =>
        {
            var view = new MusicCatalogView();
            var presenterStyle = (Style)view.FindResource("Catalog.SectionPresenter");
            var song = SongRow.Read(MusicSongFixture.Element(
                MusicSongFixture.Online("wy", "1", "歌曲", "歌手", "专辑", 180)));
            var playlist = new MusicCatalogCard("p", "歌单", "", "", "playlist", "wy", ActionId: "p");
            var artist = new MusicCatalogCard("a", "歌手", "单曲：1", "", "artist", "wy", ActionId: "a");
            var ranking = playlist with
            {
                Preview = [new MusicRankPreview("歌曲", "歌手", "", 1, 0)]
            };
            var cases = new[]
            {
                (new MusicCatalogSection("歌单", [playlist], Type: "playlists"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.Shelf),
                (new MusicCatalogSection("榜单推荐", [ranking], Type: "rankings", Layout: "ONLY_COVER"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.RankingCovers),
                (new MusicCatalogSection("精选榜", [playlist], Type: "rankings", Layout: "ONLY_COVER"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.RankingCovers),
                (new MusicCatalogSection("曲风榜", [playlist], Type: "rankings", Layout: "ONLY_COVER"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.RankingCovers),
                (new MusicCatalogSection("官方榜", [playlist], Type: "rankings", Layout: "ranked-songs"), "Catalog.Section.Rankings", MusicCatalogSectionPresentation.RankingPreviews),
                (new MusicCatalogSection("会员榜", [playlist], Type: "rankings", Layout: "vip-ranked-songs"), "Catalog.Section.Rankings", MusicCatalogSectionPresentation.RankingPreviews),
                (new MusicCatalogSection("官方榜数据", [ranking], Type: "rankings", Layout: "TOP_3"), "Catalog.Section.Rankings", MusicCatalogSectionPresentation.RankingPreviews),
                (new MusicCatalogSection("歌曲", [new MusicCatalogCard("s", "歌曲", "歌手", "", "song", "wy", song)]), "Catalog.Section.Songs", MusicCatalogSectionPresentation.Songs),
                (new MusicCatalogSection("歌曲摘要", [new MusicCatalogCard("s", "歌曲", "歌手", "", "song", "wy", song)],
                    Type: "songs", Layout: "rail"), "Catalog.Section.SongShelf", MusicCatalogSectionPresentation.Songs),
                (new MusicCatalogSection("", [artist], Type: "artists", Layout: "artist-grid"), "Catalog.Section.Artists", MusicCatalogSectionPresentation.Artists)
            };
            foreach (var (section, resource, presentation) in cases)
            {
                Equal(presentation, section.Presentation);
                if (presentation == MusicCatalogSectionPresentation.RankingCovers)
                    True(!section.IsPlaylistSection, section.Title + " must not fall into playlist rows");
                var presenter = new ContentControl { DataContext = section, Content = section, Style = presenterStyle };
                var window = new Window { Content = presenter, Width = 900, Height = 300, WindowStyle = WindowStyle.None,
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); window.UpdateLayout(); await Drain();
                    True(ReferenceEquals(view.FindResource(resource), presenter.ContentTemplate),
                        section.Title + " chooses one presenter");
                }
                finally { window.Close(); }
            }
        });
        yield return Case("summary song shelves keep source order in fixed three-row horizontal columns", async () =>
        {
            var cards = Enumerable.Range(1, 7).Select(index =>
            {
                var song = SongRow.Read(MusicSongFixture.Element(
                    MusicSongFixture.Online("wy", index.ToString(), "歌曲" + index, "歌手", "专辑", 180)));
                return new MusicCatalogCard(index.ToString(), song.Title, song.Artist, song.Cover,
                    "song", "wy", song, ActionId: index.ToString());
            }).ToArray();
            var section = new MusicCatalogSection("歌曲", cards, Type: "songs", Layout: "rail");
            Equal("1,2,3|4,5,6|7", string.Join("|",
                section.SongColumns.Select(column => string.Join(",", column.Items.Select(item => item.Id)))));

            var view = new MusicCatalogView();
            var singleColumn = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.SongColumn")).LoadContent();
            singleColumn.DataContext = new MusicCatalogSongColumn(cards.Take(1).ToArray());
            singleColumn.Measure(new Size(420, double.PositiveInfinity));
            Equal(252d, singleColumn.DesiredSize.Height);
            var presenter = new ContentControl
            {
                DataContext = section,
                Content = section,
                Style = (Style)view.FindResource("Catalog.SectionPresenter")
            };
            var window = new Window
            {
                Content = presenter, Width = 900, Height = 320, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var shelf = Descendants<Shelf>(presenter).Single();
                Equal(3, shelf.ItemsSource!.Cast<MusicCatalogSongColumn>().Count());
                Equal(Visibility.Collapsed, ((Button)shelf.FindName("Previous")).Visibility);
                Equal(Visibility.Visible, ((Button)shelf.FindName("Next")).Visibility);
            }
            finally { window.Close(); }
        });
        yield return Case("channel chrome owns direct tabs filters and no duplicate platform selector", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "feature"));
            var view = new MusicCatalogView { DataContext = f.Module };
            var window = new Window
            {
                Content = view, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var header = (DockPanel)view.FindName("ChannelHeader");
                Equal(Visibility.Visible, header.Visibility);
                Equal(new Thickness(40, 0, 40, 12), header.Margin);
                True(!Descendants<ComboBox>(view).Any(combo => combo.IsVisible &&
                    AutomationProperties.GetName(combo) == "内容平台"),
                    "channel chrome has no duplicate platform selector");
                var direct = Descendants<Button>(header)
                    .Where(button => button.Tag is MusicCatalogCard).ToArray();
                Equal("feature,playlist,chart,artist,vip,euro,djR,huayu,qingge,acg", string.Join(",", direct.Select(button =>
                    ((MusicCatalogCard)button.Tag).ActionId)));
                Equal("精选,歌单广场,排行榜,歌手,VIP,欧美,电音,华语,情歌,二次元", string.Join(",", direct.Select(button =>
                    ((MusicCatalogCard)button.Tag).Title)));
                var selected = direct.Single(button => ((MusicCatalogCard)button.Tag).ActionId == "feature");
                var underline = Descendants<Border>(selected)
                    .Single(border => border.Name == "SelectionUnderline");
                Equal(16d, underline.ActualWidth); Equal(3d, underline.ActualHeight);
                Equal(((SolidColorBrush)view.FindResource("MeowFilm.Brush.Accent")).Color,
                    ((SolidColorBrush)underline.Background).Color);

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "rock"));
                await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Id == "30"));
                window.UpdateLayout(); await Drain();
                var filters = (ItemsControl)view.FindName("ChannelFilters");
                Equal(Visibility.Visible, filters.Visibility);
                Equal(new Thickness(40, 0, 40, 16), filters.Margin);
                var tagButtons = Descendants<Button>(filters)
                    .Where(button => button.Tag is MusicCatalogTag).ToArray();
                Equal(2, tagButtons.Length);
                True(tagButtons.All(button => button.ActualHeight == 32 && button.ActualWidth >= 62),
                    "channel filters use the measured pill geometry");
                var selectedTag = f.Module.SelectedCatalogTag ??
                    throw new InvalidOperationException("channel selected tag");
                var selectedPill = tagButtons.Single(button =>
                    ((MusicCatalogTag)button.Tag).Id == selectedTag.Id);
                Equal(((SolidColorBrush)view.FindResource("MeowFilm.Brush.AccentSoft")).Color,
                    ((SolidColorBrush)selectedPill.Background).Color);
                Equal(new CornerRadius(16), Descendants<Border>(selectedPill)
                    .Single(border => border.Name == "ChannelFilterPill").CornerRadius);
            }
            finally { window.Close(); }
        });
        yield return Case("VIP route owns one three-card row and clears it outside the retained page", async () =>
        {
            await using var f = await Fixture.Create(vipAccount: true);
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "vip"));
            var vip = f.Vip ?? throw new InvalidOperationException("VIP fixture");
            Equal(0, vip.Reads);

            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 1,
                    "VIP cards did not activate after the page loaded");
                window.UpdateLayout(); await Drain();

                var catalog = (MusicCatalogView)page.FindName("CatalogView");
                var row = (Grid)catalog.FindName("VipCardRow");
                var account = (Border)catalog.FindName("VipAccountTile");
                var sign = (Border)catalog.FindName("VipSignCard");
                var banner = (Button)catalog.FindName("VipBannerButton");
                Equal(Visibility.Visible, row.Visibility);
                Equal(960d, row.ActualWidth); Equal(184d, row.ActualHeight);
                Equal(new Thickness(40, 0, 40, 32), row.Margin);
                Equal("228,16,228,16,472", string.Join(",", row.ColumnDefinitions.Select(column =>
                    column.ActualWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
                True(ReferenceEquals(Ancestor<ScrollViewer>(row), catalog.CatalogScroll),
                    "VIP cards belong to the same vertical transition and retention surface as the sections");
                Equal(Visibility.Visible, account.Visibility); Equal(228d, account.ActualWidth);
                Equal(Visibility.Visible, sign.Visibility); Equal(228d, sign.ActualWidth);
                Equal(Visibility.Visible, banner.Visibility); Equal(472d, banner.ActualWidth);
                var gradient = (LinearGradientBrush)account.Background;
                Equal(Color.FromRgb(33, 27, 27), gradient.GradientStops[0].Color);
                Equal(Color.FromRgb(91, 48, 48), gradient.GradientStops[1].Color);
                True(Descendants<TextBlock>(account).Any(text => text.Text == "NinonZQ"), "real account name is visible");
                True(Descendants<TextBlock>(account).Any(text => text.Text.StartsWith("SVIP · ", StringComparison.Ordinal)),
                    "real tier and expiry are visible");
                True(!Descendants<Button>(account).Any(button => Ancestor<ArtworkImage>(button) is null),
                    "no unconnected upgrade or marketing action");
                True(Descendants<TextBlock>(sign).Any(text => text.Text == "10月黑胶乐签") &&
                    Descendants<TextBlock>(sign).Any(text => text.Text == "第2天，得3成长值"),
                    "sign title and subtitle are native data");
                Equal(3, f.Module.VipPage.Sign!.Days.Count);
                Equal("https://img.invalid/sign-bg.jpg",
                    Descendants<ArtworkImage>(sign).First().Address);
                Equal("https://img.invalid/banner.jpg",
                    Descendants<ArtworkImage>(banner).Single().Address);
                Equal(1, vip.Reads);

                var signButton = Descendants<Button>(sign).Single(button =>
                    AutomationProperties.GetName(button) == "黑胶乐签打卡");
                signButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => vip.Signs == 1 && f.Module.VipPage.SignedToday &&
                    !signButton.IsEnabled && Equals(signButton.Content, "已打卡"),
                    "VIP sign did not publish its single refreshed sign branch");
                Equal(1, vip.Reads);

                banner.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => f.Module.IsCatalogPlaylist,
                    "VIP playlist banner did not navigate through the catalog owner");
                await f.Module.NavigateAsync("catalog-channel/wy/vip", default);
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 2,
                    "returning to VIP did not perform one page read");

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(item => item.ActionId == "feature"));
                True(!f.Module.IsVipChannel,
                    "channel chrome remains navigable after the VIP projection changes");
                await Until(() => !f.Module.VipPage.HasCards,
                    "VIP cards did not clear after leaving their route");
                window.UpdateLayout();
                Equal(Visibility.Collapsed, row.Visibility);

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(item => item.ActionId == "vip"));
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 3,
                    "VIP cards did not reactivate after returning to their route");
                vip.Invalidate();
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 4,
                    "VIP cards did not reload once after invalidation");
            }
            finally
            {
                window.Close();
                await Until(() => !f.Module.VipPage.HasCards,
                    "VIP cards did not clear after the page unloaded");
            }
        });
        yield return Case("VIP song banner uses the shared native song playback action", async () =>
        {
            await using var f = await Fixture.Create(vipAccount: true);
            var vip = f.Vip ?? throw new InvalidOperationException("VIP fixture");
            await f.Configure(new { total = 1 });
            vip.SetBanner("SONG", "1001");
            await f.SetupSource();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "vip"));
            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.CanOpenBanner && vip.Reads == 1);
                await f.Module.OpenVipBannerAsync();
                await Until(() => f.Client.Count("music.catalog.song") == 1 &&
                    f.Playback.Opens.Count == 1, "VIP song banner did not use shared playback");
                var queue = await f.Rpc("music.queue.get", new { limit = 1 });
                Equal(1, queue.GetProperty("total").GetInt32());
                Equal("1001", MusicSongFixture.NativeId(queue.GetProperty("current").GetProperty("song")));
            }
            finally { window.Close(); }
        });
        yield return Case("VIP song banner ignores a late song receipt after its card is invalidated", async () =>
        {
            await using var f = await Fixture.Create(vipAccount: true);
            var vip = f.Vip ?? throw new InvalidOperationException("VIP fixture");
            await f.Configure(new { total = 1 });
            vip.SetBanner("SONG", "1001");
            await f.SetupSource();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card =>
                card.ActionId == "vip"));
            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            Held? hold = null;
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.CanOpenBanner && vip.Reads == 1);
                hold = f.Client.Hold("music.catalog.song");
                var opening = f.Module.OpenVipBannerAsync();
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

                vip.Invalidate();
                await Until(() => vip.Reads == 2 && f.Module.VipPage.CanOpenBanner,
                    "VIP invalidation did not publish a replacement card");
                hold.Release();
                await opening;

                Equal(1, f.Client.Count("music.catalog.song"));
                Equal(0, f.Playback.Opens.Count);
                Equal(0, (await f.Rpc("music.queue.get", new { }))
                    .GetProperty("total").GetInt32());
            }
            finally
            {
                hold?.Release();
                window.Close();
            }
        });
        yield return Case("artist card is a 184 DIP hit target and opens one native round detail surface", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { artistPages = 3 });
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Group == "area" && tag.Name == "欧美"));
            await f.Module.CatalogPageAsync(1);
            Equal("第 2 页", f.Module.CatalogPageText);
            Equal(80, f.Module.CatalogSections.Single().Items.Count);
            var selectedTag = f.Module.SelectedCatalogTag?.Id ?? throw new InvalidOperationException("artist selected tag");
            var channelCalls = f.Client.Count("music.catalog.channel");
            var card = f.Module.CatalogSections.Single().Items.First();
            True(card.Kind == "artist" && card.CanOpen, "artist card must expose the native detail action");

            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var catalog = (MusicCatalogView)page.FindName("CatalogView");
                var scroll = catalog.CatalogScroll;
                True(scroll.ScrollableHeight > 0, "artist fixture must expose vertical scroll");
                scroll.ScrollToVerticalOffset(Math.Min(240, scroll.ScrollableHeight));
                window.UpdateLayout(); await Drain();
                var savedOffset = scroll.VerticalOffset;

                var button = Descendants<Button>(catalog).Single(candidate => ReferenceEquals(candidate.Tag, card));
                Equal(184d, button.ActualWidth);
                Equal(184d, Descendants<Grid>(button).Single(grid => CardClip.GetRadius(grid) == 92).ActualWidth);
                Equal(card.Title, AutomationProperties.GetName(button));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => f.Module.IsCatalogArtist && !f.Module.Busy);

                True(f.Module.IsCatalogDetailPage && f.Module.IsContentDetailPage && f.Module.ShowsSongTable &&
                    !f.Module.IsCatalogContent, "artist detail must own the shared native song surface");
                Equal(card.Title, f.Module.Heading);
                Equal("热门歌曲", f.Module.DetailSectionTitle);
                Equal("3", f.Module.DetailCountText);
                Equal("https://img.invalid/artist-" + card.ActionId + ".jpg", f.Module.DetailArtwork);
                Equal(3, f.Module.Songs.Count);
                Equal(1, f.Client.Count("music.catalog.artist"));
                window.UpdateLayout(); await Drain();
                var artwork = (Border)page.FindName("DetailArtworkFrame");
                Equal(new CornerRadius(76), artwork.CornerRadius);
                Equal(76d, CardClip.GetRadius(artwork));
                True(artwork.Clip is RectangleGeometry { RadiusX: 76, RadiusY: 76 },
                    "artist artwork content must be physically clipped to the round frame");
                True(Descendants<TextBlock>(page).Any(text => text.IsVisible && text.Text == "热门歌曲"),
                    "artist detail must label the real hot-song section");

                await f.SetupSource();
                await f.Module.PlayContentDetailAsync();
                Equal(3, (await f.Rpc("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
                Equal(1, f.Client.Count("music.catalog.artist"));

                await f.Module.NavigateAsync("catalog-channel/wy/artist", default);
                window.UpdateLayout(); await Drain();
                Equal(channelCalls, f.Client.Count("music.catalog.channel"));
                Equal("第 2 页", f.Module.CatalogPageText);
                Equal(selectedTag, f.Module.SelectedCatalogTag?.Id);
                Equal(80, f.Module.CatalogSections.Single().Items.Count);
                True(Math.Abs(scroll.VerticalOffset - savedOffset) < 1,
                    $"artist channel vertical position restored ({savedOffset} -> {scroll.VerticalOffset})");
            }
            finally { window.Close(); }
        });
        yield return Case("retired artist receipt cannot replace the page opened after it", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            var card = f.Module.CatalogSections.Single().Items.First();
            var held = f.Client.Hold("music.catalog.artist");
            var pending = f.Module.OpenCatalogCardAsync(card);
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("catalog-home", default);
            await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            held.Release();
            await pending;
            await Drain();
            True(f.Module.IsCatalogHome && !f.Module.IsCatalogArtist, "late artist receipt replaced the current home");
            Equal("发现音乐", f.Module.Heading);
            Equal(1, f.Client.Count("music.catalog.artist"));
        });
        yield return Case("ranking cover sections use square artwork cards and shelf-owned navigation scope", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1, 7)
                .Select(i => new MusicCatalogCard(i.ToString(), "榜" + i, "不应显示", "", "playlist", "wy", ActionId: i.ToString()))
                .ToArray();
            var section = new MusicCatalogSection("曲风榜", cards, Type: "rankings", Layout: "ONLY_COVER");
            var shelf = (Shelf)((DataTemplate)view.FindResource("Catalog.Section.Shelf")).LoadContent();
            shelf.DataContext = section;
            view.Content = shelf;
            var window = new Window { Content = view, Width = 960, Height = 260, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                Equal(MusicCatalogSectionPresentation.RankingCovers, section.Presentation);
                True(ReferenceEquals(view.FindResource("Catalog.RankCoverCard"), shelf.CardTemplate),
                    "ranking covers own a dedicated card template");
                Equal(120d, shelf.MinimumCardWidth);
                True(shelf.CardWidth - 16 is >= 120 and <= 168,
                    "ranking covers use the readable native range without exceeding the reference maximum");
                var card = Descendants<Button>(shelf).First(button => button.Tag is MusicCatalogCard);
                True(Math.Abs(card.ActualWidth - card.ActualHeight) < .5,
                    $"ranking artwork card is square ({card.ActualWidth} × {card.ActualHeight})");
                var visibleText = Descendants<TextBlock>(card).Where(text => text.IsVisible).ToArray();
                Equal(1, visibleText.Length); Equal("榜1", visibleText[0].Text);
                var next = (Button)shelf.FindName("Next");
                var hover = next.Style.Triggers.OfType<DataTrigger>().Single(trigger =>
                    trigger.Binding is System.Windows.Data.Binding binding && binding.Path.Path == "IsMouseOver");
                Equal("ShelfRoot", ((System.Windows.Data.Binding)hover.Binding).ElementName);
            }
            finally { window.Close(); }
        });
        yield return Case("shelf arrows occupy gutters page both ways and hide without overflow", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1,18).Select(i => new MusicCatalogCard(i.ToString(), "歌单"+i, "", "", "playlist", "wy")).ToArray();
            var section = new MusicCatalogSection("推荐歌单", cards);
            var shelf = new Shelf { MinimumCardWidth = 124, CardTemplate = (DataTemplate)view.FindResource("Catalog.PlaylistCard"),
                DataContext = section, Margin = new Thickness(40) };
            view.Content = shelf;
            var window = new Window { Content = view, Width = 906, Height = 320, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
                var previous = (Button)shelf.FindName("Previous"); var next = (Button)shelf.FindName("Next");
                True(rail.ScrollableWidth > 0 && next.IsEnabled && !previous.IsEnabled, "overflow right, left boundary disabled");
                Equal(Visibility.Collapsed, previous.Visibility); Equal(Visibility.Visible, next.Visibility);
                Equal(0d, next.Opacity); True(!next.IsHitTestVisible, "hidden controls do not intercept pointer");
                True(previous.TranslatePoint(new Point(previous.ActualWidth,0), rail).X <= 0, "left arrow outside card viewport");
                True(next.TranslatePoint(new Point(),rail).X >= rail.ActualWidth, "right arrow outside card viewport");
                next.Focus(); await Drain(); Equal(1d, next.Opacity);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                await Until(() => section.HorizontalOffset > 0 && Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5);
                True(previous.IsEnabled, "next moves real content");
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                await Until(() => rail.HorizontalOffset < .5 && section.HorizontalOffset < .5);
                var source = PresentationSource.FromVisual(shelf) ?? throw new InvalidOperationException("shelf presentation source");
                var end = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.End)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                shelf.RaiseEvent(end);
                True(end.Handled, "End is handled by the shelf");
                await Until(() => Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5);
                var home = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Home)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                shelf.RaiseEvent(home);
                True(home.Handled, "Home is handled by the shelf");
                await Until(() => rail.HorizontalOffset < .5 && section.HorizontalOffset < .5);
                shelf.DataContext = new MusicCatalogSection("推荐歌单", cards.Take(2).ToArray());
                window.UpdateLayout(); await Drain();
                await Until(() => next.Visibility == Visibility.Collapsed && previous.Visibility == Visibility.Collapsed);
            }
            finally { window.Close(); }
        });
        yield return Case("shelf virtualizes restores settled offset and never persists an in-between frame", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1, 1000)
                .Select(i => new MusicCatalogCard(i.ToString(), "歌单" + i, "", "", "playlist", "wy")).ToArray();
            var first = new MusicCatalogSection("推荐歌单", cards);
            var second = new MusicCatalogSection("其他歌单", cards.Take(40).ToArray());
            var shelf = new Shelf
            {
                MinimumCardWidth = 124,
                CardTemplate = (DataTemplate)view.FindResource("Catalog.PlaylistCard"),
                DataContext = first,
                Margin = new Thickness(40)
            };
            view.Content = shelf;
            var window = new Window
            {
                Content = view, Width = 906, Height = 320, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
                var items = (ListBox)shelf.FindName("ItemsHost");
                var realized = Enumerable.Range(0, cards.Length)
                    .Count(index => items.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                True(realized > 0 && realized < cards.Length / 4,
                    "realized containers remain bounded by viewport/cache rather than total items");

                var next = (Button)shelf.FindName("Next");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => rail.HorizontalOffset > 1);
                shelf.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                shelf.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                await Until(() => first.HorizontalOffset > 0 && Math.Abs(first.HorizontalOffset - rail.HorizontalOffset) < .5);
                True(first.HorizontalOffset >= shelf.CardWidth,
                    "a click without drag does not interrupt an in-flight native snap");
                var saved = first.HorizontalOffset;
                shelf.DataContext = second; window.UpdateLayout();
                await Until(() => rail.HorizontalOffset < .5);
                shelf.DataContext = first; window.UpdateLayout();
                await Until(() => Math.Abs(rail.HorizontalOffset - saved) < .5);

                rail.ScrollToHorizontalOffset(saved + shelf.CardWidth * .37); window.UpdateLayout();
                var inBetween = rail.HorizontalOffset;
                var settled = Math.Clamp(Math.Round(inBetween / shelf.CardWidth) * shelf.CardWidth, 0, shelf.MaximumOffset);
                shelf.DataContext = second; window.UpdateLayout();
                await Until(() => Math.Abs(first.HorizontalOffset - settled) < .5 && rail.HorizontalOffset < .5);
                True(Math.Abs(first.HorizontalOffset - inBetween) > 1,
                    "a context switch commits the legal snap target, not a transient physical frame");
                shelf.DataContext = first; window.UpdateLayout();
                await Until(() => Math.Abs(rail.HorizontalOffset - settled) < .5);
                saved = settled;

                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => rail.HorizontalOffset > saved + 1);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = 790; window.UpdateLayout();
                await Until(() => Math.Abs(first.HorizontalOffset - rail.HorizontalOffset) < .5 &&
                    Math.Abs(rail.HorizontalOffset / Math.Max(1, shelf.CardWidth) -
                        Math.Round(rail.HorizontalOffset / Math.Max(1, shelf.CardWidth))) < .01);
                rail.ScrollToHorizontalOffset(Math.Min(rail.ScrollableWidth, rail.HorizontalOffset + 17));
                window.UpdateLayout();
                var pendingUnloadOffset = rail.HorizontalOffset;
                var pendingUnloadTarget = Math.Clamp(
                    Math.Round(pendingUnloadOffset / shelf.CardWidth) * shelf.CardWidth, 0, shelf.MaximumOffset);
                view.Content = null; await Drain();
                True(Math.Abs(first.HorizontalOffset - pendingUnloadTarget) < .5,
                    "unload commits the settled target before discarding the timer");
                True(Math.Abs(first.HorizontalOffset - pendingUnloadOffset) > 1,
                    "unload never persists an in-between animation or scrolling frame");
            }
            finally { window.Close(); }
        });
        yield return Case("entry hover variants animate independently and reset on unload", async () =>
        {
            var playlist = new MusicCatalogCard("1", "入口", "真实说明", "", "playlist", "wy", ActionId: "1",
                Presentation: MusicCatalogEntryPresentation.Preview);
            var overlay = playlist with { Kind = "similar", ActionId = "similar", Presentation = MusicCatalogEntryPresentation.Overlay };
            var fm = playlist with { Kind = "fm", ActionId = "fm", Presentation = MusicCatalogEntryPresentation.Animated };
            var staticCard = playlist with { Kind = "unsupported", ActionId = "unsupported", Available = false,
                Presentation = MusicCatalogEntryPresentation.Static };
            True(playlist.IsPreviewEntry && overlay.IsOverlayEntry && fm.IsAnimatedEntry &&
                !staticCard.IsPreviewEntry && !staticCard.IsOverlayEntry && !staticCard.IsAnimatedEntry,
                "entry semantic variants");
            True(!((playlist with { Available = false }).CanPlay), "unavailable entry cannot play");
            var view = new MusicCatalogView();
            var entry = (EntryCard)((DataTemplate)view.FindResource("Catalog.Entry")).LoadContent();
            playlist.InitializePreview(new[] { "第一首", "第二首", "第三首" }.Select((title, i) => new MusicRankPreview(title, "", "", i + 1, null)).ToArray());
            entry.DataContext = playlist;
            entry.Measure(new Size(168, 300)); entry.Arrange(new Rect(0, 0, 168, 200)); entry.UpdateLayout();
            var surface = (Grid)entry.FindName("Surface");
            var caption = (TextBlock)entry.FindName("Caption");
            var restingPosition = caption.TranslatePoint(new Point(), surface);
            CardMotion.Transition(surface, true, false); await Drain();
            Equal(1d, CardMotion.GetProgress(surface)); Equal(200d, entry.ActualHeight);
            True(caption.TranslatePoint(new Point(), surface).Y < restingPosition.Y,
                "a preview entry lifts its caption with the initialized songs");
            foreach (var variant in new[] { overlay, fm })
            {
                entry.DataContext = variant; CardMotion.Transition(surface, true, false); await Drain();
                Equal(restingPosition, caption.TranslatePoint(new Point(), surface));
                Equal(10d, ((Button)entry.FindName("PlayButton")).Margin.Bottom);
            }
            entry.DataContext = staticCard; CardMotion.Transition(surface, true, false); await Drain();
            Equal(Visibility.Collapsed, ((Button)entry.FindName("PlayButton")).Visibility);
            CardMotion.Transition(surface, false, false);
            CardMotion.Transition(surface, true, true); await Task.Delay(60); await Drain();
            True(CardMotion.GetProgress(surface) is > 0 and < 1, "real intermediate animation, not instant switch");
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Equal(0d, CardMotion.GetProgress(surface));
        });
        yield return Case("entry initialization reads three songs once without hover or queue changes", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            JsonElement reply = default;
            f.Client.Transform("music.catalog.playlist", value => { reply = value; return value; });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            True(reply.GetProperty("ok").GetBoolean(), "initialization receives real native metadata");
            Equal(3, card.PreviewTitles.Count);
            Equal(3, f.Client.Last("music.catalog.playlist").GetProperty("limit").GetInt32());
            int reads = f.Client.Count("music.catalog.playlist");
            var entry = new EntryCard { DataContext = card };
            entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Equal(reads, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare")); Equal(0, f.Client.Count("music.queue.replace"));
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("same-resource cards share page initialization and leaving a card does not cancel it", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3, homePages = 1 });
            f.Client.Transform("music.catalog.home", value => Edit(value, page => {
                var entries = page["entries"]!.AsArray();
                var card = entries.First(entry => entry?["action"]?["type"]?.GetValue<string>() == "playlist")!;
                var copy = card.DeepClone(); copy["key"] = "duplicate-preview"; entries.Add(copy);
            }));
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            var pending = f.Module.CatalogEntryInitialization;
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cards = f.Module.CatalogSections.SelectMany(s => s.Items).Where(c => c.CanRequestPreview).ToArray();
            var entry = new EntryCard { DataContext = cards[0] };
            entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            True(!pending.IsCompleted && !hold.Cancelled.Task.IsCompleted, "pointer departure cannot cancel page work");
            Equal(1, f.Client.Count("music.catalog.playlist"));
            hold.Release(); await pending;
            True(cards.Length >= 2 && cards.All(card => card.PreviewTitles.Count == 3), "shared result publishes to each card");
        });
        yield return Case("entry initialization bounds physical concurrency and cancels queued reads on retirement", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { homePages = 1, detailLimit = 3 });
            f.Client.Transform("music.catalog.home", value => Edit(value, page => {
                page["entries"] = JsonSerializer.SerializeToNode(Enumerable.Range(500, 8).Select(id => new {
                    id = id.ToString(), name = "入口 " + id, presentation = "preview", action = new { type = "playlist", id = id.ToString() }
                }).ToArray());
            }));
            var holds = Enumerable.Range(0, 4).Select(_ => f.Client.Hold("music.catalog.playlist")).ToArray();
            await f.Module.NavigateAsync("catalog-home", default);
            var pending = f.Module.CatalogEntryInitialization;
            await Task.WhenAll(holds.Select(hold => hold.Arrived.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(4, f.Client.Count("music.catalog.playlist"));
            await f.Module.NavigateAsync("favorites", default);
            await Task.WhenAll(holds.Select(hold => hold.Cancelled.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var hold in holds) hold.Release();
            await pending; Equal(4, f.Client.Count("music.catalog.playlist")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("cancelled page initialization retains physical work until shutdown drains", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3 });
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            var pending = f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.CancelCatalog();
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = f.Module.PrepareShutdownAsync(); await Task.Delay(35);
            True(!shutdown.IsCompleted && !pending.IsCompleted, "shutdown waits for the noncooperative physical receipt");
            hold.Release(); await pending; await shutdown;
            True(!card.HasPreview, "cancelled initialization cannot publish");
        });
        yield return Case("page generation cancels physical initialization and blocks late publication", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3, homePages = 1 });
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            var pending = f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("favorites", default);
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!pending.IsCompleted, "logical retirement must retain physical drain");
            hold.Release(); await pending;
            True(!card.HasPreview, "late response cannot mutate the retired card");
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.CatalogEntryInitialization;
            Equal(3, f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview).PreviewTitles.Count);
            Equal(2, f.Client.Count("music.catalog.playlist")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("refresh retires pending initialization without clearing newer card data", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3, homePages = 1 });
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            var pending = f.Module.CatalogEntryInitialization;
            var old = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.RefreshCatalogAsync(); await f.Module.CatalogEntryInitialization;
            var current = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!ReferenceEquals(old, current) && current.PreviewTitles.Count == 3, "new generation owns its cards");
            hold.Release(); await pending;
            True(!old.HasPreview && current.PreviewTitles.Count == 3, "old completion cannot overwrite the new generation");
        });
        yield return Case("playlist search cards stay static and never request entry previews", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { searchPlaylistTotal = 61 });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.SearchPolicy("wy", playlists: true);
            await f.Module.SearchAsync("云歌单", default);
            await WaitForSearch(f.Module);
            await f.Module.OpenSearchTabAsync("playlists");
            True(f.Module.CanSearchMore, "fixture search must expose another page");
            var card = f.Module.CatalogSections.Single().Items.First();
            Equal(MusicCatalogEntryPresentation.Static, card.Presentation);
            True(!card.CanRequestPreview, "music search is not an entry preview surface");
            var calls = f.Client.Count("music.catalog.playlist");
            Equal(0, card.PreviewTitles.Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            await f.Module.MoreSearchAsync();
            await WaitForSearch(f.Module);
            var current = f.Module.CatalogSections.Single().Items.First();
            True(ReferenceEquals(card, current), "unchanged first page card keeps its projection");
            Equal(MusicCatalogEntryPresentation.Static, current.Presentation);
            Equal(0, current.PreviewTitles.Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            var appended = f.Module.CatalogSections.Single().Items.Last();
            True(!ReferenceEquals(card, appended), "newly appended page card is a new projection");
            Equal(MusicCatalogEntryPresentation.Static, appended.Presentation);
            Equal(0, appended.PreviewTitles.Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
        });
        yield return Case("initialized native titles bind before the first WPF hover", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var view = new MusicCatalogView { DataContext = f.Module };
            var entry = (EntryCard)((DataTemplate)view.FindResource("Catalog.Entry")).LoadContent();
            entry.DataContext = card; view.Content = entry;
            var window = new Window { Content = view, Width = 168, Height = 230, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                await f.Module.CatalogEntryInitialization;
                Equal(2, f.Client.Count("music.catalog.playlist"));
                Equal(3, ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count);
                True(Descendants<TextBlock>(entry).Any(t => t.Text.StartsWith("1 合成曲", StringComparison.Ordinal)), "real preview text rendered");
                var caption = (TextBlock)entry.FindName("Caption");
                var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(caption);
                True(clip is null || clip.Bounds.Height >= caption.DesiredSize.Height, "expanded preview is not clipped to the collapsed caption's 48px layout slot");
            }
            finally { window.Close(); }
        });
        yield return Case("failed initialization does not retry on hover or request FM", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3 });
            f.Client.Transform("music.catalog.playlist", _ => JsonSerializer.SerializeToElement(new { ok = false, error = new { code = "UNAVAILABLE" } }));
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            Equal(0, card.PreviewTitles.Count);
            int calls = f.Client.Count("music.catalog.playlist");
            var entry = new EntryCard { DataContext = card };
            for (int i = 0; i < 3; i++) entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.fm")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("home card play uses full native playlist once without opening its page", async () =>
        {
            await using var f = await Fixture.Create(); await f.SetupSource();
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
            var navigations = 0; f.Module.NavigationRequested += (_, _) => navigations++;
            var view = new MusicCatalogView { DataContext = f.Module };
            var tile = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.PlaylistCard")).LoadContent();
            tile.DataContext = card; view.Content = tile;
            ((Button)tile).Background = Brushes.Magenta;
            tile.Measure(new Size(140, 200)); tile.Arrange(new Rect(0, 0, 140, 172)); tile.UpdateLayout();
            True(Descendants<Border>(tile).First().Background is SolidColorBrush { Color.A: 0 },
                "generic button hover brush must not tint the playlist caption");
            var play = Descendants<Button>(tile).Single(b => ReferenceEquals(b.Style, view.FindResource("Catalog.CoverPlay")));
            CardMotion.Transition((FrameworkElement)tile, true, false); await Drain(); tile.UpdateLayout();
            True(play.ActualWidth == 44 && play.ActualHeight == 44, "only the central play target owns playback");
            var origin = play.TranslatePoint(new Point(), tile);
            True(origin.X > 0 && origin.Y > 0, "the play target leaves artwork outside its bounds");
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 500 && f.Playback.Opens.Count == 0; i++) { await Task.Delay(10); await Drain(); }
            Equal(1, f.Client.Count("music.catalog.playlist.prepare")); Equal(1, f.Client.Count("music.catalog.playlist.commit"));
            Equal(1, f.Playback.Opens.Count); Equal(0, navigations);
            True(f.Module.IsCatalogHome, "hover play does not navigate to a collection");
            await f.Module.PlayCatalogCardAsync(card with { Available = false });
            Equal(1, f.Client.Count("music.catalog.playlist.commit"));
        });
        yield return Case("home completes all columns without scroll and preserves cards playback and offset", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { homePages = 3, detailLimit = 3 });
            // First batch arrives normally; block the following continuation before its receipt.
            f.Client.Transform("music.catalog.home", value => value);
            var hold = f.Client.Hold("music.catalog.home");
            await f.Module.NavigateAsync("catalog-home", default);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = f.Module.CatalogSections.First(section => section.Items.Count > 0);
            var card = first.Items[0]; first.HorizontalOffset = 137;
            True(f.Module.CanCatalogBrowse && !f.Module.Busy, "first content is interactive while later columns load");
            using var source = new HwndSource(new HwndSourceParameters("home-continuation-fixture")
                { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080, Width = 906, Height = 300 });
            source.RootVisual = f.Module.View;
            f.Module.View.Measure(new(906, 300)); f.Module.View.Arrange(new Rect(0, 0, 906, 300)); f.Module.View.UpdateLayout(); await Drain();
            var view = (MusicCatalogView)f.Module.View.FindName("CatalogView");
            var scroll = (ScrollViewer)view.FindName("HomeScroll");
            scroll.ScrollToVerticalOffset(Math.Min(90, scroll.ScrollableHeight)); await Drain();
            double offset = scroll.VerticalOffset;
            var playing = f.Module.CatalogSections.SelectMany(section => section.Items).First(item => item.Kind == "playlist");
            var preparing = f.Client.Hold("music.catalog.playlist.prepare");
            var action = f.Module.PlayCatalogCardAsync(playing); await preparing.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            hold.Release(); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization; await Drain();
            Equal(3, f.Client.Count("music.catalog.home"));
            True(f.Module.CatalogSections.SelectMany(section => section.Items).Any(item => ReferenceEquals(item, card)), "append retains existing card identity");
            Equal(137d, f.Module.CatalogSections.Single(section => section.Id == first.Id).HorizontalOffset);
            True(Math.Abs(scroll.VerticalOffset - offset) < 1, "append never restores an obsolete scroll snapshot");
            True(!f.Module.CanCatalogNext && !f.Module.CanCatalogPrevious, "home has no manual page boundary");
            scroll.ScrollToEnd(); await Drain(); Equal(3, f.Client.Count("music.catalog.home"));
            True(playing.IsPreparing && !preparing.Cancelled.Task.IsCompleted, "background column append cannot retire the active playback intent");
            f.Module.CancelCatalog(); preparing.Release(); await action;
            True(!playing.IsPreparing, "cancel resets the originating card"); Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            source.RootVisual = null;
        });
        yield return Case("wheel over a horizontal card shelf scrolls the outer page", async () =>
        {
            var shelf = new Shelf { Height = 200 };
            var content = new StackPanel();
            content.Children.Add(shelf); content.Children.Add(new Border { Height = 1600 });
            var outer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window
            {
                Content = outer, Width = 826, Height = 400, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                shelf.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                await Drain(); outer.UpdateLayout();
                True(outer.VerticalOffset > 0, "outer page moves without pointing at whitespace");
                Equal(0d, (shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host")).HorizontalOffset);
            }
            finally { window.Close(); }
        });
        yield return Case("library sync cannot cancel the first pending recommendation navigation", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Client.Hold("music.catalog.home");
            var pending = f.Module.NavigateAsync("catalog-home", default);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, PropertyChangedEventArgs e)
            { if (e.PropertyName == nameof(MusicCapability.CountText)) published.TrySetResult(); }
            f.Module.PropertyChanged += Changed;
            try
            {
                f.Client.NotifyLibrarySync();
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                True(!pending.IsCompleted, "background library refresh must not retire the in-flight catalog route");
                hold.Release(); await pending;
                True(f.Module.IsCatalogHome, "first recommendation owns the visible page");
                True(f.Module.CatalogSections.Count > 0, "recommendation content is published");
            }
            finally { f.Module.PropertyChanged -= Changed; hold.Release(); await pending; }
        });
        yield return Case("desktop catalog preserves real ranking previews and responsive section types", async () =>
        {
            var reply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "rank", title = "榜单", type = "rankings", layout = "ranked-songs", items = new[] {
                    new { id = "123", name = "测试榜", kind = "ranking", cover = "", updateFrequency = "每日更新",
                        action = new { type = "playlist", id = "123" },
                        preview = new[] { new { title = "测试歌曲", artist = "测试歌手", cover = "", rank = 1, lastRank = 3 } } } } } } });
            var section = MusicCatalogDto.Read(reply, "wy", "home", "推荐").Single();
            True(section.IsRankingPreviewSection && !section.IsRankingCoverSection && !section.IsSongSection,
                "ranked-songs layout owns the preview presentation");
            Equal("ranked-songs", section.Layout); Equal("每日更新", section.Items[0].UpdateFrequency);
            Equal("↑", section.Items[0].PreviewSongs.Single().Movement);
            True(section.Items[0].CanOpen, "ranking opens its actual playlist action");
            var columns = new ColumnsConverter();
            Equal(2, columns.Convert(1000d, typeof(int), null!, System.Globalization.CultureInfo.InvariantCulture));
            Equal(2, columns.Convert(1490d, typeof(int), null!, System.Globalization.CultureInfo.InvariantCulture));
            var shelf = new Shelf { MinimumCardWidth = 124, DataContext = section with { Type = "playlists" } };
            await WithMountedShelf(shelf, 1000, () =>
                True(shelf.CardWidth is >= 140 and <= 146, "ordinary covers use seven columns at 1000 pixels"));
            var view = new MusicCatalogView();
            var card = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.PlaylistCard")).LoadContent();
            card.DataContext = section.Items[0];
            card.Measure(new Size(168, 300)); card.Arrange(new Rect(0, 0, 168, 300)); card.UpdateLayout();
            Equal(1, Descendants<TextBlock>(card).Count(text => text.Text == section.Items[0].Title));
            True(Descendants<FrameworkElement>(card).Prepend(card).All(element => element.ToolTip is null),
                "catalog content has no hover hints");
        });
        yield return Case("catalog entry cover ranking and artist cards have no hover hints in either availability state", async () =>
        {
            var view = new MusicCatalogView();
            foreach (var template in new[] { "Catalog.Entry", "Catalog.PlaylistCard", "Catalog.RankCoverCard", "Catalog.ArtistCard" })
            foreach (var available in new[] { true, false })
            {
                var data = new MusicCatalogCard("card", "保留的内容标题", "保留的副标题", "",
                    template == "Catalog.ArtistCard" ? "artist" : "playlist", "wy", ActionId: "123",
                    Available: available, ErrorCode: available ? "" : "UNSUPPORTED");
                var card = (FrameworkElement)((DataTemplate)view.FindResource(template)).LoadContent();
                card.DataContext = data;
                card.Measure(new Size(184, 300)); card.Arrange(new Rect(0, 0, 184, 300)); card.UpdateLayout();
                await Drain();
                var elements = Descendants<FrameworkElement>(card).Prepend(card).ToArray();
                True(elements.All(element => element.ToolTip is null), template + " exposes no ordinary hover hints");
                True(elements.OfType<TextBlock>().Any(text => text.Text == data.Title), "visible title remains");
                var open = card is EntryCard entry ? (Button)entry.FindName("OpenButton") : (Button)card;
                Equal(data.Title, AutomationProperties.GetName(open));
                Equal(available, open.IsEnabled);
            }
        });
        yield return Case("channel entry shelves reuse shared cards and supplied song previews", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channel", reply => Edit(reply, node =>
                node["sections"] = JsonSerializer.SerializeToNode(new[] {
                    new { id = "electronic", title = "电音旗舰", type = "entries", layout = "rail", items = new object[] {
                        new { id = "800", name = "电音歌单", subtitle = "精选电子音乐",
                            cover = "https://art.example.test/channel-playlist.jpg", presentation = "preview",
                            action = new { type = "playlist", id = "800" },
                            preview = new[] {
                                new { title = "第一首电音", artist = "歌手一", cover = "", rank = 1, lastRank = 0 },
                                new { title = "第二首电音", artist = "歌手二", cover = "", rank = 2, lastRank = 0 } } },
                        new { id = "801", name = "无副标题歌单", subtitle = "",
                            cover = "https://art.example.test/channel-empty-subtitle.jpg", presentation = "preview",
                            action = new { type = "playlist", id = "801" } },
                        new { id = "radio", name = "电音电台", subtitle = "连续播放",
                            cover = "https://art.example.test/channel-radio.jpg", presentation = "static",
                            action = new { type = "unsupported", id = "radio" } }
                    } }
                })));
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogEntryInitialization;
            var section = f.Module.ChannelSections.Single();
            True(section.IsEntrySection, "channel resource groups use the existing entry presentation");
            var card = section.Items[0];
            True(card.CanRequestPreview && card.CanPlay && card.ActionId == "800",
                "the supplied playlist identity owns preview and playback");
            var loader = new CatalogArtworkLoader();
            ArtworkScope.SetLoader(f.Module.View, loader);
            var window = new Window { Content = f.Module.View, Width = 1108, Height = 760,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain(); window.UpdateLayout();
                var view = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var scroll = (ScrollViewer)view.FindName("CatalogScroll");
                var entries = Descendants<EntryCard>(scroll).ToArray();
                Equal(3, entries.Length);
                foreach (var entry in entries)
                {
                    var item = (MusicCatalogCard)entry.DataContext;
                    Equal(1, Descendants<TextBlock>(entry).Count(text => text.IsVisible && text.Text == item.Title));
                    Equal(item.Cover, ((ArtworkImage)entry.FindName("Cover")).Address);
                }
                await Until(() => loader.Addresses.Count == 3, "channel cover addresses reach the shared artwork loader");
                var preview = entries.Single(entry => ReferenceEquals(entry.DataContext, card));
                True(card.PreviewTitles.SequenceEqual(new[] { "1 第一首电音", "2 第二首电音" }),
                    "the response's ordered song summaries reach the shared card");
                int reads = f.Client.Log.Count;
                preview.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                True(card.PreviewTitles.SequenceEqual(new[] { "1 第一首电音", "2 第二首电音" }),
                    "preloaded previews keep the supplied titles");
                Equal(reads, f.Client.Log.Count);
                var surface = (Grid)preview.FindName("Surface");
                CardMotion.Transition(surface, false, false); await Drain();
                var caption = (TextBlock)preview.FindName("Caption");
                double restingY = caption.TranslatePoint(new Point(), surface).Y;
                CardMotion.Transition(surface, true, false); await Drain();
                True(caption.TranslatePoint(new Point(), surface).Y < restingY,
                    "channel entries lift the same caption together with supplied songs");
                Equal(Visibility.Visible, ((Button)preview.FindName("PlayButton")).Visibility);
                var passive = entries.Single(entry => ((MusicCatalogCard)entry.DataContext).Id == "radio");
                Equal(Visibility.Collapsed, ((Button)passive.FindName("PlayButton")).Visibility);
                Equal(1, f.Client.Count("music.catalog.playlist"));
                Equal("801", f.Client.Last("music.catalog.playlist").GetProperty("id").GetString());
                Equal(0, f.Client.Count("music.queue.replace")); Equal(0, f.Playback.Opens.Count);
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("entry presentation DTO is strict while ordinary cards remain static", async () =>
        {
            var ordinaryReply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "section", title = "普通栏目", type = "playlists", items = new[] {
                    new { id = "1", name = "普通歌单", kind = "playlist", presentation = "preview",
                        action = new { type = "playlist", id = "1" } } } } } });
            Equal(MusicCatalogEntryPresentation.Static,
                MusicCatalogDto.Read(ordinaryReply, "wy", "home", "").Single().Items.Single().Presentation);

            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var nested = f.Module.CatalogSections.Single(section => section.IsEntrySection).Items.Single(card => card.Song is not null);
            Equal(MusicCatalogEntryPresentation.Static, nested.Presentation);
            var song = nested.Song!.Raw;

            void Rejected(JsonElement reply)
            {
                try { _ = MusicCatalogDto.Read(reply, "wy", "home", ""); }
                catch (MusicOperationException error) when (error.Code == "INVALID_RESPONSE") { return; }
                throw new InvalidOperationException("entry presentation contract accepted an invalid DTO");
            }
            Rejected(JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] {
                new { id = "1", name = "缺失展示类型", action = new { type = "playlist", id = "1" } } } }));
            Rejected(JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] {
                new { song, presentation = "Preview" } } }));
            Rejected(JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "entries", type = "entries", items = new[] {
                    new { id = "1", name = "分组缺失展示类型", action = new { type = "playlist", id = "1" } } } } } }));
            Rejected(JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "entries", type = "entries", items = new[] {
                    new { id = "1", name = "分组展示类型错误", presentation = "Preview",
                        action = new { type = "playlist", id = "1" } } } } } }));
            var valid = JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] {
                new { song, presentation = "static" } } });
            Equal(MusicCatalogEntryPresentation.Static,
                MusicCatalogDto.Read(valid, "wy", "home", "").Single().Items.Single().Presentation);
        });
        yield return Case("home nested songs and unavailable actions use actual provider DTO", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var cards = f.Module.CatalogSections.SelectMany(s => s.Items).ToArray();
            var song = cards.Single(c => c.Song is not null);
            Equal("1001", MusicSongFixture.NativeId(song.Song!.Raw));
            True(song.Id != song.ActionId && song.ActionId == "1001", "domain identity not upstream locator");
            var daily = cards.Single(c => c.Kind == "daily");
            True(!daily.CanOpen && !daily.CanPlay, "unavailable content is not fake clickable");
            var count = f.Client.Log.Count;
            var status = f.Module.Status;
            var location = f.Module.CaptureNavigationLocation();
            var statusChanges = 0;
            f.Module.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(f.Module.Status)) statusChanges++; };
            await f.Module.OpenCatalogCardAsync(daily);
            await f.Module.PlayCatalogCardAsync(daily);
            Equal(0, daily.PreviewTitles.Count);
            Equal(count, f.Client.Log.Count);
            Equal(status, f.Module.Status); Equal(0, statusChanges);
            Equal(location, f.Module.CaptureNavigationLocation());
            Equal("playlist", cards.Single(c => c.Kind == "playlist").Kind);
        });
        yield return Case("channel descriptors expose generic placement and preserve native artists and VIP pages", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            var cards = f.Module.ChannelTabs;
            True(cards.All(c => c.Kind == "channel" && c.Placement is "primary" or "more"),
                "native channel descriptors publish only neutral kinds and placements");
            True(cards.Single(c => c.Id == "feature").CanOpen, "featured channel remains available");
            True(cards.Single(c => c.Id == "rock").CanOpen, "rock channel remains available");
            True(cards.Single(c => c.Id == "artist").CanOpen, "artist directory has a native owner");
            True(cards.Single(c => c.Id == "vip").CanOpen, "VIP page has a native owner");
            Equal("feature,playlist,chart,artist,vip,euro,djR,huayu,qingge,acg",
                string.Join(",", f.Module.VisibleChannelTabs.Select(card => card.ActionId)));
            Equal("china,yingshi,rock,music_top_tab_jpop,paobu,music_top_tab_game,yueyu,music_top_tab_sunoai,old_hat,music_top_tab_dance,hFlow,music_top_tab_world,music_top_tab_dj,rnb,chuxing,music_tab_drive,music_top_tab_tipsy,rap,xiangcun,music_top_tab_french,sleep,music_top_tab_light_music,classic,music_top_tab_children,HiFi,sir,ballad,music_top_tab_latin,music_tab_pioneer,funk,landiao,yueqi,study,yinyueju,music_top_tab_sport",
                string.Join(",", f.Module.MoreChannelTabs.Select(card => card.ActionId)));
            await f.Module.OpenCatalogCardAsync(cards.Single(c => c.Id == "rock"));
            Equal("MUSIC_ROCK", f.Client.Last("music.catalog.channel").GetProperty("pageCode").GetString());
            Equal("摇滚", f.Module.Heading); // provider publishes descriptor title, not protocol title
            Equal(2, f.Module.CatalogTags.Count);
            True(f.Module.CatalogSections.SelectMany(s => s.Items).Count(c => c.Song is not null) == 2, "carousel nested song + feed song");
        });
        yield return Case("artist channel preserves three independent filters and combined selection token", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            Equal("歌手", f.Module.Heading);
            Equal("area,type,initial", string.Join(",", f.Module.CatalogTagGroups.Select(group => group.Id)));
            Equal("6,4,28", string.Join(",", f.Module.CatalogTagGroups.Select(group => group.Items.Count)));
            True(f.Module.CatalogTagGroups.All(group => group.Items.Count(tag => tag.Selected) == 1),
                "every artist filter group has one selected option");
            Equal("-1:-1:-1,-1:-1:-1,-1:-1:-1", string.Join(",",
                f.Module.CatalogTagGroups.Select(group => group.Items.Single(tag => tag.Selected).Id)));
            var section = f.Module.CatalogSections.Single();
            True(section.IsArtistSection && section.Items.Count == 40, "artist response owns the artist grid");
            True(section.Items.All(card => card.Kind == "artist" && card.Title.Length > 0),
                "artist cards preserve their native shape");

            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag =>
                tag.Group == "area" && tag.Id == "96:-1:-1"));
            Equal("96:-1:-1", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag =>
                tag.Group == "type" && tag.Id == "96:2:-1"));
            Equal("96:2:-1", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag =>
                tag.Group == "initial" && tag.Id == "96:2:65"));
            Equal("96:2:65", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            True(f.Module.CatalogTagGroups.All(group =>
                group.Items.Single(tag => tag.Selected).Id == "96:2:65"),
                "all three selected options retain the current combined token");
        });
        yield return Case("artist channel continuation appends one bounded native page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { artistPages = 2 });
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            Equal(40, f.Module.CatalogSections.Single().Items.Count);
            True(f.Module.CanCatalogNext, "first artist page exposes its continuation");
            await f.Module.CatalogPageAsync(1);
            Equal(80, f.Module.CatalogSections.Single().Items.Count);
            True(!f.Module.CanCatalogNext, "second artist page terminates pagination");
            var request = f.Client.Last("music.catalog.channel");
            Equal("-1:-1:-1", request.GetProperty("tag").GetString());
            Equal(40, request.GetProperty("continuation").GetProperty("offset").GetInt32());
            Equal("第 2 页", f.Module.CatalogPageText);
        });
        yield return Case("VIP channel publishes native recommendations and only explicit VIP rankings", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "vip"));
            Equal("VIP", f.Module.Heading);
            Equal(2, f.Module.CatalogSections.Count);
            var recommendations = f.Module.CatalogSections[0];
            True(recommendations.IsSongSection, "VIP recommendations use the song presenter");
            Equal("黑胶VIP 每日推荐", recommendations.Title);
            Equal(2, recommendations.Items.Count);
            var rankings = f.Module.CatalogSections[1];
            True(rankings.IsRankingPreviewSection, "VIP charts use the ranking presenter");
            Equal("黑胶VIP 排行榜", rankings.Title);
            Equal("黑胶VIP 热歌榜,黑胶SVIP 飙升榜", string.Join(",", rankings.Items.Select(card => card.Title)));
            True(rankings.Items.All(card => card.PreviewSongs.Count == 1),
                "VIP ranking cards keep their native previews");
        });
        yield return Case("cube tag switch resets cursor and preserves whole feed continuation", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            var first = f.Client.Last("music.catalog.channel");
            True(!first.TryGetProperty("continuation", out _), "tag switches from first page");
            await f.Module.CatalogPageAsync(1);
            var p = f.Client.Last("music.catalog.channel");
            Equal("30", p.GetProperty("tag").GetString());
            var cursor = p.GetProperty("continuation");
            foreach (var key in new[] { "offset", "ruleId", "pointIds", "algTagIds", "tag", "scope", "target" })
                True(cursor.TryGetProperty(key, out _), "whole cursor retains " + key);
            Equal(3, f.Module.CatalogSections.SelectMany(s => s.Items).Count(c => c.Song is not null));
            True(!f.Module.CanCatalogNext, "feed final page");
        });
        yield return Case("failed tag request keeps prior successful tags selection and content", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            var before = f.Module.CatalogSections.ToArray();
            await f.Configure(new { tagFailure = true });
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "31"));
            Equal("30", f.Module.SelectedCatalogTag?.Id);
            True(before.SequenceEqual(f.Module.CatalogSections), "failed filter retains data");
            Equal("平台服务暂不可用", f.Module.Status);
        });
        yield return Case("refresh failure keeps home and sends refresh true first page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            var before = f.Module.CatalogSections.ToArray();
            await f.Configure(new { homeFailure = 503 });
            await f.Module.RefreshAsync(); // exercise owner's public refresh dispatch
            var p = f.Client.Last("music.catalog.home");
            True(p.GetProperty("refresh").GetBoolean(), "refresh propagated");
            True(!p.TryGetProperty("continuation", out _), "refresh resets cursor");
            True(before.SequenceEqual(f.Module.CatalogSections), "failure retains previous successful merged page");
            Equal("第 2 页", f.Module.CatalogPageText);
        });
        yield return Case("playlist next page appends with snapshot and bounded page size then refresh resets", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            Equal(100, f.Module.Songs.Count);
            await f.Module.CatalogPageAsync(1);
            var p = f.Client.Last("music.catalog.playlist");
            Equal(2, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(64, p.GetProperty("snapshot").GetString()!.Length);
            Equal(200, f.Module.Songs.Count);
            Equal("1001", MusicSongFixture.NativeId(f.Module.Songs[0].Raw));
            Equal("1101", MusicSongFixture.NativeId(f.Module.Songs[100].Raw));
            var calls = f.Client.Count("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Configure(new { nextDetail = 0 });
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            Equal(calls + 1, f.Client.Count("music.catalog.playlist"));
            p = f.Client.Last("music.catalog.playlist");
            Equal(1, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            True(p.GetProperty("refresh").GetBoolean(), "returning to a network playlist refreshes its members");
            True(!p.TryGetProperty("snapshot", out _), "returning starts without the old member snapshot");
            Equal(100, f.Module.Songs.Count);
            Equal("第 1 页", f.Module.CatalogPageText);
            True(!f.Module.CanCatalogPrevious, "the accumulated song surface does not expose a page-back control");
            await f.Configure(new { nextDetail = 100 });
            await f.Module.LoadMoreCatalogPlaylistAsync();
            p = f.Client.Last("music.catalog.playlist");
            Equal(2, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(64, p.GetProperty("snapshot").GetString()!.Length);
            Equal(200, f.Module.Songs.Count);
            Equal("1001", MusicSongFixture.NativeId(f.Module.Songs[0].Raw));
            Equal("1101", MusicSongFixture.NativeId(f.Module.Songs[100].Raw));
            True(!f.Module.CanCatalogPrevious, "appending after re-entry does not expose previous");
            // The preceding page may use the song-detail cache instead of advancing this transport fixture.
            await f.Configure(new { nextDetail = 200 });
            await f.Module.LoadMoreCatalogPlaylistAsync();
            Equal(205, f.Module.Songs.Count);
            p = f.Client.Last("music.catalog.playlist");
            Equal(3, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(64, p.GetProperty("snapshot").GetString()!.Length);
            True(!f.Module.CanCatalogPrevious, "the final accumulated page does not expose previous");
            True(!f.Module.CanLoadMoreCatalogPlaylist, "the final page terminates scroll pagination");
            calls = f.Client.Count("music.catalog.playlist");
            await f.Module.LoadMoreCatalogPlaylistAsync();
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            await f.Configure(new { nextDetail = 0 });
            await f.Module.RefreshCatalogAsync();
            p = f.Client.Last("music.catalog.playlist");
            Equal(1, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            True(p.GetProperty("refresh").GetBoolean(), "playlist refresh");
            True(!p.TryGetProperty("snapshot", out _), "fresh first page has no old snapshot");
        });
        yield return Case("snapshot changed response cannot replace current native page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            var first = f.Module.Songs[0];
            f.Client.Transform("music.catalog.playlist", value => Edit(value, node => node["snapshot"] = new string('b', 64)));
            await f.Module.CatalogPageAsync(1);
            True(ReferenceEquals(first, f.Module.Songs[0]), "snapshot changed keeps old data");
            Equal("第 1 页", f.Module.CatalogPageText); Equal("内容已变更，请刷新", f.Module.Status);
        });
        yield return Case("rapid tag A B A suppresses old B receipt and restores A selection", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            var hold = f.Client.Hold("music.catalog.channel");
            var old = f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "31"));
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            hold.Release(); await old;
            Equal("30", f.Module.SelectedCatalogTag?.Id);
            Equal("30", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
        });
        yield return Case("repeated cursor is rejected even if opaque fields differ", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.home", value => value);
            f.Client.Transform("music.catalog.home", value => Edit(value, node => {
                node["more"] = true; node["continuation"] = JsonSerializer.SerializeToNode(new { cursor = "1", scope = "different", target = "wy:home" });
            }));
            await f.Module.NavigateAsync("catalog-home", default);
            var before = f.Module.CatalogSections.ToArray();
            await f.Module.HomeInitialization;
            True(before.SequenceEqual(f.Module.CatalogSections), "stalled page cannot replace old content");
            Equal("分页未前进", f.Module.Status); Equal(2, f.Client.Count("music.catalog.home"));
        });
        yield return Case("home follows continuation through page 101 without scrolling", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { homePages = 101 });
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            Equal("第 101 页", f.Module.CatalogPageText); Equal(101, f.Client.Count("music.catalog.home"));
            True(!f.Module.CanCatalogNext, "native final page stops paging");
            await f.Module.CatalogPageAsync(1); Equal(101, f.Client.Count("music.catalog.home"));
        });
        yield return Case("account retirement keeps home until navigation refreshes and late receipts cannot replace it", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            var home = f.Module.HomeSections.ToArray();
            var sections = f.Module.CatalogSections.ToArray();
            var songs = f.Module.Songs.ToArray();
            True(home.Length > 0, "a successful home establishes the retained display");
            var hold = f.Client.Hold("music.catalog.home");
            var pending = f.Module.RefreshCatalogAsync();
            try
            {
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                int reads = f.Client.Count("music.catalog.home");
                f.Module.RetireCatalogSession();
                await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Drain();
                True(home.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance) &&
                    sections.SequenceEqual(f.Module.CatalogSections, ReferenceEqualityComparer.Instance) &&
                    songs.SequenceEqual(f.Module.Songs, ReferenceEqualityComparer.Instance),
                    "retirement cancels request authority without clearing successful home content");
                Equal(reads, f.Client.Count("music.catalog.home"));

                await f.Configure(new { homePlaylistStart = 700 });
                var refreshHold = f.Client.Hold("music.catalog.home");
                await f.Module.NavigateAsync("catalog-home", default);
                await refreshHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(reads + 1, f.Client.Count("music.catalog.home"));
                var request = f.Client.Last("music.catalog.home");
                True(request.GetProperty("refresh").GetBoolean() && !request.TryGetProperty("continuation", out _),
                    "the next navigation starts a fresh first page rather than resuming retired authority");
                True(home.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance),
                    "home stays visible while the replacement request is pending");
                refreshHold.Release();
                await f.Module.HomeRefresh.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(reads + 2, f.Client.Count("music.catalog.home"));
                var currentHome = f.Module.HomeSections.ToArray();
                var currentSections = f.Module.CatalogSections.ToArray();
                True(currentHome.SelectMany(section => section.Items).Where(card => card.Kind == "playlist")
                    .Select(card => card.ActionId).SequenceEqual(new[] { "700", "701" }),
                    "the successful refresh replaces old playlist identities without retaining the old tail");
                hold.Release();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await Drain();
                True(currentHome.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance) &&
                    currentSections.SequenceEqual(f.Module.CatalogSections, ReferenceEqualityComparer.Instance),
                    "the late cancelled receipt cannot republish old home content");
                Equal(reads + 2, f.Client.Count("music.catalog.home"));
            }
            finally { f.Client.ReleaseAll(); await pending; await f.Module.HomeRefresh; }
        });
        yield return Case("late A after B cannot replace current route", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Client.Hold("music.catalog.home");
            var pending = f.Module.NavigateAsync("catalog-home", default); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("catalog-channels", default);
            hold.Release(); await pending;
            True(f.Module.CurrentChannelKey.Length > 0, "directory resolves to its first openable channel");
            Equal(45, f.Module.ChannelTabs.Count);
        });
        yield return Case("guest home auth failure preserves display without retry until an explicit refresh", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            var home = f.Module.HomeSections.ToArray();
            var sections = f.Module.CatalogSections.ToArray();
            var songs = f.Module.Songs.ToArray();
            True(home.Length > 0, "the auth failure follows a successful home");
            Equal("none", f.Module.Accounts.Single(account => account.Platform == "wy").State);
            int reads = f.Client.Count("music.catalog.home");
            int accountReads = f.Client.Count("music.accounts.list");
            await f.Configure(new { homeFailure = 401 });
            await f.Module.RefreshCatalogAsync();
            await Drain();
            True(home.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance) &&
                sections.SequenceEqual(f.Module.CatalogSections, ReferenceEqualityComparer.Instance) &&
                songs.SequenceEqual(f.Module.Songs, ReferenceEqualityComparer.Instance),
                "an authentication error cannot erase the last successful home");
            Equal("当前内容需要平台登录", f.Module.Status);
            Equal("none", f.Module.Accounts.Single(account => account.Platform == "wy").State);
            Equal(reads + 1, f.Client.Count("music.catalog.home"));
            Equal(accountReads, f.Client.Count("music.accounts.list"));

            await f.Configure(new { homeFailure = 0, homePlaylistStart = 700 });
            var hold = f.Client.Hold("music.catalog.home");
            var retry = f.Module.RefreshCatalogAsync();
            try
            {
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var request = f.Client.Last("music.catalog.home");
                True(request.GetProperty("refresh").GetBoolean() && !request.TryGetProperty("continuation", out _),
                    "explicit recovery still reads the real first page");
                True(home.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance),
                    "the failed refresh did not discard content needed during retry");
                hold.Release();
                await retry;
                await f.Module.HomeInitialization;
                Equal(reads + 3, f.Client.Count("music.catalog.home"));
                True(f.Module.HomeSections.SelectMany(section => section.Items).Where(card => card.Kind == "playlist")
                    .Select(card => card.ActionId).SequenceEqual(new[] { "700", "701" }),
                    "successful recovery replaces old playlist identities and follows continuation");
            }
            finally { hold.Release(); await retry; await f.Module.HomeInitialization; }
        });
        yield return Case("search playlist DTO without kind is actionable and search uses page <=100", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.SearchPolicy("wy", playlists: true);
            await f.Module.SearchAsync("云歌单", default);
            await WaitForSearch(f.Module);
            await f.Module.OpenSearchTabAsync("playlists");
            var card = f.Module.CatalogSections.Single().Items.Single();
            True(card.CanOpen && card.Kind == "playlist" && card.ActionId == "500", "search playlist DTO is not an unknown resource");
            Equal("playlists", f.Client.Last("music.search.start").GetProperty("kind").GetString());
        });
        yield return Case("platform playlist uses the one playlist detail surface", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            True(f.Module.IsContentDetailPage && f.Module.ShowsSongTable && !f.Module.IsCatalogContent,
                "platform playlist leaves the generic catalog surface");
            Equal("完整云歌单", f.Module.Heading);
            Equal("205", f.Module.DetailCountText);
            Equal("https://img.invalid/playlist.jpg", f.Module.DetailArtwork);
            True(f.Module.CanLoadMoreCatalogPlaylist, "playlist continues from the song surface");
            var page = (MusicView)f.Module.View;
            var window = new Window { Content = page, Width = 900, Height = 700, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var grid = (DataGrid)page.FindName("SongsGrid");
                Equal(Visibility.Visible, grid.Visibility);
                True(!Descendants<MusicCatalogView>(page).Any(),
                    "inactive catalog is unmounted, not a second hidden content surface");
                True(((MeowFilm.Desktop.IDesktopHeaderSource)page).Header.Slot is null,
                    "playlist detail does not publish the home-only create control");
                var visibleLabels = Descendants<Button>(page).Where(button => button.IsVisible)
                    .Select(button => AutomationProperties.GetName(button) is { Length: > 0 } name
                        ? name : button.Content?.ToString() ?? "").ToArray();
                True(visibleLabels.Any(label => label.Contains("播放全部", StringComparison.Ordinal)), "playlist primary action");
                True(!visibleLabels.Any(label => label is "上一页" or "下一页"), "playlist has no page-switch toolbar");
                True(!Descendants<TextBlock>(page).Any(text => text.IsVisible &&
                    (text.Text == "评论" || text.Text == "收藏者")),
                    "only the song section is present");
                var bar = Descendants<ScrollBar>(grid).Single(candidate =>
                    candidate.Orientation == Orientation.Vertical && candidate.IsVisible &&
                    candidate.Maximum > candidate.Minimum);
                var scroll = Ancestor<ScrollViewer>(bar) ??
                    throw new InvalidOperationException("playlist scrollbar has no owning scroll viewer");
                scroll.ScrollToEnd(); await Drain(); scroll.UpdateLayout();
                Equal(1, f.Client.Count("music.catalog.playlist"));
                grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                for (var i = 0; i < 200 && (f.Client.Count("music.catalog.playlist") < 2 || f.Module.Busy); i++)
                { await Task.Delay(10); await Drain(); }
                Equal(2, f.Client.Count("music.catalog.playlist"));
                Equal(200, f.Module.Songs.Count);
                await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                window.UpdateLayout(); scroll.ScrollToTop(); await Drain();
                var header = Descendants<DataGridColumnHeader>(grid).Single(candidate => ReferenceEquals(candidate.Column, grid.Columns[0]));
                var resizeThumb = header.Template.FindName("PART_RightHeaderGripper", header) as Thumb;
                True(resizeThumb is not null, "playlist has a real column resize thumb");
                double widthBefore = grid.Columns[0].ActualWidth;
                resizeThumb!.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                resizeThumb.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                scroll.ScrollToEnd(); await Drain();
                resizeThumb.RaiseEvent(new DragCompletedEventArgs(20, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                window.UpdateLayout();
                True(grid.Columns[0].ActualWidth != widthBefore, "routed column drag changes actual column width");
                Equal(2, f.Client.Count("music.catalog.playlist"));
                scroll.ScrollToTop(); await Drain();
                // A different scrollbar routed through the view must not grant paging intent.
                page.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, scroll.ScrollableHeight)
                    { RoutedEvent = ScrollBar.ScrollEvent, Source = new ScrollBar { Orientation = Orientation.Vertical } });
                scroll.ScrollToEnd(); await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                scroll.ScrollToTop(); await Drain();
                bar.RaiseEvent(new ScrollEventArgs(ScrollEventType.EndScroll, scroll.ScrollableHeight) { RoutedEvent = ScrollBar.ScrollEvent });
                scroll.ScrollToEnd(); await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                scroll.ScrollToTop(); await Drain();
                var scrollThumb = Descendants<Thumb>(bar).Single();
                scrollThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                bar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, scroll.ScrollableHeight) { RoutedEvent = ScrollBar.ScrollEvent });
                try
                {
                    scroll.ScrollToEnd();
                    await Until(() => f.Client.Count("music.catalog.playlist") == 3 &&
                        f.Module.Songs.Count == 205 && !f.Module.Busy);
                }
                finally
                {
                    scrollThumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                }
                Equal(3, f.Client.Count("music.catalog.playlist"));
                Equal(205, f.Module.Songs.Count);
                True(!f.Module.CanLoadMoreCatalogPlaylist, "final five songs terminate inline pagination");
                await Drain();
                Equal(3, f.Client.Count("music.catalog.playlist"));
            }
            finally { window.Content = null; window.Close(); }
        });
        foreach (var platform in new[] { "tx", "kg" })
        {
            yield return Case("actual " + platform + " provider song DTO and metadata reference save", async () =>
            {
                await using var f = await Fixture.Create();
                Equal("wy", f.Module.CatalogSourcePlatform);
                await f.Module.NavigateAsync("catalog-home", default);
                await f.SearchPolicy(platform, playlists: false);
                f.Module.SelectedCatalogPlatform = f.Module.CatalogPlatforms.Single(p => p.Key == platform);
                await f.Module.SearchAsync("歌曲", default);
                await WaitForSearch(f.Module);
                var card = f.Module.CatalogSections.SelectMany(section => section.Items).Single(item => item.Song is not null);
                True(card.CanOpen && card.Song is not null, "real provider normalized song is actionable");
                Equal(platform, MusicSongFixture.Info(card.Song!.Raw).GetProperty("source").GetString());
                var id = platform == "kg" ? "global-test" : "500";
                await f.Module.NavigateAsync("catalog-playlist/" + platform + "/" + id, default);
                Equal(platform, f.Module.SelectedCatalogPlatform.Key);
                Equal("wy", f.Module.CatalogSourcePlatform);
                int reads = f.Client.Count("music.catalog.playlist");
                await f.Module.SaveCatalogPlaylistAsync();
                var reference = f.Module.NetworkPlaylists.Single();
                Equal(platform, reference.Platform); Equal(id, reference.RemoteId); Equal(205, reference.Count);
                Equal(0, f.Module.Playlists.Count(p => p.Kind == "playlist"));
                Equal(reads, f.Client.Count("music.catalog.playlist"));
                Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
                await f.Module.NavigateAsync("catalog-home", default);
                Equal("wy", f.Module.CatalogSourcePlatform);
                Equal("wy", f.Module.SelectedCatalogPlatform.Key);
            });
        }
        yield return Case("back restores successful merged view without new requests", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            var before = f.Module.CatalogSections.ToArray(); var calls = f.Client.Count("music.catalog.home");
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.NavigateAsync("catalog-home", default);
            Equal(calls, f.Client.Count("music.catalog.home")); Equal("第 2 页", f.Module.CatalogPageText);
            True(before.SequenceEqual(f.Module.CatalogSections), "same successful state restored");
        });
        yield return Case("native metadata reference save through Native application and DPAPI", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            int reads = f.Client.Count("music.catalog.playlist");
            await f.Module.SaveCatalogPlaylistAsync();
            var list = f.Module.NetworkPlaylists.Single();
            Equal(205, list.Count);
            Equal("wy", list.Platform); Equal("500", list.RemoteId);
            Equal(0, f.Module.Playlists.Count(p => p.Kind == "playlist"));
            Equal(reads, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            True(f.Client.Log.Where(c => c.Method.StartsWith("music.catalog.playlist.", StringComparison.Ordinal))
                .All(c => c.Parameters.GetRawText().Length < 1024), "metadata control only, not oversized song JSON");
            Equal("已收藏歌单", f.Module.Status);
        });
        yield return Case("empty remote list saves a reference and play-all remains disabled", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { total = 0 });
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            True(f.Module.CanCatalogAction && !f.Module.CanPlayCatalogPlaylist, "empty save is allowed, absent playback is not");
            await f.Module.SaveCatalogPlaylistAsync();
            Equal(0, f.Module.NetworkPlaylists.Single().Count);
            Equal(0, f.Module.Playlists.Count(p => p.Kind == "playlist"));
        });
        yield return Case("native full playlist selected song 204 replaces complete queue and resolves chosen recording", async () =>
        {
            await using var f = await Fixture.Create();
            await f.SetupSource();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Module.CatalogPageAsync(1); await f.Module.CatalogPageAsync(1);
            var card = f.Module.CatalogSections.Single().Items[204];
            await f.Configure(new { nextDetail = 0 });
            await f.Module.OpenCatalogCardAsync(card);
            var q = await f.Rpc("music.queue.get", new { limit = 1 });
            Equal(205, q.GetProperty("total").GetInt32());
            Equal("1205", MusicSongFixture.NativeId(q.GetProperty("current").GetProperty("song")));
            Equal(1, f.Playback.Opens.Count);
            Equal("https://media.invalid/1205", f.Playback.Opens.Single().AbsoluteUri);
            Equal(0, f.Client.Count("music.history.recordPlayed"));
        });
        yield return Case("new selection cancels full-list load without queue replacement or auto play", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.SelectedSong = f.Module.Songs[1]; // real public user-intent hook
            hold.Release(); await loading;
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            Equal(0, f.Playback.Opens.Count);
            Equal(0, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
        });
        yield return Case("cancel full-list play before commit creates no partial queue or collection", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.CancelCatalog(); await Task.Delay(35); await Drain();
            True(!loading.IsCompleted, "cancelled prepare abandoned its successful token receipt");
            hold.Release(); await loading;
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            Equal(1, f.Client.Count("music.catalog.playlist.discard"));
            Equal(1, (await f.Rpc("music.library.list", new { })).GetProperty("total").GetInt32());
            Equal(0, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("manual pause intent cancels old playlist load even with no current media", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.TogglePlaybackAsync();
            hold.Release(); await loading;
            Equal(0, f.Client.Count("music.catalog.playlist.commit")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("cancel after admitted playlist commit waits receipt and reconciles without autoplay", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.commit");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.CancelCatalog(); await Task.Delay(35); await Drain();
            True(!loading.IsCompleted, "admitted queue commit was reported cancelled before its receipt settled");
            hold.Release(); await loading;
            Equal(1, f.Client.Count("music.catalog.playlist.commit"));
            Equal(1, f.Client.Count("music.catalog.playlist.discard"));
            Equal(205, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("queue changed during prepare is caught by real management CAS", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A second component calls the native application without a UI intent hook.
            await f.Rpc("music.queue.clear", new { });
            hold.Release(); await loading;
            Equal(1, f.Client.Count("music.catalog.playlist.commit"));
            Equal(0, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
            Equal(0, f.Playback.Opens.Count); Equal("内容已变更，请刷新", f.Module.Status);
        });
        yield return Case("anonymous home hides login-only cards but keeps real public content", async () =>
        {
            const string publicCover = "https://art.example.test/public-playlist.jpg";
            const string privateCover = "https://art.example.test/private-daily.jpg";
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var view = new MusicCatalogView { DataContext = f.Module };
            var loader = new CatalogArtworkLoader();
            ArtworkScope.SetLoader(view, loader);
            var window = new Window { Content = view, Width = 900, Height = 650, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout();
                await Until(() => loader.Addresses.Contains(publicCover), "public home artwork was not requested");
                var buttons = Descendants<Button>(view).ToArray();
                True(!buttons.Any(b => b.Tag is MusicCatalogCard { Kind: "daily" }), "login-only entry not rendered");
                True(!Descendants<TextBlock>(view).Any(t => t.Text == "当前内容需要平台登录"), "no login warning tiles");
                True(f.Module.CatalogSections.SelectMany(s => s.Items).Any(c =>
                    c.Kind == "daily" && c.Cover == privateCover && !c.CanOpen), "capability and action denial remain intact");
                True(buttons.Any(b => b.Tag is MusicCatalogCard { Song: not null } && b.IsEnabled), "nested song actual WPF card enabled");
                True(!loader.Addresses.Contains(privateCover), "hidden account artwork is never requested");
                Equal(0, f.Client.Count("music.catalog.playlist"));
            }
            finally { window.Close(); }
        });
        yield return Case("song template loads its real cover and uses reference typography", async () =>
        {
            const string address = "https://art.example.test/song.jpg";
            var view = new MusicCatalogView();
            var row = new MusicCatalogCard("1", "测试歌曲 Song", "测试歌手 / Artist", address,
                "song", "wy", new SongRow(JsonSerializer.SerializeToElement(new { songId = "1", title = "测试歌曲 Song" })), ActionId: "1");
            var content = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.SongRow")).LoadContent();
            content.DataContext = row; view.Content = content;
            var loader = new CatalogArtworkLoader(); ArtworkScope.SetLoader(view, loader);
            var window = new Window { Content = view, Width = 430, Height = 130, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                Equal(1, loader.Addresses.Count); Equal(address, loader.Addresses[0]);
                var image = Descendants<ArtworkImage>(view).Single();
                Equal(50d, image.ActualWidth); Equal(50d, image.ActualHeight);
                True(Descendants<Image>(image).Single().Source is not null, "real artwork control displays acquired pixels");
                var title = Descendants<TextBlock>(view).Single(t => t.Text == row.Title);
                var artist = Descendants<TextBlock>(view).Single(t => t.Text == row.Subtitle);
                AssertDisplayAutoText([title, artist], "actual catalog song title and artist");
                Equal(SystemFonts.MessageFontFamily, title.FontFamily);
                Equal(14d, title.FontSize); Equal(20d, title.LineHeight);
                Equal(12d, artist.FontSize); Equal(18d, artist.LineHeight); Equal(2d, artist.Margin.Top);
                Equal(84d, content.ActualHeight); Equal(new CornerRadius(12), ((Border)content).CornerRadius);
                Equal(72d, Descendants<StackPanel>(view).Single(p => p.Children.OfType<Button>().Count() == 2)
                    .Children.OfType<Button>().Sum(b => b.Width));
            }
            finally { window.Close(); }
        });
        yield return Case("reference home templates use measured entry playlist and banner geometry", async () =>
        {
            var row = new MusicCatalogCard("1", "入口标题", "真实描述", "", "playlist", "wy", ActionId: "1");
            var view = new MusicCatalogView();
            foreach (var (type, width, height) in new[] { ("Catalog.Entry", 168d, 200d), ("Catalog.PlaylistCard", 140d, 172d), ("Catalog.Banner", 282d, 160d) })
            {
                var card = (FrameworkElement)((DataTemplate)view.FindResource(type)).LoadContent();
                card.DataContext = row; card.Measure(new Size(width, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height)); card.UpdateLayout();
                True(Math.Abs(card.ActualHeight - height) <= 1, type + " reference height " + card.ActualHeight);
            }
            foreach (var (type, expected) in new[] { ("entries", 168.4), ("playlists", 140.333333), ("banner", 423d) })
            {
                var shelf = new Shelf { MinimumCardWidth = 124, DataContext = new MusicCatalogSection("", [row], Type: type) };
                await WithMountedShelf(shelf, 826, () =>
                    True(Math.Abs(shelf.CardWidth - expected) < .01, type + " reference pitch"));
            }
            var hidden = row with
            {
                Id = "private", Available = false, ErrorCode = "AUTH_REQUIRED",
                RequiresAccount = true, Display = false
            };
            var anonymous = new MusicCatalogSection("", [row, hidden], Action: hidden);
            var display = anonymous.DisplayableCards();
            Equal(1, display.Items.Count);
            True(display.Action is null, "a hidden account action is removed from the display projection");
            Equal(2, (anonymous with
            {
                Items = [row, hidden with { Available = true, ErrorCode = "", Display = true }]
            }).DisplayableCards().Items.Count);

            var reply = JsonSerializer.SerializeToElement(new
            {
                platform = "wy",
                sections = new[]
                {
                    new
                    {
                        id = "private-section", title = "账号栏目", type = "playlists",
                        available = false, errorCode = "AUTH_REQUIRED",
                        requiresAccount = true, display = false,
                        action = new { type = "daily" },
                        items = new[] { new { id = "1", name = "公开歌单", kind = "playlist" } }
                    }
                }
            });
            var action = MusicCatalogDto.Read(reply, "wy", "home", "").Single().Action!;
            True(action.RequiresAccount && !action.Display && !action.Available &&
                action.ErrorCode == "AUTH_REQUIRED", "section action display and account admission survive DTO projection");

        });
    }
    private sealed class CatalogArtworkLoader : IArtworkLoader
    {
        internal readonly List<string> Addresses = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        { Addresses.Add(address); return Task.FromResult<IArtworkLease>(new CatalogArtworkLease()); }
    }
    private sealed class CatalogArtworkLease : IArtworkLease
    {
        public BitmapSource Image { get; } = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 90, 120, 180, 255 }, 4);
        public void Dispose() { }
    }
    private static async Task WithMountedShelf(Shelf shelf, double width, Action assertion)
    {
        shelf.Width = width;
        var window = new Window { Content = shelf, Width = width, Height = 300, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); await Drain(); window.UpdateLayout();
            True(shelf.IsLoaded && shelf.IsVisible, "geometry fixture is mounted and visible");
            assertion();
        }
        finally { window.Content = null; window.Close(); }
    }
    private static TestCase Case(string name, Func<Task> body) => new("music-catalog: " + name, () => OnSta(body));
    private static JsonElement Edit(JsonElement value, Action<JsonObject> apply)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject(); apply(node); return JsonSerializer.SerializeToElement(node);
    }
    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Catalog assertion line {line}: expected {expected}, actual {actual}"); }
    private static void True(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static void AssertDisplayAutoText(IEnumerable<TextBlock> nodes, string label)
    {
        var text = nodes.Where(node => !string.IsNullOrWhiteSpace(node.Text)).ToArray();
        True(text.Length > 0, label + " must include actual text nodes");
        foreach (var node in text)
        {
            var mode = TextOptions.GetTextRenderingMode(node);
            True(mode == TextRenderingMode.Auto,
                $"{label}: '{node.Text}' must have effective Auto TextRenderingMode, actual {mode}");
            var formatting = TextOptions.GetTextFormattingMode(node);
            True(formatting == TextFormattingMode.Display,
                $"{label}: '{node.Text}' must have effective Display TextFormattingMode, actual {formatting}");
        }
    }
    private static async Task Until(Func<bool> predicate, string failure = "Catalog UI condition did not settle",
        Func<string>? timeoutSnapshot = null)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8))
                throw new TimeoutException(timeoutSnapshot is null ? failure : failure + ": " + timeoutSnapshot());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!predicate()) await Task.Delay(1);
        }
        await Drain();
    }
    private static async Task Drain() { for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitForSearch(MusicCapability module)
    {
        for (int i = 0; i < 500; i++)
        {
            if (module.CatalogSections.SelectMany(s => s.Items).Any() &&
                !module.SearchSongsLoading && !module.SearchPlaylistsLoading) return;
            await Task.Delay(10); await Drain();
        }
        throw new InvalidOperationException("Native music search did not settle");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T? Ancestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T found) return found;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
    private sealed class Held
    {
        public readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => released.TrySetResult();
        public async Task<JsonElement> Wait(JsonElement value, CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Arrived.TrySetResult(); await released.Task; return value;
        }
    }
    private sealed record Invocation(string Method, JsonElement Parameters);
    private sealed class VipApplication(IMusicApplication application) : MusicApplicationForwarder(application)
    {
        private const long Generation = 7;
        private string bannerType = "PLAYLIST", bannerId = "500";
        private bool signed;
        internal int Reads, Signs;

        public override event Action? VipPageInvalidated;

        public override Task<MusicVipPageSnapshot> ReadVipPageAsync(
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Snapshot());
        }

        public override Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Equal(Generation, expectedGeneration);
            Signs++;
            signed = true;
            return Task.FromResult(new MusicVipSignReceipt(Generation, Sign()));
        }

        internal void SetBanner(string type, string id)
        {
            bannerType = type;
            bannerId = id;
        }

        internal void Invalidate() => VipPageInvalidated?.Invoke();

        private MusicVipPageSnapshot Snapshot()
        {
            var expiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
            return new(Generation,
                new(Generation, "NinonZQ", "https://img.invalid/avatar.jpg",
                    "SVIP · " + DateTimeOffset.FromUnixTimeMilliseconds(expiry)
                        .ToLocalTime().ToString("yyyy.M.d")),
                Sign(),
                new("https://img.invalid/banner.jpg", bannerType, bannerId));
        }

        private MusicVipSignCardData Sign() => new(
            "10月黑胶乐签", "第2天，得3成长值", false, false, signed ? "已打卡" : "打卡",
            "https://img.invalid/sign-bg.jpg",
            [
                new("25日", true, "", 1, false),
                new("26日", true, "", 2, false),
                new("今天", signed, "", signed ? 3 : 0, true)
            ]);
    }

    private sealed class Client(IMusicApplication application) : MusicApplicationForwarder(application)
    {
        public override event Action? LibrarySyncChanged;
        public void NotifyLibrarySync() => LibrarySyncChanged?.Invoke();
        public override event Action<JsonElement>? SearchChanged
        {
            add => Inner.SearchChanged += value;
            remove => Inner.SearchChanged -= value;
        }
        private readonly List<(string Method, Func<JsonElement, CancellationToken, Task<JsonElement>> Apply)> rules = [];
        private readonly List<Held> holds = [];
        public readonly List<Invocation> Log = [];
        public bool FailHistory;
        public Func<CancellationToken, Task>? BeforeNextCommit;
        public int Count(string method) => Log.Count(c => c.Method == method);
        public JsonElement Last(string method) => Log.Last(c => c.Method == method).Parameters;
        public Held Hold(string method)
        {
            var held = new Held(); holds.Add(held); rules.Add((method, held.Wait)); return held;
        }
        public void HoldReply(string method, Held held, Func<JsonElement, JsonElement> change)
        {
            holds.Add(held);
            rules.Add((method, async (value, token) => change(await held.Wait(value, token))));
        }
        public void Transform(string method, Func<JsonElement, JsonElement> change) =>
            rules.Add((method, (value, _) => Task.FromResult(change(value))));
        public void ReleaseAll() { foreach (var h in holds) h.Release(); }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Log.Add(new(method, JsonSerializer.SerializeToElement(parameters ?? new { })));
            if (FailHistory && method == "music.search.history.get")
                return JsonSerializer.SerializeToElement(new { ok = false, code = "STORAGE_ERROR" });
            var index = rules.FindIndex(r => r.Method == method);
            Func<JsonElement, CancellationToken, Task<JsonElement>>? transform = null;
            if (index >= 0) { transform = rules[index].Apply; rules.RemoveAt(index); }
            if (method == "music.catalog.playlist.commit" && BeforeNextCommit is { } before)
            {
                BeforeNextCommit = null;
                await before(cancellationToken);
            }
            var result = await Inner.ExecuteAsync(method, parameters ?? new { }, cancellationToken);
            return transform is null ? result : await transform(result, cancellationToken); // deliberately delayed old receipt
        }
    }
    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 180;
        public int Volume { get; set; } = 80;
        public List<Uri> Opens { get; } = [];
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { token.ThrowIfCancellationRequested(); OwnerId = owner; Identity = identity; HasMedia = true; Opens.Add(uri); return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { HasMedia = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
    private static void OpenContentMenu(FrameworkElement target)
    {
        var request = (ContextMenuEventArgs)typeof(ContextMenuEventArgs)
            .GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                [typeof(object), typeof(bool), typeof(double), typeof(double)], null)!
            .Invoke([target, true, -1d, -1d]);
        target.RaiseEvent(request);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LocalDocumentStore store;
        private readonly string root;
        private readonly NativeMusicScriptTestRuntime runtime = new();
        private readonly CatalogTransport transport = new();
        public Client Client { get; }
        public Output Playback { get; } = new();
        public MusicCapability Module { get; }
        public VipApplication? Vip { get; }
        private Fixture(LocalDocumentStore store, bool vipAccount, string root)
        {
            this.store = store; this.root = root;
            var native = new MusicNativeApplication(store, runtime, transport);
            IMusicApplication application = native;
            if (vipAccount) application = Vip = new(native);
            Client = new(application); Module = new(Client, store, Playback);
        }
        public static async Task<Fixture> Create(bool vipAccount = false)
        {
            var data = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Isolated test data required");
            var dir = Path.Combine(data, "catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var store = new LocalDocumentStore(dir, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            await MusicClientContextFixture.SeedQqAsync(store);
            var f = new Fixture(store, vipAccount, dir);
            try
            {
                await f.Module.InitializeAsync(default);
                Equal("wy", f.Module.CatalogSourcePlatform);
                True(f.Module.CanEditCatalogSource && !f.Module.HasCatalogSourceError, "catalog fixture consumes the source restore contract");
                Equal(1, f.Client.Count("music.catalogSource.settings.get"));
                Equal(0, f.Client.Count("music.catalogSource.settings.save"));
                True(!f.runtime.HasStarted, "ordinary native initialization is script-free");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<JsonElement> Rpc(string method, object parameters)
        {
            var result = await Client.ExecuteAsync(method, parameters);
            True(result.GetProperty("ok").GetBoolean(), "fixture native command failure " + method + ":" + result.GetRawText());
            return result;
        }
        public async Task SearchPolicy(string platform, bool playlists)
        {
            foreach (var row in Module.SongSearch.Platforms.ToArray())
                await Module.SetSearchPlatformEnabledAsync("songs", row, !playlists && row.Platform == platform);
            foreach (var row in Module.PlaylistSearch.Platforms.ToArray())
                await Module.SetSearchPlatformEnabledAsync("playlists", row, playlists && row.Platform == platform);
        }
        public Task Configure(object state)
        {
            foreach (var pair in JsonSerializer.SerializeToNode(state)!.AsObject())
                transport.State[pair.Key] = pair.Value?.DeepClone();
            return Task.CompletedTask;
        }
        public void AssertNoScriptRuntime() => True(!runtime.HasStarted && runtime.Calls.IsEmpty,
            "local media fixture never starts or calls the script runtime");
        public async Task SetupLocalSongs()
        {
            var paths = Enumerable.Range(1, 2).Select(index =>
            {
                var path = Path.Combine(root, "current-media-" + index + ".wav");
                File.WriteAllBytes(path, [0, 1, 2, 3]); // Native grant/resolve; Output does not decode bytes.
                return path;
            }).ToArray();
            var grants = new GrantedMediaFiles(store, "music.files.v1", [".wav"]);
            var files = await grants.GrantAsync(paths);
            var favorite = await Rpc("music.library.detail", new { id = "favorites", limit = 1 });
            await Rpc("music.library.addSongs", new { id = "favorites",
                ifRevision = favorite.GetProperty("collection").GetProperty("revision").GetInt64(), songs = files.Select(file =>
                MusicSongFixture.Local(file.Id, file.Title, "fixture", duration: 180)).ToArray() });
            AssertNoScriptRuntime();
        }
        public async Task SetupSource()
        {
            await Module.ActivateQualitySettingsAsync();
            await Module.SetPlaybackQualityRequestAsync("320k");
            const string source = """
                lx.on(lx.EVENT_NAMES.request, async ({info}) => 'https://media.invalid/' + info.musicInfo.songmid);
                lx.send(lx.EVENT_NAMES.inited,{status:true,sources:{wy:{name:'合成',type:'music',actions:['musicUrl'],qualitys:['320k']}}});
                """;
            var bytes = System.Text.Encoding.UTF8.GetBytes(source);
            var result = await Client.ImportSourceAsync(bytes, "catalog-fixture.js");
            True(result.GetProperty("ok").GetBoolean(), "fixture source import failed: " + result.GetRawText());
            var installationId = result.GetProperty("item").GetProperty("installationId").GetString();
            var enabled = await Rpc("music.sources.enable", new { installationId });
            True(enabled.GetProperty("item").GetProperty("enabled").GetBoolean(), "enabled script participates in catalog playback");
            await Module.RefreshSourceManagementAsync();
        }
        public async ValueTask DisposeAsync()
        {
            Client.ReleaseAll();
            try { await Module.DisposeAsync(); }
            finally
            {
                await runtime.DisposeAsync(); await store.DisposeAsync();
            }
        }
    }
    private sealed class CatalogTransport : HttpMessageHandler
    {
        internal readonly JsonObject State = new();
        private int artistReads;
        private int Number(string key, int fallback = 0) => State[key]?.GetValue<int>() ?? fallback;
        private static object Wy(int id) => new { id = id.ToString(), name = "合成曲 " + id,
            ar = new[] { new { name = "合成艺人" } }, al = new { name = "合成专辑" }, dt = 185000 };
        private static object Tx(int id) => new { mid = "mid" + id, id = id.ToString(), type = 0, title = "合成曲 " + id,
            singer = new[] { new { name = "合成艺人" } }, album = new { name = "合成专辑", mid = "albumM1" }, interval = 185, file = new { } };
        private static object Kg(int id) => new { FileHash = id.ToString("X").PadLeft(32, '0'), FileName = "合成艺人 - 合成曲 " + id,
            SingerName = "合成艺人", AlbumName = "合成专辑", AlbumID = "31", MixSongID = id.ToString(), Duration = 185 };
        private static object Channel(string code, string name, string pageCode = "") => new
        {
            code,
            name,
            url = pageCode.Length == 0 ? "" : "orpheus://rnpage?component=cube-renderer-rn&page=" + pageCode
        };
        private static object[] Channels() =>
        [
            Channel("chart", "排行榜"), Channel("vip", "VIP"), Channel("playlist", "歌单"),
            Channel("huayu", "华语", "MUSIC_HUAYU"), Channel("qingge", "情歌", "MUSIC_QINGGE"),
            Channel("euro", "欧美", "MUSIC_EURO"), Channel("djR", "电音", "MUSIC_DJR"),
            Channel("yingshi", "OST", "MUSIC_OST"), Channel("rap", "说唱", "MUSIC_RAP"),
            Channel("music_top_tab_dj", "慢摇DJ", "MUSIC_SLOW_DJ"), Channel("music_top_tab_sport", "运动", "MUSIC_SPORT"),
            Channel("china", "国风", "MUSIC_CHINA"), Channel("yueyu", "粤语", "MUSIC_CANTONESE"),
            Channel("rnb", "R&B", "MUSIC_RNB"), Channel("ballad", "民谣", "MUSIC_BALLAD"),
            Channel("rock", "摇滚", "MUSIC_ROCK"), Channel("paobu", "跑步", "MUSIC_RUNNING"),
            Channel("music_top_tab_acg", "二次元", "MUSIC_ACG"), Channel("yueqi", "乐器", "MUSIC_INSTRUMENT"),
            Channel("chuxing", "出行", "MUSIC_TRAVEL"), Channel("hFlow", "K-Pop", "MUSIC_KPOP"),
            Channel("music_tab_drive", "驾车", "MUSIC_DRIVE"), Channel("classic", "古典", "MUSIC_CLASSIC"),
            Channel("study", "专注", "MUSIC_STUDY"), Channel("sir", "爵士", "MUSIC_JAZZ"),
            Channel("music_tab_pioneer", "先锋", "MUSIC_PIONEER"), Channel("xiangcun", "乡村乐", "MUSIC_COUNTRY"),
            Channel("funk", "放克", "MUSIC_FUNK"), Channel("landiao", "蓝调", "MUSIC_BLUES"),
            Channel("music_top_tab_children", "儿歌", "MUSIC_CHILDREN"), Channel("music_top_tab_game", "游戏", "MUSIC_GAME"),
            Channel("music_top_tab_light_music", "轻音乐", "MUSIC_LIGHT"), Channel("music_top_tab_jpop", "日语", "MUSIC_JPOP"),
            Channel("HiFi", "高音质", "MUSIC_HIFI"), Channel("music_top_tab_french", "法语", "MUSIC_FRENCH"),
            Channel("music_top_tab_dance", "舞蹈", "MUSIC_DANCE"), Channel("music_top_tab_latin", "拉丁", "MUSIC_LATIN"),
            Channel("music_top_tab_sunoai", "AI", "MUSIC_AI"), Channel("music_top_tab_world", "全球", "MUSIC_WORLD"),
            Channel("yinyueju", "音乐剧", "MUSIC_MUSICAL"), Channel("music_top_tab_tipsy", "放松", "MUSIC_RELAX"),
            Channel("sleep", "助眠", "MUSIC_SLEEP"), Channel("old_hat", "经典", "MUSIC_OLD"),
            Channel("feature", "精选"), Channel("newsong", "新歌"), Channel("origin", "原创", "MUSIC_ORIGIN")
        ];
        private static Dictionary<string, string> Form(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        private static async Task<JsonObject> Eapi(HttpRequestMessage request)
        {
            var form = Form(await request.Content!.ReadAsStringAsync());
            using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
            var parts = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(form["params"]), PaddingMode.PKCS7)).Split("-36cd479b6b5-");
            True(parts.Length == 3 && parts[2] == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("nobody" + parts[0] + "use" + parts[1] + "md5forencrypt"))),
                "production EAPI request checksum");
            return JsonNode.Parse(parts[1])!.AsObject();
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage Json(object body, int status = 200) => new((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            var uri = request.RequestUri!; var path = uri.AbsolutePath; int total = Number("total", 205);
            if (uri.Host == "u.y.qq.com")
            {
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["req_0"]!["param"]!;
                if (payload["query"] is not null) return Json(new { code = 0, req_0 = new { code = 0, data = new {
                    meta = new { sum = 1, nextpage = -1 }, body = new { item_song = new[] { Tx(1001) } } } } });
                int first = payload["song_begin"]!.GetValue<int>(), count = Math.Min(payload["song_num"]!.GetValue<int>(), Math.Max(0, total - first));
                return Json(new { code = 0, req_0 = new { code = 0, data = new {
                    dirinfo = new { tid = payload["disstid"]!.ToString(), title = "完整云歌单" }, songlist_size = count,
                    total_song_num = total, hasmore = first + count < total ? 1 : 0, songlist = Enumerable.Range(1001 + first, count).Select(Tx) } } });
            }
            if (uri.Host == "gateway.kugou.com")
            {
                var p = Form(uri.Query);
                if (path.Contains("/search/", StringComparison.Ordinal)) return Json(new { status = 1, error_code = 0, data = new { total = 1, lists = new[] { Kg(1001) } } });
                int first = int.Parse(p["begin_idx"]), count = Math.Min(int.Parse(p.GetValueOrDefault("pagesize", p.GetValueOrDefault("page_size", "100"))), Math.Max(0, total - first));
                return Json(new { status = 1, error_code = 0, data = new {
                    list_info = new { global_collection_id = p["global_collection_id"], name = "完整云歌单", count = total },
                    songs = Enumerable.Range(1001 + first, count).Select(Kg) } });
            }
            True(uri.Host is "music.163.com" or "interfacepc.music.163.com", "no unregistered upstream");
            if (path.Contains("/playlist/detail", StringComparison.Ordinal))
            {
                if (Number("playlistFailure") != 0) return Json(new { }, Number("playlistFailure"));
                var p = await Eapi(request);
                return Json(new { code = 200, playlist = new { id = p["id"]!.ToString(), name = "完整云歌单",
                    coverImgUrl = "https://img.invalid/playlist.jpg", trackCount = total,
                    trackIds = Enumerable.Range(1001, total).Select(id => new { id = id.ToString() }), updateTime = 1 } });
            }
            if (path.Contains("/song/detail", StringComparison.Ordinal))
            {
                var form = Form(await request.Content!.ReadAsStringAsync());
                True(form["encSecKey"].Length == 256, "production WEAPI key remains encrypted");
                int first = Number("nextDetail"), count = Math.Min(Number("detailLimit", 100), total - first);
                State["nextDetail"] = first + count >= total ? 0 : first + count;
                return Json(new { code = 200, songs = Enumerable.Range(1001 + first, count).Select(Wy) });
            }
            if (path.Contains("/cloudsearch/", StringComparison.Ordinal))
            {
                var p = await Eapi(request);
                int first = p["offset"]?.GetValue<int>() ?? 0;
                int playlistTotal = Number("searchPlaylistTotal", 1);
                int count = Math.Min(p["limit"]?.GetValue<int>() ?? 30, Math.Max(0, playlistTotal - first));
                return Json(new { code = 200, result = new
                {
                    playlistCount = playlistTotal,
                    playlists = Enumerable.Range(500 + first, count)
                        .Select(id => new { id = id.ToString(), name = "完整云歌单",
                            coverImgUrl = "https://art.example.test/search-playlist.jpg", trackCount = 205 })
                } });
            }
            if (path.EndsWith("/position/show/resource", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { generalizedSceneShow = new { generalizedMap = new { nowChannelItems = Channels() } } } });
            if (path.StartsWith("/weapi/v1/artist/", StringComparison.Ordinal) &&
                !path.EndsWith("/list", StringComparison.Ordinal))
            {
                var id = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                return Json(new
                {
                    code = 200,
                    artist = new
                    {
                        id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        name = "合成歌手 " + id,
                        picUrl = "https://img.invalid/artist-" + id + ".jpg"
                    },
                    hotSongs = Enumerable.Range(4101, 3).Select(Wy)
                });
            }
            if (path.EndsWith("/artist/list", StringComparison.Ordinal))
            {
                int page = Interlocked.Increment(ref artistReads) - 1;
                int pages = Math.Max(1, Number("artistPages", 1));
                return Json(new
                {
                    code = 200,
                    artists = Enumerable.Range(1 + page * 40, 40).Select(id => new
                    {
                        id = id.ToString(),
                        name = "合成歌手 " + id,
                        picUrl = "https://img.invalid/artist-" + id + ".jpg",
                        musicSize = 100 + id
                    }),
                    more = page + 1 < pages
                });
            }
            if (path.EndsWith("/vipnewcenter/app/viptab/recommend/song/list", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { title = "黑胶VIP 每日推荐",
                    songInfoList = new[] { Wy(3001), Wy(3002) } } });
            if (path.EndsWith("/toplist/detail/v2", StringComparison.Ordinal))
                return Json(new { code = 200, data = new[] { new { name = "会员榜", categoryCode = "vip",
                    frontDisplayType = "ranked-songs", list = new object[] {
                        new { targetType = "PLAYLIST", resourceId = "700", name = "黑胶VIP 热歌榜", canPlay = true,
                            rankItems = new[] { new { trackId = "3001", songName = "会员歌曲", artistName = "会员歌手", rank = 1, lastRank = 2 } } },
                        new { targetType = "PLAYLIST", resourceId = "701", name = "黑胶SVIP 飙升榜", canPlay = true,
                            rankItems = new[] { new { trackId = "3002", songName = "会员新歌", artistName = "会员歌手", rank = 1, lastRank = 0 } } },
                        new { targetType = "PLAYLIST", resourceId = "702", name = "普通热歌榜", canPlay = true,
                            rankItems = new[] { new { trackId = "1001", songName = "普通歌曲", artistName = "普通歌手", rank = 1, lastRank = 1 } } }
                    } } } });
            if (path.EndsWith("/cube/render/page/protocol", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { pageProtocol = new { title = "摇滚频道", pages = new object[] {
                    new { id = "feed", componentName = "Container", props = new { songList = new { ruleId = "11", pointId = new[] { new { id = "22" } }, title = "频道歌曲" } } },
                    new { id = "banner", componentName = "Container", props = new { carousel = new[] { new { title = "嵌套歌曲", action = "orpheus://song/1001" } } } } } } } });
            if (path.EndsWith("/tag/tab/tags", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { algTagInfos = new[] { new { algTagId = "30", algTagName = "风格一" }, new { algTagId = "31", algTagName = "风格二" } } } });
            if (path.EndsWith("/tag/tab/alg/recommends", StringComparison.Ordinal))
            {
                var p = await Eapi(request); int offset = p["offset"]!.GetValue<int>();
                var tag = p["algTagIds"]?.ToString() ?? "";
                if (State["tagFailure"]?.GetValue<bool>() == true && tag == "31") return Json(new { }, 503);
                return Json(new { code = 200, songs = new[] { Wy(2001 + offset + (int.TryParse(tag, out int n) ? n : 0)) }, hasMore = offset == 0 });
            }
            if (path.EndsWith("/rcmd/resource/show", StringComparison.Ordinal))
            {
                if (Number("homeFailure") != 0) return Json(new { }, Number("homeFailure"));
                var p = await Eapi(request); int cursor = p["cursor"]?.GetValue<int>() ?? 0;
                var entries = new List<object>();
                if (Number("detailLimit") > 0)
                    entries.Add(new { resourceType = "playlist", resourceId = (600 + cursor).ToString(), name = "入口歌单" });
                entries.Add(new { resourceType = "dailysongs", name = "每日推荐",
                    coverImg = "https://art.example.test/private-daily.jpg" });
                entries.Add(new { resourceType = "song", resourceExtInfo = new { songData = Wy(1001 + cursor) }, name = "嵌套歌曲" });
                return Json(new { code = 200, data = new { cursor = (cursor + 1).ToString(), hasMore = cursor < Number("homePages", 2) - 1,
                    blockCodeOrderList = "[\"PLAYLIST\"]", blocks = new object[] {
                        new { positionCode = "PLAYLIST", uiElement = new { subTitle = new { title = "推荐歌单" } },
                            creatives = new[] { new { resources = new[] { new { resourceType = "playlist", resourceId = (Number("homePlaylistStart", 500) + cursor).ToString(),
                                name = "合成歌单", coverImg = "https://art.example.test/public-playlist.jpg" } } } } },
                        new { positionCode = "ENTRANCE", creatives = new[] { new { resources = entries.ToArray() } } } } } });
            }
            throw new InvalidOperationException("Unexpected synthetic catalog request");
        }
    }
    private static async Task OnSta(Func<Task> action)
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? error = null;
                try { await action(); await Drain(); } catch (Exception e) { error = e; }
                finally { completed.TrySetResult(error); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "music-catalog-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var failure = await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
