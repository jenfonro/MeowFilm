using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class FilmPlaybackPage
{
    // Successful directory presentation only; no playback stages, attempts or media receipts.
    private sealed record BrowsingSlice(JsonElement Metadata, JsonElement Source, string Kind,
        JsonElement Browsing,
        JsonElement CandidateRows, string Summary, FilmEpisodeDirectory? Directory, long Revision);
    private readonly Dictionary<(string? Source, string? Metadata), DirectoryKey> rememberedSelections = [];
    private readonly HashSet<DirectoryKey> unreadDirectories = [];
    private BrowsingSlice? display;
    private DirectoryKey? selection;
    private string? aggregateProvider;
    private long metadataChoiceGeneration;
    private bool selectionLocked, explicitRead, preservingRead, preservingRefresh, browsingLoading = true;
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

    private void UpdateBrowsing(FilmPlaybackSnapshot value, bool sourcesChanged)
    {
        var key = Key(value);
        bool loading = FilmJson.Bool(value.Browsing, "loading");
        string error = FilmJson.Text(value.Browsing, "error");
        bool refreshing = FilmJson.Bool(value.Browsing, "refreshing");
        directories.TryGetValue(key, out var previous);
        if (error.Length > 0 || refreshing) unreadDirectories.Add(key);
        if (loading || error.Length > 0)
        {
            if (!Matches(key)) return;
            bool sameFiles = key.Source is null || previous is not null &&
                new[] { "lines", "episodes", "originalEpisodes" }.All(name =>
                    FilmJson.Equal(FilmJson.Member(previous.Browsing, name), FilmJson.Member(value.Browsing, name)));
            if (error.Length > 0 && previous is not null && key == selection && EpisodeDirectory is not null && sameFiles)
            {
                // A failed refresh is a failed operation, not a replacement for the last successful display.
                browsingLoading = explicitRead = false;
                browsingError = "";
                return;
            }
            // A disconnected execution and a failed refresh do not own the last successful display.
            if (preservingRead && !explicitRead && EpisodeDirectory is not null && !refreshing)
            {
                if (!loading) preservingRead = false;
                return;
            }
            browsingLoading = loading;
            browsingError = error;
            preservingRefresh = loading && refreshing && key == selection && EpisodeDirectory is not null;
            if (EpisodeDirectory is null)
            {
                selection = key;
                display = new(value.Metadata, value.SelectedMetadataSource, value.ContentKind,
                    value.Browsing, default, Overview, null, metadataRevision);
            }
            return;
        }

        var rows = FilmJson.Member(key.Source is null ? value.Raw : value.Browsing, "episodes");
        var candidates = key.Source is null ? FilmJson.Member(value.Raw, "candidates") : rows;
        var slice = new BrowsingSlice(OwnProjection(value.Metadata, previous?.Metadata ?? default),
            OwnProjection(value.SelectedMetadataSource, previous?.Source ?? default), value.ContentKind,
            OwnProjection(value.Browsing, previous?.Browsing ?? default),
            OwnProjection(candidates, previous?.CandidateRows ?? default), Overview, previous?.Directory,
            previous is null || !FilmJson.Equal(previous.Metadata, value.Metadata) ||
                previous.Summary != Overview ? ++metadataRevision : previous.Revision);
        var directory = slice.Directory ?? new FilmEpisodeDirectory();
        var seasons = FilmJson.Member(slice.Metadata, "doubanSeasons");
        if (seasons.ValueKind != JsonValueKind.Array) seasons = FilmJson.Member(slice.Metadata, "seasons");
        string poster = FilmJson.Text(slice.Metadata, "poster_path");
        if (poster.Length == 0) poster = FilmJson.Text(FilmJson.Member(slice.Metadata, "pic"), "normal");
        string sourceName = "", provider = "";
        if (key.Source is { } sourceId)
        {
            var source = FilmJson.Array(value.Raw, "sources").FirstOrDefault(row => FilmJson.Text(row, "id") == sourceId);
            sourceName = FilmJson.Text(FilmJson.Member(source, "source"), "name");
            if (sourceName.Length == 0) sourceName = FilmJson.Text(source, "title");
            var line = FilmJson.Array(slice.Browsing, "lines").FirstOrDefault(row =>
                FilmJson.Equal(FilmJson.Member(row, "id"), FilmJson.Member(slice.Browsing, "lineId")));
            provider = FilmJson.Text(line, "provider");
        }
        directory.Apply(poster.Length > 0 ? poster : Item.Poster, Overview, rows, seasons,
            slice.CandidateRows, slice.Revision, key.Source is not null, slice.Kind == "movie", sourceName, provider,
            previous is null || !FilmJson.Equal(previous.CandidateRows, slice.CandidateRows) ||
                !FilmJson.Equal(FilmJson.Member(previous.Browsing, "lines"), FilmJson.Member(slice.Browsing, "lines")) ||
                key.Source is not null && sourcesChanged);
        slice = slice with { Directory = directory };
        directories[key] = slice;
        unreadDirectories.Remove(key);
        rememberedSelections[(key.Source, key.Metadata)] = key;
        if (!Matches(key)) return;
        selection = key; display = slice; EpisodeDirectory = slice.Directory;
        browsingError = "";
        browsingLoading = preservingRefresh = explicitRead = preservingRead = false;
    }

    private void CancelBrowseRead()
    {
        browseRevision++;
        browseRead?.Cancel();
        browseRead = null;
    }

    private async Task BrowseAsync(DirectoryKey key, bool metadataSelection = false)
    {
        if (key == selection && !browsingLoading && browsingError.Length == 0 && !unreadDirectories.Contains(key)) return;
        CancelBrowseRead();
        long revision = browseRevision;
        bool wasApplying = Applying;
        Applying = true;
        try
        {
            bool reuseDisplay = key == selection && EpisodeDirectory is not null;
            selectionLocked = true; preservingRead = preservingRefresh = false; selection = key; browsingError = "";
            if (!unreadDirectories.Contains(key) && directories.TryGetValue(key, out var loaded))
            {
                display = loaded; EpisodeDirectory = loaded.Directory;
                browsingLoading = false; explicitRead = false;
                ProjectBrowsing();
                return;
            }
            browsingLoading = true; explicitRead = true;
            if (!reuseDisplay)
            {
                // Keep the same site's line menu while reading an unparsed line.
                var lines = display is not null && FilmJson.Text(display.Browsing, "sourceId") == (key.Source ?? "")
                    ? FilmJson.Array(display.Browsing, "lines") : [];
                var browsing = FilmJson.Encode(new { sourceId = key.Source, lineId = key.Line.Length > 0
                    ? JsonSerializer.Deserialize<JsonElement>(key.Line) : (JsonElement?)null, lines,
                    season = key.Season, loading = true });
                display = new(DisplayMetadata, DisplaySource, DisplayKind, browsing,
                    default, Overview, null, metadataRevision);
                EpisodeDirectory = null;
            }
            ProjectBrowsing();
        }
        finally { Applying = wasApplying; }
        try
        {
            using var read = new CancellationTokenSource();
            browseRead = read;
            await ReadBrowsingAsync(key, read.Token, metadataSelection);
        }
        catch (Exception error)
        {
            if (revision != browseRevision) throw new OperationCanceledException();
            bool retained = !explicitRead && EpisodeDirectory is not null && browsingError.Length == 0;
            browsingLoading = preservingRefresh = explicitRead = false;
            if (!retained)
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
        token.ThrowIfCancellationRequested();
        await owner.WaitForBrowsingAsync(this, state => Matches(Key(state)), token);
    }

    private void ProjectBrowsing()
    {
        bool wasApplying = Applying;
        Applying = true;
        try
        {
            // Reuse the same projection path without changing the execution snapshot.
            if (snapshot is { } latest) Apply(latest, updateBrowsing: false);
            else Changed("");
            Changed(nameof(EpisodeDirectory)); Changed(nameof(Episodes)); Changed(nameof(Candidates));
        }
        finally { Applying = wasApplying; }
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
