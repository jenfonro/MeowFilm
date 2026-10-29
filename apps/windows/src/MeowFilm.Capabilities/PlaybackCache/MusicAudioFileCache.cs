using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Evictable playback bytes. No DownloadService, export target or durable download task.</summary>
internal sealed class MusicAudioFileCache
{
    internal const string Namespace = "music.playback-files.v1";
    internal const long Capacity = 256L * 1024 * 1024, Ttl = 30L * 86400000;
    private const int MaximumIndexBytes = 1024 * 1024, MaximumEntries = 2048;
    private static readonly string[] EntryFields =
        ["file", "size", "sha256", "signature", "confirmedAt", "licenseExpiresAt", "duration", "info"];
    private static readonly string[] InfoFields =
    [
        "kind", "platform", "songId", "sourceKey", "scope", "generation",
        "quality", "requestedQuality", "actualQuality", "qualityVerified", "playbackVerified",
        "qualityPolicySignature", "qualityCacheSignature", "duration", "size", "full",
        "licenseExpiresAt", "delivery", "offlineAllowed", "authorizationExpiresAt",
        "qualityEvidence", "codec", "sampleRate", "bitDepth", "channels", "bitrate"
    ];
    private readonly string root;
    private readonly IProtectedDocumentStore store;
    private readonly SemaphoreSlim metadataGate = new(1, 1), transferGate = new(1, 1);
    private readonly Func<string, double, CancellationToken, Task<double>> validate;
    private readonly TimeProvider clock;
    private readonly NativeHttpMediaOptions? httpOptions;
    private readonly object transferState = new();
    private CancellationTokenSource? activeTransfer;
    private long clearEpoch;
    private sealed class Index
    {
        internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Entry(string file, long size, string sha256, string signature, long confirmedAt,
        long? licenseExpiresAt, double duration, JsonObject info)
    {
        internal string File { get; } = file;
        internal long Size { get; } = size;
        internal string Sha256 { get; } = sha256;
        internal string Signature { get; } = signature;
        internal long ConfirmedAt { get; set; } = confirmedAt;
        internal long? LicenseExpiresAt { get; } = licenseExpiresAt;
        internal double Duration { get; } = duration;
        internal JsonObject Info { get; } = info;
    }
    internal sealed class PreparedFile(string scope, string song, string signature, JsonElement receipt,
        CancellationToken token) : IDisposable
    {
        internal readonly string Scope = scope, Song = song, Signature = signature;
        internal readonly JsonElement Receipt = receipt.Clone();
        // Read only after Ready's completion establishes the publication barrier.
        internal JsonElement VerifiedReceipt { get; private set; }
        internal void AcceptReceipt(JsonObject value) => VerifiedReceipt = JsonSerializer.SerializeToElement(value);
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource<string?> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<long?> Played = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work = Task.CompletedTask;
        private int disposed;
        internal bool Matches(string scope, string song, string signature) =>
            !Cancellation.IsCancellationRequested && Scope == scope && Song == song && Signature == signature;
        internal async Task<INativeMediaReadLease?> OpenAsync(CancellationToken token)
        {
            // Never stall playback behind a speculative transfer.
            if (!Ready.Task.IsCompletedSuccessfully || Ready.Task.Result is not { } path ||
                Cancellation.IsCancellationRequested) return null;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                token.ThrowIfCancellationRequested();
                var lease = new ReadLease(stream, token); stream = null; return lease;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (OptionalFileFailure(e)) { return null; }
            finally { if (stream is not null) await stream.DisposeAsync(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Cancellation.Cancel(); Played.TrySetResult(null);
            _ = Work.ContinueWith(_ => Cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    internal PreparedFile Prepare(string scope, string song, string signature, JsonElement receipt,
        Func<bool> isCurrent, CancellationToken token)
    {
        var prepared = new PreparedFile(scope, song, signature, receipt, token);
        prepared.Work = StoreAsync(scope, song, signature, receipt, isCurrent, prepared.Cancellation.Token, prepared);
        return prepared;
    }
    internal MusicAudioFileCache(string root, IProtectedDocumentStore store,
        Func<string, double, CancellationToken, Task<double>>? validate = null,
        TimeProvider? clock = null, NativeHttpMediaOptions? httpOptions = null)
    {
        this.root = Path.GetFullPath(root); this.store = store;
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
        return Math.Min(ttl, entry.LicenseExpiresAt ?? long.MaxValue) > now;
    }
    private async Task<(Index Index, DocumentFingerprint Fingerprint)> Read(CancellationToken token)
    {
        ProtectedDocument saved;
        try { saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false); }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
        { throw MusicPlaybackCacheDataException.FromStore(error); }
        try
        {
            return (Decode(saved.Data, saved.Fingerprint), saved.Fingerprint);
        }
        finally { if (saved.Data is not null) CryptographicOperations.ZeroMemory(saved.Data); }
    }
    private static Index Decode(byte[]? data, DocumentFingerprint fingerprint)
    {
        if (data is null)
        {
            if (fingerprint != DocumentFingerprint.Missing) throw MusicPlaybackCacheDataException.Corrupt();
            return new();
        }
        if (data.Length == 0 || DocumentFingerprint.FromBytes(data) != fingerprint)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (data.Length > MaximumIndexBytes) throw MusicPlaybackCacheDataException.Limit();
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
            CheckJson(document.RootElement);
            RequireFields(document.RootElement, "schema", "entries");
            var schema = document.RootElement.GetProperty("schema");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1")
                throw MusicPlaybackCacheDataException.UnsupportedVersion();
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
                RequireFields(raw, EntryFields);
                string file = Text(raw.GetProperty("file"), 38);
                long size = CanonicalLong(raw.GetProperty("size"));
                string sha = Text(raw.GetProperty("sha256"), 64);
                string signature = Text(raw.GetProperty("signature"), 512);
                long confirmed = CanonicalLong(raw.GetProperty("confirmedAt"));
                long? license = OptionalLong(raw.GetProperty("licenseExpiresAt"));
                double duration = PositiveDouble(raw.GetProperty("duration"));
                var info = raw.GetProperty("info");
                ValidateInfo(info, property.Name, signature, size, duration, license);
                if (!ValidFileName(file) || !files.Add(file) || size > Capacity ||
                    totalSize > Capacity - size || !LowerHex(sha, 64) ||
                    !LowerHex(signature, 64) || license is { } expiry && expiry <= confirmed ||
                    !index.Entries.TryAdd(property.Name, new Entry(file, size, sha, signature,
                        confirmed, license, duration, JsonNode.Parse(info.GetRawText())!.AsObject())))
                    throw MusicPlaybackCacheDataException.Corrupt();
                totalSize += size;
            }
            return index;
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
    }
    private static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw MusicPlaybackCacheDataException.Corrupt();
                CheckJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJson(item);
    }
    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != fields.Length || fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
            throw MusicPlaybackCacheDataException.Corrupt();
    }
    private static long CanonicalLong(JsonElement value, bool allowZero = false)
    {
        string raw = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
        if (raw.Length is < 1 or > 19 || raw.Length > 1 && raw[0] == '0' ||
            raw.Any(c => c is < '0' or > '9') ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long result) ||
            (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }
    private static long? OptionalLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : CanonicalLong(value);
    private static double PositiveDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) || result <= 0 ||
            value.GetRawText() != JsonSerializer.Serialize(result))
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
        double duration, long? license)
    {
        RequireFields(info, InfoFields);
        if (Text(info.GetProperty("kind"), 32) != "official")
            throw MusicPlaybackCacheDataException.Corrupt();
        string platform = Text(info.GetProperty("platform"), 16);
        string song = Text(info.GetProperty("songId"), 2048);
        _ = Text(info.GetProperty("sourceKey"), 8192);
        string scope = Text(info.GetProperty("scope"), 128);
        if (platform is not ("wy" or "tx" or "kg") || Key(scope, song) != key)
            throw MusicPlaybackCacheDataException.Corrupt();
        _ = CanonicalLong(info.GetProperty("generation"));
        _ = Text(info.GetProperty("quality"), 64);
        _ = Text(info.GetProperty("requestedQuality"), 64);
        string actual = Text(info.GetProperty("actualQuality"), 64, true);
        bool qualityVerified = Boolean(info.GetProperty("qualityVerified"));
        if (qualityVerified && actual.Length == 0 || Boolean(info.GetProperty("playbackVerified")))
            throw MusicPlaybackCacheDataException.Corrupt();
        string policy = Text(info.GetProperty("qualityPolicySignature"), 64);
        string cache = Text(info.GetProperty("qualityCacheSignature"), 64);
        if (!LowerHex(policy, 64) || !LowerHex(cache, 64) || cache != signature)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (PositiveDouble(info.GetProperty("duration")) != duration ||
            CanonicalLong(info.GetProperty("size")) != size ||
            !Boolean(info.GetProperty("full")) ||
            OptionalLong(info.GetProperty("licenseExpiresAt")) != license ||
            Text(info.GetProperty("delivery"), 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        bool offline = Boolean(info.GetProperty("offlineAllowed"));
        long? authorization = OptionalLong(info.GetProperty("authorizationExpiresAt"));
        if (authorization != license || offline != (license is not null))
            throw MusicPlaybackCacheDataException.Corrupt();
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
                ["licenseExpiresAt"] = value.LicenseExpiresAt,
                ["duration"] = value.Duration,
                ["info"] = value.Info.DeepClone()
            };
        }
        return new JsonObject { ["schema"] = 1, ["entries"] = entries };
    }
    private async Task Write(Index index, DocumentFingerprint before, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Encode(index));
        try
        {
            if (bytes.Length > MaximumIndexBytes) throw MusicPlaybackCacheDataException.Limit();
            _ = Decode(bytes, DocumentFingerprint.FromBytes(bytes));
            try
            {
                var expected = DocumentFingerprint.FromBytes(bytes);
                var receipt = await store.WriteAsync(Namespace, bytes, before, token).ConfigureAwait(false);
                if (receipt != expected) throw new MusicPlaybackCacheCommitException();
            }
            catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
            { throw MusicPlaybackCacheDataException.FromStore(error); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal async Task<INativeMediaReadLease?> OpenAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        FileStream? file = null;
        try
        {
            var (index, _) = await Read(token).ConfigureAwait(false);
            if (!index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now)) return null;
            string path = FilePath(entry.File);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (file.Length != entry.Size || file.Length is <= 0 or > Capacity) return null;
            string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            if (digest != entry.Sha256) return null;
            file.Position = 0; token.ThrowIfCancellationRequested();
            var lease = new ReadLease(file, token); file = null; return lease;
        }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error)) { return null; }
        catch (Exception e) when (OptionalFileFailure(e)) { return null; }
        finally { if (file is not null) await file.DisposeAsync(); metadataGate.Release(); }
    }
    internal async Task<JsonObject?> ReadInfoAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (index, _) = await Read(token).ConfigureAwait(false);
            if (!index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now) ||
                !File.Exists(FilePath(entry.File))) return null;
            return entry.Info.DeepClone().AsObject();
        }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error)) { return null; }
        catch (Exception e) when (OptionalFileFailure(e)) { return null; }
        finally { metadataGate.Release(); }
    }
    internal async Task TouchAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (index, before) = await Read(token).ConfigureAwait(false);
            if (!index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now)) return;
            entry.ConfirmedAt = Now; await Write(index, before, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error)) { }
        finally { metadataGate.Release(); }
    }
    internal async Task EraseAsync(string scope, string song, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (index, before) = await Read(token).ConfigureAwait(false);
            string key = Key(scope, song);
            if (!index.Entries.Remove(key, out var entry)) return;
            await Write(index, before, token).ConfigureAwait(false);
            TryDelete(entry.File);
        }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error)) { }
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
        finally { prepared?.Ready.TrySetResult(null); }
    }
    private async Task StoreCoreAsync(string scope, string song, string signature, JsonElement receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile? prepared)
    {
        long epoch = Interlocked.Read(ref clearEpoch);
        var info = ValidateReceipt(receipt, scope, song, signature, out long? expires);
        double expected = info["duration"]!.GetValue<double>();
        long expectedSize = info["size"]!.GetValue<long>();
        bool full = info["full"]!.GetValue<bool>();
        long? license = info["licenseExpiresAt"]?.GetValue<long>();
        if (!full || expected <= 0 || expectedSize > Capacity ||
            expires is { } urlExpiry && urlExpiry <= Now ||
            license is { } licenseExpiry && licenseExpiry <= Now)
            return;
        if (!await transferGate.WaitAsync(0, token).ConfigureAwait(false))
            return; // One bounded producer per App.
        string? candidate = null;
        FileStream? producerLease = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (transferState)
            {
                activeTransfer = deadline;
                if (epoch != Interlocked.Read(ref clearEpoch)) deadline.Cancel();
            }
            // Online playback caching is not an offline entitlement. Keep the
            // explicit false flag in the receipt; the consumer must reacquire
            // official authorization before opening these bytes on a later play.
            long confirmed = Now;
            deadline.CancelAfter(TimeSpan.FromSeconds(120));
            var ct = deadline.Token;
            // Reserve at most the remaining cache capacity, including orphan files, before fetching.
            // Validate and prune the current index before any filesystem producer
            // lease or network request. An incompatible document stays byte-for-byte
            // untouched and cannot be downgraded to a cache miss.
            await ValidateCurrentIndexAsync(ct).ConfigureAwait(false);
            CheckRoot();
            producerLease = AcquireProducer();
            await PruneAsync(expectedSize > 0 ? expectedSize : Capacity / 4, ct).ConfigureAwait(false);
            long occupied = Directory.EnumerateFiles(root, "*.audio").Sum(p => new FileInfo(p).Length);
            long maximum = Capacity - occupied;
            if (maximum <= 0 || expectedSize > maximum) return;
            candidate = Guid.NewGuid().ToString("N") + ".audio";
            string path = FilePath(candidate);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using var input = await HttpMediaInput.OpenAsync(new Uri(info["url"]!.GetValue<string>()),
                    new Dictionary<string, string>(), httpOptions, ct).ConfigureAwait(false);
                long size = await input.CopyCompleteAsync(output, maximum, ct).ConfigureAwait(false);
                if (expectedSize > 0 && size != expectedSize) return;
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
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
            if (!double.IsFinite(duration) || Math.Abs(duration - expected) > Math.Max(2, expected * .02)) return;
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
            ValidateInfo(JsonSerializer.SerializeToElement(storedInfo), Key(scope, song),
                signature, length, duration, license);
            if (prepared is not null)
            {
                prepared.AcceptReceipt(storedInfo);
                prepared.Ready.TrySetResult(path);
                var playedAt = await prepared.Played.Task.WaitAsync(ct).ConfigureAwait(false);
                if (playedAt is null || playedAt <= 0 || playedAt > Now) return;
                confirmed = playedAt.Value;
            }
            ct.ThrowIfCancellationRequested();
            if (!isCurrent() || license is { } currentLicense && currentLicense <= Now) return;
            await metadataGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (index, before) = await Read(ct).ConfigureAwait(false);
                string key = Key(scope, song);
                string? previous = index.Entries.TryGetValue(key, out var replaced) ? replaced.File : null;
                var evicted = new List<string>();
                while (!index.Entries.ContainsKey(key) && index.Entries.Count >= MaximumEntries)
                {
                    var oldest = index.Entries.OrderBy(pair => pair.Value.ConfirmedAt).First();
                    index.Entries.Remove(oldest.Key); evicted.Add(oldest.Value.File);
                }
                index.Entries[key] = new Entry(candidate, length, sha, signature, confirmed,
                    license, duration, storedInfo);
                if (!isCurrent() || epoch != Interlocked.Read(ref clearEpoch)) return;
                await Write(index, before, ct).ConfigureAwait(false);
                candidate = null; // Publish index last; crash before this is an orphan, never a cache hit.
                if (previous is not null) TryDelete(previous);
                foreach (string file in evicted) TryDelete(file);
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error)) { }
        catch (Exception e) when (OptionalFileFailure(e) || e is PlaybackException or
            JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException) { }
        finally
        {
            if (candidate is not null) TryDelete(candidate);
            producerLease?.Dispose();
            lock (transferState) if (ReferenceEquals(activeTransfer, deadline)) activeTransfer = null;
            transferGate.Release();
        }
    }
    internal async Task ClearAsync(CancellationToken token)
    {
        Interlocked.Increment(ref clearEpoch);
        lock (transferState) activeTransfer?.Cancel();
        await transferGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var producerLease = AcquireProducer();
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ProtectedDocument saved;
                try { saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false); }
                catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
                { throw MusicPlaybackCacheDataException.FromStore(error); }
                try
                {
                    if (saved.Data is null && saved.Fingerprint != DocumentFingerprint.Missing ||
                        saved.Data is not null && DocumentFingerprint.FromBytes(saved.Data) != saved.Fingerprint)
                        throw MusicPlaybackCacheDataException.Corrupt();
                    await Write(new(), saved.Fingerprint, token).ConfigureAwait(false);
                }
                finally { if (saved.Data is not null) CryptographicOperations.ZeroMemory(saved.Data); }
                CheckRoot();
                foreach (var path in Directory.EnumerateFiles(root, "*.audio")) TryDelete(Path.GetFileName(path));
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
        foreach (string field in InfoFields)
            info[field] = receipt[field]?.DeepClone();
        return info;
    }
    private static JsonObject ValidateReceipt(JsonElement receipt, string scope, string song,
        string signature, out long? expires)
    {
        ValidateIdentity(scope, song, signature);
        try
        {
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
    private async Task ValidateCurrentIndexAsync(CancellationToken token)
    {
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try { _ = await Read(token).ConfigureAwait(false); }
        finally { metadataGate.Release(); }
    }
    private async Task PruneAsync(long requiredBytes, CancellationToken token)
    {
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (index, before) = await Read(token).ConfigureAwait(false);
            var removed = new List<string>();
            foreach (var pair in index.Entries.ToArray())
                if (!Accepted(pair.Value, pair.Value.Signature, Now))
                {
                    index.Entries.Remove(pair.Key);
                    removed.Add(pair.Value.File);
                }
            // LRU eviction also makes headroom for a new complete representation; not a hardcoded song limit.
            long used = index.Entries.Sum(p => p.Value.Size);
            foreach (var pair in index.Entries.OrderBy(p => p.Value.ConfirmedAt).ToArray())
            {
                if (used <= Capacity - requiredBytes) break;
                used -= pair.Value.Size;
                index.Entries.Remove(pair.Key);
                removed.Add(pair.Value.File);
            }
            if (removed.Count > 0) await Write(index, before, token).ConfigureAwait(false);
            var keep = index.Entries.Values.Select(value => value.File).ToHashSet(StringComparer.Ordinal);
            CheckRoot();
            foreach (var path in Directory.EnumerateFiles(root, "*.audio"))
            {
                string name = Path.GetFileName(path);
                if (!keep.Contains(name)) TryDelete(name);
            }
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
