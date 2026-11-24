using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone Playback service owning audio file byte caching, completed-file reuse,
/// and speculative next-track preload preparation.
/// </summary>
internal sealed class MusicPlaybackCacheService(MusicRuntimeContext ctx, MusicSettingsController settings)
{
    private sealed class MusicPreloadAttempt(MusicRecovery origin, CancellationTokenSource cancellation)
    {
        internal readonly MusicRecovery Origin = origin;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal bool QueueObserved;
        internal long QueueVersion;
        internal int QueueMode;
        internal string? QueueCurrent;
        internal string? TargetEntry;
    }

    private MusicRecovery? preloadedFrom;
    private MusicPreloadAttempt? musicPreload;
    private MusicAudioFileCache.PreparedFile? preparedAudio;
    private string? preparedEntry;

    internal string? PreparedAudioReceiptSignature =>
        preparedAudio is { } prepared ? prepared.Receipt.Info.QualityPolicySignature : null;

    internal AudioFileInfoRecord? TryGetVerifiedPreparedReceipt(SongRow song, string scope, string signature) =>
        preparedAudio is { } prepared && preparedEntry == song.EntryId &&
        prepared.Matches(scope, song.Id, signature) && prepared.Ready.Task.IsCompletedSuccessfully &&
        prepared.Ready.Task.Result is not null
            ? prepared.VerifiedReceipt
            : null;

    internal bool HasRevokedPreparedOfficial(string platform) =>
        preparedAudio is { } prepared &&
        prepared.Receipt.Info is { Kind: "official", Platform: var p, Generation: { } gen } && p == platform &&
        !ctx.Application.IsOfficialPlaybackCurrent(platform, prepared.Scope, gen);

    internal void CancelPreparedAudio()
    {
        var previous = preparedAudio; preparedAudio = null; preparedEntry = null;
        previous?.Dispose();
    }

    internal void CancelPreparedAudioIfMismatch(SongRow song, MusicOnlineTarget? acceptedTarget)
    {
        if (preparedAudio is { } prepared && (preparedEntry != song.EntryId ||
            acceptedTarget is not { Official: true, AccountScope: not null, QualityCacheSignature: not null } accepted ||
            !prepared.Matches(accepted.AccountScope, song.Id, accepted.QualityCacheSignature)))
            CancelPreparedAudio();
    }

    internal void CancelPreload()
    {
        var attempt = musicPreload;
        if (attempt is not null)
        {
            try { attempt.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        CancelPreparedAudio();
    }

    internal void ObserveQueueMutation(IMusicQueuePosition snapshot)
    {
        var attempt = musicPreload;
        if (attempt is null && preparedAudio is null) return;
        string? current = snapshot.CurrentId;
        if (preparedEntry is not null && current == preparedEntry) return;
        if (attempt is null || !attempt.QueueObserved ||
            attempt.QueueVersion != snapshot.Version ||
            attempt.QueueMode != snapshot.Mode ||
            attempt.QueueCurrent != current)
            CancelPreload();
    }

    internal void ObserveQueueSnapshot(IMusicQueuePosition snapshot)
    {
        var attempt = musicPreload;
        if (attempt is null || !attempt.QueueObserved) return;
        string? current = snapshot.CurrentId;
        if (preparedEntry is not null && current == preparedEntry) return;
        if (attempt.QueueVersion != snapshot.Version ||
            attempt.QueueMode != snapshot.Mode ||
            attempt.QueueCurrent != current)
            CancelPreload();
    }

    internal void ObserveMusicPreload(
        MusicRecovery? state,
        Func<MusicRecovery, bool> isCurrentRecovery,
        MusicPlaybackSourceService sourceService,
        bool isPlaying,
        double positionSeconds,
        double durationSeconds)
    {
        if (state is not { Media.Target: not null } || !isCurrentRecovery(state) ||
            state.Pending is not null || state.Recovering ||
            !isPlaying || durationSeconds <= 0 || durationSeconds - positionSeconds is <= 0 or > 20 ||
            ReferenceEquals(preloadedFrom, state)) return;
        preloadedFrom = state;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
        var attempt = new MusicPreloadAttempt(state, cancellation);
        musicPreload = attempt;
        TrackCacheWork(PreloadAsync(attempt, isCurrentRecovery, sourceService));
    }

    private async Task PreloadAsync(
        MusicPreloadAttempt attempt,
        Func<MusicRecovery, bool> isCurrentRecovery,
        MusicPlaybackSourceService sourceService)
    {
        MusicAudioFileCache.PreparedFile? ownedPrepared = null;
        try
        {
            var state = attempt.Origin;
            var next = await ctx.Application.PeekNextQueueAsync(attempt.Cancellation.Token);
            if (!ReferenceEquals(musicPreload, attempt) || !isCurrentRecovery(state)) return;
            attempt.QueueVersion = next.Version;
            attempt.QueueMode = next.Mode;
            attempt.QueueCurrent = next.CurrentId;
            if (attempt.QueueCurrent != state.Song.EntryId) return;
            if (next.Next is not { } entry) return;
            attempt.TargetEntry = entry.EntryId;
            attempt.QueueObserved = true;
            var song = SongRow.FromRecord(entry.Song, attempt.TargetEntry);
            if (sourceService.HasRememberedRecording(song.Id)) return;
            // Built-in, already-mapped recordings only: preparing a file never searches or runs scripts.
            await using var session = await sourceService.BeginSessionAsync(song, attempt.Cancellation.Token, officialOnly: true);
            if (session.Signature != settings.SavedQualitySignature) return;
            var media = await session.AutomaticAsync(attempt.Cancellation.Token);
            if (media.Uri is null || media.Receipt is not { } receipt || media.Target is not { } selected) return;
            string platform = selected.Platform;
            if (!ReferenceEquals(musicPreload, attempt) || !isCurrentRecovery(state) || ctx.AudioFileCache is null) return;
            CancelPreparedAudio();
            if (receipt.Scope is not { Length: > 0 } scope ||
                receipt.QualityCacheSignature is not { Length: > 0 } signature ||
                receipt.SongId is not { Length: > 0 } songId ||
                receipt.Generation is not { } generation) return;
            if (await ctx.AudioFileCache.ReadInfoAsync(scope, songId, signature, attempt.Cancellation.Token) is not null) return;
            if (!ReferenceEquals(musicPreload, attempt) || !isCurrentRecovery(state)) return;
            preparedEntry = attempt.TargetEntry;
            ownedPrepared = ctx.AudioFileCache.Prepare(scope, songId, signature, FileReceipt(song, media),
                () => !ctx.Stopping && settings.SavedQualitySignature == session.Signature &&
                    ctx.Application.IsOfficialPlaybackCurrent(platform, scope, generation), ctx.Lifetime.Token);
            preparedAudio = ownedPrepared;
            // Complete validated bytes remain provisional until this exact queue entry really starts.
            await ownedPrepared.Work;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicPlayback, "preload", "speculative-preload-failed",
                new { error = error.Message });
        }
        finally
        {
            if (ReferenceEquals(musicPreload, attempt)) musicPreload = null;
            attempt.Cancellation.Dispose();
            if (ownedPrepared is not null && ReferenceEquals(preparedAudio, ownedPrepared))
            {
                preparedAudio = null;
                preparedEntry = null;
                ownedPrepared.Dispose();
            }
        }
    }

    internal void ConfirmFileCache(SongRow? played, bool ownsCurrent, string currentArtwork,
        MusicRecovery? state, Func<MusicRecovery, bool> isCurrentRecovery)
    {
        if (played is not null && ownsCurrent && ctx.PlaybackDevice.HasMedia)
            TrackCacheWork(ctx.PresentationCache.TouchPlayedAsync(played.Id, currentArtwork, ctx.Lifetime.Token));
        if (ctx.AudioFileCache is null || state is not { Media.Target: not null } || !isCurrentRecovery(state)) return;
        if (state.Media!.Bytes is { } bytes)
        {
            bytes.Played.TrySetResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }
        if (state.Cached && MusicPlaybackSourceService.FileIdentity(state.Media) is { } identity)
            TrackCacheWork(ctx.AudioFileCache.TouchAsync(identity.Scope, state.Song.Id, identity.Signature, state.Token));
    }

    internal ResolvedMusic PreparePlaybackBytes(MusicRecovery state, ResolvedMusic media,
        Func<MusicRecovery, bool> isCurrentRecovery)
    {
        if (ctx.AudioFileCache is null || media.Bytes is not null || media.Target is not { } target) return media;
        var identity = MusicPlaybackSourceService.FileIdentity(media);
        if (identity is not null && preparedAudio is { } prepared && preparedEntry == state.Song.EntryId &&
            prepared.Matches(identity.Scope, state.Song.Id, identity.Signature))
        {
            preparedAudio = null; preparedEntry = null;
            TrackCacheWork(prepared.Work);
            return media with { File = identity, Bytes = prepared };
        }
        if (media.Uri is null ||
            System.IO.Path.GetExtension(media.Uri.AbsolutePath).ToLowerInvariant() is ".m3u" or ".m3u8") return media;
        AudioFileReceipt receipt;
        if (target.Official)
        {
            if (identity is null || media.Receipt is null) throw new MusicOperationException("INVALID_MEDIA_CANDIDATE");
            receipt = FileReceipt(state.Song, media);
        }
        else
        {
            string signature = MusicAudioFileCache.ScriptSignature(target.Installation!, target.Platform, target.SourceKey!, target.Quality);
            identity = new("script:" + signature, signature);
            var info = new AudioFileInfoRecord(
                Kind: "script",
                Platform: target.Platform,
                SongId: state.Song.Id,
                SourceKey: target.SourceKey,
                Scope: identity.Scope,
                Generation: null,
                InstallationId: target.Installation,
                ProviderName: target.ProviderName ?? "",
                Quality: target.Quality,
                RequestedQuality: target.Quality,
                ActualQuality: "",
                QualityVerified: false,
                PlaybackVerified: false,
                QualityPolicySignature: null,
                QualityCacheSignature: signature,
                Duration: MusicPlaybackSourceService.ExpectedDuration(state.Song),
                Size: 0L,
                Full: true,
                Delivery: null,
                QualityEvidence: null,
                Codec: null,
                SampleRate: null,
                BitDepth: null,
                Channels: null,
                Bitrate: null);
            receipt = new AudioFileReceipt(true, media.Uri.AbsoluteUri, media.Headers, media.ExpiresAt, info);
        }
        var source = ctx.AudioFileCache.Prepare(identity.Scope, state.Song.Id, identity.Signature, receipt,
            () => isCurrentRecovery(state) && (target is not { Official: true, AccountScope: not null } official ||
                ctx.Application.IsOfficialPlaybackCurrent(official.Platform, official.AccountScope, official.Generation)),
            state.Token, speculative: false);
        TrackCacheWork(source.Work);
        return media with { File = identity, Bytes = source };
    }

    internal void ObservePlaybackCache(MusicRecovery state, Func<MusicRecovery, bool> isCurrentRecovery,
        Action syncClockCache, Action onCompletedBytes)
    {
        if (state.ObservedBytes is { } previous && state.CacheProgress is { } callback) previous.ProgressChanged -= callback;
        state.ObservedBytes = state.Media?.Bytes; state.CacheProgress = null;
        if (state.ObservedBytes is { } source)
        {
            int queued = 0;
            state.CacheProgress = () =>
            {
                if (ctx.Dispatcher.IsClosing || Interlocked.Exchange(ref queued, 1) != 0) return;
                _ = ctx.Dispatcher.EnqueueAsync(() =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    if (isCurrentRecovery(state) && ReferenceEquals(state.Media?.Bytes, source))
                        syncClockCache();
                });
            };
            source.ProgressChanged += state.CacheProgress;
            TrackCacheWork(CompletePlaybackBytesAsync(state, source, isCurrentRecovery, onCompletedBytes));
        }
        syncClockCache();
    }

    private async Task CompletePlaybackBytesAsync(MusicRecovery state, MusicAudioFileCache.PreparedFile source,
        Func<MusicRecovery, bool> isCurrentRecovery, Action onCompletedBytes)
    {
        await source.Work;
        if (!isCurrentRecovery(state) || !ReferenceEquals(state.Media?.Bytes, source)) return;
        var info = await ctx.AudioFileCache!.ReadInfoAsync(source.Scope, state.Song.Id, source.Signature, state.Token);
        if (info is null || !isCurrentRecovery(state) || !ReferenceEquals(state.Media?.Bytes, source)) return;
        var accepted = state.Media!;
        state.Media = accepted with { Receipt = info };
        if (state.Selection.Source is not null) state.Selection = state.Selection with { Source = state.Media };
        if (state.Session is { } session)
            foreach (var attempt in session.Attempts.Values.Where(attempt => ReferenceEquals(attempt.Media, accepted)))
                attempt.Media = state.Media;
        onCompletedBytes();
    }

    internal static AudioFileReceipt FileReceipt(SongRow song, ResolvedMusic media) =>
        // File validation is relative to the song, not a second tolerance around the upstream duration.
        new(
            Ok: true,
            Url: media.Uri!.AbsoluteUri,
            Headers: media.Headers,
            ExpiresAt: media.ExpiresAt,
            Info: media.Receipt! with
            {
                Duration = MusicPlaybackSourceService.ExpectedDuration(song),
                Size = media.Receipt.Size ?? 0L
            });

    internal async Task<ResolvedMusic?> CompletedPlaybackFileAsync(MusicRecovery state,
        MusicSourceSession session, ResolvedMusic media, MusicSelection selection, CancellationToken token)
    {
        if (ctx.AudioFileCache is null || media.Target is not { } target) return null;
        bool manualSource = selection.Manual;
        await session.LoadExclusionsAsync(token);
        if (!session.CanReuse(media) || target.Official &&
            (!manualSource && target.QualityPolicySignature != session.Signature || target.AccountScope is not { } scope ||
                !ctx.Application.IsOfficialPlaybackCurrent(target.Platform, scope, target.Generation))) return null;
        var identity = MusicPlaybackSourceService.FileIdentity(media);
        if (identity is null && !target.Official)
        {
            string signature = MusicAudioFileCache.ScriptSignature(target.Installation!, target.Platform, target.SourceKey!, target.Quality);
            identity = new("script:" + signature, signature);
        }
        if (identity is null) return null;
        var info = await ctx.AudioFileCache.ReadInfoAsync(identity.Scope, state.Song.Id, identity.Signature, token);
        if (info is null) return null;
        var cached = media with
        {
            Uri = null, Headers = new Dictionary<string, string>(), ExpiresAt = null,
            File = identity, Receipt = info, Bytes = null
        };
        if (manualSource)
            cached = cached with
            {
                Target = target with
                {
                    ManualSource = true,
                    QualityPolicySignature = target.Official ? session.Signature : target.QualityPolicySignature
                }
            };
        await session.LoadExclusionsAsync(token);
        return session.CanReuse(cached) && (manualSource || MusicQualityPolicy.Enabled(MusicPlaybackSourceService.EffectiveLevel(cached), session.Policy)) &&
            selection.Allows(cached) ? cached : null;
    }

    internal async Task<INativeMediaReadLease?> OpenPlaybackFileAsync(MusicRecovery state,
        ResolvedMusic media, CancellationToken token)
    {
        if (media.Bytes is { } source) return await source.OpenAsync(token);
        if (ctx.AudioFileCache is null || MusicPlaybackSourceService.FileIdentity(media) is not { } identity) return null;
        return await ctx.AudioFileCache.OpenAsync(identity.Scope, state.Song.Id, identity.Signature, token);
    }

    internal async Task InvalidateFileCacheAsync(MusicRecovery state)
    {
        if (ctx.AudioFileCache is null || state.Media is not { } media || MusicPlaybackSourceService.FileIdentity(media) is not { } identity) return;
        await ctx.AudioFileCache.EraseAsync(identity.Scope, state.Song.Id, identity.Signature, state.Token);
    }

    internal async Task<ResolvedMusic?> RefreshMissingFileAsync(MusicRecovery state,
        MusicSourceSession session, ResolvedMusic media, CancellationToken token)
    {
        // Missing/corrupt bytes revoke this representation only. Do not turn an
        // optional file miss into a provider blacklist.
        if (ctx.AudioFileCache is not null && MusicPlaybackSourceService.FileIdentity(media) is { } identity)
            await ctx.AudioFileCache.EraseAsync(identity.Scope, state.Song.Id, identity.Signature, token);
        try { return await session.RefreshAsync(media.Target!, token); }
        catch (MusicOperationException error) when (MusicPlaybackSourceService.UnavailableSourceCandidate(error.Code))
        {
            session.Reject(media, error.Code);
            return null;
        }
    }

    internal void TrackCacheWork(Task task)
    {
        var observed = Observe();
        ctx.AddEventWrite(observed);
        _ = Remove();
        async Task Observe()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (DebugLog.IsEnabled)
                    DebugLog.Warn(DebugCategories.MusicPlayback, "music.file_cache", "optional_work.failed", error: error);
            }
        }
        async Task Remove() { await observed; ctx.RemoveEventWrite(observed); }
    }
}
