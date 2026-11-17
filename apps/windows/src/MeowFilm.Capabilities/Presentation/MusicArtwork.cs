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
        => Sources(song, platforms, enabled).Select(value => value.Address).Distinct(StringComparer.Ordinal).ToArray();

    internal static (string SourceKey, string Address)[] Sources(JsonElement song, IReadOnlyList<string> platforms, IReadOnlySet<string> enabled)
    {
        try
        {
            var current = NativeMusicSong.Normalize(JsonNode.Parse(song.GetRawText()));
            var sources = current["sources"]!.AsArray();
            var addresses = new List<(string, string)>();
            foreach (string platform in platforms)
            {
                if (!enabled.Contains(platform)) continue;
                foreach (var node in sources.OfType<JsonObject>()
                    .Where(row => LJ.S(row["source"]) == platform))
                {
                    string address = PublicUrl(NativeMusicSong.Display(node).Cover);
                    if (address.Length != 0) addresses.Add((LJ.S(node["sourceKey"]), address));
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

public sealed partial class MusicCapability
{
    private string currentArtwork = "";
    private string? artworkSongId;
    public string CurrentArtwork => !stopping ? current?.Cover ?? "" : "";
    public string CurrentArtworkIdentity => current?.Id ?? "";

    private void RefreshCurrentArtwork()
    {
        if (current is not null && application is MusicNativeApplication owner)
            current.ApplyRepository(owner.SongSnapshot(current.Id));
        bool changedSong = artworkSongId != current?.Id;
        artworkSongId = current?.Id;
        if (changedSong) Changed(nameof(CurrentArtworkIdentity));
        string next = current?.Cover ?? "";
        if (currentArtwork == next) return;
        currentArtwork = next;
        Changed(nameof(CurrentArtwork));
    }

    private void RepositorySongsChanged(IReadOnlySet<string> ids)
    {
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return;
        _ = Presentation.Dispatcher.InvokeAsync(() =>
        {
            if (stopping || disposed || application is not MusicNativeApplication owner) return;
            var snapshots = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            JsonElement? Read(string id)
            {
                if (!ids.Contains(id)) return null;
                if (!snapshots.TryGetValue(id, out var value)) snapshots[id] = value = owner.SongSnapshot(id);
                return value;
            }
            foreach (var song in searchSongs.Concat(catalogSongs).Append(current).Append(selectedSong).OfType<SongRow>()
                .Where(song => ids.Contains(song.Id)))
                song.ApplyRepository(Read(song.Id));
            var cards = homeSections.Concat(homeDisplaySections).Concat(channelSections).Concat(utilitySections)
                .Concat(searchSections.Values.SelectMany(value => value))
                .SelectMany(section => section.Items.Concat(section.Action is { } action ? [action] : []))
                .Concat(searchCardCache.Values.SelectMany(value => value.Cards)).Concat(ChannelTabs)
                .Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance);
            foreach (var card in cards)
            {
                if (card.Song is { } song && ids.Contains(song.Id)) card.RefreshSong(Read(song.Id));
                if (card.PreviewSongs.Any(preview => preview.Song is { } song && ids.Contains(song.Id)))
                    card.RefreshPreview(Read);
            }
            foreach (var row in queueSurfaceRows.Where(row => ids.Contains(row.Song.Id))) row.RefreshSong(Read(row.Song.Id));
            foreach (var row in RecommendationCandidates.Where(row => ids.Contains(row.Song.Id))) row.RefreshSong(Read(row.Song.Id));
            foreach (var row in DiscoveryCharts.SelectMany(chart => chart.Songs).Where(row => ids.Contains(row.Song.Id)))
                row.RefreshSong(Read(row.Song.Id));
            if (current is not null && ids.Contains(current.Id)) RefreshCurrentArtwork();
            RefreshBindings();
        });
    }

    private void RefreshCatalogSongs(IEnumerable<MusicCatalogCard> cards)
    {
        if (application is not MusicNativeApplication owner) return;
        foreach (var card in cards)
        {
            if (card.Song is { } song) card.RefreshSong(owner.SongSnapshot(song.Id));
            card.RefreshPreview(owner.SongSnapshot);
        }
    }
}
