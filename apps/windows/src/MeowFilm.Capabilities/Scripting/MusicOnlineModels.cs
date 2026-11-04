using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed record MusicSourceRow(string InstallationId, string Name, string Version, string Hash,
    long Generation, bool Enabled, string State, bool RollbackAvailable, string[] Platforms, string Capabilities)
{
    public string Label => $"{Name} · {InstallationId[^8..]}";
    public string StateText => State switch { "ready" => "已启用", "idle" => "待加载", "disabled" => "已停用", _ => "失败" };
    public string Metadata => $"网络源 · 版本 {Version}" + (Platforms.Length == 0 ? "" : " · " +
        string.Join("、", Platforms.Select(PlatformName)));
    internal static string PlatformName(string platform) => platform switch
    {
        "wy" => "网易云", "tx" => "QQ", "kg" => "酷狗", "kw" => "酷我", "mg" => "咪咕",
        "local" => "本地", _ => platform
    };
    internal static MusicSourceRow Read(JsonElement value)
    {
        var id = MusicDto.Text(value, "installationId");
        MusicDto.Require(id.Length >= 8 && id.Length <= 128);
        var capabilities = value.GetProperty("capabilities").EnumerateArray().ToArray();
        var platforms = capabilities.Where(c => c.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "musicUrl"))
            .Select(c => MusicDto.Text(c, "platform"))
            .Where(platform => NativeMusicSong.Platforms.Append("local").Contains(platform)).ToArray();
        return new(id, MusicDto.Text(value, "name"), MusicDto.Text(value, "version"),
            MusicDto.Text(value, "contentHash"), MusicDto.Integer(value, "generation"),
            value.GetProperty("enabled").GetBoolean(), MusicDto.Text(value, "state"),
            value.GetProperty("rollbackAvailable").GetBoolean(), platforms,
            string.Join(" · ", capabilities.Select(c => MusicDto.Text(c, "platform") + ": " +
                string.Join(", ", c.GetProperty("actions").EnumerateArray().Select(a => a.GetString())))));
    }
}

public sealed record MusicLyricRow(double Time, string Text, string Translation, string Romanization = "",
    IReadOnlyList<MusicLyricWord>? Words = null, string Timing = "");
public sealed record MusicLyricWord(string Text, double Time, double Duration);
public sealed record MusicLyricSourceRow(string SourceKey, string Label);
public sealed record MusicRecordingRow(string SongId, string SourceKey, string Platform, string Label);
