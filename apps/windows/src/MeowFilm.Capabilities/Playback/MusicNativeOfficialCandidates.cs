using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    /// <summary>
    /// Built-in candidates of the song's accepted recordings for one request, history first.
    /// Local only: no catalog search. Missing platforms are completed by `music.songs.complete`.
    /// </summary>
    private async Task<JsonElement> ExecuteOfficialCandidatesAsync(object? parameters, CancellationToken token)
    {
        try
        {
            var input = JsonSerializer.SerializeToNode(parameters) as JsonObject ?? throw new MusicLibraryException("INVALID_REQUEST");
            LJ.Require(LJ.Bytes(input).Length <= 512 * 1024, "LIMIT");
            LJ.Fields(input, "song quality qualityPolicySignature manualSource", "song quality qualityPolicySignature");
            LJ.Require(input["manualSource"] is null || LJ.IsBool(input["manualSource"]), "INVALID_REQUEST");
            bool manualSource = LJ.B(input["manualSource"]);
            var song = NativeMusicSong.Normalize(input["song"]);
            var originalSong = song;
            string songId = LJ.S(song["songId"]), request = LJ.S(input["quality"]);
            LJ.Require(request == "auto" || MusicQualityPolicy.OfficialTypes.Contains(request), "QUALITY_UNAVAILABLE");
            using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
            using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token, officialPolicy.Token);
            token = linked.Token;
            LJ.Require(LJ.S(input["qualityPolicySignature"]) == policy.Signature, "QUALITY_POLICY_CHANGED");
            LJ.Require(manualSource && request != "auto" || (request == "auto" ? MusicQualityPolicy.HasEnabled(policy.Policy) :
                MusicQualityPolicy.Enabled(request, policy.Policy)), "QUALITY_DISABLED");
            var owners = new Dictionary<string, MusicPlatformSession?>(StringComparer.Ordinal);
            foreach (string platform in new[] { "wy", "tx", "kg" })
                if (officialPolicy.IsEnabled(platform))
                    owners[platform] = await accounts.AcquireOfficialAsync(platform,
                        request is "128k" or "auto" && (manualSource || MusicQualityPolicy.Enabled("128k", policy.Policy)), token).ConfigureAwait(false);
            void Current()
            {
                token.ThrowIfCancellationRequested();
                officialPolicy.Check();
                foreach (var owner in owners.Values.OfType<MusicPlatformSession>())
                    LJ.Require(accounts.IsOfficialCurrent(owner), "AUTH_CHANGED");
            }
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
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            return officialPolicy.Publish(null, () =>
            {
                Current();
                var response = plan.Snapshot(owners.Where(pair => pair.Value is not null).Select(pair => pair.Key));
                response["candidates"] = LJ.Arr(response["candidates"]!.AsArray().OfType<JsonObject>().Where(candidate =>
                    !songMatcher.IsExcluded(originalSong, LJ.S(candidate["sourceKey"]))));
                foreach (var candidate in response["candidates"]!.AsArray().OfType<JsonObject>())
                {
                    var owner = owners[LJ.S(candidate["platform"])]!;
                    candidate["scope"] = owner.Scope;
                    candidate["generation"] = owner.Generation;
                }
                response["ok"] = true;
                response["qualityPolicySignature"] = policy.Signature;
                response["authorizationRequired"] = owners.Values.All(owner => owner is null);
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
