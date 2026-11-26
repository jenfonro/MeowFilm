using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Scripting;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native metadata orchestration. Platform priority is independent of the audio
/// delivery source. Enabled scripts are tried in installation order for exact
/// WY/KW/MG recordings after the available native protocol returns no lyrics.
/// </summary>
internal sealed class MusicLyricApplication(
    MusicLibraryOwner library, MusicPlatformClient platform, MusicScriptOwner scripts) : IAsyncDisposable
{
    public const int ContractVersion = 3;
    private sealed record OfficialLyricRequest(string Platform, string RecordingId,
        double DurationSeconds = 0, string? AlbumAudioId = null);
    private readonly object gate = new();
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptRead> scriptReads = new(StringComparer.Ordinal);
    private readonly HashSet<Task> operations = [];
    private readonly CancellationTokenSource lifetime = new();
    private long policyEpoch;
    private bool closed;
    private Task? disposal;

    private sealed class Pending(string key, OfficialLyricRequest request, long epoch, CancellationToken lifetime)
    {
        internal string Key = key;
        internal OfficialLyricRequest Request = request;
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

    public async Task<MusicLyricLookupResult> LookupAsync(MusicSongRecord song, string? sourceKey = null, CancellationToken cancellationToken = default)
    {
        try
        {
            long policy;
            lock (gate) { MusicRules.Require(!closed, "CLOSED"); policy = policyEpoch; }
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
                    var request = new OfficialLyricRequest(platform, id, duration, albumAudioId);
                    var value = await ReadOfficial(request, cancellationToken).ConfigureAwait(false);
                    CheckEpoch(policy, platform, epoch);
                    if (value is { } found) return found with { SourceKey = rowSourceKey };
                }
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
        var key = $"{installationId}\0{platform}\0{song.SongId}\0{sourceKey}\0{policy}\0{epoch}";
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

    private async Task<MusicLyricLookupResult?> ReadOfficial(OfficialLyricRequest request, CancellationToken token)
    {
        Pending entry;
        lock (gate)
        {
            MusicRules.Require(!closed, "CLOSED");
            var epoch = epochs.GetValueOrDefault(request.Platform);
            var key = $"{request.Platform}\0{request.RecordingId}\0{request.DurationSeconds:R}\0{request.AlbumAudioId}\0{epoch}";
            if (!pending.TryGetValue(key, out entry!))
            {
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
        catch { }
        finally { lock (gate) operations.Remove(task); }
    }

    private async Task Complete(Pending entry)
    {
        await Task.Yield();
        try
        {
            var token = entry.Cancellation.Token;
            var value = await platform.ReadAsync(entry.Request.Platform, "lyrics", new
            {
                id = entry.Request.RecordingId,
                musicInfo = new
                {
                    songmid = entry.Request.RecordingId,
                    duration = entry.Request.DurationSeconds,
                    albumAudioId = entry.Request.AlbumAudioId
                }
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            MusicLyricLookupResult? result = null;
            string text = value.GetProperty("text").GetString()!;
            if (text.Length > 0)
            {
                var lines = MusicLyricTiming.ParseLinesRecord(text,
                    value.GetProperty("translation").GetString()!,
                    entry.Request.DurationSeconds,
                    value.GetProperty("romanization").GetString()!);
                if (lines.Length > 0)
                    result = new MusicLyricLookupResult("ready", lines);
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                MusicRules.Require(!closed && entry.Epoch == epochs.GetValueOrDefault(entry.Request.Platform), "STALE_SESSION");
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

    internal object GetCacheStats()
    {
        lock (gate) return new { inFlightCount = pending.Count + scriptReads.Count, closed };
    }

    public void Invalidate(string? platform = null)
    {
        lock (gate)
        {
            if (platform is null) policyEpoch = checked(policyEpoch + 1);
            foreach (var key in platform is null ? new[] { "wy", "tx", "kg", "kw", "mg" } : new[] { platform })
                epochs[key] = checked(epochs.GetValueOrDefault(key) + 1);
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
            closed = true;
            return new(disposal = Close());
        }
    }

    internal async Task DrainAsync()
    {
        Task[] active; lock (gate) active = operations.ToArray();
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch { }
    }

    private async Task Close()
    {
        await Task.Yield();
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { await DrainAsync().ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }
}
