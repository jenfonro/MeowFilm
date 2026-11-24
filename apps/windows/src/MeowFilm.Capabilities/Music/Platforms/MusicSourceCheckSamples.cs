using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>
/// Public catalog identifiers of one well-known recording per built-in platform, the
/// same samples as the shared music module's script check. A pass means "this sample
/// resolved", never that every song or quality is playable.
/// </summary>
internal static class MusicSourceCheckSamples
{
    internal static JsonElement Recording(string platform) => platform switch
    {
        "wy" => MusicNodeSongCodec.BuildObject(w =>
        {
            w.WriteString("songmid", "347230"); w.WriteString("name", "海阔天空"); w.WriteString("singer", "Beyond");
        }),
        "tx" => MusicNodeSongCodec.BuildObject(w =>
        {
            w.WriteString("songmid", "0039MnYb0qxYhV"); w.WriteString("name", "晴天"); w.WriteString("singer", "周杰伦");
        }),
        "kg" => MusicNodeSongCodec.BuildObject(w =>
        {
            w.WriteString("hash", "30ece05c17d4e9ec8ba0ef9f96e7a07c"); w.WriteString("name", "晴天"); w.WriteString("singer", "周杰伦");
        }),
        _ => throw new MusicLibraryException("INVALID_REQUEST")
    };

    internal static string Identity(string platform) => platform switch
    {
        "wy" => "wy:347230",
        "tx" => "tx:0039MnYb0qxYhV",
        "kg" => "kg:30ece05c17d4e9ec8ba0ef9f96e7a07c",
        _ => throw new MusicLibraryException("INVALID_REQUEST")
    };
}
