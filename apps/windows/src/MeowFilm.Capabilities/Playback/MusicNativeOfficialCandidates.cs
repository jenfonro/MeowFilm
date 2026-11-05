using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    private async Task<JsonElement> ExecuteOfficialCandidatesAsync(object? parameters, CancellationToken token)
    {
        try
        {
            var input = JsonSerializer.SerializeToNode(parameters) as JsonObject ?? throw new MusicLibraryException("INVALID_REQUEST");
            LJ.Require(LJ.Bytes(input).Length <= 512 * 1024, "LIMIT");
            LJ.Fields(input, "song quality qualityPolicySignature discover page refresh rejectedSourceKeys", "song quality qualityPolicySignature discover");
            LJ.Require(LJ.IsBool(input["discover"]));
            LJ.Require(input["refresh"] is null || LJ.IsBool(input["refresh"]));
            bool refresh = LJ.B(input["refresh"]);
            var rejected = input["rejectedSourceKeys"] is null ? new HashSet<string>(StringComparer.Ordinal) :
                LJ.Array(input["rejectedSourceKeys"]).Select(value => LJ.Text(value)).ToHashSet(StringComparer.Ordinal);
            int pageNumber = input["page"] is null ? 1 : checked((int)LJ.I(input["page"]));
            LJ.Require(pageNumber is >= 1 and <= 3, "LIMIT");
            var song = NativeMusicSong.Normalize(input["song"]);
            var originalSong = song;
            string songId = LJ.S(song["songId"]), request = LJ.S(input["quality"]);
            LJ.Require(request == "auto" || MusicQualityPolicy.RequestTypes.Contains(request), "QUALITY_UNAVAILABLE");
            using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
            using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token, officialPolicy.Token);
            token = linked.Token;
            LJ.Require(LJ.S(input["qualityPolicySignature"]) == policy.Signature, "QUALITY_POLICY_CHANGED");
            LJ.Require(request == "auto" ? MusicQualityPolicy.HasEnabled(policy.Policy) :
                MusicQualityPolicy.Enabled(request, policy.Policy), "QUALITY_DISABLED");
            var owners = new Dictionary<string, MusicPlatformSession?>(StringComparer.Ordinal);
            // Settled restore is local; a file-first lookup must not invoke a platform endpoint.
            foreach (string platform in new[] { "wy", "tx", "kg" })
                if (officialPolicy.IsEnabled(platform))
                    owners[platform] = await accounts.AcquireOfficialAsync(platform,
                        request is "128k" or "auto" && MusicQualityPolicy.Enabled("128k", policy.Policy), token).ConfigureAwait(false);
            void Current()
            {
                token.ThrowIfCancellationRequested();
                officialPolicy.Check();
                foreach (var owner in owners.Values.OfType<MusicPlatformSession>())
                    LJ.Require(accounts.IsOfficialCurrent(owner), "AUTH_CHANGED");
            }
            // Local accepted mappings only, after authoritative policy checks; discovery below is the sole catalog path.
            song = await songMatcher.KnownAsync(song, token).ConfigureAwait(false);
            Current();
            var plan = new MusicOfficialCandidates(song);
            foreach (var (platform, owner) in owners)
            {
                if (owner is null) continue;
                var remembered = await sourceCache.ReadHistoryAsync(owner.Scope, songId, policy.Signature,
                    request, owner.Generation, token).ConfigureAwait(false);
                Current();
                if (remembered is not null && !songMatcher.IsExcluded(originalSong, LJ.S(remembered["sourceKey"])))
                    plan.Remember(remembered, platform);
            }
            var failures = new JsonArray();
            bool hasMore = false;
            if (LJ.B(input["discover"]) && NativeMusicSong.Fingerprint(song).Length > 0)
            {
                using var discovery = CancellationTokenSource.CreateLinkedTokenSource(token);
                var available = owners.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToArray();
                // Automatic playback uses the first page. The player source layer
                // can request up to three bounded pages, without mutating search history.
                var pages = await Task.WhenAll(available.Select(async platform =>
                {
                    try
                    {
                        var result = await songMatcher.MatchAsync(originalSong, platform, MatchingContext(platform, owners[platform]),
                            discovery.Token, refresh, pageNumber, rejected).ConfigureAwait(false);
                        return (Platform: platform, Match: result);
                    }
                    catch (MusicPlatformException e)
                    { return (Platform: platform, Match: new MusicSongMatcher.MatchResult(song, Error: e.Code)); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { return (Platform: platform, Match: new MusicSongMatcher.MatchResult(song, Error: "TIMEOUT")); }
                })).ConfigureAwait(false);
                Current();
                foreach (var page in pages)
                {
                    if (page.Match.Error is { } error)
                    {
                        failures.Add(LJ.Obj(("platform", page.Platform), ("code", error)));
                    }
                    plan.Discover(page.Match.Song, page.Platform);
                    hasMore |= page.Match.More;
                }
            }
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            return officialPolicy.Publish(null, () =>
            {
                Current();
                var response = plan.Snapshot(owners.Where(pair => pair.Value is not null).Select(pair => pair.Key));
                response["candidates"] = LJ.Arr(response["candidates"]!.AsArray().OfType<JsonObject>().Where(candidate =>
                    !rejected.Contains(LJ.S(candidate["sourceKey"])) &&
                    !songMatcher.IsExcluded(originalSong, LJ.S(candidate["sourceKey"]))));
                foreach (var candidate in response["candidates"]!.AsArray().OfType<JsonObject>())
                {
                    var owner = owners[LJ.S(candidate["platform"])]!;
                    candidate["scope"] = owner.Scope;
                    candidate["generation"] = owner.Generation;
                }
                response["ok"] = true;
                response["qualityPolicySignature"] = policy.Signature;
                response["failures"] = failures;
                response["hasMore"] = pageNumber < 3 && hasMore;
                response["authorizationRequired"] = owners.Values.All(owner => owner is null);
                response["page"] = pageNumber;
                return JsonSerializer.SerializeToElement(response);
            });
        }
        catch (MusicLibraryException e) { return Failed(e.Code); }
        catch (MusicAccountException e) { return Failed(e.Code); }
        catch (MusicPlatformException e) { return Failed(e.Code); }
        catch (MusicPlaybackCacheDataException) { return Failed("CACHE_DATA_INCOMPATIBLE"); }
        catch (MusicPlaybackCacheCommitException) { return Failed("CACHE_COMMIT_UNKNOWN"); }
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return Failed("INVALID_RESPONSE"); }
    }
}
