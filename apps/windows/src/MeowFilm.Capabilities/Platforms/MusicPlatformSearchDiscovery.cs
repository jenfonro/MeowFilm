// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Search;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

internal sealed class MusicPlatformSearchDiscovery(MusicPlatformClient client, MusicLibraryOwner library)
{
    internal sealed record Data(MusicSearchDiscoveryHot[] Hot, string[] Suggestions,
        MusicSearchDiscoveryChart[] Charts, MusicSearchDiscoveryFailure[] Failures);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal async Task<Data> ReadAsync(MusicPlatformSession? session, bool refresh, Action guard, CancellationToken token)
    {
        long observation = library.BeginSongObservation();
        var value = await client.RequestAsync("wy", "searchDiscovery", new { refresh }, session, token, guard).ConfigureAwait(false);
        value = await library.AdoptPlatformResultAsync(value, token, observation: observation,
            shape: MusicLibraryOwner.PlatformSongShape.Discovery).ConfigureAwait(false);
        guard();
        return new(value.GetProperty("hot").Deserialize<MusicSearchDiscoveryHot[]>(Json)!,
            value.GetProperty("suggestions").Deserialize<string[]>(Json)!,
            value.GetProperty("charts").Deserialize<MusicSearchDiscoveryChart[]>(Json)!,
            value.GetProperty("failures").Deserialize<MusicSearchDiscoveryFailure[]>(Json)!);
    }
}
