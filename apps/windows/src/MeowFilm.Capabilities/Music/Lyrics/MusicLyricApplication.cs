using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native metadata orchestration. Platform priority is independent of the audio
/// delivery source. Enabled scripts are tried in installation order for exact
/// WY/KW/MG recordings after the available native protocol returns no lyrics.
/// </summary>
public sealed class MusicLyricApplication(
    MusicLibraryOwner library, NativeMusicLyrics official,
    Func<string, object?, CancellationToken, Task<JsonElement>> script) : IAsyncDisposable
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
    private int cacheBytes;
    private long policyEpoch;
    private bool closed;
    private Task? disposal;
    private sealed record Cached(string Platform, JsonElement Value, int Bytes, DateTimeOffset Until);
    private sealed class Pending(string key, NativeLyricRequest request, long epoch, CancellationToken lifetime)
    {
        internal string Key = key;
        internal NativeLyricRequest Request = request;
        internal long Epoch = epoch;
        internal CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal TaskCompletionSource<JsonElement?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work = Task.CompletedTask;
        internal Task? Cancelling;
        internal void Cancel() => Cancelling ??= Cancellation.CancelAsync();
    }
    private sealed class ScriptRead(string platform, CancellationToken lifetime) : IDisposable
    {
        internal readonly string Platform = platform;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Task<JsonElement> Work = null!;
        internal Task? Cancelling;
        internal void Cancel() => Cancelling ??= Cancellation.CancelAsync();
        public void Dispose() => Cancellation.Dispose();
    }

    public async Task<JsonElement> ExecuteAsync(object? parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            long policy;
            lock (gate) { MusicRules.Require(!closed, "CLOSED"); policy = policyEpoch; }
            var p = MusicWireCodec.ParseParameters(parameters);
            MusicWireCodec.RequireFields(p, "song");
            var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(p.GetProperty("song")));
            var settings = await library.ExecuteAsync("music.settings.get", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!settings.GetProperty("ok").GetBoolean()) return settings;
            CheckPolicy(policy);
            if (!settings.GetProperty("settings").GetProperty("lyrics").GetBoolean()) return Success("disabled");
            var preferences = await library.ExecuteAsync("music.metadata.settings.get", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!preferences.GetProperty("ok").GetBoolean()) return preferences;
            CheckPolicy(policy);
            var metadata = preferences.GetProperty("settings");
            var enabled = metadata.GetProperty("enabled").EnumerateArray().Select(row => row.GetString()!).ToHashSet(StringComparer.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) MusicRules.Require(!closed, "CLOSED");
            var rows = song.Sources;
            string? selectedKey = null;
            if (MusicWireCodec.TryGet(p, "sourceKey", out var skEl) && skEl.ValueKind != JsonValueKind.Null)
                selectedKey = MusicWireCodec.RequireStr(skEl);
            if (selectedKey is not null)
            {
                rows = rows.Where(row => row.SourceKey == selectedKey).ToArray();
                MusicRules.Require(rows.Length == 1, "TARGET_CHANGED");
                if (!enabled.Contains(rows[0].Platform)) return Success("disabled");
            }
            else
            {
                var local = await library.ExecuteAsync("music.lyrics.local.get",
                    new { song = MusicWireCodec.SongToElement(song) }, cancellationToken).ConfigureAwait(false);
                CheckPolicy(policy);
                if (!local.GetProperty("ok").GetBoolean() || local.GetProperty("state").GetString() is "local" or "disabled")
                    return local;
                // These are already admitted exact editions of this song, not a
                // lookup by title. The original metadata traversal is <=10.
                var order = metadata.GetProperty("platforms").EnumerateArray().Select(item => item.GetString()!).ToArray();
                rows = order.Concat(metadata.GetProperty("enabled").EnumerateArray()
                        .Select(item => item.GetString()!).Except(order))
                    .Where(enabled.Contains).SelectMany(platform =>
                    rows.Where(row => row.Platform == platform)
                        .OrderBy(row => row.SourceKey, StringComparer.Ordinal).Take(2)).ToArray();
                if (enabled.Count == 0 || rows.Length == 0 && song.Sources.Length > 0)
                    return Success("disabled");
            }
            JsonElement? installations = null;
            JsonElement? songElement = null;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string platform = MusicRules.RequireId(row.Platform), sourceKey = row.SourceKey;
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
                    if (value is { } found) return WithTarget(found, sourceKey);
                }
                // TX/KG are official-only, even when an enabled LX advertises
                // lyric. A missing official result does not authorize bypass.
                if (platform is not ("wy" or "kw" or "mg")) continue;
                if (installations is null)
                {
                    var listed = await script("music.sources.list", null, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckEpoch(policy, platform, epoch);
                    if (!listed.GetProperty("ok").GetBoolean()) return listed;
                    installations = listed;
                }
                songElement ??= MusicWireCodec.SongToElement(song);
                foreach (var installation in installations.Value.GetProperty("items").EnumerateArray())
                {
                    if (!installation.GetProperty("enabled").GetBoolean()) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckEpoch(policy, platform, epoch);
                    string id = installation.GetProperty("installationId").GetString()!;
                    // Known declarations skip a platform without lyric; an undeclared
                    // installation is loaded by the actual lyric request itself.
                    var capabilities = installation.GetProperty("capabilities");
                    if (capabilities.ValueKind != JsonValueKind.Null &&
                        !(capabilities.TryGetProperty(platform, out var site) &&
                            site.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "lyric")))
                        continue;
                    var result = await ReadScriptAsync(platform, new
                    {
                        installationId = id, platform, sourceKey, song = songElement.Value
                    }, policy, epoch, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckEpoch(policy, platform, epoch);
                    if (!result.GetProperty("ok").GetBoolean())
                    {
                        if (result.GetProperty("code").GetString() == "CAPABILITY_UNAVAILABLE") continue;
                        return result;
                    }
                    if (result.GetProperty("lines").GetArrayLength() > 0) return WithTarget(result, sourceKey);
                }
            }
            CheckPolicy(policy);
            return Success("none");
        }
        catch (OperationCanceledException) { return Failure("CANCELLED"); }
        catch (MusicLibraryException error) { return Failure(error.Code); }
        catch (MusicPlatformException error) { return Failure(error.Code); }
        catch (JsonException) { return Failure("INVALID_REQUEST"); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return Failure("INVALID_LYRIC_RESPONSE"); }
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

    private Task<JsonElement> ReadScriptAsync(string platform, object input, long policy, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parameters = JsonSerializer.SerializeToElement(input);
        var key = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { parameters, policy, epoch })));
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

        async Task<JsonElement> CompleteScriptAsync(ScriptRead read)
        {
            await Task.Yield();
            try
            {
                var reply = await script("music.online.lyrics", parameters, read.Cancellation.Token).ConfigureAwait(false);
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

    private async Task<JsonElement?> ReadOfficial(NativeLyricRequest request, CancellationToken token)
    {
        Pending entry;
        lock (gate)
        {
            MusicRules.Require(!closed, "CLOSED");
            var epoch = epochs.GetValueOrDefault(request.Platform);
            var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request, epoch })));
            if (cache.TryGetValue(key, out var cached))
            {
                if (cached.Until > clock.GetUtcNow()) return cached.Value.Clone();
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
        return (await entry.Result.Task.WaitAsync(token).ConfigureAwait(false))?.Clone();
    }

    private async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The subscribed caller observes failure; abandoned reads remain owned. */ }
        finally { lock (gate) operations.Remove(task); }
    }

    private async Task Complete(Pending entry)
    {
        await Task.Yield();
        try
        {
            var token = entry.Cancellation.Token;
            var payload = await official.ReadAsync(entry.Request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            JsonElement? result = null;
            if (payload is not null)
            {
                var lines = MusicLyricTiming.ParseLinesRecord(payload.Text, payload.Translation,
                    entry.Request.DurationSeconds);
                var romanized = MusicLyricTiming.ParseLinesRecord(payload.Romanization, duration: entry.Request.DurationSeconds)
                    .GroupBy(row => row.TimeSec)
                    .ToDictionary(group => group.Key, group => string.Join("\n",
                        group.Select(row => row.Text).Distinct()));
                for (int i = 0; i < lines.Length; i++)
                    if (romanized.TryGetValue(lines[i].TimeSec, out var text) && text != lines[i].Text)
                        lines[i] = lines[i] with { Romanization = text };
                if (lines.Length > 0)
                {
                    result = MusicWireCodec.Ok(w =>
                    {
                        w.WriteString("state", "ready");
                        w.WritePropertyName("lines");
                        MusicWireCodec.WriteLyricLines(w, lines);
                    });
                }
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                MusicRules.Require(!closed && entry.Epoch == epochs.GetValueOrDefault(entry.Request.Platform), "STALE_SESSION");
                if (result is { } value &&
                    pending.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    int bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
                    if (bytes <= 1024 * 1024)
                    {
                        cache[entry.Key] = new(entry.Request.Platform, value, bytes, clock.GetUtcNow().AddMinutes(10));
                        cacheBytes += bytes;
                        while (cache.Count > 100 || cacheBytes > 4 * 1024 * 1024)
                        {
                            var oldest = cache.First(); cache.Remove(oldest.Key); cacheBytes -= oldest.Value.Bytes;
                        }
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

    private static JsonElement WithTarget(JsonElement value, string sourceKey) =>
        MusicWireCodec.BuildObject(w =>
        {
            w.WriteBoolean("ok", true);
            w.WriteString("sourceKey", sourceKey);
            foreach (var prop in value.EnumerateObject())
                if (prop.Name is not ("ok" or "sourceKey"))
                    prop.WriteTo(w);
        });

    private static JsonElement Success(string state) => MusicWireCodec.Ok(w =>
    {
        w.WriteString("state", state);
        w.WritePropertyName("lines"); w.WriteStartArray(); w.WriteEndArray();
    });

    private static JsonElement Failure(string code) => MusicWireCodec.Fail(code);
}
