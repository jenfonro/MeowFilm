using System.Text.Json;
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
            var input = MusicWireCodec.ParseParameters(parameters);
            MusicWireCodec.RequireFields(input, "song quality qualityPolicySignature");
            bool manualSource = MusicWireCodec.OptionalBoolProp(input, "manualSource");
            var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(input.GetProperty("song")));
            string request = MusicWireCodec.Str(input, "quality");
            MusicRules.Require(request == "auto" || MusicQualityPolicy.OfficialTypes.Contains(request), "QUALITY_UNAVAILABLE");
            using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
            using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token, officialPolicy.Token);
            token = linked.Token;
            MusicRules.Require(MusicWireCodec.Str(input, "qualityPolicySignature") == policy.Signature, "QUALITY_POLICY_CHANGED");
            MusicRules.Require(manualSource && request != "auto" || (request == "auto" ? MusicQualityPolicy.HasEnabled(policy.Policy) :
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
                    MusicRules.Require(accounts.IsOfficialCurrent(owner), "AUTH_CHANGED");
            }
            song = await songMatcher.KnownAsync(song, token).ConfigureAwait(false);
            string songId = song.SongId;
            Current();
            var plan = new MusicOfficialCandidates(song);
            foreach (var (platform, owner) in owners)
            {
                if (owner is null) continue;
                var remembered = await sourceCache.ReadHistoryAsync(owner.Scope, songId, policy.Signature,
                    request, owner.Generation, token).ConfigureAwait(false);
                Current();
                if (remembered is not null)
                    plan.Remember(remembered, platform);
            }
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            return officialPolicy.Publish(null, () =>
            {
                Current();
                var snap = plan.Snapshot(owners.Where(pair => pair.Value is not null).Select(pair => pair.Key));
                return MusicWireCodec.Ok(w =>
                {
                    w.WritePropertyName("song");
                    MusicWireCodec.WriteSong(w, snap.Song);
                    w.WritePropertyName("candidates");
                    w.WriteStartArray();
                    foreach (var c in snap.Candidates)
                    {
                        var owner = owners[c.Platform]!;
                        w.WriteStartObject();
                        w.WriteString("platform", c.Platform);
                        w.WriteString("sourceKey", c.SourceKey);
                        w.WriteString("scope", owner.Scope);
                        w.WriteNumber("generation", owner.Generation);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteString("qualityPolicySignature", policy.Signature);
                    w.WriteBoolean("authorizationRequired", owners.Values.All(owner => owner is null));
                });
            });
        }
        catch (MusicLibraryException e) { return Failed(e.Code); }
        catch (MusicAccountException e) { return Failed(e.Code); }
        catch (MusicPlatformException e) { return Failed(e.Code); }
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return Failed("INVALID_RESPONSE"); }
    }
}
