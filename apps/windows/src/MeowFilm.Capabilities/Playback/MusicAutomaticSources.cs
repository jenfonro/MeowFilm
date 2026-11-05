using System.Text.Json;
using System.Runtime.ExceptionServices;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // The identity of a candidate includes its provider, not only the recording.
    // An unavailable built-in URL must not exclude a script for that same recording.
    private static string SourceIdentity(MusicOnlineTarget target) =>
        JsonSerializer.Serialize(new[] { target.Installation, target.Platform, target.SourceKey });

    private static bool UnavailableSourceCandidate(string code) => UnavailableOfficialCandidate(code) || code is
        "CAPABILITY_UNAVAILABLE" or "MEDIA_UNAVAILABLE" or "EXPIRED_MEDIA_CANDIDATE" or
        "SCRIPT_FAILURE" or "SCRIPT_EXCEPTION" or "SCRIPT_PROTOCOL_ERROR" or "INVALID_MEDIA_CANDIDATE" or
        "MEDIA_HEADERS_UNSUPPORTED" or "OFFICIAL_SOURCE_DISABLED" or "DISABLED";

    private static string SourceSetIdentity(JsonElement items) => JsonSerializer.Serialize(
        items.EnumerateArray().Select(row => new {
            id = MusicDto.Text(row, "installationId"), enabled = row.GetProperty("enabled").GetBoolean(),
            hash = MusicDto.Text(row, "executionHash")
        }));

    private async Task<ResolvedMusic> ResolveAutomaticSources(SongRow song, string quality, string policy,
        CancellationToken token, IReadOnlySet<string>? rejectedSources = null,
        IReadOnlySet<string>? rejectedUrls = null, bool requireNetwork = false,
        string? recordingPlatform = null, string? recordingKey = null)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(35));
        var cancellation = request.Token;
        var list = await Call("music.sources.list", cancellationToken: cancellation);
        var items = list.GetProperty("items");
        string sourceSet = SourceSetIdentity(items);
        var scripts = items.EnumerateArray().Where(row => row.GetProperty("enabled").GetBoolean()).ToArray();
        var settings = savedQualityPolicy!.DeepClone().AsObject();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        string? scriptFailure = null;
        int Rank(ResolvedMusic media)
        {
            string level = media.Target!.Quality;
            if (media.Receipt is { } receipt && receipt.TryGetProperty("qualityVerified", out var verified) &&
                verified.ValueKind == JsonValueKind.True)
                level = MusicDto.Text(receipt, "actualQuality");
            return MusicQualityPolicy.Priority(level, settings);
        }
        async Task<ResolvedMusic?> Network(JsonElement snapshot, string[] preference, bool matchMissing,
            CancellationToken workerToken)
        {
            ResolvedMusic? best = null;
            int RecordingRank(string key)
            {
                int index = Array.IndexOf(preference, key);
                return index < 0 ? int.MaxValue : index;
            }
            async Task<JsonElement> Detail(JsonElement script) =>
                (await Call("music.sources.detail", new { installationId = MusicDto.Text(script, "installationId") }, workerToken))
                    .GetProperty("item");
            static bool CanResolve(JsonElement capability) => capability.GetProperty("actions").EnumerateArray()
                .Any(action => action.GetString() == "musicUrl");
            async Task Try(JsonElement detail, string platform, string? key)
            {
                if (!detail.GetProperty("enabled").GetBoolean()) return;
                var target = new MusicOnlineTarget(MusicDto.Text(detail, "installationId"), platform,
                    MusicDto.Integer(detail, "generation"), key, quality, quality, policy,
                    RecordingSong: snapshot, Automatic: recordingKey is null);
                string identity = SourceIdentity(target);
                if (rejectedSources?.Contains(identity) == true || !attempted.Add(identity)) return;
                try
                {
                    var media = await ResolveMusicQualityTarget(song, target, workerToken, initial: true);
                    if (rejectedSources?.Contains(SourceIdentity(media.Target!)) == true ||
                        media.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true) return;
                    if (Rank(media) != int.MaxValue && (best is null || Rank(media) < Rank(best))) best = media;
                }
                catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
                { scriptFailure ??= error.Code; }
            }
            int bestRank = quality == "auto"
                ? MusicQualityPolicy.LxTypes.Min(level => MusicQualityPolicy.Priority(level, settings))
                : MusicQualityPolicy.Priority(quality, settings);
            bool Complete() => best is not null && (recordingKey is null || Rank(best) == bestRank);
            try
            {
                if (matchMissing)
                {
                    // An earlier actual request activated idle sources. Only declared missing target
                    // platforms are matched, never every platform or every song in the queue.
                    var knownPlatforms = snapshot.GetProperty("sources").EnumerateArray()
                        .Select(row => MusicDto.Text(row, "source")).ToHashSet(StringComparer.Ordinal);
                    foreach (var script in scripts)
                    {
                        var detail = await Detail(script);
                        if (!detail.GetProperty("enabled").GetBoolean() || MusicDto.Text(detail, "state") != "ready") continue;
                        foreach (var capability in detail.GetProperty("capabilities").EnumerateArray())
                        {
                            string platform = MusicDto.Text(capability, "platform");
                            if (!NativeMusicSong.Platforms.Contains(platform) || knownPlatforms.Contains(platform) || !CanResolve(capability)) continue;
                            await Try(detail, platform, null);
                            if (Complete()) return best;
                        }
                    }
                }
                else
                {
                    // Built-in authorization does not filter script recordings. Reuse the same
                    // native snapshot without launching a second catalog discovery.
                    var recordings = snapshot.GetProperty("sources").EnumerateArray()
                        .Where(row => recordingKey is null || MusicDto.Text(row, "sourceKey") == recordingKey &&
                            MusicDto.Text(row, "source") == recordingPlatform)
                        .OrderBy(row => RecordingRank(MusicDto.Text(row, "sourceKey")));
                    foreach (var recording in recordings)
                    {
                        string platform = MusicDto.Text(recording, "source"), key = MusicDto.Text(recording, "sourceKey");
                        if (!NativeMusicSong.Platforms.Contains(platform)) continue;
                        foreach (var script in scripts)
                        {
                            var detail = await Detail(script);
                            // Listing never starts the runtime. An actual request may activate an
                            // idle installation, whose declarations are checked by the native owner.
                            if (MusicDto.Text(detail, "state") == "ready" &&
                                !detail.GetProperty("capabilities").EnumerateArray().Any(cap =>
                                    MusicDto.Text(cap, "platform") == platform && CanResolve(cap))) continue;
                            await Try(detail, platform, key);
                            if (Complete()) return best;
                        }
                    }
                }
                return best;
            }
            catch (Exception error) when (!token.IsCancellationRequested && workerToken.IsCancellationRequested &&
                (error is OperationCanceledException || error is MusicOperationException { Code: "CANCELLED" }))
            { scriptFailure ??= "TIMEOUT"; return best; }
        }
        async Task<ResolvedMusic> Accept(ResolvedMusic media)
        {
            token.ThrowIfCancellationRequested();
            var currentSources = await Call("music.sources.list", cancellationToken: token);
            if (SourceSetIdentity(currentSources.GetProperty("items")) != sourceSet)
                throw new MusicOperationException("SOURCES_CHANGED");
            var currentPolicy = await QualityCall("music.quality.settings.get", null, token);
            if (MusicDto.Text(currentPolicy, "signature") != policy)
                throw new MusicOperationException("QUALITY_POLICY_CHANGED");
            return media;
        }
        try
        {
            if (recordingKey is null)
            {
                // A playable cache or known mapping completes the operation before scripts
                // or discovery are started. Script fallback is admitted only when needed.
                try
                {
                    return await Accept(await ResolveAutomaticOfficial(song, quality, policy, cancellation,
                        rejectedSources, rejectedUrls, requireNetwork, scripts.Length == 0 ? null : Network));
                }
                catch (MusicOperationException error) when (scriptFailure is not null &&
                    error.Code is "RECORDING_UNAVAILABLE" or "PLAYBACK_UNAVAILABLE" or "AUTH_REQUIRED")
                { throw new MusicOperationException(scriptFailure); }
            }
            // An explicit recording keeps the existing provider comparison within that recording.
            // Both workers are drained before any error or successful selection is published.
            ExceptionDispatchInfo? failure = null;
            async Task<(ResolvedMusic? Media, string? Error)> Guard(Func<Task<ResolvedMusic?>> action)
            {
                try { return (await action(), null); }
                catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
                { return (null, error.Code); }
                catch (Exception error) when (!token.IsCancellationRequested && cancellation.IsCancellationRequested &&
                    (error is OperationCanceledException || error is MusicOperationException { Code: "CANCELLED" }))
                { return (null, "TIMEOUT"); }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                    request.Cancel();
                    return (null, null);
                }
            }
            async Task<ResolvedMusic?> BuiltIn()
            {
                var target = new MusicOnlineTarget(null, recordingPlatform!, 0, recordingKey,
                    quality, quality, policy, RecordingSong: KnownOfficialSong(song));
                if (rejectedSources?.Contains(SourceIdentity(target)) == true) return null;
                var media = await ResolveOfficialTarget(song, target, cancellation, initial: true, useCache: !requireNetwork);
                return media.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true ? null : media;
            }
            var results = await Task.WhenAll(Guard(BuiltIn),
                Guard(() => Network(KnownOfficialSong(song), [], false, cancellation)));
            failure?.Throw();
            var selected = results.Select(result => result.Media).OfType<ResolvedMusic>()
                .Where(media => Rank(media) != int.MaxValue).OrderBy(Rank).FirstOrDefault()
                ?? throw new MusicOperationException(results.Select(result => result.Error)
                    .FirstOrDefault(error => error is not null) ?? scriptFailure ?? "PLAYBACK_UNAVAILABLE");
            return await Accept(selected);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && request.IsCancellationRequested)
        { throw new MusicOperationException("TIMEOUT"); }
    }
}
