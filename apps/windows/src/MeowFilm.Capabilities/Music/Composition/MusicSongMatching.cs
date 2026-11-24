using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly Dictionary<string, string> matchingContexts = new(StringComparer.Ordinal);

    private string MatchingContext(string platform, MusicPlatformSession? session)
    {
        bool guest = session is null || accounts.IsOfficialGuest(session);
        var tuple = MusicNodeSongCodec.Build(w =>
        {
            w.WriteStartArray();
            w.WriteStringValue("recording-match-v1");
            w.WriteStringValue(platform);
            w.WriteStringValue(guest ? "public" : session!.Scope);
            w.WriteNumberValue(guest ? 0 : session!.Generation);
            w.WriteEndArray();
        });
        return NativeMusicSong.Hash(MusicRules.CanonicalElement(tuple));
    }

    private void ObserveSongMatches(MusicSearchSnapshot snapshot) =>
        songMatcher.ObserveSearch(snapshot, platform =>
        {
            lock (gate) return matchingContexts.GetValueOrDefault(platform) ?? MatchingContext(platform, null);
        });

    private void InvalidateSongMatches(string platform)
    {
        lock (gate) matchingContexts.Remove(platform);
        songMatcher.Invalidate(platform);
    }

    private Task PrepareSongFactsAsync(CancellationToken token) => songMatcher.InitializeAsync(token);

    /// <summary>
    /// Supplements missing recordings, or rechecks an old mapping after content failure.
    /// The matcher owns query scope, freshness and account-generation admission.
    /// </summary>
    public Task<MusicSongCompletionResult> CompleteSongAsync(
        MusicSongRecord songRecord,
        string[] platforms,
        string[]? contentFailed = null,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            var uniquePlatforms = platforms.Distinct(StringComparer.Ordinal).ToArray();
            MusicRules.Require(uniquePlatforms.Length is > 0 and <= 5 && uniquePlatforms.All(NativeMusicSong.Platforms.Contains));
            var failedSet = contentFailed is not null
                ? contentFailed.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            MusicRules.Require(failedSet.All(NativeMusicSong.Platforms.Contains));
            var origin = NativeMusicSong.Normalize(songRecord);
            var song = await songMatcher.KnownAsync(origin, token).ConfigureAwait(false);
            var known = song.Sources.Select(source => source.Platform).ToHashSet(StringComparer.Ordinal);
            var failures = new List<MusicSongCompletionFailure>();
            var targets = uniquePlatforms.Where(platform => !known.Contains(platform) || failedSet.Contains(platform)).ToArray();
            var results = await Task.WhenAll(targets.Select(async platform =>
            {
                // A catalog frame holds one platform's account lease. Parallel completion
                // needs one frame per platform, never one shared captured lease.
                using var frame = sessions.Begin();
                MusicPlatformSession? session = null;
                try
                {
                    session = await sessions.AcquireAsync(platform, token).ConfigureAwait(false);
                    var match = await songMatcher.MatchAsync(origin, platform,
                        MatchingContext(platform, session), token, contentFailed: failedSet.Contains(platform)).ConfigureAwait(false);
                    return (Platform: platform, Session: session, Match: match);
                }
                catch (MusicPlatformException e)
                { return (Platform: platform, Session: session, Match: new MusicSongMatcher.MatchResult(origin, Error: e.Code)); }
            })).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var (platform, session, match) in results)
            {
                if (session is not null) MusicRules.Require(accounts.IsCurrent(session), "AUTH_CHANGED");
                if (match.Error is { } error) failures.Add(new MusicSongCompletionFailure(platform, error));
            }
            // Every accepted match is merged into the matcher's entry for this recording.
            if (targets.Length > 0) song = await songMatcher.KnownAsync(origin, token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return new MusicSongCompletionResult(song, failures.ToArray());
        }, cancellationToken);

    private async Task SaveSongMatchesAsync(CancellationToken token)
    {
        try { await songMatcher.PersistAsync(token).ConfigureAwait(false); }
        // Optional derived persistence never changes an authoritative library receipt.
        catch (MusicLibraryException error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "matcher", "save_matches_skipped",
                new Dictionary<string, object?> { ["code"] = error.Code });
        }
        catch (StorageException error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "matcher", "save_matches_failed",
                new Dictionary<string, object?> { ["error"] = error.Error.ToString() });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
