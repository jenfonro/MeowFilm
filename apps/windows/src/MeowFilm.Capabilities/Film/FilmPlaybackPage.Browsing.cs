using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class FilmPlaybackPage
{
    // Successful directory presentation only; no playback stages, attempts or media receipts.
    private sealed record BrowsingSlice(JsonElement Metadata, JsonElement Source, string Kind,
        FilmMetadataCache? Cache, JsonElement Browsing,
        JsonElement CandidateRows, string Summary, FilmEpisodeDirectory? Directory, long Revision);
    private readonly Dictionary<(string? Source, string? Metadata), DirectoryKey> rememberedSelections = [];
    private readonly HashSet<DirectoryKey> unreadDirectories = [];
    private BrowsingSlice? display;
    private DirectoryKey? selection;
    private string? aggregateProvider;
    private long metadataChoiceGeneration;
    private bool selectionLocked, explicitRead, preservingRead, browsingLoading = true;
    private string browsingError = "";
    private long browseRevision;
    private CancellationTokenSource? browseRead;

    private static DirectoryKey Key(FilmPlaybackSnapshot value)
    {
        var line = FilmJson.Member(value.Browsing, "lineId");
        return new(FilmJson.Text(value.Browsing, "sourceId") is { Length: > 0 } source ? source : null,
            FilmJson.IsIdentity(line) ? line.GetRawText() : "",
            FilmJson.Text(value.Raw, "metadataSourceId") is { Length: > 0 } metadata ? metadata : null,
            FilmJson.Integer(value.Browsing, "season"));
    }

    private bool Matches(DirectoryKey key) => !selectionLocked || selection is { } selected &&
        selected.Source == key.Source && selected.Metadata == key.Metadata &&
        (selected.Line.Length == 0 || selected.Line == key.Line) &&
        (selected.Season is null || selected.Season == key.Season);

    private static JsonElement OwnProjection(JsonElement value, JsonElement previous) =>
        FilmJson.Equal(value, previous) ? previous : FilmJson.Copy(value);

    private void UpdateBrowsing(FilmPlaybackSnapshot value)
    {
        var key = Key(value);
        bool loading = FilmJson.Bool(value.Browsing, "loading");
        string error = FilmJson.Text(value.Browsing, "error");
        directories.TryGetValue(key, out var previous);
        if (error.Length > 0 || FilmJson.Bool(value.Browsing, "refreshing")) unreadDirectories.Add(key);
        var slice = new BrowsingSlice(OwnProjection(value.Metadata, previous?.Metadata ?? default),
            OwnProjection(value.SelectedMetadataSource, previous?.Source ?? default), value.ContentKind,
            value.MetadataCache, OwnProjection(value.Browsing, previous?.Browsing ?? default),
            OwnProjection(FilmJson.Member(value.Raw, "candidates"), previous?.CandidateRows ?? default),
            value.WorkInfo?.Summary ?? "", previous?.Directory,
            previous is null || !FilmJson.Equal(previous.Metadata, value.Metadata) ||
                previous.Summary != (value.WorkInfo?.Summary ?? "") ? ++metadataRevision : previous.Revision);
        if (!loading && error.Length == 0)
        {
            var directory = slice.Directory ?? new FilmEpisodeDirectory();
            var seasons = FilmJson.Member(slice.Metadata, "doubanSeasons");
            if (seasons.ValueKind != JsonValueKind.Array) seasons = FilmJson.Member(slice.Metadata, "seasons");
            string poster = FilmJson.Text(slice.Metadata, "poster_path");
            if (poster.Length == 0) poster = FilmJson.Text(FilmJson.Member(slice.Metadata, "pic"), "normal");
            directory.Apply(poster.Length > 0 ? poster : Item.Poster, value.WorkInfo?.Summary ?? "",
                FilmJson.Member(key.Source is null ? value.Raw : slice.Browsing, "episodes"), seasons,
                slice.CandidateRows, slice.Revision, key.Source is not null,
                slice.Kind == "movie", previous is null || !FilmJson.Equal(previous.CandidateRows, slice.CandidateRows));
            slice = slice with { Directory = directory };
            directories[key] = slice;
            unreadDirectories.Remove(key);
            rememberedSelections[(key.Source, key.Metadata)] = key;
        }
        if (!Matches(key)) return;
        // A read-only execution rebind is not a new display selection or a refresh.
        if (previous is not null && preservingRead && !explicitRead && (loading || error.Length > 0) &&
            !FilmJson.Bool(value.Browsing, "refreshing"))
        {
            if (!loading) preservingRead = false;
            return;
        }
        selection = key; display = slice;
        EpisodeDirectory = loading || error.Length > 0 ? null : slice.Directory;
        browsingLoading = loading; browsingError = error;
        if (!loading) { explicitRead = false; preservingRead = false; }
    }

    private async Task BrowseAsync(DirectoryKey key, bool metadataSelection = false)
    {
        if (key == selection && !browsingLoading && browsingError.Length == 0 && !unreadDirectories.Contains(key)) return;
        long revision = ++browseRevision;
        browseRead?.Cancel();
        browseRead?.Dispose();
        browseRead = null;
        Applying = true;
        try
        {
            selectionLocked = true; preservingRead = false; selection = key; browsingError = "";
            if (!unreadDirectories.Contains(key) && directories.TryGetValue(key, out var loaded))
            {
                display = loaded; EpisodeDirectory = loaded.Directory;
                browsingLoading = false; explicitRead = false;
                ProjectBrowsing();
                return;
            }
            browsingLoading = true; explicitRead = true;
            // Keep the metadata/line menu for an unparsed line, but not another site's menu.
            var lines = display is not null && FilmJson.Text(display.Browsing, "sourceId") == (key.Source ?? "")
                ? FilmJson.Array(display.Browsing, "lines") : [];
            var browsing = FilmJson.Encode(new { sourceId = key.Source, lineId = key.Line.Length > 0
                ? JsonSerializer.Deserialize<JsonElement>(key.Line) : (JsonElement?)null, lines,
                season = key.Season, loading = true });
            display = new(DisplayMetadata, DisplaySource, DisplayKind, display?.Cache, browsing,
                default, Overview, null, metadataRevision);
            EpisodeDirectory = null;
            ProjectBrowsing();
        }
        finally { Applying = false; }
        try
        {
            using var read = new CancellationTokenSource();
            browseRead = read;
            await ReadBrowsingAsync(key, read.Token, metadataSelection);
        }
        catch (Exception error)
        {
            if (revision != browseRevision) throw new OperationCanceledException();
            browsingLoading = false; explicitRead = false;
            browsingError = error is FilmPlaybackException film ? film.Code :
                error is MeowFilm.Core.Runtime.BridgeException bridge ? bridge.Code :
                error is OperationCanceledException ? "CANCELLED" : "SOURCE_FAILURE";
            ProjectBrowsing();
            throw;
        }
        finally { if (revision == browseRevision) browseRead = null; }
    }

    private async Task ReadBrowsingAsync(DirectoryKey key, CancellationToken token, bool metadataSelection)
    {
        var owner = RequireSession();
        var parameters = new Dictionary<string, object?>
        {
            ["sourceId"] = key.Source, ["metadataSourceId"] = key.Metadata
        };
        if (key.Line.Length > 0) parameters["lineId"] = JsonSerializer.Deserialize<JsonElement>(key.Line);
        if (key.Season is { } season) parameters["season"] = season;
        if (metadataSelection) parameters["keepSource"] = true;
        await CommandAsync(unreadDirectories.Contains(key) ? "browse.refresh" :
            metadataSelection ? "browse.metadata" : "browse", parameters);
        await owner.WaitForBrowsingAsync(this, state => Matches(Key(state)), token);
        ProjectBrowsing();
    }

    private void ProjectBrowsing()
    {
        // Reuse the same projection path without changing the execution snapshot.
        if (snapshot is { } latest) Apply(latest, updateBrowsing: false);
        else Changed("");
        Changed(nameof(EpisodeDirectory)); Changed(nameof(Episodes)); Changed(nameof(Candidates));
    }

    internal Task BrowseSourceAsync(FilmSelection value)
    {
        if (!Sources.Contains(value)) throw new FilmPlaybackException("INVALID_REQUEST");
        string? source = value.MetadataSourceId is null ? value.Id : null;
        if (value.MetadataSourceId is not null)
        {
            aggregateProvider = value.Provider;
            Changed(nameof(SelectedSource));
        }
        string? metadata = MetadataSourceId;
        return BrowseAsync(rememberedSelections.GetValueOrDefault((source, metadata)) ??
            new DirectoryKey(source, "", metadata, source is null ? null : Season));
    }

    internal async Task BrowseMetadataMappingAsync(FilmSelection value)
    {
        if (!MetadataSources.Contains(value) || value.MetadataSourceId is null || value.Provider is not ("tmdb" or "douban"))
            throw new FilmPlaybackException("INVALID_REQUEST");
        long generation = ++metadataChoiceGeneration;
        if (preferences is not null)
            await preferences.SavePreferencesAsync(FilmPreferences.Defaults with { MetadataViewProvider = value.Provider }, "metadataViewProvider");
        if (generation != metadataChoiceGeneration || !MetadataSources.Contains(value)) return;
        await BrowseAsync(rememberedSelections.GetValueOrDefault((SourceId, value.MetadataSourceId)) ??
            new DirectoryKey(SourceId, selection?.Line ?? "", value.MetadataSourceId, null), metadataSelection: true);
    }

    internal Task BrowseLineAsync(FilmSelection value)
    {
        if (!Lines.Contains(value) || value.WireId is null) throw new FilmPlaybackException("INVALID_REQUEST");
        return BrowseAsync(new(SourceId, value.WireId.Value.GetRawText(), MetadataSourceId, Season));
    }

    internal Task BrowseSeasonAsync(FilmSelection value) => !Seasons.Contains(value) || value.Season is null
        ? Task.CompletedTask : BrowseAsync(new(SourceId, selection?.Line ?? "", MetadataSourceId, value.Season));
}
