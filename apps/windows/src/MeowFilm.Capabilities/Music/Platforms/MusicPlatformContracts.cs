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
        if (platform is not ("wy" or "tx" or "kg") || string.IsNullOrEmpty(scope) ||
            generation < 0 || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(cookie))
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
public enum MusicHeartbeatEntry { Favorites, CurrentSong }

/// <summary>
/// Page is the first native page, Limit the platform page size. Pages &gt; 1 lets
/// an offset recipe span several pages; <see cref="MusicSearchPaging"/> supplies UI defaults.
/// </summary>
public sealed record MusicSearchRequest(string Platform, string Keyword,
    MusicSearchKind Kind = MusicSearchKind.Songs, int Page = 1, int Limit = 30, bool Refresh = false, int Pages = 1);

/// <summary>
/// Platform search adapter: what one "page" means for a platform. Page-numbered platforms
/// send one request per page; offset platforms may cover several pages with one request.
/// This selects the existing UI batch, not an admission limit on catalog requests.
/// </summary>
public sealed record MusicSearchPaging(int PageSize, int PagesPerRequest)
{
    public const int MaxItemsPerRequest = 100;
    public static MusicSearchPaging For(string platform) => platform switch
    {
        "wy" => new(30, MaxItemsPerRequest / 30),
        _ => new(30, 1)
    };
}
public sealed record MusicSongRequest(string Platform, string Id, bool Refresh = false);
public sealed record MusicPlaylistsRequest(string Platform, IReadOnlyList<string> Ids);
public sealed record MusicPlaylistRequest(string Platform, string Id, int Page = 1, int Limit = 100,
    bool Refresh = false, JsonElement? Personal = null, JsonElement? Continuation = null);
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
    string Submode = "", bool Preview = false, string? RuleId = null, string? TabTagId = null,
    IReadOnlyList<string>? UnplaySongIds = null, bool Continuation = false);
public sealed record MusicSimilarRequest(string Platform, IReadOnlyList<string> SourceIds,
    string SourceType = "song", IReadOnlyList<string>? UnplaySongIds = null);
public sealed record MusicHeartbeatContinuation(string PlaylistId, string StartMusicId, string AccountId, long Count);
public sealed record MusicHeartbeatRequest(string Platform, MusicHeartbeatEntry Entry, string? SongId = null,
    string? PlaylistId = null, MusicHeartbeatContinuation? Continuation = null);
public sealed record MusicQueueRecommendationsRequest(string Platform,
    string SeedSongId, int Offset = 0);
public sealed record MusicAlbumRequest(string Platform, string Id, bool Refresh = false);
public sealed record MusicArtistRequest(string Platform, string Id, bool Refresh = false);

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
