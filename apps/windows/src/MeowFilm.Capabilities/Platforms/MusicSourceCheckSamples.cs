using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>
/// Public catalog identifiers of one well-known recording per built-in platform, the
/// same samples as the shared music module's script check. A pass means "this sample
/// resolved", never that every song or quality is playable.
/// </summary>
internal static class MusicSourceCheckSamples
{
    internal static JsonObject Recording(string platform) => platform switch
    {
        "wy" => LJ.Obj(("songmid", "347230"), ("name", "海阔天空"), ("singer", "Beyond")),
        "tx" => LJ.Obj(("songmid", "0039MnYb0qxYhV"), ("name", "晴天"), ("singer", "周杰伦")),
        "kg" => LJ.Obj(("hash", "30ece05c17d4e9ec8ba0ef9f96e7a07c"), ("name", "晴天"), ("singer", "周杰伦")),
        _ => throw new MusicLibraryException("INVALID_REQUEST")
    };
    internal static string Identity(string platform) =>
        platform + ":" + (platform == "kg" ? "30ece05c17d4e9ec8ba0ef9f96e7a07c" : LJ.S(Recording(platform)["songmid"]));
}
