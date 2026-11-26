using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

/// <summary>
/// Per-playback-intent source selection session.
/// Automatic play, manual quality, source list, "load more" and "wrong source" share its attempts;
/// each tier is exhausted over every eligible source (cache first, then bounded 3-way network probes)
/// before a later tier is tried.
/// </summary>
internal sealed class MusicSourceSession(
    MusicPlaybackController owner,
    SongRow song,
    string signature,
    MusicQualityPolicyRecord policy,
    HashSet<string> officialEnabled,
    bool officialOnly,
    string? preferredPlatform,
    string? preferredKey) : IAsyncDisposable
{
    private sealed record SourceScript(
        string Installation,
        string Name,
        IReadOnlyDictionary<string, MusicScriptSiteCapability>? Sites);
    private sealed record SourceExclusion(string Provider, string Platform);

    private const long ScriptMediaTtl = 10 * 60 * 1000;
    internal readonly SongRow Song = song;
    internal readonly string Signature = signature;
    internal readonly long Revision = owner.SelectionSourcesRevision;
    internal readonly MusicQualityPolicyRecord Policy = policy;
    internal readonly MusicQualityPolicy.Tier[] Tiers = MusicQualityPolicy.Tiers(policy);
    internal readonly Dictionary<string, SourceAttempt> Attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MusicOnlineTarget[]> official = new(StringComparer.Ordinal);
    private readonly HashSet<string> exhausted = new(StringComparer.Ordinal);
    private readonly HashSet<string> rejected = new(StringComparer.Ordinal);
    private readonly HashSet<string> mismatchedRecordings = new(StringComparer.Ordinal);
    private readonly HashSet<string> contentFailed = new(StringComparer.Ordinal);
    private readonly HashSet<string> otherFailed = new(StringComparer.Ordinal);
    private readonly HashSet<string> checkedContent = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim selection = new(1);
    private readonly object disposeGate = new();
    private readonly bool OfficialOnly = officialOnly;
    private HashSet<SourceExclusion> exclusions = [];
    private long exclusionsRevision = -1;
    private MusicSongRecord expanded = song.Record;
    private long expandedPublication = song.RepositoryPublication;
    private string[] preference = [];
    private bool completed, historyLoaded;
    private int activeTiers;
    private TaskCompletionSource tiersDrained = CompletedTiers();
    private Task? disposal;
    private SourceScript[] Scripts = [];
    private bool scriptsLoaded = officialOnly;

    private static TaskCompletionSource CompletedTiers()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    internal string? Failure;

    private void AcceptSong(MusicSongRecord value, long publication)
    {
        if (publication < expandedPublication) return;
        expandedPublication = publication;
        if (value.ContentEquals(expanded)) return;
        expanded = value;
        official.Clear(); exhausted.Clear();
    }

    private void RefreshSong()
    {
        if (Song.RepositoryPublication > expandedPublication)
            AcceptSong(Song.Record, Song.RepositoryPublication);
    }

    internal async Task RefreshSongAsync(CancellationToken token)
    {
        RefreshSong();
        var snapshot = await owner.Application.Library.SongSnapshotsAsync([Song.Id], token);
        token.ThrowIfCancellationRequested();
        if (snapshot.Publication > expandedPublication && snapshot.Songs.GetValueOrDefault(Song.Id) is { } value)
            AcceptSong(value, snapshot.Publication);
    }

    private async Task AcceptReplyAsync(MusicSongRecord value, CancellationToken token)
    {
        var snapshot = await owner.Application.Library.SongSnapshotsAsync([Song.Id], token);
        token.ThrowIfCancellationRequested();
        AcceptSong(snapshot.Songs.GetValueOrDefault(Song.Id) ?? value, snapshot.Publication);
    }

    internal void ReleaseRecording()
    {
        preferredPlatform = preferredKey = null;
        official.Clear(); exhausted.Clear();
    }

    private async Task LoadScriptsAsync(CancellationToken token)
    {
        if (scriptsLoaded) return;
        try
        {
            var list = await owner.Application.Scripts.ListSourcesAsync(token);
            Scripts = list.Items.Where(item => item.Enabled)
                .Select(item => new SourceScript(item.InstallationId, item.Name, item.Sites)).ToArray();
            scriptsLoaded = true;
        }
        catch (Exception error) when (MusicRuntimeContext.ErrorCode(error) is { } code && MusicPlaybackController.UnavailableSourceCandidate(code))
        {
            DebugLog.Warn(DebugCategories.MusicPlayback, "source-session", "scripts-unavailable",
                new { songId = Song.Id, code });
            Failure ??= code;
            scriptsLoaded = true;
        }
    }

    internal void Inherit(MusicSourceSession previous)
    {
        if (previous.expandedPublication >= expandedPublication)
            AcceptSong(previous.expanded, previous.expandedPublication);
        foreach (var pair in previous.Attempts.Where(pair => pair.Value.State == SourceAttemptState.Ok && Fresh(pair.Value) &&
            (!pair.Value.Target.Official || previous.Signature == Signature &&
                pair.Value.Target.AccountScope is { } scope &&
                owner.Application.IsOfficialPlaybackCurrent(pair.Value.Target.Platform, scope, pair.Value.Target.Generation))))
            Attempts[pair.Key] = pair.Value;
    }

    private async Task LoadHistoryAsync(CancellationToken token)
    {
        if (historyLoaded || OfficialOnly || owner.AudioFileCache is null) return;
        foreach (var info in await owner.AudioFileCache.ReadScriptHistoryAsync(Song.Id, token))
        {
            RefreshSong();
            string level = info.Quality!;
            var target = new MusicOnlineTarget(info.InstallationId!, info.Platform!,
                0, info.SourceKey!, level, level, RecordingSong: expanded,
                ProviderName: info.ProviderName ?? "");
            if (Excluded(target) || Rejected(target)) continue;
            string key = AttemptKey(target, level);
            Attempts[key] = new(key, target)
            {
                State = SourceAttemptState.Ok,
                Media = new(null, new Dictionary<string, string>(), target, Receipt: info,
                    File: new(info.Scope!, info.QualityCacheSignature!))
            };
        }
        historyLoaded = true;
    }

    private static string AttemptKey(MusicOnlineTarget target, string level) =>
        MusicRules.CanonicalArray(target.Installation ?? MusicLibraryOwner.OfficialProvider,
            target.Platform, target.SourceKey ?? "\0", level);

    internal bool Excluded(MusicOnlineTarget target) =>
        exclusions.Contains(new(target.Installation ?? MusicLibraryOwner.OfficialProvider, target.Platform)) ||
        (target.SourceKey is { Length: > 0 } sourceKey &&
         exclusions.Contains(new(MusicSourceExclusionsOwner.MakeRecordingProvider(sourceKey), target.Platform)));

    internal bool HasOtherUnexcludedRecording(string sourceKey)
    {
        RefreshSong();
        foreach (var src in expanded.Sources)
        {
            if (src.SourceKey == sourceKey) continue;
            if (!exclusions.Contains(new(MusicSourceExclusionsOwner.MakeRecordingProvider(src.SourceKey), src.Platform)))
                return true;
        }
        return false;
    }

    private bool Rejected(MusicOnlineTarget target) =>
        rejected.Contains(AttemptKey(target, target.PolicyRequest)) ||
        mismatchedRecordings.Contains(MusicPlaybackController.SourceIdentity(target));

    internal bool CanReuse(ResolvedMusic media) => Revision == owner.SelectionSourcesRevision &&
        Signature == owner.Settings.SavedQualitySignature && exclusionsRevision == owner.ExclusionsRevision &&
        !Excluded(media.Target!) && !Rejected(media.Target!);

    internal static bool Fresh(SourceAttempt attempt) => attempt.Media is { } media && (media.Uri is null ||
        (media.ExpiresAt ?? attempt.At + ScriptMediaTtl) - 30_000 > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private bool Accepts(MusicQualityPolicy.Tier tier, ResolvedMusic media, bool exact) =>
        tier.Levels.Contains(MusicPlaybackController.EffectiveLevel(media)) || !exact && tier.Levels.Any(level =>
            MusicQualityPolicy.Priority(MusicPlaybackController.EffectiveLevel(media), Policy) < MusicQualityPolicy.Priority(level, Policy));

    internal async Task LoadExclusionsAsync(CancellationToken token)
    {
        long revision = owner.ExclusionsRevision;
        if (revision == exclusionsRevision) return;
        var list = await owner.Application.Library.ListSourceExclusionsAsync(songId: Song.Id, cancellationToken: token);
        var next = list.Items.Select(item => new SourceExclusion(item.Provider, item.Platform)).ToHashSet();
        bool restored = exclusions.Except(next).Any();
        exclusions = next;
        exclusionsRevision = revision;
        foreach (var attempt in Attempts.Values.Where(attempt =>
            attempt.Code == "SOURCE_EXCLUDED" && !Excluded(attempt.Target)).ToArray())
            Attempts.Remove(attempt.Key);
        if (restored) { exhausted.Clear(); historyLoaded = false; }
        foreach (var attempt in Attempts.Values.Where(attempt => Excluded(attempt.Target)))
        { attempt.State = SourceAttemptState.Unavailable; attempt.Code = "SOURCE_EXCLUDED"; attempt.Media = null; }
    }

    private ResolvedMusic? Known(MusicQualityPolicy.Tier tier, bool exact) => Attempts.Values
        .Where(attempt => attempt.State == SourceAttemptState.Ok && attempt.Media is { } media &&
            !Excluded(attempt.Target) && !Rejected(attempt.Target) &&
            (!attempt.Target.Official || attempt.Target.AccountScope is { } scope &&
                owner.Application.IsOfficialPlaybackCurrent(attempt.Target.Platform, scope, attempt.Target.Generation)) &&
            Fresh(attempt) && Accepts(tier, media, exact))
        .Select(attempt => attempt.Media).FirstOrDefault();

    internal void Reject(ResolvedMusic media, string code) => Reject(media.Target!, code);

    internal void ObserveFailure(MusicOnlineTarget target, string code)
    {
        if (code != "CACHE_MISS" && !code.StartsWith("MEDIA_READ_", StringComparison.Ordinal))
            (MusicPlaybackController.ContentSourceFailure(code) ? contentFailed : otherFailed).Add(target.Platform);
    }

    private void Reject(MusicOnlineTarget target, string code)
    {
        ObserveFailure(target, code);
        DebugLog.Warn(DebugCategories.MusicPlayback, "source-session", "candidate-rejected",
            new
            {
                songId = Song.Id,
                provider = target.Installation ?? MusicLibraryOwner.OfficialProvider,
                platform = target.Platform,
                quality = target.PolicyRequest,
                code
            });
        if (code is "RECORDING_MISMATCH" or "IDENTITY_MISMATCH" or "MEDIA_MISMATCH")
            mismatchedRecordings.Add(MusicPlaybackController.SourceIdentity(target));
        else rejected.Add(AttemptKey(target, target.PolicyRequest));
        foreach (var attempt in Attempts.Values.Where(attempt => Rejected(attempt.Target)))
        { attempt.State = SourceAttemptState.Unavailable; attempt.Code = code; attempt.Media = null; }
    }

    private async Task<MusicOnlineTarget[]> OfficialAsync(string level, CancellationToken token, bool manualSource = false)
    {
        RefreshSong();
        string cacheKey = level + (manualSource ? ":manual" : ":automatic");
        if (official.TryGetValue(cacheKey, out var known)) return known;
        var result = await owner.Application.GetOfficialCandidatesAsync(expanded, level, Signature, manualSource, token);
        var decorated = SongRow.FromRecord(result.Song, Song.EntryId);
        await AcceptReplyAsync(decorated.Record, token);
        if (preference.Length == 0) preference = result.Candidates.Select(row => row.SourceKey).ToArray();
        var targets = result.Candidates.Select(row => new MusicOnlineTarget(null, row.Platform,
                row.Generation, row.SourceKey, level, level, Signature,
                AccountScope: row.Scope, RecordingSong: expanded, ManualSource: manualSource))
            .OrderBy(target => target.Platform == preferredPlatform && target.SourceKey == preferredKey ? 0 : 1)
            .ToArray();
        if (expanded.ContentEquals(decorated.Record)) official[cacheKey] = targets;
        return targets;
    }

    private IEnumerable<MusicOnlineTarget> ScriptTargets(string level, bool manualSource = false)
    {
        if (OfficialOnly || !MusicQualityPolicy.LxTypes.Contains(level)) yield break;
        RefreshSong();
        var recordings = expanded.Sources
            .OrderBy(row => row.Platform == preferredPlatform && row.SourceKey == preferredKey ? 0 : 1)
            .ThenBy(row => Array.IndexOf(preference, row.SourceKey) is var index && index >= 0 ? index : int.MaxValue);
        foreach (var recording in recordings)
        {
            string platform = recording.Platform, key = recording.SourceKey;
            if (!NativeMusicSong.Platforms.Contains(platform)) continue;
            if (!NativeMusicSong.HasRecordingQuality(recording, level)) continue;
            foreach (var script in Scripts)
            {
                if (script.Sites is { } sites && !(sites.TryGetValue(platform, out var site) &&
                    site.SupportsAction("musicUrl") && site.SupportsQuality(level)))
                    continue;
                yield return new(script.Installation, platform, 0, key, level, level, Signature,
                    RecordingSong: expanded, ProviderName: script.Name, SourceRevision: Revision, ManualSource: manualSource);
            }
        }
    }

    private async Task<(MusicOnlineTarget Target, string Level)[]> CandidatesAsync(MusicQualityPolicy.Tier tier,
        CancellationToken token, bool manualSource = false)
    {
        var result = new List<(MusicOnlineTarget, string)>();
        foreach (string level in tier.Levels)
        {
            foreach (var target in await OfficialAsync(level, token, manualSource)) result.Add((target, level));
            foreach (var target in ScriptTargets(level, manualSource)) result.Add((target, level));
        }
        return result.Where(pair => !Excluded(pair.Item1) && !Rejected(pair.Item1) &&
            (manualSource || !Capped(pair.Item1, pair.Item2))).ToArray();
    }

    private bool Capped(MusicOnlineTarget target, string level) => target.Official && Attempts.Values.Any(attempt =>
        attempt is { State: SourceAttemptState.Ok, Media: { } media } && attempt.Target.Official &&
        attempt.Target.Platform == target.Platform && attempt.Target.SourceKey == target.SourceKey &&
        MusicQualityPolicy.Priority(attempt.Target.PolicyRequest, Policy) < MusicQualityPolicy.Priority(level, Policy) &&
        MusicQualityPolicy.Priority(MusicPlaybackController.EffectiveLevel(media), Policy) > MusicQualityPolicy.Priority(level, Policy));

    private async Task<ResolvedMusic?> TryAsync(MusicOnlineTarget target, string level, MusicQualityPolicy.Tier tier,
        bool exact, CancellationToken token, bool cacheOnly = false, bool urlCacheOnly = false)
    {
        if (Excluded(target) || Rejected(target)) return null;
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
                ? await owner.ResolveOfficialTargetAsync(Song, target, token, initial: true,
                    cacheOnly: cacheOnly, urlCacheOnly: urlCacheOnly, refresh: !cacheOnly && !urlCacheOnly)
                : await owner.ResolveScriptTargetAsync(Song, target, token, initial: true);
            token.ThrowIfCancellationRequested();
            await LoadExclusionsAsync(token);
            if (Excluded(target) || Rejected(target)) return null;
            attempt.State = SourceAttemptState.Ok; attempt.Media = media; attempt.Code = null;
            attempt.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool accepted = Accepts(tier, media, exact);
            DebugLog.Info(DebugCategories.MusicPlayback, "source-session", "candidate-resolved",
                new
                {
                    songId = Song.Id,
                    provider = target.Installation ?? MusicLibraryOwner.OfficialProvider,
                    platform = target.Platform,
                    requestedQuality = level,
                    effectiveQuality = MusicPlaybackController.EffectiveLevel(media),
                    cacheMode = cacheOnly ? "file" : urlCacheOnly ? "url" : "network",
                    accepted
                });
            return accepted ? media : null;
        }
        catch (Exception error) when (MusicRuntimeContext.ErrorCode(error) == "CACHE_MISS" && (cacheOnly || urlCacheOnly))
        { if (attempt.State == SourceAttemptState.Pending && known is null) Attempts.Remove(key); return null; }
        catch (Exception error) when (MusicRuntimeContext.ErrorCode(error) is { } code && MusicPlaybackController.UnavailableSourceCandidate(code))
        {
            Reject(target, code);
            Failure ??= code;
            return null;
        }
    }

    private async Task<ResolvedMusic?> PassAsync(MusicQualityPolicy.Tier tier, (MusicOnlineTarget Target, string Level)[] candidates,
        bool collect, bool exact, CancellationToken token)
    {
        ResolvedMusic? found = collect ? null : Known(tier, exact);
        if (found is not null) return found;
        foreach (var (target, level) in candidates.Where(pair => pair.Target.Official))
            if (await TryAsync(target, level, tier, exact, token, cacheOnly: true) is { } cached)
            { if (!collect) return cached; found ??= cached; }
        foreach (var (target, level) in candidates.Where(pair => pair.Target.Official))
            if (await TryAsync(target, level, tier, exact, token, urlCacheOnly: true) is { } cached)
            { if (!collect) return cached; found ??= cached; }
        bool loadedScripts = scriptsLoaded;
        await LoadScriptsAsync(token);
        if (!loadedScripts) candidates = candidates.Concat(tier.Levels.SelectMany(level => ScriptTargets(level)
                .Select(target => (Target: target, Level: level))))
            .DistinctBy(pair => AttemptKey(pair.Target, pair.Level))
            .Where(pair => !Excluded(pair.Target) && !Rejected(pair.Target)).ToArray();
        var work = candidates.Where(pair => !Attempts.TryGetValue(AttemptKey(pair.Target, pair.Level), out var attempt) ||
            attempt.State == SourceAttemptState.Pending || attempt.State == SourceAttemptState.Ok && !Fresh(attempt)).ToArray();
        if (work.Length == 0) return found ?? Known(tier, exact);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var gate = new SemaphoreSlim(3);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
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
            catch (Exception error) when (MusicRuntimeContext.ErrorCode(error) == "CANCELLED" &&
                !token.IsCancellationRequested && stop.IsCancellationRequested) { }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                stop.Cancel();
            }
            finally { if (entered) gate.Release(); }
        }));
        token.ThrowIfCancellationRequested();
        foreach (var attempt in Attempts.Values.Where(attempt => attempt.State == SourceAttemptState.Pending &&
            work.Any(pair => AttemptKey(pair.Target, pair.Level) == attempt.Key)).ToArray())
            Attempts.Remove(attempt.Key);
        failure?.Throw();
        return found ?? Known(tier, exact);
    }

    private async Task<bool> CompleteAsync(CancellationToken token)
    {
        if (OfficialOnly) return false;
        var failures = contentFailed.Except(otherFailed).Except(checkedContent).ToArray();
        if (completed && failures.Length == 0) return false;
        var targets = NativeMusicSong.Platforms.Where(platform =>
                (!completed || failures.Contains(platform)) &&
                (officialEnabled.Contains(platform) || Scripts.Any(script => script.Sites is not { } sites ||
                    sites.TryGetValue(platform, out var site) && site.SupportsAction("musicUrl"))))
            .ToArray();
        if (targets.Length == 0) { completed = true; return false; }
        RefreshSong();
        var beforeSources = expanded.Sources;
        var completion = await owner.Application.CompleteSongAsync(
            expanded, targets, failures.Intersect(targets).ToArray(), token);
        token.ThrowIfCancellationRequested();
        var merged = SongRow.FromRecord(completion.Song, Song.EntryId);
        await AcceptReplyAsync(merged.Record, token);
        completed = true;
        checkedContent.UnionWith(failures);
        official.Clear();
        bool changed = !SourcesEqual(expanded.Sources, beforeSources);
        if (changed) exhausted.Clear();
        DebugLog.Info(DebugCategories.MusicPlayback, "source-session", "complete-song",
            new
            {
                songId = Song.Id,
                targets = string.Join(",", targets),
                changed,
                failures = completion.Failures.Length
            });
        return changed;
    }

    internal async Task<ResolvedMusic?> TierAsync(MusicQualityPolicy.Tier tier, bool collect, bool complete,
        CancellationToken token, bool exact = false, ResolvedMusic? resolved = null, bool manualSource = false)
    {
        lock (disposeGate)
        {
            if (disposal is not null) throw new OperationCanceledException(token);
            if (activeTiers++ == 0) tiersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        bool entered = false;
        try
        {
            await selection.WaitAsync(token);
            entered = true;
            lock (disposeGate) if (disposal is not null) throw new OperationCanceledException(token);
            await RefreshSongAsync(token);
            await LoadExclusionsAsync(token);
            await LoadHistoryAsync(token);
            if (!collect && Known(tier, exact) is { } history) return history;
            if (collect || manualSource) await LoadScriptsAsync(token);
            if (!collect && resolved is not null && !Excluded(resolved.Target!) && !Rejected(resolved.Target!) &&
                (resolved.ExpiresAt is null || resolved.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) &&
                Accepts(tier, resolved, exact)) return resolved;
            async Task<(MusicOnlineTarget Target, string Level)[]> Candidates()
            {
                var candidates = await CandidatesAsync(tier, token, manualSource);
                if (resolved is null) return candidates;
                string resolvedKey = AttemptKey(resolved.Target!, resolved.Target!.PolicyRequest);
                return candidates.Where(pair => AttemptKey(pair.Target, pair.Level) != resolvedKey).ToArray();
            }
            var found = await PassAsync(tier, await Candidates(), collect, exact, token);
            if ((found is null || collect) && complete && await CompleteAsync(token))
                found = await PassAsync(tier, await Candidates(), collect, exact, token) ?? found;
            if (collect && manualSource) exhausted.Add(tier.Category + ":" + string.Join(",", tier.Levels));
            return found;
        }
        finally
        {
            if (entered) selection.Release();
            lock (disposeGate) if (--activeTiers == 0) tiersDrained.TrySetResult();
        }
    }

    internal async Task<ResolvedMusic> AutomaticAsync(CancellationToken token, ResolvedMusic? resolved = null)
    {
        if (Tiers.Length == 0) throw new MusicOperationException("QUALITY_DISABLED");
        foreach (var tier in Tiers)
            if (await TierAsync(tier, collect: false, complete: true, token, resolved: resolved) is { } media)
            {
                DebugLog.Info(DebugCategories.MusicPlayback, "source-session", "automatic-selected",
                    new
                    {
                        songId = Song.Id,
                        tier = tier.Category,
                        provider = media.Target!.Installation ?? MusicLibraryOwner.OfficialProvider,
                        platform = media.Target.Platform,
                        quality = MusicPlaybackController.EffectiveLevel(media),
                        cachedFile = media.Uri is null
                    });
                return media.Target!.ManualSource ? media with { Target = media.Target with { ManualSource = false } } : media;
            }
        DebugLog.Warn(DebugCategories.MusicPlayback, "source-session", "automatic-exhausted",
            new { songId = Song.Id, failure = Failure ?? "PLAYBACK_UNAVAILABLE" });
        throw new MusicOperationException(Failure ?? "PLAYBACK_UNAVAILABLE");
    }

    internal async Task<ResolvedMusic> RefreshAsync(MusicOnlineTarget target, CancellationToken token)
    {
        try { return await RefreshTargetAsync(target, token); }
        catch (Exception error) when (MusicRuntimeContext.ErrorCode(error) is { } code && MusicPlaybackController.UnavailableSourceCandidate(code))
        {
            Reject(target, code);
            Failure ??= code;
            throw;
        }
    }

    private async Task<ResolvedMusic> RefreshTargetAsync(MusicOnlineTarget target, CancellationToken token)
    {
        await LoadExclusionsAsync(token);
        await RefreshSongAsync(token);
        target = target with { RecordingSong = expanded };
        if (Excluded(target)) throw new MusicOperationException("SOURCE_EXCLUDED");
        ResolvedMusic media;
        if (!target.Official && (target.QualityPolicySignature != Signature || target.Generation == 0 || target.SourceRevision != Revision))
        {
            await LoadScriptsAsync(token);
            var eligible = ScriptTargets(target.Quality, target.ManualSource)
                .FirstOrDefault(value => MusicPlaybackController.SourceIdentity(value) == MusicPlaybackController.SourceIdentity(target));
            if (eligible is null) throw new MusicOperationException("SOURCE_DISABLED");
            media = await owner.ResolveScriptTargetAsync(Song, eligible, token, initial: true);
        }
        else if (target.Official && (target.QualityPolicySignature != Signature ||
            target.AccountScope is not { } scope ||
            !owner.Application.IsOfficialPlaybackCurrent(target.Platform, scope, target.Generation)))
        {
            var eligible = (await OfficialAsync(target.PolicyRequest, token, target.ManualSource))
                .FirstOrDefault(value => MusicPlaybackController.SourceIdentity(value) == MusicPlaybackController.SourceIdentity(target));
            if (eligible is null) throw new MusicOperationException("RECORDING_UNAVAILABLE");
            media = await owner.ResolveOfficialTargetAsync(Song, eligible, token, initial: true);
        }
        else media = await owner.ResolveTargetAsync(Song, target, token);
        token.ThrowIfCancellationRequested();
        await LoadExclusionsAsync(token);
        if (Excluded(media.Target!)) throw new MusicOperationException("SOURCE_EXCLUDED");
        Remember(media);
        return media;
    }

    internal void Remember(ResolvedMusic media)
    {
        string key = AttemptKey(media.Target!, media.Target!.PolicyRequest);
        Attempts[key] = new(key, media.Target)
        {
            State = SourceAttemptState.Ok, Media = media, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    internal void Adopt(ResolvedMusic media)
    {
        string key = AttemptKey(media.Target!, media.Target!.PolicyRequest);
        if (!Attempts.TryGetValue(key, out var previous)) { Remember(media); return; }
        Attempts[key] = new(key, media.Target) { State = SourceAttemptState.Ok, Media = media, At = previous.At };
    }

    internal async Task<ResolvedMusic?> ExactAsync(MusicQualityPolicy.Tier tier, CancellationToken token,
        ResolvedMusic? resolved = null, bool manualSource = false)
    {
        var media = await TierAsync(tier, collect: false, complete: true, token,
            exact: true, resolved: resolved, manualSource: manualSource);
        return media is null || media.Target!.ManualSource == manualSource ? media :
            media with { Target = media.Target with { ManualSource = manualSource } };
    }

    internal async Task<bool> ExpandAsync(CancellationToken token)
    {
        await RefreshSongAsync(token);
        var tier = MusicQualityPolicy.SourceTiers.FirstOrDefault(value => !exhausted.Contains(value.Category + ":" + string.Join(",", value.Levels)));
        if (tier is null) return false;
        await TierAsync(tier, collect: true, complete: true, token, exact: true, manualSource: true);
        return true;
    }

    private static bool SourcesEqual(MusicSongSourceRecord[] a, MusicSongSourceRecord[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].ContentEquals(b[i])) return false;
        return true;
    }

    internal bool CanExpand => Song.RepositoryPublication > expandedPublication && !Song.Record.ContentEquals(expanded) ||
        MusicQualityPolicy.SourceTiers.Any(value =>
            !exhausted.Contains(value.Category + ":" + string.Join(",", value.Levels)));

    internal void Exclude(string provider, string platform)
    {
        exclusions.Add(new(provider, platform));
        foreach (var attempt in Attempts.Values.Where(attempt => Excluded(attempt.Target)))
        { attempt.State = SourceAttemptState.Unavailable; attempt.Code = "SOURCE_EXCLUDED"; attempt.Media = null; }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposal is not null) return new(disposal);
            var waitTiers = tiersDrained.Task;
            disposal = DisposeCoreAsync(waitTiers);
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync(Task waitTiers)
    {
        await Task.Yield();
        await waitTiers.ConfigureAwait(false);
        await selection.WaitAsync().ConfigureAwait(false);
        selection.Release();
        selection.Dispose();
    }
}
