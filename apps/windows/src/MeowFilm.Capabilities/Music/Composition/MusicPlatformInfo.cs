namespace MeowFilm.Capabilities;

internal static class MusicPlatformInfo
{
    internal static string DisplayName(string platform) => platform switch
    {
        "wy" => "网易云音乐",
        "tx" => "QQ 音乐",
        "kg" => "酷狗音乐",
        "kw" => "酷我音乐",
        "mg" => "咪咕音乐",
        _ => platform
    };

    internal static string LogoUri(string platform) =>
        "ms-appx:///MeowFilm.Capabilities/Music/Accounts/Presentation/Assets/" + (platform switch
        {
            "tx" => "qq-music.png",
            "kg" => "kugou-music.png",
            "kw" => "kuwo-music.png",
            "mg" => "migu-music.png",
            _ => "netease-music.png"
        });
}
