// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace MeowFilm.Capabilities.Platforms;
public interface IMusicArtistCatalog
{
    Task<JsonElement> ArtistAsync(MusicArtistRequest request, CancellationToken cancellationToken = default);
}
