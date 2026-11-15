// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace MeowFilm.Capabilities.Platforms;
public sealed record MusicChartRequest(string Platform, string Kind, string Period = "daily", int? Year = null, bool Refresh = false);
public interface IMusicChartCatalog
{
    Task<JsonElement> ChartAsync(MusicChartRequest request, CancellationToken cancellationToken = default);
}
