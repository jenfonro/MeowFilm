using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>Artwork is selected from this recording's admitted metadata, not its delivery/LX target.</summary>
public static class MusicArtworkPolicy
{
    public static string Select(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
        => Candidates(song, platforms, enabled).FirstOrDefault() ?? "";

    internal static string[] Candidates(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
        => Sources(song, platforms, enabled).Select(value => value.Address).Distinct(StringComparer.Ordinal).ToArray();

    internal static (string SourceKey, string Address)[] Sources(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
    {
        try { return Sources(MusicWireCodec.ReadSong(song), platforms, enabled); }
        catch { return []; }
    }

    internal static (string SourceKey, string Address)[] Sources(MusicSongRecord song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
    {
        try
        {
            var current = NativeMusicSong.Normalize(song);
            var addresses = new List<(string, string)>();
            foreach (string platform in platforms)
            {
                if (!enabled.Contains(platform)) continue;
                foreach (var src in current.Sources.Where(row => row.Platform == platform))
                {
                    string address = PublicUrl(NativeMusicSong.Display(src).Cover);
                    if (address.Length != 0) addresses.Add((src.SourceKey, address));
                }
            }
            return addresses.ToArray();
        }
        catch { return []; }
    }

    internal static string PublicUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        ? uri.AbsoluteUri : "";
}
