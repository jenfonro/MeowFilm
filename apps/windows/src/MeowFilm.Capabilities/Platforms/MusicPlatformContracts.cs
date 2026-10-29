// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>Trusted native account-owner port. No RPC, persistence or login is performed by the catalog.</summary>
public interface IMusicPlatformSessionSource
{
    ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken);
    bool IsCurrent(MusicPlatformSession session);
}

/// <summary>Immutable, native-only snapshot. Never serialize or log credentials.</summary>
[DebuggerDisplay("MusicPlatformSession (redacted)")]
public sealed class MusicPlatformSession
{
    public MusicPlatformSession(string platform, string scope, long generation, string userId, string cookie)
    {
        if (platform is not ("wy" or "tx" or "kg") || string.IsNullOrEmpty(scope) || scope.Length > 256 ||
            generation < 0 || string.IsNullOrEmpty(userId) || userId.Length > 64 ||
            string.IsNullOrEmpty(cookie) || cookie.Length > 16384)
            throw new ArgumentException("INVALID_SESSION");
        Platform = platform;
        Scope = scope;
        Generation = generation;
        UserId = userId;
        Cookie = cookie;
    }
    [JsonIgnore] public string Platform { get; }
    [JsonIgnore] public string Scope { get; }
    [JsonIgnore] public long Generation { get; }
    [JsonIgnore] public string UserId { get; }
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string Cookie { get; }
    public override string ToString() => "MusicPlatformSession (redacted)";
}

public enum MusicSearchKind { Songs, Playlists }
public enum MusicPersonalListKind { All, Created, Subscribed }
public enum MusicDailyBranch { Default, Style }
public enum MusicFmMode { Default, Familiar, Explore, Puzzle, Scene }
public enum MusicPersonalPlaylistKind { QqFavorites, KugouOwned }

/// <summary>Account-bound directory locator, not credentials or a transferable authorization token.</summary>
public sealed record MusicPersonalPlaylistLocator(string Scope, MusicPersonalPlaylistKind Kind, string ListId,
    long? Total = null, string? Version = null, string? Name = null);

public sealed record MusicSearchRequest(string Platform, string Keyword,
    MusicSearchKind Kind = MusicSearchKind.Songs, int Page = 1, int Limit = 30, bool Refresh = false);
public sealed record MusicSongRequest(string Platform, string Id, bool Refresh = false);
public sealed record MusicPlaylistRequest(string Platform, string Id, int Page = 1, int Limit = 100,
    string? Snapshot = null, bool Refresh = false, MusicPersonalPlaylistLocator? Personal = null);
public sealed record MusicHomeRequest(string Platform, JsonElement? Continuation = null, bool Refresh = false);
public sealed record MusicChannelsRequest(string Platform, bool Refresh = false);
public sealed record MusicChannelRequest(string Platform, string Id, string? PageCode = null,
    JsonElement? Continuation = null, string? Tag = null, bool Refresh = false);
public sealed record MusicPersonalPlaylistsRequest(string Platform,
    MusicPersonalListKind Kind = MusicPersonalListKind.All, int Page = 1, int Limit = 30,
    JsonElement? Continuation = null, bool Refresh = false);
public sealed record MusicDailyRequest(string Platform = "wy",
    MusicDailyBranch Branch = MusicDailyBranch.Default, bool Refresh = false);
public sealed record MusicFmRequest(string Platform = "wy", MusicFmMode Mode = MusicFmMode.Default,
    string Submode = "", bool Preview = false, string? RuleId = null, string? TabTagId = null);
public sealed record MusicSimilarRequest(string Platform, IReadOnlyList<string> SourceIds);
public sealed record MusicHeartbeatRequest(string Platform, string PlaylistId, string StartMusicId,
    string SongId, bool FromHome = false);
public sealed record MusicQueueRecommendationsRequest(string Platform,
    IReadOnlyList<string> SongIds, string CurrentPlaySongId);
public sealed record MusicAlbumRequest(string Platform, string Id, bool Refresh = false);
public sealed record MusicArtistRequest(string Platform, string Id, bool Refresh = false);

/// <summary>
/// Native-built catalog JSON retains the existing UI's song/card/page shapes.
/// Cookie/session objects never appear in results. Each call is explicitly targeted.
/// Personal batches do not start playback, background polling or cloud writes.
/// </summary>
public interface IMusicPlatformCatalog : IDisposable
{
    Task<JsonElement> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> SongAsync(MusicSongRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> PlaylistAsync(MusicPlaylistRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> HomeAsync(MusicHomeRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> ChannelsAsync(MusicChannelsRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> ChannelAsync(MusicChannelRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> DailyAsync(MusicDailyRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> FmAsync(MusicFmRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> SimilarAsync(MusicSimilarRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> AlbumAsync(MusicAlbumRequest request, CancellationToken cancellationToken = default);
    JsonElement Describe();
    // Account owner invokes this synchronously on logout/change. No I/O; old callbacks are fenced.
    void Invalidate(string? platform = null);
}

public sealed class MusicPlatformException : Exception
{
    internal MusicPlatformException(string code, string? platform = null, string? operation = null)
        : base(code) { Code = code; Platform = platform; Operation = operation; }
    public string Code { get; }
    public string? Platform { get; }
    public string? Operation { get; }
    // Deliberately no inner exception, request URI, response, headers or credentials.
    public override string ToString() => $"{nameof(MusicPlatformException)}: {Code}";
}
