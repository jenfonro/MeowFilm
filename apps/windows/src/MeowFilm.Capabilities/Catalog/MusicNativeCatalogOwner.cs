using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native catalog orchestration. Its platform and library ports are borrowed.
/// Complete playlist staging never crosses a script process or writes half a
/// remote playlist to the local queue. Favorites persist only a remote reference.
/// </summary>
public sealed class MusicNativeCatalogOwner : IAsyncDisposable, IMusicDailyStylePreferences
{
    private const long MaximumStageBytes = 32 * 1024 * 1024;
    private readonly IMusicPlatformCatalog catalog;
    private readonly MusicLibraryOwner library;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource session = new();
    private TaskCompletionSource drained = Completed();
    private long epoch, reservedBytes;
    private int pending;
    private bool closed;
    private Task? disposal;
    internal Func<MusicSearchRequest, CancellationToken, Task<JsonElement>>? SearchSongs { get; set; }
    private sealed record Stage(string Platform, string Id, string Name,
        JsonElement[] Songs, long Bytes, DateTimeOffset Expires, long Epoch, string HeartbeatAccount);

    public MusicNativeCatalogOwner(IMusicPlatformCatalog catalog, MusicLibraryOwner library, TimeProvider? clock = null)
        => (this.catalog, this.library, this.clock) = (catalog ?? throw new ArgumentNullException(nameof(catalog)),
            library ?? throw new ArgumentNullException(nameof(library)), clock ?? TimeProvider.System);

    public Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default) =>
        DailyStyleOperation((port, token) => port.OpenDailyStyleEditorAsync(token), cancellationToken);

    public Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default) =>
        DailyStyleOperation((port, token) => port.SaveDailyStyleAsync(editor, selection, token), cancellationToken, write: true);

    private async Task<T> DailyStyleOperation<T>(Func<IMusicDailyStylePreferences, CancellationToken, Task<T>> action, CancellationToken token, bool write = false)
    {
        long captured;
        CancellationTokenSource request;
        lock (gate)
        {
            if (closed) throw new MusicPlatformException("CLOSED");
            captured = epoch;
            request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, session.Token);
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            Guard(request.Token, captured);
            if (catalog is not IMusicDailyStylePreferences preferences) throw new MusicPlatformException("UNSUPPORTED");
            var result = await action(preferences, request.Token).ConfigureAwait(false);
            if (!write) Guard(CancellationToken.None, captured);
            return result;
        }
        catch (CatalogFailure error) { throw new MusicPlatformException(error.Code); }
        finally
        {
            request.Dispose();
            lock (gate) { if (--pending == 0) drained.TrySetResult(); }
        }
    }

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? operation = null;
        CancellationTokenSource? deadline = null;
        string? preparedToken = null;
        bool returned = false;
        long captured = 0;
        try
        {
            var input = parameters is JsonElement json ? json.Clone()
                : JsonSerializer.SerializeToElement(parameters ?? new { });
            Check(input.ValueKind == JsonValueKind.Object, "INVALID_REQUEST");
            lock (gate)
            {
                Check(!closed, "CLOSED");
                deadline = new(TimeSpan.FromSeconds(25), clock);
                operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.Token, session.Token, deadline.Token);
                captured = epoch;
                if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var signal = operation.Token;
            Guard(signal, captured);
            long observation = library.BeginSongObservation();
            JsonElement result;
            switch (method)
            {
                case "music.catalog.describe":
                    result = catalog.Describe(); break;
                case "music.catalog.search":
                    var kind = Text(input, "kind", "songs");
                    Check(kind is "songs" or "playlists", "INVALID_REQUEST");
                    var search = new MusicSearchRequest(Platform(input), Text(input, "keyword"),
                        kind == "songs" ? MusicSearchKind.Songs : MusicSearchKind.Playlists,
                        Number(input, "page", 1), Number(input, "limit", 30), Flag(input, "refresh"));
                    result = await (SearchSongs?.Invoke(search, signal) ?? catalog.SearchAsync(search, signal)).ConfigureAwait(false);
                    break;
                case "music.catalog.chorus":
                    if (catalog is not IMusicChorusCatalog chorus) throw new CatalogFailure("UNSUPPORTED");
                    result = await chorus.ChorusAsync(Platform(input), Text(input, "id"), signal).ConfigureAwait(false); break;
                case "music.catalog.song":
                    var song = await catalog.SongAsync(new(Platform(input), Text(input, "id"), Flag(input, "refresh")), signal).ConfigureAwait(false);
                    result = JsonSerializer.SerializeToElement(new { song }); break;
                case "music.catalog.playlist":
                    var playlistPlatform = Platform(input);
                    var personal = Optional(input, "personal");
                    var pageSize = 100;
                    result = await catalog.PlaylistAsync(new(playlistPlatform, Text(input, "id"),
                        Number(input, "page", 1), Number(input, "limit", pageSize),
                        Flag(input, "refresh"), personal, Optional(input, "continuation")), signal).ConfigureAwait(false);
                    break;
                case "music.catalog.home":
                    result = await catalog.HomeAsync(new(Platform(input), Optional(input, "continuation"),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.channels":
                    result = await catalog.ChannelsAsync(new(Platform(input), Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.channel":
                    result = await catalog.ChannelAsync(new(Platform(input), Text(input, "id"),
                        OptionalText(input, "pageCode"), Optional(input, "continuation"), OptionalText(input, "tag", allowEmpty: true),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.personalPlaylists":
                    var personalKind = Text(input, "kind", "all") switch
                    {
                        "all" => MusicPersonalListKind.All, "created" => MusicPersonalListKind.Created,
                        "subscribed" => MusicPersonalListKind.Subscribed, _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    result = await catalog.PersonalPlaylistsAsync(new(Platform(input), personalKind,
                        Number(input, "page", 1), Number(input, "limit", 30),
                        Optional(input, "continuation"), Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.daily":
                    var branch = Text(input, "branch", "default") switch
                    {
                        "default" => MusicDailyBranch.Default, "style" => MusicDailyBranch.Style,
                        _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    result = await catalog.DailyAsync(new(Platform(input), branch, Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.fm":
                    var fmMode = Text(input, "mode", "default") switch
                    {
                        "default" => MusicFmMode.Default, "familiar" => MusicFmMode.Familiar,
                        "explore" => MusicFmMode.Explore, "puzzle" => MusicFmMode.Puzzle,
                        "scene" => MusicFmMode.Scene, _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    result = await catalog.FmAsync(new(Platform(input), fmMode,
                        OptionalText(input, "submode", allowEmpty: true) ?? "", Flag(input, "preview"),
                        OptionalText(input, "ruleId"), OptionalText(input, "tabTagId")), signal).ConfigureAwait(false); break;
                case "music.catalog.similar":
                    var sourceIds = Strings(input, "sourceIds");
                    result = await catalog.SimilarAsync(new(Platform(input), sourceIds, Text(input, "sourceType")), signal).ConfigureAwait(false); break;
                case "music.catalog.heartbeat":
                    var entry = Text(input, "entry") switch
                    {
                        "favorites" => MusicHeartbeatEntry.Favorites, "current" => MusicHeartbeatEntry.CurrentSong,
                        _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    var heartbeatSongId = OptionalText(input, "songId");
                    Check(entry != MusicHeartbeatEntry.CurrentSong || heartbeatSongId is not null && OptionalText(input, "playlistId") is not null, "INVALID_REQUEST");
                    result = await catalog.HeartbeatAsync(new(Platform(input), entry, heartbeatSongId, OptionalText(input, "playlistId")), signal).ConfigureAwait(false); break;
                case "music.catalog.queueRecommendations":
                    result = await catalog.QueueRecommendationsAsync(new(Platform(input), Text(input, "seedSongId"), Number(input, "offset", 0)), signal).ConfigureAwait(false); break;
                case "music.catalog.album":
                    result = await catalog.AlbumAsync(new(Platform(input), Text(input, "id"),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.chart":
                    if (catalog is not IMusicChartCatalog charts) throw new CatalogFailure("UNSUPPORTED");
                    result = await charts.ChartAsync(new(Platform(input), Text(input, "kind"),
                        Text(input, "period", "daily"), input.TryGetProperty("year", out _) ? Number(input, "year") : null,
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.artist":
                    if (catalog is not IMusicArtistCatalog artists) throw new CatalogFailure("UNSUPPORTED");
                    result = await artists.ArtistAsync(new(Platform(input), Text(input, "id"),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.playlist.prepare":
                    result = await PrepareAsync(input, signal, captured).ConfigureAwait(false);
                    preparedToken = Text(result, "token"); break;
                case "music.catalog.playlist.save":
                    var reference = new Dictionary<string, object?>
                    {
                        ["platform"] = Platform(input), ["id"] = Text(input, "id"),
                        ["name"] = Text(input, "name")
                    };
                    if (input.TryGetProperty("cover", out var cover)) reference["cover"] = cover.Clone();
                    if (input.TryGetProperty("count", out var count)) reference["count"] = count.Clone();
                    Guard(signal, captured);
                    result = await library.ExecuteAsync("music.library.network.save", reference, signal).ConfigureAwait(false);
                    if (result.GetProperty("ok").ValueKind != JsonValueKind.True)
                        throw new CatalogFailure(Text(result, "code"));
                    return result;
                case "music.catalog.playlist.commit":
                    result = await CommitAsync(input, signal, captured).ConfigureAwait(false);
                    // Storage admission is final: a cancelled caller must not be
                    // told this committed mutation was rolled back.
                    return Okay(result);
                case "music.catalog.playlist.discard":
                    bool removed;
                    lock (gate) removed = Remove(Text(input, "token"));
                    result = JsonSerializer.SerializeToElement(new { discarded = removed }); break;
                default: throw new CatalogFailure("METHOD_UNAVAILABLE");
            }
            Guard(signal, captured);
            var songShape = method switch
            {
                "music.catalog.describe" or "music.catalog.chorus" or "music.catalog.channels" or
                    "music.catalog.playlist.prepare" or "music.catalog.playlist.discard" => MusicLibraryOwner.PlatformSongShape.None,
                "music.catalog.song" => MusicLibraryOwner.PlatformSongShape.SongDetail,
                "music.catalog.search" => Text(input, "kind", "songs") == "songs"
                    ? MusicLibraryOwner.PlatformSongShape.Page : MusicLibraryOwner.PlatformSongShape.None,
                _ => MusicLibraryOwner.PlatformSongShape.Catalog
            };
            if (method is "music.catalog.playlist" or "music.catalog.search" or "music.catalog.channels" or
                "music.catalog.personalPlaylists" or "music.catalog.daily" or "music.catalog.fm" or
                "music.catalog.similar" or "music.catalog.heartbeat" or "music.catalog.queueRecommendations" or "music.catalog.album")
                Check(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var items) &&
                    items.ValueKind == JsonValueKind.Array, "INVALID_RESPONSE");
            result = await library.AdoptPlatformResultAsync(result, signal,
                authoritative: method != "music.catalog.search" || SearchSongs is null, observation: observation,
                shape: songShape).ConfigureAwait(false);
            Guard(signal, captured);
            var response = Okay(result);
            returned = true;
            return response;
        }
        catch (OperationCanceledException)
        {
            string code;
            lock (gate) code = closed ? "CLOSED" : captured != epoch ? "STALE_SESSION"
                : cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT";
            return Failed(code);
        }
        catch (CatalogFailure error) { return Failed(error.Code); }
        catch (MusicPlatformException error) { return Failed(error.Code); }
        catch (MusicLibraryException error) { return Failed(error.Code); }
        catch { return Failed("INVALID_RESPONSE"); }
        finally
        {
            if (operation is not null)
            {
                operation.Dispose();
                lock (gate)
                {
                    if (!returned && preparedToken is not null) Remove(preparedToken);
                    if (--pending == 0) drained.TrySetResult();
                }
            }
            deadline?.Dispose();
        }
    }

    private async Task<JsonElement> PrepareAsync(JsonElement input, CancellationToken signal, long captured)
    {
        var platform = Platform(input); var id = Text(input, "id");
        long observation = library.BeginSongObservation();
        var result = await catalog.CompletePlaylistAsync(new(platform, id,
            Refresh: Flag(input, "refresh"), Personal: Optional(input, "personal")), signal).ConfigureAwait(false);
        Guard(signal, captured);
        Check(result.GetProperty("items").EnumerateArray().Sum(song =>
            (long)Encoding.UTF8.GetByteCount(song.GetRawText()) + 1) + 2 <= MaximumStageBytes, "LIMIT");
        result = await library.AdoptPlatformResultAsync(result, signal, observation: observation).ConfigureAwait(false);
        Guard(signal, captured);
        var songs = result.GetProperty("items").EnumerateArray().Select(song => song.Clone()).ToArray();
        long bytes = 2 + songs.Sum(song => (long)Encoding.UTF8.GetByteCount(song.GetRawText()) + 1);
        var name = OptionalText(result, "name", allowEmpty: true) ?? "";
        var actualId = Text(result, "id");
        var heartbeatAccount = OptionalText(result, "heartbeatAccountId") ?? "";
        var token = Guid.NewGuid().ToString("D");
        lock (gate)
        {
            Check(!closed && captured == epoch, "STALE_SESSION"); Prune();
            Check(bytes <= MaximumStageBytes && reservedBytes + bytes <= MaximumStageBytes * 2, "LIMIT");
            reservedBytes += bytes;
            stages.Add(token, new(platform, actualId, name, songs, bytes,
                clock.GetUtcNow().AddMinutes(1), captured, heartbeatAccount));
        }
        return JsonSerializer.SerializeToElement(new { token, platform, id = actualId, name, count = songs.Length });
    }

    private async Task<JsonElement> CommitAsync(JsonElement input, CancellationToken signal, long captured)
    {
        var token = Text(input, "token"); var action = Text(input, "action");
        Check(action is "play" or "next", "INVALID_REQUEST");
        var memberId = OptionalText(input, "memberId");
        var songId = OptionalText(input, "songId");
        var occurrence = Number(input, "memberOccurrence", 0);
        int index = 0; long version = 0;
        if (action == "next")
        {
            version = Long(input, "expectedVersion");
            Check(version >= 0, "INVALID_REQUEST");
        }
        else if (action == "play")
        {
            index = Number(input, "index", 0); version = Long(input, "expectedVersion");
            Check(index >= 0 && version >= 0 &&
                occurrence >= 0, "INVALID_REQUEST");
        }
        Stage item;
        Guard(signal, captured);
        lock (gate)
        {
            Prune(); Check(stages.TryGetValue(token, out var found), "CATALOG_TOKEN_EXPIRED"); item = found!;
            Check(item.Epoch == epoch && captured == epoch && !closed, "STALE_SESSION");
            if (action == "next") Check(item.Songs.Length > 0, "INVALID_REQUEST");
            if (action == "play")
            {
                if (memberId is not null || songId is not null)
                {
                    int seen = 0;
                    index = Array.FindIndex(item.Songs, song =>
                        (memberId is not null ? SongJson.NativeId(song) == memberId : Text(song, "songId") == songId) &&
                        seen++ == occurrence);
                }
                Check(index >= 0 && index < item.Songs.Length,
                    "NOT_FOUND");
            }
            stages.Remove(token);
            // Keep bytes reserved throughout an admitted storage transaction.
        }
        try
        {
            var context = JsonSerializer.SerializeToNode(new { sourceTitle = item.Name,
                sourcePlatform = item.Platform, sourceType = "playlist", sourceId = item.Id,
                queueSource = new { id = item.Id, title = item.Name, kind = "playlist", platform = item.Platform } })!.AsObject();
            if (item.HeartbeatAccount.Length > 0)
            {
                context["heartbeatPlaylistId"] = item.Id;
                context["heartbeatAccountId"] = item.HeartbeatAccount;
            }
            var sourceContext = JsonSerializer.SerializeToElement(context);
            var result = action == "next"
                ? await library.AppendQueueAsync(item.Songs, sourceContext, version, signal).ConfigureAwait(false)
                : await library.ReplaceQueueAsync(item.Songs, index, sourceContext, version, signal).ConfigureAwait(false);
            Check(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out _), "INVALID_RESPONSE");
            if (result.GetProperty("ok").ValueKind != JsonValueKind.True)
            {
                var code = Text(result, "code");
                // Library reports CANCELLED only before durable CAS admission.
                if (code == "CANCELLED" && signal.IsCancellationRequested) signal.ThrowIfCancellationRequested();
                throw new CatalogFailure(code);
            }
            return JsonSerializer.SerializeToElement(new { currentId = result.GetProperty("currentId"),
                version = result.GetProperty("version"), mode = result.GetProperty("mode") });
        }
        finally { lock (gate) reservedBytes -= item.Bytes; }
    }

    public void Invalidate(string? platform = null)
    {
        Check(platform is null or "wy" or "tx" or "kg" or "kw" or "mg", "INVALID_REQUEST");
        CancellationTokenSource previous;
        lock (gate)
        {
            if (closed) return;
            epoch++; previous = session; session = new();
            foreach (var key in stages.Keys.ToArray()) Remove(key);
        }
        try { previous.Cancel(); }
        finally { previous.Dispose(); catalog.Invalidate(platform); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true; epoch++;
            foreach (var key in stages.Keys.ToArray()) Remove(key);
            disposal = DisposeCoreAsync(drained.Task);
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task pendingOperations)
    {
        await Task.Yield();
        // Cancellation callbacks belong to borrowed adapters; even a faulty
        // callback cannot skip draining admitted storage or other operations.
        Exception? cancellationFailure = null;
        try { lifetime.Cancel(); } catch (Exception error) { cancellationFailure = error; }
        try { session.Cancel(); } catch (Exception error) { cancellationFailure ??= error; }
        async Task DrainAsync()
        {
            await pendingOperations.ConfigureAwait(false);
            lifetime.Dispose(); session.Dispose();
        }
        // If the bounded wait times out, reclamation still waits for actual
        // completion. The failed Dispose never claims dependencies are safe.
        await DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), clock).ConfigureAwait(false);
        if (cancellationFailure is not null) throw new InvalidOperationException("CATALOG_CLEANUP");
    }
    private void Guard(CancellationToken signal, long captured)
    {
        signal.ThrowIfCancellationRequested();
        lock (gate) { Check(!closed, "CLOSED"); Check(captured == epoch, "STALE_SESSION"); }
    }
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in stages.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) Remove(entry);
    }
    private bool Remove(string token)
    {
        if (!stages.Remove(token, out var item)) return false;
        reservedBytes -= item.Bytes; return true;
    }
    private static TaskCompletionSource Completed() { var task = new TaskCompletionSource(); task.SetResult(); return task; }
    private static JsonElement Failed(string code) => JsonSerializer.SerializeToElement(new
    {
        ok = false,
        code = code.Length is > 0 and <= 64 && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? code : "INVALID_RESPONSE"
    });
    private static JsonElement Okay(JsonElement value)
    {
        Check(value.ValueKind == JsonValueKind.Object &&
            (!value.TryGetProperty("ok", out var ok) || ok.ValueKind == JsonValueKind.True), "INVALID_RESPONSE");
        var result = JsonNode.Parse(value.GetRawText())!.AsObject(); result["ok"] = true;
        return JsonSerializer.SerializeToElement(result);
    }
    private static string[] Strings(JsonElement value, string name)
    {
        Check(value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array &&
            array.GetArrayLength() > 0, "INVALID_REQUEST");
        return array.EnumerateArray().Select(item => StringValue(item)).ToArray();
    }
    private static string StringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!, JsonValueKind.Number => value.GetRawText(),
            _ => throw new CatalogFailure("INVALID_REQUEST")
        };
    }
    private static string Platform(JsonElement value) => Text(value, "platform");
    private static string Text(JsonElement value, string name, string? fallback = null)
    {
        if (!value.TryGetProperty(name, out var field)) return fallback ?? throw new CatalogFailure("INVALID_REQUEST");
        return StringValue(field);
    }
    private static string? OptionalText(JsonElement value, string name, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (allowEmpty && field.ValueKind == JsonValueKind.String && field.GetString() == "") return "";
        return Text(value, name);
    }
    private static JsonElement? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind != JsonValueKind.Null ? found.Clone() : null;
    private static long Long(JsonElement value, string name)
    {
        Check(value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out _), "INVALID_REQUEST");
        return value.GetProperty(name).GetInt64();
    }
    private static int Number(JsonElement value, string name, int? fallback = null)
    {
        if (!value.TryGetProperty(name, out var field)) return fallback ?? throw new CatalogFailure("INVALID_REQUEST");
        Check(field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out _), "INVALID_REQUEST"); return field.GetInt32();
    }
    private static bool Flag(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) return false;
        Check(field.ValueKind is JsonValueKind.True or JsonValueKind.False, "INVALID_REQUEST"); return field.GetBoolean();
    }
    private static void Check(bool condition, string code) { if (!condition) throw new CatalogFailure(code); }
    private sealed class CatalogFailure(string code) : Exception(code) { internal string Code { get; } = code; }
}
