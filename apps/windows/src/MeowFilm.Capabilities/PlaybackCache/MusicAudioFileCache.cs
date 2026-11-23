using System.IO;
using System.Security.Cryptography;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record AudioFileReceipt(
    bool Ok,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    long? ExpiresAt,
    AudioFileInfoRecord Info);

/// <summary>Evictable playback bytes. No DownloadService, export target or durable download task.</summary>
internal sealed partial class MusicAudioFileCache
{
    internal const string StorageOwner = "music.playback-files.v1";
    internal const long Capacity = 256L * 1024 * 1024, Ttl = 30L * 86400000;
    private const int MaximumIndexBytes = 1024 * 1024, MaximumEntries = 2048;

    // Stable provenance, not the runtime generation or settings revision. Each tier/provider is isolated.
    internal static string ScriptSignature(string installation, string platform, string recording, string quality) =>
        MusicRules.CanonicalTupleHash(installation, platform, recording, quality);

    private readonly string root;
    private readonly IStorageDatabase database;
    private bool readable = true;
    private readonly SemaphoreSlim metadataGate = new(1, 1), transferGate = new(1, 1);
    private readonly Func<string, double, CancellationToken, Task<double>> validate;
    private readonly TimeProvider clock;
    private readonly NativeHttpMediaOptions? httpOptions;
    private readonly object transferState = new();
    private CancellationTokenSource? activeTransfer;
    private PreparedFile? activePrepared;
    private long clearEpoch;

    private sealed class Index
    {
        internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, AudioFileCacheRecord> Before { get; } = new(StringComparer.Ordinal);
        internal StorageRevision Revision = StorageRevision.Empty;
        internal long Epoch;
    }

    private sealed class Entry(string file, long size, string sha256, string signature, long confirmedAt,
        double duration, AudioFileInfoRecord info)
    {
        internal string File { get; } = file;
        internal long Size { get; } = size;
        internal string Sha256 { get; } = sha256;
        internal string Signature { get; } = signature;
        internal long ConfirmedAt { get; set; } = confirmedAt;
        internal double Duration { get; } = duration;
        internal AudioFileInfoRecord Info { get; } = info;
    }

    internal sealed partial class PreparedFile : IDisposable
    {
        internal readonly string Scope, Song, Signature;
        internal readonly AudioFileReceipt Receipt;
        internal readonly bool Speculative;

        internal PreparedFile(string scope, string song, string signature, AudioFileReceipt receipt, CancellationToken token, bool speculative)
        {
            Scope = scope; Song = song; Signature = signature; Receipt = receipt; Speculative = speculative;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); SourceToken = Cancellation.Token;
        }

        // Read only after Ready's completion establishes the publication barrier.
        internal AudioFileInfoRecord? VerifiedReceipt { get; private set; }
        internal void AcceptReceipt(AudioFileInfoRecord value) => VerifiedReceipt = value;
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

    internal PreparedFile Prepare(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, bool speculative = true)
    {
        var prepared = new PreparedFile(scope, song, signature, receipt, token, speculative);
        prepared.Work = StoreAsync(scope, song, signature, receipt, isCurrent, prepared.Cancellation.Token, prepared);
        return prepared;
    }

    internal MusicAudioFileCache(string root, IStorageDatabase database,
        Func<string, double, CancellationToken, Task<double>>? validate = null,
        TimeProvider? clock = null, NativeHttpMediaOptions? httpOptions = null)
    {
        this.root = Path.GetFullPath(root); this.database = database;
        this.validate = validate ?? NativeAudioCacheProbe.ValidateAsync; this.clock = clock ?? TimeProvider.System;
        this.httpOptions = httpOptions;
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static string Key(string scope, string song) => MusicRules.CanonicalTupleHash(scope, song);
    private static bool LowerHex(string value, int length) => MusicRules.IsLowerHex(value, length);
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

    private async Task<Index> Read(CancellationToken token, string? key = null, string? song = null)
    {
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                var index = new Index { Revision = db.Revision(StorageOwner), Epoch = Interlocked.Read(ref clearEpoch) };
                if (!Volatile.Read(ref readable)) return index;
                IReadOnlyList<AudioFileCacheRecord> rows;
                if (key is not null) rows = AudioFileCacheStore.ReadOne(db, key) is { } one ? [one] : [];
                else if (song is not null) rows = AudioFileCacheStore.ReadByScriptSong(db, song);
                else rows = [];
                foreach (var record in rows)
                {
                    string id = record.ResourceKey;
                    if (!LowerHex(id, 64)) throw MusicPlaybackCacheDataException.Corrupt();
                    RequireText(record.FileName, 38);
                    RequireText(record.Sha256, 64);
                    RequireText(record.Signature, 64);
                    if (record.FileSize <= 0 || record.ConfirmedAt <= 0 ||
                        record.Duration is not { } duration || !double.IsFinite(duration) || duration <= 0)
                        throw MusicPlaybackCacheDataException.Corrupt();
                    ValidateInfoRecord(record.Info, id, record.Signature, record.FileSize, duration, decoded: true);
                    if (!ValidFileName(record.FileName) || record.FileSize > Capacity ||
                        !LowerHex(record.Sha256, 64) || !LowerHex(record.Signature, 64))
                        throw MusicPlaybackCacheDataException.Corrupt();
                    index.Before.Add(id, record);
                    index.Entries.Add(id, new Entry(record.FileName, record.FileSize, record.Sha256,
                        record.Signature, record.ConfirmedAt, duration, record.Info));
                }
                return index;
            }, token).ConfigureAwait(false);
        }
        catch (StorageException) { token.ThrowIfCancellationRequested(); return new(); }
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

    private static string RequireField(string? value, int maximum, bool empty = false)
    {
        if (value is null) throw MusicPlaybackCacheDataException.Corrupt();
        RequireText(value, maximum, empty);
        return value;
    }

    private static void ValidateInfoRecord(AudioFileInfoRecord info, string key, string signature, long size,
        double duration, bool decoded)
    {
        bool script = info.Kind == "script";
        if (!script && info.Kind != "official")
            throw MusicPlaybackCacheDataException.Corrupt();
        string platform = RequireField(info.Platform, 16);
        string song = RequireField(info.SongId, 2048);
        string recording = RequireField(info.SourceKey, 8192);
        string scope = RequireField(info.Scope, 128);
        if (!(script ? NativeMusicSong.Platforms.Contains(platform) : platform is "wy" or "tx" or "kg") || Key(scope, song) != key)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (!script && (info.Generation is null || info.Generation <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        string quality = RequireField(info.Quality, 64);
        if (script)
        {
            string installation = RequireField(info.InstallationId, 128);
            _ = RequireField(info.ProviderName, 512, empty: true);
            if (!MusicQualityPolicy.LxTypes.Contains(quality) ||
                ScriptSignature(installation, platform, recording, quality) != signature || scope != "script:" + signature)
                throw MusicPlaybackCacheDataException.Corrupt();
        }
        _ = RequireField(info.RequestedQuality, 64);
        string actual = RequireField(info.ActualQuality, 64, empty: true);
        if (info.QualityVerified is not { } qualityVerified || info.PlaybackVerified is not { } playbackVerified)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (qualityVerified && actual.Length == 0 || playbackVerified)
            throw MusicPlaybackCacheDataException.Corrupt();
        string cache = RequireField(info.QualityCacheSignature, 64);
        if (!script && !LowerHex(RequireField(info.QualityPolicySignature, 64), 64) ||
            !LowerHex(cache, 64) || cache != signature)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Duration is not { } infoDuration || !double.IsFinite(infoDuration) ||
            (decoded ? infoDuration <= 0 : infoDuration < 0) || infoDuration != duration)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Size is not { } infoSize || (decoded ? infoSize <= 0 : infoSize < 0) || infoSize != size)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Full is null || decoded && info.Full is not true)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (!script && RequireField(info.Delivery, 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.QualityEvidence is not null) RequireText(info.QualityEvidence, 128, empty: true);
        if (info.Codec is not null) RequireText(info.Codec, 64, empty: true);
        if (info.SampleRate is <= 0 || info.BitDepth is < 0 || info.Channels is <= 0 || info.Bitrate is < 0)
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private static AudioFileCacheRecord Row(string id, Entry value) => new(
        id, value.File, value.Size, value.Sha256, value.Signature, value.ConfirmedAt, value.Duration, value.Info);

    private async Task Write(Index index, CancellationToken token, Func<bool>? current = null)
    {
        if (index.Entries.Count > MaximumEntries) throw MusicPlaybackCacheDataException.Limit();
        var changes = new Dictionary<string, AudioFileCacheRecord?>(); int bytes = 0;
        foreach (var (id, entry) in index.Entries)
        {
            var row = Row(id, entry);
            index.Before.TryGetValue(id, out var previous);
            if (row == previous) continue;
            bytes = checked(bytes + row.EstimateBytes());
            if (bytes > MaximumIndexBytes) throw MusicPlaybackCacheDataException.Limit();
            changes.Add(id, row);
        }
        foreach (var id in index.Before.Keys.Where(id => !index.Entries.ContainsKey(id))) changes.Add(id, null);
        await database.WriteAsync(db =>
        {
            if (db.Revision(StorageOwner) != index.Revision) throw new StorageException(StorageError.Conflict);
            if (!Volatile.Read(ref readable)) db.Execute("DELETE FROM windows_music_audio_files");
            foreach (var (id, value) in changes)
            {
                AudioFileCacheStore.Save(db, id, value);
            }
            db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key IN (SELECT resource_key FROM windows_music_audio_files ORDER BY confirmed_at_ms DESC,resource_key DESC LIMIT -1 OFFSET $limit)", ("$limit", MaximumEntries));
            return db.CommitRevision(StorageOwner, index.Revision);
        }, token, bytes, commit: commit =>
        {
            if (index.Epoch != Interlocked.Read(ref clearEpoch) || current is not null && !current()) throw new OperationCanceledException(token);
            commit();
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ClearIndex(CancellationToken token)
    {
        var epoch = Interlocked.Read(ref clearEpoch);
        var files = await database.WriteAsync(db =>
        {
            var names = db.Query("SELECT file_name FROM windows_music_audio_files", row => row.GetString(0));
            db.Execute("DELETE FROM windows_music_audio_files");
            db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
            return names;
        }, token).ConfigureAwait(false);
        if (epoch == Interlocked.Read(ref clearEpoch)) Volatile.Write(ref readable, true);
        return files;
    }

    internal async Task<INativeMediaReadLease?> OpenAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        long epoch = Interlocked.Read(ref clearEpoch);
        FileStream? file = null;
        try
        {
            var index = await Read(token, key: Key(scope, song)).ConfigureAwait(false);
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
        catch (StorageException) { token.ThrowIfCancellationRequested(); return null; }
        catch (Exception e) when (OptionalFileFailure(e)) { token.ThrowIfCancellationRequested(); return null; }
        finally { if (file is not null) await file.DisposeAsync(); }
    }

    internal async Task<AudioFileInfoRecord?> ReadInfoAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            var index = await Read(token, key: Key(scope, song)).ConfigureAwait(false);
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    !index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now) ||
                    !File.Exists(FilePath(entry.File))) return null;
                return entry.Info;
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { token.ThrowIfCancellationRequested(); return null; }
        catch (Exception e) when (OptionalFileFailure(e)) { token.ThrowIfCancellationRequested(); return null; }
    }

    internal async Task<AudioFileInfoRecord[]> ReadScriptHistoryAsync(string song, CancellationToken token)
    {
        RequireText(song, 2048);
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            var index = await Read(token, song: song).ConfigureAwait(false);
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref clearEpoch)) return [];
                return index.Entries.Values.Where(entry => entry.Info.Kind == "script" &&
                    entry.Info.SongId == song && Accepted(entry, entry.Signature, Now) &&
                    File.Exists(FilePath(entry.File))).OrderByDescending(entry => entry.ConfirmedAt)
                    .Select(entry => entry.Info).ToArray();
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { token.ThrowIfCancellationRequested(); return []; }
        catch (Exception e) when (OptionalFileFailure(e)) { token.ThrowIfCancellationRequested(); return []; }
    }

    internal async Task TouchAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = await Read(token, key: Key(scope, song)).ConfigureAwait(false);
            if (!index.Entries.TryGetValue(Key(scope, song), out var entry) || !Accepted(entry, signature, Now)) return;
            entry.ConfirmedAt = Now; await Write(index, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { }
        finally { metadataGate.Release(); }
    }

    internal async Task EraseAsync(string scope, string song, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = await Read(token, key: Key(scope, song)).ConfigureAwait(false);
            string key = Key(scope, song);
            index.Entries.Remove(key, out var entry);
            // Publish even an empty index so a pending initial read cannot restore it.
            await Write(index, token).ConfigureAwait(false);
            if (entry is not null) TryDelete(entry.File);
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { }
        finally { metadataGate.Release(); }
    }

    // Called only after the actual player's Started event. Probe callbacks never call this method.
    internal Task StorePlayedAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token) => StoreAsync(scope, song, signature, receipt, isCurrent, token, null);

    private async Task StoreAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile? prepared)
    {
        try
        {
            await StoreCoreAsync(scope, song, signature, receipt, isCurrent, token, prepared).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
        finally { prepared?.FinishWork(); }
    }

    private async Task StoreCoreAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile? prepared)
    {
        long epoch = Interlocked.Read(ref clearEpoch);
        ValidateReceipt(receipt, scope, song, signature, out long? expires);
        var info = receipt.Info;
        double expected = info.Duration!.Value;
        long expectedSize = info.Size!.Value;
        bool full = info.Full!.Value;
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
            var headers = info.Kind == "script" && receipt.Headers.Count > 0
                ? new Dictionary<string, string>(receipt.Headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous))
            {
                long size = await NativeHttpMediaTransfer.CopyCompleteAsync(new Uri(receipt.Url),
                    headers, output, maximum, httpOptions, ct,
                    prepared is null ? null : prepared.ReceivedBytes,
                    prepared is null ? null : length => prepared.BeginFile(path, length)).ConfigureAwait(false);
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
            var updatedInfo = info with { Duration = duration, Size = length };
            if (facts is { Quality.Length: > 0 })
            {
                updatedInfo = updatedInfo with
                {
                    ActualQuality = facts.Quality,
                    QualityVerified = true,
                    QualityEvidence = "complete-file-native-decoded",
                    Codec = facts.Codec,
                    SampleRate = facts.SampleRate,
                    BitDepth = facts.BitDepth,
                    Channels = facts.Channels,
                    Bitrate = facts.Bitrate
                };
            }
            var storedInfo = SafeInfo(updatedInfo);
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
                if (!isCurrent() || epoch != Interlocked.Read(ref clearEpoch)) return;
                string key = Key(scope, song);
                var row = new AudioFileCacheRecord(key, candidate, length, sha, signature, confirmed, duration, storedInfo);
                int bytes = row.EstimateBytes();
                if (bytes > MaximumIndexBytes) throw MusicPlaybackCacheDataException.Limit();
                var (previous, evicted) = await database.WriteAsync(db =>
                {
                    if (!Volatile.Read(ref readable)) db.Execute("DELETE FROM windows_music_audio_files");
                    string? prevFile = db.TryQuerySingle(
                        "SELECT file_name FROM windows_music_audio_files WHERE resource_key=$key",
                        r => r.GetString(0), out var existingFile, ("$key", key))
                        ? existingFile : null;
                    var evictedFiles = new List<string>();
                    if (prevFile is null)
                    {
                        long count = db.Scalar<long>("SELECT count(*) FROM windows_music_audio_files");
                        long overflow = count - (MaximumEntries - 1);
                        if (overflow > 0)
                        {
                            var victims = db.Query("""
                                SELECT resource_key,file_name FROM windows_music_audio_files
                                ORDER BY confirmed_at_ms ASC, resource_key ASC
                                LIMIT $n
                                """, r => (Key: r.GetString(0), File: r.GetString(1)), ("$n", overflow));
                            foreach (var v in victims)
                            {
                                db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key=$k", ("$k", v.Key));
                                evictedFiles.Add(v.File);
                            }
                        }
                    }
                    AudioFileCacheStore.Save(db, key, row);
                    db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                    return (prevFile, evictedFiles);
                }, ct, bytes, commit: commit =>
                {
                    if (epoch != Interlocked.Read(ref clearEpoch) || !isCurrent()) throw new OperationCanceledException(ct);
                    commit();
                }).ConfigureAwait(false);
                candidate = null; // Publish index last; crash before this is an orphan, never a cache hit.
                prepared?.Published();
                if (previous is not null) TryDelete(previous);
                foreach (string file in evicted) TryDelete(file);
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { prepared?.SourceFailed(new OperationCanceledException(token)); }
        catch (StorageException) { }
        catch (Exception e) when (OptionalFileFailure(e) || e is PlaybackException or
            InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
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
        Volatile.Write(ref readable, false);
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
                var names = await ClearIndex(token).ConfigureAwait(false);
                foreach (var name in names) TryDelete(name);
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
                await ClearIndex(token).ConfigureAwait(false);
                CheckRoot();
                // Our read leases allow delete sharing: unlink now, let the OS keep
                // their already-open bytes alive until disposal. Other failures are real
                // explicit-cleanup failures, not optional automatic eviction failures.
                foreach (var path in Directory.EnumerateFiles(root, "*.audio"))
                {
                    token.ThrowIfCancellationRequested();
                    File.Delete(FilePath(Path.GetFileName(path)));
                }
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

    private static AudioFileInfoRecord SafeInfo(AudioFileInfoRecord info) =>
        info.Kind == "script"
            ? info with { Generation = null, QualityPolicySignature = null, Delivery = null }
            : info with { InstallationId = null, ProviderName = null };

    private static void ValidateReceipt(AudioFileReceipt receipt, string scope, string song,
        string signature, out long? expires)
    {
        ValidateIdentity(scope, song, signature);
        var info = receipt.Info;
        bool script = info.Kind == "script";
        if (!script)
        {
            if (!receipt.Ok || receipt.Headers.Count > 0)
                throw MusicPlaybackCacheDataException.Corrupt();
            RequireText(receipt.Url, 16384);
            MusicSourceCache.ValidateMediaUrl(receipt.Url);
        }
        else
        {
            RequireText(receipt.Url, 16384);
            MusicSourceCache.ValidateMediaUrl(receipt.Url);
        }
        if (receipt.ExpiresAt is <= 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Scope != scope || info.SongId != song || info.QualityCacheSignature != signature)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Duration is not { } duration || !double.IsFinite(duration) || duration < 0 ||
            info.Size is not { } size || size < 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        // Receipts may have unknown extents; persisted entries must have measured positive extents.
        ValidateInfoRecord(SafeInfo(info), Key(scope, song), signature, size, duration, decoded: false);
        expires = receipt.ExpiresAt;
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
            long epoch = Interlocked.Read(ref clearEpoch);
            long now = Now;
            var keep = await database.WriteAsync(db =>
            {
                if (!Volatile.Read(ref readable)) return new HashSet<string>(StringComparer.Ordinal);
                var summaries = AudioFileCacheStore.ReadSummaries(db);
                var alive = new List<(string Key, string FileName, long FileSize)>(summaries.Count);
                var toDelete = new List<string>();
                long used = 0;
                foreach (var item in summaries)
                {
                    bool valid = item.ConfirmedAtMs > 0 && item.ConfirmedAtMs <= now &&
                        (item.ConfirmedAtMs > long.MaxValue - Ttl ? long.MaxValue : item.ConfirmedAtMs + Ttl) > now &&
                        item.FileSize is > 0 and <= Capacity;
                    if (!valid)
                    {
                        toDelete.Add(item.Key);
                        continue;
                    }
                    alive.Add((item.Key, item.FileName, item.FileSize));
                    used += item.FileSize;
                }
                int keptStart = 0;
                while (keptStart < alive.Count && used > Capacity - requiredBytes)
                {
                    used -= alive[keptStart].FileSize;
                    toDelete.Add(alive[keptStart].Key);
                    keptStart++;
                }
                if (toDelete.Count > 0)
                {
                    foreach (string key in toDelete)
                        db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key=$key", ("$key", key));
                    db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                }
                var retained = new HashSet<string>(alive.Count - keptStart, StringComparer.Ordinal);
                for (int i = keptStart; i < alive.Count; i++) retained.Add(alive[i].FileName);
                return retained;
            }, token, commit: commit =>
            {
                if (epoch != Interlocked.Read(ref clearEpoch)) throw new OperationCanceledException(token);
                commit();
            }).ConfigureAwait(false);
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
