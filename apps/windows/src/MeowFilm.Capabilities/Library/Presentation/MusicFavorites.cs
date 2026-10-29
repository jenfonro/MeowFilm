namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private bool currentFavorite;
    private long favoriteRead;
    public bool IsCurrentFavorite => currentFavorite;
    public string CurrentFavoriteLabel => currentFavorite ? "取消收藏当前歌曲" : "收藏当前歌曲";

    private Task RefreshCurrentFavoriteAsync()
    {
        var song = current;
        var generation = ++favoriteRead;
        if (song is null) { PublishFavorite(false); return Task.CompletedTask; }
        var task = Read();
        loadDrains.Add(task);
        _ = Observe();
        return task;
        async Task Observe() { try { await task; } finally { loadDrains.Remove(task); } }
        async Task Read()
        {
            try
            {
                var result = await Call("music.library.membership", new { id = "favorites", song = song.Raw }, lifetime.Token);
                if (!stopping && generation == favoriteRead && ReferenceEquals(song, current))
                    PublishFavorite(result.GetProperty("present").GetBoolean());
            }
            catch
            {
                // A read failure cannot invent a successful favorite. Mutations
                // reread under the command lane and report their actual errors.
                if (!stopping && generation == favoriteRead && ReferenceEquals(song, current)) PublishFavorite(false);
            }
        }
    }
    private void PublishFavorite(bool value)
    {
        currentFavorite = value;
        Changed(nameof(IsCurrentFavorite)); Changed(nameof(CurrentFavoriteLabel));
    }
    public Task ToggleCurrentFavoriteAsync()
    {
        var song = current; var origin = loadGeneration;
        if (song is null || !CanEdit) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var membership = await Call("music.library.membership", new { id = "favorites", song = song.Raw }, lifetime.Token);
            var revision = MusicDto.Integer(membership, "revision");
            if (membership.GetProperty("present").GetBoolean())
                await Call("music.library.removeSong", new { id = "favorites", ifRevision = revision,
                    songId = membership.GetProperty("songId").GetString() }, lifetime.Token);
            else
                await Call("music.library.addSongs", new { id = "favorites", ifRevision = revision, songs = new[] { song.Raw } }, lifetime.Token);
            await RefreshAfterEdit(origin);
            await RefreshCurrentFavoriteAsync();
        });
    }
}
