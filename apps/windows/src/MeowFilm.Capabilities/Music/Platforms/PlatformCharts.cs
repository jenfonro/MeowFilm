// SPDX-License-Identifier: GPL-3.0-only
namespace MeowFilm.Capabilities.Platforms;
public sealed record MusicChartRequest(string Platform, string Kind, string Period = "daily", int? Year = null, bool Refresh = false);
