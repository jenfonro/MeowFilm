// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public interface IMusicArtistCatalog
{
    Task<JsonElement> ArtistAsync(MusicArtistRequest request, CancellationToken cancellationToken = default);
}

public sealed partial class MusicPlatformCatalog : IMusicArtistCatalog
{
    public Task<JsonElement> ArtistAsync(MusicArtistRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null);
        var p = request!;
        PJ.Require(p.Platform == "wy", "UNSUPPORTED");
        p = p with { Id = PJ.IdInput(p.Id) };
        return RunAsync(p.Platform, "artist", p, p.Refresh, c =>
        {
            return c.Wy("/api/v1/artist/" + p.Id, new(), data =>
            {
                var artist = data["artist"];
                PJ.Require(artist is JsonObject && PJ.Id(PJ.At(artist, "id")) == p.Id, "IDENTITY_MISMATCH");
                var name = PJ.Metadata(PJ.At(artist, "name"));
                var cover = new[] { PJ.At(artist, "img1v1Url"), PJ.At(artist, "picUrl") }
                    .Select(PJ.Image).FirstOrDefault(address => address.Length > 0) ?? "";
                PJ.Require(name.Length > 0 && cover.Length > 0, "INVALID_RESPONSE");
                var songs = PJ.Rows(data["hotSongs"], 1000).Select(PlatformSongs.Wy).ToArray();
                PJ.Require(songs.Select(NativeMusicSong.NativeId).Distinct(StringComparer.Ordinal).Count() == songs.Length,
                    "IDENTITY_MISMATCH");
                return PJ.Obj(("platform", "wy"), ("id", p.Id), ("name", name), ("cover", cover),
                    ("items", PJ.Arr(songs)), ("count", songs.Length), ("total", songs.Length),
                    ("more", false), ("continuation", null), ("source", "wy"), ("engine", "NeteaseArtist"));
            }, true);
        }, cancellationToken);
    }
}
