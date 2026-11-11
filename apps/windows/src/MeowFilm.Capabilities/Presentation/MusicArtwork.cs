using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>Artwork is selected from this recording's admitted metadata, not its delivery/LX target.</summary>
public static class MusicArtworkPolicy
{
    public static string Select(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
        => Candidates(song, platforms, enabled).FirstOrDefault() ?? "";

    internal static string[] Candidates(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
    {
        try
        {
            if (song.ValueKind != JsonValueKind.Object || song.GetRawText().Length > 262144) return [];
            var current = NativeMusicSong.Normalize(JsonNode.Parse(song.GetRawText()));
            var sources = current["sources"]!.AsArray();
            var addresses = new List<string>();
            foreach (string platform in platforms)
            {
                if (!enabled.Contains(platform)) continue;
                foreach (var node in sources.OfType<JsonObject>()
                    .Where(row => LJ.S(row["source"]) == platform).Take(2))
                {
                    string address = PublicUrl(NativeMusicSong.Display(node).Cover);
                    if (address.Length != 0 && !addresses.Contains(address)) addresses.Add(address);
                }
            }
            return addresses.ToArray();
        }
        catch { return []; }
    }

    internal static string PublicUrl(string value) => value.Length <= 8192 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri.AbsoluteUri : "";
}

public sealed partial class MusicCapability
{
    private string[] savedArtworkPlatforms = [];
    private HashSet<string> savedArtworkEnabled = new(StringComparer.Ordinal);
    private bool artworkPolicyKnown;
    private string currentArtwork = "";
    private string? artworkEntryId, artworkSongId;
    private readonly HashSet<string> failedArtworkAddresses = new(StringComparer.Ordinal);
    public string CurrentArtwork => !stopping && artworkPolicyKnown ? currentArtwork : "";

    private void RefreshCurrentArtwork(bool force = false)
    {
        if (!artworkPolicyKnown) return;
        bool changedSong = artworkEntryId != current?.EntryId || artworkSongId != current?.Id;
        if (changedSong || force) failedArtworkAddresses.Clear();
        string[] addresses = current is null ? [] :
            MusicArtworkPolicy.Candidates(PlaybackSong(current), savedArtworkPlatforms, savedArtworkEnabled);
        string retained = changedSong || force ? "" : currentArtwork;
        string next;
        if (addresses.Contains(retained) && !failedArtworkAddresses.Contains(retained))
            next = retained;
        else
        {
            string initial = changedSong && !force && current is not null ? MusicArtworkPolicy.PublicUrl(current.Cover) : "";
            next = addresses.Contains(initial) ? initial :
                addresses.FirstOrDefault(address => !failedArtworkAddresses.Contains(address)) ??
                // Keep an exhausted failure visible for the image's explicit retry action.
                (addresses.Contains(retained) ? retained : "");
        }
        artworkEntryId = current?.EntryId; artworkSongId = current?.Id;
        if (currentArtwork == next) return;
        currentArtwork = next;
        Changed(nameof(CurrentArtwork));
    }

    internal void ArtworkLoadCompleted(string address, bool succeeded)
    {
        if (stopping || !artworkPolicyKnown || current is null || address != currentArtwork) return;
        if (succeeded) failedArtworkAddresses.Remove(address);
        else if (failedArtworkAddresses.Add(address)) RefreshCurrentArtwork();
    }
}
