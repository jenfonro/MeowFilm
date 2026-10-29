using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>Artwork is selected from this recording's admitted metadata, not its delivery/LX target.</summary>
public static class MusicArtworkPolicy
{
    public static string Select(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
    {
        try
        {
            if (song.ValueKind != JsonValueKind.Object || song.GetRawText().Length > 262144) return "";
            var current = NativeMusicSong.Normalize(JsonNode.Parse(song.GetRawText()));
            var sources = current["sources"]!.AsArray();
            foreach (string platform in platforms)
            {
                if (!enabled.Contains(platform)) continue;
                foreach (var node in sources.OfType<JsonObject>()
                    .Where(row => LJ.S(row["source"]) == platform).Take(2))
                {
                    var row = LJ.Element(node);
                    var data = row.GetProperty("source_data");
                    string address = PublicUrl(Text(row, "cover_url"));
                    if (address.Length == 0) address = PublicUrl(Text(data, "img"));
                    if (address.Length != 0) return address;
                }
            }
            return "";
        }
        catch { return ""; }
    }
    private static string Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? "" : "";
    private static string PublicUrl(string value) => value.Length <= 8192 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri.AbsoluteUri : "";
}

public sealed partial class MusicCapability
{
    private string[] savedArtworkPlatforms = [];
    private HashSet<string> savedArtworkEnabled = new(StringComparer.Ordinal);
    private bool artworkPolicyKnown;
    public string CurrentArtwork => !stopping && artworkPolicyKnown && current is not null
        ? MusicArtworkPolicy.Select(current.Raw, savedArtworkPlatforms, savedArtworkEnabled) : "";
}
