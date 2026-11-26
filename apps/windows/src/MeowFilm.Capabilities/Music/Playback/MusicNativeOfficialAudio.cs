using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public sealed record MusicResolvedOfficialAudio(
    string SongId,
    string Platform,
    string SourceKey,
    string Scope,
    long Generation,
    string Quality,
    string RequestedQuality,
    string ActualQuality,
    bool QualityVerified,
    bool PlaybackVerified,
    string QualityPolicySignature,
    string QualityCacheSignature,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    long? ExpiresAt,
    double? Duration,
    long? Size,
    bool? Full,
    string? Delivery)
{
    internal AudioFileInfoRecord ToAudioFileInfoRecord() => new(
        Kind: "official",
        Platform: Platform,
        SongId: SongId,
        SourceKey: SourceKey,
        Scope: Scope,
        Generation: Generation,
        InstallationId: null,
        ProviderName: null,
        Quality: Quality,
        RequestedQuality: RequestedQuality,
        ActualQuality: ActualQuality,
        QualityVerified: QualityVerified,
        PlaybackVerified: PlaybackVerified,
        QualityPolicySignature: QualityPolicySignature,
        QualityCacheSignature: QualityCacheSignature,
        Duration: Duration,
        Size: Size,
        Full: Full,
        Delivery: Delivery,
        QualityEvidence: null,
        Codec: null,
        SampleRate: null,
        BitDepth: null,
        Channels: null,
        Bitrate: null);
}

public sealed partial class MusicNativeApplication
{
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

    public Task<MusicOfficialCandidatesResult> GetOfficialCandidatesAsync(
        MusicSongRecord songRecord,
        string quality,
        string qualityPolicySignature,
        bool manualSource = false,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            try
            {
                MusicRules.Require(quality == "auto" || MusicQualityPolicy.OfficialTypes.Contains(quality), "QUALITY_UNAVAILABLE");
                using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
                using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token, officialPolicy.Token);
                token = linked.Token;
                MusicRules.Require(qualityPolicySignature == policy.Signature, "QUALITY_POLICY_CHANGED");
                MusicRules.Require(manualSource && quality != "auto" || (quality == "auto" ? MusicQualityPolicy.HasEnabled(policy.Policy) :
                    MusicQualityPolicy.Enabled(quality, policy.Policy)), "QUALITY_DISABLED");
                var owners = new Dictionary<string, MusicPlatformSession?>(StringComparer.Ordinal);
                foreach (string platform in new[] { "wy", "tx", "kg" })
                    if (officialPolicy.IsEnabled(platform) &&
                        (quality == "auto"
                            ? MusicQualityPolicy.PlatformOfficialTypes(platform).Any(q => MusicQualityPolicy.Enabled(q, policy.Policy))
                            : MusicQualityPolicy.SupportsOfficialQuality(platform, quality)))
                        owners[platform] = await accounts.AcquireOfficialAsync(platform,
                            quality is "128k" or "auto" && (manualSource || MusicQualityPolicy.Enabled("128k", policy.Policy)), token).ConfigureAwait(false);
                void Current()
                {
                    token.ThrowIfCancellationRequested();
                    officialPolicy.Check();
                    foreach (var owner in owners.Values.OfType<MusicPlatformSession>())
                        MusicRules.Require(accounts.IsOfficialCurrent(owner), "AUTH_CHANGED");
                }
                var song = await songMatcher.KnownAsync(songRecord, token).ConfigureAwait(false);
                string songId = song.SongId, primary = NativeMusicSong.SourceKey(song);
                Current();
                var recordings = new NativeMusicSong.Recordings(song);
                var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
                var rememberedAt = new Dictionary<string, long>(StringComparer.Ordinal);
                void Select(MusicSongSourceRecord row)
                {
                    if (row.SourceKey.Length > 0 && row.Platform is "wy" or "tx" or "kg" && recordings.TryAddSource(row))
                        candidateKeys.Add(row.SourceKey);
                }
                foreach (var row in recordings.Sources) Select(row);
                foreach (var (platform, owner) in owners)
                {
                    if (owner is null) continue;
                    var remembered = await sourceCache.ReadHistoryAsync(owner.Scope, songId, policy.Signature,
                        quality, owner.Generation, token).ConfigureAwait(false);
                    Current();
                    if (remembered is { Recording: { } rec } && remembered.Platform == platform)
                    {
                        var row = MusicSourceCache.ToSongSourceRecord(rec);
                        if (row.Platform == platform && row.SourceKey == remembered.SourceKey)
                        {
                            Select(row);
                            if (candidateKeys.Contains(row.SourceKey)) rememberedAt[row.SourceKey] = remembered.At;
                        }
                    }
                }
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                return officialPolicy.Publish(null, () =>
                {
                    Current();
                    var candidates = recordings.Sources
                        .Where(row => candidateKeys.Contains(row.SourceKey) && owners.GetValueOrDefault(row.Platform) is not null)
                        .OrderByDescending(row => rememberedAt.GetValueOrDefault(row.SourceKey))
                        .ThenBy(row => row.SourceKey == primary ? 0 : 1)
                        .ThenBy(row => row.SourceKey, StringComparer.Ordinal)
                        .Select(row =>
                        {
                            var owner = owners[row.Platform]!;
                            return new MusicOfficialCandidateItem(row.Platform, row.SourceKey, owner.Scope, owner.Generation);
                        })
                        .ToArray();
                    return new MusicOfficialCandidatesResult(
                        recordings.ToSong(), candidates, policy.Signature, owners.Values.All(owner => owner is null));
                });
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
            { throw new MusicOperationException("INVALID_RESPONSE"); }
        }, cancellationToken);

    /// <summary>Light check of one built-in provider: its fixed sample at standard quality.
    /// Checking a disabled provider is allowed and changes nothing.</summary>
    public Task<MusicOfficialCheckResult> CheckOfficialSourceAsync(string platform, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(platform is "wy" or "tx" or "kg");
            string sampleIdentity = platform switch
            {
                "wy" => "wy:347230",
                "tx" => "tx:0039MnYb0qxYhV",
                _ => "kg:30ece05c17d4e9ec8ba0ef9f96e7a07c"
            };
            MusicOfficialCheckResult Result(string state, string? code) =>
                new(platform, state, code, "128k", sampleIdentity);
            var session = await accounts.AcquireOfficialAsync(platform, true, token).ConfigureAwait(false);
            if (session is null) return Result("failed", "AUTH_REQUIRED");
            void Current()
            {
                token.ThrowIfCancellationRequested();
                MusicRules.Require(accounts.IsOfficialCurrent(session), "AUTH_CHANGED");
            }
            try
            {
                _ = await RequestOfficialAudioAsync(session, () => platform switch
                {
                    "wy" => MusicNodeSongCodec.BuildObject(w => { w.WriteString("songmid", "347230"); w.WriteString("name", "海阔天空"); w.WriteString("singer", "Beyond"); }),
                    "tx" => MusicNodeSongCodec.BuildObject(w => { w.WriteString("songmid", "0039MnYb0qxYhV"); w.WriteString("name", "晴天"); w.WriteString("singer", "周杰伦"); }),
                    _ => MusicNodeSongCodec.BuildObject(w => { w.WriteString("hash", "30ece05c17d4e9ec8ba0ef9f96e7a07c"); w.WriteString("name", "晴天"); w.WriteString("singer", "周杰伦"); })
                }, ["128k"], token, Current).ConfigureAwait(false);
                return Result("ok", null);
            }
            catch (MusicPlatformException e) { return Result("failed", e.Code); }
            catch (MusicAccountException e) { return Result("failed", e.Code); }
            catch (MusicLibraryException e) when (e.Code != "CANCELLED") { return Result("failed", e.Code); }
            catch (Exception e) when (e is not OperationCanceledException) { return Result("failed", "UPSTREAM_ERROR"); }
        }, cancellationToken);

    public Task<MusicOfficialCacheContextResult> GetOfficialCacheContextAsync(
        string platform,
        MusicSongRecord songRecord,
        string quality,
        string? sourceKey = null,
        string? scope = null,
        long? generation = null,
        string? qualityPolicySignature = null,
        bool manualSource = false,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => ExecuteOfficialAudioAsync(
            platform, songRecord, quality, sourceKey, scope, generation,
            qualityPolicySignature, manualSource, token,
            async ctx =>
            {
                // Local identity and current source/quality admission only.
                // Complete files do not require another platform authorization.
                await library.CheckQualityAsync(ctx.QualityPolicy, ctx.Token).ConfigureAwait(false);
                return ctx.OfficialPolicy.Publish(platform, () =>
                {
                    ctx.CheckCurrent();
                    lock (gate) officialAccepted[platform] = ctx.Session;
                    return new MusicOfficialCacheContextResult(
                        platform, ctx.SourceKey, ctx.SongId, ctx.Session.Scope, ctx.Session.Generation,
                        ctx.Signature, ctx.QualityPolicy.Signature);
                });
            }), cancellationToken);

    public Task ConfirmOfficialPlaybackAsync(
        string platform,
        MusicSongRecord songRecord,
        string quality,
        string? sourceKey = null,
        string? scope = null,
        long? generation = null,
        string? qualityPolicySignature = null,
        bool manualSource = false,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token => ExecuteOfficialAudioAsync(
            platform, songRecord, quality, sourceKey, scope, generation,
            qualityPolicySignature, manualSource, token,
            async ctx =>
            {
                MusicRules.Require(IsOfficialPlaybackCurrent(platform, ctx.Session.Scope, ctx.Session.Generation), "AUTH_CHANGED");
                var confirmed = await sourceCache.ReadAsync(ctx.Session.Scope, ctx.SongId, ctx.Signature, ctx.Token).ConfigureAwait(false);
                ctx.CheckCurrent();
                confirmed ??= new PlaybackSourceEntry(
                    ctx.Signature, 0, 0, 0,
                    Ok: true, Kind: "official", Platform: platform, SourceKey: ctx.SourceKey, Scope: ctx.Session.Scope,
                    Generation: ctx.Session.Generation, Quality: quality, ActualQuality: "",
                    QualityVerified: false, PlaybackVerified: false,
                    QualityPolicySignature: ctx.QualityPolicy.Signature, QualityCacheSignature: ctx.Signature,
                    Url: null, ExpiresAt: null, Duration: null, Size: null, Full: null,
                    Delivery: platform + "-official", RequestedQuality: quality,
                    Recording: null);
                await sourceCache.ConfirmAsync(ctx.Session.Scope, ctx.SongId, ctx.Signature, confirmed, ctx.CurrentRecording(), ctx.Token).ConfigureAwait(false);
                return ctx.OfficialPolicy.Publish(platform, () =>
                {
                    ctx.CheckCurrent();
                    return true;
                });
            }), cancellationToken);

    public Task<MusicResolvedOfficialAudio> ResolveOfficialAudioAsync(
        string platform,
        MusicSongRecord songRecord,
        string quality,
        string? sourceKey = null,
        string? scope = null,
        long? generation = null,
        string? qualityPolicySignature = null,
        bool manualSource = false,
        bool cacheOnly = false,
        bool refresh = false,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(token =>
        {
            MusicRules.Require(!cacheOnly || !refresh, "INVALID_REQUEST");
            return ExecuteOfficialAudioAsync(
                platform, songRecord, quality, sourceKey, scope, generation,
                qualityPolicySignature, manualSource, token,
                async ctx =>
                {
                    // A cache-only probe is local on a miss.
                    PlaybackSourceEntry? result = !refresh
                        ? await sourceCache.ReadAsync(ctx.Session.Scope, ctx.SongId, ctx.Signature, ctx.Token).ConfigureAwait(false) : null;
                    ctx.CheckCurrent();
                    if (result is not null && (result.Quality is null || !ctx.Qualities.Contains(result.Quality))) result = null;
                    MusicRules.Require(result is not null || !cacheOnly, "CACHE_MISS");
                    if (result is null)
                    {
                        await sourceCache.InvalidateAsync(ctx.Session.Scope, ctx.SongId, ctx.Token).ConfigureAwait(false);
                        var media = await RequestOfficialAudioAsync(ctx.Session, () => MusicNodeSongCodec.MusicInfoToElement(ctx.CurrentRecording()),
                            ctx.Qualities, ctx.Token, ctx.CheckCurrent).ConfigureAwait(false);
                        ctx.CheckCurrent();
                        result = new PlaybackSourceEntry(
                            ctx.Signature, 0, 0, 0,
                            Ok: true, Kind: "official", Platform: platform, SourceKey: ctx.SourceKey, Scope: ctx.Session.Scope,
                            Generation: ctx.Session.Generation, Quality: media.RequestQuality, ActualQuality: media.ActualQuality,
                            QualityVerified: media.QualityVerified, PlaybackVerified: false,
                            QualityPolicySignature: ctx.QualityPolicy.Signature, QualityCacheSignature: ctx.Signature,
                            Url: media.Uri.AbsoluteUri, ExpiresAt: media.ExpiresAt, Duration: media.Duration,
                            Size: media.Size, Full: media.Full, Delivery: platform + "-official",
                            RequestedQuality: quality, Recording: null);
                        await library.CheckQualityAsync(ctx.QualityPolicy, ctx.Token).ConfigureAwait(false);
                        ctx.CheckCurrent();
                        await sourceCache.PutAsync(ctx.Session.Scope, ctx.SongId, ctx.Signature, result, ctx.Token).ConfigureAwait(false);
                    }
                    await library.CheckQualityAsync(ctx.QualityPolicy, ctx.Token).ConfigureAwait(false);
                    ctx.CheckCurrent();
                    var published = result with { Generation = ctx.Session.Generation, QualityPolicySignature = ctx.QualityPolicy.Signature };
                    Uri? uri = null;
                    string? resolvedQuality = null;
                    MusicRules.Require(
                        published.Url is { Length: > 0 } rawUrl &&
                        Uri.TryCreate(rawUrl, UriKind.Absolute, out uri) &&
                        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
                        (resolvedQuality = published.Quality) is { Length: > 0 } &&
                        (published.ExpiresAt is null || published.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        "INVALID_RESPONSE");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return ctx.OfficialPolicy.Publish(platform, () =>
                    {
                        ctx.CheckCurrent();
                        lock (gate) officialAccepted[platform] = ctx.Session;
                        return new MusicResolvedOfficialAudio(
                            SongId: ctx.SongId,
                            Platform: published.Platform ?? platform,
                            SourceKey: published.SourceKey ?? ctx.SourceKey,
                            Scope: published.Scope ?? ctx.Session.Scope,
                            Generation: ctx.Session.Generation,
                            Quality: resolvedQuality!,
                            RequestedQuality: published.RequestedQuality ?? quality,
                            ActualQuality: published.ActualQuality ?? "",
                            QualityVerified: published.QualityVerified == true,
                            PlaybackVerified: published.PlaybackVerified == true,
                            QualityPolicySignature: ctx.QualityPolicy.Signature,
                            QualityCacheSignature: published.QualityCacheSignature ?? ctx.Signature,
                            Uri: uri!,
                            Headers: headers,
                            ExpiresAt: published.ExpiresAt,
                            Duration: published.Duration,
                            Size: published.Size,
                            Full: published.Full,
                            Delivery: published.Delivery);
                    });
                });
        }, cancellationToken);

    private sealed record OfficialAudioContext(
        MusicPlatformSession Session,
        MusicLibraryOwner.OfficialSourcesLease OfficialPolicy,
        MusicLibraryOwner.QualityLease QualityPolicy,
        CancellationToken Token,
        string SongId,
        string SourceKey,
        string[] Qualities,
        string Signature,
        Action CheckCurrent,
        Func<MusicSongSourceRecord> CurrentRecording);

    private async Task<T> ExecuteOfficialAudioAsync<T>(
        string platform,
        MusicSongRecord songRecord,
        string quality,
        string? sourceKey,
        string? scope,
        long? generation,
        string? qualityPolicySignature,
        bool manualSource,
        CancellationToken token,
        Func<OfficialAudioContext, Task<T>> action)
    {
        MusicPlatformSession? session = null;
        try
        {
            MusicRules.Require(platform is "wy" or "tx" or "kg");
            using var officialPolicy = await library.AcquireOfficialSourcesAsync(token).ConfigureAwait(false);
            officialPolicy.Check(platform);
            using var officialRequest = CancellationTokenSource.CreateLinkedTokenSource(token, officialPolicy.Token);
            token = officialRequest.Token;
            session = await accounts.AcquireOfficialAsync(platform, quality is "128k" or "auto", token).ConfigureAwait(false);
            MusicRules.Require(session is not null, "AUTH_REQUIRED");
            void Current()
            {
                token.ThrowIfCancellationRequested();
                officialPolicy.Check(platform);
                MusicRules.Require(accounts.IsOfficialCurrent(session!), "AUTH_CHANGED");
                if (scope is not null)
                    MusicRules.Require(scope == session!.Scope && generation == session.Generation, "AUTH_CHANGED");
            }
            Current();
            using var policy = await library.AcquireQualityAsync(token).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, policy.Token);
            token = linked.Token;
            if (qualityPolicySignature is not null)
                MusicRules.Require(qualityPolicySignature == policy.Signature, "QUALITY_POLICY_CHANGED");
            var song = songRecord;
            string songId = song.SongId, selectedSourceKey = sourceKey ?? "";
            MusicSongSourceRecord CurrentRecording()
            {
                Current();
                if (library.ReadSongRecord(songId) is { } current)
                    song = current;
                var recordings = song.Sources
                    .Where(s => s.Platform == platform && (selectedSourceKey.Length == 0 || s.SourceKey == selectedSourceKey)).ToArray();
                MusicRules.Require(recordings.Length == 1, recordings.Length == 0 ? "RECORDING_UNAVAILABLE" : "RECORDING_REQUIRED");
                return recordings[0];
            }
            var recording = CurrentRecording();
            selectedSourceKey = recording.SourceKey;
            MusicRules.Require(quality == "auto" || MusicQualityPolicy.OfficialTypes.Contains(quality), "QUALITY_UNAVAILABLE");
            if (quality != "auto")
                MusicRules.Require(MusicQualityPolicy.SupportsOfficialQuality(platform, quality), "QUALITY_UNAVAILABLE");
            string[] qualities = quality == "auto"
                ? MusicQualityPolicy.PlatformOfficialTypes(platform)
                    .Where(q => MusicQualityPolicy.Enabled(q, policy.Policy))
                    .OrderBy(q => MusicQualityPolicy.Priority(q, policy.Policy))
                    .ThenBy(q => q == "320k" ? 0 : q == "192k" ? 1 : 2).ToArray()
                : manualSource || MusicQualityPolicy.Enabled(quality, policy.Policy) ? [quality] : [];
            MusicRules.Require(qualities.Length > 0,
                quality == "auto" && MusicQualityPolicy.HasEnabled(policy.Policy) ? "QUALITY_UNAVAILABLE" : "QUALITY_DISABLED");
            string signature = MusicRules.CanonicalTupleHash(session!.Scope, songId, selectedSourceKey, quality);
            var context = new OfficialAudioContext(
                session!, officialPolicy, policy, token, songId, selectedSourceKey, qualities, signature, Current, CurrentRecording);
            return await action(context).ConfigureAwait(false);
        }
        catch (MusicAccountException e)
        {
            if (session is not null && e.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED") accounts.Revoke(session);
            throw;
        }
        catch (MusicPlatformException e)
        {
            if (session is not null && e.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED") accounts.Revoke(session);
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { throw new MusicOperationException("INVALID_RESPONSE"); }
    }

    private async Task<(Uri Uri, string RequestQuality, string ActualQuality, bool QualityVerified, double Duration, long Size, long? ExpiresAt, bool Full)>
        RequestOfficialAudioAsync(MusicPlatformSession session, Func<JsonElement> recording,
            IReadOnlyList<string> qualities, CancellationToken token, Action accountGuard)
    {
        var value = await platform.RequestAsync(session.Platform, "audio", () =>
        {
            var data = recording();
            return MusicNodeSongCodec.BuildObject(w =>
            {
                if (data.TryGetProperty("songmid", out var mid)) { w.WritePropertyName("id"); mid.WriteTo(w); }
                else w.WriteNull("id");
                w.WritePropertyName("musicInfo");
                data.WriteTo(w);
                MusicNodeSongCodec.WriteStringArray(w, "qualities", qualities);
            });
        }, session, token, accountGuard).ConfigureAwait(false);
        var url = value.GetProperty("url");
        string? rawUrl = url.ValueKind == JsonValueKind.String ? url.GetString() : null;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
            throw new MusicPlatformException("INVALID_RESPONSE");
        return (uri,
            value.GetProperty("requestQuality").GetString()!,
            value.GetProperty("actualQuality").GetString()!,
            value.GetProperty("qualityVerified").GetBoolean(),
            value.GetProperty("duration").ValueKind == JsonValueKind.Null ? 0 : value.GetProperty("duration").GetDouble(),
            value.GetProperty("size").GetInt64(),
            value.GetProperty("expiresAt").ValueKind == JsonValueKind.Null ? null : value.GetProperty("expiresAt").GetInt64(),
            value.GetProperty("full").GetBoolean());
    }
}
