using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>Artwork is selected from this recording's admitted metadata, not its delivery/LX target.</summary>
public static class MusicArtworkPolicy
{
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
