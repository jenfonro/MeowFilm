using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using FontWeights = Microsoft.UI.Text.FontWeights;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Platform.Storage;
using MeowFilm.Platform.Files;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowFilm.Windows.Tests;

// Real WinUI + native application/catalog + DPAPI. The shared canonical runtime
// peer supplies platform results; only explicit authored LX starts a script runtime.
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
            using var source = new DesktopTestUi.Host(new Grid(), 700, 400);
            source.Mount(view);
            foreach (var layout in new[] { MusicCatalogCardLayout.Playlist, MusicCatalogCardLayout.Row })
            {
                var host = new MusicCatalogCardView { Presentation = layout };
                view.Content = host; host.DataContext = first;
                view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400));
                view.UpdateLayout(); await Drain();
                True(host.IsLoaded && DesktopVisual.IsVisible(host), "playlist menu target is mounted and visible");
                int calls = f.Client.Log.Count;
                var menu = OpenContentMenu(host); await Until(() => menu.IsOpen);
                True(menu.Items.Select(MenuLabel).SequenceEqual(
                    new[] { "查看歌单", "播放", "下一首播放", "收藏" }), "four business actions only");
                Equal(calls, f.Client.Log.Count);
                var stale = menu.Items[3]; menu.Hide();
                host.DataContext = second;
                MusicTestUi.Invoke(stale); await Drain();
                Equal(calls, f.Client.Log.Count);
                menu = OpenContentMenu(host); await Until(() => menu.IsOpen);
                int saves = f.Client.Count("music.catalog.playlist.save");
                MusicTestUi.Invoke(menu.Items[3]);
                await Until(() => f.Client.Count("music.catalog.playlist.save") > saves && !second.IsPreparing &&
                    f.Module.NetworkPlaylists.Any(p => p.Platform == second.Platform && p.RemoteId == second.ActionId));
                var saved = f.Client.Last("music.catalog.playlist.save");
                Equal(second.ActionId, saved.GetProperty("id").GetString());
                menu.Hide(); menu = OpenContentMenu(host); await Until(() => menu.IsOpen);
                Equal("取消收藏", MenuLabel(menu.Items[3]));
                int removes = f.Client.Count("music.library.network.remove");
                MusicTestUi.Invoke(menu.Items[3]);
                await Until(() => f.Client.Count("music.library.network.remove") == removes + 1 && !f.Module.Busy &&
                    !f.Module.NetworkPlaylists.Any(p => p.Platform == second.Platform && p.RemoteId == second.ActionId));
                menu.Hide(); menu = OpenContentMenu(host); await Until(() => menu.IsOpen);
                stale = menu.Items[2]; menu.Hide();
                await f.Module.NavigateAsync("favorites", default);
                calls = f.Client.Log.Count;
                MusicTestUi.Invoke(stale); await Drain();
                Equal(calls, f.Client.Log.Count);
                await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                first = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
                second = f.Module.CatalogSections.SelectMany(s => s.Items).Last(c => c.Kind == "playlist");
            }
            var entry = new EntryCard();
            True(entry.ContextFlyout is null, "header entries do not gain content menus");
        });
        yield return Case("catalog song menu captures its row and keeps section playback and discovery actions", async () =>
        {
            await using var f = await Fixture.Create(); await f.SetupSource();
            var songs = (await f.Rpc("music.catalog.playlist",
                new { platform = "wy", id = "500", page = 1, limit = 3 })).GetProperty("items");
            f.Client.Transform("music.catalog.home", reply => Edit(reply, page =>
                page["sections"]!.AsArray().Add(JsonSerializer.SerializeToNode(new {
                    id = "menu-song-section", title = "菜单歌曲栏目", layout = "song-list", items = songs.EnumerateArray().Select(song => new {
                        kind = "song", title = MusicCatalogDto.Text(song, "title"), subtitle = MusicCatalogDto.Text(song, "artist"),
                        cover = MusicCatalogDto.Text(song, "cover_url"), song, action = new { type = "song", id = SongJson.NativeId(song) }
                    }).ToArray()
                }))));
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            await f.Module.CatalogEntryInitialization;
            var cards = f.Module.CatalogSections.Single(section => section.Id == "menu-song-section").Items.ToArray();
            Equal(3, cards.Length);
            True(cards.All(item => item.Song is { Id.Length: > 0 }), "menu songs retain actual repository identities");
            var card = cards[1]; f.Module.SelectedSong = cards[0].Song;
            var view = new MusicCatalogView { DataContext = f.Module };
            var row = new MusicCatalogCardView { Presentation = MusicCatalogCardLayout.Song };
            view.Content = row; row.DataContext = card;
            using var source = new DesktopTestUi.Host(new Grid(), 700, 400);
            source.Mount(view);
            view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400));
            view.UpdateLayout(); await Drain();
            True(row.IsLoaded && DesktopVisual.IsVisible(row), "song menu target is mounted and visible");
            int calls = f.Client.Log.Count;
            var menu = OpenContentMenu(row); await Until(() => menu.IsOpen);
            Equal(calls, f.Client.Log.Count);
            True(menu.Items.Where(item => item is not MenuFlyoutSeparator).Select(MenuLabel).SequenceEqual(new[]
                { "播放", "下一首播放", "收藏", "添加到歌单", "相似歌曲", "专辑" }),
                "catalog rows share all search actions and preserve their discovery extensions");
            True(!menu.Items.Any(i => MenuLabel(i) == "心动推荐"),
                "an ordinary home song row is not an eligible heartbeat context");
            var similar = menu.Items.Single(i => MenuLabel(i) == "相似歌曲");
            True(similar.IsEnabled, "an identified home song keeps its valid recommendation action");
            True(!menu.Items.Any(i => MenuLabel(i) == "移除"),
                "catalog content does not expose collection member removal");
            menu.Hide(); row.DataContext = cards[0]; await Drain();
            calls = f.Client.Log.Count;
            MusicTestUi.Invoke(similar); await Drain();
            Equal(calls, f.Client.Log.Count);
            Equal(0, f.Playback.Opens.Count);
            row.DataContext = card; await Drain(); menu = OpenContentMenu(row); await Until(() => menu.IsOpen);
            MusicTestUi.Invoke(menu.Items[2]);
            await Until(() => !f.Module.Busy && f.Client.Count("music.library.addSongs") == 1, "song.favorite");
            var favorite = await f.Rpc("music.library.detail", new { id = "favorites" });
            Equal(card.Song!.Id, SongRow.Read(favorite.GetProperty("songs")[0]).Id);
            menu.Hide(); menu = OpenContentMenu(row); await Until(() => menu.IsOpen);
            MusicTestUi.Invoke(menu.Items[0]);
            await Until(() => f.Playback.Opens.Count == 1 && !f.Module.Busy && !card.IsPreparing, "song.play");
            var replace = f.Client.Log.Last(c => c.Method == "music.queue.replace").Parameters;
            Equal(cards.Length, replace.GetProperty("songs").GetArrayLength());
            Equal(1, replace.GetProperty("index").GetInt32());
            menu.Hide(); menu = OpenContentMenu(row); await Until(() => menu.IsOpen);
            similar = menu.Items.Single(i => MenuLabel(i) == "相似歌曲");
            True(similar.IsEnabled, "the valid recommendation remains available before leaving its source");
            menu.Hide(); await f.Module.NavigateAsync("favorites", default);
            calls = f.Client.Log.Count;
            MusicTestUi.Invoke(similar); await Drain();
            Equal(calls, f.Client.Log.Count);
            Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("channel ordinary playlist cards expose four actions without adding tab menus", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channel", reply => Edit(reply, node =>
                node["sections"] = JsonSerializer.SerializeToNode(new[] {
                    new { id = "menu-playlists", title = "频道歌单", layout = "playlist-rail", items = new[] {
                        new { id = "500", title = "频道歌单A", kind = "playlist", action = new { type = "playlist", id = "500" } },
                        new { id = "501", title = "频道歌单B", kind = "playlist", action = new { type = "playlist", id = "501" } }
                    } } })));
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            using var source = new DesktopTestUi.Host(new Grid(), 1100, 760);
            source.Mount(f.Module.View);
            f.Module.View.Measure(new(1100, 760)); f.Module.View.Arrange(new Rect(0, 0, 1100, 760));
            f.Module.View.UpdateLayout(); await Drain();
            var second = f.Module.ChannelSections.Single().Items[1];
            var host = Descendants<MusicCatalogCardView>(f.Module.View).Single(card => ReferenceEquals(card.DataContext, second) &&
                DesktopVisual.IsVisible(card) && card.IsEnabled && card.IsHitTestVisible);
            int reads = f.Client.Log.Count;
            var menu = OpenContentMenu(host); await Until(() => menu.IsOpen);
            Equal(reads, f.Client.Log.Count);
            True(menu.Items.Select(MenuLabel).SequenceEqual(
                new[] { "查看歌单", "播放", "下一首播放", "收藏" }), "ordinary channel playlist has four content actions");
            var routes = new List<string>(); f.Module.NavigationRequested += (_, e) => routes.Add(e.Route);
            MusicTestUi.Invoke(menu.Items[0]);
            await Until(() => routes.Count == 1); Equal("catalog-playlist/wy/501", routes.Single());
            True(Descendants<Button>(f.Module.View).Where(button => button.Tag is MusicCatalogCard card &&
                f.Module.ChannelTabs.Any(tab => ReferenceEquals(tab, card))).All(button => button.ContextFlyout is null),
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
            using var source = new DesktopTestUi.Host(view, 700, 400);
            foreach (bool songMenu in new[] { true, false })
            {
                var card = new MusicCatalogCardView { Presentation = songMenu ? MusicCatalogCardLayout.Song : MusicCatalogCardLayout.Playlist,
                    DataContext = songMenu ? song : playlist };
                view.Content = card; view.UpdateLayout(); await Drain();
                MenuFlyout menu;
                if (songMenu)
                {
                    var more = Descendants<Button>(card).Single(b => AutomationProperties.GetName(b) == "歌曲操作");
                    True(more.Focus(FocusState.Programmatic), "the actual native more control accepts focus");
                    True(DesktopTestUi.HasFocus(card), "focus belongs to the mounted song row");
                    DesktopTestUi.Invoke(more);
                    menu = (MenuFlyout)typeof(MusicCatalogCardView).GetField("menu", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)!.GetValue(card)!;
                }
                else menu = OpenContentMenu(card);
                try
                {
                    await Until(() => menu.IsOpen); await Drain();
                    var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(card.XamlRoot).SelectMany(popup =>
                        popup.Child is DependencyObject child ? Descendants<DependencyObject>(child) : [])
                        .OfType<MenuFlyoutPresenter>().Single();
                    True(ReferenceEquals(presenter.Style, DesktopVisual.Resource(card, "MeowFilm.ContextMenu")) && presenter.Template is not null,
                        "the opened native presenter uses the shared host style and a real template");
                    var itemStyle = DesktopVisual.Resource(card, "MeowFilm.MenuItem");
                    foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
                        True(ReferenceEquals(item.Style, itemStyle) && item.FontWeight == FontWeights.Normal,
                            "actual native actions consume the shared regular-text style");
                    if (!songMenu) continue;
                    var destinations = menu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "添加到歌单");
                    True(destinations.Items.Count > 0, "dynamic playlist style coverage is not vacuous");
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(destinations)!;
                    var expansion = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
                    expansion.Expand(); await Drain();
                    foreach (var child in destinations.Items.OfType<MenuFlyoutItem>())
                        True(child.IsLoaded && ReferenceEquals(child.Style, itemStyle) && child.Template is not null && child.FontWeight == FontWeights.Normal,
                            "opened dynamic destination actions use the same shared native style");
                    expansion.Collapse();
                }
                finally { menu.Hide(); await Until(() => !menu.IsOpen); }
            }
        });
        yield return Case("cancel closing leaves mounted hidden settings and discovery inactive", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.FailHistory = true; // Keep activation reads observable instead of satisfying them from history cache.
            var host = new Grid(); host.Children.Add(f.Module.View); host.Children.Add(f.Module.SettingsView);
            var window = DesktopTestUi.CreateWindow(host, 906, 650);
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
            var window = DesktopTestUi.CreateWindow(f.Module.View, 906, 650);
            try
            {
                window.Show();
                await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => Descendants<Image>(f.Module.View).Any(image => image.Source is not null), "home pixels load");
                Equal(0, f.Client.Count("music.catalog.channels"));
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var scroll = (ScrollViewer)catalog.FindName("HomeScroll");
                scroll.ChangeView(null, Math.Min(100, scroll.ScrollableHeight), null, true); await Drain();
                double offset = scroll.VerticalOffset;
                var controls = Descendants<ArtworkImage>(scroll).ToArray();
                var images = Descendants<Image>(scroll).Where(image => image.Source is not null)
                    .Select(image => (Control: image, Pixels: image.Source)).ToArray();
                True(images.Length > 0, "real decoded images establish the retention assertion");
                string homeLocation = f.Module.CaptureNavigationLocation();
                await f.Module.NavigateAsync("catalog-channels", default); await Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
                True(controls.All(control => !DesktopVisual.IsVisible(control)), "home remains mounted but inactive");
                True(images.All(image => ReferenceEquals(image.Pixels, image.Control.Source)), "hiding preserves decoded pixels");
                int reads = loader.Addresses.Count;
                await f.Module.RestoreNavigationAsync(homeLocation, default); await Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
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
            var window = DesktopTestUi.CreateWindow(f.Module.View, 906, 900);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
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
                    await f.Module.NavigateAsync(route, default); await Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
                    AssertRetained();
                    await f.Module.RestoreNavigationAsync(position, default); await Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
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
            var window = DesktopTestUi.CreateWindow(f.Module.View, 906, 650);
            var hold = f.Client.Hold("music.catalog.home");
            try
            {
                window.Show();
                var loading = f.Module.NavigateAsync("catalog-home", default);
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                True(f.Module.ShowCatalogSkeleton, "initial empty home has a loading skeleton");
                True(Descendants<LoadingSkeleton>(f.Module.View).Any(skeleton => DesktopVisual.IsVisible(skeleton)), "skeleton is in the visible tree");
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
            CardMotion.SetPointerActive(previewEntry, true);
            Equal(0, previewOpens);
            var previewHost = DesktopTestUi.CreateWindow(previewEntry, 168, 230);
            try
            {
                previewHost.Show(); DesktopTestUi.WindowRoot(previewHost).UpdateLayout(); await Drain();
                CardMotion.SetPointerActive(previewEntry, true);
                Equal(0, previewOpens);
                foreach (var passive in new[] { overlay, animated, card })
                {
                    var passiveEntry = new EntryCard { DataContext = passive };
                    var requests = 0; passiveEntry.OpenRequested += (_, _) => requests++;
                    previewHost.Content = passiveEntry; DesktopTestUi.WindowRoot(previewHost).UpdateLayout(); await Drain();
                    CardMotion.SetPointerActive(passiveEntry, true);
                    Equal(0, requests);
                }
            }
            finally { previewHost.Content = null; previewHost.Close(); }
            var entry = new EntryCard { DataContext = card };
            var window = DesktopTestUi.CreateWindow(entry, 152, 200);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var surface = (Grid)entry.FindName("Surface");
                True(entry.IsEnabled && surface.IsHitTestVisible, "unsupported action must not disable the visual hover surface");
                True(!((Button)entry.FindName("OpenButton")).IsEnabled && !((Button)entry.FindName("PlayButton")).IsEnabled,
                    "unsupported open and play remain denied; artwork retry is a separate control");
                CardMotion.SetPointerActive(entry, true);
                Equal(0, ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count);
                CardMotion.SetPointerActive(surface, true); await Drain();
                var caption = (FrameworkElement)entry.FindName("ContentBlock");
                Equal(0d, ((TranslateTransform)caption.RenderTransform).Y);
                CardMotion.SetPointerActive(surface, false); await Drain();
                Equal(0d, ((TranslateTransform)caption.RenderTransform).Y);
                Equal(Visibility.Collapsed, ((Button)entry.FindName("PlayButton")).Visibility);
            }
            finally { window.Close(); }
        });
        yield return Case("entry palette conversion uses decoded artwork without changing shared image tones", () =>
        {

            var tones = new List<Color>();
            foreach (var color in new[] { Color.FromArgb(255, 83, 74, 85), Color.FromArgb(255, 169, 149, 142), Colors.White })
            {
                var image = MusicEntryPaletteTests.Image((x, y) => color);
                var raw = (SolidColorBrush)ArtworkImage.SamplePalette(new ArtworkPixels(image.PixelWidth, image.PixelHeight,
                    MusicTestUi.Pixels((WriteableBitmap)image))).Raw;
                var brush = (SolidColorBrush)MusicEntryPalette.Sample(image);

                Equal(color, raw.Color);
                Equal(MusicEntryPalette.Sample(image).Color, brush.Color);
                var preview = MusicEntryPalette.Sample(image, preview: true);
                Equal(MusicEntryPalette.Sample(image, preview: true).Color, preview.Color);
                tones.Add(brush.Color);
            }
            True(tones[2].G <= 96 && tones[2].G > tones[0].G && tones[1].R > tones[1].B,
                "standard entries obey the shared luminance ceiling while retaining the artwork color family");
            return Task.CompletedTask;
        });
        yield return Case("home headings use system typography and unclipped refresh without extra options", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var view = new MusicCatalogView { DataContext = f.Module, Width = 900, Height = 700 };
            var window = DesktopTestUi.CreateWindow(view, 900, 700);
            try
            {
                window.Show();
                view.Measure(new(900, 700)); view.Arrange(new Rect(0, 0, 900, 700)); view.UpdateLayout(); await Drain();
                var buttons = Descendants<Button>(view).Where(b => DesktopVisual.IsVisible(b)).ToArray();
                var channelMore = (Flyout)view.FindName("ChannelMorePopup");
                Equal(FlyoutPlacementMode.Bottom, channelMore.Placement);
                Equal(540d, ((ScrollViewer)channelMore.Content).Width);
                True(((ScrollViewer)channelMore.Content).MaxHeight == 308,
                    "more channels use the bounded native flyout scroller");
                var refreshes = buttons.Where(b => AutomationProperties.GetName(b) == "刷新推荐").ToArray();
                True(refreshes.Length > 0, "real headings have refresh controls");
                foreach (var refresh in refreshes)
                {
                    Equal(24d, refresh.ActualHeight); Equal(0d, refresh.MinHeight);
                    Equal(Color.FromArgb(255, 139,144,150), ((SolidColorBrush)refresh.Foreground).Color);
                    True(Descendants<Microsoft.UI.Xaml.Shapes.Path>(refresh).Any(), "vector refresh, not clipped font glyph");
                    var canvas = Descendants<Canvas>(refresh).Single();
                    True(canvas.TransformToVisual(refresh).TransformPoint(new Point()).Y >= 0 && canvas.ActualHeight <= refresh.ActualHeight,
                        "complete icon fits within actual button bounds");
                }
                var body = new FontFamily("XamlAutoFontFamily");
                var title = Descendants<TextBlock>(view).First(text => DesktopVisual.IsVisible(text) &&
                    f.Module.HomeSections.Any(section => section.Title == text.Text));
                Equal(body, title.FontFamily); Equal(18d, title.FontSize);
                Equal(FontWeights.Normal, title.FontWeight);
                var playlist = Descendants<MusicCatalogCardView>(view).Where(card => card.Presentation == MusicCatalogCardLayout.Playlist)
                    .SelectMany(Descendants<TextBlock>).First(text => DesktopVisual.IsVisible(text) && text.FontSize == 13);
                Equal(body, playlist.FontFamily); Equal(13d, playlist.FontSize);
                Equal(FontWeights.Normal, playlist.FontWeight);
                Equal(TextTrimming.CharacterEllipsis, playlist.TextTrimming);
                var catalogText = Descendants<TextBlock>(view).ToArray();
                AssertDisplayAutoText(catalogText, "actual catalog headings and captions");

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
            var window = DesktopTestUi.CreateWindow(view, 240, 300);
            void Layout()
            { view.Measure(new(240, 300)); view.Arrange(new Rect(0, 0, 240, 300)); view.UpdateLayout(); }
            try
            {
                window.Show(); Layout(); await Drain();




                Equal(new FontFamily("XamlAutoFontFamily"), view.FontFamily);
                Equal(new FontFamily("XamlAutoFontFamily"), entry.FontFamily);
                True(view.UseLayoutRounding && entry.UseLayoutRounding,
                    "text and cover layout align to the current display pixels");
                var systemFont = new FontFamily("XamlAutoFontFamily");

                await Drain();
                Equal(systemFont, view.FontFamily); Equal(systemFont, entry.FontFamily);
                var surface = (Grid)entry.FindName("Surface");
                var caption = (TextBlock)entry.FindName("Caption");
                var subtitle = caption;
                Equal(systemFont, subtitle.FontFamily);
                var entryText = Descendants<TextBlock>(entry).ToArray();
                Equal(3, ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count);
                True(((MusicCatalogCard)entry.DataContext).PreviewTitles.All(preview => entryText.Any(text => text.Text == preview)),
                    "entry typography covers the actual generated preview text");
                var open = (Button)entry.FindName("OpenButton");
                True(open.UseSystemFocusVisuals && open.Focus(FocusState.Programmatic),
                    "entry actions keep native keyboard focus visuals");
                foreach (var width in new[] { 152.4, 180.8, 200.6 })
                {
                    entry.Width = width;
                    Layout(); await Drain();
                    CardMotion.SetPointerActive(surface, false);
                    await Until(() => !CardMotion.HasActiveAnimation(surface));
                    AssertDisplayAutoText(Descendants<TextBlock>(entry), "resting entry title caption and preview");
                    Equal(0d, ((TranslateTransform)((FrameworkElement)entry.FindName("ContentBlock")).RenderTransform).Y);
                    var y = caption.TransformToVisual(surface).TransformPoint(new Point()).Y;
                    var pixelY = y * surface.XamlRoot.RasterizationScale;
                    True(Math.Abs(pixelY - Math.Round(pixelY)) < .01, "caption baseline starts on a display pixel");
                    var cover = (ArtworkImage)entry.FindName("Cover");
                    True(y >= cover.ActualHeight && y + subtitle.ActualHeight <= surface.ActualHeight,
                        "the resting caption fits in its footer after resize");
                    var clip = RoundedClip(surface);
                    True(clip.CornerRadius.X > 0 && clip.CornerRadius.Y > 0, "the complete entry has rounded corners");
                    Equal((float)surface.ActualWidth, clip.Size.X); Equal((float)surface.ActualHeight, clip.Size.Y);
                    CardMotion.SetPointerActive(surface, true);
                    await Until(() => !CardMotion.HasActiveAnimation(surface));
                    AssertDisplayAutoText(Descendants<TextBlock>(entry), "raised entry title caption and preview");
                    True(caption.TransformToVisual(surface).TransformPoint(new Point()).Y < y,
                        "the caption rises with its preview instead of remaining underneath it");
                    var raisedY = caption.TransformToVisual(surface).TransformPoint(new Point()).Y * surface.XamlRoot.RasterizationScale;
                    True(Math.Abs(raisedY - Math.Round(raisedY)) < .01, "settled preview also starts on a display pixel");
                    var previewBlock = (FrameworkElement)entry.FindName("PreviewBlock");
                    var previewBottom = previewBlock.TransformToVisual(surface).TransformPoint(new Point(0, previewBlock.ActualHeight)).Y;
                    True(previewBottom <= surface.ActualHeight && previewBottom > 0,
                        "all preview rows remain inside the actual entry clip after expansion");
                }
            }
            finally { window.Content = null; window.Close(); await Drain(); }
        });
        yield return Case("catalog system typography uses Display Auto text and paints neutral glyphs at bitmap DPI", async () =>
        {
            var text = new TextBlock { Text = "二次元赛高", Foreground = new SolidColorBrush(Colors.Black),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var host = new Grid { Background = new SolidColorBrush(Colors.White) }; host.Children.Add(text);
            using var window = new DesktopTestUi.Host(host, 200, 52); await Drain();
            var seek = new SeekBar();
            var preview = (ToolTip)ToolTipService.GetToolTip(seek);
            Equal("XamlAutoFontFamily", preview.FontFamily.Source); Equal(FontWeights.Normal, preview.FontWeight);
            foreach (var size in new[] { 18d, 14d, 13d })
            {
                text.FontSize = size; host.UpdateLayout(); await Drain();
                Equal("XamlAutoFontFamily", text.FontFamily.Source); AssertDisplayAutoText([text], "native system text " + size);
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                {
                    var frame = await DesktopTestUi.Render(host, scale); int ink = 0;
                    for (var i = 0; i < frame.Pixels.Length; i += 4)
                    {
                        var bytes = frame.Pixels;
                        True(Math.Abs(bytes[i] - bytes[i + 1]) <= 1 && Math.Abs(bytes[i + 1] - bytes[i + 2]) <= 1,
                            "native neutral glyph rasters keep matching RGB channels");
                        if (bytes[i + 3] > 240 && bytes[i] < 200) ink++;
                    }
                    True(ink > 50, "the real native raster includes painted system glyphs at every DPI");
                }
            }
        });
        yield return Case("home controls execute through native automation on their retained shell", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3, total = 3, homePages = 2 });
            await f.SetupSource(); await f.SearchPolicy("wy", playlists: true); f.Module.CatalogSearchKind = "歌单";
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask); shell.PrepareForRender();
            shell.Resize(1110, 800); shell.Show();
            var root = DesktopTestUi.WindowRoot(shell);
            void Layout() => root.UpdateLayout();
            void Invoke(Button button) { True(button.IsEnabled, "native automation invokes only enabled controls"); DesktopTestUi.Invoke(button); }
            try
            {
                await Until(() => f.Module.IsCatalogHome && f.Client.Count("music.catalog.home") >= 1 && !f.Module.Busy &&
                    ((ListView)root.FindName("NavigationList")).SelectedItem is ShellNavigationEntry { Route: "catalog-home" }, "native home startup did not settle");
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization; Layout(); await Drain();
                True(WinRT.Interop.WindowNative.GetWindowHandle(shell) != 0 && DesktopTestUi.WindowIsVisible(shell), "the actual shell owns a visible native HWND");
                foreach (var control in new[] { f.Module.SettingsView, f.Module.PlayerView,
                    f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail), f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue) }.Cast<Control>())
                { Equal("XamlAutoFontFamily", control.FontFamily.Source); Equal(FontWeights.Normal, control.FontWeight); }
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var search = (TextBox)root.FindName("SearchBox");
                var value = (IValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(search)!.GetPattern(PatternInterface.Value);
                void SubmitSearch() => True(shell.TrySubmitSearch(VirtualKeyModifiers.None, false), "the same real search admission owner accepts Enter intent");
                var back = (Button)root.FindName("BackButton"); var player = f.Module.PlayerView;
                value.SetValue("   "); SubmitSearch(); await Drain(); Equal(0, f.Client.Count("music.search.start"));
                var refresh = Descendants<Button>(catalog).First(button => DesktopVisual.IsVisible(button) && AutomationProperties.GetName(button) == "刷新推荐");
                int homeReads = f.Client.Count("music.catalog.home"); Invoke(refresh);
                await Until(() => f.Client.Count("music.catalog.home") > homeReads && !f.Module.Busy, "native refresh did not settle");
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization; Layout(); await Drain();
                var entry = Descendants<EntryCard>(catalog).First(control => control.DataContext is MusicCatalogCard { CanRequestPreview: true });
                var surface = (Grid)entry.FindName("Surface"); CardMotion.SetPointerActive(surface, true);
                await Until(() => ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count == 3, "native preview did not settle"); await Drain();
                Equal(4, f.Client.Count("music.catalog.playlist"));
                True(Descendants<TextBlock>(entry).Any(text => text.Text.StartsWith("1 合成曲", StringComparison.Ordinal)), "native preview controls show the actual receipt");
                Invoke((Button)entry.FindName("PlayButton"));
                await Until(() => f.Playback.Opens.Count == 1 && !f.Module.Busy, "native play did not settle"); Layout(); await Drain();
                AssertDisplayAutoText([Descendants<TextBlock>(player).Single(text => text.Text == f.Module.CurrentTitle),
                    Descendants<TextBlock>(player).Single(text => text.Text == f.Module.CurrentArtist)], "actual playing title and artist");
                Equal(1, f.Client.Count("music.catalog.playlist.prepare")); Equal(1, f.Client.Count("music.catalog.playlist.commit"));
                True(f.Module.IsCatalogHome, "entry play does not navigate"); CardMotion.SetPointerActive(surface, false);
                value.SetValue("  云歌单  "); SubmitSearch();
                await Until(() => f.Client.Count("music.search.start") == 2 && f.Module.IsSearchPage, "native search did not settle");
                await WaitForSearch(f.Module); Layout();
                Equal("云歌单", f.Client.Last("music.search.start").GetProperty("keyword").GetString());
                Equal("playlists", f.Client.Last("music.search.start").GetProperty("kind").GetString());
                True(f.Module.CatalogSections.SelectMany(section => section.Items).Any(card => card.CanOpen), "real search publishes actionable native results");
                Invoke(back); await Until(() => f.Module.IsCatalogHome && !f.Module.Busy, "native search back did not settle"); Layout(); await Drain();
                True(ReferenceEquals(player, ((ContentControl)root.FindName("PlayerHost")).Content), "search and back retain the same player controls");
                entry = Descendants<EntryCard>(catalog).First(control => control.DataContext is MusicCatalogCard { CanRequestPreview: true });
                Invoke((Button)entry.FindName("OpenButton"));
                await Until(() => f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 3 && !f.Module.Busy, "native entry open did not settle"); Equal(1, f.Playback.Opens.Count);
                Invoke(back); await Until(() => f.Module.IsCatalogHome && !f.Module.Busy, "native detail back did not settle"); Layout(); await Drain();
                var scroll = (ScrollViewer)catalog.FindName("HomeScroll"); int pages = f.Client.Count("music.catalog.home");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); Layout(); await Drain(); Equal(pages, f.Client.Count("music.catalog.home"));
                catalog.BrowseCatalogKey(VirtualKey.PageDown); await Drain(); Equal(pages, f.Client.Count("music.catalog.home"));
                True(!f.Module.CanCatalogNext, "scrolling cannot restart completed column initialization");
                foreach (var size in new[] { new Size(860, 600), new Size(1110, 800), new Size(1440, 900) })
                {
                    shell.Resize(size.Width, size.Height); await Drain(); Layout();
                    var content = (Grid)root.FindName("ContentRegion"); var footer = (Border)root.FindName("PlayerFrame");
                    True(content.TransformToVisual(root).TransformPoint(new Point(0, content.ActualHeight)).Y <= footer.TransformToVisual(root).TransformPoint(new Point()).Y + .5, "footer does not cover content");
                    var hit = DesktopTestUi.Hit(root, search.TransformToVisual(root).TransformPoint(new Point(search.Padding.Left + 16, 16)));
                    True(hit is not null && Descendants<DependencyObject>(search).Contains(hit), "the native search field owns its real hit target");
                    var frame = await DesktopTestUi.Render(root);
                    True(frame.Pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 240) > frame.Width * frame.Height * .8, "the actual native shell paints opaque content");
                }
                True(Descendants<UIElement>(root).All(element => element.PointerCaptures.Count == 0), "semantic actions leave no pointer captures");
                Console.WriteLine("NATIVE_HOME_SHELL Value/Invoke actions; real search admission; refresh, preview, play, search, back, open, paging and resize; no keyboard or pointer input");
            }
            finally { shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(8)); await Drain(); }
        });
        yield return Case("each catalog section selects exactly one presenter", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            var song = SongRow.Read(MusicSongFixture.Element(MusicSongFixture.Online("wy", "1", "歌曲", "歌手", "专辑", 180)));
            var playlist = new MusicCatalogCard("p", "歌单", "", "", "playlist", "wy", ActionId: "p");
            var artist = new MusicCatalogCard("a", "歌手", "单曲：1", "", "artist", "wy", ActionId: "a");
            var ranking = playlist with { Preview = [new MusicRankPreview("歌曲", "歌手", "", 1, 0)] };
            var cases = new[]
            {
                (new MusicCatalogSection("歌单", [playlist], Layout: "playlist-rail"), MusicCatalogSectionPresentation.Shelf, MusicCatalogCardLayout.Playlist),
                (new MusicCatalogSection("榜单推荐", [ranking], Layout: "ranking-small"), MusicCatalogSectionPresentation.RankingCovers, MusicCatalogCardLayout.RankCover),
                (new MusicCatalogSection("精选榜", [playlist], Layout: "ranking-small"), MusicCatalogSectionPresentation.RankingCovers, MusicCatalogCardLayout.RankCover),
                (new MusicCatalogSection("曲风榜", [playlist], Layout: "ranking-small"), MusicCatalogSectionPresentation.RankingCovers, MusicCatalogCardLayout.RankCover),
                (new MusicCatalogSection("官方榜", [playlist], Type: "rankings", Layout: "ranked-songs"), MusicCatalogSectionPresentation.RankingPreviews, MusicCatalogCardLayout.Ranking),
                (new MusicCatalogSection("会员榜", [playlist], Type: "rankings", Layout: "vip-ranked-songs"), MusicCatalogSectionPresentation.RankingPreviews, MusicCatalogCardLayout.Ranking),
                (new MusicCatalogSection("官方榜数据", [ranking], Layout: "ranking"), MusicCatalogSectionPresentation.RankingPreviews, MusicCatalogCardLayout.Ranking),
                (new MusicCatalogSection("歌曲", [new MusicCatalogCard("s", "歌曲", "歌手", "", "song", "wy", song)], Layout: "song-list"), MusicCatalogSectionPresentation.Songs, MusicCatalogCardLayout.Song),
                (new MusicCatalogSection("歌曲摘要", [new MusicCatalogCard("s", "歌曲", "歌手", "", "song", "wy", song)], Layout: "song-columns"), MusicCatalogSectionPresentation.Songs, MusicCatalogCardLayout.Song),
                (new MusicCatalogSection("", [artist], Type: "artists", Layout: "artist-grid"), MusicCatalogSectionPresentation.Artists, MusicCatalogCardLayout.Artist)
            };
            var view = new MusicCatalogView { DataContext = f.Module };
            using var host = new DesktopTestUi.Host(view, 900, 320);
            foreach (var (section, presentation, layout) in cases)
            {
                Equal(presentation, section.Presentation);
                if (presentation == MusicCatalogSectionPresentation.RankingCovers)
                    True(!section.IsPlaylistSection, section.Title + " must not fall into playlist rows");
                var presenter = new MusicCatalogSectionView { DataContext = section }; view.Content = presenter;
                view.UpdateLayout(); await Drain();
                var body = ((StackPanel)presenter.Content).Children.OfType<ContentControl>().Single().Content;
                bool shelf = presentation is MusicCatalogSectionPresentation.Shelf or MusicCatalogSectionPresentation.RankingCovers || section.IsSongShelfSection;
                True(shelf ? body is Shelf : body is ItemsControl, section.Title + " installs exactly its one native body presenter");
                var cards = Descendants<MusicCatalogCardView>(presenter).ToArray();
                True(cards.Length > 0 && cards.All(card => card.Presentation == layout), section.Title + " uses the requested native card layout");
            }
        });
        yield return Case("summary song shelves keep source order in fixed three-row horizontal columns", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            var cards = Enumerable.Range(1, 7).Select(index =>
            {
                var song = SongRow.Read(MusicSongFixture.Element(MusicSongFixture.Online("wy", index.ToString(), "歌曲" + index, "歌手", "专辑", 180)));
                return new MusicCatalogCard(index.ToString(), song.Title, song.Artist, song.Cover, "song", "wy", song, ActionId: index.ToString());
            }).ToArray();
            var section = new MusicCatalogSection("歌曲", cards, Layout: "song-columns");
            Equal("1,2,3|4,5,6|7", string.Join("|", section.SongColumns.Select(column => string.Join(",", column.Items.Select(item => item.Id)))));
            var view = new MusicCatalogView { DataContext = f.Module };
            using var host = new DesktopTestUi.Host(view, 900, 320);
            foreach (var current in new[] { new MusicCatalogSection("单列", cards.Take(1).ToArray(), Layout: "song-columns"), section })
            {
                var presenter = new MusicCatalogSectionView { DataContext = current }; view.Content = presenter;
                view.UpdateLayout(); await Drain();
                var shelf = Descendants<Shelf>(presenter).Single();
                Equal(current.SongColumns.Count, shelf.ItemsSource!.Cast<MusicCatalogSongColumn>().Count());
                var column = Descendants<ItemsControl>(shelf).First(item => item.DataContext is MusicCatalogSongColumn);
                Equal(252d, column.ActualHeight);
                True(Descendants<MusicCatalogCardView>(column).All(card => card.ActualHeight == 84), "three source rows retain their complete native hit heights");
                Equal(Visibility.Collapsed, ((Button)shelf.FindName("Previous")).Visibility);
                Equal(current.Items.Count == 1 ? Visibility.Collapsed : Visibility.Visible, ((Button)shelf.FindName("Next")).Visibility);
            }
        });
        yield return Case("channel chrome owns direct tabs filters and no duplicate platform selector", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "feature"));
            var view = new MusicCatalogView { DataContext = f.Module };
            var window = DesktopTestUi.CreateWindow(view, 1042, 700);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var header = (DockPanel)view.FindName("ChannelHeader");
                Equal(Visibility.Visible, header.Visibility);
                Equal(new Thickness(40, 0, 40, 12), header.Margin);
                True(!Descendants<ComboBox>(view).Any(combo => DesktopVisual.IsVisible(combo) &&
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
                Equal(((SolidColorBrush)DesktopVisual.Resource(view, "MeowFilm.Brush.Accent")).Color,
                    ((SolidColorBrush)underline.Background).Color);

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "rock"));
                await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Id == "30"));
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
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
                Equal(((SolidColorBrush)DesktopVisual.Resource(view, "MeowFilm.Brush.AccentSoft")).Color,
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
            var window = DesktopTestUi.CreateWindow(page, 1042, 700);
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 1,
                    "VIP cards did not activate after the page loaded");
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();

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
                True(ReferenceEquals(Ancestor<ScrollViewer>(row), catalog.ActiveScroll),
                    "VIP cards belong to the same vertical transition and retention surface as the sections");
                Equal(Visibility.Visible, account.Visibility); Equal(228d, account.ActualWidth);
                Equal(Visibility.Visible, sign.Visibility); Equal(228d, sign.ActualWidth);
                Equal(Visibility.Visible, banner.Visibility); Equal(472d, banner.ActualWidth);
                var gradient = (LinearGradientBrush)account.Background;
                Equal(Color.FromArgb(255, 33, 27, 27), gradient.GradientStops[0].Color);
                Equal(Color.FromArgb(255, 91, 48, 48), gradient.GradientStops[1].Color);
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
                DesktopTestUi.Invoke(signButton);
                await Until(() => vip.Signs == 1 && f.Module.VipPage.SignedToday &&
                    !signButton.IsEnabled && Equals(signButton.Content, "已打卡"),
                    "VIP sign did not publish its single refreshed sign branch");
                Equal(1, vip.Reads);

                DesktopTestUi.Invoke(banner);
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
                DesktopTestUi.WindowRoot(window).UpdateLayout();
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
            var window = DesktopTestUi.CreateWindow(page, 1042, 700);
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
            var window = DesktopTestUi.CreateWindow(page, 1042, 700);
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
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Group == "area" && tag.Id == "96:-1:-1"));
            Equal("96:-1:-1", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            await f.Module.CatalogPageAsync(1);
            Equal("第 2 页", f.Module.CatalogPageText);
            Equal(80, f.Module.CatalogSections.Single().Items.Count);
            var selectedTag = f.Module.SelectedCatalogTag?.Id ?? throw new InvalidOperationException("artist selected tag");
            var channelCalls = f.Client.Count("music.catalog.channel");
            var card = f.Module.CatalogSections.Single().Items.First();
            True(card.Kind == "artist" && card.CanOpen, "artist card must expose the native detail action");

            var page = (MusicView)f.Module.View;
            var window = DesktopTestUi.CreateWindow(page, 1042, 700);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var catalog = (MusicCatalogView)page.FindName("CatalogView");
                var scroll = catalog.ActiveScroll;
                True(scroll.ScrollableHeight > 0, "artist fixture must expose vertical scroll");
                scroll.ChangeView(null, Math.Min(240, scroll.ScrollableHeight), null, true);
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var savedOffset = scroll.VerticalOffset;

                var button = Descendants<Button>(catalog).Single(candidate => ReferenceEquals(candidate.Tag, card));
                Equal(184d, button.ActualWidth);
                Equal(184d, Descendants<Grid>(button).Single(grid => CardClip.GetRadius(grid) == 92).ActualWidth);
                Equal(card.Title, AutomationProperties.GetName(button));
                DesktopTestUi.Invoke(button);
                await Until(() => f.Module.IsCatalogArtist && !f.Module.Busy);

                True(f.Module.IsCatalogDetailPage && f.Module.IsContentDetailPage && f.Module.ShowsSongTable &&
                    !f.Module.IsCatalogContent, "artist detail must own the shared native song surface");
                Equal(card.Title, f.Module.Heading);
                Equal("热门歌曲", f.Module.DetailSectionTitle);
                Equal("3", f.Module.DetailCountText);
                Equal("https://img.invalid/artist-" + card.ActionId + ".jpg", f.Module.DetailArtwork);
                Equal(3, f.Module.Songs.Count);
                Equal(1, f.Client.Count("music.catalog.artist"));
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var artwork = (Border)page.FindName("DetailArtworkFrame");
                Equal(new CornerRadius(76), artwork.CornerRadius);
                Equal(76d, CardClip.GetRadius(artwork));
                True(RoundedClip(artwork).CornerRadius == new System.Numerics.Vector2(76),
                    "artist artwork content must be physically clipped to the round frame");
                True(Descendants<TextBlock>(page).Any(text => DesktopVisual.IsVisible(text) && text.Text == "热门歌曲"),
                    "artist detail must label the real hot-song section");

                await f.SetupSource();
                await f.Module.PlayContentDetailAsync();
                Equal(3, (await f.Rpc("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
                Equal(1, f.Client.Count("music.catalog.artist"));

                await f.Module.NavigateAsync("catalog-channel/wy/artist", default);
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
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
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
            var view = new MusicCatalogView { DataContext = f.Module };
            var cards = Enumerable.Range(1, 7)
                .Select(i => new MusicCatalogCard(i.ToString(), "榜" + i, "不应显示", "", "playlist", "wy", ActionId: i.ToString()))
                .ToArray();
            var section = new MusicCatalogSection("曲风榜", cards, Layout: "ranking-small");
            var presenter = new MusicCatalogSectionView { DataContext = section };
            view.Content = presenter;
            var window = DesktopTestUi.CreateWindow(view, 960, 260);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                Equal(MusicCatalogSectionPresentation.RankingCovers, section.Presentation);
                var shelf = Descendants<Shelf>(presenter).Single();
                True(Descendants<MusicCatalogCardView>(shelf).All(card => card.Presentation == MusicCatalogCardLayout.RankCover),
                    "ranking covers own their dedicated native presentation");
                Equal(120d, shelf.MinimumCardWidth);
                True(shelf.CardWidth - 16 is >= 120 and <= 168,
                    "ranking covers use the readable native range without exceeding the reference maximum");
                var card = Descendants<Button>(shelf).First(button => button.Tag is MusicCatalogCard);
                True(Math.Abs(card.ActualWidth - card.ActualHeight) < .5,
                    $"ranking artwork card is square ({card.ActualWidth} × {card.ActualHeight})");
                var visibleText = Descendants<TextBlock>(card).Where(text => DesktopVisual.IsVisible(text)).ToArray();
                Equal(1, visibleText.Length); Equal("榜1", visibleText[0].Text);
                var next = (Button)shelf.FindName("Next");
                CardMotion.SetPointerActive(shelf, true); await Until(() => !CardMotion.HasActiveAnimation(shelf));
                Equal(1d, next.Opacity);
                CardMotion.SetPointerActive(shelf, false); await Until(() => !CardMotion.HasActiveAnimation(shelf));
                Equal(0d, next.Opacity);
            }
            finally { window.Close(); }
        });
        yield return Case("shelf arrows occupy gutters page both ways and hide without overflow", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1,18).Select(i => new MusicCatalogCard(i.ToString(), "歌单"+i, "", "", "playlist", "wy")).ToArray();
            var section = new MusicCatalogSection("推荐歌单", cards);
            var shelf = new Shelf { MinimumCardWidth = 124, CardTemplate = MusicTestUi.Template("<local:MusicCatalogCardView Presentation='Playlist'/>"),
                DataContext = section, Margin = new Thickness(40) };
            view.Content = shelf;
            var window = DesktopTestUi.CreateWindow(view, 906, 320);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
                var previous = (Button)shelf.FindName("Previous"); var next = (Button)shelf.FindName("Next");
                True(rail.ScrollableWidth > 0 && next.IsEnabled && !previous.IsEnabled, "overflow right, left boundary disabled");
                Equal(Visibility.Collapsed, previous.Visibility); Equal(Visibility.Visible, next.Visibility);
                Equal(0d, next.Opacity); True(!next.IsHitTestVisible, "hidden controls do not intercept pointer");
                True(previous.TransformToVisual(rail).TransformPoint(new Point(previous.ActualWidth,0)).X <= 0, "left arrow outside card viewport");
                True(next.TransformToVisual(rail).TransformPoint(new Point()).X >= rail.ActualWidth, "right arrow outside card viewport");
                next.Focus(FocusState.Programmatic); await Drain(); Equal(1d, next.Opacity);
                DesktopTestUi.Invoke(next); await Drain();
                await Until(() => section.HorizontalOffset > 0 && Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5);
                True(previous.IsEnabled, "next moves real content");
                DesktopTestUi.Invoke(previous); await Drain();
                await Until(() => rail.HorizontalOffset < .5 && section.HorizontalOffset < .5);
                True(shelf.Navigate(VirtualKey.End), "End is handled by the shelf navigation owner");
                await Until(() => Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5);
                True(shelf.Navigate(VirtualKey.Home), "Home is handled by the shelf navigation owner");
                await Until(() => rail.HorizontalOffset < .5 && section.HorizontalOffset < .5);
                shelf.DataContext = new MusicCatalogSection("推荐歌单", cards.Take(2).ToArray());
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
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
                CardTemplate = MusicTestUi.Template("<local:MusicCatalogCardView Presentation='Playlist'/>"),
                DataContext = first,
                Margin = new Thickness(40)
            };
            view.Content = shelf;
            var window = DesktopTestUi.CreateWindow(view, 906, 320);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
                var items = (ListView)shelf.FindName("ItemsHost");
                var realized = Enumerable.Range(0, cards.Length)
                    .Count(index => items.ContainerFromIndex(index) is not null);
                True(realized > 0 && realized < cards.Length / 4,
                    "realized containers remain bounded by viewport/cache rather than total items");

                var next = (Button)shelf.FindName("Next");
                DesktopTestUi.Invoke(next);
                await Until(() => rail.HorizontalOffset > 1);
                await Until(() => first.HorizontalOffset > 0 && Math.Abs(first.HorizontalOffset - rail.HorizontalOffset) < .5);
                True(first.HorizontalOffset >= shelf.CardWidth,
                    "native page navigation finishes at a legal snap offset");
                var saved = first.HorizontalOffset;
                shelf.DataContext = second; DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => rail.HorizontalOffset < .5);
                shelf.DataContext = first; DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => Math.Abs(rail.HorizontalOffset - saved) < .5);

                rail.ChangeView(saved + shelf.CardWidth * .37, null, null, true); DesktopTestUi.WindowRoot(window).UpdateLayout();
                var inBetween = rail.HorizontalOffset;
                var settled = Math.Clamp(Math.Round(inBetween / shelf.CardWidth) * shelf.CardWidth, 0, shelf.MaximumOffset);
                shelf.DataContext = second; DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => Math.Abs(first.HorizontalOffset - settled) < .5 && rail.HorizontalOffset < .5);
                True(Math.Abs(first.HorizontalOffset - inBetween) > 1,
                    "a context switch commits the legal snap target, not a transient physical frame");
                shelf.DataContext = first; DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => Math.Abs(rail.HorizontalOffset - settled) < .5);
                saved = settled;

                DesktopTestUi.Invoke(next);
                await Until(() => rail.HorizontalOffset > saved + 1);
                DesktopTestUi.Invoke(next);
                window.Resize(790, DesktopTestUi.WindowSize(window).Height); DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => Math.Abs(first.HorizontalOffset - rail.HorizontalOffset) < .5 &&
                    Math.Abs(rail.HorizontalOffset / Math.Max(1, shelf.CardWidth) -
                        Math.Round(rail.HorizontalOffset / Math.Max(1, shelf.CardWidth))) < .01);
                rail.ChangeView(Math.Min(rail.ScrollableWidth, rail.HorizontalOffset + 17), null, null, true);
                DesktopTestUi.WindowRoot(window).UpdateLayout();
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
            var entry = new EntryCard { Width = 152, VerticalAlignment = VerticalAlignment.Top };
            playlist.InitializePreview(new[] { "第一首", "第二首", "第三首" }.Select((title, i) => new MusicRankPreview(title, "", "", i + 1, null)).ToArray());
            entry.DataContext = playlist;
            using var host = new DesktopTestUi.Host(entry, 320, 300);
            await TestUiHost.LoadedAsync(entry); await Drain(); entry.UpdateLayout();
            var surface = (Grid)entry.FindName("Surface");
            var caption = (TextBlock)entry.FindName("Caption");
            var restingPosition = caption.TransformToVisual(surface).TransformPoint(new Point());
            CardMotion.SetPointerActive(surface, true); await Until(() => !CardMotion.HasActiveAnimation(surface));
            Equal(1d, CardMotion.GetProgress(surface)); Equal(200d, entry.ActualHeight);
            True(caption.TransformToVisual(surface).TransformPoint(new Point()).Y < restingPosition.Y,
                "a preview entry lifts its caption with the initialized songs");
            foreach (var variant in new[] { overlay, fm })
            {
                entry.DataContext = variant; CardMotion.SetPointerActive(surface, true); await Until(() => !CardMotion.HasActiveAnimation(surface));
                Equal(restingPosition, caption.TransformToVisual(surface).TransformPoint(new Point()));
                Equal(10d, ((Button)entry.FindName("PlayButton")).Margin.Bottom);
            }
            entry.DataContext = staticCard; CardMotion.SetPointerActive(surface, true); await Drain();
            Equal(Visibility.Collapsed, ((Button)entry.FindName("PlayButton")).Visibility);
            CardMotion.SetPointerActive(surface, false);
            CardMotion.SetPointerActive(surface, true); await Task.Delay(60); await Drain();
            True(CardMotion.GetProgress(surface) is > 0 and < 1, "real intermediate animation, not instant switch");
            host.Window.Content = null; await Drain();
            Equal(0d, CardMotion.GetProgress(surface));
        });
        yield return Case("entry initialization reads three songs once without hover or queue changes", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            JsonElement reply = default;
            f.Client.Transform("music.catalog.playlist", value => { reply = value; return value; });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            True(reply.GetProperty("ok").GetBoolean(), "initialization receives real native metadata");
            Equal(3, card.PreviewTitles.Count);
            Equal(3, f.Client.Last("music.catalog.playlist").GetProperty("limit").GetInt32());
            int reads = f.Client.Count("music.catalog.playlist");
            var entry = new EntryCard { DataContext = card };
            CardMotion.SetPointerActive(entry, true);
            CardMotion.SetPointerActive(entry, false);
            Equal(reads, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare")); Equal(0, f.Client.Count("music.queue.replace"));
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("same-resource cards share page initialization and leaving a card does not cancel it", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3, homePages = 1 });
            f.Client.Transform("music.catalog.home", value => Edit(value, page => {
                var entries = page["sections"]!.AsArray().First(section => section?["layout"]?.GetValue<string>() == "feature-rail")!["items"]!.AsArray();
                var card = entries.First(entry => entry?["action"]?["type"]?.GetValue<string>() == "playlist")!;
                var copy = card.DeepClone(); copy["key"] = "duplicate-preview"; entries.Add(copy);
            }));
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            var pending = f.Module.CatalogEntryInitialization;
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cards = f.Module.CatalogSections.SelectMany(s => s.Items).Where(c => c.CanRequestPreview).ToArray();
            var entry = new EntryCard { DataContext = cards[0] };
            CardMotion.SetPointerActive(entry, true);
            CardMotion.SetPointerActive(entry, false);
            True(!pending.IsCompleted && !hold.Cancelled.Task.IsCompleted, "pointer departure cannot cancel page work");
            Equal(1, f.Client.Count("music.catalog.playlist"));
            hold.Release(); await pending;
            True(cards.Length >= 2 && cards.All(card => card.PreviewTitles.Count == 3), "shared result publishes to each card");
        });
        yield return Case("entry initialization bounds physical concurrency and stops queued reads while hidden", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { homePages = 1, detailLimit = 3 });
            f.Client.Transform("music.catalog.home", value => Edit(value, page => {
                page["sections"]!.AsArray().First(section => section?["layout"]?.GetValue<string>() == "feature-rail")!["items"] = JsonSerializer.SerializeToNode(Enumerable.Range(500, 8).Select(id => new {
                    id = id.ToString(), kind = "playlist", title = "入口 " + id, presentation = "preview", action = new { type = "playlist", id = id.ToString() }
                }).ToArray());
            }));
            var holds = Enumerable.Range(0, 4).Select(_ => f.Client.Hold("music.catalog.playlist")).ToArray();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            var pending = f.Module.CatalogEntryInitialization;
            await Task.WhenAll(holds.Select(hold => hold.Arrived.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(4, f.Client.Count("music.catalog.playlist"));
            await f.Module.NavigateAsync("favorites", default);
            True(holds.All(hold => !hold.Cancelled.Task.IsCompleted), "hiding retains admitted physical work");
            foreach (var hold in holds) hold.Release();
            await pending; Equal(4, f.Client.Count("music.catalog.playlist")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("cancelled page initialization retains physical work until shutdown drains", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3 });
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
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
        yield return Case("hidden page retains physical initialization without publishing until redisplay", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3, homePages = 1 });
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            var pending = f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("favorites", default);
            True(!hold.Cancelled.Task.IsCompleted, "hiding does not retire the content snapshot");
            True(!pending.IsCompleted, "hidden content retains physical drain");
            hold.Release(); await pending;
            True(!card.HasPreview, "hidden response does not publish to its card");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            await f.Module.CatalogEntryInitialization;
            Equal(3, f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview).PreviewTitles.Count);
            Equal(1, f.Client.Count("music.catalog.playlist")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("refresh retires pending initialization without clearing newer card data", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3, homePages = 1 });
            var hold = f.Client.Hold("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            var pending = f.Module.CatalogEntryInitialization;
            var old = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.RefreshCatalogAsync();
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            await f.Module.CatalogEntryInitialization;
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
        yield return Case("initialized native titles bind before the first hover", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var view = new MusicCatalogView { DataContext = f.Module };
            var entry = new EntryCard { Width = 152, VerticalAlignment = VerticalAlignment.Top };
            entry.DataContext = card; view.Content = entry;
            var window = DesktopTestUi.CreateWindow(view, 168, 230);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                CardMotion.SetPointerActive(entry, true);
                await f.Module.CatalogEntryInitialization;
                Equal(2, f.Client.Count("music.catalog.playlist"));
                Equal(3, ((MusicCatalogCard)entry.DataContext).PreviewTitles.Count);
                True(Descendants<TextBlock>(entry).Any(t => t.Text.StartsWith("1 合成曲", StringComparison.Ordinal)), "real preview text rendered");
                var surface = (Grid)entry.FindName("Surface");
                CardMotion.SetPointerActive(surface, true); await Until(() => !CardMotion.HasActiveAnimation(surface));
                var preview = (FrameworkElement)entry.FindName("PreviewBlock");
                var bottom = preview.TransformToVisual(surface).TransformPoint(new Point(0, preview.ActualHeight)).Y;
                True(bottom <= surface.ActualHeight && bottom > 0, "expanded preview remains inside the actual entry clip");
            }
            finally { window.Close(); }
        });
        yield return Case("failed initialization does not retry on hover or request FM", async () =>
        {
            await using var f = await Fixture.Create(); await f.Configure(new { detailLimit = 3 });
            f.Client.Transform("music.catalog.playlist", _ => JsonSerializer.SerializeToElement(new { ok = false, error = new { code = "UNAVAILABLE" } }));
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            f.Module.UpdateCatalogVisibleCards(f.Module.DisplayCatalogSections.SelectMany(section => section.Items));
            await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            Equal(0, card.PreviewTitles.Count);
            int calls = f.Client.Count("music.catalog.playlist");
            var entry = new EntryCard { DataContext = card };
            for (int i = 0; i < 3; i++) CardMotion.SetPointerActive(entry, true);
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
            var tile = new MusicCatalogCardView { Presentation = MusicCatalogCardLayout.Playlist, Width = 124, VerticalAlignment = VerticalAlignment.Top };
            tile.DataContext = card; view.Content = tile;
            using var host = new DesktopTestUi.Host(view, 320, 300);
            await TestUiHost.LoadedAsync(tile); await Drain(); tile.UpdateLayout();
            var open = Descendants<Button>(tile).Single(button => ReferenceEquals(button.Tag, card));
            True(open.Background is SolidColorBrush { Color.A: 0 },
                "generic button hover brush must not tint the playlist caption");
            var play = Descendants<Button>(tile).Single(button => button.Content is CardPlayGlyph);
            CardMotion.SetPointerActive((FrameworkElement)tile.Content, true); await Until(() => !CardMotion.HasActiveAnimation((FrameworkElement)tile.Content)); tile.UpdateLayout();
            True(play.ActualWidth == 44 && play.ActualHeight == 44, "only the central play target owns playback");
            var origin = play.TransformToVisual(tile).TransformPoint(new Point());
            True(origin.X > 0 && origin.Y > 0, "the play target leaves artwork outside its bounds");
            DesktopTestUi.Invoke(play);
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
            using var source = new DesktopTestUi.Host(new Grid(), 906, 300);
            source.Mount(f.Module.View);
            f.Module.View.Measure(new(906, 300)); f.Module.View.Arrange(new Rect(0, 0, 906, 300)); f.Module.View.UpdateLayout(); await Drain();
            var view = (MusicCatalogView)f.Module.View.FindName("CatalogView");
            var scroll = (ScrollViewer)view.FindName("HomeScroll");
            scroll.ChangeView(null, Math.Min(90, scroll.ScrollableHeight), null, true); await Drain();
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
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain(); Equal(3, f.Client.Count("music.catalog.home"));
            True(playing.IsPreparing && !preparing.Cancelled.Task.IsCompleted, "background column append cannot retire the active playback intent");
            f.Module.CancelCatalog(); preparing.Release(); await action;
            True(!playing.IsPreparing, "cancel resets the originating card"); Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            source.Window.Content = null;
        });
        yield return Case("native outer scrolling preserves a horizontal card shelf offset", async () =>
        {
            var shelf = new Shelf { Height = 200 };
            var content = new StackPanel();
            content.Children.Add(shelf); content.Children.Add(new Border { Height = 1600 });
            var outer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = DesktopTestUi.CreateWindow(outer, 826, 400);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(outer)!;
                ((IScrollProvider)peer.GetPattern(PatternInterface.Scroll)).Scroll(
                    Microsoft.UI.Xaml.Automation.ScrollAmount.NoAmount, Microsoft.UI.Xaml.Automation.ScrollAmount.SmallIncrement);
                await Drain(); outer.UpdateLayout();
                True(outer.VerticalOffset > 0, "native outer page moves without changing horizontal content");
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
                new { id = "rank", title = "榜单", layout = "ranked-songs", items = new[] {
                    new { id = "123", title = "测试榜", kind = "playlist", cover = "", updateFrequency = "每日更新",
                        action = new { type = "playlist", id = "123" },
                        preview = new[] { new { title = "测试歌曲", artist = "测试歌手", cover = "", rank = 1, lastRank = 3 } } } } } } });
            var section = MusicCatalogDto.Read(reply, "wy", "home", "推荐").Single();
            True(section.IsRankingPreviewSection && !section.IsRankingCoverSection && !section.IsSongSection,
                "ranked-songs layout owns the preview presentation");
            Equal("ranked-songs", section.Layout); Equal("每日更新", section.Items[0].UpdateFrequency);
            Equal("↑", section.Items[0].PreviewSongs.Single().Movement);
            True(section.Items[0].CanOpen, "ranking opens its actual playlist action");
            foreach (int width in new[] { 1000, 1490 })
            {
                await using var f = await Fixture.Create();
                await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
                var view = new MusicCatalogView { DataContext = f.Module, Width = width };
                var presenter = new MusicCatalogSectionView { DataContext = section with { Items = Enumerable.Range(1, 4).Select(i => section.Items[0] with { Id = i.ToString() }).ToArray() } };
                view.Content = presenter;
                using var host = new DesktopTestUi.Host(view, width, 700);
                await TestUiHost.LoadedAsync(presenter); await Drain(); view.UpdateLayout();
                var items = Descendants<MusicCatalogCardView>(presenter).ToArray();
                Equal(4, items.Length);
                Equal(2, items.Count(item => DesktopTestUi.Bounds(item, presenter).Top == DesktopTestUi.Bounds(items[0], presenter).Top));
            }
            var shelf = new Shelf { MinimumCardWidth = 124, DataContext = section with { Layout = "playlist-rail" } };
            await WithMountedShelf(shelf, 1000, () =>
                True(shelf.CardWidth is >= 140 and <= 146, "ordinary covers use seven columns at 1000 pixels"));
            var card = new MusicCatalogCardView { DataContext = section.Items[0], Presentation = MusicCatalogCardLayout.Playlist, Width = 152, VerticalAlignment = VerticalAlignment.Top };
            using var cardHost = new DesktopTestUi.Host(card, 320, 300);
            await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
            Equal(1, Descendants<TextBlock>(card).Count(text => text.Text == section.Items[0].Title));
            True(Descendants<FrameworkElement>(card).Prepend(card).All(element => ToolTipService.GetToolTip(element) is null),
                "catalog content has no hover hints");
        });
        yield return Case("catalog entry cover ranking and artist cards have no hover hints in either availability state", async () =>
        {
            foreach (var presentation in new[] { "entry", "playlist", "ranking-cover", "artist" })
            foreach (var available in new[] { true, false })
            {
                var data = new MusicCatalogCard("card", "保留的内容标题", "保留的副标题", "",
                    presentation == "artist" ? "artist" : "playlist", "wy", ActionId: "123",
                    Available: available, ErrorCode: available ? "" : "UNSUPPORTED");
                FrameworkElement card = presentation == "entry" ? new EntryCard() : new MusicCatalogCardView { Presentation = presentation switch
                { "artist" => MusicCatalogCardLayout.Artist, "ranking-cover" => MusicCatalogCardLayout.RankCover, _ => MusicCatalogCardLayout.Playlist } };
                card.DataContext = data;
                card.Width = 168; card.VerticalAlignment = VerticalAlignment.Top;
                using var host = new DesktopTestUi.Host(card, 320, 400);
                await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
                var elements = Descendants<FrameworkElement>(card).Prepend(card).ToArray();
                True(elements.All(element => ToolTipService.GetToolTip(element) is null), presentation + " exposes no ordinary hover hints");
                True(elements.OfType<TextBlock>().Any(text => text.Text == data.Title), "visible title remains");
                var open = card is EntryCard entry ? (Button)entry.FindName("OpenButton") : Descendants<Button>(card).Single(button => ReferenceEquals(button.Tag, data));
                Equal(data.Title, AutomationProperties.GetName(open));
                Equal(available, open.IsEnabled);
            }
        });
        yield return Case("channel entry shelves reuse shared cards and supplied song previews", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.channel", reply => Edit(reply, node =>
                node["sections"] = JsonSerializer.SerializeToNode(new[] {
                    new { id = "electronic", title = "电音旗舰", layout = "feature-rail", items = new object[] {
                        new { id = "800", kind = "playlist", title = "电音歌单", subtitle = "精选电子音乐",
                            cover = "https://art.example.test/channel-playlist.jpg", presentation = "preview",
                            action = new { type = "playlist", id = "800" },
                            preview = new[] {
                                new { title = "第一首电音", artist = "歌手一", cover = "", rank = 1, lastRank = 0 },
                                new { title = "第二首电音", artist = "歌手二", cover = "", rank = 2, lastRank = 0 } } },
                        new { id = "801", kind = "playlist", title = "无副标题歌单", subtitle = "",
                            cover = "https://art.example.test/channel-empty-subtitle.jpg", presentation = "preview",
                            action = new { type = "playlist", id = "801" } },
                        new { id = "radio", kind = "unsupported", title = "电音电台", subtitle = "连续播放",
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
            var window = DesktopTestUi.CreateWindow(f.Module.View, 1108, 760);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
                var view = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var scroll = (ScrollViewer)view.FindName("CatalogScroll");
                var entries = Descendants<EntryCard>(scroll).ToArray();
                Equal(3, entries.Length);
                foreach (var entry in entries)
                {
                    var item = (MusicCatalogCard)entry.DataContext;
                    Equal(1, Descendants<TextBlock>(entry).Count(text => DesktopVisual.IsVisible(text) && text.Text == item.Title));
                    Equal(item.Cover, ((ArtworkImage)entry.FindName("Cover")).Address);
                }
                await Until(() => loader.Addresses.Count == 3, "channel cover addresses reach the shared artwork loader");
                var preview = entries.Single(entry => ReferenceEquals(entry.DataContext, card));
                True(card.PreviewTitles.SequenceEqual(new[] { "1 第一首电音", "2 第二首电音" }),
                    "the response's ordered song summaries reach the shared card");
                int reads = f.Client.Log.Count;
                CardMotion.SetPointerActive(preview, true);
                True(card.PreviewTitles.SequenceEqual(new[] { "1 第一首电音", "2 第二首电音" }),
                    "preloaded previews keep the supplied titles");
                Equal(reads, f.Client.Log.Count);
                var surface = (Grid)preview.FindName("Surface");
                CardMotion.SetPointerActive(surface, false); await Drain();
                var caption = (TextBlock)preview.FindName("Caption");
                double restingY = caption.TransformToVisual(surface).TransformPoint(new Point()).Y;
                CardMotion.SetPointerActive(surface, true); await Drain();
                True(caption.TransformToVisual(surface).TransformPoint(new Point()).Y < restingY,
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
        yield return Case("entry presentation consumes known values and defaults missing or unknown values", async () =>
        {
            var ordinaryReply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "section", title = "普通栏目", layout = "playlist-rail", items = new[] {
                    new { id = "1", title = "普通歌单", kind = "playlist", presentation = "preview",
                        action = new { type = "playlist", id = "1" } } } } } });
            Equal(MusicCatalogEntryPresentation.Preview,
                MusicCatalogDto.Read(ordinaryReply, "wy", "home", "").Single().Items.Single().Presentation);

            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var nested = f.Module.CatalogSections.Single(section => section.IsEntrySection).Items.Single(card => card.Song is not null);
            Equal(MusicCatalogEntryPresentation.Static, nested.Presentation);
            var song = nested.Song!.Raw;

            foreach (string? presentation in new string?[] { null, "Preview", "static" })
            {
                var reply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                    new { id = "entry", layout = "feature-rail", items = new[] {
                        new { id = "1", kind = "song", title = "固定卡片", song, presentation } } } } });
                Equal(MusicCatalogEntryPresentation.Static,
                    MusicCatalogDto.Read(reply, "wy", "home", "").Single().Items.Single().Presentation);
            }
        });
        yield return Case("home nested songs and unavailable actions use actual provider DTO", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { homePages = 1 });
            f.Client.Transform("music.catalog.home", reply => Edit(reply, page =>
            {
                var entries = page["sections"]!.AsArray().Single(section =>
                    section!["id"]!.GetValue<string>() == "ENTRANCE")!["items"]!.AsArray();
                entries.Single(entry => entry?["kind"]?.GetValue<string>() == "daily")!["display"] = true;
            }));
            await f.Module.NavigateAsync("catalog-home", default);
            var cards = f.Module.CatalogSections.SelectMany(s => s.Items).ToArray();
            var song = cards.Single(c => c.Song is not null);
            Equal("1001", MusicSongFixture.NativeId(song.Song!.Raw));
            True(song.Song!.Id != song.ActionId && song.ActionId == "1001", "permanent song identity is distinct from the card and action IDs");
            var daily = cards.Single(c => c.Kind == "daily");
            True(daily.Display && daily.RequiresAccount && f.Module.HomeSections.SelectMany(section => section.Items)
                .Any(card => ReferenceEquals(card, daily)), "the unavailable action is part of the actual displayed home");
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
                string.Join(",", f.Module.MoreChannelTabs.Take(35).Select(card => card.ActionId)));
            Equal("music_top_tab_acg,newsong,origin",
                string.Join(",", f.Module.MoreChannelTabs.Skip(35).Select(card => card.ActionId)));
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
        yield return Case("playlist next page appends with actual continuation then refresh resets", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            Equal(100, f.Module.Songs.Count);
            await f.Module.CatalogPageAsync(1);
            var p = f.Client.Last("music.catalog.playlist");
            Equal(2, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(100, p.GetProperty("continuation").GetProperty("offset").GetInt32());
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
            True(!p.TryGetProperty("continuation", out _), "returning starts from the current first page");
            Equal(100, f.Module.Songs.Count);
            Equal("第 1 页", f.Module.CatalogPageText);
            True(!f.Module.CanCatalogPrevious, "the accumulated song surface does not expose a page-back control");
            await f.Configure(new { nextDetail = 100 });
            await f.Module.LoadMoreCatalogPlaylistAsync();
            p = f.Client.Last("music.catalog.playlist");
            Equal(2, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(100, p.GetProperty("continuation").GetProperty("offset").GetInt32());
            Equal(200, f.Module.Songs.Count);
            Equal("1001", MusicSongFixture.NativeId(f.Module.Songs[0].Raw));
            Equal("1101", MusicSongFixture.NativeId(f.Module.Songs[100].Raw));
            True(!f.Module.CanCatalogPrevious, "appending after re-entry does not expose previous");
            await f.Configure(new { nextDetail = 200 });
            await f.Module.LoadMoreCatalogPlaylistAsync();
            Equal(205, f.Module.Songs.Count);
            p = f.Client.Last("music.catalog.playlist");
            Equal(3, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(200, p.GetProperty("continuation").GetProperty("offset").GetInt32());
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
            True(!p.TryGetProperty("continuation", out _), "refresh starts from the first page");
        });
        yield return Case("unused snapshot field does not block the actual next page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            var first = f.Module.Songs[0];
            f.Client.Transform("music.catalog.playlist", value => Edit(value, node => node["snapshot"] = new string('b', 64)));
            await f.Module.CatalogPageAsync(1);
            Equal(first.Id, f.Module.Songs[0].Id);
            Equal(200, f.Module.Songs.Count); Equal("第 2 页", f.Module.CatalogPageText);
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
        yield return Case("repeated cursor stops further reads while retaining returned content", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.home", value => value);
            f.Client.Transform("music.catalog.home", value => Edit(value, node => {
                node["more"] = true; node["continuation"] = JsonSerializer.SerializeToNode(new { cursor = "1", scope = "different", target = "wy:home" });
            }));
            await f.Module.NavigateAsync("catalog-home", default);
            var before = f.Module.CatalogSections.ToArray();
            await f.Module.HomeInitialization;
            True(f.Module.CatalogSections.SelectMany(section => section.Items).Count() >= before.Sum(section => section.Items.Count),
                "a stalled continuation never discards already returned cards");
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
            Equal(48, f.Module.ChannelTabs.Count);
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
            var window = DesktopTestUi.CreateWindow(page, 900, 700);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                var grid = (ListView)page.FindName("SongsGrid");
                Equal(Visibility.Visible, grid.Visibility);
                True(!Descendants<MusicCatalogView>(page).Any(),
                    "inactive catalog is unmounted, not a second hidden content surface");
                True(((MeowFilm.Desktop.IDesktopHeaderSource)page).Header.Slot is null,
                    "playlist detail does not publish the home-only create control");
                var visibleLabels = Descendants<Button>(page).Where(button => DesktopVisual.IsVisible(button))
                    .Select(button => AutomationProperties.GetName(button) is { Length: > 0 } name
                        ? name : button.Content?.ToString() ?? "").ToArray();
                True(visibleLabels.Any(label => label.Contains("播放全部", StringComparison.Ordinal)), "playlist primary action");
                True(!visibleLabels.Any(label => label is "上一页" or "下一页"), "playlist has no page-switch toolbar");
                True(!Descendants<TextBlock>(page).Any(text => DesktopVisual.IsVisible(text) &&
                    (text.Text == "评论" || text.Text == "收藏者")),
                    "only the song section is present");
                var bar = Descendants<ScrollBar>(grid).Single(candidate =>
                    candidate.Orientation == Orientation.Vertical && DesktopVisual.IsVisible(candidate) &&
                    candidate.Maximum > candidate.Minimum);
                var scroll = Ancestor<ScrollViewer>(bar) ??
                    throw new InvalidOperationException("playlist scrollbar has no owning scroll viewer");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain(); scroll.UpdateLayout();
                Equal(1, f.Client.Count("music.catalog.playlist"));
                await page.ExecuteSongKeyAsync(VirtualKey.End);
                for (var i = 0; i < 200 && (f.Client.Count("music.catalog.playlist") < 2 || f.Module.Busy); i++)
                { await Task.Delay(10); await Drain(); }
                Equal(2, f.Client.Count("music.catalog.playlist"));
                Equal(200, f.Module.Songs.Count);
                await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                DesktopTestUi.WindowRoot(window).UpdateLayout(); scroll.ChangeView(null, 0, null, true); await Drain();
                double widthBefore = grid.ActualWidth;
                window.Resize(1100, DesktopTestUi.WindowSize(window).Height);
                await Until(() => grid.ActualWidth > widthBefore);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                True(grid.ItemsPanelRoot is ItemsStackPanel, "song rows use the actual native recycling panel");
                var first = (ListViewItem)grid.ContainerFromIndex(0);
                if (first is not null)
                    True(Descendants<TextBlock>(first).Any(text => text.Text == f.Module.Songs[0].Title),
                        "resizing retains the actual first song content");
                scroll.ChangeView(null, 0, null, true); await Drain();
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                await page.ExecuteSongKeyAsync(VirtualKey.End);
                await Until(() => f.Client.Count("music.catalog.playlist") == 3 &&
                    f.Module.Songs.Count == 205 && !f.Module.Busy);
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
                var recording = card.Song!.Raw.GetProperty("sources").EnumerateArray()
                    .Single(source => source.GetProperty("source").GetString() == platform);
                Equal(platform, recording.GetProperty("musicInfo").GetProperty("source").GetString());
                Equal("1001", recording.GetProperty("musicInfo").GetProperty("songmid").GetString());
                True(Guid.TryParseExact(card.Song.Id, "D", out _), "the requested source belongs to a permanent native song");
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
            JsonElement reply = default;
            f.Client.Transform("music.catalog.home", value => { reply = value; return value; });
            await f.Module.NavigateAsync("catalog-home", default);
            var daily = reply.GetProperty("sections").EnumerateArray()
                .SelectMany(section => section.GetProperty("items").EnumerateArray())
                .Single(item => MusicCatalogDto.Text(item, "kind") == "daily");
            True(daily.GetProperty("requiresAccount").GetBoolean() && !daily.GetProperty("display").GetBoolean() &&
                !daily.GetProperty("available").GetBoolean() && daily.GetProperty("cover").GetString() == privateCover &&
                daily.GetProperty("errorCode").GetString() == "AUTH_REQUIRED", "provider account and action denial remain intact");
            var view = new MusicCatalogView { DataContext = f.Module };
            var loader = new CatalogArtworkLoader();
            ArtworkScope.SetLoader(view, loader);
            var window = DesktopTestUi.CreateWindow(view, 900, 650);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout();
                await Until(() => loader.Addresses.Contains(publicCover), "public home artwork was not requested");
                var buttons = Descendants<Button>(view).ToArray();
                True(!buttons.Any(b => b.Tag is MusicCatalogCard { Kind: "daily" }), "login-only entry not rendered");
                True(!Descendants<TextBlock>(view).Any(t => t.Text == "当前内容需要平台登录"), "no login warning tiles");
                True(!f.Module.DisplayCatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "daily"),
                    "hidden account entries are excluded from the display projection");
                True(buttons.Any(b => b.Tag is MusicCatalogCard { Song: not null } && b.IsEnabled), "nested song actual WinUI card enabled");
                True(!loader.Addresses.Contains(privateCover), "hidden account artwork is never requested");
                Equal(0, f.Client.Count("music.catalog.playlist"));
                Equal(0, f.Client.Count("music.catalog.daily"));
            }
            finally { window.Close(); }
        });
        yield return Case("song template loads its real cover and uses reference typography", async () =>
        {
            const string address = "https://art.example.test/song.jpg";
            var view = new MusicCatalogView();
            await using var store = new MusicOfficialAudioTests.MemoryStore();
            await using var library = new MeowFilm.Capabilities.Library.MusicLibraryOwner(MusicTestStores.For(store));
            var song = await MusicSongFixture.AdoptAsync(library,
                MusicSongFixture.Online("wy", "1", "测试歌曲 Song", "测试歌手 / Artist", cover: address));
            var songRow = SongRow.Read(MusicSongFixture.Element(song));
            var row = new MusicCatalogCard("1", "测试歌曲 Song", "测试歌手 / Artist", address,
                "song", "wy", songRow, ActionId: "1");
            var content = new MusicCatalogCardView { Presentation = MusicCatalogCardLayout.Song, VerticalAlignment = VerticalAlignment.Top };
            content.DataContext = row; view.Content = content;
            var loader = new CatalogArtworkLoader(); ArtworkScope.SetLoader(view, loader);
            var window = DesktopTestUi.CreateWindow(view, 430, 130);
            try
            {
                window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                Equal(1, loader.Addresses.Count); Equal(address, loader.Addresses[0]);
                var image = Descendants<ArtworkImage>(view).Single();
                Equal(songRow.Id, image.DisplayIdentity);
                Equal(50d, image.ActualWidth); Equal(50d, image.ActualHeight);
                True(Descendants<Image>(image).Single().Source is not null, "real artwork control displays acquired pixels");
                var title = Descendants<TextBlock>(view).Single(t => t.Text == row.Title);
                var artist = Descendants<TextBlock>(view).Single(t => t.Text == row.Subtitle);
                AssertDisplayAutoText([title, artist], "actual catalog song title and artist");
                Equal("XamlAutoFontFamily", title.FontFamily.Source);
                Equal(14d, title.FontSize); Equal(20d, title.LineHeight);
                Equal(12d, artist.FontSize); Equal(18d, artist.LineHeight); Equal(2d, artist.Margin.Top);
                Equal(84d, content.ActualHeight); Equal(new CornerRadius(12), ((Border)content.Content).CornerRadius);
                Equal(72d, Descendants<StackPanel>(view).Single(p => p.Children.OfType<Button>().Count() == 2)
                    .Children.OfType<Button>().Sum(b => b.Width));
            }
            finally { window.Close(); }
        });
        yield return Case("reference home templates use measured entry playlist and banner geometry", async () =>
        {
            var row = new MusicCatalogCard("1", "入口标题", "真实描述", "", "playlist", "wy", ActionId: "1");
            foreach (var (type, width, height) in new[] { ("entry", 152d, 200d), ("playlist", 124d, 172d), ("banner", 262d, 262d / 2.56) })
            {
                FrameworkElement card = type == "entry" ? new EntryCard() : new MusicCatalogCardView { Presentation = type == "banner" ? MusicCatalogCardLayout.Banner : MusicCatalogCardLayout.Playlist };
                card.DataContext = row; card.Width = width; card.VerticalAlignment = VerticalAlignment.Top;
                using var host = new DesktopTestUi.Host(card, 400, 500);
                await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
                True(Math.Abs(card.ActualHeight - height) <= 1, type + " reference height " + card.ActualHeight);
            }
            foreach (var (type, expected) in new[] { ("feature-rail", 168.4), ("playlist-rail", 140.333333), ("banner", 423d) })
            {
                var shelf = new Shelf { MinimumCardWidth = 124, DataContext = new MusicCatalogSection("", [row], Layout: type) };
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
                        id = "private-section", title = "账号栏目", layout = "playlist-rail",
                        available = false, errorCode = "AUTH_REQUIRED",
                        requiresAccount = true, display = false,
                        action = new { type = "daily" },
                        items = new[] { new { id = "1", title = "公开歌单", kind = "playlist" } }
                    }
                }
            });
            Equal(1, MusicCatalogDto.Read(reply, "wy", "home", "").Count);
            var visible = Edit(reply, page => page["sections"]![0]!["display"] = true);
            var action = MusicCatalogDto.Read(visible, "wy", "home", "").Single().Action!;
            True(action.RequiresAccount, "section action retains declared account requirements");

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
        public ArtworkPixels Image { get; } = new(1, 1, new byte[] { 90, 120, 180, 255 });
        public void Dispose() { }
    }
    private static async Task WithMountedShelf(Shelf shelf, double width, Action assertion)
    {
        shelf.Width = width;
        var window = DesktopTestUi.CreateWindow(shelf, width, 300);
        try
        {
            window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
            True(shelf.IsLoaded && DesktopVisual.IsVisible(shelf), "geometry fixture is mounted and visible");
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
            Equal(FontWeights.Normal, node.FontWeight);
            Equal(new FontFamily("XamlAutoFontFamily").Source, node.FontFamily.Source);
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
            await DesktopTestUi.DrainAsync();
            if (!predicate()) await Task.Delay(1);
        }
        await Drain();
    }
    private static async Task Drain() { for (int i = 0; i < 3; i++) await DesktopTestUi.DrainAsync(); }
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
    private static MenuFlyout OpenContentMenu(MusicCatalogCardView target) =>
        target.ShowMenu(target, (MusicCatalogCard)target.DataContext)
            ?? throw new InvalidOperationException("Native catalog target exposes no content menu");
    private static Microsoft.UI.Composition.CompositionRoundedRectangleGeometry RoundedClip(FrameworkElement target) =>
        (Microsoft.UI.Composition.CompositionRoundedRectangleGeometry)((Microsoft.UI.Composition.CompositionGeometricClip)
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(target).Clip).Geometry;
    private static string MenuLabel(MenuFlyoutItemBase item) => item switch
    {
        ToggleMenuFlyoutItem choice => choice.Text,
        MenuFlyoutItem action => action.Text,
        MenuFlyoutSubItem submenu => submenu.Text,
        _ => ""
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NativeDatabase store;
        private readonly string root;
        private readonly NativeMusicScriptTestRuntime runtime = new();
        private readonly CatalogTransport transport = new();
        public Client Client { get; }
        public Output Playback { get; } = new();
        public MusicCapability Module { get; }
        public VipApplication? Vip { get; }
        private Fixture(NativeDatabase store, bool vipAccount, string root)
        {
            this.store = store; this.root = root;
            runtime.Platform.OnRequest = transport.Read;
            var native = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            IMusicApplication application = native;
            if (vipAccount) application = Vip = new(native);
            Client = new(application); Module = new(Client, store, Playback);
        }
        public static async Task<Fixture> Create(bool vipAccount = false)
        {
            var data = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Isolated test data required");
            var dir = Path.Combine(data, "catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var store = new NativeDatabase(dir);
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
            var grants = new GrantedMediaFiles(store, [".wav"]);
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
    private sealed class CatalogTransport
    {
        internal readonly JsonObject State = new();
        private int Number(string key, int fallback = 0) => State[key]?.GetValue<int>() ?? fallback;
        private static JsonObject Track(string platform, int id) => MusicSongFixture.Online(platform, id.ToString(),
            "合成曲 " + id, "合成艺人", "合成专辑", 185,
            native: platform == "kg" ? new JsonObject { ["hash"] = id.ToString("X").PadLeft(32, '0') } : null);
        private static object TrackCard(string platform, int id) => new {
            id = id.ToString(), kind = "song", title = "合成曲 " + id, subtitle = "合成艺人", cover = "",
            song = Track(platform, id), action = new { type = "song", id = id.ToString() }
        };
        private static object Playlist(string platform, string id, string name = "完整云歌单", string cover = "https://img.invalid/playlist.jpg") =>
            new { id, name, title = name, source = platform, count = 205, cover, kind = "playlist", action = new { type = "playlist", id } };
        private static object Section(string id, string title, string layout, IEnumerable<object> items) =>
            new { id, title, layout, items = items.ToArray() };
        private static object Channels()
        {
            string[] ids = ("feature,playlist,chart,artist,vip,euro,djR,huayu,qingge,acg," +
                "china,yingshi,rock,music_top_tab_jpop,paobu,music_top_tab_game,yueyu,music_top_tab_sunoai,old_hat,music_top_tab_dance,hFlow,music_top_tab_world,music_top_tab_dj,rnb,chuxing,music_tab_drive,music_top_tab_tipsy,rap,xiangcun,music_top_tab_french,sleep,music_top_tab_light_music,classic,music_top_tab_children,HiFi,sir,ballad,music_top_tab_latin,music_tab_pioneer,funk,landiao,yueqi,study,yinyueju,music_top_tab_sport").Split(',');
            string[] names = ("精选,歌单广场,排行榜,歌手,VIP,欧美,电音,华语,情歌,二次元," +
                "国风,OST,摇滚,日语,跑步,游戏,粤语,AI,经典,舞蹈,K-Pop,全球,慢摇DJ,R&B,出行,驾车,放松,说唱,乡村乐,法语,助眠,轻音乐,古典,儿歌,高音质,爵士,民谣,拉丁,先锋,放克,蓝调,乐器,专注,音乐剧,运动").Split(',');
            ids = [.. ids, "music_top_tab_acg", "newsong", "origin"];
            names = [.. names, "二次元", "新歌", "原创"];
            return new { platform = "wy", items = ids.Select((id, index) => new {
                id, name = names[index], kind = "channel", channelKind = id == "vip" ? "vip" : id == "artist" ? "artists" : "music", available = true,
                pageCode = id == "rock" ? "MUSIC_ROCK" : index < 5 ? "" : "MUSIC_" + id.ToUpperInvariant(),
                placement = index < 10 ? "primary" : "more" }).ToArray() };
        }
        internal Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            string platform = call.Platform, operation = call.Operation;
            var p = call.Parameters;
            int total = Number("total", 205);
            string Text(string key, string fallback = "") => p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
            int Integer(string key, int fallback) => p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : fallback;
            int Cursor(string key = "offset") => p.TryGetProperty("continuation", out var value) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field.GetInt32() : 0;
            object result;
            if (operation is "playlist" or "playlist.complete")
            {
                if (Number("playlistFailure") != 0) throw new MeowFilm.Capabilities.Platforms.MusicPlatformException("UPSTREAM_ERROR");
                int first = operation == "playlist.complete" ? 0 : (Integer("page", 1) - 1) * Integer("limit", 100);
                int count = operation == "playlist.complete" ? total : Math.Min(Integer("limit", 100), Math.Max(0, total - first));
                if (operation == "playlist" && Number("detailLimit") > 0) count = Math.Min(count, Number("detailLimit"));
                result = new { platform, kind = "playlist", id = Text("id"), name = "完整云歌单", cover = "https://img.invalid/playlist.jpg",
                    count = total, total, items = Enumerable.Range(1001 + first, count).Select(id => Track(platform, id)).ToArray(),
                    more = first + count < total, continuation = first + count < total ? new { offset = first + count } : null,
                    source = new { sourceTitle = "完整云歌单", sourceType = "playlist", playlistId = Text("id"), platform } };
            }
            else if (operation == "song") result = Track(platform, int.TryParse(Text("id"), out int id) ? id : 1001);
            else if (operation == "search")
            {
                if (Text("kind") == "playlists")
                {
                    int current = Integer("page", 1), pages = Integer("pages", 1);
                    int first = (current - 1) * Integer("limit", 30), all = Number("searchPlaylistTotal", 1);
                    int count = Math.Min(Integer("limit", 30) * pages, Math.Max(0, all - first));
                    result = new { platform, kind = "playlists", items = Enumerable.Range(500 + first, count)
                        .Select(id => Playlist(platform, id.ToString(), cover: "https://art.example.test/search-playlist.jpg")).ToArray(),
                        more = first + count < all, continuation = first + count < all ? new { page = current + pages } : null };
                }
                else result = new { platform, kind = "songs", items = new[] { Track(platform, 1001) }, more = false, continuation = (object?)null };
            }
            else if (operation == "channels") result = Channels();
            else if (operation == "artist")
            {
                string id = Text("id");
                result = new { platform, id, name = "合成歌手 " + id, cover = "https://img.invalid/artist-" + id + ".jpg",
                    items = Enumerable.Range(4101, 3).Select(i => Track(platform, i)).ToArray(), count = 3, more = false };
            }
            else if (operation == "channel" && Text("id") == "artist")
            {
                int first = Cursor(), pages = Math.Max(1, Number("artistPages", 1));
                string selected = Text("tag", "-1:-1:-1");
                if (selected.Length == 0) selected = "-1:-1:-1";
                int[] chosen = selected.Split(':').Select(int.Parse).ToArray();
                var tags = new List<object>();
                foreach (var (group, values, component) in new[] {
                    ("area", new[] { -1, 7, 96, 8, 16, 0 }, 0), ("type", new[] { -1, 1, 2, 3 }, 1),
                    ("initial", new[] { -1 }.Concat(Enumerable.Range(65, 26)).Append(0).ToArray(), 2) })
                    foreach (int value in values)
                    {
                        int[] next = chosen.ToArray(); next[component] = value;
                        tags.Add(new { id = string.Join(":", next), name = value.ToString(), group, selected = value == chosen[component] });
                    }
                result = new { platform, id = "artist", title = "歌手", tags, selectedTag = selected,
                    sections = new[] { Section("artists", "", "artist-grid", Enumerable.Range(1 + first, 40).Select(id => (object)new {
                        id = id.ToString(), title = "合成歌手 " + id, cover = "https://img.invalid/artist-" + id + ".jpg",
                        kind = "artist", action = new { type = "artist", id = id.ToString() } })) },
                    more = first / 40 + 1 < pages, continuation = first / 40 + 1 < pages ? new { offset = first + 40 } : null };
            }
            else if (operation == "channel" && Text("id") == "vip")
                result = new { platform, id = "vip", title = "VIP", more = false,
                    sections = new[] {
                        Section("vip-songs", "黑胶VIP 每日推荐", "vip-songs", new[] { TrackCard(platform, 3001), TrackCard(platform, 3002) }),
                        Section("vip-ranks", "黑胶VIP 排行榜", "vip-ranked-songs", new[] { 700, 701 }.Select((id, index) => (object)new {
                            id = id.ToString(), title = index == 0 ? "黑胶VIP 热歌榜" : "黑胶SVIP 飙升榜", kind = "playlist",
                            action = new { type = "playlist", id = id.ToString() },
                            preview = new[] { new { title = "会员歌曲", artist = "会员歌手", cover = "", rank = 1, lastRank = index == 0 ? 2 : 0,
                                song = Track(platform, 3001 + index) } } })) } };
            else if (operation == "channel")
            {
                int offset = Cursor(); string tag = Text("tag", "30");
                if (tag.Length == 0) tag = "30";
                if (State["tagFailure"]?.GetValue<bool>() == true && tag == "31") throw new MeowFilm.Capabilities.Platforms.MusicPlatformException("UPSTREAM_ERROR");
                var sections = new List<object>();
                if (offset == 0) sections.Add(Section("banner", "", "banner", new[] { TrackCard(platform, 1001) }));
                sections.Add(Section("feed", "频道歌曲", "song-columns", new[] { TrackCard(platform, 2001 + offset + int.Parse(tag)) }));
                result = new { platform, id = Text("id"), title = "摇滚", sections,
                    tags = new[] { new { id = "30", name = "风格一" }, new { id = "31", name = "风格二" } }, selectedTag = tag,
                    more = offset == 0, continuation = offset == 0 ? new { offset = 1, ruleId = "11", pointIds = new[] { "22" }, algTagIds = tag,
                        tag, scope = "public", target = Text("id") } : null };
            }
            else if (operation == "home")
            {
                if (Number("homeFailure") != 0) throw new MeowFilm.Capabilities.Platforms.MusicPlatformException(
                    Number("homeFailure") == 401 ? "AUTH_REQUIRED" : "UPSTREAM_ERROR");
                int cursor = Cursor("cursor");
                var entries = new List<object>();
                if (Number("detailLimit") > 0)
                {
                    var entry = JsonSerializer.SerializeToNode(Playlist(platform, (600 + cursor).ToString(), "入口歌单"))!.AsObject();
                    entry["presentation"] = "preview";
                    entries.Add(entry);
                }
                entries.Add(new { id = "daily", title = "每日推荐", label = "每日推荐", kind = "daily", cover = "https://art.example.test/private-daily.jpg",
                    action = new { type = "daily", branch = "default" }, presentation = "preview", requiresAccount = true, display = false, available = false, errorCode = "AUTH_REQUIRED" });
                entries.Add(TrackCard(platform, 1001 + cursor));
                result = new { platform, sections = new[] {
                    Section("PLAYLIST", "推荐歌单", "playlist-rail", new[] { Playlist(platform, (Number("homePlaylistStart", 500) + cursor).ToString(), "合成歌单", "https://art.example.test/public-playlist.jpg") }),
                    Section("ENTRANCE", "", "feature-rail", entries) }, more = cursor < Number("homePages", 2) - 1,
                    continuation = cursor < Number("homePages", 2) - 1 ? new { cursor = cursor + 1 } : null };
            }
            else throw new InvalidOperationException("Unexpected canonical catalog operation: " + operation);
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(result));
        }
    }
    private static async Task OnSta(Func<Task> action)
    { await TestUiHost.InvokeAsync(action); }
}
