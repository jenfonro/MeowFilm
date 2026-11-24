using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    /// <summary>
    /// Built-in candidates of the song's accepted recordings for one request, history first.
    /// Local only: no catalog search. Missing platforms are completed by CompleteSongAsync.
    /// </summary>
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
                var song = NativeMusicSong.Normalize(songRecord);
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
                song = await songMatcher.KnownAsync(song, token).ConfigureAwait(false);
                string songId = song.SongId;
                Current();
                var plan = new MusicOfficialCandidates(song);
                foreach (var (platform, owner) in owners)
                {
                    if (owner is null) continue;
                    var remembered = await sourceCache.ReadHistoryAsync(owner.Scope, songId, policy.Signature,
                        quality, owner.Generation, token).ConfigureAwait(false);
                    Current();
                    if (remembered is not null)
                        plan.Remember(remembered, platform);
                }
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                return officialPolicy.Publish(null, () =>
                {
                    Current();
                    var snap = plan.Snapshot(owners.Where(pair => pair.Value is not null).Select(pair => pair.Key));
                    var candidates = snap.Candidates.Select(c =>
                    {
                        var owner = owners[c.Platform]!;
                        return new MusicOfficialCandidateItem(c.Platform, c.SourceKey, owner.Scope, owner.Generation);
                    }).ToArray();
                    return new MusicOfficialCandidatesResult(
                        snap.Song,
                        candidates,
                        policy.Signature,
                        owners.Values.All(owner => owner is null));
                });
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
            { throw new MusicOperationException("INVALID_RESPONSE"); }
        }, cancellationToken);
}
