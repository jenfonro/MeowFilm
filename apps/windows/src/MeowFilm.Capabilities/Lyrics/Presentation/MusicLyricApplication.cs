using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native metadata orchestration. Platform priority is independent of the audio
/// delivery source. Only an explicitly supplied same-platform LX target may be
/// used after an empty WY/KW/MG native result; no title search or global source.
/// </summary>
public sealed class MusicLyricApplication(
    MusicLibraryOwner library, INativeMusicLyrics official,
    Func<string, object?, CancellationToken, Task<JsonElement>> script,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public const int ContractVersion = 2;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
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
        internal int Subscribers;
        internal CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal TaskCompletionSource<JsonElement?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work = Task.CompletedTask;
        internal Task? Cancelling;
        internal void Cancel() => Cancelling ??= Cancellation.CancelAsync();
    }

    public async Task<JsonElement> ExecuteAsync(object? parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            long policy;
            lock (gate) { LJ.Require(!closed, "CLOSED"); policy = policyEpoch; }
            var p = LJ.Parameters(parameters);
            LJ.Fields(p, "song sourceKey scriptTarget", "song");
            var song = NativeMusicSong.Normalize(p["song"]);
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
            lock (gate) LJ.Require(!closed, "CLOSED");
            var rows = LJ.Array(song["sources"]).OfType<JsonObject>().ToArray();
            var selectedKey = p.ContainsKey("sourceKey") ? LJ.Text(p["sourceKey"], 8192) : null;
            if (selectedKey is not null)
            {
                rows = rows.Where(row => LJ.S(row["sourceKey"]) == selectedKey).ToArray();
                LJ.Require(rows.Length == 1, "TARGET_CHANGED");
                if (!enabled.Contains(LJ.S(rows[0]["source"]))) return Success("disabled");
            }
            else
            {
                var local = await library.ExecuteAsync("music.lyrics.local.get", new { song }, cancellationToken).ConfigureAwait(false);
                CheckPolicy(policy);
                if (!local.GetProperty("ok").GetBoolean() || local.GetProperty("state").GetString() is "local" or "disabled")
                    return local;
                // These are already admitted exact editions of this song, not a
                // lookup by title. The original metadata traversal is <=10.
                rows = metadata.GetProperty("platforms").EnumerateArray().Select(item => item.GetString()!)
                    .Where(enabled.Contains).SelectMany(platform =>
                    rows.Where(row => LJ.S(row["source"]) == platform)
                        .OrderBy(row => LJ.S(row["sourceKey"]), StringComparer.Ordinal).Take(2)).ToArray();
                if (enabled.Count == 0 || rows.Length == 0 && LJ.Array(song["sources"]).Count > 0)
                    return Success("disabled");
            }
            JsonObject? scriptTarget = null;
            if (p["scriptTarget"] is not null)
            {
                scriptTarget = LJ.Object(p["scriptTarget"]);
                LJ.Fields(scriptTarget, "installationId platform", "installationId platform");
                LJ.Text(scriptTarget["installationId"], 128);
                LJ.Require(NativeMusicSong.Platforms.Contains(LJ.Text(scriptTarget["platform"])));
            }
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string platform = LJ.Text(row["source"]), sourceKey = LJ.Text(row["sourceKey"]);
                long epoch;
                lock (gate) { CheckPolicy(policy); epoch = epochs.GetValueOrDefault(platform); }
                var data = LJ.Object(row["source_data"]);
                if (platform is "wy" or "tx" or "kg")
                {
                    var id = platform switch {
                        "tx" => LJ.S(data["songmid"]),
                        "kg" => LJ.S(data["hash"]).ToUpperInvariant(),
                        _ => LJ.Scalar(data["musicId"] ?? data["songmid"])
                    };
                    var duration = LJ.N(data["duration"], LJ.N(song["duration"]));
                    if (duration <= 0) duration = LJ.N(song["duration"]);
                    var request = new NativeLyricRequest(platform, id, duration,
                        data["albumAudioId"] is null ? null : LJ.Scalar(data["albumAudioId"]));
                    var value = await ReadOfficial(request, cancellationToken).ConfigureAwait(false);
                    CheckEpoch(policy, platform, epoch);
                    if (value is { } found) return WithTarget(found, sourceKey);
                }
                // TX/KG are official-only, even when the selected LX advertises
                // lyric. A missing official result does not authorize bypass.
                if (platform is not ("wy" or "kw" or "mg") || scriptTarget is null ||
                    LJ.S(scriptTarget["platform"]) != platform) continue;
                var result = await script("music.online.lyrics", new {
                    installationId = LJ.S(scriptTarget["installationId"]), platform, sourceKey, song
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                CheckEpoch(policy, platform, epoch);
                if (!result.GetProperty("ok").GetBoolean())
                {
                    var code = result.GetProperty("code").GetString();
                    if (code == "CAPABILITY_UNAVAILABLE") continue;
                    return result;
                }
                if (result.GetProperty("lines").GetArrayLength() > 0) return WithTarget(result, sourceKey);
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
            LJ.Require(!closed, "CLOSED");
            LJ.Require(expected == policyEpoch, "STALE_SESSION");
        }
    }
    private void CheckEpoch(long policy, string platform, long expected)
    {
        lock (gate)
        {
            CheckPolicy(policy);
            LJ.Require(expected == epochs.GetValueOrDefault(platform), "STALE_SESSION");
        }
    }

    private async Task<JsonElement?> ReadOfficial(NativeLyricRequest request, CancellationToken token)
    {
        Pending entry;
        lock (gate)
        {
            LJ.Require(!closed, "CLOSED");
            var epoch = epochs.GetValueOrDefault(request.Platform);
            var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request, epoch })));
            if (cache.TryGetValue(key, out var cached))
            {
                if (cached.Until > clock.GetUtcNow()) return cached.Value.Clone();
                cache.Remove(key); cacheBytes -= cached.Bytes;
            }
            if (!pending.TryGetValue(key, out entry!))
            {
                // Cancelled work still consumes a slot until its real provider
                // exits. Removing a dedup key is not physical cancellation.
                LJ.Require(operations.Count < 16, "LIMIT");
                entry = new(key, request, epoch, lifetime.Token);
                pending.Add(key, entry); entry.Subscribers = 1;
                entry.Work = Complete(entry);
                operations.Add(entry.Work); _ = Observe(entry.Work);
            }
            else entry.Subscribers++;
        }
        try { return (await entry.Result.Task.WaitAsync(token).ConfigureAwait(false))?.Clone(); }
        finally
        {
            bool drain = false;
            lock (gate)
            {
                entry.Subscribers--;
                if (entry.Subscribers == 0 && pending.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    // Cancellation callbacks must not be synchronously invoked
                    // while holding the cache gate.
                    entry.Cancel();
                    drain = true;
                }
            }
            if (drain)
            {
                try { await entry.Work.ConfigureAwait(false); }
                catch { /* The cancelled subscriber keeps only physical-drain ownership. */ }
            }
        }
    }
    private async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
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
                LJ.Require(payload.Platform == entry.Request.Platform &&
                    payload.RecordingId == entry.Request.RecordingId, "LYRIC_IDENTITY_MISMATCH");
                var lines = JsonNode.Parse(MusicLyricTiming.Parse(payload.Text, payload.Translation,
                    entry.Request.DurationSeconds).GetRawText())!.AsArray();
                var romanized = MusicLyricTiming.Parse(payload.Romanization, duration: entry.Request.DurationSeconds)
                    .EnumerateArray().GroupBy(row => row.GetProperty("time").GetDouble())
                    .ToDictionary(group => group.Key, group => string.Join("\n",
                        group.Select(row => row.GetProperty("text").GetString()).Distinct()));
                foreach (var line in lines.OfType<JsonObject>())
                    if (romanized.TryGetValue(LJ.N(line["time"]), out var text) && text != LJ.S(line["text"]))
                        line["romanization"] = text;
                if (lines.Count > 0)
                    result = LJ.Element(LJ.Obj(("ok", true), ("state", "ready"), ("platform", payload.Platform),
                        ("recordingId", payload.RecordingId), ("lines", lines)));
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                LJ.Require(!closed && entry.Epoch == epochs.GetValueOrDefault(entry.Request.Platform), "STALE_SESSION");
                if (result is { } value && entry.Subscribers > 0 &&
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
        }
        if (platform is null or "wy" or "tx" or "kg") official.Invalidate(platform);
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
    private async Task Close()
    {
        await Task.Yield();
        await lifetime.CancelAsync().ConfigureAwait(false);
        Task[] active; lock (gate) active = operations.ToArray();
        await Task.WhenAll(active).ConfigureAwait(false);
        lifetime.Dispose();
    }
    private static JsonElement WithTarget(JsonElement value, string sourceKey)
    {
        var result = JsonNode.Parse(value.GetRawText())!.AsObject();
        result["ok"] = true; result["sourceKey"] = sourceKey;
        return LJ.Element(result);
    }
    private static JsonElement Success(string state) =>
        JsonSerializer.SerializeToElement(new { ok = true, state, lines = Array.Empty<object>() });
    private static JsonElement Failure(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
}
