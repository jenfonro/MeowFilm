using MeowFilm.Desktop;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogTests
{
    private static IEnumerable<TestCase> NetworkReferenceCases()
    {
        yield return Case("network references do not consume the managed playlist navigation capacity", async () =>
        {
            await using var f = await Fixture.Create();
            JsonElement ListPage(JsonElement value)
            {
                const int managed = 10_000;
                var reply = JsonNode.Parse(value.GetRawText())!.AsObject();
                int offset = reply["offset"]!.GetValue<int>();
                var items = new JsonArray();
                for (int index = offset; index < Math.Min(offset + 100, managed + 1); index++)
                    items.Add(index == managed
                        ? JsonSerializer.SerializeToNode(new { id = "network:wy:500", name = "Network", kind = "network",
                            revision = 0, count = 1, platform = "wy", remoteId = "500", cover = "", editable = false })
                        : JsonSerializer.SerializeToNode(new { id = index == 0 ? "favorites" : "playlist:" + index,
                            name = index == 0 ? "我的收藏" : "Managed " + index,
                            kind = index == 0 ? "favorites" : "playlist", revision = 0, count = 0 }));
                reply["total"] = managed + 1; reply["items"] = items;
                return JsonSerializer.SerializeToElement(reply);
            }
            for (int page = 0; page < 101; page++) f.Client.Transform("music.library.list", ListPage);
            int reads = f.Client.Count("music.library.list");
            await f.Module.NavigateAsync("favorites", default); await f.Module.RefreshAsync();
            Equal(reads + 101, f.Client.Count("music.library.list"));
            Equal(10_000, f.Module.Playlists.Count);
            Equal("network:wy:500", f.Module.NetworkPlaylists.Single().Id);
            True(f.Module.Navigation.Any(item => item.Route == "catalog-playlist/wy/500" && item.Group == "网络歌单"),
                "the final network reference is published without reducing managed collection capacity");
            Equal("", f.Module.Status);
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("network sidebar uses real remote route and refreshes on every open", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Module.SaveCatalogPlaylistAsync();
            var reference = f.Module.NetworkPlaylists.Single();
            True(f.Module.Navigation.Any(item => item.Route == reference.Route && item.Group == "网络歌单"),
                "saved networks have a separate sidebar group");
            True(!f.Module.Playlists.Any(item => item.Id == reference.Id), "networks are not editable playlist targets");
            await f.Module.NavigateAsync("favorites", default);
            await f.Configure(new { total = 3, nextDetail = 0 });
            int reads = f.Client.Count("music.catalog.playlist");
            await f.Module.OpenPlaylistAsync(reference.Id);
            Equal(reads + 1, f.Client.Count("music.catalog.playlist"));
            Equal("500", f.Client.Last("music.catalog.playlist").GetProperty("id").GetString());
            True(f.Client.Last("music.catalog.playlist").GetProperty("refresh").GetBoolean(), "opening bypasses remote content cache");
            Equal(3, f.Module.Songs.Count);
            True(f.Module.NetworkPlaylistSaved && !f.Module.HasCollection && !f.Module.CanDeleteCollection &&
                !f.Module.CanRemoveSelection && !f.Module.CanReorderPlaylist, "network detail is not a managed content editor");
            await f.Module.NavigateAsync("favorites", default);
            await f.Configure(new { playlistFailure = 503 });
            await f.Module.OpenPlaylistAsync(reference.Id);
            True(f.Module.NetworkPlaylistSaved && f.Module.NetworkPlaylists.Single().Id == reference.Id &&
                f.Module.NetworkPlaylists.Single().RemoteId == "500", "failed refresh retains the original reference");
            Equal(0, f.Module.Playlists.Count(item => item.Kind == "playlist"));
            await f.Module.ToggleNetworkPlaylistAsync();
            Equal(0, f.Module.NetworkPlaylists.Count);
            Equal(1, f.Client.Count("music.library.network.remove"));
            Equal(0, f.Client.Count("music.library.remove"));
        });
        yield return Case("network detail native button toggles reference and exposes only read-only member actions", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            var page = (MusicView)f.Module.View;
            using var source = new DesktopTestUi.Host(new Grid(), 900, 700);
            source.Mount(page);
            page.Measure(new(900, 700)); page.Arrange(new Rect(0, 0, 900, 700)); page.UpdateLayout(); await Drain();
            var button = (Button)page.FindName("NetworkPlaylistButton");
            True(DesktopVisual.IsVisible(button) && button.IsEnabled, "network detail favorite button is mounted");
            Equal("收藏", button.Content); Equal("收藏", AutomationProperties.GetName(button));
            int reads = f.Client.Count("music.catalog.playlist");
            DesktopTestUi.Invoke(button);
            await Until(() => f.Module.NetworkPlaylistSaved && !f.Module.Busy && button.Content as string == "取消收藏");
            Equal(reads, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
            var menu = new MenuFlyout();
            f.Module.PopulateSongMenu(menu, f.Module.Songs[0], null, () => true);
            True(menu.Items.Select(MenuLabel).SequenceEqual(
                new object[] { "播放", "下一首播放", "收藏", "添加到歌单" }), "no member delete, rename or order action");
            var add = menu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "添加到歌单");
            True(add.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(f.Module.Playlists.Select(item => item.Name)),
                "single songs can still be added to managed collections only");
            DesktopTestUi.Invoke(button);
            await Until(() => !f.Module.NetworkPlaylistSaved && !f.Module.Busy && button.Content as string == "收藏");
            Equal(1, f.Client.Count("music.library.network.remove"));
            Equal(0, f.Client.Count("music.library.remove"));
            Equal(0, f.Playback.Opens.Count);
            source.Window.Content = null;
        });
        foreach (bool keepOpen in new[] { true, false })
        {
            yield return Case("network native playlist menu rejects old cancel after same-page cancel and resave: " +
                (keepOpen ? "open" : "retained item"), async () =>
            {
                await using var f = await Fixture.Create();
                await f.Module.NavigateAsync("catalog-home", default);
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                var card = f.Module.CatalogSections.SelectMany(section => section.Items).First(item => item.CanSavePlaylist);
                await f.Module.FavoriteCatalogCardAsync(card);
                var captured = f.Module.NetworkPlaylists.Single();
                var view = new MusicCatalogView { DataContext = f.Module };
                var host = new MusicCatalogCardView { Presentation = keepOpen ? MusicCatalogCardLayout.Playlist : MusicCatalogCardLayout.Row };
                view.Content = host; host.DataContext = card;
                using var source = new DesktopTestUi.Host(new Grid(), 700, 400);
                source.Mount(view);
                view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400)); view.UpdateLayout(); await Drain();
                var menu = OpenContentMenu(host);
                var stale = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "取消收藏");
                True(stale.IsEnabled, "the captured cancel is initially applicable");
                await Until(() => menu.IsOpen);
                if (!keepOpen) { menu.Hide(); await Drain(); }
                try
                {
                    await f.Rpc("music.library.network.remove", new { id = captured.Id, ifRevision = captured.Revision });
                    f.Client.NotifyLibrarySync();
                    await Until(() => !f.Module.Busy && f.Module.NetworkPlaylists.Count == 0);
                    True(f.Module.IsCatalogHome && f.Module.CanCatalogBrowse && f.Module.CanEdit &&
                        ReferenceEquals(host.DataContext, card) &&
                        f.Module.CatalogSections.SelectMany(section => section.Items).Any(item => ReferenceEquals(item, card)),
                        "same-page sync leaves the captured card and its native menu target current");
                    Equal(keepOpen, menu.IsOpen);
                    int calls = f.Client.Log.Count;
                    MusicTestUi.Invoke(stale); await Drain();
                    Equal(calls, f.Client.Log.Count);
                    Equal(0, f.Module.NetworkPlaylists.Count);

                    await f.Module.FavoriteCatalogCardAsync(card); await Drain();
                    var replacement = f.Module.NetworkPlaylists.Single();
                    Equal(captured.Id, replacement.Id);
                    True(replacement.Revision > captured.Revision, "same identity has a new incarnation revision");
                    Equal(keepOpen, menu.IsOpen);
                    Equal("取消收藏", stale.Text);
                    True(stale.IsEnabled && host.IsLoaded && DesktopVisual.IsVisible(host) && f.Module.CanCatalogBrowse && f.Module.CanEdit,
                        "the old native action remains callable on the unchanged idle page");
                    calls = f.Client.Log.Count;
                    MusicTestUi.Invoke(stale); await Drain();
                    Equal(calls, f.Client.Log.Count);
                    Equal(replacement, f.Module.NetworkPlaylists.Single());
                    var persisted = await f.Rpc("music.library.network.get", new { platform = card.Platform, id = card.ActionId });
                    True(persisted.GetProperty("present").GetBoolean(), "old cancel cannot delete the replacement reference");
                    Equal(replacement.Revision, persisted.GetProperty("collection").GetProperty("revision").GetInt64());

                    menu.Hide(); await Drain(); menu = OpenContentMenu(host);
                    var current = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "取消收藏");
                    True(current.IsEnabled, "a newly opened menu can cancel the replacement");
                    await Until(() => menu.IsOpen);
                    int removes = f.Client.Count("music.library.network.remove");
                    MusicTestUi.Invoke(current);
                    await Until(() => !f.Module.Busy && f.Module.NetworkPlaylists.Count == 0);
                    Equal(removes + 1, f.Client.Count("music.library.network.remove"));
                    Equal(replacement.Id, f.Client.Last("music.library.network.remove").GetProperty("id").GetString());
                    Equal(replacement.Revision, f.Client.Last("music.library.network.remove").GetProperty("ifRevision").GetInt64());
                    Equal(0, f.Client.Count("music.library.remove"));
                    Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
                    Equal(0, f.Playback.Opens.Count);
                }
                finally { menu.Hide(); source.Window.Content = null; }
            });
            yield return Case("network native playlist menu never turns old favorite into cancel: " +
                (keepOpen ? "open" : "retained item"), async () =>
            {
                await using var f = await Fixture.Create();
                await f.Module.NavigateAsync("catalog-home", default);
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                var card = f.Module.CatalogSections.SelectMany(section => section.Items).First(item => item.CanSavePlaylist);
                var view = new MusicCatalogView { DataContext = f.Module };
                var host = new MusicCatalogCardView { Presentation = keepOpen ? MusicCatalogCardLayout.Playlist : MusicCatalogCardLayout.Row };
                view.Content = host; host.DataContext = card;
                using var source = new DesktopTestUi.Host(new Grid(), 700, 400);
                source.Mount(view);
                view.Measure(new(700, 400)); view.Arrange(new Rect(0, 0, 700, 400)); view.UpdateLayout(); await Drain();
                var menu = OpenContentMenu(host);
                var stale = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "收藏");
                True(stale.IsEnabled, "the captured favorite is initially applicable");
                await Until(() => menu.IsOpen);
                if (!keepOpen) { menu.Hide(); await Drain(); }
                try
                {
                    await f.Module.FavoriteCatalogCardAsync(card); await Drain();
                    var saved = f.Module.NetworkPlaylists.Single();
                    True(f.Module.IsCatalogHome && f.Module.CanCatalogBrowse && f.Module.CanEdit &&
                        ReferenceEquals(host.DataContext, card) &&
                        f.Module.CatalogSections.SelectMany(section => section.Items).Any(item => ReferenceEquals(item, card)),
                        "the unrelated favorite leaves the original page and card current");
                    Equal(keepOpen, menu.IsOpen);
                    Equal("收藏", stale.Text);
                    True(stale.IsEnabled && host.IsLoaded && DesktopVisual.IsVisible(host), "the old favorite remains callable");
                    int calls = f.Client.Log.Count;
                    MusicTestUi.Invoke(stale); await Drain();
                    Equal(calls, f.Client.Log.Count);
                    Equal(saved, f.Module.NetworkPlaylists.Single());
                    var persisted = await f.Rpc("music.library.network.get", new { platform = card.Platform, id = card.ActionId });
                    True(persisted.GetProperty("present").GetBoolean(), "old favorite cannot reverse into cancellation");
                    Equal(saved.Revision, persisted.GetProperty("collection").GetProperty("revision").GetInt64());

                    menu.Hide(); await Drain(); menu = OpenContentMenu(host);
                    var cancel = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "取消收藏");
                    True(cancel.IsEnabled, "a fresh menu can cancel the saved reference");
                    await Until(() => menu.IsOpen);
                    int removes = f.Client.Count("music.library.network.remove");
                    MusicTestUi.Invoke(cancel);
                    await Until(() => !f.Module.Busy && f.Module.NetworkPlaylists.Count == 0);
                    Equal(removes + 1, f.Client.Count("music.library.network.remove"));
                    Equal(saved.Revision, f.Client.Last("music.library.network.remove").GetProperty("ifRevision").GetInt64());
                    menu.Hide(); await Drain(); menu = OpenContentMenu(host);
                    var favorite = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "收藏");
                    True(favorite.IsEnabled, "a fresh menu can save an absent reference");
                    await Until(() => menu.IsOpen);
                    int saves = f.Client.Count("music.catalog.playlist.save");
                    MusicTestUi.Invoke(favorite);
                    await Until(() => !f.Module.Busy && !card.IsPreparing && f.Module.NetworkPlaylists.Count == 1);
                    Equal(saves + 1, f.Client.Count("music.catalog.playlist.save"));
                    Equal(card.Platform, f.Client.Last("music.catalog.playlist.save").GetProperty("platform").GetString());
                    Equal(card.ActionId, f.Client.Last("music.catalog.playlist.save").GetProperty("id").GetString());
                    Equal(0, f.Client.Count("music.library.remove"));
                    Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
                    Equal(0, f.Playback.Opens.Count);
                }
                finally { menu.Hide(); source.Window.Content = null; }
            });
        }
        yield return Case("network reference keeps full playback single-song favorite and next actions", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Module.SaveCatalogPlaylistAsync(); var reference = f.Module.NetworkPlaylists.Single();
            await f.Module.NavigateAsync("favorites", default);
            await f.Configure(new { nextDetail = 0 });
            await f.Module.OpenPlaylistAsync(reference.Id);
            True(f.Module.CanCatalogAction, "the refreshed network detail is ready before playback");
            await f.SetupSource(); await f.Configure(new { nextDetail = 0 });
            await f.Module.PlayCatalogPlaylistAsync();
            Equal(205, (await f.Rpc("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
            Equal(1, f.Playback.Opens.Count);
            f.Module.SelectedSong = f.Module.Songs[0]; await f.Module.FavoriteAsync();
            Equal(1, (await f.Rpc("music.library.detail", new { id = "favorites" })).GetProperty("total").GetInt32());
            var menu = new MenuFlyout(); f.Module.PopulateSongMenu(menu, f.Module.Songs[0], null, () => true);
            MusicTestUi.Invoke(menu.Items[1]);
            await Until(() => !f.Module.Busy && f.Client.Count("music.queue.append") == 1);
            Equal(206, (await f.Rpc("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
            Equal(reference.Id, f.Module.NetworkPlaylists.Single().Id);
            Equal(0, f.Module.Playlists.Count(item => item.Kind == "playlist"));
        });
        yield return Case("known account-owned playlist cannot be saved through the network consumer", async () =>
        {
            await using var f = await Fixture.Create();
            f.Client.Transform("music.catalog.home", reply => Edit(reply, page =>
                page["sections"]!.AsArray().Add(JsonSerializer.SerializeToNode(new {
                    id = "owned-fixture", title = "Owned", layout = "playlist-rail", items = new[] {
                        new { id = "500", title = "Owned", kind = "playlist", action = new { type = "playlist", id = "500" }, relationship = "owned", writable = true, count = 205 }
                    }
                }))));
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            var card = f.Module.CatalogSections.Single(section => section.Id == "owned-fixture").Items.Single();
            True(card.IsManagedPlaylist && !card.CanSavePlaylist, "account directory ownership is retained by the consumer");
            var menu = new MenuFlyout(); f.Module.PopulatePlaylistMenu(menu, card, () => true);
            True(!menu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text is "收藏" or "取消收藏"),
                "owned card does not offer network subscription");
            int saves = f.Client.Count("music.catalog.playlist.save");
            await f.Module.FavoriteCatalogCardAsync(card);
            Equal(saves, f.Client.Count("music.catalog.playlist.save"));
            await f.Module.OpenCatalogCardAsync(card);
            True(f.Module.IsCatalogPlaylist && !f.Module.ShowsNetworkPlaylistAction && !f.Module.CanSaveNetworkPlaylist,
                "opening retains known managed ownership");
            await f.Module.SaveCatalogPlaylistAsync();
            Equal(saves, f.Client.Count("music.catalog.playlist.save"));
            Equal(0, f.Module.NetworkPlaylists.Count);
        });
        yield return Case("network local success and official failure are distinct in the consumer", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            f.Client.Transform("music.catalog.playlist.save", value =>
            {
                var receipt = JsonNode.Parse(value.GetRawText())!.AsObject();
                receipt["sync"] = new JsonObject { ["ok"] = false, ["code"] = "SDK_FAILURE" };
                return JsonSerializer.SerializeToElement(receipt);
            });
            await f.Module.SaveCatalogPlaylistAsync();
            True(f.Module.NetworkPlaylistSaved && f.Module.Status.StartsWith("已收藏歌单，平台同步未完成：", StringComparison.Ordinal),
                "local save must not claim official success");
            True(f.Module.Status.Contains("订阅验证失败", StringComparison.Ordinal), "token acquisition failure preserves the native local save");
            Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("network admitted save cancellation awaits receipt and never creates content copy", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            var hold = f.Client.Hold("music.catalog.playlist.save");
            var saving = f.Module.SaveCatalogPlaylistAsync();
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.CancelCatalog(); await Drain();
            True(!saving.IsCompleted, "admitted metadata CAS must settle before reporting completion");
            hold.Release(); await saving;
            Equal(1, f.Module.NetworkPlaylists.Count);
            Equal(0, f.Module.Playlists.Count(item => item.Kind == "playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare"));
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            Equal(0, f.Playback.Opens.Count);
        });
    }
}
