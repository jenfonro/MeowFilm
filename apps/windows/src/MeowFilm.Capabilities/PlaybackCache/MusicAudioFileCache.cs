using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Evictable playback bytes. No DownloadService, export target or durable download task.</summary>
internal sealed partial class MusicAudioFileCache
{
    internal const string Namespace = "music.playback-files.v1";
    internal const long Capacity = 256L * 1024 * 1024, Ttl = 30L * 86400000;
    private const int MaximumIndexBytes = 1024 * 1024, MaximumEntries = 2048;
    private static readonly string[] InfoFields =
    [
        "kind", "platform", "songId", "sourceKey", "scope", "generation",
        "quality", "requestedQuality", "actualQuality", "qualityVerified", "playbackVerified",
        "qualityPolicySignature", "qualityCacheSignature", "duration", "size", "full",
        "delivery",
        "qualityEvidence", "codec", "sampleRate", "bitDepth", "channels", "bitrate"
    ];
    private static readonly string[] ScriptInfoFields =
    [
        "kind", "platform", "songId", "sourceKey", "scope", "installationId", "providerName",
        "quality", "requestedQuality", "actualQuality", "qualityVerified", "playbackVerified",
        "qualityCacheSignature", "duration", "size", "full",
        "qualityEvidence", "codec", "sampleRate", "bitDepth", "channels", "bitrate"
    ];
    // Stable provenance, not the runtime generation or settings revision. Each tier/provider is isolated.
    internal static string ScriptSignature(string installation, string platform, string recording, string quality) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { installation, platform, recording, quality }))));
    private readonly string root;
    private readonly CacheDocumentStore store;
    private readonly SemaphoreSlim metadataGate = new(1, 1), transferGate = new(1, 1);
    private readonly Func<string, double, CancellationToken, Task<double>> validate;
    private readonly TimeProvider clock;
    private readonly NativeHttpMediaOptions? httpOptions;
    private readonly MusicRecordingPresentationCache presentations;
    private readonly object transferState = new();
    private CancellationTokenSource? activeTransfer;
    private PreparedFile? activePrepared;
    private long clearEpoch;
    private sealed class Index
    {
        internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Entry(string file, long size, string sha256, string signature, long confirmedAt,
        double duration, JsonObject info)
    {
        internal string File { get; } = file;
        internal long Size { get; } = size;
        internal string Sha256 { get; } = sha256;
        internal string Signature { get; } = signature;
        internal long ConfirmedAt { get; set; } = confirmedAt;
        internal double Duration { get; } = duration;
        internal JsonObject Info { get; } = info;
    }
    internal sealed partial class PreparedFile : IDisposable
    {
        internal readonly string Scope, Song, Signature;
        internal readonly JsonElement Receipt;
        internal readonly bool Speculative;
        internal PreparedFile(string scope, string song, string signature, JsonElement receipt, CancellationToken token, bool speculative)
        {
            Scope = scope; Song = song; Signature = signature; Receipt = receipt.Clone(); Speculative = speculative;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); SourceToken = Cancellation.Token;
        }
        // Read only after Ready's completion establishes the publication barrier.
        internal JsonElement VerifiedReceipt { get; private set; }
        internal void AcceptReceipt(JsonObject value) => VerifiedReceipt = JsonSerializer.SerializeToElement(value);
        internal readonly CancellationTokenSource Cancellation;
        private readonly CancellationToken SourceToken;
        internal readonly TaskCompletionSource<string?> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<long?> Played = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work = Task.CompletedTask;
        private int disposed;
        internal bool Matches(string scope, string song, string signature) =>
            !Cancellation.IsCancellationRequested && Scope == scope && Song == song && Signature == signature;
        internal async Task<INativeMediaReadLease?> OpenAsync(CancellationToken token)
        {
            var source = await Available.Task.WaitAsync(token).ConfigureAwait(false);
            if (source is null) { CheckSource(); return null; }
            FileStream? stream = null;
            try
            {
                stream = new FileStream(source.Value.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                token.ThrowIfCancellationRequested();
                var lease = new GrowingReadLease(this, stream, source.Value.Length, token);
                stream = null;
                lock (bytesGate) readers++;
                Interlocked.Exchange(ref opened, 1); return lease;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (OptionalFileFailure(e)) { return null; }
            finally { if (stream is not null) await stream.DisposeAsync(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Cancellation.Cancel(); Played.TrySetResult(null); Pulse();
            CleanupRetiredFile();
            _ = Work.ContinueWith(_ => Cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    internal PreparedFile Prepare(string scope, string song, string signature, JsonElement receipt,
        Func<bool> isCurrent, CancellationToken token, bool speculative = true)
    {
        var prepared = new PreparedFile(scope, song, signature, receipt, token, speculative);
        prepared.Work = StoreAsync(scope, song, signature, receipt, isCurrent, prepared.Cancellation.Token, prepared);
        return prepared;
    }
    internal MusicAudioFileCache(string root, CacheDocumentStore store,
        Func<string, double, CancellationToken, Task<double>>? validate = null,
        TimeProvider? clock = null, NativeHttpMediaOptions? httpOptions = null)
    {
        this.root = Path.GetFullPath(root); this.store = store;
        presentations = new(store, clock);
        this.validate = validate ?? NativeAudioCacheProbe.ValidateAsync; this.clock = clock ?? TimeProvider.System;
        this.httpOptions = httpOptions;
    }
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static string Key(string scope, string song) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { scope, song }))));
    private static bool LowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidFileName(string name) =>
        name.Length == 38 && name.EndsWith(".audio", StringComparison.Ordinal) &&
        LowerHex(name[..32], 32) && Guid.TryParseExact(name[..32], "N", out _);
    private void CheckRoot()
    {
        Directory.CreateDirectory(root);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("CACHE_UNSAFE_PATH");
    }
    private string FilePath(string name)
    {
        if (!ValidFileName(name)) throw new IOException("CACHE_UNSAFE_PATH");
        CheckRoot();
        string path = Path.Combine(root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("CACHE_UNSAFE_PATH");
        return path;
    }
    private static bool Accepted(Entry entry, string signature, long now)
    {
        if (entry.Signature != signature || entry.ConfirmedAt <= 0 || entry.ConfirmedAt > now) return false;
        long ttl = entry.ConfirmedAt > long.MaxValue - Ttl ? long.MaxValue : entry.ConfirmedAt + Ttl;
        return ttl > now;
    }
    private async Task<Index> Read(CancellationToken token, bool memory = false)
    {
        try
        {
            var saved = memory ? store.ReadMemory(Namespace) :
                await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Decode(saved.Data);
        }
        catch (DocumentStoreException) { token.ThrowIfCancellationRequested(); return new(); }
    }
    private static Index Decode(byte[]? data)
    {
        if (data is null || data.Length > MaximumIndexBytes) return new();
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
            var rawEntries = document.RootElement.GetProperty("entries");
            if (rawEntries.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
            int count = 0;
            var index = new Index();
            var files = new HashSet<string>(StringComparer.Ordinal);
            long totalSize = 0;
            foreach (var property in rawEntries.EnumerateObject())
            {
                if (++count > MaximumEntries) throw MusicPlaybackCacheDataException.Limit();
                if (!LowerHex(property.Name, 64)) throw MusicPlaybackCacheDataException.Corrupt();
                var raw = property.Value;
                string file = Text(raw.GetProperty("file"), 38);
                long size = CanonicalLong(raw.GetProperty("size"));
                string sha = Text(raw.GetProperty("sha256"), 64);
                string signature = Text(raw.GetProperty("signature"), 512);
                long confirmed = CanonicalLong(raw.GetProperty("confirmedAt"));
                double duration = DurationValue(raw.GetProperty("duration"), false);
                var info = raw.GetProperty("info");
                ValidateInfo(info, property.Name, signature, size, duration);
                if (!ValidFileName(file) || !files.Add(file) || size > Capacity ||
                    totalSize > Capacity - size || !LowerHex(sha, 64) ||
                    !LowerHex(signature, 64) ||
                    !index.Entries.TryAdd(property.Name, new Entry(file, size, sha, signature,
                        confirmed, duration, JsonNode.Parse(info.GetRawText())!.AsObject())))
                    throw MusicPlaybackCacheDataException.Corrupt();
                totalSize += size;
            }
            return index;
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return new(); }
    }


    private static long CanonicalLong(JsonElement value, bool allowZero = false)
    {
        if (!value.TryGetInt64(out long result) || (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }
    private static long? OptionalLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : CanonicalLong(value);
    private static double DurationValue(JsonElement value, bool allowUnknown)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) || (allowUnknown ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }
    private static string Text(JsonElement value, int maximum, bool empty = false)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
            throw MusicPlaybackCacheDataException.Corrupt();
        RequireText(result, maximum, empty);
        return result;
    }
    private static void RequireText(string result, int maximum, bool empty = false)
    {
        if (result.Length > maximum || !empty && result.Length == 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            if (c < 32 || c == 127 || char.IsLowSurrogate(c))
                throw MusicPlaybackCacheDataException.Corrupt();
            if (char.IsHighSurrogate(c))
            {
                if (++i >= result.Length || !char.IsLowSurrogate(result[i]))
                    throw MusicPlaybackCacheDataException.Corrupt();
            }
        }
    }
    private static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw MusicPlaybackCacheDataException.Corrupt()
    };
    private static string? NullableText(JsonElement value, int maximum, bool empty = false) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value, maximum, empty);
    private static long? NullablePositiveLong(JsonElement value, bool allowZero = false) =>
        value.ValueKind == JsonValueKind.Null ? null : CanonicalLong(value, allowZero);
    private static void ValidateInfo(JsonElement info, string key, string signature, long size,
        double duration, bool decoded = true)
    {
        bool script = info.TryGetProperty("kind", out var kind) && kind.GetString() == "script";
        if (!script && Text(info.GetProperty("kind"), 32) != "official")
            throw MusicPlaybackCacheDataException.Corrupt();
        string platform = Text(info.GetProperty("platform"), 16);
        string song = Text(info.GetProperty("songId"), 2048);
        string recording = Text(info.GetProperty("sourceKey"), 8192);
        string scope = Text(info.GetProperty("scope"), 128);
        if (!(script ? Library.NativeMusicSong.Platforms.Contains(platform) : platform is "wy" or "tx" or "kg") || Key(scope, song) != key)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (!script) _ = CanonicalLong(info.GetProperty("generation"));
        string quality = Text(info.GetProperty("quality"), 64);
        if (script)
        {
            string installation = Text(info.GetProperty("installationId"), 128);
            _ = Text(info.GetProperty("providerName"), 512, true);
            if (!MusicQualityPolicy.LxTypes.Contains(quality) ||
                ScriptSignature(installation, platform, recording, quality) != signature || scope != "script:" + signature)
                throw MusicPlaybackCacheDataException.Corrupt();
        }
        _ = Text(info.GetProperty("requestedQuality"), 64);
        string actual = Text(info.GetProperty("actualQuality"), 64, true);
        bool qualityVerified = Boolean(info.GetProperty("qualityVerified"));
        if (qualityVerified && actual.Length == 0 || Boolean(info.GetProperty("playbackVerified")))
            throw MusicPlaybackCacheDataException.Corrupt();
        string cache = Text(info.GetProperty("qualityCacheSignature"), 64);
        if (!script && !LowerHex(Text(info.GetProperty("qualityPolicySignature"), 64), 64) ||
            !LowerHex(cache, 64) || cache != signature)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (DurationValue(info.GetProperty("duration"), !decoded) != duration ||
            CanonicalLong(info.GetProperty("size"), allowZero: !decoded) != size ||
            !Boolean(info.GetProperty("full")) && decoded)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (!script)
        {
            if (Text(info.GetProperty("delivery"), 128) != platform + "-official")
                throw MusicPlaybackCacheDataException.Corrupt();
        }
        _ = NullableText(info.GetProperty("qualityEvidence"), 128, true);
        _ = NullableText(info.GetProperty("codec"), 64, true);
        _ = NullablePositiveLong(info.GetProperty("sampleRate"));
        _ = NullablePositiveLong(info.GetProperty("bitDepth"), true);
        _ = NullablePositiveLong(info.GetProperty("channels"));
        _ = NullablePositiveLong(info.GetProperty("bitrate"), true);
    }
    private static JsonObject Encode(Index index)
    {
        if (index.Entries.Count > MaximumEntries) throw MusicPlaybackCacheDataException.Limit();
        var entries = new JsonObject();
        foreach (var pair in index.Entries)
        {
            if (!LowerHex(pair.Key, 64)) throw MusicPlaybackCacheDataException.Corrupt();
            var value = pair.Value;
            entries[pair.Key] = new JsonObject
            {
                ["file"] = value.File,
                ["size"] = value.Size,
                ["sha256"] = value.Sha256,
                ["signature"] = value.Signature,
                ["confirmedAt"] = value.ConfirmedAt,
                ["duration"] = value.Duration,
                ["info"] = value.Info.DeepClone()
            };
        }
        return new JsonObject { ["entries"] = entries };
    }
    private Task Write(Index index, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Encode(index));
        if (bytes.Length > MaximumIndexBytes) throw MusicPlaybackCacheDataException.Limit();
        token.ThrowIfCancellationRequested();
        store.Save(Namespace, bytes);
        return Task.CompletedTask;
    }
    internal async Task<INativeMediaReadLease?> OpenAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        long epoch = Interlocked.Read(ref clearEpoch);
        FileStream? file = null;
        try
        {
            var index = await Read(token).ConfigureAwait(false);
            // Initial disk reads do not own the mutation gate. File access and hit
            // publication do: clear cannot finish between eligibility and opening.
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    !index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now)) return null;
                string path = FilePath(entry.File);
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
                if (file.Length != entry.Size || file.Length is <= 0 or > Capacity) return null;
                string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
                token.ThrowIfCancellationRequested();
                if (digest != entry.Sha256 || epoch != Interlocked.Read(ref clearEpoch)) return null;
                file.Position = 0;
                var lease = new ReadLease(file, token); file = null; return lease;
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { token.ThrowIfCancellationRequested(); return null; }
        catch (Exception e) when (OptionalFileFailure(e)) { token.ThrowIfCancellationRequested(); return null; }
        finally { if (file is not null) await file.DisposeAsync(); }
    }
    internal async Task<JsonObject?> ReadInfoAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            var index = await Read(token).ConfigureAwait(false);
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    !index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now) ||
                    !File.Exists(FilePath(entry.File))) return null;
                return entry.Info.DeepClone().AsObject();
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { token.ThrowIfCancellationRequested(); return null; }
        catch (Exception e) when (OptionalFileFailure(e)) { token.ThrowIfCancellationRequested(); return null; }
    }
    internal async Task<JsonElement[]> ReadScriptHistoryAsync(string song, CancellationToken token)
    {
        RequireText(song, 2048);
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            var index = await Read(token).ConfigureAwait(false);
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref clearEpoch)) return [];
                return index.Entries.Values.Where(entry => entry.Info["kind"]?.GetValue<string>() == "script" &&
                    entry.Info["songId"]?.GetValue<string>() == song && Accepted(entry, entry.Signature, Now) &&
                    File.Exists(FilePath(entry.File))).OrderByDescending(entry => entry.ConfirmedAt)
                    .Select(entry => JsonSerializer.SerializeToElement(entry.Info)).ToArray();
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { token.ThrowIfCancellationRequested(); return []; }
        catch (Exception e) when (OptionalFileFailure(e)) { token.ThrowIfCancellationRequested(); return []; }
    }
    internal async Task TouchAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = await Read(token, memory: true).ConfigureAwait(false);
            if (!index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now)) return;
            entry.ConfirmedAt = Now; await Write(index, token).ConfigureAwait(false);
            await presentations.TouchAsync(song, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { }
        finally { metadataGate.Release(); }
    }
    internal async Task EraseAsync(string scope, string song, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = await Read(token, memory: true).ConfigureAwait(false);
            string key = Key(scope, song);
            index.Entries.Remove(key, out var entry);
            // Publish even an empty index so a pending initial read cannot restore it.
            await Write(index, token).ConfigureAwait(false);
            if (entry is not null) TryDelete(entry.File);
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { }
        finally { metadataGate.Release(); }
    }
    // Called only after the actual player's Started event. Probe callbacks never call this method.
    internal Task StorePlayedAsync(string scope, string song, string signature, JsonElement receipt,
        Func<bool> isCurrent, CancellationToken token) => StoreAsync(scope, song, signature, receipt, isCurrent, token, null);
    private async Task StoreAsync(string scope, string song, string signature, JsonElement receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile? prepared)
    {
        try
        {
            await StoreCoreAsync(scope, song, signature, receipt, isCurrent, token, prepared).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
        finally { prepared?.FinishWork(); }
    }
    private async Task StoreCoreAsync(string scope, string song, string signature, JsonElement receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile? prepared)
    {
        long epoch = Interlocked.Read(ref clearEpoch);
        var info = ValidateReceipt(receipt, scope, song, signature, out long? expires);
        double expected = info["duration"]!.GetValue<double>();
        long expectedSize = info["size"]!.GetValue<long>();
        bool full = info["full"]!.GetValue<bool>();
        if ((!full && prepared is null) || expectedSize > Capacity ||
            expires is { } urlExpiry && urlExpiry <= Now)
            return;
        // Actual playback may wait asynchronously for a retiring speculative producer;
        // prefetch never queues work or stalls the player's read path.
        if (prepared?.Speculative != true) await transferGate.WaitAsync(token).ConfigureAwait(false);
        else if (!await transferGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        string? candidate = null;
        FileStream? producerLease = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (transferState)
            {
                activeTransfer = deadline;
                activePrepared = prepared;
                if (epoch != Interlocked.Read(ref clearEpoch)) deadline.Cancel();
            }
            long confirmed = Now;
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            var ct = deadline.Token;
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return;
            // Reserve at most the remaining cache capacity, including orphan files, before fetching.
            CheckRoot();
            producerLease = AcquireProducer();
            await PruneAsync(expectedSize > 0 ? expectedSize : Capacity / 4, ct).ConfigureAwait(false);
            long occupied = Directory.EnumerateFiles(root, "*.audio").Sum(p => new FileInfo(p).Length);
            long maximum = Capacity - occupied;
            if (maximum <= 0 || expectedSize > maximum) return;
            candidate = Guid.NewGuid().ToString("N") + ".audio";
            string path = FilePath(candidate);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous))
            {
                using var input = await HttpMediaInput.OpenAsync(new Uri(info["url"]!.GetValue<string>()),
                    info["kind"]?.GetValue<string>() == "script" && info["headers"] is JsonObject headers
                        ? headers.ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(), httpOptions, ct).ConfigureAwait(false);
                prepared?.BeginFile(path, input.Length);
                long size = await input.CopyCompleteAsync(output, maximum, ct,
                    prepared is null ? null : prepared.ReceivedBytes).ConfigureAwait(false);
                if (expectedSize > 0 && size != expectedSize) return;
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                prepared?.CompleteBody(size);
            }
            // Unknown completeness can still be played from the sequential
            // file, but is not evidence for a persistent complete-file hit.
            if (!full) return;
            deadline.CancelAfter(TimeSpan.FromSeconds(120));
            if (!isCurrent()) return;
            string sha;
            long length;
            AudioCacheStructure.AudioFacts? facts;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = file.Length; facts = AudioCacheStructure.Inspect(file, ct); file.Position = 0;
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            }
            double duration = await validate(path, expected, ct).ConfigureAwait(false);
            if (!double.IsFinite(duration) || duration <= 0 || expected > 0 && Math.Abs(duration - expected) > 10) return;
            info["duration"] = duration;
            info["size"] = length;
            if (facts is { Quality.Length: > 0 })
            {
                info["actualQuality"] = facts.Quality; info["qualityVerified"] = true;
                info["qualityEvidence"] = "complete-file-native-decoded";
                info["codec"] = facts.Codec; info["sampleRate"] = facts.SampleRate;
                info["bitDepth"] = facts.BitDepth; info["channels"] = facts.Channels; info["bitrate"] = facts.Bitrate;
            }
            var storedInfo = SafeInfo(info);
            if (prepared is not null)
            {
                prepared.AcceptReceipt(storedInfo);
                prepared.Ready.TrySetResult(path);
                if (epoch != Interlocked.Read(ref clearEpoch)) return;
                deadline.CancelAfter(prepared.Speculative ? TimeSpan.FromSeconds(120) : Timeout.InfiniteTimeSpan);
                var playedAt = await prepared.Played.Task.WaitAsync(ct).ConfigureAwait(false);
                if (playedAt is null || playedAt <= 0 || playedAt > Now) return;
                confirmed = playedAt.Value;
            }
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return;
            await metadataGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var index = await Read(ct, memory: true).ConfigureAwait(false);
                string key = Key(scope, song);
                string? previous = index.Entries.TryGetValue(key, out var replaced) ? replaced.File : null;
                var evicted = new List<string>();
                while (!index.Entries.ContainsKey(key) && index.Entries.Count >= MaximumEntries)
                {
                    var oldest = index.Entries.OrderBy(pair => pair.Value.ConfirmedAt).First();
                    index.Entries.Remove(oldest.Key); evicted.Add(oldest.Value.File);
                }
                index.Entries[key] = new Entry(candidate, length, sha, signature, confirmed,
                    duration, storedInfo);
                if (!isCurrent() || epoch != Interlocked.Read(ref clearEpoch)) return;
                await Write(index, ct).ConfigureAwait(false);
                candidate = null; // Publish index last; crash before this is an orphan, never a cache hit.
                prepared?.Published();
                if (previous is not null) TryDelete(previous);
                foreach (string file in evicted) TryDelete(file);
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { prepared?.SourceFailed(new OperationCanceledException(token)); }
        catch (DocumentStoreException) { }
        catch (Exception e) when (OptionalFileFailure(e) || e is PlaybackException or
            JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { prepared?.SourceFailed(e); }
        finally
        {
            if (candidate is not null && prepared?.KeepsFile != true) TryDelete(candidate);
            producerLease?.Dispose();
            lock (transferState) if (ReferenceEquals(activeTransfer, deadline)) { activeTransfer = null; activePrepared = null; }
            transferGate.Release();
        }
    }
    internal async Task ClearAsync(CancellationToken token)
    {
        Interlocked.Increment(ref clearEpoch);
        bool keepReader;
        lock (transferState)
        {
            keepReader = activePrepared?.Opened == true;
            if (!keepReader) activeTransfer?.Cancel();
            else activePrepared!.Played.TrySetResult(null);
        }
        if (keepReader)
        {
            // Do not wait for the current sequential download or unlink its
            // growing file. The epoch forbids its later cache publication.
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var index = await Read(token, memory: true).ConfigureAwait(false);
                await Write(new(), token).ConfigureAwait(false);
                foreach (var entry in index.Entries.Values) TryDelete(entry.File);
                await presentations.ClearAsync(token).ConfigureAwait(false);
            }
            finally { metadataGate.Release(); }
            return;
        }
        await transferGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var producerLease = AcquireProducer();
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Write(new(), token).ConfigureAwait(false);
                CheckRoot();
                // Our read leases allow delete sharing: unlink now, let the OS keep
                // their already-open bytes alive until disposal. Other failures are real
                // explicit-cleanup failures, not optional automatic eviction failures.
                foreach (var path in Directory.EnumerateFiles(root, "*.audio"))
                {
                    token.ThrowIfCancellationRequested();
                    File.Delete(FilePath(Path.GetFileName(path)));
                }
                await presentations.ClearAsync(token).ConfigureAwait(false);
            }
            finally { metadataGate.Release(); }
        }
        finally { transferGate.Release(); }
    }
    private FileStream AcquireProducer()
    {
        CheckRoot();
        string path = Path.Combine(root, ".producer.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("CACHE_UNSAFE_PATH");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static JsonObject SafeInfo(JsonObject receipt)
    {
        var info = new JsonObject();
        // Files outlive signed URLs. Persist only recording, quality and authorization metadata.
        foreach (string field in receipt["kind"]?.GetValue<string>() == "script" ? ScriptInfoFields : InfoFields)
            info[field] = receipt[field]?.DeepClone();
        return info;
    }
    private static JsonObject ValidateReceipt(JsonElement receipt, string scope, string song,
        string signature, out long? expires)
    {
        ValidateIdentity(scope, song, signature);
        try
        {
            if (receipt.TryGetProperty("kind", out var kind) && kind.GetString() == "script")
            {
                var info = JsonNode.Parse(receipt.GetRawText())!.AsObject();
                double duration = info["duration"]!.GetValue<double>();
                long size = info["size"]!.GetValue<long>();
                if (!double.IsFinite(duration) || duration < 0 || size < 0 ||
                    info["scope"]!.GetValue<string>() != scope || info["songId"]!.GetValue<string>() != song ||
                    info["qualityCacheSignature"]!.GetValue<string>() != signature)
                    throw MusicPlaybackCacheDataException.Corrupt();
                // Receipts may have unknown extents; persisted entries must have measured positive extents.
                ValidateInfo(JsonSerializer.SerializeToElement(SafeInfo(info)), Key(scope, song), signature,
                    size, duration, decoded: false);
                expires = receipt.TryGetProperty("expiresAt", out var at) ? OptionalLong(at) : null;
                return info;
            }
            expires = MusicSourceCache.ValidateCandidate(receipt, scope, song, signature);
            return JsonNode.Parse(receipt.GetRawText())?.AsObject() ??
                throw MusicPlaybackCacheDataException.Corrupt();
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException or OverflowException or ArgumentException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
    }
    private static void ValidateIdentity(string scope, string song, string? signature = null)
    {
        RequireText(scope, 128);
        RequireText(song, 2048);
        if (signature is not null && !LowerHex(signature, 64))
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private async Task PruneAsync(long requiredBytes, CancellationToken token)
    {
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = await Read(token, memory: true).ConfigureAwait(false);
            var removed = new List<string>();
            var removedSongs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in index.Entries.ToArray())
                if (!Accepted(pair.Value, pair.Value.Signature, Now))
                {
                    index.Entries.Remove(pair.Key);
                    removed.Add(pair.Value.File);
                    removedSongs.Add(pair.Value.Info["songId"]!.GetValue<string>());
                }
            // LRU eviction also makes headroom for a new complete representation; not a hardcoded song limit.
            long used = index.Entries.Sum(p => p.Value.Size);
            foreach (var pair in index.Entries.OrderBy(p => p.Value.ConfirmedAt).ToArray())
            {
                if (used <= Capacity - requiredBytes) break;
                used -= pair.Value.Size;
                index.Entries.Remove(pair.Key);
                removed.Add(pair.Value.File);
                removedSongs.Add(pair.Value.Info["songId"]!.GetValue<string>());
            }
            if (removed.Count > 0) await Write(index, token).ConfigureAwait(false);
            var keep = index.Entries.Values.Select(value => value.File).ToHashSet(StringComparer.Ordinal);
            CheckRoot();
            foreach (var path in Directory.EnumerateFiles(root, "*.audio"))
            {
                string name = Path.GetFileName(path);
                if (!keep.Contains(name)) TryDelete(name);
            }
            removedSongs.ExceptWith(index.Entries.Values.Select(entry => entry.Info["songId"]!.GetValue<string>()));
            if (removedSongs.Count > 0) await presentations.RemoveAsync(removedSongs, Now, token).ConfigureAwait(false);
        }
        finally { metadataGate.Release(); }
    }
    private void TryDelete(string file)
    {
        try { File.Delete(FilePath(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Active read leases keep the file; prune on next use. */ }
    }
    private static bool OptionalFileFailure(Exception e) => e is IOException or UnauthorizedAccessException;
    private sealed class ReadLease(FileStream file, CancellationToken revoked) : INativeMediaReadLease
    {
        public Stream Content => file;
        public long Length => file.Length;
        public CancellationToken Revoked => revoked;
        public ValueTask DisposeAsync() => file.DisposeAsync();
    }
}
