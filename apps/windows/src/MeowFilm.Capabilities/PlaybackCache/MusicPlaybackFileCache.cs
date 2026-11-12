using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    public double PlaybackCacheFraction => OwnsCurrent && musicRecovery is { } state
        ? state.Cached ? 1 : state.Media?.Bytes?.BufferedFraction ?? 0 : 0;

    public bool CanClearPlaybackCache => CanEdit && audioFileCache is not null;
    public Task ClearPlaybackCacheAsync() => RunAsync(async () =>
    {
        if (audioFileCache is null || !dialogs.Confirm("清空播放缓存？", "播放缓存")) return;
        presentationCacheEpoch++;
        CancelLyrics(); // Fence pending metadata patches without clearing visible text.
        CancelMusicPreload();
        await audioFileCache.ClearAsync(lifetime.Token);
        status = "播放缓存文件已清理（当前播放不中断）"; Changed(nameof(Status));
    });

    private void ConfirmPlaybackFileCache()
    {
        if (audioFileCache is null || musicRecovery is not { Media.Target: { } target } state || !CurrentRecovery(state)) return;
        if (state.Media!.Bytes is { } bytes)
        {
            bytes.Played.TrySetResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }
        if (state.Cached && FileIdentity(state.Media) is { } identity)
            TrackPlaybackCache(audioFileCache.TouchAsync(identity.Scope, state.Song.Id, identity.Signature, state.Token));
    }

    private ResolvedMusic PreparePlaybackBytes(MusicRecovery state, ResolvedMusic media)
    {
        if (audioFileCache is null || media.Bytes is not null || media.Target is not { } target) return media;
        var identity = FileIdentity(media);
        if (identity is not null && preparedAudio is { } prepared && preparedEntry == state.Song.EntryId &&
            prepared.Matches(identity.Scope, state.Song.Id, identity.Signature))
        {
            preparedAudio = null; preparedEntry = null;
            TrackPlaybackCache(prepared.Work);
            return media with { File = identity, Bytes = prepared };
        }
        if (media.Uri is null ||
            System.IO.Path.GetExtension(media.Uri.AbsolutePath).ToLowerInvariant() is ".m3u" or ".m3u8") return media;
        JsonElement receipt;
        if (target.Official)
        {
            if (identity is null || media.Receipt is not { } official) throw new MusicOperationException("INVALID_MEDIA_CANDIDATE");
            receipt = FileReceipt(state.Song, official);
        }
        else
        {
            string signature = MusicAudioFileCache.ScriptSignature(target.Installation!, target.Platform, target.SourceKey!, target.Quality);
            identity = new("script:" + signature, signature);
            receipt = JsonSerializer.SerializeToElement(new
            {
                kind = "script", songId = state.Song.Id, installationId = target.Installation,
                providerName = target.ProviderName ?? "", platform = target.Platform, sourceKey = target.SourceKey,
                scope = identity.Scope, quality = target.Quality, requestedQuality = target.Quality,
                actualQuality = "", qualityVerified = false, playbackVerified = false,
                qualityCacheSignature = signature, duration = ExpectedDuration(state.Song), size = 0L,
                full = true,
                url = media.Uri.AbsoluteUri, headers = media.Headers, expiresAt = media.ExpiresAt,
                qualityEvidence = (string?)null, codec = (string?)null, sampleRate = (long?)null,
                bitDepth = (long?)null, channels = (long?)null, bitrate = (long?)null
            });
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
        state.Media = accepted with { Receipt = JsonSerializer.SerializeToElement(info) };
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
                if (Presentation.Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
                _ = Presentation.Dispatcher.InvokeAsync(() =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    if (CurrentRecovery(state) && ReferenceEquals(state.Media?.Bytes, source))
                        Changed(nameof(PlaybackCacheFraction));
                });
            };
            source.ProgressChanged += state.CacheProgress;
            TrackPlaybackCache(CompletePlaybackBytes(state, source));
        }
        Changed(nameof(PlaybackCacheFraction));
    }
    private static JsonElement FileReceipt(SongRow song, JsonElement receipt)
    {
        var value = JsonNode.Parse(receipt.GetRawText())!.AsObject();
        // File validation is relative to the song, not a second tolerance around the upstream duration.
        value["duration"] = ExpectedDuration(song);
        return JsonSerializer.SerializeToElement(value);
    }

    private async Task<ResolvedMusic?> CompletedPlaybackFileAsync(MusicRecovery state,
        MusicSourceSession session, ResolvedMusic media, MusicSelection selection, CancellationToken token)
    {
        if (audioFileCache is null || media.Target is not { } target) return null;
        bool manualSource = selection.Manual;
        await session.LoadExclusionsAsync(token);
        if (!session.CanReuse(media) || target.Official &&
            (!manualSource && target.QualityPolicySignature != session.Signature || target.AccountScope is not { } scope ||
                !application.IsOfficialPlaybackCurrent(target.Platform, scope, target.Generation))) return null;
        var identity = FileIdentity(media);
        if (identity is null && !target.Official)
        {
            string signature = MusicAudioFileCache.ScriptSignature(target.Installation!, target.Platform, target.SourceKey!, target.Quality);
            identity = new("script:" + signature, signature);
        }
        if (identity is null) return null;
        var info = await audioFileCache.ReadInfoAsync(identity.Scope, state.Song.Id, identity.Signature, token);
        if (info is null) return null;
        var cached = media with { Uri = null, Headers = new Dictionary<string, string>(), ExpiresAt = null,
            File = identity, Receipt = JsonSerializer.SerializeToElement(info), Bytes = null };
        if (manualSource)
            cached = cached with { Target = target with { ManualSource = true,
                QualityPolicySignature = target.Official ? session.Signature : target.QualityPolicySignature } };
        await session.LoadExclusionsAsync(token);
        return session.CanReuse(cached) && (manualSource || MusicQualityPolicy.Enabled(EffectiveLevel(cached), session.Policy)) &&
            selection.Allows(cached) ? cached : null;
    }

    private async Task<MeowFilm.Media.INativeMediaReadLease?> OpenPlaybackFileAsync(MusicRecovery state,
        ResolvedMusic media, CancellationToken token)
    {
        if (media.Bytes is { } source) return await source.OpenAsync(token);
        if (audioFileCache is null || FileIdentity(media) is not { } identity) return null;
        var lease = await audioFileCache.OpenAsync(identity.Scope, state.Song.Id, identity.Signature, token);
        return lease;
    }

    private async Task InvalidatePlaybackFileCache(MusicRecovery state)
    {
        if (audioFileCache is null || state.Media is not { } media || FileIdentity(media) is not { } identity) return;
        await audioFileCache.EraseAsync(identity.Scope, state.Song.Id, state.Token);
    }
    private async Task<ResolvedMusic?> RefreshMissingPlaybackFileAsync(MusicRecovery state,
        MusicSourceSession session, ResolvedMusic media, CancellationToken token)
    {
        // Missing/corrupt bytes revoke this representation only. Do not turn an
        // optional file miss into a provider blacklist.
        if (audioFileCache is not null && FileIdentity(media) is { } identity)
            await audioFileCache.EraseAsync(identity.Scope, state.Song.Id, token);
        try { return await session.RefreshAsync(media.Target!, token); }
        catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
        {
            session.Reject(media, error.Code);
            return null;
        }
    }
    private void TrackPlaybackCache(Task task)
    {
        var observed = Observe();
        eventWrites.Add(observed);
        _ = Remove();
        async Task Observe()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Optional playback cache: {error.GetType().Name}"); }
        }
        async Task Remove() { await observed; eventWrites.Remove(observed); }
    }
}
