using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // Accepted player/UI snapshots only. Native candidates own reusable matching state.
    private readonly Dictionary<string, (string Original, JsonElement Expanded)> officialSongSnapshots = [];
    private JsonElement KnownOfficialSong(SongRow song) =>
        officialSongSnapshots.TryGetValue(song.Id, out var known) && known.Original == song.Raw.GetRawText()
            ? known.Expanded : song.Raw;
    private void AcceptOfficialSong(SongRow song, MusicOnlineTarget? target)
    {
        if (target?.RecordingSong is not { } expanded) return;
        officialSongSnapshots.Remove(song.Id);
        officialSongSnapshots[song.Id] = (song.Raw.GetRawText(), expanded.Clone());
        while (officialSongSnapshots.Count > 24) officialSongSnapshots.Remove(officialSongSnapshots.Keys.First());
    }
    // The stable queue song is never replaced by a cross-platform search result.
    // All refresh, chorus and feedback consumers use this accepted recording snapshot.
    private JsonElement PlaybackSong(SongRow song) =>
        musicRecovery is { Media.Target.RecordingSong: { } recording } state && state.Song.EntryId == song.EntryId &&
        state.Song.Id == song.Id ? recording : KnownOfficialSong(song);

    private JsonElement FeedbackSong(SongRow song)
    {
        var snapshot = PlaybackSong(song);
        if (musicRecovery?.Media?.Target is not { Official: true, SourceKey: not null } target) return snapshot;
        var raw = System.Text.Json.Nodes.JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        if (raw["sources"] is System.Text.Json.Nodes.JsonArray sources)
            raw["sources"] = LJ.Arr(sources.OfType<System.Text.Json.Nodes.JsonObject>().Where(row =>
                LJ.S(row["source"]) != target.Platform || LJ.S(row["sourceKey"]) == target.SourceKey));
        return JsonSerializer.SerializeToElement(raw);
    }

    private static bool UnavailableOfficialCandidate(string code) => code is
        "AUTH_REQUIRED" or "VIP_REQUIRED" or "FORBIDDEN" or "PLAYBACK_RESTRICTED" or "TRIAL_ONLY" or
        "QUALITY_UNAVAILABLE" or "RECORDING_QUALITY_UNAVAILABLE" or "RECORDING_UNAVAILABLE" or
        "RECORDING_MISMATCH" or "MEDIA_EXPIRED" or "NETWORK_ERROR" or "UPSTREAM_ERROR" or
        "TIMEOUT" or "RATE_LIMITED" or "CACHE_MISS";

    private async Task<ResolvedMusic> ResolveAutomaticOfficial(SongRow song, string quality, string policy,
        CancellationToken token, IReadOnlySet<string>? rejectedSources = null,
        IReadOnlySet<string>? rejectedUrls = null, bool requireNetwork = false)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(35));
        var cancellation = request.Token;
        var settings = savedQualityPolicy!.DeepClone().AsObject();
        // Sources failed in this attempt only; never a persistent wrong-recording exclusion.
        var unavailable = new HashSet<string>(rejectedSources ?? new HashSet<string>(), StringComparer.Ordinal);
        string? knownFailure = null;
        int Rank(ResolvedMusic media)
        {
            string level = media.Target!.Quality;
            if (media.Receipt is { } receipt && receipt.TryGetProperty("qualityVerified", out var verified) &&
                verified.ValueKind == JsonValueKind.True)
                level = MusicDto.Text(receipt, "actualQuality");
            return MusicQualityPolicy.Priority(level, settings);
        }
        int bestRank = MusicQualityPolicy.RequestTypes.Concat(new[] { "jyeffect", "dolby", "sky", "vivid" })
            .Min(level => MusicQualityPolicy.Priority(level, settings));
        async Task<MusicOnlineTarget[]> Plan(bool discover)
        {
            object parameters = discover
                ? new { song = song.Raw, quality, qualityPolicySignature = policy, discover, rejectedSourceKeys = unavailable.ToArray() }
                : new { song = song.Raw, quality, qualityPolicySignature = policy, discover };
            var reply = await QualityCall("music.official.candidates", parameters, cancellation);
            MusicDto.Require(MusicDto.Text(reply, "qualityPolicySignature") == policy);
            if (reply.TryGetProperty("authorizationRequired", out var required) && required.ValueKind == JsonValueKind.True)
                throw new MusicOperationException("AUTH_REQUIRED");
            var decorated = SongRow.Read(reply.GetProperty("song"), song.EntryId);
            MusicDto.Require(decorated.Id == song.Id);
            var candidates = reply.GetProperty("candidates").EnumerateArray().ToArray();
            MusicDto.Require(candidates.Length <= 24);
            return candidates.Where(row => !unavailable.Contains(MusicDto.Text(row, "sourceKey")))
                .Select(row => new MusicOnlineTarget(null, MusicDto.Text(row, "platform"),
                MusicDto.Integer(row, "generation"), MusicDto.Text(row, "sourceKey"),
                PolicyRequest: quality, Quality: quality, QualityPolicySignature: policy,
                AccountScope: MusicDto.Text(row, "scope"), RecordingSong: decorated.Raw, Automatic: true)).ToArray();
        }
        async Task<ResolvedMusic> Accept(ResolvedMusic media)
        {
            var currentPolicy = await QualityCall("music.quality.settings.get", null, token);
            token.ThrowIfCancellationRequested();
            if (MusicDto.Text(currentPolicy, "signature") != policy)
                throw new MusicOperationException("QUALITY_POLICY_CHANGED");
            // Other successful resolutions are choices only, never a late stream switch or a played-history write.
            return media;
        }
        try
        {
            // Accepted local mappings only: history, the queue song and earlier matches. No catalog request.
            var known = await Plan(false);
            // Probe all authorized complete-file candidates before any catalog discovery or URL resolution.
            // History is preference only; cacheContext still checks account generation and policy.
            foreach (var target in requireNetwork ? Array.Empty<MusicOnlineTarget>() : known)
            {
                try { return await ResolveOfficialTarget(song, target, cancellation, initial: true, cacheOnly: true); }
                catch (MusicOperationException e) when (UnavailableOfficialCandidate(e.Code)) { }
            }
            // A still-valid URL under the current account and quality authority starts before any media lookup.
            foreach (var target in known)
            {
                try
                {
                    var cached = await ResolveOfficialTarget(song, target, cancellation, initial: true,
                        useCache: false, urlCacheOnly: true);
                    cancellation.ThrowIfCancellationRequested();
                    if (cached.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true) continue;
                    if (Rank(cached) != int.MaxValue) return await Accept(cached);
                }
                catch (MusicOperationException e) when (e.Code == "CACHE_MISS") { }
                catch (MusicOperationException e) when (UnavailableOfficialCandidate(e.Code))
                { knownFailure ??= e.Code; unavailable.Add(target.SourceKey!); }
            }
            // An acceptable existing mapping completes automatic selection; unknown platforms are not
            // searched merely to compare their quality.
            foreach (var target in known.Where(target => !unavailable.Contains(target.SourceKey!)).ToArray())
            {
                try
                {
                    var media = await ResolveOfficialTarget(song, target, cancellation, initial: true,
                        useCache: false, refresh: true);
                    cancellation.ThrowIfCancellationRequested();
                    if (media.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true)
                        knownFailure ??= "MEDIA_EXPIRED";
                    else if (Rank(media) != int.MaxValue) return await Accept(media);
                    else knownFailure ??= "RECORDING_QUALITY_UNAVAILABLE";
                }
                catch (MusicOperationException e) when (UnavailableOfficialCandidate(e.Code)) { knownFailure ??= e.Code; }
                unavailable.Add(target.SourceKey!);
            }
            // Only now: a missing target platform, or every existing source is unavailable.
            var discovered = await Plan(true);
            if (discovered.Length == 0) throw new MusicOperationException(knownFailure ?? "RECORDING_UNAVAILABLE");
            // At most three native platform workers; each tries its compatible recordings in preference order.
            // Explicit script/recording choices never enter this coordinator.
            var results = await Task.WhenAll(discovered.GroupBy(target => target.Platform).Select(async group =>
            {
                ResolvedMusic? best = null;
                string? failure = null;
                foreach (var target in group)
                {
                    try
                    {
                        var media = await ResolveOfficialTarget(song, target, cancellation, initial: true, useCache: false);
                        cancellation.ThrowIfCancellationRequested();
                        if (media.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true) continue;
                        if (Rank(media) != int.MaxValue && (best is null || Rank(media) < Rank(best))) best = media;
                        if (best is not null && (quality != "auto" || Rank(best) == bestRank)) break;
                    }
                    catch (MusicOperationException e) when (UnavailableOfficialCandidate(e.Code))
                    {
                        // Keep the actual platform denial instead of replacing it
                        // with an unrelated generic failure after all candidates.
                        failure ??= e.Code;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && cancellation.IsCancellationRequested)
                    { return (Media: best, Failure: failure ?? "TIMEOUT"); }
                    catch { request.Cancel(); throw; }
                }
                return (Media: best, Failure: failure);
            }));
            token.ThrowIfCancellationRequested();
            // Rank returned quality when observed; otherwise rank the requested format without claiming it is verified.
            // Stable ordering preserves the confirmed source preference within equal quality.
            var selected = results.Select(result => result.Media).OfType<ResolvedMusic>().Where(media => Rank(media) != int.MaxValue)
                .OrderBy(Rank).FirstOrDefault() ?? throw new MusicOperationException(
                    cancellation.IsCancellationRequested ? "TIMEOUT" :
                    results.Select(result => result.Failure).FirstOrDefault(code => code is not null) ??
                    knownFailure ?? "PLAYBACK_UNAVAILABLE");
            return await Accept(selected);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && request.IsCancellationRequested)
        { throw new MusicOperationException("TIMEOUT"); }
    }
}
