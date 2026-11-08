using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogTests
{
    private static IEnumerable<TestCase> NextRecoveryCases()
    {
        foreach (string departure in new[] { "collapse", "unload" })
            yield return Case("playlist next preparation retires on view " + departure + " without navigation", async () =>
            {
                await using var f = await CurrentMediaFixture();
                var host = new Grid(); host.Children.Add(f.Module.View);
                var window = new Window { Content = host, Width = 906, Height = 650,
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); window.UpdateLayout(); await Drain();
                    True(f.Module.View.IsLoaded && f.Module.View.IsVisible, "real mounted active music view");
                    var before = await f.Rpc("music.queue.get", new { limit = 100 });
                    var route = f.Module.CaptureNavigationLocation();
                    var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
                    await f.Configure(new { total = 3, nextDetail = 0 });
                    var hold = f.Client.Hold("music.catalog.playlist.prepare");
                    var pending = f.Module.QueueCatalogCardAsync(card);
                    await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (departure == "collapse") f.Module.View.Visibility = Visibility.Collapsed;
                    else host.Children.Remove(f.Module.View);
                    await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    hold.Release(); await pending;
                    Equal(route, f.Module.CaptureNavigationLocation());
                    Equal(0, f.Client.Count("music.catalog.playlist.commit"));
                    Equal(1, f.Client.Count("music.catalog.playlist.discard"));
                    AssertSameQueue(before, await f.Rpc("music.queue.get", new { limit = 100 }));
                    AssertCurrentControls(f, enabled: true);
                }
                finally { f.Client.ReleaseAll(); window.Content = null; window.Close(); }
            });

        foreach (string departure in new[] { "collapse", "unload" })
            yield return Case("playlist retained menu callbacks reject view " + departure + " without navigation", async () =>
            {
                await using var f = await Fixture.Create();
                f.Client.Transform("music.catalog.channel", reply => Edit(reply, node =>
                    node["sections"] = JsonSerializer.SerializeToNode(new[] {
                        new { id = "retained-menu-playlists", title = "频道歌单", type = "playlists", items = new[] {
                            new { id = "500", name = "歌单A", kind = "playlist", action = new { type = "playlist", id = "500" } },
                            new { id = "501", name = "歌单B", kind = "playlist", action = new { type = "playlist", id = "501" } }
                        } }
                    })));
                await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
                var host = new Grid(); host.Children.Add(f.Module.View);
                var window = new Window { Content = host, Width = 1100, Height = 760,
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                ContextMenu? menu = null;
                try
                {
                    window.Show(); window.UpdateLayout(); await Drain();
                    var targets = Descendants<Button>(f.Module.View).Where(button => button.IsLoaded && button.IsVisible &&
                        button.DataContext is MusicCatalogCard { Kind: "playlist" } && button.ContextMenu is not null)
                        .DistinctBy(button => ((MusicCatalogCard)button.DataContext).ActionId).Take(2).ToArray();
                    Equal(2, targets.Length);
                    int openCalls = f.Client.Log.Count;
                    OpenContentMenu(targets[0]); targets[0].ContextMenu.IsOpen = true; await Drain();
                    targets[0].ContextMenu.IsOpen = false;
                    var second = targets[1]; var card = (MusicCatalogCard)second.DataContext;
                    OpenContentMenu(second); menu = second.ContextMenu; menu.IsOpen = true; await Drain();
                    True(menu.IsOpen && second.IsLoaded && second.IsVisible, "B menu really opens on a mounted active target");
                    Equal(openCalls, f.Client.Log.Count);
                    var items = menu.Items.OfType<MenuItem>().ToArray();
                    Equal(4, items.Length); True(items.All(item => item.IsEnabled), "all four callbacks are admitted before leaving");
                    var route = f.Module.CaptureNavigationLocation();
                    var routes = new List<string>(); f.Module.NavigationRequested += (_, e) => routes.Add(e.Route);
                    if (departure == "collapse") f.Module.View.Visibility = Visibility.Collapsed;
                    else host.Children.Remove(f.Module.View);
                    await Until(() => !second.IsLoaded || !second.IsVisible, "menu.target.retired");
                    await Drain();
                    True(ReferenceEquals(second.DataContext, card), "the hidden target is not recycled to a different card");
                    int calls = f.Client.Log.Count;
                    foreach (var item in items) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Drain();
                    Equal(calls, f.Client.Log.Count); Equal(0, routes.Count);
                    Equal(route, f.Module.CaptureNavigationLocation());
                    f.AssertNoScriptRuntime();
                }
                finally { if (menu is not null) menu.IsOpen = false; window.Content = null; window.Close(); }
            });

        foreach (bool changeTab in new[] { true, false })
            yield return Case(changeTab
                ? "playlist next preparation retires when search tab changes"
                : "playlist next preparation survives selecting the same search tab", async () =>
            {
                await using var f = await CurrentMediaFixture();
                await f.SearchPolicy("wy", playlists: true);
                await f.Module.SearchAsync("栏目意图", default); await WaitForSearch(f.Module);
                await f.Module.OpenSearchTabAsync("playlists");
                var card = f.Module.CatalogSections.Single().Items.First();
                var before = await f.Rpc("music.queue.get", new { limit = 100 });
                await f.Configure(new { total = 3, nextDetail = 0 });
                var hold = f.Client.Hold("music.catalog.playlist.prepare");
                var pending = f.Module.QueueCatalogCardAsync(card);
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Module.OpenSearchTabAsync(changeTab ? "overview" : "playlists");
                if (changeTab) await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                else True(!hold.Cancelled.Task.IsCompleted, "unchanged tab does not revoke the intent");
                hold.Release(); await pending;
                Equal(changeTab ? 0 : 1, f.Client.Count("music.catalog.playlist.commit"));
                Equal(1, f.Client.Count("music.catalog.playlist.discard"));
                var after = await f.Rpc("music.queue.get", new { limit = 100 });
                if (changeTab) AssertSameQueue(before, after);
                else
                {
                    Equal(before.GetProperty("currentId").GetString(), after.GetProperty("currentId").GetString());
                    Equal(before.GetProperty("mode").GetInt32(), after.GetProperty("mode").GetInt32());
                    Equal(before.GetProperty("total").GetInt32() + 3, after.GetProperty("total").GetInt32());
                }
                AssertCurrentControls(f, enabled: true);
            });

        foreach (string failure in new[] { "empty stage", "pre-admission cancellation", "pending snapshot", "snapshot read failure" })
            yield return Case("playlist next recovers authoritative playback gates after " + failure, async () =>
            {
                await using var f = await CurrentMediaFixture();
                var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
                var before = await f.Rpc("music.queue.get", new { limit = 100 });
                await f.Configure(new { total = 0, nextDetail = 0 });
                Held? admission = null;
                if (failure == "pre-admission cancellation")
                {
                    // This barrier precedes the native call, unlike Hold's late receipt barrier.
                    var held = new Held(); admission = held;
                    f.Client.BeforeNextCommit = async token => { await held.Wait(default, token); };
                }
                var reconciliation = new Held();
                JsonElement? commitReceipt = null;
                f.Client.Transform("music.catalog.playlist.commit", receipt =>
                {
                    commitReceipt = receipt;
                    // Queue read #1 captures expectedVersion. Only the post-commit
                    // authoritative read is controlled; native prepare/commit stay real.
                    f.Client.HoldReply("music.queue.get", reconciliation, snapshot =>
                    {
                        if (failure == "snapshot read failure")
                            return JsonSerializer.SerializeToElement(new { ok = false, code = "STORAGE_ERROR" });
                        if (failure != "pending snapshot") return snapshot;
                        var node = JsonNode.Parse(snapshot.GetRawText())!;
                        node["persistence"]!["state"] = "pending";
                        return JsonSerializer.SerializeToElement(node);
                    });
                    return receipt;
                });
                var pending = f.Module.QueueCatalogCardAsync(card);
                try
                {
                    if (admission is not null)
                    {
                        await admission.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        f.Module.InvalidateCatalogAction();
                        await admission.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        admission.Release();
                    }
                    await reconciliation.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var receipt = commitReceipt ?? throw new InvalidOperationException("Missing native commit receipt");
                    Equal(false, receipt.GetProperty("ok").GetBoolean());
                    Equal(failure == "pre-admission cancellation" ? "CANCELLED" : "INVALID_REQUEST",
                        receipt.GetProperty("code").GetString());
                    True(!f.Module.CanPlay && !f.Module.CanTogglePlayback && !f.Module.CanSeek,
                        "failure gates controls while reconciliation is pending");
                    True(!reconciliation.Cancelled.Task.IsCompleted, "authoritative recovery uses lifetime, not cancelled action token");
                    reconciliation.Release(); await pending;
                    Equal(1, f.Client.Count("music.catalog.playlist.commit"));
                    Equal(1, f.Client.Count("music.catalog.playlist.discard"));
                    var after = await f.Rpc("music.queue.get", new { limit = 100 });
                    Equal(after.GetProperty("currentId").GetString(), f.Playback.Identity);
                    AssertSameQueue(before, after);
                    AssertCurrentControls(f, enabled: failure is not ("pending snapshot" or "snapshot read failure"));
                }
                finally { admission?.Release(); reconciliation.Release(); f.Client.ReleaseAll(); await pending; }
            });
    }

    private static async Task<Fixture> CurrentMediaFixture()
    {
        var f = await Fixture.Create();
        try
        {
            await f.SetupLocalSongs();
            await f.Module.NavigateAsync("favorites", default);
            f.Module.SelectedSong = f.Module.Songs[0]; await f.Module.PlaySelectionAsync();
            await f.Module.ChangeModeAsync(2);
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
            AssertCurrentControls(f, enabled: true);
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    private static void AssertCurrentControls(Fixture f, bool enabled)
    {
        Equal(enabled, f.Module.CanPlay); Equal(enabled, f.Module.CanTogglePlayback); Equal(enabled, f.Module.CanSeek);
        True(f.Playback.HasMedia, "current media remains owned");
        True(f.Playback.Opens.Single().IsFile, "current media uses a granted native local file");
        f.AssertNoScriptRuntime();
        Equal(1, f.Playback.Opens.Count);
    }

    private static void AssertSameQueue(JsonElement before, JsonElement after)
    {
        foreach (string field in new[] { "currentId", "mode", "version", "entries", "total" })
            Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
    }
}
