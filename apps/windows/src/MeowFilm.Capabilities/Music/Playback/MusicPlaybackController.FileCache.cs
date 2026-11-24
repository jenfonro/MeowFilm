using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private void ConfirmPlaybackFileCache()
    {
        if (current is { } played && OwnsCurrent && playback.HasMedia)
            TrackPlaybackCache(presentationCache.TouchPlayedAsync(played.Id, CurrentArtwork, lifetime.Token));
        if (audioFileCache is null || musicRecovery is not { Media.Target: not null } state || !CurrentRecovery(state)) return;
        if (state.Media!.Bytes is { } bytes)
        {
            bytes.Played.TrySetResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }
        if (state.Cached && MusicPlaybackSourceService.FileIdentity(state.Media) is { } identity)
            TrackPlaybackCache(audioFileCache.TouchAsync(identity.Scope, state.Song.Id, identity.Signature, state.Token));
    }

    private ResolvedMusic PreparePlaybackBytes(MusicRecovery state, ResolvedMusic media)
    {
        if (audioFileCache is null || media.Bytes is not null || media.Target is not { } target) return media;
        var identity = MusicPlaybackSourceService.FileIdentity(media);
        if (identity is not null && preparedAudio is { } prepared && preparedEntry == state.Song.EntryId &&
            prepared.Matches(identity.Scope, state.Song.Id, identity.Signature))
        {
            preparedAudio = null; preparedEntry = null;
            TrackPlaybackCache(prepared.Work);
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
        var source = audioFileCache.Prepare(identity.Scope, state.Song.Id, identity.Signature, receipt,
            () => CurrentRecovery(state) && (target is not { Official: true, AccountScope: not null } official ||
                application.IsOfficialPlaybackCurrent(official.Platform, official.AccountScope, official.Generation)),
            state.Token, speculative: false);
        TrackPlaybackCache(source.Work);
        return media with { File = identity, Bytes = source };
    }

    private async Task CompletePlaybackBytes(MusicRecovery state, MusicAudioFileCache.PreparedFile source)
    {
        await source.Work;
        if (!CurrentRecovery(state) || !ReferenceEquals(state.Media?.Bytes, source)) return;
        var info = await audioFileCache!.ReadInfoAsync(source.Scope, state.Song.Id, source.Signature, state.Token);
        if (info is null || !CurrentRecovery(state) || !ReferenceEquals(state.Media?.Bytes, source)) return;
        var accepted = state.Media!;
        state.Media = accepted with { Receipt = info };
        if (state.Selection.Source is not null) state.Selection = state.Selection with { Source = state.Media };
        if (state.Session is { } session)
            foreach (var attempt in session.Attempts.Values.Where(attempt => ReferenceEquals(attempt.Media, accepted)))
                attempt.Media = state.Media;
        if (SourceChoices.Count > 0) UpdateSourceChoices();
        PlaybackSourceBindings();
    }

    private void ObservePlaybackCache(MusicRecovery state)
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
                    if (CurrentRecovery(state) && ReferenceEquals(state.Media?.Bytes, source))
                        SyncPlaybackClockCache();
                });
            };
            source.ProgressChanged += state.CacheProgress;
            TrackPlaybackCache(CompletePlaybackBytes(state, source));
        }
        SyncPlaybackClockCache();
    }

    private static AudioFileReceipt FileReceipt(SongRow song, ResolvedMusic media) =>
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

    private async Task<ResolvedMusic?> CompletedPlaybackFileAsync(MusicRecovery state,
        MusicSourceSession session, ResolvedMusic media, MusicSelection selection, CancellationToken token)
    {
        if (audioFileCache is null || media.Target is not { } target) return null;
        bool manualSource = selection.Manual;
        await session.LoadExclusionsAsync(token);
        if (!session.CanReuse(media) || target.Official &&
            (!manualSource && target.QualityPolicySignature != session.Signature || target.AccountScope is not { } scope ||
                !application.IsOfficialPlaybackCurrent(target.Platform, scope, target.Generation))) return null;
        var identity = MusicPlaybackSourceService.FileIdentity(media);
        if (identity is null && !target.Official)
        {
            string signature = MusicAudioFileCache.ScriptSignature(target.Installation!, target.Platform, target.SourceKey!, target.Quality);
            identity = new("script:" + signature, signature);
        }
        if (identity is null) return null;
        var info = await audioFileCache.ReadInfoAsync(identity.Scope, state.Song.Id, identity.Signature, token);
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

    private async Task<MeowFilm.Media.INativeMediaReadLease?> OpenPlaybackFileAsync(MusicRecovery state,
        ResolvedMusic media, CancellationToken token)
    {
        if (media.Bytes is { } source) return await source.OpenAsync(token);
        if (audioFileCache is null || MusicPlaybackSourceService.FileIdentity(media) is not { } identity) return null;
        return await audioFileCache.OpenAsync(identity.Scope, state.Song.Id, identity.Signature, token);
    }

    private async Task InvalidatePlaybackFileCache(MusicRecovery state)
    {
        if (audioFileCache is null || state.Media is not { } media || MusicPlaybackSourceService.FileIdentity(media) is not { } identity) return;
        await audioFileCache.EraseAsync(identity.Scope, state.Song.Id, identity.Signature, state.Token);
    }

    private async Task<ResolvedMusic?> RefreshMissingPlaybackFileAsync(MusicRecovery state,
        MusicSourceSession session, ResolvedMusic media, CancellationToken token)
    {
        // Missing/corrupt bytes revoke this representation only. Do not turn an
        // optional file miss into a provider blacklist.
        if (audioFileCache is not null && MusicPlaybackSourceService.FileIdentity(media) is { } identity)
            await audioFileCache.EraseAsync(identity.Scope, state.Song.Id, identity.Signature, token);
        try { return await session.RefreshAsync(media.Target!, token); }
        catch (MusicOperationException error) when (MusicPlaybackSourceService.UnavailableSourceCandidate(error.Code))
        {
            session.Reject(media, error.Code);
            return null;
        }
    }

    private void TrackPlaybackCache(Task task)
    {
        var observed = Observe();
        AddEventWrite(observed);
        _ = Remove();
        async Task Observe()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "file-cache", "optional-task-failed",
                    new { type = error.GetType().Name, error = error.Message });
            }
        }
        async Task Remove() { await observed; RemoveEventWrite(observed); }
    }
}
