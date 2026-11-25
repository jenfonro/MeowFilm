using System.IO;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
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
/// - File open and O(1) size verification run outside the metadata gate.
/// </summary>
internal sealed partial class MusicAudioFileCache : IAsyncDisposable
{
    internal const string StorageOwner = "music.playback-files.v1";
    internal const long Capacity = 256L * 1024 * 1024, Ttl = 30L * 86400000;
    private const int MaximumIndexBytes = 1024 * 1024, MaximumEntries = 2048, MaxConcurrentBackgroundTransfers = 2;

    internal static string ScriptSignature(string installation, string platform, string recording, string quality) =>
        MusicRules.CanonicalTupleHash(installation, platform, recording, quality);

    private readonly string root;
    private readonly IStorageDatabase database;
    private readonly SemaphoreSlim metadataGate = new(1, 1);
    private readonly SemaphoreSlim backgroundTransferGate = new(MaxConcurrentBackgroundTransfers, MaxConcurrentBackgroundTransfers);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateGate = new();
    private readonly Dictionary<string, int> activeReadLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pendingDeletes = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeTemporaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreparedFile> activeByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<PreparedFile, CancellationTokenSource> activeTransfers = new(ReferenceEqualityComparer.Instance);
    private long clearEpoch, officialClearEpoch;
    private bool closed, startupSwept;
    private int activeOperations;
    private TaskCompletionSource operationsDrained = CompletedOperations();
    private Task? disposal;

    private static TaskCompletionSource CompletedOperations()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private bool TryEnterOperation()
    {
        lock (stateGate)
        {
            if (closed) return false;
            if (activeOperations++ == 0)
                operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    private void LeaveOperation()
    {
        lock (stateGate) if (--activeOperations == 0) operationsDrained.TrySetResult();
    }
    private sealed class Entry(string file, long size, string signature, long confirmedAt,
        double duration, AudioFileInfoRecord info)
    {
        internal string File { get; } = file;
        internal long Size { get; } = size;
        internal string Signature { get; } = signature;
        internal long ConfirmedAt { get; } = confirmedAt;
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
            AudioFileReceipt receipt, CancellationToken token, CancellationToken ownerToken, bool speculative)
        {
            this.owner = owner;
            Scope = scope; Song = song; Signature = signature;
            ResourceKey = Key(scope, song, signature);
            Receipt = receipt; Speculative = speculative;
            IsOfficial = receipt.Info.Kind == "official";
            Cancellation = ownerToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(token, ownerToken)
                : CancellationTokenSource.CreateLinkedTokenSource(token);
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
            string openPath;
            string? lateLease = null;
            lock (bytesGate)
            {
                readers++;
                openPath = path ?? source.Value.Path;
                if (published && publishedName is not null && leasedFinalName is null)
                    lateLease = publishedName;
            }
            if (lateLease is not null)
            {
                owner.AcquireReadLease(lateLease);
                bool keepLease = false;
                lock (bytesGate)
                {
                    if (readers > 0 && leasedFinalName is null)
                    {
                        leasedFinalName = lateLease;
                        keepLease = true;
                    }
                }
                if (!keepLease) owner.ReleaseReadLease(lateLease);
            }
            FileStream? stream = null;
            bool handedOff = false;
            try
            {
                stream = new FileStream(openPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.RandomAccess);
                token.ThrowIfCancellationRequested();
                var lease = new GrowingReadLease(this, stream, source.Value.Length, token);
                stream = null;
                handedOff = true;
                Interlocked.Exchange(ref opened, 1);
                return lease;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (OptionalFileFailure(e)) { return null; }
            finally
            {
                if (stream is not null) await stream.DisposeAsync();
                if (!handedOff)
                {
                    lock (bytesGate) readers--;
                    CleanupRetiredFile();
                }
            }
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
        PreparedFile prepared;
        lock (stateGate)
        {
            if (closed)
            {
                prepared = new PreparedFile(this, scope, song, signature, receipt, token, CancellationToken.None, speculative);
                prepared.Cancellation.Cancel();
                prepared.FinishWork();
                return prepared;
            }
            if (activeByKey.TryGetValue(key, out var existing))
            {
                if (speculative && !existing.Cancellation.IsCancellationRequested)
                {
                    // Avoid duplicate speculative work for a key already in flight.
                    prepared = new PreparedFile(this, scope, song, signature, receipt, token, CancellationToken.None, speculative);
                    prepared.Cancellation.Cancel();
                    prepared.FinishWork();
                    return prepared;
                }
                if (!speculative && existing.Speculative)
                    previousToCancel = existing;
            }
            prepared = new PreparedFile(this, scope, song, signature, receipt, token, lifetime.Token, speculative);
            activeByKey[key] = prepared;
            if (activeOperations++ == 0)
                operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { previousToCancel?.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        prepared.Work = StoreAsync(scope, song, signature, receipt, isCurrent, prepared.Cancellation.Token, prepared);
        return prepared;
    }

    internal MusicAudioFileCache(string root, IStorageDatabase database)
    {
        this.root = Path.GetFullPath(root);
        this.database = database;
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
        if (entry.Signature != signature || entry.ConfirmedAt <= 0) return false;
        long ttl = entry.ConfirmedAt > long.MaxValue - Ttl ? long.MaxValue : entry.ConfirmedAt + Ttl;
        return ttl > now;
    }

    private async Task<Dictionary<string, Entry>> Read(CancellationToken token, string? key = null, string? song = null)
    {
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                IReadOnlyList<AudioFileCacheRecord> rows;
                if (key is not null) rows = AudioFileCacheStore.ReadOne(db, key) is { } one ? [one] : [];
                else if (song is not null) rows = AudioFileCacheStore.ReadByScriptSong(db, song);
                else rows = [];
                foreach (var record in rows)
                {
                    string id = record.ResourceKey;
                    if (!ValidCommittedName(record.FileName) ||
                        record.FileSize is <= 0 or > Capacity || record.ConfirmedAt <= 0 ||
                        record.Duration is not { } duration || !double.IsFinite(duration) || duration <= 0)
                        throw MusicPlaybackCacheDataException.Corrupt();
                    entries.Add(id, new Entry(record.FileName, record.FileSize,
                        record.Signature, record.ConfirmedAt, duration, record.Info));
                }
                return entries;
            }, token).ConfigureAwait(false);
        }
        catch (StorageException) { token.ThrowIfCancellationRequested(); return new(StringComparer.Ordinal); }
    }

    private async Task SweepStartupOrphansOnceAsync(CancellationToken token)
    {
        if (startupSwept) return;
        startupSwept = true;
        if (!Directory.Exists(root)) return;
        try
        {
            var keep = await database.ReadSnapshotAsync(
                db => db.Query("SELECT file_name FROM windows_music_audio_files", r => r.GetString(0))
                    .ToHashSet(StringComparer.Ordinal),
                token).ConfigureAwait(false);
            foreach (var path in Directory.EnumerateFiles(root))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if (keep.Contains(name) || IsProtectedFile(name)) continue;
                TryDeletePath(path);
            }
        }
        catch (OperationCanceledException) { startupSwept = false; throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e) || OptionalFileFailure(e))
        { startupSwept = false; }
    }

    internal async Task<INativeMediaReadLease?> OpenAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        if (!TryEnterOperation()) return null;
        long epoch = Interlocked.Read(ref clearEpoch);
        long officialEpoch = Interlocked.Read(ref officialClearEpoch);
        FileStream? file = null;
        string? leasedFile = null;
        try
        {
            string key = Key(scope, song, signature);
            Entry entry;
            await metadataGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                await SweepStartupOrphansOnceAsync(token).ConfigureAwait(false);
                var entries = await Read(token, key: key).ConfigureAwait(false);
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    !entries.TryGetValue(key, out entry!) || !Accepted(entry, signature, Now) ||
                    entry.Info.Kind == "official" && officialEpoch != Interlocked.Read(ref officialClearEpoch))
                    return null;
                AcquireReadLease(entry.File);
                leasedFile = entry.File;
            }
            finally { metadataGate.Release(); }

            // Perform file open and O(1) size verification outside metadataGate so concurrent ops never stall.
            string path = FilePath(entry.File);
            if (!File.Exists(path))
            {
                ReleaseReadLease(leasedFile); leasedFile = null;
                ScheduleSelfHealErase(scope, song, signature);
                return null;
            }
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.RandomAccess);
            if (file.Length != entry.Size || file.Length is <= 0 or > Capacity)
            {
                await file.DisposeAsync(); file = null;
                ReleaseReadLease(leasedFile); leasedFile = null;
                ScheduleSelfHealErase(scope, song, signature);
                return null;
            }
            if (epoch != Interlocked.Read(ref clearEpoch) ||
                entry.Info.Kind == "official" && officialEpoch != Interlocked.Read(ref officialClearEpoch))
            {
                await file.DisposeAsync(); file = null;
                ReleaseReadLease(leasedFile); leasedFile = null;
                return null;
            }
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
            LeaveOperation();
        }
    }

    internal async Task<AudioFileInfoRecord?> ReadInfoAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        if (!TryEnterOperation()) return null;
        long epoch = Interlocked.Read(ref clearEpoch);
        long officialEpoch = Interlocked.Read(ref officialClearEpoch);
        try
        {
            string key = Key(scope, song, signature);
            var entries = await Read(token, key: key).ConfigureAwait(false);
            if (epoch != Interlocked.Read(ref clearEpoch) ||
                !entries.TryGetValue(key, out var entry) || !Accepted(entry, signature, Now) ||
                entry.Info.Kind == "official" && officialEpoch != Interlocked.Read(ref officialClearEpoch) ||
                !File.Exists(FilePath(entry.File)))
                return null;
            return entry.Info;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e) || OptionalFileFailure(e))
        { token.ThrowIfCancellationRequested(); return null; }
        finally { LeaveOperation(); }
    }

    internal async Task<AudioFileInfoRecord[]> ReadScriptHistoryAsync(string song, CancellationToken token)
    {
        RequireNonEmpty(song);
        if (!TryEnterOperation()) return [];
        long epoch = Interlocked.Read(ref clearEpoch);
        try
        {
            var entries = await Read(token, song: song).ConfigureAwait(false);
            if (epoch != Interlocked.Read(ref clearEpoch)) return [];
            return entries.Values.Where(entry => entry.Info.Kind == "script" &&
                entry.Info.SongId == song && Accepted(entry, entry.Signature, Now) &&
                File.Exists(FilePath(entry.File))).OrderByDescending(entry => entry.ConfirmedAt)
                .Select(entry => entry.Info).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e) || OptionalFileFailure(e))
        { token.ThrowIfCancellationRequested(); return []; }
        finally { LeaveOperation(); }
    }

    internal async Task TouchAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        if (!TryEnterOperation()) return;
        try
        {
            try
            {
                string key = Key(scope, song, signature);
                long now = Now;
                long expired = now - Ttl;
                await database.WriteAsync(db =>
                {
                    int changed = db.Execute("""
                        UPDATE windows_music_audio_files
                        SET confirmed_at_ms=$now
                        WHERE resource_key=$key AND signature=$sig AND confirmed_at_ms>$expired AND confirmed_at_ms<$now
                        """, ("$now", now), ("$key", key), ("$sig", signature), ("$expired", expired));
                    if (changed > 0) db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
                    return changed;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e))
            {
                DebugLog.Warn(DebugCategories.Storage, "music.audioCache", "touch.failed",
                    new { scope, song }, error: e);
            }
        }
        finally { LeaveOperation(); }
    }

    private void ScheduleSelfHealErase(string scope, string song, string signature)
    {
        if (!TryEnterOperation()) return;
        _ = RunSelfHealEraseAsync(scope, song, signature);
    }

    private async Task RunSelfHealEraseAsync(string scope, string song, string signature)
    {
        try { await EraseCoreAsync(scope, song, signature, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.audioCache", "selfHealErase.failed",
                new { scope, song }, error: error);
        }
        finally { LeaveOperation(); }
    }

    internal async Task EraseAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song, signature);
        if (!TryEnterOperation()) return;
        try { await EraseCoreAsync(scope, song, signature, token).ConfigureAwait(false); }
        finally { LeaveOperation(); }
    }

    private async Task EraseCoreAsync(string scope, string song, string signature, CancellationToken token)
    {
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
        catch (Exception e) when (MusicPlaybackCacheDataException.IsCacheFailure(e))
        {
            DebugLog.Warn(DebugCategories.Storage, "music.audioCache", "erase.failed",
                new { scope, song }, error: e);
        }
        finally { metadataGate.Release(); }
    }

    private async Task StoreAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile prepared)
    {
        try
        {
            await StoreCoreAsync(scope, song, signature, receipt, isCurrent, token, prepared).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
        {
            prepared.SourceFailed(error);
            DebugLog.Warn(DebugCategories.Storage, "music.audioCache", "store.failed",
                new { scope, song }, error: error);
        }
        finally
        {
            lock (stateGate)
            {
                if (activeByKey.TryGetValue(prepared.ResourceKey, out var current) && ReferenceEquals(current, prepared))
                    activeByKey.Remove(prepared.ResourceKey);
            }
            prepared.FinishWork();
            LeaveOperation();
        }
    }

    private async Task StoreCoreAsync(string scope, string song, string signature, AudioFileReceipt receipt,
        Func<bool> isCurrent, CancellationToken token, PreparedFile prepared)
    {
        long epoch = Interlocked.Read(ref clearEpoch);
        long officialEpoch = Interlocked.Read(ref officialClearEpoch);
        ValidateIdentity(scope, song, signature);
        if (!Uri.TryCreate(receipt.Url, UriKind.Absolute, out var mediaUri) || mediaUri.Scheme is not ("http" or "https"))
            throw MusicPlaybackCacheDataException.Corrupt();
        var info = receipt.Info;
        if (info.Duration is not { } expected || !double.IsFinite(expected) || expected < 0 ||
            info.Size is not { } expectedSize || expectedSize < 0 || info.Full is not { } full)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (expectedSize > Capacity || receipt.ExpiresAt is { } urlExpiry && urlExpiry <= Now)
            return;

        string? fileId = null;
        string? partFileName = null;
        string? partPath = null;
        string? committedFileName = null;
        long committedFileSize = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (stateGate)
            {
                if (closed) return;
                activeTransfers[prepared] = deadline;
                if (epoch != Interlocked.Read(ref clearEpoch) ||
                    prepared.IsOfficial && officialEpoch != Interlocked.Read(ref officialClearEpoch))
                    deadline.Cancel();
            }
            long confirmed = Now;
            var ct = deadline.Token;
            ct.ThrowIfCancellationRequested();
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            if (!isCurrent()) return;

            bool acquiredBackgroundSlot = false;
            try
            {
                // Foreground streams never contend on the background preload semaphore;
                // background preloads release the slot immediately after HTTP transfer completes.
                if (prepared.Speculative)
                {
                    await backgroundTransferGate.WaitAsync(ct).ConfigureAwait(false);
                    acquiredBackgroundSlot = true;
                    ct.ThrowIfCancellationRequested();
                    if (!isCurrent()) return;
                }

                EnsureRoot();
                long occupied = await PruneAsync(expectedSize > 0 ? expectedSize : Capacity / 4, ct).ConfigureAwait(false);
                long maximum = Capacity - occupied;
                if (maximum <= 0 || expectedSize > maximum) return;
                // Stage downloads to a .part file so crashes never leave truncated .audio files.
                fileId = Guid.NewGuid().ToString("N");
                partFileName = fileId + ".part";
                string localPartPath = FilePath(partFileName);
                partPath = localPartPath;
                RegisterTemporary(partFileName);
                var headers = info.Kind == "script" && receipt.Headers.Count > 0
                    ? new Dictionary<string, string>(receipt.Headers, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>();
                await using (var output = new FileStream(localPartPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read | FileShare.Delete, 0, FileOptions.Asynchronous))
                {
                    long size = await NativeHttpMediaTransfer.CopyCompleteAsync(mediaUri,
                        headers, output, maximum, null, ct,
                        prepared.ReceivedBytes,
                        length => prepared.BeginFile(localPartPath, length)).ConfigureAwait(false);
                    if (expectedSize > 0 && size != expectedSize) return;
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    prepared.CompleteBody(size);
                }
            }
            finally
            {
                if (acquiredBackgroundSlot)
                    backgroundTransferGate.Release();
            }

            if (!full || partPath is null || fileId is null || partFileName is null) return;
            deadline.CancelAfter(TimeSpan.FromSeconds(120));
            if (!isCurrent()) return;

            long length;
            AudioCacheStructure.AudioFacts? facts;
            using (var file = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                length = file.Length;
                facts = AudioCacheStructure.Inspect(file, ct);
            }
            double duration = await NativeAudioCacheProbe.ValidateAsync(partPath, expected, ct).ConfigureAwait(false);
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
            if (playedAt is null || playedAt <= 0) return;
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
                committedFileSize = length;
                UnregisterTemporary(partFileName);
                partFileName = null;

                string key = Key(scope, song, signature);
                var row = new AudioFileCacheRecord(key, finalName, length, signature, confirmed, duration, storedInfo);
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
                committedFileSize = 0;
                prepared.Published(finalPath, finalName);
                if (previous is { } old) DeleteOrDefer(old.File, old.Size);
                foreach (var item in evicted) DeleteOrDefer(item.File, item.Size);
            }
            finally { metadataGate.Release(); }
        }
        catch (OperationCanceledException) { prepared.SourceFailed(new OperationCanceledException(token)); }
        catch (StorageException error)
        {
            DebugLog.Warn(DebugCategories.Storage, "audio.cache", "store_failed",
                new Dictionary<string, object?> { ["error"] = error.Error.ToString(), ["song"] = song });
            prepared.SourceFailed(error);
        }
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
                DeleteOrDefer(committedFileName, committedFileSize);
            lock (stateGate)
            {
                if (activeTransfers.TryGetValue(prepared, out var currentDeadline) && ReferenceEquals(currentDeadline, deadline))
                    activeTransfers.Remove(prepared);
            }
        }
    }

    /// <summary>Explicitly clears all cached audio. Active read leases defer unlink until playback stops.</summary>
    internal async Task ClearAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryEnterOperation()) return;
        try
        {
            Interlocked.Increment(ref clearEpoch);
            List<CancellationTokenSource> toCancel = [];
            lock (stateGate)
            {
                foreach (var (prepared, deadline) in activeTransfers)
                {
                    if (prepared.Opened) prepared.Played.TrySetResult(null);
                    else toCancel.Add(deadline);
                }
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
                        if (Directory.Exists(entry)) TryDeleteDirectory(entry);
                        else TryDeletePath(entry);
                    }
                }
            }
            finally { metadataGate.Release(); }
        }
        finally { LeaveOperation(); }
    }

    /// <summary>Clears official account audio files while keeping independent script history.</summary>
    internal async Task ClearOfficialAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TryEnterOperation()) return;
        try
        {
            Interlocked.Increment(ref officialClearEpoch);
            List<CancellationTokenSource> toCancel = [];
            lock (stateGate)
            {
                foreach (var (prepared, deadline) in activeTransfers)
                {
                    if (!prepared.IsOfficial) continue;
                    if (prepared.Opened) prepared.Played.TrySetResult(null);
                    else toCancel.Add(deadline);
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
        finally { LeaveOperation(); }
    }

    internal async Task<MusicAudioFileCacheStats> GetStatsAsync(CancellationToken token = default)
    {
        if (!TryEnterOperation())
            return new MusicAudioFileCacheStats(0, 0, 0, 0, 0, 0, Capacity, true, root);
        try
        {
            int leaseCount, pendingCount, tempCount;
            long pendingBytes;
            lock (stateGate)
            {
                leaseCount = activeReadLeases.Values.Sum();
                pendingCount = pendingDeletes.Count;
                pendingBytes = pendingDeletes.Values.Sum();
                tempCount = activeTemporaries.Count;
            }
            int count;
            try
            {
                var stats = await database.ReadSnapshotAsync(AudioFileCacheStore.ReadStats, token).ConfigureAwait(false);
                count = stats.Count;
            }
            catch (StorageException error) when (error.Error == StorageError.Closed)
            {
                return new MusicAudioFileCacheStats(0, 0, 0, 0, 0, 0, Capacity, true, root);
            }
            long totalDiskBytes = 0;
            if (Directory.Exists(root))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root))
                    {
                        try { totalDiskBytes += new FileInfo(path).Length; }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return new MusicAudioFileCacheStats(
                EntryCount: count,
                TotalDiskBytes: totalDiskBytes,
                PendingDeleteCount: pendingCount,
                PendingDeleteBytes: pendingBytes,
                ActiveReadLeases: leaseCount,
                ActiveTemporaries: tempCount,
                CapacityBytes: Capacity,
                Closed: false,
                RootPath: root);
        }
        finally { LeaveOperation(); }
    }

    private static AudioFileInfoRecord SafeInfo(AudioFileInfoRecord info) =>
        info.Kind == "script"
            ? info with { Generation = null, QualityPolicySignature = null, Delivery = null }
            : info with { InstallationId = null, ProviderName = null };

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
                    bool valid = item.ConfirmedAtMs > 0 &&
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

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool OptionalFileFailure(Exception e) => e is IOException or UnauthorizedAccessException;

    public ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            closed = true;
            var waitOps = operationsDrained.Task;
            return new(disposal ??= DisposeCoreAsync(waitOps));
        }
    }

    private async Task DisposeCoreAsync(Task waitOps)
    {
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        CancellationTokenSource[] activeCts;
        CancellationTokenSource[] queuedCts;
        lock (stateGate)
        {
            activeCts = activeTransfers.Values.ToArray();
            queuedCts = activeByKey.Values.Select(p => p.Cancellation).ToArray();
        }
        foreach (var cts in activeCts)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var cts in queuedCts)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }

        await waitOps.ConfigureAwait(false);
        lock (stateGate)
        {
            activeTransfers.Clear();
            activeByKey.Clear();
            activeReadLeases.Clear();
            pendingDeletes.Clear();
            activeTemporaries.Clear();
        }
        backgroundTransferGate.Dispose();
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
