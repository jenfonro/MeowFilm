namespace MeowFilm.Capabilities;

internal static class MusicQualityLabels
{
    public static string Name(string key) => key switch
    {
        "master" => "母带", "spatial" => "空间／全景", "enhanced" => "平台增强", "hires" => "高解析度",
        "lossless" => "无损", "high" => "高音质", "standard" => "标准",
        "dolby" => "杜比全景声", "dts_x" => "DTS:X", "sony360" => "360全景音频",
        "sky" => "沉浸环绕声", "vivid" => "臻音全景声", "qq_atmos" => "臻品全景声",
        "viper_atmos" => "蝰蛇全景声", _ => key
    };
}
