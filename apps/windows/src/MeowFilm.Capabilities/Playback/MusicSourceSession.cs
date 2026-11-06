using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // Accepted player/UI snapshots only. Native candidates own reusable matching state.
    private readonly Dictionary<string, (string Original, JsonElement Expanded)> officialSongSnapshots = [];
    private long exclusionsRevision;
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
        var raw = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        if (raw["sources"] is JsonArray sources)
            raw["sources"] = LJ.Arr(sources.OfType<JsonObject>().Where(row =>
                LJ.S(row["source"]) != target.Platform || LJ.S(row["sourceKey"]) == target.SourceKey));
        return JsonSerializer.SerializeToElement(raw);
    }

    // The identity of a candidate includes its provider, not only the recording.
    private static string SourceIdentity(MusicOnlineTarget target) =>
        JsonSerializer.Serialize(new[] { target.Installation, target.Platform, target.SourceKey });
    private static bool UnavailableOfficialCandidate(string code) => code is
        "AUTH_REQUIRED" or "VIP_REQUIRED" or "FORBIDDEN" or "PLAYBACK_RESTRICTED" or "TRIAL_ONLY" or
        "QUALITY_UNAVAILABLE" or "RECORDING_QUALITY_UNAVAILABLE" or "RECORDING_UNAVAILABLE" or
        "RECORDING_MISMATCH" or "MEDIA_EXPIRED" or "NETWORK_ERROR" or "UPSTREAM_ERROR" or
        "TIMEOUT" or "RATE_LIMITED" or "CACHE_MISS" or "IDENTITY_MISMATCH" or "MEDIA_MISMATCH" or
        "QUALITY_DISABLED" or "OFFICIAL_SOURCE_DISABLED";
    private static bool UnavailableSourceCandidate(string code) => UnavailableOfficialCandidate(code) || code is
        "CAPABILITY_UNAVAILABLE" or "MEDIA_UNAVAILABLE" or "EXPIRED_MEDIA_CANDIDATE" or
        "SCRIPT_FAILURE" or "SCRIPT_EXCEPTION" or "SCRIPT_PROTOCOL_ERROR" or "INVALID_MEDIA_CANDIDATE" or
        "MEDIA_HEADERS_UNSUPPORTED" or "SOURCE_DISABLED" or "SOURCE_NOT_FOUND" or "STALE_SOURCE" or
        "UPSTREAM_HTTP" or "UPSTREAM_FORMAT" or "TARGET_CHANGED" or "TARGET_REQUIRED" or
        "SCRIPT_RUNTIME_UNAVAILABLE";

    /// <summary>The level a resolved media actually represents: verified official quality, else its request.</summary>
    private static string EffectiveLevel(ResolvedMusic media) =>
        media.Receipt is { } receipt && receipt.TryGetProperty("qualityVerified", out var verified) &&
            verified.ValueKind == JsonValueKind.True && MusicDto.Text(receipt, "actualQuality") is { Length: > 0 } actual
            ? actual : media.Target!.Quality;
    private static bool QualityVerified(ResolvedMusic media) =>
        media.Receipt is { } receipt && receipt.TryGetProperty("qualityVerified", out var verified) &&
        verified.ValueKind == JsonValueKind.True;

    private sealed record SourceScript(string Installation, string Name, JsonElement? Sites);
    private sealed record SourceExclusion(string Provider, string Platform);
    private enum SourceAttemptState { Pending, Ok, Unavailable }
    private sealed class SourceAttempt(string key, MusicOnlineTarget target)
    {
        internal readonly string Key = key;
        internal readonly MusicOnlineTarget Target = target;
        internal SourceAttemptState State;
        internal ResolvedMusic? Media;
        internal string? Code;
        internal long At;
    }

    /// <summary>
    /// One playback intent's source selection. Automatic play, a manual quality, the source
    /// list, "load more" and "wrong source" share its attempts; each tier is exhausted over
    /// every eligible source (cache first, then network) before a later tier is tried.
    /// </summary>
    private sealed class MusicSourceSession(MusicCapability owner, SongRow song, string signature,
        JsonObject policy, SourceScript[] scripts, HashSet<string> officialEnabled, bool officialOnly,
        string? preferredPlatform, string? preferredKey)
    {
        private const long ScriptMediaTtl = 10 * 60 * 1000;
        internal readonly SongRow Song = song;
        internal readonly string Signature = signature;
        internal readonly JsonObject Policy = policy;
        internal readonly MusicQualityPolicy.Tier[] Tiers = MusicQualityPolicy.Tiers(policy);
        internal readonly Dictionary<string, SourceAttempt> Attempts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MusicOnlineTarget[]> official = new(StringComparer.Ordinal);
        private readonly HashSet<string> exhausted = new(StringComparer.Ordinal);
        private readonly HashSet<string> rejected = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim selection = new(1);
        private HashSet<SourceExclusion> exclusions = [];
        private long exclusionsRevision = -1;
        private JsonElement expanded = owner.KnownOfficialSong(song);
        private string[] preference = [];
        private bool completed;
        internal readonly SourceScript[] Scripts = scripts;
        internal string? Failure;
        internal string ProviderName(string? installation) => installation is null ? "" :
            Scripts.FirstOrDefault(script => script.Installation == installation)?.Name ?? "";

        private static string AttemptKey(MusicOnlineTarget target, string level) =>
            JsonSerializer.Serialize(new[] { target.Installation ?? MusicLibraryOwner.OfficialProvider,
                target.Platform, target.SourceKey, level });
        internal bool Excluded(MusicOnlineTarget target) =>
            exclusions.Contains(new(target.Installation ?? MusicLibraryOwner.OfficialProvider, target.Platform));
        internal static bool Fresh(SourceAttempt attempt) => attempt.Media is { } media && (media.Uri is null ||
            (media.ExpiresAt ?? attempt.At + ScriptMediaTtl) - 30_000 > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        private bool Accepts(MusicQualityPolicy.Tier tier, ResolvedMusic media, bool exact) =>
            tier.Levels.Contains(EffectiveLevel(media)) || !exact && tier.Levels.Any(level =>
                MusicQualityPolicy.Priority(EffectiveLevel(media), Policy) < MusicQualityPolicy.Priority(level, Policy));

        internal async Task LoadExclusionsAsync(CancellationToken token)
        {
            long revision = owner.exclusionsRevision;
            if (revision == exclusionsRevision) return;
            var reply = await owner.Call("music.sourceExclusions.list", new { songId = Song.Id }, token);
            var next = reply.GetProperty("items").EnumerateArray().Select(item =>
                new SourceExclusion(MusicDto.Text(item, "provider"), MusicDto.Text(item, "platform"))).ToHashSet();
            bool restored = exclusions.Except(next).Any();
            exclusions = next;
            exclusionsRevision = revision;
            foreach (var attempt in Attempts.Values.Where(attempt =>
                attempt.Code == "SOURCE_EXCLUDED" && !Excluded(attempt.Target)).ToArray())
                Attempts.Remove(attempt.Key);
            if (restored) exhausted.Clear();
            foreach (var attempt in Attempts.Values.Where(attempt => Excluded(attempt.Target)))
            { attempt.State = SourceAttemptState.Unavailable; attempt.Code = "SOURCE_EXCLUDED"; attempt.Media = null; }
        }
        private ResolvedMusic? Known(MusicQualityPolicy.Tier tier, bool exact) => Attempts.Values
            .Where(attempt => attempt.State == SourceAttemptState.Ok && attempt.Media is { } media &&
                !Excluded(attempt.Target) && Fresh(attempt) && Accepts(tier, media, exact))
            .Select(attempt => attempt.Media).FirstOrDefault();
        internal void Reject(ResolvedMusic media, string code)
        {
            rejected.Add(SourceIdentity(media.Target!));
            foreach (var attempt in Attempts.Values.Where(attempt => attempt.Media is { } value &&
                SourceIdentity(value.Target!) == SourceIdentity(media.Target!)))
            { attempt.State = SourceAttemptState.Unavailable; attempt.Code = code; attempt.Media = null; }
        }

        private async Task<MusicOnlineTarget[]> OfficialAsync(string level, CancellationToken token)
        {
            if (official.TryGetValue(level, out var known)) return known;
            var reply = await owner.QualityCall("music.official.candidates",
                new { song = expanded, quality = level, qualityPolicySignature = Signature }, token);
            MusicDto.Require(MusicDto.Text(reply, "qualityPolicySignature") == Signature);
            var decorated = SongRow.Read(reply.GetProperty("song"), Song.EntryId);
            MusicDto.Require(decorated.Id == Song.Id);
            expanded = decorated.Raw.Clone();
            var candidates = reply.GetProperty("candidates").EnumerateArray().ToArray();
            MusicDto.Require(candidates.Length <= 24);
            if (preference.Length == 0) preference = candidates.Select(row => MusicDto.Text(row, "sourceKey")).ToArray();
            var targets = candidates.Select(row => new MusicOnlineTarget(null, MusicDto.Text(row, "platform"),
                    MusicDto.Integer(row, "generation"), MusicDto.Text(row, "sourceKey"), level, level, Signature,
                    AccountScope: MusicDto.Text(row, "scope"), RecordingSong: expanded, Automatic: true))
                .Where(target => preferredPlatform is null || target.Platform == preferredPlatform && target.SourceKey == preferredKey)
                .ToArray();
            official[level] = targets;
            return targets;
        }
        private IEnumerable<MusicOnlineTarget> ScriptTargets(string level)
        {
            if (officialOnly || !MusicQualityPolicy.LxTypes.Contains(level)) yield break;
            var recordings = expanded.GetProperty("sources").EnumerateArray()
                .Where(row => preferredPlatform is null || MusicDto.Text(row, "source") == preferredPlatform &&
                    MusicDto.Text(row, "sourceKey") == preferredKey)
                .OrderBy(row => Array.IndexOf(preference, MusicDto.Text(row, "sourceKey")) is var index && index >= 0 ? index : int.MaxValue);
            foreach (var recording in recordings)
            {
                string platform = MusicDto.Text(recording, "source"), key = MusicDto.Text(recording, "sourceKey");
                if (!Library.NativeMusicSong.Platforms.Contains(platform)) continue;
                var data = recording.TryGetProperty("musicInfo", out var info) ? info : recording;
                if (data.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array &&
                    !types.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.Object &&
                        MusicDto.Text(type, "type") == level)) continue;
                foreach (var script in Scripts)
                {
                    // Intersection dispatch: an installation is asked only for platforms it declares.
                    if (script.Sites is { } sites && !(sites.TryGetProperty(platform, out var site) &&
                        site.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "musicUrl") &&
                        site.GetProperty("qualitys").EnumerateArray().Any(quality => quality.GetString() == level)))
                        continue;
                    yield return new(script.Installation, platform, 0, key, level, level, Signature,
                        RecordingSong: expanded, Automatic: true);
                }
            }
        }
        private async Task<(MusicOnlineTarget Target, string Level)[]> CandidatesAsync(MusicQualityPolicy.Tier tier,
            CancellationToken token)
        {
            var result = new List<(MusicOnlineTarget, string)>();
            foreach (string level in tier.Levels)
            {
                foreach (var target in await OfficialAsync(level, token)) result.Add((target, level));
                foreach (var target in ScriptTargets(level)) result.Add((target, level));
            }
            return result.Where(pair => !Excluded(pair.Item1) && !rejected.Contains(SourceIdentity(pair.Item1)) &&
                !Capped(pair.Item1, pair.Item2)).ToArray();
        }
        /// <summary>A built-in provider that already answered a better request with a lower level
        /// cannot offer a level in between; asking again would only repeat that answer.</summary>
        private bool Capped(MusicOnlineTarget target, string level) => target.Official && Attempts.Values.Any(attempt =>
            attempt is { State: SourceAttemptState.Ok, Media: { } media } && attempt.Target.Official &&
            attempt.Target.Platform == target.Platform && attempt.Target.SourceKey == target.SourceKey &&
            MusicQualityPolicy.Priority(attempt.Target.PolicyRequest, Policy) < MusicQualityPolicy.Priority(level, Policy) &&
            MusicQualityPolicy.Priority(EffectiveLevel(media), Policy) > MusicQualityPolicy.Priority(level, Policy));

        private async Task<ResolvedMusic?> TryAsync(MusicOnlineTarget target, string level, MusicQualityPolicy.Tier tier,
            bool exact, CancellationToken token, bool cacheOnly = false, bool urlCacheOnly = false)
        {
            string key = AttemptKey(target, level);
            if (Attempts.TryGetValue(key, out var known) && known.State != SourceAttemptState.Pending)
            {
                if (known.State == SourceAttemptState.Unavailable) return null;
                if (Fresh(known)) return known.Media is { } media && Accepts(tier, media, exact) ? media : null;
                known.State = SourceAttemptState.Pending; known.Media = null;
            }
            var attempt = known ?? new(key, target);
            Attempts[key] = attempt;
            try
            {
                ResolvedMusic media = target.Official
                    ? await owner.ResolveOfficialTarget(Song, target, token, initial: true, useCache: cacheOnly,
                        cacheOnly: cacheOnly, urlCacheOnly: urlCacheOnly, refresh: !cacheOnly && !urlCacheOnly)
                    : await owner.ResolveMusicQualityTarget(Song, target, token, initial: true);
                token.ThrowIfCancellationRequested();
                await LoadExclusionsAsync(token);
                if (Excluded(target)) return null;
                attempt.State = SourceAttemptState.Ok; attempt.Media = media; attempt.Code = null;
                attempt.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return Accepts(tier, media, exact) ? media : null;
            }
            catch (MusicOperationException error) when (error.Code == "CACHE_MISS" && (cacheOnly || urlCacheOnly))
            { if (attempt.State == SourceAttemptState.Pending && known is null) Attempts.Remove(key); return null; }
            catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
            {
                attempt.State = SourceAttemptState.Unavailable; attempt.Code = error.Code; attempt.Media = null;
                Failure ??= error.Code;
                return null;
            }
        }
        private async Task<ResolvedMusic?> PassAsync(MusicQualityPolicy.Tier tier, (MusicOnlineTarget Target, string Level)[] candidates,
            bool collect, bool exact, CancellationToken token)
        {
            ResolvedMusic? found = collect ? null : Known(tier, exact);
            if (found is not null) return found;
            // Cache first: complete files, then still-valid URL receipts of built-in providers.
            foreach (var (target, level) in candidates.Where(pair => pair.Target.Official))
                if (await TryAsync(target, level, tier, exact, token, cacheOnly: true) is { } cached)
                { if (!collect) return cached; found ??= cached; }
            foreach (var (target, level) in candidates.Where(pair => pair.Target.Official))
                if (await TryAsync(target, level, tier, exact, token, urlCacheOnly: true) is { } cached)
                { if (!collect) return cached; found ??= cached; }
            var work = candidates.Where(pair => !Attempts.TryGetValue(AttemptKey(pair.Target, pair.Level), out var attempt) ||
                attempt.State == SourceAttemptState.Pending || attempt.State == SourceAttemptState.Ok && !Fresh(attempt)).ToArray();
            if (work.Length == 0) return found ?? Known(tier, exact);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var gate = new SemaphoreSlim(3);
            ExceptionDispatch? failure = null;
            await Task.WhenAll(work.Select(async pair =>
            {
                bool entered = false;
                try
                {
                    await gate.WaitAsync(stop.Token); entered = true;
                    if (stop.IsCancellationRequested) return;
                    if (await TryAsync(pair.Target, pair.Level, tier, exact, stop.Token) is { } media)
                    {
                        found ??= media;
                        if (!collect) stop.Cancel();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && stop.IsCancellationRequested) { }
                catch (MusicOperationException error) when (error.Code == "CANCELLED" &&
                    !token.IsCancellationRequested && stop.IsCancellationRequested) { }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    failure ??= new(error);
                    stop.Cancel();
                }
                finally { if (entered) gate.Release(); }
            }));
            token.ThrowIfCancellationRequested();
            foreach (var attempt in Attempts.Values.Where(attempt => attempt.State == SourceAttemptState.Pending &&
                work.Any(pair => AttemptKey(pair.Target, pair.Level) == attempt.Key)).ToArray())
                Attempts.Remove(attempt.Key); // Cancelled after another source won: still untried.
            if (failure is not null) failure.Throw();
            return found ?? Known(tier, exact);
        }
        private sealed class ExceptionDispatch(Exception error)
        {
            internal void Throw() => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        /// <summary>Searches the platforms the song has no accepted mapping for, once per session,
        /// limited to platforms some enabled provider can actually resolve.</summary>
        private async Task<bool> CompleteAsync(CancellationToken token)
        {
            if (completed || officialOnly) return false;
            completed = true;
            var known = expanded.GetProperty("sources").EnumerateArray().Select(row => MusicDto.Text(row, "source"))
                .ToHashSet(StringComparer.Ordinal);
            var targets = Library.NativeMusicSong.Platforms.Where(platform => !known.Contains(platform) &&
                    (officialEnabled.Contains(platform) || Scripts.Any(script => script.Sites is not { } sites ||
                        sites.TryGetProperty(platform, out _))))
                .ToArray();
            if (targets.Length == 0 || preferredPlatform is not null) return false;
            var reply = await owner.Call("music.songs.complete", new { song = expanded, platforms = targets }, token);
            var merged = SongRow.Read(reply.GetProperty("song"), Song.EntryId);
            MusicDto.Require(merged.Id == Song.Id);
            int before = known.Count;
            expanded = merged.Raw.Clone();
            official.Clear();
            return expanded.GetProperty("sources").EnumerateArray().Select(row => MusicDto.Text(row, "source"))
                .Distinct(StringComparer.Ordinal).Count() > before;
        }

        internal async Task<ResolvedMusic?> TierAsync(MusicQualityPolicy.Tier tier, bool collect, bool complete,
            CancellationToken token, bool exact = false)
        {
            await selection.WaitAsync(token);
            try
            {
                await LoadExclusionsAsync(token);
                var found = await PassAsync(tier, await CandidatesAsync(tier, token), collect, exact, token);
                if ((found is null || collect) && complete && await CompleteAsync(token))
                    found = await PassAsync(tier, await CandidatesAsync(tier, token), collect, exact, token) ?? found;
                // A winning automatic request leaves the remaining same-tier work queued.
                // Only a collection pass or a definite miss exhausts this tier.
                if (collect || found is null) exhausted.Add(tier.Category + ":" + string.Join(",", tier.Levels));
                return found;
            }
            finally { selection.Release(); }
        }
        /// <summary>Automatic: the first tier with a playable source; later tiers only after it is exhausted.</summary>
        internal async Task<ResolvedMusic> AutomaticAsync(CancellationToken token)
        {
            if (Tiers.Length == 0) throw new MusicOperationException("QUALITY_DISABLED");
            foreach (var tier in Tiers)
                if (await TierAsync(tier, collect: false, complete: true, token) is { } media) return media;
            throw new MusicOperationException(Failure ?? "PLAYBACK_UNAVAILABLE");
        }
        /// <summary>One exact tier, never another: manual quality and wrong-source replacement.</summary>
        internal Task<ResolvedMusic?> ExactAsync(MusicQualityPolicy.Tier tier, CancellationToken token) =>
            TierAsync(tier, collect: false, complete: true, token, exact: true);
        /// <summary>The next automatic tier with queued candidates, collecting every result of it.</summary>
        internal async Task<bool> ExpandAsync(CancellationToken token)
        {
            var tier = Tiers.FirstOrDefault(value => !exhausted.Contains(value.Category + ":" + string.Join(",", value.Levels)));
            if (tier is null) return false;
            await TierAsync(tier, collect: true, complete: true, token);
            return true;
        }
        internal bool CanExpand => Tiers.Any(value => !exhausted.Contains(value.Category + ":" + string.Join(",", value.Levels)));
        internal void Exclude(string provider, string platform)
        {
            exclusions.Add(new(provider, platform));
            foreach (var attempt in Attempts.Values.Where(attempt => Excluded(attempt.Target)))
            { attempt.State = SourceAttemptState.Unavailable; attempt.Code = "SOURCE_EXCLUDED"; attempt.Media = null; }
        }
    }

    private async Task<MusicSourceSession> BeginSourceSession(SongRow song, CancellationToken token, bool officialOnly = false)
    {
        var settings = await QualityCall("music.quality.settings.get", null, token);
        AcceptQuality(settings);
        var policy = JsonNode.Parse(settings.GetProperty("settings").GetRawText())!.AsObject();
        string signature = MusicDto.Text(settings, "signature");
        var officials = await Call("music.officialSources.settings.get", cancellationToken: token);
        var enabled = officials.GetProperty("settings").GetProperty("enabled").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        SourceScript[] scripts = [];
        bool unavailable = false;
        if (!officialOnly)
        {
            try
            {
                var list = await Call("music.sources.list", cancellationToken: token);
                scripts = list.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("enabled").GetBoolean())
                    .Select(item => new SourceScript(MusicDto.Text(item, "installationId"), MusicDto.Text(item, "name"),
                        item.GetProperty("sites") is { ValueKind: JsonValueKind.Object } sites ? sites.Clone() : null))
                    .ToArray();
            }
            // The script service failing never blocks built-in providers.
            catch (MusicOperationException error) when (error.Code == "SCRIPT_RUNTIME_UNAVAILABLE") { unavailable = true; }
        }
        string? platform = recordingPlatforms.GetValueOrDefault(song.Id);
        string? key = platform is null ? null : recordingChoices.GetValueOrDefault(song.Id + "\0" + platform);
        var session = new MusicSourceSession(this, song, signature, policy, scripts, enabled, officialOnly, platform, key);
        if (unavailable) session.Failure = "SCRIPT_RUNTIME_UNAVAILABLE";
        await session.LoadExclusionsAsync(token);
        return session;
    }
}
