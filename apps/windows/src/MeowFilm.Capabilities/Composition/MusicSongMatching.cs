using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly Dictionary<string, string> matchingContexts = new(StringComparer.Ordinal);
    private string MatchingContext(string platform, MusicPlatformSession? session) =>
        NativeMusicSong.Hash(LJ.Stringify(new JsonArray("recording-match-v1", platform,
            session is null || accounts.IsOfficialGuest(session) ? "public" : session.Scope,
            session is null || accounts.IsOfficialGuest(session) ? 0 : session.Generation)));

    private void ObserveSongMatches(JsonElement snapshot) =>
        songMatcher.ObserveSearch(snapshot, platform =>
        {
            lock (gate) return matchingContexts.GetValueOrDefault(platform) ?? MatchingContext(platform, null);
        });

    private void InvalidateSongMatches(string platform)
    {
        lock (gate) matchingContexts.Remove(platform);
        songMatcher.Invalidate(platform);
    }

    private async Task PrepareSongFactsAsync(CancellationToken token)
    {
        try { await songMatcher.InitializeAsync(token).ConfigureAwait(false); }
        // Library/catalog acquisition remains available when optional derived data is incompatible.
        // Matching itself still reports the current store's format error; there is no migration path.
        catch (MusicPlaybackCacheDataException) { }
    }

    /// <summary>
    /// Supplements missing recordings, or rechecks an old mapping after content failure.
    /// The matcher owns query scope, freshness and account-generation admission.
    /// </summary>
    private async Task<JsonElement> CompleteSongAsync(object? parameters, CancellationToken token)
    {
        try
        {
            var input = JsonSerializer.SerializeToNode(parameters) as JsonObject ?? throw new MusicLibraryException("INVALID_REQUEST");
            LJ.Require(LJ.Bytes(input).Length <= 512 * 1024, "LIMIT");
            LJ.Fields(input, "song platforms contentFailed", "song platforms");
            var platforms = LJ.Array(input["platforms"]).Select(value => LJ.Text(value)).Distinct(StringComparer.Ordinal).ToArray();
            LJ.Require(platforms.Length is > 0 and <= 5 && platforms.All(NativeMusicSong.Platforms.Contains));
            var contentFailed = input.ContainsKey("contentFailed")
                ? LJ.Array(input["contentFailed"]).Select(value => LJ.Text(value)).ToHashSet(StringComparer.Ordinal) : [];
            LJ.Require(contentFailed.All(NativeMusicSong.Platforms.Contains));
            var origin = NativeMusicSong.Normalize(input["song"]);
            var song = await songMatcher.KnownAsync(origin, token).ConfigureAwait(false);
            var known = LJ.Array(song["sources"]).OfType<JsonObject>().Select(source => LJ.S(source["source"]))
                .ToHashSet(StringComparer.Ordinal);
            var failures = new JsonArray();
            var targets = platforms.Where(platform => !known.Contains(platform) || contentFailed.Contains(platform)).ToArray();
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
                        MatchingContext(platform, session), token, contentFailed: contentFailed.Contains(platform)).ConfigureAwait(false);
                    return (Platform: platform, Session: session, Match: match);
                }
                catch (MusicPlatformException e)
                { return (Platform: platform, Session: session, Match: new MusicSongMatcher.MatchResult(origin, Error: e.Code)); }
            })).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var (platform, session, match) in results)
            {
                if (session is not null) LJ.Require(accounts.IsCurrent(session), "AUTH_CHANGED");
                if (match.Error is { } error) failures.Add(LJ.Obj(("platform", platform), ("code", error)));
            }
            // Every accepted match is merged into the matcher's entry for this recording.
            if (targets.Length > 0) song = await songMatcher.KnownAsync(origin, token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { ok = true, song, failures });
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicLibraryException e) { return Failed(e.Code); }
        catch (MusicAccountException e) { return Failed(e.Code); }
        catch (MusicPlatformException e) { return Failed(e.Code); }
        catch (MusicPlaybackCacheDataException) { return Failed("CACHE_DATA_INCOMPATIBLE"); }
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
    }

    private async Task SaveSongMatchesAsync(CancellationToken token)
    {
        try { await songMatcher.PersistAsync(token).ConfigureAwait(false); }
        // Optional derived data must never turn an authoritative library commit into
        // a failed receipt. Incompatible cache data is retained and playback reports it.
        catch (MusicPlaybackCacheDataException) { }
        catch (MusicPlaybackCacheCommitException) { }
        catch (MusicLibraryException) { }
        catch (DocumentStoreException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
