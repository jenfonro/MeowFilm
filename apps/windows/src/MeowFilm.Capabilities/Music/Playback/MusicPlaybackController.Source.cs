using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

internal sealed record MusicOnlineTarget(
    string? Installation,
    string Platform,
    long Generation,
    string? SourceKey,
    string PolicyRequest,
    string Quality,
    string? QualityPolicySignature = null,
    string? QualityCacheSignature = null,
    string? AccountScope = null,
    MusicSongRecord? RecordingSong = null,
    string? ProviderName = null,
    long SourceRevision = 0,
    bool ManualSource = false)
{
    internal bool Official => Installation is null;
}

internal sealed record MusicFileIdentity(string Scope, string Signature);

internal sealed record ResolvedMusic(
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    MusicOnlineTarget? Target = null,
    long? ExpiresAt = null,
    AudioFileInfoRecord? Receipt = null,
    MusicFileIdentity? File = null,
    MusicAudioFileCache.PreparedFile? Bytes = null);

internal sealed record MusicSelection(
    MusicQualityPolicy.Tier? Tier = null,
    ResolvedMusic? Source = null,
    bool ManualSource = false)
{
    internal bool Automatic => Tier is null && Source is null;
    internal bool Manual => ManualSource || Source is not null;

    internal bool Allows(ResolvedMusic media) =>
        (Tier is null || Tier.Levels.Contains(MusicPlaybackController.EffectiveLevel(media))) &&
        (Source is null || MusicPlaybackController.SourceIdentity(Source.Target!) == MusicPlaybackController.SourceIdentity(media.Target!) &&
            Source.Target!.PolicyRequest == media.Target!.PolicyRequest &&
            Source.Target.Quality == media.Target.Quality &&
            (!MusicPlaybackController.QualityVerified(Source) || !MusicPlaybackController.QualityVerified(media) ||
                MusicPlaybackController.EffectiveLevel(Source) == MusicPlaybackController.EffectiveLevel(media)));
}

internal enum SourceAttemptState { Pending, Ok, Unavailable }

internal sealed class SourceAttempt(string key, MusicOnlineTarget target)
{
    internal readonly string Key = key;
    internal readonly MusicOnlineTarget Target = target;
    internal SourceAttemptState State;
    internal ResolvedMusic? Media;
    internal string? Code;
    internal long At;
}

public sealed partial class MusicPlaybackController
{
    private readonly Dictionary<string, string> recordingChoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> recordingPlatforms = new(StringComparer.Ordinal);
    private long exclusionsRevision;
    private long selectionSourcesRevision;

    internal MusicNativeApplication Application => application;
    internal MusicSettingsController Settings => settings;
    internal MusicAudioFileCache? AudioFileCache => audioFileCache;
    internal long ExclusionsRevision => Interlocked.Read(ref exclusionsRevision);
    internal long SelectionSourcesRevision => Interlocked.Read(ref selectionSourcesRevision);

    private void IncrementExclusionsRevision() => Interlocked.Increment(ref exclusionsRevision);
    internal void IncrementSelectionSourcesRevision() => Interlocked.Increment(ref selectionSourcesRevision);

    private void RememberRecording(string songId, string platform, string sourceKey)
    {
        recordingPlatforms[songId] = platform;
        recordingChoices[songId + "\0" + platform] = sourceKey;
    }

    private void ForgetRecording(string songId)
    {
        recordingPlatforms.Remove(songId);
        foreach (var key in recordingChoices.Keys.Where(key => key.StartsWith(songId + "\0", StringComparison.Ordinal)).ToArray())
            recordingChoices.Remove(key);
    }

    private bool HasRememberedRecording(string songId) =>
        recordingChoices.Keys.Any(key => key.StartsWith(songId + "\0", StringComparison.Ordinal));

    internal static MusicSongRecord KnownOfficialSong(SongRow song, MusicSongRecord? snapshot = null) =>
        snapshot ?? song.Record;

    internal static MusicFileIdentity? FileIdentity(ResolvedMusic media) => media.File ??
        (media.Target is { Official: true, AccountScope: not null, QualityCacheSignature: not null } target
            ? new(target.AccountScope, target.QualityCacheSignature) : null);

    internal static string SourceIdentity(MusicOnlineTarget target) =>
        MusicRules.CanonicalArray(target.Installation ?? "\0", target.Platform, target.SourceKey ?? "\0");

    internal static bool UnavailableOfficialCandidate(string code) => code is
        "AUTH_REQUIRED" or "VIP_REQUIRED" or "FORBIDDEN" or "PLAYBACK_RESTRICTED" or "TRIAL_ONLY" or
        "QUALITY_UNAVAILABLE" or "RECORDING_QUALITY_UNAVAILABLE" or "RECORDING_UNAVAILABLE" or
        "RECORDING_MISMATCH" or "MEDIA_EXPIRED" or "NETWORK_ERROR" or "UPSTREAM_ERROR" or
        "TIMEOUT" or "RATE_LIMITED" or "CACHE_MISS" or "IDENTITY_MISMATCH" or "MEDIA_MISMATCH" or
        "QUALITY_DISABLED" or "OFFICIAL_SOURCE_DISABLED" or "UNSUPPORTED";

    internal static bool UnavailableSourceCandidate(string code) => UnavailableOfficialCandidate(code) || code is
        "CAPABILITY_UNAVAILABLE" or "MEDIA_UNAVAILABLE" or "EXPIRED_MEDIA_CANDIDATE" or
        "SCRIPT_FAILURE" or "SCRIPT_EXCEPTION" or "SCRIPT_PROTOCOL_ERROR" or "INVALID_MEDIA_CANDIDATE" or
        "MEDIA_HEADERS_UNSUPPORTED" or "SOURCE_DISABLED" or "SOURCE_NOT_FOUND" or "STALE_SOURCE" or
        "UPSTREAM_HTTP" or "UPSTREAM_FORMAT" or "TARGET_CHANGED" or "TARGET_REQUIRED" or
        "SCRIPT_RUNTIME_UNAVAILABLE";

    internal static bool ContentSourceFailure(string code) => code is
        "RECORDING_UNAVAILABLE" or "MEDIA_UNAVAILABLE" or "FORBIDDEN" or "PLAYBACK_RESTRICTED" or
        "TRIAL_ONLY" or "QUALITY_UNAVAILABLE" or "RECORDING_QUALITY_UNAVAILABLE" or
        "RECORDING_MISMATCH" or "IDENTITY_MISMATCH" or "MEDIA_MISMATCH" or PlaybackError.HttpNotFound;

    internal static string EffectiveLevel(ResolvedMusic media) =>
        media.Receipt is { QualityVerified: true, ActualQuality.Length: > 0 } receipt
            ? receipt.ActualQuality : media.Target!.Quality;

    internal static bool QualityVerified(ResolvedMusic media) =>
        media.Receipt?.QualityVerified == true;

    internal static double ExpectedDuration(SongRow song) => song.Record.Duration is { } seconds &&
        double.IsFinite(seconds) && seconds > 0 ? seconds : 0;

    internal static bool MediaDurationMismatch(SongRow song, double actual) =>
        double.IsFinite(actual) && actual > 0 && ExpectedDuration(song) is > 0 and var expected && Math.Abs(actual - expected) > 10;

    private async Task<MusicSourceSession> BeginSourceSessionAsync(SongRow song, CancellationToken token, bool officialOnly = false)
    {
        var qualityResult = await application.Library.GetQualityPolicyAsync(token);
        settings.AcceptQuality(qualityResult);
        var officials = await application.Library.GetOfficialSourcesAsync(token);
        var enabled = officials.Enabled.ToHashSet(StringComparer.Ordinal);
        string? platform = recordingPlatforms.GetValueOrDefault(song.Id);
        string? key = platform is null ? null : recordingChoices.GetValueOrDefault(song.Id + "\0" + platform);
        var session = new MusicSourceSession(this, song, qualityResult.Signature, qualityResult.Settings, enabled, officialOnly, platform, key);
        await session.RefreshSongAsync(token);
        await session.LoadExclusionsAsync(token);
        return session;
    }

    internal Task<ResolvedMusic> ResolveTargetAsync(SongRow song, MusicOnlineTarget target, CancellationToken token) =>
        target.Official
            ? ResolveOfficialTargetAsync(song, target, token, initial: false)
            : ResolveScriptTargetAsync(song, target, token, initial: false);

    internal async Task<ResolvedMusic> ResolveOfficialTargetAsync(
        SongRow song,
        MusicOnlineTarget target,
        CancellationToken token,
        bool initial,
        bool cacheOnly = false,
        bool urlCacheOnly = false,
        bool refresh = false)
    {
        var knownSong = KnownOfficialSong(song, target.RecordingSong);
        string quality = initial ? target.PolicyRequest : target.Quality;
        if (initial && cacheOnly && audioFileCache is not null)
        {
            var authority = await application.GetOfficialCacheContextAsync(
                target.Platform, knownSong, quality, target.SourceKey, target.AccountScope,
                target.Generation, target.QualityPolicySignature, target.ManualSource, token);
            string scope = authority.Scope, signature = authority.QualityCacheSignature;
            var info = TryGetVerifiedPreparedReceipt(song, scope, signature)
                ?? await audioFileCache.ReadInfoAsync(scope, song.Id, signature, token);
            if (info is not null)
            {
                if (info.Full != true)
                    throw new MusicOperationException("CACHE_MISS");
                var cachedTarget = target with
                {
                    AccountScope = scope, Generation = authority.Generation,
                    SourceKey = authority.SourceKey, QualityCacheSignature = signature,
                    Quality = info.Quality!, RecordingSong = knownSong
                };
                var policy = await application.Library.GetQualityPolicyAsync(token);
                if (policy.Signature != target.QualityPolicySignature)
                    throw new MusicOperationException("QUALITY_POLICY_CHANGED");
                return new(null, new Dictionary<string, string>(), cachedTarget, Receipt: info);
            }
        }
        if (cacheOnly) throw new MusicOperationException("CACHE_MISS");
        var response = await application.ResolveOfficialAudioAsync(
            target.Platform, knownSong, quality, target.SourceKey, target.AccountScope,
            target.Generation, target.QualityPolicySignature, target.ManualSource,
            cacheOnly: urlCacheOnly, refresh: !initial || refresh, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        var currentPolicy = await application.Library.GetQualityPolicyAsync(token);
        token.ThrowIfCancellationRequested();
        if (currentPolicy.Signature != target.QualityPolicySignature)
            throw new MusicOperationException("QUALITY_POLICY_CHANGED");
        if (response.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new MusicOperationException("MEDIA_EXPIRED");
        return new(response.Uri, response.Headers, target with
        {
            Generation = response.Generation, SourceKey = response.SourceKey,
            AccountScope = response.Scope, Quality = response.Quality,
            QualityCacheSignature = response.QualityCacheSignature,
            RecordingSong = knownSong
        }, response.ExpiresAt, response.ToAudioFileInfoRecord());
    }

    internal async Task<ResolvedMusic> ResolveScriptTargetAsync(
        SongRow song,
        MusicOnlineTarget target,
        CancellationToken token,
        bool initial = false)
    {
        var recordingSong = KnownOfficialSong(song, target.RecordingSong);
        string installationId = target.Installation ?? throw new MusicOperationException("TARGET_REQUIRED");
        var response = await application.Scripts.ResolveOnlineAsync(
            installationId,
            target.Platform,
            recordingSong,
            target.Quality,
            target.SourceKey,
            target.ManualSource,
            target.QualityPolicySignature,
            target.QualityCacheSignature,
            token);
        token.ThrowIfCancellationRequested();
        if (response.Duration is { } seconds && MediaDurationMismatch(song, seconds))
            throw new MusicOperationException("RECORDING_MISMATCH");
        var qualityReply = await application.Library.GetQualityPolicyAsync(token);
        token.ThrowIfCancellationRequested();
        if (qualityReply.Signature != response.QualityPolicySignature)
            throw new MusicOperationException("QUALITY_POLICY_CHANGED");
        if (response.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new MusicOperationException("EXPIRED_MEDIA_CANDIDATE");
        return new(response.Uri, response.Headers, target with
        {
            Generation = response.Generation, SourceKey = response.SourceKey, Quality = response.Quality,
            QualityPolicySignature = response.QualityPolicySignature,
            QualityCacheSignature = response.QualityCacheSignature,
            RecordingSong = KnownOfficialSong(song, recordingSong)
        }, response.ExpiresAt);
    }

    private async Task ConfirmOfficialPlaybackAsync(SongRow song, MusicOnlineTarget target, CancellationToken token)
    {
        var knownSong = KnownOfficialSong(song, target.RecordingSong);
        try
        {
            await application.ConfirmOfficialPlaybackAsync(
                target.Platform, knownSong, target.Quality, target.SourceKey, target.AccountScope,
                target.Generation, target.QualityPolicySignature, target.ManualSource, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicPlayback, "official-playback", "confirm-failed",
                new { platform = target.Platform, error = error.Message });
        }
    }
}
