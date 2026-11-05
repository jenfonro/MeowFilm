using System.Text.Json;
using System.Text.Json.Nodes;
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

    private async Task<JsonObject> MatchScriptSongAsync(JsonObject song, string platform, CancellationToken token)
    {
        var known = await songMatcher.KnownAsync(song, token).ConfigureAwait(false);
        if (LJ.Array(known["sources"]).OfType<JsonObject>().Any(source => LJ.S(source["source"]) == platform))
            return known;
        using var frame = sessions.Begin();
        var session = await sessions.AcquireAsync(platform, token).ConfigureAwait(false);
        string context = MatchingContext(platform, session);
        var result = await songMatcher.MatchAsync(song, platform, context, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (session is not null) LJ.Require(accounts.IsCurrent(session), "AUTH_CHANGED");
        if (result.Error is not null) throw new MusicLibraryException(result.Error);
        LJ.Require(LJ.Array(result.Song["sources"]).OfType<JsonObject>().Any(source =>
            LJ.S(source["source"]) == platform), "RECORDING_UNAVAILABLE");
        return result.Song;
    }

    private async Task SaveSongMatchesAsync(CancellationToken token)
    {
        try { await songMatcher.PersistAsync(token).ConfigureAwait(false); }
        // Optional derived data must never turn an authoritative library commit into
        // a failed receipt. Incompatible cache data is retained and playback reports it.
        catch (MusicPlaybackCacheDataException) { }
        catch (MusicPlaybackCacheCommitException) { }
        catch (MusicLibraryException) { }
        catch (ProtectedDocumentException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
