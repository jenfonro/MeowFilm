using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native catalog orchestration. Its platform and library ports are borrowed.
/// Complete playlist staging never crosses a script process or writes half a
/// remote playlist to the local queue/library.
/// </summary>
public sealed class MusicNativeCatalogOwner : IAsyncDisposable
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
    internal Action<IEnumerable<JsonObject>>? SongsObserved { get; set; }
    internal Func<JsonElement, JsonElement>? SongFactsProjection { get; set; }
    internal Func<JsonElement, JsonElement>? KnownSongFactsProjection { get; set; }
    internal Func<CancellationToken, Task>? PrepareSongFacts { get; set; }
    internal Func<MusicSearchRequest, CancellationToken, Task<JsonElement>>? SearchSongs { get; set; }
    private sealed record Stage(string Platform, string Id, string Name, string Snapshot,
        JsonElement[] Songs, string[] Members, long Bytes, DateTimeOffset Expires, long Epoch, string HeartbeatAccount);
    private static string NativeId(JsonElement song)
    {
        var id = SongJson.NativeId(song);
        Check(id.Length > 0, "IDENTITY_MISMATCH");
        return id;
    }

    public MusicNativeCatalogOwner(IMusicPlatformCatalog catalog, MusicLibraryOwner library, TimeProvider? clock = null)
        => (this.catalog, this.library, this.clock) = (catalog ?? throw new ArgumentNullException(nameof(catalog)),
            library ?? throw new ArgumentNullException(nameof(library)), clock ?? TimeProvider.System);

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
            if (PrepareSongFacts is { } prepareFacts) await prepareFacts(signal).ConfigureAwait(false);
            JsonElement result;
            switch (method)
            {
                case "music.catalog.describe":
                    Fields(input, []); result = JsonSerializer.SerializeToElement(new { descriptor = catalog.Describe() }); break;
                case "music.catalog.search":
                    Fields(input, ["platform", "keyword", "kind", "page", "limit", "refresh"], ["platform", "keyword"]);
                    var kind = Text(input, "kind", "songs");
                    Check(kind is "songs" or "playlists", "INVALID_REQUEST");
                    var search = new MusicSearchRequest(Platform(input, publicCatalog: true), Text(input, "keyword"),
                        kind == "songs" ? MusicSearchKind.Songs : MusicSearchKind.Playlists,
                        Number(input, "page", 1), Number(input, "limit", 30), Flag(input, "refresh"));
                    result = await (SearchSongs?.Invoke(search, signal) ?? catalog.SearchAsync(search, signal)).ConfigureAwait(false);
                    break;
                case "music.catalog.chorus":
                    Fields(input, ["platform", "id"], ["platform", "id"]);
                    if (catalog is not IMusicChorusCatalog chorus) throw new CatalogFailure("UNSUPPORTED");
                    result = await chorus.ChorusAsync(Platform(input), Text(input, "id"), signal).ConfigureAwait(false); break;
                case "music.catalog.song":
                    Fields(input, ["platform", "id", "refresh"], ["platform", "id"]);
                    var song = await catalog.SongAsync(new(Platform(input, publicCatalog: true), Text(input, "id"), Flag(input, "refresh")), signal).ConfigureAwait(false);
                    result = JsonSerializer.SerializeToElement(new { song }); break;
                case "music.catalog.playlist":
                    Fields(input, ["platform", "id", "page", "limit", "snapshot", "refresh", "personal"], ["platform", "id"]);
                    var playlistPlatform = Platform(input, publicCatalog: true);
                    var personal = Personal(input, playlistPlatform, Text(input, "id"));
                    var pageSize = personal?.Kind == MusicPersonalPlaylistKind.KugouOwned ? 30 : 100;
                    result = await catalog.PlaylistAsync(new(playlistPlatform, Text(input, "id"),
                        Number(input, "page", 1), Number(input, "limit", pageSize),
                        OptionalText(input, "snapshot"), Flag(input, "refresh"), personal), signal).ConfigureAwait(false);
                    break;
                case "music.catalog.home":
                    Fields(input, ["platform", "continuation", "refresh"], ["platform"]);
                    result = await catalog.HomeAsync(new(Platform(input), Optional(input, "continuation"),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.channels":
                    Fields(input, ["platform", "refresh"], ["platform"]);
                    result = await catalog.ChannelsAsync(new(Platform(input), Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.channel":
                    Fields(input, ["platform", "id", "pageCode", "continuation", "tag", "refresh"], ["platform", "id"]);
                    result = await catalog.ChannelAsync(new(Platform(input), Text(input, "id"),
                        OptionalText(input, "pageCode"), Optional(input, "continuation"), OptionalText(input, "tag", allowEmpty: true),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.personalPlaylists":
                    Fields(input, ["platform", "kind", "page", "limit", "continuation", "refresh"], ["platform"]);
                    var personalKind = Text(input, "kind", "all") switch
                    {
                        "all" => MusicPersonalListKind.All, "created" => MusicPersonalListKind.Created,
                        "subscribed" => MusicPersonalListKind.Subscribed, _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    result = await catalog.PersonalPlaylistsAsync(new(Platform(input), personalKind,
                        Number(input, "page", 1), Number(input, "limit", 30),
                        Optional(input, "continuation"), Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.daily":
                    Fields(input, ["platform", "branch", "refresh"], ["platform"]);
                    var branch = Text(input, "branch", "default") switch
                    {
                        "default" => MusicDailyBranch.Default, "style" => MusicDailyBranch.Style,
                        _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    result = await catalog.DailyAsync(new(Platform(input), branch, Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.fm":
                    Fields(input, ["platform", "mode", "submode", "preview", "ruleId", "tabTagId"], ["platform"]);
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
                    Fields(input, ["platform", "sourceIds", "sourceType"], ["platform", "sourceIds", "sourceType"]);
                    var sourceIds = Strings(input, "sourceIds");
                    Check(sourceIds.Length <= 20, "INVALID_REQUEST");
                    result = await catalog.SimilarAsync(new(Platform(input), sourceIds, Text(input, "sourceType")), signal).ConfigureAwait(false); break;
                case "music.catalog.heartbeat":
                    Fields(input, ["platform", "entry", "songId", "playlistId"], ["platform", "entry"]);
                    var entry = Text(input, "entry") switch
                    {
                        "favorites" => MusicHeartbeatEntry.Favorites, "current" => MusicHeartbeatEntry.CurrentSong,
                        _ => throw new CatalogFailure("INVALID_REQUEST")
                    };
                    var heartbeatSongId = OptionalText(input, "songId");
                    Check(entry != MusicHeartbeatEntry.CurrentSong || heartbeatSongId is not null && OptionalText(input, "playlistId") is not null, "INVALID_REQUEST");
                    result = await catalog.HeartbeatAsync(new(Platform(input), entry, heartbeatSongId, OptionalText(input, "playlistId")), signal).ConfigureAwait(false); break;
                case "music.catalog.queueRecommendations":
                    Fields(input, ["platform", "seedSongId", "offset"], ["platform", "seedSongId"]);
                    result = await catalog.QueueRecommendationsAsync(new(Platform(input), Text(input, "seedSongId"), Number(input, "offset", 0)), signal).ConfigureAwait(false); break;
                case "music.catalog.album":
                    Fields(input, ["platform", "id", "refresh"], ["platform", "id"]);
                    result = await catalog.AlbumAsync(new(Platform(input), Text(input, "id"),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.chart":
                    Fields(input, ["platform", "kind", "period", "year", "refresh"], ["platform", "kind"]);
                    if (catalog is not IMusicChartCatalog charts) throw new CatalogFailure("UNSUPPORTED");
                    result = await charts.ChartAsync(new(Platform(input), Text(input, "kind"),
                        Text(input, "period", "daily"), input.TryGetProperty("year", out _) ? Number(input, "year") : null,
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.artist":
                    Fields(input, ["platform", "id", "refresh"], ["platform", "id"]);
                    if (catalog is not IMusicArtistCatalog artists) throw new CatalogFailure("UNSUPPORTED");
                    result = await artists.ArtistAsync(new(Platform(input), Text(input, "id"),
                        Flag(input, "refresh")), signal).ConfigureAwait(false); break;
                case "music.catalog.playlist.prepare":
                    result = await PrepareAsync(input, signal, captured).ConfigureAwait(false);
                    preparedToken = Text(result, "token"); break;
                case "music.catalog.playlist.commit":
                    result = await CommitAsync(input, signal, captured).ConfigureAwait(false);
                    // Storage admission is final: a cancelled caller must not be
                    // told this committed mutation was rolled back.
                    return Okay(result);
                case "music.catalog.playlist.discard":
                    Fields(input, ["token"]);
                    bool removed;
                    lock (gate) removed = Remove(Text(input, "token"));
                    result = JsonSerializer.SerializeToElement(new { discarded = removed }); break;
                default: throw new CatalogFailure("METHOD_UNAVAILABLE");
            }
            Guard(signal, captured);
            var projection = method == "music.catalog.search" && SearchSongs is not null
                ? KnownSongFactsProjection : SongFactsProjection;
            if (projection is { } project) result = project(result);
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
        Fields(input, ["platform", "id", "refresh", "personal"], ["platform", "id"]);
        var platform = Platform(input, publicCatalog: true); var id = Text(input, "id"); var refresh = Flag(input, "refresh");
        var personal = Personal(input, platform, id);
        int pageSize = personal?.Kind == MusicPersonalPlaylistKind.KugouOwned ? 30 : 100;
        lock (gate)
        {
            Prune();
        }
        long size = 2;
        bool reservationTransferred = false;
        try
        {
            var songs = new List<JsonElement>(); var members = new List<string>(); var sorts = new List<long?>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? snapshot = null, name = null, strength = null; int? total = null;
            bool? memberIds = null;
            string heartbeatAccount = "";
            for (int page = 1; ; page = checked(page + 1))
            {
                Guard(signal, captured);
                var result = await catalog.PlaylistAsync(new(platform, id, page, pageSize, snapshot, refresh, personal), signal).ConfigureAwait(false);
                Guard(signal, captured);
                var identities = ValidatePage(result, platform, id, page, pageSize, snapshot, total,
                    memberIds, personal?.Kind == MusicPersonalPlaylistKind.KugouOwned);
                SongsObserved?.Invoke(result.GetProperty("items").EnumerateArray()
                    .Select(song => NativeMusicSong.Normalize(JsonNode.Parse(song.GetRawText()))));
                snapshot ??= Text(result, "snapshot"); name ??= Text(result, "name"); total ??= Number(result, "total");
                if (page == 1) { strength = OptionalText(result, "snapshotStrength"); memberIds = result.TryGetProperty("memberIds", out _);
                    heartbeatAccount = OptionalText(result, "heartbeatAccountId") ?? ""; }
                Check(Text(result, "name") == name, "SNAPSHOT_CHANGED");
                Check(OptionalText(result, "snapshotStrength") == strength, "SNAPSHOT_CHANGED");
                int memberIndex = 0;
                foreach (var song in result.GetProperty("items").EnumerateArray())
                {
                    var member = identities[memberIndex++];
                    Check(seen.Add(member.Id), "PAGINATION_STALLED");
                    long bytes = Encoding.UTF8.GetByteCount(song.GetRawText()) + (songs.Count == 0 ? 0 : 1);
                    lock (gate)
                    {
                        Check(size + bytes <= MaximumStageBytes && reservedBytes + bytes <= MaximumStageBytes * 2, "LIMIT");
                        reservedBytes += bytes; size += bytes;
                    }
                    songs.Add(song.Clone()); members.Add(member.Id); sorts.Add(member.Sort);
                }
                if (!Flag(result, "more")) break;
            }
            Check(songs.Count == total, "INCOMPLETE_PLAYLIST");
            // These platforms may only expose a metadata/count snapshot. Verify
            // the complete ordered identity list with fresh pages, not just page 1.
            if (platform != "wy")
                for (int page = 1; page <= Math.Max(1, (int)(((long)songs.Count + pageSize - 1) / pageSize)); page++)
                {
                    Guard(signal, captured);
                    var result = await catalog.PlaylistAsync(new(platform, id, page, pageSize, snapshot, true, personal), signal).ConfigureAwait(false);
                    Guard(signal, captured);
                    var identities = ValidatePage(result, platform, id, page, pageSize, snapshot, total,
                        memberIds, personal?.Kind == MusicPersonalPlaylistKind.KugouOwned);
                    Check(Text(result, "name") == name && OptionalText(result, "snapshotStrength") == strength, "SNAPSHOT_CHANGED");
                    int index = (page - 1) * pageSize, offset = 0;
                    foreach (var song in result.GetProperty("items").EnumerateArray())
                    {
                        var member = identities[offset++];
                        Check(member.Id == members[index] && member.Sort == sorts[index] &&
                            NativeId(song) == NativeId(songs[index]), "SNAPSHOT_CHANGED");
                        index++;
                    }
                }
            if (personal?.Kind == MusicPersonalPlaylistKind.KugouOwned)
            {
                // Source order is the stable tie-breaker. Sort only AFTER the
                // complete second scan, never independently inside a page.
                var order = Enumerable.Range(0, songs.Count).OrderBy(i => sorts[i] is null)
                    .ThenBy(i => sorts[i]).ThenBy(i => i).ToArray();
                songs = order.Select(i => songs[i]).ToList();
                members = order.Select(i => members[i]).ToList();
            }
            Guard(signal, captured);
            var token = Guid.NewGuid().ToString("D");
            lock (gate)
            {
                Check(!closed && captured == epoch, "STALE_SESSION"); Prune();
                // Account for array delimiters, including a valid empty playlist.
                Check(reservedBytes + 2 <= MaximumStageBytes * 2, "LIMIT"); reservedBytes += 2;
                stages.Add(token, new(platform, id, name!, snapshot!, songs.ToArray(), members.ToArray(), size,
                    clock.GetUtcNow().AddMinutes(1), captured, heartbeatAccount));
                reservationTransferred = true;
            }
            return JsonSerializer.SerializeToElement(new { token, platform, id, name, count = total, snapshot });
        }
        finally
        {
            lock (gate)
            {
                if (!reservationTransferred) reservedBytes -= size - 2;
            }
        }
    }

    private async Task<JsonElement> CommitAsync(JsonElement input, CancellationToken signal, long captured)
    {
        Fields(input, ["token", "action", "index", "memberId", "expectedVersion", "expectedSongId", "name"], ["token", "action"]);
        var token = Text(input, "token"); var action = Text(input, "action");
        Check(action is "save" or "play" or "next", "INVALID_REQUEST");
        var name = OptionalText(input, "name");
        var memberId = OptionalText(input, "memberId");
        Check(name is null || name.Length <= 200, "INVALID_REQUEST");
        int index = 0; long version = 0;
        if (action == "next")
        {
            Fields(input, ["token", "action", "expectedVersion"], ["token", "action", "expectedVersion"]);
            version = Long(input, "expectedVersion");
            Check(version >= 0, "INVALID_REQUEST");
        }
        else if (action == "play")
        {
            index = Number(input, "index", 0); version = Long(input, "expectedVersion");
            Check(index >= 0 && version >= 0 && !input.TryGetProperty("name", out _) &&
                (memberId is null || !input.TryGetProperty("index", out _)), "INVALID_REQUEST");
        }
        else Check(!input.TryGetProperty("index", out _) && !input.TryGetProperty("expectedVersion", out _) &&
            !input.TryGetProperty("expectedSongId", out _) && !input.TryGetProperty("memberId", out _), "INVALID_REQUEST");
        Stage item;
        Guard(signal, captured);
        lock (gate)
        {
            Prune(); Check(stages.TryGetValue(token, out var found), "CATALOG_TOKEN_EXPIRED"); item = found!;
            Check(item.Epoch == epoch && captured == epoch && !closed, "STALE_SESSION");
            if (action == "next") Check(item.Songs.Length > 0, "INVALID_REQUEST");
            if (action == "play")
            {
                if (memberId is not null) index = Array.IndexOf(item.Members, memberId);
                else if (!input.TryGetProperty("index", out _) && input.TryGetProperty("expectedSongId", out _))
                    index = Array.FindIndex(item.Songs, song => Text(song, "songId") == Text(input, "expectedSongId"));
                Check(index >= 0 && index < item.Songs.Length,
                    memberId is null && !input.TryGetProperty("expectedSongId", out _) ? "INVALID_REQUEST" : "SNAPSHOT_CHANGED");
                Check(!input.TryGetProperty("expectedSongId", out _) ||
                    Text(input, "expectedSongId") == Text(item.Songs[index], "songId"), "SNAPSHOT_CHANGED");
            }
            stages.Remove(token);
            // Keep bytes reserved throughout an admitted storage transaction.
        }
        try
        {
            JsonElement result;
            if (action == "save")
                result = await library.CreatePlaylistAsync(name ?? item.Name, item.Songs, signal).ConfigureAwait(false);
            else
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
                result = action == "next"
                    ? await library.AppendQueueAsync(item.Songs, sourceContext, version, signal).ConfigureAwait(false)
                    : await library.ReplaceQueueAsync(item.Songs, index, sourceContext, version, signal).ConfigureAwait(false);
            }
            Check(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out _), "INVALID_RESPONSE");
            if (result.GetProperty("ok").ValueKind != JsonValueKind.True)
            {
                var code = Text(result, "code");
                // Library reports CANCELLED only before durable CAS admission.
                if (code == "CANCELLED" && signal.IsCancellationRequested) signal.ThrowIfCancellationRequested();
                throw new CatalogFailure(code);
            }
            return action == "save" ? JsonSerializer.SerializeToElement(new { collection = result.GetProperty("collection") })
                : JsonSerializer.SerializeToElement(new { currentId = result.GetProperty("currentId"),
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
    private sealed record Member(string Id, long? Sort);
    private static Member[] ValidatePage(JsonElement value, string platform, string id, int page, int pageSize,
        string? snapshot, int? total, bool? expectedMembers, bool requireMembers)
    {
        Check(Text(value, "platform") == platform && Text(value, "id") == id, "IDENTITY_MISMATCH");
        Check(Number(value, "page") == page && Number(value, "limit") == pageSize, "INVALID_RESPONSE");
        int count = Number(value, "total"); Check(count >= 0, "INVALID_RESPONSE");
        string current = Text(value, "snapshot");
        Check(current.Length == 64 && current.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_RESPONSE");
        Check((snapshot is null || snapshot == current) && (total is null || total == count), "SNAPSHOT_CHANGED");
        var items = value.GetProperty("items");
        Check(items.ValueKind == JsonValueKind.Array &&
            items.GetArrayLength() == Math.Min(pageSize, Math.Max(0L, count - (page - 1L) * pageSize)),
            "INCOMPLETE_PLAYLIST");
        bool more = Flag(value, "more");
        Check(value.TryGetProperty("more", out _) && more == (page * (long)pageSize < count), "INCOMPLETE_PLAYLIST");
        if (more) Check(Number(value.GetProperty("continuation"), "page") == checked(page + 1), "PAGINATION_STALLED");
        else Check(!value.TryGetProperty("continuation", out var next) || next.ValueKind == JsonValueKind.Null, "PAGINATION_STALLED");
        bool hasMembers = value.TryGetProperty("memberIds", out var declaredMembers);
        Check((!requireMembers || hasMembers) && (expectedMembers is null || expectedMembers == hasMembers), "IDENTITY_MISMATCH");
        if (hasMembers) Check(declaredMembers.ValueKind == JsonValueKind.Array &&
            declaredMembers.GetArrayLength() == items.GetArrayLength(), "IDENTITY_MISMATCH");
        bool hasSorts = value.TryGetProperty("memberSorts", out var declaredSorts);
        Check(!requireMembers || hasSorts, "IDENTITY_MISMATCH");
        if (hasSorts) Check(hasMembers && declaredSorts.ValueKind == JsonValueKind.Array &&
            declaredSorts.GetArrayLength() == items.GetArrayLength(), "IDENTITY_MISMATCH");
        var identities = new List<Member>();
        foreach (var song in items.EnumerateArray())
        {
            _ = Text(song, "songId");
            var nativeId = NativeId(song);
            Check(SongJson.Platform(song) == platform, "IDENTITY_MISMATCH");
            string member = hasMembers ? StringValue(declaredMembers[identities.Count], 256) : nativeId;
            long? sort = null;
            if (hasSorts && declaredSorts[identities.Count].ValueKind != JsonValueKind.Null)
            {
                var s = declaredSorts[identities.Count];
                Check(s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out _), "INVALID_RESPONSE");
                sort = s.GetInt64();
            }
            identities.Add(new(member, sort));
        }
        return identities.ToArray();
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
    private static MusicPersonalPlaylistLocator? Personal(JsonElement input, string platform, string id)
    {
        if (!input.TryGetProperty("personal", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        Fields(value, ["scope", "kind", "listId", "total", "version", "name", "pageSize",
            "playlistId", "dirId", "listType", "globalCollectionId", "source", "listVersion"],
            ["scope", "kind", "listId"]);
        string scope = Text(value, "scope"), listId = Text(value, "listId");
        Check(scope.Length == 64 && scope.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_REQUEST");
        var kind = Text(value, "kind") switch
        {
            "qq-favorites" => MusicPersonalPlaylistKind.QqFavorites,
            "kugou-owned" => MusicPersonalPlaylistKind.KugouOwned,
            _ => throw new CatalogFailure("INVALID_REQUEST")
        };
        Check(listId.All(c => c is >= '0' and <= '9') && listId.Length <= 64 &&
            (kind == MusicPersonalPlaylistKind.QqFavorites ? platform == "tx" && id == "favorites" && listId == "201"
                : platform == "kg" && id == "0:" + listId), "IDENTITY_MISMATCH");
        if (value.TryGetProperty("pageSize", out _))
            Check(Number(value, "pageSize") == (kind == MusicPersonalPlaylistKind.KugouOwned ? 30 : 100), "INVALID_REQUEST");
        if (kind == MusicPersonalPlaylistKind.QqFavorites)
        {
            Check(!value.TryGetProperty("playlistId", out _) || Text(value, "playlistId") == id, "IDENTITY_MISMATCH");
            Check(!value.TryGetProperty("dirId", out _) || Text(value, "dirId") == listId, "IDENTITY_MISMATCH");
            Check(!new[] { "listType", "globalCollectionId", "source", "listVersion" }.Any(k => value.TryGetProperty(k, out _)), "INVALID_REQUEST");
        }
        else
        {
            Check(!value.TryGetProperty("playlistId", out _) && !value.TryGetProperty("dirId", out _), "INVALID_REQUEST");
            Check(!value.TryGetProperty("listType", out _) || Number(value, "listType") == 0, "IDENTITY_MISMATCH");
            _ = OptionalText(value, "globalCollectionId");
            if (value.TryGetProperty("source", out _)) _ = Number(value, "source");
            if (value.TryGetProperty("listVersion", out var raw) && raw.ValueKind != JsonValueKind.Null)
                Check(raw.ValueKind == JsonValueKind.String && raw.GetString()!.Length <= 2048 ||
                    raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out _), "INVALID_REQUEST");
        }
        long? total = value.TryGetProperty("total", out var count) && count.ValueKind != JsonValueKind.Null ? Long(value, "total") : null;
        Check(total is null or >= 0, "INVALID_REQUEST");
        return new(scope, kind, listId, total, OptionalText(value, "version"), OptionalText(value, "name"));
    }
    private static string[] Strings(JsonElement value, string name)
    {
        Check(value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array &&
            array.GetArrayLength() > 0, "INVALID_REQUEST");
        return array.EnumerateArray().Select(item => StringValue(item, 2048)).ToArray();
    }
    private static string StringValue(JsonElement value, int max)
    {
        Check(value.ValueKind == JsonValueKind.String, "INVALID_REQUEST");
        var text = value.GetString()!;
        Check(!string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl), "INVALID_REQUEST");
        return text;
    }
    private static string Platform(JsonElement value, bool publicCatalog = false)
    {
        var platform = Text(value, "platform");
        Check(platform is "wy" or "tx" or "kg" || publicCatalog && platform is "kw" or "mg", "INVALID_REQUEST");
        return platform;
    }
    private static string Text(JsonElement value, string name, string? fallback = null)
    {
        if (!value.TryGetProperty(name, out var field)) return fallback ?? throw new CatalogFailure("INVALID_REQUEST");
        return StringValue(field, 2048);
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
    private static void Fields(JsonElement input, string[] allowed, string[]? required = null)
    {
        Check(input.ValueKind == JsonValueKind.Object && input.EnumerateObject().All(property => allowed.Contains(property.Name)) &&
            input.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == input.EnumerateObject().Count() &&
            (required ?? allowed).All(name => input.TryGetProperty(name, out _)), "INVALID_REQUEST");
    }
    private static void Check(bool condition, string code) { if (!condition) throw new CatalogFailure(code); }
    private sealed class CatalogFailure(string code) : Exception(code) { internal string Code { get; } = code; }
}
