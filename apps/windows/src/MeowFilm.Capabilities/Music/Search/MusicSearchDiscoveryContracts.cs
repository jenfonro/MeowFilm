// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace MeowFilm.Capabilities.Search;

public sealed record MusicSearchDiscoveryHot(string Keyword, double? Score);
// Songs contain only the existing native-normalized song shape, never raw platform responses or credentials.
public sealed record MusicSearchDiscoveryChart(string Id, string Name, string Cover, long? Count, string Creator,
    JsonElement[] Songs);
public sealed record MusicSearchDiscoveryFailure(string Stage, string? ChartId, string Code);
public sealed record MusicSearchDiscoverySnapshot(long Generation, MusicSearchDiscoveryHot[] Hot,
    string[] Suggestions, string[] Recommended, MusicSearchDiscoveryChart[] Charts,
    MusicSearchDiscoveryFailure[] Failures, long FetchedAt);
