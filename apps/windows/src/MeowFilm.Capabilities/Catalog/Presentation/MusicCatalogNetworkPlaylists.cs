using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private PlaylistRow? CurrentNetworkPlaylist => IsCatalogPlaylist
        ? NetworkPlaylists.FirstOrDefault(reference => reference.Route == location.Route) : null;
    public bool ShowsNetworkPlaylistAction => IsCatalogPlaylist &&
        catalogLoadedTarget?.ManagedPlaylist != true &&
        catalogLoadedTarget?.Parameters.TryGetProperty("personal", out _) != true;
    public bool CanSaveNetworkPlaylist => CanCatalogAction && ShowsNetworkPlaylistAction;
    public bool CanToggleNetworkPlaylist => CanEdit && ShowsNetworkPlaylistAction &&
        (NetworkPlaylistSaved || CanSaveNetworkPlaylist);
    public bool NetworkPlaylistSaved => CurrentNetworkPlaylist is not null;
    public string NetworkPlaylistActionText => NetworkPlaylistSaved ? "取消收藏" : "收藏";
    private void NetworkPlaylistBindings()
    {
        foreach (string name in new[] { nameof(ShowsNetworkPlaylistAction), nameof(CanSaveNetworkPlaylist),
            nameof(CanToggleNetworkPlaylist), nameof(NetworkPlaylistSaved), nameof(NetworkPlaylistActionText) }) Changed(name);
    }
    public Task ToggleNetworkPlaylistAsync() => !CanToggleNetworkPlaylist ? Task.CompletedTask :
        CurrentNetworkPlaylist is { } reference ? RemoveNetworkPlaylistAsync(reference.Id) : SaveNetworkPlaylistAsync();
    private PlaylistRow? NetworkPlaylistForCard(MusicCatalogCard card) =>
        NetworkPlaylists.FirstOrDefault(reference => reference.Platform == card.Platform && reference.RemoteId == card.ActionId);
    public Task RemoveNetworkPlaylistAsync(string id)
    {
        var reference = NetworkPlaylists.FirstOrDefault(item => item.Id == id);
        return reference is null || !CanEdit ? Task.CompletedTask : RemoveNetworkPlaylistAsync(reference);
    }
    private Task RemoveNetworkPlaylistAsync(PlaylistRow reference)
    {
        var origin = loadGeneration;
        return RunAsync(async () =>
        {
            var receipt = await CallSettledReceipt("music.library.network.remove",
                new { id = reference.Id, ifRevision = reference.Revision }, lifetime.Token);
            await RefreshPlaylists(lifetime.Token);
            if (origin == loadGeneration) PublishNetworkPlaylistReceipt(receipt, "已取消收藏");
        });
    }
    private Task SaveNetworkPlaylistAsync(MusicCatalogCard? entry = null)
    {
        if (entry is null ? !CanSaveNetworkPlaylist :
            !CanCatalogBrowse || !entry.CanSavePlaylist || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        string[] parts = entry is null ? location.Route.Split('/', 3) : ["catalog-playlist", entry.Platform, Uri.EscapeDataString(entry.ActionId)];
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        string title = entry?.Title ?? heading, cover = entry?.Cover ?? catalogArtwork;
        int count = entry?.Count ?? (entry is null ? total : 0);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            await RunAsync(async () =>
            {
                if (!currentAction()) return;
                var receipt = await CallSettledReceipt("music.catalog.playlist.save",
                    new { platform, id, name = title, cover, count }, signal);
                _ = PlaylistRow.Read(receipt.GetProperty("collection"));
                await RefreshPlaylists(lifetime.Token);
                if (currentAction()) PublishNetworkPlaylistReceipt(receipt, "已收藏歌单");
            });
        }, card: entry);
    }
    private void PublishNetworkPlaylistReceipt(JsonElement receipt, string success)
    {
        MusicDto.Require(receipt.GetProperty("localSaved").ValueKind == JsonValueKind.True);
        var sync = receipt.GetProperty("sync");
        bool partial = sync.TryGetProperty("status", out var state) &&
            MusicCatalogDto.Text(state, "phase") == "partial";
        if (sync.GetProperty("ok").ValueKind == JsonValueKind.True && !partial) status = success;
        else
        {
            var code = partial && state.TryGetProperty("issues", out var issues) && issues.GetArrayLength() > 0
                ? MusicCatalogDto.Text(issues[0], "code") : MusicCatalogDto.Text(sync, "code");
            status = success + "，平台同步未完成：" + ErrorText(new MusicOperationException(code));
        }
        Changed(nameof(Status)); NetworkPlaylistBindings();
    }
}
