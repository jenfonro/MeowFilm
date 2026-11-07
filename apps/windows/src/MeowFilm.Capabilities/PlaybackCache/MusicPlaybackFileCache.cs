using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    public bool CanClearPlaybackCache => CanEdit && audioFileCache is not null;
    public Task ClearPlaybackCacheAsync() => RunAsync(async () =>
    {
        if (audioFileCache is null || !dialogs.Confirm("清空播放缓存？", "播放缓存")) return;
        CancelMusicPreload();
        await audioFileCache.ClearAsync(lifetime.Token);
        status = "播放缓存已清空"; Changed(nameof(Status));
    });

    private void ConfirmPlaybackFileCache()
    {
        if (audioFileCache is null || musicRecovery is not { Media.Target: { } target } state || !CurrentRecovery(state)) return;
        var identity = FileIdentity(state.Media!);
        JsonElement? receipt = state.Media!.Receipt;
        if (!target.Official && state.Media.Uri is not null)
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
                full = true, licenseExpiresAt = (long?)null,
                url = state.Media.Uri.AbsoluteUri, headers = state.Media.Headers, expiresAt = state.Media.ExpiresAt,
                qualityEvidence = (string?)null, codec = (string?)null, sampleRate = (long?)null,
                bitDepth = (long?)null, channels = (long?)null, bitrate = (long?)null
            });
        }
        if (identity is null) return;
        if (preparedAudio is { } prepared && preparedEntry == state.Song.EntryId &&
            prepared.Matches(identity.Scope, state.Song.Id, identity.Signature) &&
            !prepared.Work.IsCompleted)
        {
            prepared.Played.TrySetResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }
        if (state.Cached)
            TrackPlaybackCache(Touch(), state);
        else if (receipt is { } value)
            TrackPlaybackCache(Store(value), state);
        async Task Store(JsonElement receipt)
        {
            var accepted = state.Media;
            if (target.Official) receipt = FileReceipt(state.Song, receipt);
            await audioFileCache.StorePlayedAsync(identity.Scope, state.Song.Id,
                identity.Signature, receipt, () => CurrentRecovery(state) && ReferenceEquals(state.Media, accepted), state.Token);
            var info = await audioFileCache.ReadInfoAsync(identity.Scope, state.Song.Id,
                identity.Signature, state.Token);
            if (info is not null && CurrentRecovery(state) && ReferenceEquals(state.Media, accepted) && state.Media is { } media)
            {
                state.Media = media with { Receipt = JsonSerializer.SerializeToElement(info), File = identity };
                if (state.Session is { } session)
                    foreach (var attempt in session.Attempts.Values.Where(attempt => ReferenceEquals(attempt.Media, accepted)))
                        attempt.Media = state.Media;
                if (SourceChoices.Count > 0) UpdateSourceChoices();
                Changed(nameof(CurrentQualityLabel));
            }
        }
        Task Touch() => audioFileCache.TouchAsync(identity.Scope, state.Song.Id,
            identity.Signature, state.Token);
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
        await session.LoadExclusionsAsync(token);
        if (!session.CanReuse(media) || target.Official &&
            (target.QualityPolicySignature != session.Signature || target.AccountScope is not { } scope ||
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
            File = identity, Receipt = JsonSerializer.SerializeToElement(info) };
        await session.LoadExclusionsAsync(token);
        return session.CanReuse(cached) && MusicQualityPolicy.Enabled(EffectiveLevel(cached), session.Policy) &&
            selection.Allows(cached) ? cached : null;
    }

    private async Task<MeowFilm.Media.INativeMediaReadLease?> OpenPlaybackFileAsync(MusicRecovery state,
        ResolvedMusic media, CancellationToken token)
    {
        if (audioFileCache is null || FileIdentity(media) is not { } identity) return null;
        var lease = await audioFileCache.OpenAsync(identity.Scope, state.Song.Id, identity.Signature, token);
        if (lease is null) return null;
        try
        {
            if (media.Target is { Official: true } target &&
                (media.Receipt is not { } receipt || !receipt.TryGetProperty("offlineAllowed", out var offline) ||
                    offline.ValueKind != JsonValueKind.True))
                await QualityCall("music.official.authorize", OfficialInput(state.Song, target, false, false), token);
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
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
        // optional file miss into a provider blacklist or bypass an incompatible index.
        if (audioFileCache is not null && FileIdentity(media) is { } identity)
            await audioFileCache.EraseAsync(identity.Scope, state.Song.Id, token);
        try { return await session.RefreshAsync(media.Target!, token); }
        catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
        {
            session.Reject(media, error.Code);
            return null;
        }
    }
    private void TrackPlaybackCache(Task task, MusicRecovery state)
    {
        long stamp = statusEpoch;
        var observed = Observe();
        eventWrites.Add(observed);
        _ = Remove();
        async Task Observe()
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { PublishPlaybackCacheFailure(error, state, stamp); }
        }
        async Task Remove() { await observed; eventWrites.Remove(observed); }
    }
    private void PublishPlaybackCacheFailure(Exception error, MusicRecovery state, long stamp)
    {
        if (stopping || disposed || stamp != statusEpoch || !ReferenceEquals(musicRecovery, state)) return;
        status = ErrorText(error);
        Changed(nameof(Status));
    }
}
