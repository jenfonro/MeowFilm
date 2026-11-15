// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace MeowFilm.Capabilities.Platforms;
public interface IMusicChorusCatalog
{
    Task<JsonElement> ChorusAsync(string platform, string id, CancellationToken token = default);
}
