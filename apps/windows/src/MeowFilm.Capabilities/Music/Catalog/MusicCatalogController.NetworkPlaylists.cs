using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private PlaylistRow? CurrentNetworkPlaylist => IsCatalogPlaylist
        ? GetNetworkPlaylists().FirstOrDefault(reference => reference.Route == ctx.CurrentRoute) : null;
    public bool ShowsNetworkPlaylistAction => IsCatalogPlaylist &&
        catalogLoadedTarget?.ManagedPlaylist != true &&
        catalogLoadedTarget?.Parameters.TryGetProperty("personal", out _) != true;
    public bool CanSaveNetworkPlaylist => CanCatalogAction && ShowsNetworkPlaylistAction;
    public bool CanToggleNetworkPlaylist => CanEdit && ShowsNetworkPlaylistAction &&
        (NetworkPlaylistSaved || CanSaveNetworkPlaylist);
    public bool NetworkPlaylistSaved => CurrentNetworkPlaylist is not null;
    public string NetworkPlaylistActionText => NetworkPlaylistSaved ? "取消收藏" : "收藏";

    internal void NetworkPlaylistBindings()
    {
        Changed(nameof(ShowsNetworkPlaylistAction));
        Changed(nameof(CanSaveNetworkPlaylist));
        Changed(nameof(CanToggleNetworkPlaylist));
        Changed(nameof(NetworkPlaylistSaved));
        Changed(nameof(NetworkPlaylistActionText));
    }

    public Task ToggleNetworkPlaylistAsync() => !CanToggleNetworkPlaylist ? Task.CompletedTask :
        CurrentNetworkPlaylist is { } reference ? RemoveNetworkPlaylistAsync(reference.Id) : SaveNetworkPlaylistAsync();

    internal PlaylistRow? NetworkPlaylistForCard(MusicCatalogCard card) =>
        GetNetworkPlaylists().FirstOrDefault(reference => reference.Platform == card.Platform && reference.RemoteId == card.ActionId);

    public Task RemoveNetworkPlaylistAsync(string id)
    {
        var reference = GetNetworkPlaylists().FirstOrDefault(item => item.Id == id);
        return reference is null || !CanEdit ? Task.CompletedTask : RemoveNetworkPlaylistAsync(reference);
    }

    internal Task RemoveNetworkPlaylistAsync(PlaylistRow reference)
    {
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            var receipt = await CallSettledReceipt("music.library.network.remove",
                new { id = reference.Id, ifRevision = reference.Revision }, lifetime.Token);
            await OnRefreshLibraryPlaylistsAsync(lifetime.Token);
            if (origin == ctx.LoadGeneration) PublishNetworkPlaylistReceipt(receipt, "已取消收藏");
        });
    }

    internal Task SaveNetworkPlaylistAsync(MusicCatalogCard? entry = null)
    {
        if (entry is null ? !CanSaveNetworkPlaylist :
            !CanCatalogBrowse || !entry.CanSavePlaylist || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        string[] parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : ["catalog-playlist", entry.Platform, Uri.EscapeDataString(entry.ActionId)];
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        string title = entry?.Title ?? GetLibraryHeading(), cover = entry?.Cover ?? catalogArtwork;
        int count = entry?.Count ?? (entry is null ? GetLibraryTotal() : 0);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            await RunAsync(async () =>
            {
                if (!currentAction()) return;
                var receipt = await CallSettledReceipt("music.catalog.playlist.save",
                    new { platform, id, name = title, cover, count }, signal);
                _ = PlaylistRow.Read(receipt.GetProperty("collection"));
                await OnRefreshLibraryPlaylistsAsync(lifetime.Token);
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
        if (sync.GetProperty("ok").ValueKind == JsonValueKind.True && !partial) ctx.SetStatus(success);
        else
        {
            var code = partial && state.TryGetProperty("issues", out var issues) && issues.GetArrayLength() > 0
                ? MusicCatalogDto.Text(issues[0], "code") : MusicCatalogDto.Text(sync, "code");
            ctx.SetStatus(success + "，平台同步未完成：" + ErrorText(new MusicOperationException(code)));
        }
        NetworkPlaylistBindings();
    }
}
