using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public interface IMusicOfficialPlaybackAuthority
{
    bool IsOfficialPlaybackCurrent(string platform, string scope, long generation);
}

public sealed partial class MusicNativeApplication
{
    private readonly OfficialMusicAudio officialAudio;
    private readonly MusicSourceCache sourceCache;
    private readonly Dictionary<string, MusicPlatformSession> officialAccepted = [];

    public bool IsOfficialPlaybackCurrent(string platform, string scope, long generation)
    {
        MusicPlatformSession? session;
        lock (gate)
        {
            if (stopping || closed) return false;
            officialAccepted.TryGetValue(platform, out session);
        }
        return session is not null && session.Scope == scope && session.Generation == generation &&
            library.IsOfficialSourceEnabled(platform) && accounts.IsOfficialCurrent(session);
    }

    /// <summary>Light check of one built-in provider: its fixed sample at standard quality.
    /// Checking a disabled provider is allowed and changes nothing.</summary>
    private async Task<JsonElement> CheckOfficialSourceAsync(object? parameters, CancellationToken token)
    {
        try
        {
            var input = JsonSerializer.SerializeToNode(parameters) as JsonObject ?? throw new MusicLibraryException("INVALID_REQUEST");
            LJ.Fields(input, "platform", "platform");
            string platform = LJ.S(input["platform"]);
            LJ.Require(platform is "wy" or "tx" or "kg");
            JsonElement Result(string state, string? code) => JsonSerializer.SerializeToElement(new { ok = true, platform,
                state, code, quality = "128k", sample = MusicSourceCheckSamples.Identity(platform) });
            var session = await accounts.AcquireOfficialAsync(platform, true, token).ConfigureAwait(false);
            if (session is null) return Result("failed", "AUTH_REQUIRED");
            void Current()
            {
                token.ThrowIfCancellationRequested();
                LJ.Require(accounts.IsOfficialCurrent(session), "AUTH_CHANGED");
            }
            try
            {
                _ = await officialAudio.ResolveAsync(session, MusicSourceCheckSamples.Recording(platform), ["128k"],
                    token, Current).ConfigureAwait(false);
                return Result("ok", null);
            }
            catch (MusicPlatformException e) { return Result("failed", e.Code); }
            catch (MusicAccountException e) { return Result("failed", e.Code); }
            catch (MusicLibraryException e) when (e.Code != "CANCELLED") { return Result("failed", e.Code); }
            // Any other failure of the sample request is this check's result, not a crash of the check.
            catch (Exception e) when (e is not OperationCanceledException) { return Result("failed", "UPSTREAM_ERROR"); }
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicLibraryException e) { return Failed(e.Code); }
        catch (MusicAccountException e) { return Failed(e.Code); }
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
    }

    private async Task<JsonElement> ExecuteOfficialAudioAsync(string method, object? parameters, CancellationToken token)
    {
        MusicPlatformSession? session = null;
        try
        {
            LJ.Require(method is "music.official.resolve" or "music.official.cacheContext" or
                "music.official.authorize" or "music.official.confirm", "CAPABILITY_UNAVAILABLE");
            var input = JsonSerializer.SerializeToNode(parameters) as JsonObject ?? throw new MusicLibraryException("INVALID_REQUEST");
            LJ.Require(LJ.Bytes(input).Length <= 512 * 1024, "LIMIT");
            LJ.Fields(input, "platform song originSong sourceKey quality scope generation qualityPolicySignature refresh cacheOnly manualSource",
                "platform song quality");
            LJ.Require(input["manualSource"] is null || LJ.IsBool(input["manualSource"]), "INVALID_REQUEST");
            bool manualSource = LJ.B(input["manualSource"]);
            LJ.Require(input["cacheOnly"] is null || LJ.IsBool(input["cacheOnly"]), "INVALID_REQUEST");
            LJ.Require(!LJ.B(input["cacheOnly"]) || !LJ.B(input["refresh"]), "INVALID_REQUEST");
            string platform = LJ.S(input["platform"]);
            LJ.Require(platform is "wy" or "tx" or "kg");
            using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
            officialPolicy.Check(platform);
            using var officialRequest = CancellationTokenSource.CreateLinkedTokenSource(token, officialPolicy.Token);
            token = officialRequest.Token;
            string request = LJ.S(input["quality"]);
            session = await accounts.AcquireOfficialAsync(platform, request is "128k" or "auto", token).ConfigureAwait(false);
            LJ.Require(session is not null, "AUTH_REQUIRED");
            void Current()
            {
                token.ThrowIfCancellationRequested();
                officialPolicy.Check(platform);
                LJ.Require(accounts.IsOfficialCurrent(session!), "AUTH_CHANGED");
                if (input["scope"] is not null) LJ.Require(LJ.S(input["scope"]) == session!.Scope &&
                    LJ.I(input["generation"]) == session.Generation, "AUTH_CHANGED");
            }
            Current();
            using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token);
            token = linked.Token;
            if (input["qualityPolicySignature"] is not null)
                LJ.Require(LJ.S(input["qualityPolicySignature"]) == policy.Signature, "QUALITY_POLICY_CHANGED");
            var song = NativeMusicSong.Normalize(input["song"]);
            var originSong = input["originSong"] is null ? song : NativeMusicSong.Normalize(input["originSong"]);
            LJ.Require(LJ.S(originSong["songId"]) == LJ.S(song["songId"]) &&
                new NativeMusicSong.Recordings(originSong).CanMerge(new(song)), "RECORDING_MISMATCH");
            string songId = LJ.S(song["songId"]), sourceKey = LJ.S(input["sourceKey"]);
            var recordings = (song["sources"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(s => LJ.S(s["source"]) == platform && (sourceKey.Length == 0 || LJ.S(s["sourceKey"]) == sourceKey)).ToArray();
            LJ.Require(recordings.Length == 1, recordings.Length == 0 ? "RECORDING_UNAVAILABLE" : "RECORDING_REQUIRED");
            var recording = recordings[0];
            sourceKey = LJ.S(recording["sourceKey"]);
            LJ.Require(!songMatcher.IsExcluded(originSong, sourceKey), "RECORDING_MISMATCH");
            LJ.Require(request == "auto" || MusicQualityPolicy.OfficialTypes.Contains(request), "QUALITY_UNAVAILABLE");
            string[] qualities = request == "auto"
                ? MusicQualityPolicy.RequestTypes.Concat(new[] { "jyeffect", "dolby", "sky", "vivid" })
                    .Where(q => MusicQualityPolicy.Enabled(q, policy.Policy))
                    .OrderBy(q => MusicQualityPolicy.Priority(q, policy.Policy))
                    .ThenBy(q => q == "320k" ? 0 : q == "192k" ? 1 : 2).ToArray()
                : manualSource || MusicQualityPolicy.Enabled(request, policy.Policy) ? [request] : [];
            LJ.Require(qualities.Length > 0, "QUALITY_DISABLED");
            string signature = LJ.Digest(LJ.Obj(("scope", session!.Scope), ("generation", session.Generation),
                ("songId", songId), ("sourceKey", sourceKey), ("policy", policy.Signature), ("request", request)));
            if (method == "music.official.cacheContext")
            {
                // Identity and policy only. This does not grant a new media URL or a new license:
                // the cache consumer must also require its protected, full-file offline receipt.
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                return officialPolicy.Publish(platform, () =>
                {
                    Current();
                    lock (gate) officialAccepted[platform] = session;
                    return JsonSerializer.SerializeToElement(new { ok = true, platform, sourceKey, songId,
                        scope = session.Scope, generation = session.Generation, qualityCacheSignature = signature,
                        qualityPolicySignature = policy.Signature });
                });
            }
            if (method == "music.official.confirm")
            {
                LJ.Require(IsOfficialPlaybackCurrent(platform, session.Scope, session.Generation), "AUTH_CHANGED");
                var confirmed = await sourceCache.ReadAsync(session.Scope, songId, signature, token).ConfigureAwait(false);
                Current();
                // A licensed complete file may outlive its signed URL. Started still confirms a
                // recording preference, not a renewed URL, media quality or offline permission.
                confirmed ??= LJ.Obj(("platform", platform), ("sourceKey", sourceKey), ("quality", request),
                    ("requestedQuality", request), ("qualityPolicySignature", policy.Signature),
                    ("generation", session.Generation), ("qualityVerified", false));
                if (confirmed is not null && (confirmed["licenseExpiresAt"] is null ||
                    LJ.I(confirmed["licenseExpiresAt"]) > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                {
                    confirmed["recording"] = recording.DeepClone();
                    await sourceCache.ConfirmAsync(session.Scope, songId, signature, confirmed, token).ConfigureAwait(false);
                }
                return officialPolicy.Publish(platform, () =>
                {
                    Current();
                    return JsonSerializer.SerializeToElement(new { ok = true });
                });
            }
            // A cache-only probe is local on a miss. A hit still passes the current rights check below.
            JsonObject? result = method == "music.official.resolve" && !LJ.B(input["refresh"]) ?
                await sourceCache.ReadAsync(session.Scope, songId, signature, token).ConfigureAwait(false) : null;
            Current();
            if (result is not null && !qualities.Contains(LJ.S(result["quality"]))) result = null;
            LJ.Require(result is not null || !LJ.B(input["cacheOnly"]), "CACHE_MISS");
            // Standard WY access is decided by the official media response, not
            // by local membership. It never creates an offline license or login.
            var rights = new MusicAccountEntitlements(new(false, "none", null), 0, 0, []);
            if (!(platform == "wy" && (request == "128k" || accounts.IsOfficialGuest(session))))
            {
                await using var authority = new MusicAccountProtocol(http, clients);
                var observed = await authority.ReadAccountDetailsAsync(session, token).ConfigureAwait(false);
                Current();
                rights = observed.Entitlements;
                LJ.Require(observed.Profile.UserId == session.UserId, "IDENTITY_MISMATCH");
            }
            // Membership never gates a request: the platform's real response decides what plays.
            // It only bounds an offline license for a complete file.
            bool member = !rights.VerificationDeferred && rights.Membership.Vip == true &&
                (rights.Membership.ExpiresAt is null || rights.Membership.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            long? licenseExpiresAt = member ? rights.Membership.ExpiresAt : null;
            if (method == "music.official.authorize")
            {
                // A guest/non-member cannot authorize a completed file merely by
                // existing locally. Recheck the exact recording with the platform.
                if (!member)
                    _ = await officialAudio.ResolveAsync(session, NativeMusicSong.SourceData(recording),
                        qualities, token, Current).ConfigureAwait(false);
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                return officialPolicy.Publish(platform, () =>
                {
                    Current();
                    lock (gate) officialAccepted[platform] = session;
                    return JsonSerializer.SerializeToElement(new { ok = true, platform, sourceKey, songId,
                        scope = session.Scope, generation = session.Generation, qualityCacheSignature = signature,
                        qualityPolicySignature = policy.Signature, licenseExpiresAt });
                });
            }
            LJ.Require(method == "music.official.resolve", "CAPABILITY_UNAVAILABLE");
            Current();
            if (result is not null && !qualities.Contains(LJ.S(result["quality"]))) result = null;
            LJ.Require(result is not null || !LJ.B(input["cacheOnly"]), "CACHE_MISS");
            if (result is null)
            {
                await sourceCache.InvalidateAsync(session.Scope, songId, token).ConfigureAwait(false);
                var media = await officialAudio.ResolveAsync(session, NativeMusicSong.SourceData(recording),
                    qualities, token, Current).ConfigureAwait(false);
                Current();
                double expected = LJ.N(song["duration"]);
                if (expected > 0 && media.Duration > 0 &&
                    Math.Abs(expected - media.Duration) > 10)
                {
                    await songMatcher.ExcludeAsync(originSong, sourceKey, token).ConfigureAwait(false);
                    await sourceCache.RejectRecordingAsync(session.Scope, songId, sourceKey, token).ConfigureAwait(false);
                    throw new MusicLibraryException("RECORDING_MISMATCH");
                }
                result = LJ.Obj(("ok", true), ("kind", "official"), ("platform", platform), ("songId", songId),
                    ("sourceKey", sourceKey), ("scope", session.Scope), ("generation", session.Generation),
                    ("quality", media.RequestQuality), ("requestedQuality", request), ("actualQuality", media.ActualQuality),
                    ("qualityVerified", media.QualityVerified), ("playbackVerified", false),
                    ("qualityPolicySignature", policy.Signature), ("qualityCacheSignature", signature),
                    ("url", media.Uri.AbsoluteUri), ("headers", new JsonObject()), ("expiresAt", media.ExpiresAt),
                    ("duration", media.Duration), ("size", media.Size), ("full", media.Full),
                    ("licenseExpiresAt", licenseExpiresAt), ("delivery", platform + "-official"),
                    ("offlineAllowed", member && media.Full && licenseExpiresAt is not null),
                    ("authorizationExpiresAt", licenseExpiresAt));
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                Current();
                await sourceCache.PutAsync(session.Scope, songId, signature, result, token).ConfigureAwait(false);
            }
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            Current();
            // Current authorization can shorten but never extend an already-issued license.
            if (licenseExpiresAt is { } license)
            {
                LJ.Require(license > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "VIP_REQUIRED");
                result["licenseExpiresAt"] = Math.Min(license, result["licenseExpiresAt"]?.GetValue<long>() ?? license);
                result["authorizationExpiresAt"] = result["licenseExpiresAt"]!.DeepClone();
            }
            if (!member)
            {
                result["licenseExpiresAt"] = null;
                result["authorizationExpiresAt"] = null;
                result["offlineAllowed"] = false;
            }
            return officialPolicy.Publish(platform, () =>
            {
                Current();
                lock (gate) officialAccepted[platform] = session;
                return JsonSerializer.SerializeToElement(result);
            });
        }
        catch (MusicLibraryException e) { return Failed(e.Code); }
        catch (MusicAccountException e)
        {
            if (session is not null && e.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED") accounts.Revoke(session);
            return Failed(e.Code);
        }
        catch (MusicPlatformException e)
        {
            if (session is not null && e.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED") accounts.Revoke(session);
            return Failed(e.Code);
        }
        catch (MusicPlaybackCacheCommitException) { return Failed("COMMIT_UNKNOWN"); }
        catch (MusicPlaybackCacheDataException) { return Failed("CACHE_DATA_INCOMPATIBLE"); }
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return Failed("INVALID_RESPONSE"); }
    }
}
