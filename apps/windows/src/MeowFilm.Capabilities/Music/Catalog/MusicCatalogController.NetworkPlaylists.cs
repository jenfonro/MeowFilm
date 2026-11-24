using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private PlaylistRow? CurrentNetworkPlaylist => IsCatalogPlaylist
        ? libraryPort.NetworkPlaylists.FirstOrDefault(reference => reference.Route == ctx.CurrentRoute) : null;
    public bool ShowsNetworkPlaylistAction => IsCatalogPlaylist &&
        catalogLoadedTarget?.ManagedPlaylist != true &&
        catalogLoadedTarget?.Params.Personal is null;
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
        libraryPort.NetworkPlaylists.FirstOrDefault(reference => reference.Platform == card.Platform && reference.RemoteId == card.ActionId);

    public Task RemoveNetworkPlaylistAsync(string id)
    {
        var reference = libraryPort.NetworkPlaylists.FirstOrDefault(item => item.Id == id);
        return reference is null || !CanEdit ? Task.CompletedTask : RemoveNetworkPlaylistAsync(reference);
    }

    internal Task RemoveNetworkPlaylistAsync(PlaylistRow reference)
    {
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            var receipt = await application.RemoveNetworkPlaylistAsync(reference.Id, reference.Revision, lifetime.Token);
            await libraryPort.RefreshPlaylistsAsync(lifetime.Token);
            if (origin == ctx.LoadGeneration) PublishNetworkPlaylistReceipt(receipt, "已取消收藏");
        });
    }

    internal Task SaveNetworkPlaylistAsync(MusicCatalogCard? entry = null)
    {
        if (entry is null ? !CanSaveNetworkPlaylist :
            !CanCatalogBrowse || !entry.CanSavePlaylist || !ContainsCatalogCard(entry)) return Task.CompletedTask;
        string[] parts = entry is null ? ctx.CurrentRoute.Split('/', 3) : ["catalog-playlist", entry.Platform, Uri.EscapeDataString(entry.ActionId)];
        var platform = parts[1]; var id = Uri.UnescapeDataString(parts[2]);
        string title = entry?.Title ?? libraryPort.Heading, cover = entry?.Cover ?? catalogArtwork;
        int count = entry?.Count ?? (entry is null ? libraryPort.Total : 0);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            await RunAsync(async () =>
            {
                if (!currentAction()) return;
                var receipt = await application.SaveCatalogPlaylistAsync(platform, id, title, cover, count, signal);
                _ = PlaylistRow.FromSummary(receipt.Collection);
                await libraryPort.RefreshPlaylistsAsync(lifetime.Token);
                if (currentAction()) PublishNetworkPlaylistReceipt(receipt, "已收藏歌单");
            });
        }, card: entry);
    }

    private void PublishNetworkPlaylistReceipt(MusicNetworkPlaylistMutationResult receipt, string success)
    {
        MusicDto.Require(receipt.LocalSaved);
        var sync = receipt.Sync;
        bool partial = sync.Phase == "partial";
        if (sync.Ok && !partial) ctx.SetStatus(success);
        else
        {
            var code = partial && !string.IsNullOrEmpty(sync.FirstIssueCode) ? sync.FirstIssueCode! : sync.Code;
            ctx.SetStatus(success + "，平台同步未完成：" + ErrorText(new MusicOperationException(code)));
        }
        NetworkPlaylistBindings();
    }
}
