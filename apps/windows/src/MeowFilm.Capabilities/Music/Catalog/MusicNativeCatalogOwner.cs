using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed record PreparedCatalogPlaylist(string Token, string Platform, string Id, string Name, int Count);

/// <summary>
/// Native catalog orchestration. Its platform and library ports are borrowed.
/// Complete playlist staging never crosses a script process or writes half a
/// remote playlist to the local queue. Favorites persist only a remote reference.
/// </summary>
public sealed class MusicNativeCatalogOwner : IAsyncDisposable
{
    private const int MaxStages = 16;
    private static readonly string[] AllPlatforms = ["wy", "tx", "kg", "kw", "mg"];
    private readonly MusicPlatformCatalog catalog;
    private readonly MusicLibraryOwner library;
    private static TimeProvider clock => TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, PlatformState> platformStates =
        AllPlatforms.ToDictionary(p => p, _ => new PlatformState(), StringComparer.Ordinal);
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource drained = Completed();
    private long stageUseSequence;
    private int pending;
    private bool closed;
    private Task? disposal;

    private sealed class PlatformState
    {
        internal long Epoch;
        internal CancellationTokenSource Session = new();
    }

    private sealed class Stage(string platform, string id, string name,
        MusicSongRecord[] songs, DateTimeOffset expires, long epoch, string heartbeatAccount, long lastUsed)
    {
        internal string Platform { get; } = platform;
        internal string Id { get; } = id;
        internal string Name { get; } = name;
        internal MusicSongRecord[] Songs { get; } = songs;
        internal DateTimeOffset Expires { get; } = expires;
        internal long Epoch { get; } = epoch;
        internal string HeartbeatAccount { get; } = heartbeatAccount;
        internal long LastUsed { get; set; } = lastUsed;
    }

    public MusicNativeCatalogOwner(MusicPlatformCatalog catalog, MusicLibraryOwner library)
        => (this.catalog, this.library) = (catalog ?? throw new ArgumentNullException(nameof(catalog)),
            library ?? throw new ArgumentNullException(nameof(library)));

    private static string StorageCode(StorageException error) => error.Error switch
    {
        StorageError.Unavailable => "STORAGE_UNAVAILABLE",
        StorageError.Corrupt or StorageError.UnsupportedVersion => "CORRUPT_STORAGE",
        _ => "STORAGE"
    };

    private PlatformState StateForLocked(string platform)
    {
        Check(platformStates.TryGetValue(platform, out var state), "INVALID_REQUEST");
        return state!;
    }

    public Task<MusicDailyStyleEditor> OpenDailyStyleEditorAsync(CancellationToken cancellationToken = default) =>
        DailyStyleOperation((port, token) => port.OpenDailyStyleEditorAsync(token), cancellationToken);

    public Task<MusicDailyStyleSaveResult> SaveDailyStyleAsync(MusicDailyStyleEditor editor,
        MusicDailyStyleSelection selection, CancellationToken cancellationToken = default) =>
        DailyStyleOperation((port, token) => port.SaveDailyStyleAsync(editor, selection, token), cancellationToken, write: true);

    private async Task<T> DailyStyleOperation<T>(Func<MusicPlatformCatalog, CancellationToken, Task<T>> action, CancellationToken token, bool write = false)
    {
        const string opPlatform = "wy";
        long captured;
        CancellationTokenSource request;
        lock (gate)
        {
            if (closed) throw new MusicPlatformException("CLOSED");
            var state = StateForLocked(opPlatform);
            captured = state.Epoch;
            request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, state.Session.Token);
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            Guard(request.Token, opPlatform, captured);
            long observation = library.BeginSongObservation();
            var result = await action(catalog, request.Token).ConfigureAwait(false);
            if (!write) Guard(CancellationToken.None, opPlatform, captured);
            if (write && result is MusicDailyStyleSaveResult { Readback: { } readback } receipt)
            {
                try
                {
                    Guard(request.Token, opPlatform, captured);
                    var songs = await library.AdoptPlatformResultAsync(readback, request.Token,
                        observation: observation).ConfigureAwait(false);
                    Guard(request.Token, opPlatform, captured);
                    return (T)(object)(receipt with { Readback = songs });
                }
                catch (OperationCanceledException) { return (T)(object)(receipt with { Readback = null, ErrorCode = "CANCELLED" }); }
                catch (CatalogFailure error) { return (T)(object)(receipt with { Readback = null, ErrorCode = error.Code }); }
                catch (MusicLibraryException error) { return (T)(object)(receipt with { Readback = null, ErrorCode = error.Code }); }
                catch (StorageException error) { return (T)(object)(receipt with { Readback = null, ErrorCode = StorageCode(error) }); }
            }
            return result;
        }
        catch (CatalogFailure error) { throw new MusicPlatformException(error.Code); }
        catch (StorageException error) { throw new MusicPlatformException(StorageCode(error)); }
        finally
        {
            request.Dispose();
            lock (gate) { if (--pending == 0) drained.TrySetResult(); }
        }
    }

    private Task<JsonElement> RunCatalogAsync(
        string opPlatform,
        MusicLibraryOwner.PlatformSongShape songShape,
        Func<CancellationToken, long, Task<JsonElement>> action,
        CancellationToken cancellationToken) =>
        RunCatalogTypedAsync(opPlatform, async (signal, captured) =>
        {
            long observation = library.BeginSongObservation();
            var result = await action(signal, captured).ConfigureAwait(false);
            Guard(signal, opPlatform, captured);
            if (songShape != MusicLibraryOwner.PlatformSongShape.None)
            {
                result = await library.AdoptPlatformResultAsync(result, signal,
                    authoritative: true, observation: observation, shape: songShape).ConfigureAwait(false);
                Guard(signal, opPlatform, captured);
            }
            Check(result.ValueKind == JsonValueKind.Object &&
                (!result.TryGetProperty("ok", out var ok) || ok.ValueKind == JsonValueKind.True), "INVALID_RESPONSE");
            return result;
        }, cancellationToken);

    public Task<JsonElement> ChorusAsync(string platform, string id, CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.None,
            (signal, _) => catalog.ChorusAsync(platform, id, signal), cancellationToken);

    public Task<JsonElement> PlaylistsAsync(string platform, string[] ids, CancellationToken cancellationToken = default)
    {
        Check(ids.Length > 0, "INVALID_REQUEST");
        return RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.None,
            (signal, _) => catalog.PlaylistsAsync(new(platform, ids), signal), cancellationToken);
    }

    public Task<MusicSongRecord> SongAsync(string platform, string id, bool refresh = false, CancellationToken cancellationToken = default) =>
        RunCatalogTypedAsync(platform,
            async (signal, captured) =>
            {
                long observation = library.BeginSongObservation();
                var song = await catalog.SongAsync(new(platform, id, refresh), signal).ConfigureAwait(false);
                Guard(signal, platform, captured);
                var adopted = await library.AdoptSongAsync(MusicNodeSongCodec.ReadSong(song), signal,
                    authoritative: true, observation: observation).ConfigureAwait(false);
                Guard(signal, platform, captured);
                return adopted;
            }, cancellationToken);

    public Task<JsonElement> PlaylistAsync(string platform, string id, int page = 1, int limit = 100, bool refresh = false,
        JsonElement? personal = null, JsonElement? continuation = null, CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.PlaylistAsync(new(platform, id, page, limit, refresh, personal, continuation), signal),
            cancellationToken);

    public Task<JsonElement> HomeAsync(string platform, JsonElement? continuation = null, bool refresh = false,
        CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Catalog,
            (signal, _) => catalog.HomeAsync(new(platform, continuation, refresh), signal), cancellationToken);

    public Task<JsonElement> ChannelsAsync(string platform, bool refresh = false, CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.None,
            (signal, _) => catalog.ChannelsAsync(new(platform, refresh), signal), cancellationToken);

    public Task<JsonElement> ChannelAsync(string platform, string id, string? pageCode = null, JsonElement? continuation = null,
        string? tag = null, bool refresh = false, CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Catalog,
            (signal, _) => catalog.ChannelAsync(new(platform, id, pageCode, continuation, tag, refresh), signal), cancellationToken);

    public Task<JsonElement> PersonalPlaylistsAsync(string platform, string kind = "all", int page = 1, int limit = 30,
        JsonElement? continuation = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var personalKind = kind switch
        {
            "all" => MusicPersonalListKind.All,
            "created" => MusicPersonalListKind.Created,
            "subscribed" => MusicPersonalListKind.Subscribed,
            _ => throw new MusicPlatformException("INVALID_REQUEST")
        };
        return RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.None,
            (signal, _) => catalog.PersonalPlaylistsAsync(new(platform, personalKind, page, limit, continuation, refresh), signal),
            cancellationToken);
    }

    public Task<JsonElement> DailyAsync(string platform, string branch = "default", bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var parsed = branch switch
        {
            "default" => MusicDailyBranch.Default,
            "style" => MusicDailyBranch.Style,
            _ => throw new MusicPlatformException("INVALID_REQUEST")
        };
        return RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.DailyAsync(new(platform, parsed, refresh), signal), cancellationToken);
    }

    public Task<JsonElement> FmAsync(string platform, string mode = "default", string submode = "", bool preview = false,
        string? ruleId = null, string? tabTagId = null, CancellationToken cancellationToken = default)
    {
        var fmMode = mode switch
        {
            "default" => MusicFmMode.Default,
            "familiar" => MusicFmMode.Familiar,
            "explore" => MusicFmMode.Explore,
            "puzzle" => MusicFmMode.Puzzle,
            "scene" => MusicFmMode.Scene,
            _ => throw new MusicPlatformException("INVALID_REQUEST")
        };
        return RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.FmAsync(new(platform, fmMode, submode, preview, ruleId, tabTagId), signal), cancellationToken);
    }

    public Task<JsonElement> SimilarAsync(string platform, string[] sourceIds, string sourceType,
        CancellationToken cancellationToken = default)
    {
        Check(sourceIds.Length > 0, "INVALID_REQUEST");
        return RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.SimilarAsync(new(platform, sourceIds, sourceType), signal), cancellationToken);
    }

    public Task<JsonElement> HeartbeatAsync(string platform, string entry, string? songId = null, string? playlistId = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = entry switch
        {
            "favorites" => MusicHeartbeatEntry.Favorites,
            "current" => MusicHeartbeatEntry.CurrentSong,
            _ => throw new MusicPlatformException("INVALID_REQUEST")
        };
        Check(parsed != MusicHeartbeatEntry.CurrentSong || !string.IsNullOrEmpty(songId) && !string.IsNullOrEmpty(playlistId), "INVALID_REQUEST");
        return RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.HeartbeatAsync(new(platform, parsed, songId, playlistId), signal), cancellationToken);
    }

    public Task<JsonElement> AlbumAsync(string platform, string id, bool refresh = false, CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.AlbumAsync(new(platform, id, refresh), signal), cancellationToken);

    public Task<JsonElement> ChartAsync(string platform, string kind, string period = "daily", int? year = null, bool refresh = false,
        CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Catalog,
            (signal, _) => catalog.ChartAsync(new(platform, kind, period, year, refresh), signal), cancellationToken);

    public Task<JsonElement> ArtistAsync(string platform, string id, bool refresh = false, CancellationToken cancellationToken = default) =>
        RunCatalogAsync(platform, MusicLibraryOwner.PlatformSongShape.Page,
            (signal, _) => catalog.ArtistAsync(new(platform, id, refresh), signal), cancellationToken);

    public Task<PreparedCatalogPlaylist> PreparePlaylistAsync(string platform, string id, bool refresh = false, JsonElement? personal = null,
        CancellationToken cancellationToken = default) =>
        RunCatalogTypedAsync(platform,
            (signal, captured) => PrepareCoreAsync(platform, id, refresh, personal, signal, captured),
            cancellationToken);

    private async Task<T> RunCatalogTypedAsync<T>(
        string? opPlatform,
        Func<CancellationToken, long, Task<T>> action,
        CancellationToken cancellationToken,
        string? stageTokenForLookup = null)
    {
        CancellationTokenSource? operation = null;
        CancellationTokenSource? deadline = null;
        long captured = 0;
        try
        {
            lock (gate)
            {
                Check(!closed, "CLOSED");
                if (stageTokenForLookup is not null)
                {
                    Prune();
                    Check(stages.TryGetValue(stageTokenForLookup, out var found), "CATALOG_TOKEN_EXPIRED");
                    found!.LastUsed = ++stageUseSequence;
                    opPlatform = found.Platform;
                }
                deadline = new(TimeSpan.FromSeconds(25), clock);
                if (opPlatform is not null)
                {
                    var state = StateForLocked(opPlatform);
                    captured = state.Epoch;
                    operation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, lifetime.Token, state.Session.Token, deadline.Token);
                }
                else
                {
                    operation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, lifetime.Token, deadline.Token);
                }
                if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var signal = operation.Token;
            Guard(signal, opPlatform, captured);
            return await action(signal, captured).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            string code;
            lock (gate)
            {
                bool stale = opPlatform is not null && platformStates.TryGetValue(opPlatform, out var st) && captured != st.Epoch;
                code = closed ? "CLOSED" : stale ? "STALE_SESSION"
                    : cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT";
            }
            if (code == "CANCELLED" && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new MusicPlatformException(code);
        }
        catch (CatalogFailure error) { throw new MusicPlatformException(error.Code); }
        catch (MusicPlatformException) { throw; }
        catch (MusicLibraryException error) { throw new MusicPlatformException(error.Code); }
        catch (StorageException error) { throw new MusicPlatformException(StorageCode(error)); }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicCatalog, "owner", "execute_unexpected_error",
                new Dictionary<string, object?> { ["platform"] = opPlatform, ["error"] = error.GetType().Name });
            throw new MusicPlatformException("INVALID_RESPONSE");
        }
        finally
        {
            if (operation is not null)
            {
                operation.Dispose();
                lock (gate) { if (--pending == 0) drained.TrySetResult(); }
            }
            deadline?.Dispose();
        }
    }

    public Task<MusicNetworkPlaylistMutationResult> SavePlaylistAsync(string platform, string id, string name, string? cover = null, long? count = null,
        CancellationToken cancellationToken = default) =>
        RunCatalogTypedAsync(platform,
            async (signal, captured) =>
            {
                Guard(signal, platform, captured);
                return await library.SaveNetworkPlaylistAsync(platform, id, name, cover, count, signal).ConfigureAwait(false);
            }, cancellationToken);

    public Task<MusicQueuePositionResult> CommitPlaylistAsync(string token, string action, long expectedVersion, int index = 0,
        string? memberId = null, string? songId = null, int memberOccurrence = 0, CancellationToken cancellationToken = default) =>
        RunCatalogTypedAsync(null,
            (signal, captured) => CommitCoreAsync(token, action, expectedVersion, index, memberId, songId, memberOccurrence, signal, captured),
            cancellationToken, stageTokenForLookup: token);

    public Task<bool> DiscardPlaylistAsync(string token, CancellationToken cancellationToken = default) =>
        RunCatalogTypedAsync(null,
            (_, _) =>
            {
                bool removed;
                lock (gate) removed = Remove(token);
                return Task.FromResult(removed);
            }, cancellationToken);

    private async Task<PreparedCatalogPlaylist> PrepareCoreAsync(string platform, string id, bool refresh, JsonElement? personal,
        CancellationToken signal, long captured)
    {
        long observation = library.BeginSongObservation();
        var result = await catalog.CompletePlaylistAsync(new(platform, id, Refresh: refresh, Personal: personal), signal).ConfigureAwait(false);
        Guard(signal, platform, captured);
        result = await library.AdoptPlatformResultAsync(result, signal, observation: observation).ConfigureAwait(false);
        Guard(signal, platform, captured);
        var songs = result.GetProperty("items").EnumerateArray().Select(song => NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(song))).ToArray();
        var name = OptionalText(result, "name", allowEmpty: true) ?? "";
        var actualId = Text(result, "id");
        var heartbeatAccount = OptionalText(result, "heartbeatAccountId", allowEmpty: true) ?? "";
        var token = Guid.NewGuid().ToString("D");
        int evicted = 0;
        lock (gate)
        {
            var state = StateForLocked(platform);
            Check(!closed && captured == state.Epoch, "STALE_SESSION");
            Prune();
            while (stages.Count >= MaxStages)
            {
                string? oldest = null;
                long oldestUse = long.MaxValue;
                foreach (var pair in stages)
                {
                    if (pair.Value.LastUsed < oldestUse)
                    {
                        oldestUse = pair.Value.LastUsed;
                        oldest = pair.Key;
                    }
                }
                if (oldest is null) break;
                stages.Remove(oldest);
                evicted++;
            }
            stages.Add(token, new(platform, actualId, name, songs,
                clock.GetUtcNow().AddMinutes(1), captured, heartbeatAccount, ++stageUseSequence));
        }
        DebugLog.Info(DebugCategories.MusicCatalog, "owner", "stage_prepared",
            new Dictionary<string, object?>
            {
                ["platform"] = platform, ["playlistId"] = actualId, ["count"] = songs.Length, ["evicted"] = evicted
            });
        return new PreparedCatalogPlaylist(token, platform, actualId, name, songs.Length);
    }

    private async Task<MusicQueuePositionResult> CommitCoreAsync(string token, string action, long expectedVersion, int index,
        string? memberId, string? songId, int memberOccurrence, CancellationToken signal, long captured)
    {
        Check(action is "play" or "next", "INVALID_REQUEST");
        Check(expectedVersion >= 0 && index >= 0 && memberOccurrence >= 0, "INVALID_REQUEST");
        Stage item;
        lock (gate)
        {
            Prune(); Check(stages.TryGetValue(token, out var found), "CATALOG_TOKEN_EXPIRED"); item = found!;
            var state = StateForLocked(item.Platform);
            Check(item.Epoch == state.Epoch && captured == state.Epoch && !closed, "STALE_SESSION");
            if (action == "next") Check(item.Songs.Length > 0, "INVALID_REQUEST");
            if (action == "play")
            {
                if (memberId is not null || songId is not null)
                {
                    int seen = 0;
                    index = Array.FindIndex(item.Songs, song =>
                        (memberId is not null ? SongJson.NativeId(song) == memberId : song.SongId == songId) &&
                        seen++ == memberOccurrence);
                }
                Check(index >= 0 && index < item.Songs.Length, "NOT_FOUND");
            }
            stages.Remove(token);
        }
        Guard(signal, item.Platform, captured);
        DebugLog.Info(DebugCategories.MusicCatalog, "owner", "stage_commit",
            new Dictionary<string, object?>
            {
                ["platform"] = item.Platform, ["playlistId"] = item.Id, ["action"] = action, ["index"] = index, ["count"] = item.Songs.Length
            });
        var playContext = MusicPlayContextRecord.Empty with
        {
            SourceTitle = item.Name,
            SourcePlatform = item.Platform,
            SourceType = "playlist",
            SourceId = item.Id,
            HeartbeatPlaylistId = item.HeartbeatAccount.Length > 0 ? item.Id : null,
            HeartbeatAccountId = item.HeartbeatAccount.Length > 0 ? item.HeartbeatAccount : null
        };
        var queueSource = new MusicQueueSourceRecord(item.Id, item.Name, "playlist", item.Platform);
        IMusicQueuePosition result = action == "next"
            ? await library.AppendQueueAsync(item.Songs, playContext, next: true, expectedVersion: expectedVersion, cancellationToken: signal).ConfigureAwait(false)
            : await library.ReplaceQueueAsync(item.Songs, index, playContext, queueSource, expectedVersion: expectedVersion, cancellationToken: signal).ConfigureAwait(false);
        return new MusicQueuePositionResult(result.CurrentId, result.Version, result.Mode);
    }

    public void Invalidate(string? platform = null)
    {
        Check(platform is null or "wy" or "tx" or "kg" or "kw" or "mg", "INVALID_REQUEST");
        var previousSessions = new List<CancellationTokenSource>();
        int evictedStages = 0;
        lock (gate)
        {
            if (closed) return;
            if (platform is not null)
            {
                var state = StateForLocked(platform);
                state.Epoch++;
                previousSessions.Add(state.Session);
                state.Session = new();
                foreach (var key in stages.Where(pair => pair.Value.Platform == platform).Select(pair => pair.Key).ToArray())
                    if (Remove(key)) evictedStages++;
            }
            else
            {
                foreach (var state in platformStates.Values)
                {
                    state.Epoch++;
                    previousSessions.Add(state.Session);
                    state.Session = new();
                }
                evictedStages = stages.Count;
                stages.Clear();
            }
        }
        DebugLog.Info(DebugCategories.MusicCatalog, "owner", "invalidate",
            new Dictionary<string, object?> { ["platform"] = platform ?? "*", ["evictedStages"] = evictedStages });
        try
        {
            foreach (var previous in previousSessions)
                try { previous.Cancel(); }
                finally { previous.Dispose(); }
        }
        finally { catalog.Invalidate(platform); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            foreach (var state in platformStates.Values) state.Epoch++;
            stages.Clear();
            disposal = DisposeCoreAsync(drained.Task);
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task pendingOperations)
    {
        await Task.Yield();
        Exception? cancellationFailure = null;
        try { lifetime.Cancel(); } catch (Exception error) { cancellationFailure = error; }
        CancellationTokenSource[] activeSessions;
        lock (gate) activeSessions = platformStates.Values.Select(s => s.Session).ToArray();
        foreach (var active in activeSessions)
            try { active.Cancel(); } catch (Exception error) { cancellationFailure ??= error; }
        async Task DrainAsync()
        {
            await pendingOperations.ConfigureAwait(false);
            lifetime.Dispose();
            foreach (var active in activeSessions) active.Dispose();
        }
        await DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), clock).ConfigureAwait(false);
        if (cancellationFailure is not null) throw new InvalidOperationException("CATALOG_CLEANUP");
    }
    private void Guard(CancellationToken signal, string? platform, long captured)
    {
        signal.ThrowIfCancellationRequested();
        lock (gate)
        {
            Check(!closed, "CLOSED");
            if (platform is not null)
                Check(captured == StateForLocked(platform).Epoch, "STALE_SESSION");
        }
    }
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in stages.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) Remove(entry);
    }
    private bool Remove(string token) => stages.Remove(token);
    private static TaskCompletionSource Completed() { var task = new TaskCompletionSource(); task.SetResult(); return task; }
    private static string StringValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetRawText(),
        _ => throw new CatalogFailure("INVALID_REQUEST")
    };
    private static string Text(JsonElement value, string name, string? fallback = null)
    {
        if (!value.TryGetProperty(name, out var field)) return fallback ?? throw new CatalogFailure("INVALID_REQUEST");
        return StringValue(field);
    }
    private static string? OptionalText(JsonElement value, string name, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        var text = StringValue(field);
        Check(allowEmpty || text.Length > 0, "INVALID_REQUEST");
        return text;
    }
    private static void Check(bool condition, string code) { if (!condition) throw new CatalogFailure(code); }
    private sealed class CatalogFailure(string code) : Exception(code) { internal string Code { get; } = code; }
}
