using System.Text.Json;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // One exact script request. A refresh re-asks the same installation, platform, recording
    // and quality; its accepted cache signature proves the same content and policy.
    private async Task<ResolvedMusic> ResolveMusicQualityTarget(SongRow song, MusicOnlineTarget target,
        CancellationToken token, bool initial = false)
    {
        var parameters = new Dictionary<string, object>
        {
            ["installationId"] = target.Installation ?? throw new MusicOperationException("TARGET_REQUIRED"), ["platform"] = target.Platform,
            ["song"] = target.RecordingSong ?? song.Raw, ["quality"] = target.Quality
        };
        if (target.ManualSource) parameters["manualSource"] = true;
        if (target.SourceKey is not null) parameters["sourceKey"] = target.SourceKey;
        if (target.QualityPolicySignature is not null) parameters["qualityPolicySignature"] = target.QualityPolicySignature;
        if (target.QualityCacheSignature is not null) parameters["qualityCacheSignature"] = target.QualityCacheSignature;
        if (!initial)
            MusicDto.Require(target.QualityPolicySignature is not null && target.QualityCacheSignature is not null);
        var response = await QualityCall("music.online.resolve", parameters, token);
        token.ThrowIfCancellationRequested();
        var recordingSong = target.RecordingSong;
        var generation = MusicDto.Integer(response, "generation");
        var sourceKey = MusicDto.Text(response, "sourceKey");
        string actualRequest = MusicDto.Text(response, "quality");
        string policy = MusicDto.Text(response, "qualityPolicySignature");
        string cache = MusicDto.Text(response, "qualityCacheSignature");
        MusicDto.Require(MusicDto.Text(response, "installationId") == target.Installation &&
            MusicDto.Text(response, "platform") == target.Platform && MusicDto.Text(response, "songId") == song.Id &&
            MusicQualityPolicy.LxTypes.Contains(actualRequest) && actualRequest == target.Quality &&
            (target.SourceKey is null || sourceKey == target.SourceKey) &&
            (initial || generation == target.Generation) &&
            IsQualityHash(policy) && IsQualityHash(cache) &&
            (target.QualityPolicySignature is null || policy == target.QualityPolicySignature) &&
            (target.QualityCacheSignature is null || cache == target.QualityCacheSignature) &&
            response.GetProperty("playbackVerified").ValueKind == JsonValueKind.False &&
            response.GetProperty("qualityVerified").ValueKind == JsonValueKind.False);
        if (response.TryGetProperty("duration", out var duration) && duration.ValueKind != JsonValueKind.Null)
        {
            MusicDto.Require(duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds) &&
                double.IsFinite(seconds) && seconds >= 0);
            if (MediaDurationMismatch(song, duration.GetDouble()))
                throw new MusicOperationException("RECORDING_MISMATCH");
        }
        // Catch a concurrent settings write after the resolver's last gate, too.
        // A subsequent refresh carries the immutable accepted signatures.
        var settings = await QualityCall("music.quality.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        if (MusicDto.Text(settings, "signature") != policy) throw new MusicOperationException("QUALITY_POLICY_CHANGED");
        var uri = new Uri(MusicDto.Text(response, "url"), UriKind.Absolute);
        MusicDto.Require(uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0);
        var headers = response.GetProperty("headers").EnumerateObject()
            .ToDictionary(h => h.Name, h => h.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        MusicDto.Require(headers.Count <= 32);
        long? expires = response.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : null;
        MusicDto.Require(expires is null || expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new(uri, headers, target with
        {
            Generation = generation, SourceKey = sourceKey, Quality = actualRequest,
            QualityPolicySignature = policy, QualityCacheSignature = cache, RecordingSong = recordingSong
        }, expires);
    }
}
