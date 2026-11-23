using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.PlaybackCache.PlaybackCacheValidation;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record AudioFileReceipt(
    bool Ok,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    long? ExpiresAt,
    AudioFileInfoRecord Info);

internal sealed record MusicAudioFileCacheStats(
    [property: JsonPropertyName("entryCount")] int EntryCount,
    [property: JsonPropertyName("totalDiskBytes")] long TotalDiskBytes,
    [property: JsonPropertyName("pendingDeleteCount")] int PendingDeleteCount,
    [property: JsonPropertyName("pendingDeleteBytes")] long PendingDeleteBytes,
    [property: JsonPropertyName("activeReadLeases")] int ActiveReadLeases,
    [property: JsonPropertyName("activeTemporaries")] int ActiveTemporaries,
    [property: JsonPropertyName("capacityBytes")] long CapacityBytes,
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("rootPath")] string RootPath);

/// <summary>
/// Evictable local playback audio cache (256 MiB, 30-day TTL), aligned with iOS:
/// - Multi-dimensional key Hash(scope, song, signature) isolates distinct sources/qualities.
/// - Atomic .part -> .audio commit prevents truncated files on crash.
/// - Active read leases defer unlink until playback finishes.
/// - SHA-256 verification runs outside the metadata gate.
/// </summary>
internal sealed partial class MusicAudioFileCache : IAsyncDisposable
{
    internal const string StorageOwner = "music.playback-files.v1";
    internal const long Capacity = 256L * 1024 * 1024, Ttl = 30L * 86400000;
    private const int MaximumIndexBytes = 1024 * 1024, MaximumEntries = 2048;

    internal static string ScriptSignature(string installation, string platform, string recording, string quality) =>
        MusicRules.CanonicalTupleHash(installation, platform, recording, quality);

    private readonly string root;
    private readonly IStorageDatabase database;
    private readonly SemaphoreSlim metadataGate = new(1, 1), transferGate = new(1, 1);
    private readonly Func<string, double, CancellationToken, Task<double>> validate;
    private readonly NativeHttpMediaOptions? httpOptions;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateGate = new();
    private readonly Dictionary<string, int> activeReadLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pendingDeletes = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeTemporaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreparedFile> activeByKey = new(StringComparer.Ordinal);
    private CancellationTokenSource? activeTransfer;
    private PreparedFile? activePrepared;
    private long clearEpoch, officialClearEpoch;
    private bool closed, startupSwept;
    private Task? disposal;

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
        internal readonly string Scope, Song, Signature, ResourceKey;
        internal readonly AudioFileReceipt Receipt;
        internal readonly bool Speculative;
        internal readonly bool IsOfficial;
        private readonly MusicAudioFileCache owner;

        internal PreparedFile(MusicAudioFileCache owner, string scope, string song, string signature,
            AudioFileReceipt receipt, CancellationToken token, bool speculative)
        {
            this.owner = owner;
            Scope = scope; Song = song; Signature = signature;
            ResourceKey = Key(scope, song, signature);
            Receipt = receipt; Speculative = speculative;
            IsOfficial = receipt.Info.Kind == "official";
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, owner.lifetime.Token);
            SourceToken = Cancellation.Token;
        }

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
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
                token.ThrowIfCancellationRequested();
                var lease = new GrowingReadLease(this, stream, source.Value.Length, token);
                stream = null;
                lock (bytesGate) readers++;
                Interlocked.Exchange(ref opened, 1);
                return lease;
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
        var key = Key(scope, song, signature);
        PreparedFile? previousToCancel = null;
        var prepared = new PreparedFile(this, scope, song, signature, receipt, token, speculative);
        lock (stateGate)
        {
            if (closed)
            {
                prepared.Cancellation.Cancel();
                prepared.FinishWork();
                return prepared;
            }
            if (activeByKey.TryGetValue(key, out var existing))
            {
                if (speculative && !existing.Cancellation.IsCancellationRequested)
                {
                    // Avoid duplicate speculative work for a key already in flight.
                    prepared.Cancellation.Cancel();
                    prepared.FinishWork();
                    return prepared;
                }
                if (!speculative && existing.Speculative)
                    previousToCancel = existing;
            }
            activeByKey[key] = prepared;
        }
        try { previousToCancel?.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        prepared.Work = StoreAsync(scope, song, signature, receipt, isCurrent, prepared.Cancellation.Token, prepared);
        return prepared;
    }

    internal MusicAudioFileCache(string root, IStorageDatabase database,
        Func<string, double, CancellationToken, Task<double>>? validate = null,
        NativeHttpMediaOptions? httpOptions = null)
    {
        this.root = Path.GetFullPath(root); this.database = database;
        this.validate = validate ?? NativeAudioCacheProbe.ValidateAsync;
        this.httpOptions = httpOptions;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Key(string scope, string song, string signature) =>
        MusicRules.CanonicalTupleHash(scope, song, signature);

    private static bool ValidCommittedName(string name) =>
        name.Length == 38 && name.EndsWith(".audio", StringComparison.Ordinal) && LowerHex(name[..32], 32);

    private static bool ValidPartName(string name) =>
        name.Length == 37 && name.EndsWith(".part", StringComparison.Ordinal) && LowerHex(name[..32], 32);

    private void EnsureRoot() => Directory.CreateDirectory(root);

    private string FilePath(string name)
    {
        if (!ValidCommittedName(name) && !ValidPartName(name))
            throw new IOException("CACHE_INVALID_NAME");
        EnsureRoot();
        return Path.Combine(root, name);
    }

    private void AcquireReadLease(string fileName)
    {
        lock (stateGate)
            activeReadLeases[fileName] = activeReadLeases.GetValueOrDefault(fileName) + 1;
    }

    private void ReleaseReadLease(string fileName)
    {
        bool shouldDelete = false;
        lock (stateGate)
        {
            if (!activeReadLeases.TryGetValue(fileName, out int count)) return;
            if (count <= 1)
            {
                activeReadLeases.Remove(fileName);
                shouldDelete = pendingDeletes.Remove(fileName);
            }
            else
            {
                activeReadLeases[fileName] = count - 1;
            }
        }
        if (shouldDelete) TryDeleteFile(fileName);
    }

    private void DeleteOrDefer(string fileName, long fileSize)
    {
        if (!ValidCommittedName(fileName) && !ValidPartName(fileName)) return;
        bool deferred = false;
        lock (stateGate)
        {
            if (activeReadLeases.GetValueOrDefault(fileName) > 0)
            {
                pendingDeletes[fileName] = Math.Max(pendingDeletes.GetValueOrDefault(fileName), fileSize);
                deferred = true;
            }
            else
            {
                pendingDeletes.Remove(fileName);
            }
        }
        if (!deferred) TryDeleteFile(fileName);
    }

    private void RegisterTemporary(string fileName)
    {
        lock (stateGate) activeTemporaries.Add(fileName);
    }

    private void UnregisterTemporary(string fileName)
    {
        lock (stateGate) activeTemporaries.Remove(fileName);
    }

    private bool IsProtectedFile(string fileName)
    {
        lock (stateGate)
            return activeReadLeases.ContainsKey(fileName) || pendingDeletes.ContainsKey(fileName) ||
                   activeTemporaries.Contains(fileName);
    }

    private long DeferredDeleteBytes()
    {
        lock (stateGate)
        {
            long sum = 0;
            foreach (var v in pendingDeletes.Values) sum += v;
            return sum;
        }
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
                    if (!ValidCommittedName(record.FileName) || record.FileSize > Capacity ||
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
        if (!(script ? NativeMusicSong.Platforms.Contains(platform) : platform is "wy" or "tx" or "kg") ||
            Key(scope, song, signature) != key)
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
            foreach (var (id, value) in changes)
                AudioFileCacheStore.Save(db, id, value);
            return db.CommitRevision(StorageOwner, index.Revision);
        }, token, bytes, commit: commit =>
        {
            if (index.Epoch != Interlocked.Read(ref clearEpoch) || current is not null && !current())
                throw new OperationCanceledException(token);
            commit();
        }).ConfigureAwait(false);
    }

    internal async Task<INativeMediaReadLease?> OpenAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        lock (stateGate) if (closed) return null;
        long epoch = Interlocked.Read(ref clearEpoch);
        FileStream? file = null;
        string? leasedFile = null;
        try
        {
            string key = Key(scope, song, signature);
            var index = await Read(token, key: key).ConfigureAwait(false);
            Entry entry;
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    !index.Entries.TryGetValue(key, out entry!) || !Accepted(entry, signature, Now))
                    return null;
                AcquireReadLease(entry.File);
                leasedFile = entry.File;
            }
            finally { metadataGate.Release(); }

            // Perform file I/O and SHA-256 verification outside metadataGate so concurrent ops never stall.
            string path = FilePath(entry.File);
            if (!File.Exists(path))
            {
                ReleaseReadLease(leasedFile); leasedFile = null;
                _ = EraseAsync(scope, song, signature, CancellationToken.None);
                return null;
            }
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            if (file.Length != entry.Size || file.Length is <= 0 or > Capacity)
            {
                await file.DisposeAsync(); file = null;
                ReleaseReadLease(leasedFile); leasedFile = null;
                _ = EraseAsync(scope, song, signature, CancellationToken.None);
                return null;
            }
            string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            if (digest != entry.Sha256)
            {
                await file.DisposeAsync(); file = null;
                ReleaseReadLease(leasedFile); leasedFile = null;
                _ = EraseAsync(scope, song, signature, CancellationToken.None);
                return null;
            }
            if (epoch != Interlocked.Read(ref clearEpoch))
            {
                await file.DisposeAsync(); file = null;
                ReleaseReadLease(leasedFile); leasedFile = null;
                return null;
            }
            file.Position = 0;
            var lease = new ReadLease(this, leasedFile, file, token);
            file = null;
            leasedFile = null;
            return lease;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e) || OptionalFileFailure(e))
        { token.ThrowIfCancellationRequested(); return null; }
        finally
        {
            if (file is not null) await file.DisposeAsync();
            if (leasedFile is not null) ReleaseReadLease(leasedFile);
        }
    }

    internal async Task<AudioFileInfoRecord?> ReadInfoAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        lock (stateGate) if (closed) return null;
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            string key = Key(scope, song, signature);
            var index = await Read(token, key: key).ConfigureAwait(false);
            if (epoch != Interlocked.Read(ref clearEpoch) ||
                !index.Entries.TryGetValue(key, out var entry) || !Accepted(entry, signature, Now) ||
                !File.Exists(FilePath(entry.File)))
                return null;
            return entry.Info;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e) || OptionalFileFailure(e))
        { token.ThrowIfCancellationRequested(); return null; }
    }

    internal async Task<AudioFileInfoRecord[]> ReadScriptHistoryAsync(string song, CancellationToken token)
    {
        RequireText(song, 2048);
        lock (stateGate) if (closed) return [];
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            var index = await Read(token, song: song).ConfigureAwait(false);
            if (epoch != Interlocked.Read(ref clearEpoch)) return [];
            return index.Entries.Values.Where(entry => entry.Info.Kind == "script" &&
                entry.Info.SongId == song && Accepted(entry, entry.Signature, Now) &&
                File.Exists(FilePath(entry.File))).OrderByDescending(entry => entry.ConfirmedAt)
                .Select(entry => entry.Info).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e) || OptionalFileFailure(e))
        { token.ThrowIfCancellationRequested(); return []; }
    }

    internal async Task TouchAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        lock (stateGate) if (closed) return;
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string key = Key(scope, song, signature);
            var index = await Read(token, key: key).ConfigureAwait(false);
            if (!index.Entries.TryGetValue(key, out var entry) || !Accepted(entry, signature, Now)) return;
            entry.ConfirmedAt = Now;
            await Write(index, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e)) { }
        finally { metadataGate.Release(); }
    }

    internal async Task EraseAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        lock (stateGate) if (closed) return;
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string key = Key(scope, song, signature);
            var removed = await database.WriteAsync(db =>
            {
                var rows = db.Query("SELECT file_name,file_size FROM windows_music_audio_files WHERE resource_key=$key",
                    row => (File: row.GetString(0), Size: row.GetInt64(1)), ("$key", key));
                db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key=$key", ("$key", key));
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return rows;
            }, token).ConfigureAwait(false);
            foreach (var item in removed)
                DeleteOrDefer(item.File, item.Size);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e)) { }
        finally { metadataGate.Release(); }
    }

    private async Task StoreAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile prepared)
    {
        try
        {
            await StoreCoreAsync(scope, song, signature, receipt, isCurrent, token, prepared).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
        finally
        {
            lock (stateGate)
            {
                if (activeByKey.TryGetValue(prepared.ResourceKey, out var current) && ReferenceEquals(current, prepared))
                    activeByKey.Remove(prepared.ResourceKey);
            }
            prepared.FinishWork();
        }
    }

    private async Task StoreCoreAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile prepared)
    {
        long epoch = Interlocked.Read(ref clearEpoch);
        long officialEpoch = Interlocked.Read(ref officialClearEpoch);
        ValidateReceipt(receipt, scope, song, signature, out long? expires);
        var info = receipt.Info;
        double expected = info.Duration!.Value;
        long expectedSize = info.Size!.Value;
        bool full = info.Full!.Value;
        if (expectedSize > Capacity ||
            expires is { } urlExpiry && urlExpiry <= Now)
            return;

        if (!prepared.Speculative) await transferGate.WaitAsync(token).ConfigureAwait(false);
        else if (!await transferGate.WaitAsync(0, token).ConfigureAwait(false)) return;

        string? partFileName = null;
        string? committedFileName = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (stateGate)
            {
                if (closed) return;
                activeTransfer = deadline;
                activePrepared = prepared;
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    prepared.IsOfficial && officialEpoch != Interlocked.Read(ref officialClearEpoch))
                    deadline.Cancel();
            }
            long confirmed = Now;
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            var ct = deadline.Token;
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return;

            EnsureRoot();
            long occupied = await PruneAsync(expectedSize > 0 ? expectedSize : Capacity / 4, ct).ConfigureAwait(false);
            long maximum = Capacity - occupied;
            if (maximum <= 0 || expectedSize > maximum) return;

            // Stage downloads to a .part file so crashes never leave truncated .audio files.
            string fileId = Guid.NewGuid().ToString("N");
            partFileName = fileId + ".part";
            string partPath = FilePath(partFileName);
            RegisterTemporary(partFileName);

            var headers = info.Kind == "script" && receipt.Headers.Count > 0
                ? new Dictionary<string, string>(receipt.Headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
            await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous))
            {
                long size = await NativeHttpMediaTransfer.CopyCompleteAsync(new Uri(receipt.Url),
                    headers, output, maximum, httpOptions, ct,
                    prepared.ReceivedBytes,
                    length => prepared.BeginFile(partPath, length)).ConfigureAwait(false);
                if (expectedSize > 0 && size != expectedSize) return;
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                prepared.CompleteBody(size);
            }

            if (!full) return;
            deadline.CancelAfter(TimeSpan.FromSeconds(120));
            if (!isCurrent()) return;

            string sha;
            long length;
            AudioCacheStructure.AudioFacts? facts;
            await using (var file = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                length = file.Length;
                facts = AudioCacheStructure.Inspect(file, ct);
                file.Position = 0;
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            }
            double duration = await validate(partPath, expected, ct).ConfigureAwait(false);
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
            prepared.AcceptReceipt(storedInfo);
            prepared.Ready.TrySetResult(partPath);

            if (epoch != Interlocked.Read(ref clearEpoch) ||
                prepared.IsOfficial && officialEpoch != Interlocked.Read(ref officialClearEpoch))
                return;

            deadline.CancelAfter(prepared.Speculative ? TimeSpan.FromSeconds(120) : Timeout.InfiniteTimeSpan);
            var playedAt = await prepared.Played.Task.WaitAsync(ct).ConfigureAwait(false);
            if (playedAt is null || playedAt <= 0 || playedAt > Now) return;
            confirmed = playedAt.Value;
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return;

            await metadataGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!isCurrent() || epoch != Interlocked.Read(ref clearEpoch) ||
                    prepared.IsOfficial && officialEpoch != Interlocked.Read(ref officialClearEpoch))
                    return;

                // Atomically commit .part -> .audio before publishing index row.
                string finalName = fileId + ".audio";
                string finalPath = FilePath(finalName);
                File.Move(partPath, finalPath, overwrite: false);
                committedFileName = finalName;
                UnregisterTemporary(partFileName);
                partFileName = null;

                string key = Key(scope, song, signature);
                var row = new AudioFileCacheRecord(key, finalName, length, sha, signature, confirmed, duration, storedInfo);
                int bytes = row.EstimateBytes();
                if (bytes > MaximumIndexBytes) throw MusicPlaybackCacheDataException.Limit();

                var (previous, evicted) = await database.WriteAsync(db =>
                {
                    var expectedRev = db.Revision(StorageOwner);
                    (string File, long Size)? prev = db.TryQuerySingle(
                        "SELECT file_name,file_size FROM windows_music_audio_files WHERE resource_key=$key",
                        r => (r.GetString(0), r.GetInt64(1)), out var existingRow, ("$key", key))
                        ? existingRow : null;
                    var evictedFiles = new List<(string File, long Size)>();
                    if (prev is null)
                    {
                        long count = db.Scalar<long>("SELECT count(*) FROM windows_music_audio_files");
                        long overflow = count - (MaximumEntries - 1);
                        if (overflow > 0)
                        {
                            var victims = db.Query("""
                                SELECT resource_key,file_name,file_size FROM windows_music_audio_files
                                ORDER BY confirmed_at_ms ASC, resource_key ASC
                                """, r => (Key: r.GetString(0), File: r.GetString(1), Size: r.GetInt64(2)));
                            foreach (var v in victims)
                            {
                                if (overflow <= 0) break;
                                if (IsProtectedFile(v.File)) continue;
                                db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key=$k", ("$k", v.Key));
                                evictedFiles.Add((v.File, v.Size));
                                overflow--;
                            }
                        }
                    }
                    AudioFileCacheStore.Save(db, key, row);
                    db.CommitRevision(StorageOwner, expectedRev);
                    return (prev, evictedFiles);
                }, ct, bytes, commit: commit =>
                {
                    if (epoch != Interlocked.Read(ref clearEpoch) ||
                        prepared.IsOfficial && officialEpoch != Interlocked.Read(ref officialClearEpoch) ||
                        !isCurrent())
                        throw new OperationCanceledException(ct);
                    commit();
                }).ConfigureAwait(false);

                committedFileName = null;
                prepared.Published(finalPath, finalName);
                if (previous is { } old) DeleteOrDefer(old.File, old.Size);
                foreach (var item in evicted) DeleteOrDefer(item.File, item.Size);
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { prepared.SourceFailed(new OperationCanceledException(token)); }
        catch (StorageException) { }
        catch (Exception e) when (OptionalFileFailure(e) || e is PlaybackException or
            InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { prepared.SourceFailed(e); }
        finally
        {
            if (partFileName is not null && !prepared.KeepsFile)
            {
                UnregisterTemporary(partFileName);
                TryDeleteFile(partFileName);
            }
            if (committedFileName is not null)
                DeleteOrDefer(committedFileName, 0);
            lock (stateGate)
            {
                if (ReferenceEquals(activeTransfer, deadline))
                {
                    activeTransfer = null;
                    activePrepared = null;
                }
            }
            transferGate.Release();
        }
    }

    /// <summary>Explicitly clears all cached audio. Active read leases defer unlink until playback stops.</summary>
    internal async Task ClearAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref clearEpoch);
        bool keepReader;
        List<CancellationTokenSource> toCancel = [];
        lock (stateGate)
        {
            if (closed) return;
            keepReader = activePrepared?.Opened == true;
            if (!keepReader && activeTransfer is not null) toCancel.Add(activeTransfer);
            else activePrepared?.Played.TrySetResult(null);
            foreach (var queued in activeByKey.Values)
            {
                if (queued.Opened) queued.Played.TrySetResult(null);
                else toCancel.Add(queued.Cancellation);
            }
        }
        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (!keepReader)
            await transferGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var rows = await database.WriteAsync(db =>
                {
                    var items = db.Query("SELECT file_name,file_size FROM windows_music_audio_files",
                        r => (File: r.GetString(0), Size: r.GetInt64(1)));
                    db.Execute("DELETE FROM windows_music_audio_files");
                    db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                    return items;
                }, token).ConfigureAwait(false);

                foreach (var item in rows)
                    DeleteOrDefer(item.File, item.Size);

                if (Directory.Exists(root))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                    {
                        token.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(entry);
                        if (IsProtectedFile(name)) continue;
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else if (File.Exists(entry)) File.Delete(entry);
                    }
                }
            }
            finally { metadataGate.Release(); }
        }
        finally
        {
            if (!keepReader) transferGate.Release();
        }
    }

    /// <summary>Clears official account audio files while keeping independent script history.</summary>
    internal async Task ClearOfficialAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref officialClearEpoch);
        List<CancellationTokenSource> toCancel = [];
        lock (stateGate)
        {
            if (closed) return;
            if (activePrepared is { IsOfficial: true })
            {
                if (activePrepared.Opened) activePrepared.Played.TrySetResult(null);
                else if (activeTransfer is not null) toCancel.Add(activeTransfer);
            }
            foreach (var queued in activeByKey.Values)
            {
                if (!queued.IsOfficial) continue;
                if (queued.Opened) queued.Played.TrySetResult(null);
                else toCancel.Add(queued.Cancellation);
            }
        }
        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var officialRows = await database.WriteAsync(db =>
            {
                var items = AudioFileCacheStore.ReadOfficialFiles(db);
                AudioFileCacheStore.DeleteOfficial(db);
                db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                return items;
            }, token).ConfigureAwait(false);

            foreach (var item in officialRows)
                DeleteOrDefer(item.FileName, item.FileSize);
        }
        finally { metadataGate.Release(); }
    }

    internal async Task<MusicAudioFileCacheStats> GetStatsAsync(CancellationToken token = default)
    {
        bool isClosed;
        int leaseCount, pendingCount, tempCount;
        long pendingBytes;
        lock (stateGate)
        {
            isClosed = closed;
            leaseCount = activeReadLeases.Values.Sum();
            pendingCount = pendingDeletes.Count;
            pendingBytes = pendingDeletes.Values.Sum();
            tempCount = activeTemporaries.Count;
        }
        if (isClosed)
            return new MusicAudioFileCacheStats(0, 0, 0, 0, 0, 0, Capacity, true, root);

        var (count, indexedBytes) = await database.ReadSnapshotAsync(AudioFileCacheStore.ReadStats, token).ConfigureAwait(false);
        return new MusicAudioFileCacheStats(
            EntryCount: count,
            TotalDiskBytes: indexedBytes + pendingBytes,
            PendingDeleteCount: pendingCount,
            PendingDeleteBytes: pendingBytes,
            ActiveReadLeases: leaseCount,
            ActiveTemporaries: tempCount,
            CapacityBytes: Capacity,
            Closed: false,
            RootPath: root);
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
        ValidateInfoRecord(SafeInfo(info), Key(scope, song, signature), signature, size, duration, decoded: false);
        expires = receipt.ExpiresAt;
    }

    private async Task<long> PruneAsync(long requiredBytes, CancellationToken token)
    {
        await metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long epoch = Interlocked.Read(ref clearEpoch);
            long now = Now;
            long deferredBytes = DeferredDeleteBytes();
            var (keep, deletedRows, retainedBytes) = await database.WriteAsync(db =>
            {
                var summaries = AudioFileCacheStore.ReadSummaries(db);
                var alive = new List<(string Key, string FileName, long FileSize)>(summaries.Count);
                var toDelete = new List<(string Key, string FileName, long FileSize)>();
                long used = deferredBytes;
                foreach (var item in summaries)
                {
                    bool valid = item.ConfirmedAtMs > 0 && item.ConfirmedAtMs <= now &&
                        (item.ConfirmedAtMs > long.MaxValue - Ttl ? long.MaxValue : item.ConfirmedAtMs + Ttl) > now &&
                        item.FileSize is > 0 and <= Capacity;
                    if (!valid)
                    {
                        toDelete.Add((item.Key, item.FileName, item.FileSize));
                        continue;
                    }
                    alive.Add((item.Key, item.FileName, item.FileSize));
                    used += item.FileSize;
                }
                var retained = new HashSet<string>(StringComparer.Ordinal);
                long retainedSize = 0;
                foreach (var item in alive)
                {
                    if (used > Capacity - requiredBytes && !IsProtectedFile(item.FileName))
                    {
                        used -= item.FileSize;
                        toDelete.Add(item);
                    }
                    else
                    {
                        retained.Add(item.FileName);
                        retainedSize += item.FileSize;
                    }
                }
                if (toDelete.Count > 0)
                {
                    foreach (var item in toDelete)
                        db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key=$key", ("$key", item.Key));
                    db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                }
                return (retained, toDelete, retainedSize);
            }, token, commit: commit =>
            {
                if (epoch != Interlocked.Read(ref clearEpoch)) throw new OperationCanceledException(token);
                commit();
            }).ConfigureAwait(false);

            foreach (var item in deletedRows)
                DeleteOrDefer(item.FileName, item.FileSize);

            long activeTempBytes = 0;
            if (Directory.Exists(root))
            {
                bool sweepAll = !startupSwept;
                startupSwept = true;
                foreach (var path in Directory.EnumerateFiles(root))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(path);
                    if (keep.Contains(name)) continue;
                    if (IsProtectedFile(name))
                    {
                        bool isPendingDelete;
                        lock (stateGate) isPendingDelete = pendingDeletes.ContainsKey(name);
                        if (!isPendingDelete && ValidPartName(name))
                        {
                            try { activeTempBytes += new FileInfo(path).Length; }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        continue;
                    }
                    if (sweepAll || ValidCommittedName(name) || ValidPartName(name))
                        TryDeletePath(path);
                }
            }
            return retainedBytes + DeferredDeleteBytes() + activeTempBytes;
        }
        finally { metadataGate.Release(); }
    }

    private void TryDeleteFile(string fileName) => TryDeletePath(Path.Combine(root, fileName));

    private static void TryDeletePath(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool OptionalFileFailure(Exception e) => e is IOException or UnauthorizedAccessException;

    public ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            closed = true;
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        CancellationTokenSource? transfer;
        lock (stateGate) transfer = activeTransfer;
        try { transfer?.Cancel(); } catch (ObjectDisposedException) { }

        await transferGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await metadataGate.WaitAsync().ConfigureAwait(false);
            metadataGate.Release();
        }
        finally
        {
            transferGate.Release();
        }
        lock (stateGate)
        {
            activeByKey.Clear();
            activeReadLeases.Clear();
            pendingDeletes.Clear();
            activeTemporaries.Clear();
        }
        transferGate.Dispose();
        metadataGate.Dispose();
        lifetime.Dispose();
    }

    private sealed class ReadLease(MusicAudioFileCache owner, string fileName, FileStream file,
        CancellationToken revoked) : INativeMediaReadLease
    {
        private int released;
        public Stream Content => file;
        public long Length => file.Length;
        public CancellationToken Revoked => revoked;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            try { await file.DisposeAsync().ConfigureAwait(false); }
            finally { owner.ReleaseReadLease(fileName); }
        }
    }
}
