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
        bool changedSong = artworkSongId != current?.Id;
        artworkSongId = current?.Id;
        if (changedSong) Changed(nameof(CurrentArtworkIdentity));
        string next = CurrentArtwork;
        if (currentArtwork == next) return;
        currentArtwork = next;
        Changed(nameof(CurrentArtwork));
    }

    private static IEnumerable<MusicCatalogCard> RepositoryCards(IEnumerable<MusicCatalogSection> sections) =>
        sections.SelectMany(section => section.Items.Concat(section.Action is { } action ? [action] : [])
            .Concat(section.Tabs?.SelectMany(tab => tab.Items) ?? []));

    private void RepositorySongsChanged(IReadOnlySet<string> ids)
    {
        if (Presentation.Dispatcher.IsClosing) return;
        _ = Presentation.Dispatcher.EnqueueAsync(() =>
        {
            if (stopping || disposed || application is not MusicNativeApplication owner) return;
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loadDrains.Add(drained.Task);
            _ = Refresh();
            async Task Refresh()
            {
                try
                {
                    var snapshot = await owner.SongSnapshotsAsync(ids.ToArray(), lifetime.Token);
                    if (stopping || disposed) return;
                    JsonElement? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
                    foreach (var song in searchSongs.Concat(catalogSongs).Append(current).Append(selectedSong).OfType<SongRow>()
                        .Where(song => ids.Contains(song.Id)))
                        song.ApplyRepository(Read(song.Id), snapshot.Publication);
                    var sections = homeSections.Concat(homeDisplaySections).Concat(channelSections).Concat(utilitySections)
                        .Concat(searchSections.Values.SelectMany(value => value))
                        .Concat(catalogPages.Values.SelectMany(page => page.Sections))
                        .Concat(catalogLocations.Values.SelectMany(saved => saved.Pages.Values).SelectMany(page => page.Sections));
                    var cards = RepositoryCards(sections)
                        .Concat(searchCardCache.Values.SelectMany(value => value.Cards)).Concat(ChannelTabs)
                        .Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance);
                    foreach (var card in cards)
                    {
                        if (card.Song is { } song && ids.Contains(song.Id)) card.RefreshSong(Read(song.Id), snapshot.Publication);
                        if (card.PreviewSongs.Any(preview => preview.Song is { } song && ids.Contains(song.Id)))
                            card.RefreshPreview(Read, snapshot.Publication);
                    }
                    foreach (var row in queueSurfaceRows.Where(row => ids.Contains(row.Song.Id))) row.RefreshSong(Read(row.Song.Id), snapshot.Publication);
                    foreach (var row in RecommendationCandidates.Where(row => ids.Contains(row.Song.Id))) row.RefreshSong(Read(row.Song.Id), snapshot.Publication);
                    foreach (var row in DiscoveryCharts.SelectMany(chart => chart.Songs).Where(row => ids.Contains(row.Song.Id)))
                        row.RefreshSong(Read(row.Song.Id), snapshot.Publication);
                    Changed(nameof(CollectionArtwork)); Changed(nameof(DetailArtwork));
                }
                catch (OperationCanceledException) { }
                catch (MusicOperationException error) when (stopping && error.Code == "CLOSED") { }
                catch (Exception error) { if (!stopping) await Notify(ErrorText(error)); }
                finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); }
            }
        });
    }

    private async Task RefreshCatalogSongsAsync(IEnumerable<MusicCatalogCard> cards, CancellationToken token)
    {
        if (application is not MusicNativeApplication owner) return;
        var rows = cards.Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance).ToArray();
        var ids = rows.SelectMany(card => card.PreviewSongs.Select(preview => preview.Song).Append(card.Song))
            .OfType<SongRow>().Select(song => song.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        var snapshot = await owner.SongSnapshotsAsync(ids, token);
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        JsonElement? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
        foreach (var card in rows)
        {
            if (card.Song is { } song) card.RefreshSong(Read(song.Id), snapshot.Publication);
            card.RefreshPreview(Read, snapshot.Publication);
        }
    }
}
