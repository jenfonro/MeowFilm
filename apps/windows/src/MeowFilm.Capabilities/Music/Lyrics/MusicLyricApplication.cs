using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Capabilities.Scripting;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native metadata orchestration. Platform priority is independent of the audio
/// delivery source. Enabled scripts are tried in installation order for exact
/// WY/KW/MG recordings after the available native protocol returns no lyrics.
/// </summary>
public sealed class MusicLyricApplication(
    MusicLibraryOwner library, NativeMusicLyrics official, MusicScriptOwner scripts) : IAsyncDisposable
{
    public const int ContractVersion = 3;
    private static TimeProvider clock => TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptRead> scriptReads = new(StringComparer.Ordinal);
    private readonly HashSet<Task> operations = [];
    private readonly CancellationTokenSource lifetime = new();
    private const int MaximumCacheEntries = 100;
    private const int MaximumCacheBytes = 4 * 1024 * 1024;
    private int cacheBytes;
    private long cacheClock;
    private long policyEpoch;
    private bool closed;
    private Task? disposal;
    private sealed class Cached(string platform, MusicLyricLookupResult value, int bytes, DateTimeOffset until, long used)
    {
        internal readonly string Platform = platform;
        internal readonly MusicLyricLookupResult Value = value;
        internal readonly int Bytes = bytes;
        internal readonly DateTimeOffset Until = until;
        internal long Used = used;
    }
    private sealed class Pending(string key, NativeLyricRequest request, long epoch, CancellationToken lifetime)
    {
        internal string Key = key;
        internal NativeLyricRequest Request = request;
        internal long Epoch = epoch;
        internal CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal TaskCompletionSource<MusicLyricLookupResult?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work = Task.CompletedTask;
        internal Task? Cancelling;
        internal void Cancel() => Cancelling ??= Cancellation.CancelAsync();
    }
    private sealed class ScriptRead(string platform, CancellationToken lifetime) : IDisposable
    {
        internal readonly string Platform = platform;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Task<MusicLyricLookupResult> Work = null!;
        internal Task? Cancelling;
        internal void Cancel() => Cancelling ??= Cancellation.CancelAsync();
        public void Dispose() => Cancellation.Dispose();
    }

    public async Task<MusicLyricLookupResult> LookupAsync(MusicSongRecord songRecord, string? sourceKey = null, CancellationToken cancellationToken = default)
    {
        try
        {
            long policy;
            lock (gate) { MusicRules.Require(!closed, "CLOSED"); policy = policyEpoch; }
            var song = NativeMusicSong.Normalize(songRecord);
            var settings = await library.GetOnlineSettingsAsync(cancellationToken).ConfigureAwait(false);
            CheckPolicy(policy);
            if (!settings.LyricsEnabled) return new MusicLyricLookupResult("disabled", []);
            var metadata = await library.GetMetadataPolicyAsync(cancellationToken).ConfigureAwait(false);
            CheckPolicy(policy);
            var enabled = metadata.Enabled.ToHashSet(StringComparer.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) MusicRules.Require(!closed, "CLOSED");
            var rows = song.Sources;
            if (sourceKey is not null)
            {
                rows = rows.Where(row => row.SourceKey == sourceKey).ToArray();
                MusicRules.Require(rows.Length == 1, "TARGET_CHANGED");
                if (!enabled.Contains(rows[0].Platform)) return new MusicLyricLookupResult("disabled", []);
            }
            else
            {
                var local = await library.GetLocalLyricsAsync(song, cancellationToken).ConfigureAwait(false);
                CheckPolicy(policy);
                if (local.State is "local" or "disabled")
                    return local;
                // These are already admitted exact editions of this song, not a
                // lookup by title. The original metadata traversal is <=10.
                rows = metadata.Platforms.Concat(metadata.Enabled.Except(metadata.Platforms))
                    .Where(enabled.Contains).SelectMany(platform =>
                    rows.Where(row => row.Platform == platform)
                        .OrderBy(row => row.SourceKey, StringComparer.Ordinal).Take(2)).ToArray();
                if (enabled.Count == 0 || rows.Length == 0 && song.Sources.Length > 0)
                    return new MusicLyricLookupResult("disabled", []);
            }
            MusicScriptSourceSnapshot? installations = null;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string platform = MusicRules.RequireId(row.Platform), rowSourceKey = row.SourceKey;
                long epoch;
                lock (gate) { CheckPolicy(policy); epoch = epochs.GetValueOrDefault(platform); }
                if (platform is "wy" or "tx" or "kg")
                {
                    var id = platform switch
                    {
                        "tx" => row.SongMid ?? "",
                        "kg" => (row.Hash ?? "").ToUpperInvariant(),
                        _ => NativeMusicSong.InfoId(row)
                    };
                    double duration = row.Duration is { } d && d > 0 ? d : song.Duration ?? 0;
                    var albumAudioVal = StorageValueJson.Field(row.WireExtras, "albumAudioId");
                    string? albumAudioId = albumAudioVal is null ? null : StorageValueJson.ScalarText(albumAudioVal);
                    var request = new NativeLyricRequest(platform, id, duration, albumAudioId);
                    var value = await ReadOfficial(request, cancellationToken).ConfigureAwait(false);
                    CheckEpoch(policy, platform, epoch);
                    if (value is { } found) return found with { SourceKey = rowSourceKey };
                }
                // TX/KG are official-only, even when an enabled LX advertises
                // lyric. A missing official result does not authorize bypass.
                if (platform is not ("wy" or "kw" or "mg")) continue;
                if (installations is null)
                {
                    var listed = await scripts.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckEpoch(policy, platform, epoch);
                    installations = listed;
                }
                foreach (var installation in installations.Items)
                {
                    if (!installation.Enabled) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckEpoch(policy, platform, epoch);
                    string id = installation.InstallationId;
                    // Known declarations skip a platform without lyric; an undeclared
                    // installation is loaded by the actual lyric request itself.
                    if (installation.Capabilities is { } capabilities &&
                        !(capabilities.TryGetValue(platform, out var site) && site.SupportsAction("lyric")))
                        continue;
                    MusicLyricLookupResult result;
                    try
                    {
                        result = await ReadScriptAsync(id, platform, song, rowSourceKey, policy, epoch, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MusicLibraryException error) when (error.Code == "CAPABILITY_UNAVAILABLE")
                    {
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckEpoch(policy, platform, epoch);
                    if (result.Lines.Length > 0) return result with { SourceKey = rowSourceKey };
                }
            }
            CheckPolicy(policy);
            return new MusicLyricLookupResult("none", []);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { throw new MusicLibraryException("INVALID_LYRIC_RESPONSE"); }
    }

    private void CheckPolicy(long expected)
    {
        lock (gate)
        {
            MusicRules.Require(!closed, "CLOSED");
            MusicRules.Require(expected == policyEpoch, "STALE_SESSION");
        }
    }

    private void CheckEpoch(long policy, string platform, long expected)
    {
        lock (gate)
        {
            CheckPolicy(policy);
            MusicRules.Require(expected == epochs.GetValueOrDefault(platform), "STALE_SESSION");
        }
    }

    private Task<MusicLyricLookupResult> ReadScriptAsync(string installationId, string platform, MusicSongRecord song, string sourceKey,
        long policy, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{installationId}\0{platform}\0{song.SongId}\0{sourceKey}\0{policy}\0{epoch}")));
        ScriptRead entry;
        lock (gate)
        {
            CheckEpoch(policy, platform, epoch);
            if (!scriptReads.TryGetValue(key, out entry!))
            {
                entry = new(platform, lifetime.Token);
                scriptReads.Add(key, entry);
                entry.Work = CompleteScriptAsync(entry);
                operations.Add(entry.Work); _ = Observe(entry.Work);
            }
        }
        return entry.Work.WaitAsync(token);

        async Task<MusicLyricLookupResult> CompleteScriptAsync(ScriptRead read)
        {
            await Task.Yield();
            try
            {
                var reply = await scripts.FetchOnlineLyricsAsync(installationId, platform, song, sourceKey, read.Cancellation.Token).ConfigureAwait(false);
                CheckEpoch(policy, platform, epoch);
                read.Cancellation.Token.ThrowIfCancellationRequested();
                return reply;
            }
            catch (OperationCanceledException) { CheckEpoch(policy, platform, epoch); throw; }
            finally
            {
                Task? cancelling;
                lock (gate)
                {
                    if (scriptReads.GetValueOrDefault(key) == read) scriptReads.Remove(key);
                    cancelling = read.Cancelling;
                }
                try { if (cancelling is not null) await cancelling.ConfigureAwait(false); }
                finally { read.Dispose(); }
            }
        }
    }

    private async Task<MusicLyricLookupResult?> ReadOfficial(NativeLyricRequest request, CancellationToken token)
    {
        Pending entry;
        lock (gate)
        {
            MusicRules.Require(!closed, "CLOSED");
            var epoch = epochs.GetValueOrDefault(request.Platform);
            var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request, epoch })));
            if (cache.TryGetValue(key, out var cached))
            {
                if (cached.Until > clock.GetUtcNow())
                {
                    cached.Used = ++cacheClock;
                    return cached.Value;
                }
                cache.Remove(key); cacheBytes -= cached.Bytes;
            }
            if (!pending.TryGetValue(key, out entry!))
            {
                // Cancelled work stays owned until its real provider exits.
                // Removing a dedup key is not physical cancellation.
                entry = new(key, request, epoch, lifetime.Token);
                pending.Add(key, entry);
                _ = entry.Result.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                entry.Work = Complete(entry);
                operations.Add(entry.Work); _ = Observe(entry.Work);
            }
        }
        return await entry.Result.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The subscribed caller observes failure; abandoned reads remain owned. */ }
        finally { lock (gate) operations.Remove(task); }
    }

    private static int EstimateLinesBytes(MusicLyricLineRecord[] lines)
    {
        int bytes = 32;
        foreach (var line in lines)
        {
            bytes += 48 + Encoding.UTF8.GetByteCount(line.Text);
            if (line.Translation is not null) bytes += 18 + Encoding.UTF8.GetByteCount(line.Translation);
            if (line.Romanization is not null) bytes += 18 + Encoding.UTF8.GetByteCount(line.Romanization);
            foreach (var w in line.Words) bytes += 48 + Encoding.UTF8.GetByteCount(w.Text);
        }
        return bytes;
    }

    private async Task Complete(Pending entry)
    {
        await Task.Yield();
        try
        {
            var token = entry.Cancellation.Token;
            var payload = await official.ReadAsync(entry.Request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            MusicLyricLookupResult? result = null;
            if (payload is not null)
            {
                var lines = MusicLyricTiming.ParseLinesRecord(payload.Text, payload.Translation,
                    entry.Request.DurationSeconds, payload.Romanization);
                if (lines.Length > 0)
                    result = new MusicLyricLookupResult("ready", lines);
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                MusicRules.Require(!closed && entry.Epoch == epochs.GetValueOrDefault(entry.Request.Platform), "STALE_SESSION");
                if (result is { } value &&
                    pending.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    int bytes = EstimateLinesBytes(value.Lines);
                    if (bytes <= 1024 * 1024)
                    {
                        var now = clock.GetUtcNow();
                        if (cache.Remove(entry.Key, out var previous))
                            cacheBytes -= previous.Bytes;
                        if (cache.Count >= MaximumCacheEntries || cacheBytes + bytes > MaximumCacheBytes)
                        {
                            foreach (var expired in cache.Where(pair => pair.Value.Until <= now).ToArray())
                            {
                                cache.Remove(expired.Key);
                                cacheBytes -= expired.Value.Bytes;
                            }
                        }
                        while ((cache.Count >= MaximumCacheEntries || cacheBytes + bytes > MaximumCacheBytes) && cache.Count > 0)
                        {
                            var oldest = cache.MinBy(pair => pair.Value.Used);
                            cache.Remove(oldest.Key);
                            cacheBytes -= oldest.Value.Bytes;
                        }
                        cache[entry.Key] = new(entry.Request.Platform, value, bytes, now.AddMinutes(10), ++cacheClock);
                        cacheBytes += bytes;
                    }
                }
                entry.Result.TrySetResult(result);
            }
        }
        catch (OperationCanceledException) { entry.Result.TrySetCanceled(); }
        catch (Exception error) { entry.Result.TrySetException(error); }
        finally
        {
            Task? cancelled;
            lock (gate)
            {
                if (pending.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    pending.Remove(entry.Key);
                cancelled = entry.Cancelling;
            }
            try { if (cancelled is not null) await cancelled.ConfigureAwait(false); }
            finally { entry.Cancellation.Dispose(); }
        }
    }

    internal void ClearMemoryCache()
    {
        lock (gate)
        {
            cache.Clear();
            cacheBytes = 0;
        }
    }

    internal object GetCacheStats()
    {
        lock (gate)
        {
            return new
            {
                entryCount = cache.Count,
                bytes = cacheBytes,
                maximumEntries = MaximumCacheEntries,
                maximumBytes = MaximumCacheBytes,
                inFlightCount = pending.Count + scriptReads.Count,
                closed
            };
        }
    }

    public void Invalidate(string? platform = null)
    {
        lock (gate)
        {
            if (platform is null) policyEpoch = checked(policyEpoch + 1);
            foreach (var key in platform is null ? new[] { "wy", "tx", "kg", "kw", "mg" } : new[] { platform })
                epochs[key] = checked(epochs.GetValueOrDefault(key) + 1);
            foreach (var item in cache.Where(pair => platform is null || pair.Value.Platform == platform).ToArray())
            { cache.Remove(item.Key); cacheBytes -= item.Value.Bytes; }
            foreach (var item in pending.Where(pair => platform is null || pair.Value.Request.Platform == platform).ToArray())
            { pending.Remove(item.Key); item.Value.Cancel(); }
            foreach (var item in scriptReads.Where(pair => platform is null || pair.Value.Platform == platform).ToArray())
            { scriptReads.Remove(item.Key); item.Value.Cancel(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true; cache.Clear(); cacheBytes = 0;
            return new(disposal = Close());
        }
    }

    internal async Task DrainAsync()
    {
        Task[] active; lock (gate) active = operations.ToArray();
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch { /* Individual reads retain their errors after physical settlement. */ }
    }

    private async Task Close()
    {
        await Task.Yield();
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { await DrainAsync().ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }
}
