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
        var plan = new TaskCompletionSource<(JsonElement Song, string[] Preference)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Planned(JsonElement snapshot, string[] preference) =>
            plan.TrySetResult((snapshot, preference));
        int Rank(ResolvedMusic media)
        {
            string level = media.Target!.Quality;
            if (media.Receipt is { } receipt && receipt.TryGetProperty("qualityVerified", out var verified) &&
                verified.ValueKind == JsonValueKind.True)
                level = MusicDto.Text(receipt, "actualQuality");
            return MusicQualityPolicy.Priority(level, settings);
        }
        async Task<(ResolvedMusic? Media, string? Error)> BuiltIn()
        {
            try
            {
                if (recordingKey is null)
                    return (await ResolveAutomaticOfficial(song, quality, policy, cancellation,
                        rejectedSources, rejectedUrls, requireNetwork, Planned), null);
                Planned(KnownOfficialSong(song), []);
                var target = new MusicOnlineTarget(null, recordingPlatform!, 0, recordingKey,
                    quality, quality, policy, RecordingSong: KnownOfficialSong(song));
                if (rejectedSources?.Contains(SourceIdentity(target)) == true) return (null, null);
                var media = await ResolveOfficialTarget(song, target, cancellation, initial: true, useCache: !requireNetwork);
                return (media.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true ? null : media, null);
            }
            catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
            { return (null, error.Code); }
            finally { Planned(KnownOfficialSong(song), []); }
        }
        async Task<(ResolvedMusic? Media, string? Error)> Network()
        {
            ResolvedMusic? best = null;
            string? failure = null;
            // Share native discovery and recording preference, not its authorization:
            // a disabled built-in provider cannot filter the script's recordings.
            var selectedPlan = await plan.Task.WaitAsync(cancellation);
            if (scripts.Length == 0) return (null, null);
            var snapshot = selectedPlan.Song;
            int RecordingRank(string key)
            {
                int index = Array.IndexOf(selectedPlan.Preference, key);
                return index < 0 ? int.MaxValue : index;
            }
            var recordings = snapshot.GetProperty("sources").EnumerateArray()
                .Where(row => recordingKey is null || MusicDto.Text(row, "sourceKey") == recordingKey &&
                    MusicDto.Text(row, "source") == recordingPlatform)
                .OrderBy(row => RecordingRank(MusicDto.Text(row, "sourceKey"))).ToArray();
            int bestRank = quality == "auto"
                ? MusicQualityPolicy.LxTypes.Min(level => MusicQualityPolicy.Priority(level, settings))
                : MusicQualityPolicy.Priority(quality, settings);
            foreach (var recording in recordings)
            {
                string platform = MusicDto.Text(recording, "source"), key = MusicDto.Text(recording, "sourceKey");
                if (!NativeMusicSong.Platforms.Contains(platform)) continue;
                foreach (var script in scripts)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                        return (best, failure ?? "TIMEOUT");
                    }
                    string id = MusicDto.Text(script, "installationId");
                    var target = new MusicOnlineTarget(id, platform, 0, key, quality, quality, policy,
                        RecordingSong: snapshot, Automatic: recordingKey is null);
                    if (rejectedSources?.Contains(SourceIdentity(target)) == true) continue;
                    try
                    {
                        var detail = (await Call("music.sources.detail", new { installationId = id }, cancellation))
                            .GetProperty("item");
                        if (!detail.GetProperty("enabled").GetBoolean()) continue;
                        // Idle installations have no live declarations. Only an actual
                        // request may activate one; listing never starts the runtime.
                        if (MusicDto.Text(detail, "state") == "ready" &&
                            !detail.GetProperty("capabilities").EnumerateArray().Any(cap =>
                                MusicDto.Text(cap, "platform") == platform &&
                                cap.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "musicUrl")))
                            continue;
                        target = target with { Generation = MusicDto.Integer(detail, "generation") };
                        var media = await ResolveMusicQualityTarget(song, target, cancellation, initial: true);
                        if (media.Uri is { } uri && rejectedUrls?.Contains(uri.AbsoluteUri) == true) continue;
                        if (Rank(media) != int.MaxValue && (best is null || Rank(media) < Rank(best))) best = media;
                        if (best is not null && Rank(best) == bestRank) return (best, failure);
                    }
                    catch (MusicOperationException error) when (UnavailableSourceCandidate(error.Code))
                    { failure ??= error.Code; }
                    catch (Exception error) when (!token.IsCancellationRequested && cancellation.IsCancellationRequested &&
                        (error is OperationCanceledException || error is MusicOperationException { Code: "CANCELLED" }))
                    { return (best, failure ?? "TIMEOUT"); }
                }
            }
            return (best, failure);
        }
        ExceptionDispatchInfo? failure = null;
        async Task<(ResolvedMusic? Media, string? Error)> Guard(Func<Task<(ResolvedMusic? Media, string? Error)>> action)
        {
            try { return await action(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && cancellation.IsCancellationRequested)
            { return (null, "TIMEOUT"); }
            catch (MusicOperationException error) when (error.Code == "CANCELLED" &&
                !token.IsCancellationRequested && cancellation.IsCancellationRequested)
            { return (null, "TIMEOUT"); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                request.Cancel();
                return (null, null);
            }
        }
        try
        {
            // Native providers and the sequential script worker share one deadline.
            // A script miss does not suppress built-in sources, or vice versa.
            var results = await Task.WhenAll(Guard(BuiltIn), Guard(Network));
            failure?.Throw();
            token.ThrowIfCancellationRequested();
            var currentSources = await Call("music.sources.list", cancellationToken: token);
            if (SourceSetIdentity(currentSources.GetProperty("items")) != sourceSet)
                throw new MusicOperationException("SOURCES_CHANGED");
            var currentPolicy = await QualityCall("music.quality.settings.get", null, token);
            if (MusicDto.Text(currentPolicy, "signature") != policy)
                throw new MusicOperationException("QUALITY_POLICY_CHANGED");
            var selectedPlan = await plan.Task;
            int RecordingRank(ResolvedMusic media)
            {
                int index = Array.IndexOf(selectedPlan.Preference, media.Target!.SourceKey);
                return index < 0 ? int.MaxValue : index;
            }
            // Equal quality retains confirmed recording preference before stable
            // provider order. A type never excludes another eligible candidate.
            return results.Select(result => result.Media).OfType<ResolvedMusic>()
                .Where(media => Rank(media) != int.MaxValue).OrderBy(Rank).ThenBy(RecordingRank).FirstOrDefault()
                ?? throw new MusicOperationException(results.Select(result => result.Error)
                    .FirstOrDefault(error => error is not null) ?? "PLAYBACK_UNAVAILABLE");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && request.IsCancellationRequested)
        { throw new MusicOperationException("TIMEOUT"); }
    }
}
