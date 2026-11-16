using System.ComponentModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

// Presentation state only. The runtime remains the owner of directory validity and data.
public sealed class FilmEpisodeDirectory
{
    private JsonElement rows;
    private long metadataRevision = -1;
    private FilmEpisodeItem? selectedEpisode;
    private FilmCandidateItem? selectedCandidate;
    public IReadOnlyList<FilmEpisodeItem> Episodes { get; private set; } = [];
    public IReadOnlyList<FilmCandidateItem> Candidates { get; private set; } = [];
    public double HorizontalOffset { get; set; }
    internal double VerticalOffset { get; set; }

    internal void Apply(FilmPlaybackPage page, JsonElement nextRows, JsonElement nextSeasons,
        JsonElement nextCandidates, long revision, bool raw, bool movie, bool refreshCandidates)
    {
        bool rowsChanged = !FilmJson.Equal(rows, nextRows), metadataChanged = metadataRevision != revision;
        bool candidatesChanged = false;
        if (rowsChanged || metadataChanged || refreshCandidates)
        {
            var visible = raw ? FilmJson.Array(nextRows).Select(row => FilmJson.Text(row, "candidateId"))
                .ToHashSet(StringComparer.Ordinal) : null;
            var duplicates = new Dictionary<string, int>(StringComparer.Ordinal);
            var nextChoices = FilmJson.Array(nextCandidates).Where(row => visible is null || visible.Contains(FilmJson.Text(row, "id")))
                .Select(row =>
                {
                    var candidate = FilmCandidateItem.Read(row) with { Image = page.ContentPoster, Overview = page.Overview };
                    if (!movie) return candidate;
                    int count = duplicates.GetValueOrDefault(candidate.DisplayLabel) + 1;
                    duplicates[candidate.DisplayLabel] = count;
                    return count == 1 ? candidate : candidate with { DisplayLabel = $"{candidate.DisplayLabel} ({count})" };
                }).ToArray();
            var previousChoices = Candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
            for (int index = 0; index < nextChoices.Length; index++)
                if (previousChoices.TryGetValue(nextChoices[index].Id, out var existing) && existing.SameDisplay(nextChoices[index]))
                    nextChoices[index] = existing;
            if (!Candidates.SequenceEqual(nextChoices, ReferenceEqualityComparer.Instance))
            { Candidates = nextChoices; candidatesChanged = true; }
        }
        if (!rowsChanged && !metadataChanged)
        {
            if (candidatesChanged) UpdateCandidateFields();
            return;
        }
        rows = FilmJson.Copy(nextRows);
        metadataRevision = revision;
        var details = new Dictionary<(int? Season, int? Episode), JsonElement>();
        var posters = new Dictionary<int, string>();
        foreach (var season in FilmJson.Array(nextSeasons))
        {
            int? number = FilmJson.Integer(season, "season_number") ?? FilmJson.Integer(season, "season");
            if (number is null) continue;
            posters[number.Value] = FilmJson.Text(season, "poster_path");
            foreach (var episode in FilmJson.Array(season, "episodes"))
                details[(number, FilmJson.Integer(episode, "episode_number") ?? FilmJson.Integer(episode, "episode"))] = episode;
        }
        var previous = Episodes.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var next = FilmJson.Array(rows).Select(row =>
        {
            string identity = FilmEpisodeItem.IdentityOf(row);
            if (!previous.TryGetValue(identity, out var item)) item = new(raw, identity);
            int? season = FilmJson.Integer(row, "season") ?? FilmJson.Integer(row, "season_number");
            int? episode = FilmJson.Integer(row, "episode") ?? FilmJson.Integer(row, "episode_number");
            item.Apply(row, raw ? details.GetValueOrDefault((season, episode)) : row,
                season is { } value ? posters.GetValueOrDefault(value, "") : "", page.ContentPoster, page.Overview);
            return item;
        }).ToArray();
        if (!Episodes.SequenceEqual(next)) Episodes = next;
        UpdateCandidateFields();
    }

    private void UpdateCandidateFields()
    {
        var candidates = Candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in Episodes)
            item.SetCandidate(item.CandidateId is { } id ? candidates.GetValueOrDefault(id) : null);
    }

    internal void Select(FilmPlaybackPage page)
    {
        var nextEpisode = selectedEpisode is not null && Episodes.Contains(selectedEpisode) && page.Selected(selectedEpisode)
            ? selectedEpisode : Episodes.FirstOrDefault(page.Selected);
        if (!ReferenceEquals(nextEpisode, selectedEpisode))
        {
            selectedEpisode?.Select(false);
            nextEpisode?.Select(true);
            selectedEpisode = nextEpisode;
        }
        var nextCandidate = Candidates.FirstOrDefault(item => item.Id == page.CurrentCandidateId);
        if (!ReferenceEquals(nextCandidate, selectedCandidate))
        {
            selectedCandidate?.Select(false);
            nextCandidate?.Select(true);
            selectedCandidate = nextCandidate;
        }
    }
}

public sealed class FilmEpisodeItem : INotifyPropertyChanged
{
    private sealed record Display(int? Season, int? Number, int? GlobalEpisode, string? CandidateId,
        string OriginalName, string Title, string Overview, string Image, string Duration, string Path);
    private readonly bool raw;
    private Display display = new(null, null, null, null, "", "", "", "", "", "");
    private bool selected;
    private string quality = "", provider = "";
    internal FilmEpisodeItem(bool raw, string identity) { this.raw = raw; Identity = identity; }
    internal string Identity { get; }
    public FilmPlaybackTarget? PlaybackTarget { get; private set; }
    internal static string IdentityOf(JsonElement row) => FilmJson.RequiredText(row, "businessId");

    internal void Apply(JsonElement row, JsonElement detail, string seasonPoster, string poster, string overview)
    {
        PlaybackTarget = FilmJson.Target(row.GetProperty("playbackTarget"));
        PlaybackTarget.Validate();
        int? season = FilmJson.Integer(row, "season") ?? FilmJson.Integer(row, "season_number");
        int? episode = FilmJson.Integer(row, "episode") ?? FilmJson.Integer(row, "episode_number");
        int? global = FilmJson.Integer(row, "globalEpisode");
        if (!raw && (season is null or < 0 || episode is null or < 0 ||
            (season == 0 || episode == 0) && global is not null))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        string still = FilmJson.Text(detail, "still_path");
        if (still.Length == 0) still = FilmJson.Text(row, "still_path");
        var next = new Display(season, episode, global,
            raw && FilmJson.Text(row, "candidateId") is { Length: > 0 } candidateId ? candidateId : null,
            FilmJson.Text(row, "name"), FilmJson.Text(detail, "name"),
            FilmJson.Text(detail, "overview") is { Length: > 0 } summary ? summary :
                FilmJson.Text(row, "overview") is { Length: > 0 } original ? original : overview,
            still.Length > 0 ? still : seasonPoster.Length > 0 ? seasonPoster : poster,
            (FilmJson.Integer(detail, "runtime") ?? FilmJson.Integer(row, "runtime")) is > 0 and var minutes ? $"{minutes}分钟" : "",
            FilmJson.Text(row, "path"));
        if (display == next) return;
        var old = display; display = next;
        if (old.Season != next.Season) Changed(nameof(Season));
        if (old.Number != next.Number) Changed(nameof(Number));
        if (old.GlobalEpisode != next.GlobalEpisode) Changed(nameof(GlobalEpisode));
        if (old.CandidateId != next.CandidateId) { Changed(nameof(CandidateId)); Changed(nameof(CanSelect)); }
        if (old.OriginalName != next.OriginalName) Changed(nameof(OriginalName));
        if (old.Title != next.Title) Changed(nameof(Title));
        if (old.Overview != next.Overview) Changed(nameof(Overview));
        if (old.Image != next.Image) Changed(nameof(Image));
        if (old.Duration != next.Duration) Changed(nameof(Duration));
        if (old.Path != next.Path) Changed(nameof(Path));
        if (old.Number != next.Number || old.GlobalEpisode != next.GlobalEpisode ||
            old.OriginalName != next.OriginalName || old.Title != next.Title)
        {
            Changed(nameof(EpisodeNumberLabel)); Changed(nameof(HasEpisodeTitle)); Changed(nameof(Label));
        }
    }
    internal void SetCandidate(FilmCandidateItem? candidate)
    {
        string nextQuality = candidate?.Quality ?? "", nextProvider = candidate?.Provider ?? "";
        if (quality != nextQuality) { quality = nextQuality; Changed(nameof(Quality)); }
        if (provider != nextProvider) { provider = nextProvider; Changed(nameof(Provider)); }
    }
    internal void Select(bool value)
    {
        if (selected == value) return;
        selected = value; Changed(nameof(IsSelected));
    }
    public int? Season => display.Season;
    public int? Number => display.Number;
    public int? GlobalEpisode => display.GlobalEpisode;
    public string? CandidateId => display.CandidateId;
    public string OriginalName => display.OriginalName;
    public string Title => display.Title;
    public string EpisodeNumberLabel => (Number ?? GlobalEpisode) is { } number ? $"第{number}集" : OriginalName;
    public bool HasEpisodeTitle => Number is not null && Title.Length > 0;
    public string Quality => quality;
    public string Provider => provider;
    public string Overview => display.Overview;
    public string Image => display.Image;
    public string Duration => display.Duration;
    public string Path => display.Path;
    public string Label => Number is null && GlobalEpisode is null ? OriginalName :
        HasEpisodeTitle ? $"{EpisodeNumberLabel} · {Title}" : EpisodeNumberLabel;
    public bool CanSelect => raw ? CandidateId is not null : Season is >= 0 && Number is >= 0;
    public bool IsSelected => selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
