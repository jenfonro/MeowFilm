using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private long officialAccountEpoch;
    private object OfficialInput(SongRow song, MusicOnlineTarget target, bool initial, bool refresh, bool cacheOnly = false) =>
        new {
            platform = target.Platform, song = target.RecordingSong ?? song.Raw, originSong = song.Raw, sourceKey = target.SourceKey,
            quality = initial ? target.PolicyRequest : target.Quality,
            qualityPolicySignature = target.QualityPolicySignature,
            scope = target.AccountScope, generation = target.Generation, refresh, cacheOnly,
            manualSource = target.ManualSource
        };

    // cacheOnly: complete file only. urlCacheOnly: a still-valid native URL receipt only, no media lookup.
    // refresh: skip that URL receipt (already found unusable) and request the platform directly.
    private async Task<ResolvedMusic> ResolveOfficialTarget(SongRow song, MusicOnlineTarget target, CancellationToken token, bool initial,
        bool useCache = true, bool cacheOnly = false, bool urlCacheOnly = false, bool refresh = false)
    {
        if (initial && useCache && audioFileCache is not null)
        {
            // Local recording/account identity, not a copied display snapshot.
            // Complete files outlive signed URLs and membership dates.
            var authority = await QualityCall("music.official.cacheContext", OfficialInput(song, target, true, false), token);
            string scope = MusicDto.Text(authority, "scope"), signature = MusicDto.Text(authority, "qualityCacheSignature");
            if (!cacheOnly && preparedAudio is { } pending && (preparedEntry != song.EntryId || !pending.Matches(scope, song.Id, signature)))
                CancelPreparedAudio();
            var info = preparedAudio is { } prepared && preparedEntry == song.EntryId &&
                prepared.Matches(scope, song.Id, signature) && prepared.Ready.Task.IsCompletedSuccessfully &&
                prepared.Ready.Task.Result is not null
                ? System.Text.Json.Nodes.JsonNode.Parse(prepared.VerifiedReceipt.GetRawText())!.AsObject()
                : await audioFileCache.ReadInfoAsync(scope, song.Id, signature, token);
            if (info is not null)
            {
                if (info["full"]?.GetValue<bool>() != true)
                {
                    if (cacheOnly) throw new MusicOperationException("CACHE_MISS");
                    CancelPreparedAudio();
                    return await ResolveOfficialTarget(song, target, token, initial: true, useCache: false);
                }
                var cachedTarget = target with { AccountScope = scope, Generation = MusicDto.Integer(authority, "generation"),
                    SourceKey = MusicDto.Text(authority, "sourceKey"), QualityCacheSignature = signature,
                    Quality = info["quality"]!.GetValue<string>() };
                var policy = await QualityCall("music.quality.settings.get", null, token);
                if (MusicDto.Text(policy, "signature") != target.QualityPolicySignature)
                    throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                return new(null, new Dictionary<string, string>(), cachedTarget, Receipt: JsonSerializer.SerializeToElement(info));
            }
        }
        if (cacheOnly) throw new MusicOperationException("CACHE_MISS");
        var response = await QualityCall("music.official.resolve",
            OfficialInput(song, target, initial, !initial || refresh, urlCacheOnly), token);
        token.ThrowIfCancellationRequested();
        MusicDto.Require(MusicDto.Text(response, "kind") == "official" &&
            MusicDto.Text(response, "platform") == target.Platform && MusicDto.Text(response, "songId") == song.Id &&
            (target.SourceKey is null || MusicDto.Text(response, "sourceKey") == target.SourceKey) &&
            (initial || MusicDto.Integer(response, "generation") == target.Generation &&
                MusicDto.Text(response, "scope") == target.AccountScope) &&
            MusicDto.Text(response, "qualityPolicySignature") == target.QualityPolicySignature &&
            response.GetProperty("playbackVerified").ValueKind == JsonValueKind.False);
        string request = MusicDto.Text(response, "quality");
        MusicDto.Require(initial || request == target.Quality);
        var uri = new Uri(MusicDto.Text(response, "url"), UriKind.Absolute);
        MusicDto.Require(uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0);
        long? expiry = response.GetProperty("expiresAt").ValueKind == JsonValueKind.Number
            ? response.GetProperty("expiresAt").GetInt64() : null;
        MusicDto.Require(expiry is null || expiry > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var currentPolicy = await QualityCall("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        if (MusicDto.Text(currentPolicy, "signature") != target.QualityPolicySignature)
            throw new MusicOperationException("QUALITY_POLICY_CHANGED");
        return new(uri, new Dictionary<string, string>(), target with {
            Generation = MusicDto.Integer(response, "generation"), SourceKey = MusicDto.Text(response, "sourceKey"),
            AccountScope = MusicDto.Text(response, "scope"), Quality = request,
            QualityCacheSignature = MusicDto.Text(response, "qualityCacheSignature")
        }, expiry, response);
    }

    private void ConfirmOfficialPlayback()
    {
        if (musicRecovery is not { Media.Target.Official: true } state || !CurrentRecovery(state)) return;
        TrackMusicRecovery(Confirm());
        async Task Confirm()
        {
            try { await QualityCall("music.official.confirm", OfficialInput(state.Song, state.Media!.Target!, false, false), state.Token); }
            catch { /* Cache history is optional, never turn a successful native playing event into a playback failure. */ }
        }
    }
    private void OfficialAccountChanged(string platform)
    {
        // Account notifications may arrive off-dispatcher. Fence the selection snapshot
        // even when a script is playing, so its next explicit search uses new authority.
        Interlocked.Increment(ref selectionSourcesRevision);
        // Automatic preload may currently be planning any official platform,
        // not only the active recording. Retire the whole speculative owner at
        // invalidation time so no stale plan can start a transfer afterward.
        CancelMusicPreload();
        var state = musicRecovery;
        if (state?.Pending?.Media.Target is { Official: true } pending && pending.Platform == platform)
            CancelPendingMedia(state, "AUTH_REQUIRED");
        if (state?.Media is null || state.Media.Target is { Official: true } active && active.Platform == platform)
            Interlocked.Increment(ref officialAccountEpoch);
        bool revokeActive = state?.Media?.Target is { Official: true } target && target.Platform == platform;
        // Fence at notification time, before dispatching UI cleanup.
        if (revokeActive)
            try { state!.Access.Cancel(); } catch (ObjectDisposedException) { }
        if (!Presentation.Dispatcher.HasShutdownStarted)
            _ = Presentation.Dispatcher.InvokeAsync(() =>
            {
                if (stopping) return;
                Changed(nameof(CanLoadMoreSources));
                if (!revokeActive || state is null || !ReferenceEquals(musicRecovery, state)) return;
                TrackMusicRecovery(StopRevoked());
                async Task StopRevoked()
                {
                    var epoch = state.Epoch;
                    await RetireMusicRecovery();
                    if (epoch == playbackEpoch && playback.OwnerId == Id && playback.Identity == state.Song.EntryId)
                        await playback.StopAsync();
                }
            });
    }
}
