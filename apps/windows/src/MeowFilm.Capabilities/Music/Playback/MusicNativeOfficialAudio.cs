using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

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
            var input = MusicWireCodec.ParseParameters(parameters);
            string platform = MusicWireCodec.Str(input, "platform");
            MusicRules.Require(platform is "wy" or "tx" or "kg");
            JsonElement Result(string state, string? code) => MusicWireCodec.Ok(w =>
            {
                w.WriteString("platform", platform);
                w.WriteString("state", state);
                if (code is not null) w.WriteString("code", code); else w.WriteNull("code");
                w.WriteString("quality", "128k");
                w.WriteString("sample", MusicSourceCheckSamples.Identity(platform));
            });
            var session = await accounts.AcquireOfficialAsync(platform, true, token).ConfigureAwait(false);
            if (session is null) return Result("failed", "AUTH_REQUIRED");
            void Current()
            {
                token.ThrowIfCancellationRequested();
                MusicRules.Require(accounts.IsOfficialCurrent(session), "AUTH_CHANGED");
            }
            try
            {
                _ = await officialAudio.ResolveAsync(session, () => MusicSourceCheckSamples.Recording(platform), ["128k"],
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
            MusicRules.Require(method is "music.official.resolve" or "music.official.cacheContext" or
                "music.official.confirm", "CAPABILITY_UNAVAILABLE");
            var input = MusicWireCodec.ParseParameters(parameters);
            bool manualSource = MusicWireCodec.OptionalBoolProp(input, "manualSource");
            bool cacheOnly = MusicWireCodec.OptionalBoolProp(input, "cacheOnly");
            bool refresh = MusicWireCodec.OptionalBoolProp(input, "refresh");
            MusicRules.Require(!cacheOnly || !refresh, "INVALID_REQUEST");
            string platform = MusicWireCodec.Str(input, "platform");
            MusicRules.Require(platform is "wy" or "tx" or "kg");
            using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
            officialPolicy.Check(platform);
            using var officialRequest = CancellationTokenSource.CreateLinkedTokenSource(token, officialPolicy.Token);
            token = officialRequest.Token;
            string request = MusicWireCodec.Str(input, "quality");
            session = await accounts.AcquireOfficialAsync(platform, request is "128k" or "auto", token).ConfigureAwait(false);
            MusicRules.Require(session is not null, "AUTH_REQUIRED");
            void Current()
            {
                token.ThrowIfCancellationRequested();
                officialPolicy.Check(platform);
                MusicRules.Require(accounts.IsOfficialCurrent(session!), "AUTH_CHANGED");
                if (MusicWireCodec.HasNonNull(input, "scope"))
                    MusicRules.Require(MusicWireCodec.Str(input, "scope") == session!.Scope &&
                        MusicWireCodec.RequireIntProp(input, "generation") == session.Generation, "AUTH_CHANGED");
            }
            Current();
            using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token);
            token = linked.Token;
            if (MusicWireCodec.HasNonNull(input, "qualityPolicySignature"))
                MusicRules.Require(MusicWireCodec.Str(input, "qualityPolicySignature") == policy.Signature, "QUALITY_POLICY_CHANGED");
            var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(input.GetProperty("song")));
            string songId = song.SongId, sourceKey = MusicWireCodec.Str(input, "sourceKey");
            MusicSongSourceRecord CurrentRecording()
            {
                Current();
                if (library.SongSnapshot(songId) is { } current)
                    song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(current));
                var recordings = song.Sources
                    .Where(s => s.Platform == platform && (sourceKey.Length == 0 || s.SourceKey == sourceKey)).ToArray();
                MusicRules.Require(recordings.Length == 1, recordings.Length == 0 ? "RECORDING_UNAVAILABLE" : "RECORDING_REQUIRED");
                return recordings[0];
            }
            var recording = CurrentRecording();
            sourceKey = recording.SourceKey;
            MusicRules.Require(request == "auto" || MusicQualityPolicy.OfficialTypes.Contains(request), "QUALITY_UNAVAILABLE");
            string[] qualities = request == "auto"
                ? MusicQualityPolicy.OfficialTypes
                    .Where(q => MusicQualityPolicy.Enabled(q, policy.Policy))
                    .OrderBy(q => MusicQualityPolicy.Priority(q, policy.Policy))
                    .ThenBy(q => q == "320k" ? 0 : q == "192k" ? 1 : 2).ToArray()
                : manualSource || MusicQualityPolicy.Enabled(request, policy.Policy) ? [request] : [];
            MusicRules.Require(qualities.Length > 0, "QUALITY_DISABLED");
            string signature = MusicRules.CanonicalDigest(MusicWireCodec.BuildObject(w =>
            {
                w.WriteString("scope", session!.Scope);
                w.WriteString("songId", songId);
                w.WriteString("sourceKey", sourceKey);
                w.WriteString("request", request);
            }));
            if (method == "music.official.cacheContext")
            {
                // Local identity and current source/quality admission only.
                // Complete files do not require another platform authorization.
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                return officialPolicy.Publish(platform, () =>
                {
                    Current();
                    lock (gate) officialAccepted[platform] = session;
                    return MusicWireCodec.Ok(w =>
                    {
                        w.WriteString("platform", platform);
                        w.WriteString("sourceKey", sourceKey);
                        w.WriteString("songId", songId);
                        w.WriteString("scope", session.Scope);
                        w.WriteNumber("generation", session.Generation);
                        w.WriteString("qualityCacheSignature", signature);
                        w.WriteString("qualityPolicySignature", policy.Signature);
                    });
                });
            }
            if (method == "music.official.confirm")
            {
                MusicRules.Require(IsOfficialPlaybackCurrent(platform, session.Scope, session.Generation), "AUTH_CHANGED");
                var confirmed = await sourceCache.ReadAsync(session.Scope, songId, signature, token).ConfigureAwait(false);
                Current();
                // A complete file may outlive its signed URL. Started still confirms a
                // recording preference, not a renewed URL, media quality or offline permission.
                confirmed ??= new PlaybackSourceEntry(
                    signature, 0, 0, 0,
                    Ok: true, Kind: "official", Platform: platform, SourceKey: sourceKey, Scope: session.Scope,
                    Generation: session.Generation, Quality: request, ActualQuality: "",
                    QualityVerified: false, PlaybackVerified: false,
                    QualityPolicySignature: policy.Signature, QualityCacheSignature: signature,
                    Url: null, ExpiresAt: null, Duration: null, Size: null, Full: null,
                    Delivery: platform + "-official", RequestedQuality: request,
                    Recording: null);
                await sourceCache.ConfirmAsync(session.Scope, songId, signature, confirmed, CurrentRecording(), token).ConfigureAwait(false);
                return officialPolicy.Publish(platform, () =>
                {
                    Current();
                    return MusicWireCodec.Ok();
                });
            }
            // A cache-only probe is local on a miss.
            PlaybackSourceEntry? result = !refresh
                ? await sourceCache.ReadAsync(session.Scope, songId, signature, token).ConfigureAwait(false) : null;
            Current();
            if (result is not null && (result.Quality is null || !qualities.Contains(result.Quality))) result = null;
            MusicRules.Require(result is not null || !cacheOnly, "CACHE_MISS");
            if (result is null)
            {
                await sourceCache.InvalidateAsync(session.Scope, songId, token).ConfigureAwait(false);
                var media = await officialAudio.ResolveAsync(session, () => MusicWireCodec.MusicInfoToElement(CurrentRecording()),
                    qualities, token, Current).ConfigureAwait(false);
                Current();
                result = new PlaybackSourceEntry(
                    signature, 0, 0, 0,
                    Ok: true, Kind: "official", Platform: platform, SourceKey: sourceKey, Scope: session.Scope,
                    Generation: session.Generation, Quality: media.RequestQuality, ActualQuality: media.ActualQuality,
                    QualityVerified: media.QualityVerified, PlaybackVerified: false,
                    QualityPolicySignature: policy.Signature, QualityCacheSignature: signature,
                    Url: media.Uri.AbsoluteUri, ExpiresAt: media.ExpiresAt, Duration: media.Duration,
                    Size: media.Size, Full: media.Full, Delivery: platform + "-official",
                    RequestedQuality: request, Recording: null);
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                Current();
                await sourceCache.PutAsync(session.Scope, songId, signature, result, token).ConfigureAwait(false);
            }
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            Current();
            var published = result with { Generation = session.Generation, QualityPolicySignature = policy.Signature };
            return officialPolicy.Publish(platform, () =>
            {
                Current();
                lock (gate) officialAccepted[platform] = session;
                return MusicWireCodec.PlaybackSourceToElement(published, songId);
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
        catch (JsonException) { return Failed("INVALID_REQUEST"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return Failed("INVALID_RESPONSE"); }
    }
}
