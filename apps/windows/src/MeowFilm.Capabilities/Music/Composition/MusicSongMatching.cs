using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private readonly Dictionary<string, string> matchingContexts = new(StringComparer.Ordinal);

    private string MatchingContext(string platform, MusicPlatformSession? session)
    {
        bool guest = session is null || accounts.IsOfficialGuest(session);
        var tuple = MusicWireCodec.Build(w =>
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

    private Task PrepareSongFactsAsync(CancellationToken token) => songMatcher.InitializeAsync(token);

    /// <summary>
    /// Supplements missing recordings, or rechecks an old mapping after content failure.
    /// The matcher owns query scope, freshness and account-generation admission.
    /// </summary>
    private async Task<JsonElement> CompleteSongAsync(object? parameters, CancellationToken token)
    {
        try
        {
            var input = MusicWireCodec.ParseParameters(parameters);
            MusicWireCodec.RequireFields(input, "song platforms");
            var platforms = MusicWireCodec.RequireIdArrayProp(input, "platforms").Distinct(StringComparer.Ordinal).ToArray();
            MusicRules.Require(platforms.Length is > 0 and <= 5 && platforms.All(NativeMusicSong.Platforms.Contains));
            var contentFailed = MusicWireCodec.HasNonNull(input, "contentFailed")
                ? MusicWireCodec.RequireIdArrayProp(input, "contentFailed").ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            MusicRules.Require(contentFailed.All(NativeMusicSong.Platforms.Contains));
            var origin = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(input.GetProperty("song")));
            var song = await songMatcher.KnownAsync(origin, token).ConfigureAwait(false);
            var known = song.Sources.Select(source => source.Platform).ToHashSet(StringComparer.Ordinal);
            var failures = new List<(string Platform, string Code)>();
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
                if (session is not null) MusicRules.Require(accounts.IsCurrent(session), "AUTH_CHANGED");
                if (match.Error is { } error) failures.Add((platform, error));
            }
            // Every accepted match is merged into the matcher's entry for this recording.
            if (targets.Length > 0) song = await songMatcher.KnownAsync(origin, token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("song");
                MusicWireCodec.WriteSong(w, song);
                w.WritePropertyName("failures");
                w.WriteStartArray();
                foreach (var (plat, code) in failures)
                {
                    w.WriteStartObject();
                    w.WriteString("platform", plat);
                    w.WriteString("code", code);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicLibraryException e) { return Failed(e.Code); }
        catch (MusicAccountException e) { return Failed(e.Code); }
        catch (MusicPlatformException e) { return Failed(e.Code); }
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
    }

    private async Task SaveSongMatchesAsync(CancellationToken token)
    {
        try { await songMatcher.PersistAsync(token).ConfigureAwait(false); }
        // Optional derived persistence never changes an authoritative library receipt.
        catch (MusicLibraryException) { }
        catch (StorageException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
