using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Sync;

/// <summary>
/// One physical-network budget for a complete sync round. Every native HTTP,
/// account-bound catalog and cloud call must enter through this owner.
/// </summary>
internal sealed class MusicSyncRequestBudget
{
    internal const int MaximumRoundRequests = 192;
    internal const int MaximumPlatformRequests = 96;
    internal const int MaximumDirectoryPages = 20;
    internal const int MaximumCollectionPages = 40;
    internal const int MaximumCollectionsPerPlatform = 40;
    internal const int MaximumMappingLookupsPerPlatform = 24;

    private readonly object gate = new();
    private readonly Dictionary<string, int> platforms = [];
    private int total;

    internal void Admit(string platform)
    {
        lock (gate)
        {
            LJ.Require(MusicLibraryOwner.SyncPlatforms.Contains(platform), "INVALID_PLATFORM");
            int platformCount = platforms.GetValueOrDefault(platform);
            LJ.Require(total < MaximumRoundRequests &&
                platformCount < MaximumPlatformRequests, "SYNC_REQUEST_LIMIT");
            total++;
            platforms[platform] = platformCount + 1;
        }
    }
}
