// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>Native model adapter for the shared Node platform owner. No platform HTTP or API cache.</summary>
public sealed partial class MusicPlatformCatalog : IMusicPlatformCatalog, IMusicArtistCatalog,
    IMusicChartCatalog, IMusicChorusCatalog, IMusicDailyStylePreferences
{
    private readonly MusicPlatformClient client;
    private readonly IMusicPlatformSessionSource sessions;
    private bool closed;
    internal MusicPlatformCatalog(MusicPlatformClient client, IMusicPlatformSessionSource sessions) =>
        (this.client, this.sessions) = (client, sessions);
    private Task<JsonElement> Read(string platform, string operation, object parameters, CancellationToken token)
    {
        if (closed) throw new MusicPlatformException("CLOSED");
        return client.ReadAsync(platform, operation, parameters, token);
    }
    public Task<JsonElement> SearchAsync(MusicSearchRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "search", new { p.Keyword, kind = p.Kind == MusicSearchKind.Songs ? "songs" : "playlists",
            p.Page, p.Limit, p.Pages, p.Refresh }, cancellationToken);
    public Task<JsonElement> SongAsync(MusicSongRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "song", new { p.Id, p.Refresh }, cancellationToken);
    public Task<JsonElement> PlaylistsAsync(MusicPlaylistsRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "playlists", new { p.Ids }, cancellationToken);
    public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "playlist", new { p.Id, p.Page, p.Limit, p.Refresh, p.Personal, p.Continuation }, cancellationToken);
    public Task<JsonElement> CompletePlaylistAsync(MusicPlaylistRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "playlist.complete", new { p.Id, p.Refresh, p.Personal }, cancellationToken);
    public Task<JsonElement> HomeAsync(MusicHomeRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "home", new { p.Continuation, p.Refresh }, cancellationToken);
    public Task<JsonElement> ChannelsAsync(MusicChannelsRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "channels", new { p.Refresh }, cancellationToken);
    public Task<JsonElement> ChannelAsync(MusicChannelRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "channel", new { p.Id, p.PageCode, p.Continuation, p.Tag, p.Refresh }, cancellationToken);
    public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "personalPlaylists", new { kind = p.Kind.ToString().ToLowerInvariant(), p.Page, p.Limit,
            p.Continuation, p.Refresh }, cancellationToken);
    public Task<JsonElement> DailyAsync(MusicDailyRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "daily", new { branch = p.Branch.ToString().ToLowerInvariant(), p.Refresh }, cancellationToken);
    public Task<JsonElement> FmAsync(MusicFmRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "fm", new { mode = p.Mode.ToString().ToLowerInvariant(), p.Submode, p.Preview,
            p.RuleId, p.TabTagId, p.UnplaySongIds }, cancellationToken);
    public Task<JsonElement> SimilarAsync(MusicSimilarRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "similar", new { p.SourceIds, p.SourceType, p.UnplaySongIds }, cancellationToken);
    public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "heartbeat", new { entry = p.Entry == MusicHeartbeatEntry.Favorites ? "favorites" : "current",
            p.SongId, p.PlaylistId, p.Continuation }, cancellationToken);
    public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "queueRecommendations", new { p.SeedSongId, p.Offset }, cancellationToken);
    public Task<JsonElement> AlbumAsync(MusicAlbumRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "album", new { p.Id, p.Refresh }, cancellationToken);
    public Task<JsonElement> ArtistAsync(MusicArtistRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "artist", new { p.Id, p.Refresh }, cancellationToken);
    public Task<JsonElement> ChartAsync(MusicChartRequest p, CancellationToken cancellationToken = default) =>
        Read(p.Platform, "charts", new { p.Kind, p.Period, p.Year, p.Refresh }, cancellationToken);
    public Task<JsonElement> ChorusAsync(string platform, string id, CancellationToken token = default) =>
        Read(platform, "chorus", new { id }, token);
    internal Task DrainAsync() => client.DrainAsync();
    public void Invalidate(string? platform = null) => client.Invalidate(platform);
    public void Dispose() { if (closed) return; closed = true; client.Invalidate(); }
    public JsonElement Describe() => JsonSerializer.SerializeToElement(new
    { version = "1.0.0", execution = "node", contract = "music.platform.v1", platforms = new
        {
            wy = new { search = new[] { "songs", "playlists" }, song = true, playlist = true, home = true,
                channels = true, personalPlaylists = true, daily = true, dailyStyle = true, fm = true, similar = true, artist = true },
            tx = new { search = new[] { "songs", "playlists" }, song = true, playlist = true, home = false,
                channels = false, personalPlaylists = true },
            kg = new { search = new[] { "songs", "playlists" }, song = true, playlist = true, home = true,
                channels = true, personalPlaylists = true },
            kw = new { search = new[] { "songs", "playlists" }, song = true, playlist = true, home = false,
                channels = false, personalPlaylists = false, accounts = false },
            mg = new { search = new[] { "songs", "playlists" }, song = true, playlist = true, home = false,
                channels = false, personalPlaylists = false, accounts = false }
        } });
}
