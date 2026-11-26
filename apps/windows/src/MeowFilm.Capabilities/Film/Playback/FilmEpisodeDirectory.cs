using System.ComponentModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

/// <summary>Presentation projection of the runtime-normalized directory snapshot.</summary>
public sealed class FilmEpisodeDirectory : INotifyPropertyChanged
{
    private string mode = "";
    private JsonElement rows;
    private string currentPoster = "";
    private string currentOverview = "";
    private FilmEpisodeItem? selectedEpisode;
    private FilmCandidateItem? selectedCandidate;

    public IReadOnlyList<FilmEpisodeItem> Episodes { get; private set; } = [];
    public IReadOnlyList<FilmCandidateItem> Candidates { get; private set; } = [];
    public double HorizontalOffset { get; set; }
    internal double VerticalOffset { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(string nextMode, JsonElement nextRows, string poster, string overview)
    {
        bool sameInputs = mode == nextMode && currentPoster == poster &&
            currentOverview == overview && FilmJson.Equal(rows, nextRows);
        if (sameInputs) return;

        mode = nextMode;
        currentPoster = poster;
        currentOverview = overview;
        rows = FilmJson.Copy(nextRows);

        if (nextMode == "movies")
        {
            var duplicates = new Dictionary<string, int>(StringComparer.Ordinal);
            var previousChoices = Candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var nextChoices = FilmJson.Array(rows).Select(row =>
            {
                var candidate = FilmCandidateItem.Read(row, poster, overview);
                int count = duplicates.GetValueOrDefault(candidate.DisplayLabel) + 1;
                duplicates[candidate.DisplayLabel] = count;
                if (count > 1) candidate = candidate with { DisplayLabel = $"{candidate.DisplayLabel} ({count})" };
                if (previousChoices.TryGetValue(candidate.Id, out var existing) && existing.SameDisplay(candidate))
                    candidate = existing;
                candidate.Select(FilmJson.Bool(row, "active"));
                return candidate;
            }).ToArray();

            selectedCandidate = nextChoices.FirstOrDefault(c => c.IsSelected);
            if (!Candidates.SequenceEqual(nextChoices, ReferenceEqualityComparer.Instance))
            {
                Candidates = nextChoices;
                PropertyChanged?.Invoke(this, new(nameof(Candidates)));
            }
            if (Episodes.Count > 0)
            {
                selectedEpisode?.Select(false);
                selectedEpisode = null;
                Episodes = [];
                PropertyChanged?.Invoke(this, new(nameof(Episodes)));
            }
            return;
        }

        if (nextMode is "episodes" or "files")
        {
            var previous = Episodes.ToDictionary(item => item.Identity, StringComparer.Ordinal);
            var next = FilmJson.Array(rows).Select(row =>
            {
                string identity = FilmJson.RequiredText(row, "id");
                if (!previous.TryGetValue(identity, out var item)) item = new(identity);
                item.Apply(row, poster);
                return item;
            }).ToArray();

            selectedEpisode = next.FirstOrDefault(e => e.IsSelected);
            if (!Episodes.SequenceEqual(next, ReferenceEqualityComparer.Instance))
            {
                Episodes = next;
                PropertyChanged?.Invoke(this, new(nameof(Episodes)));
            }
            if (Candidates.Count > 0)
            {
                selectedCandidate?.Select(false);
                selectedCandidate = null;
                Candidates = [];
                PropertyChanged?.Invoke(this, new(nameof(Candidates)));
            }
            return;
        }

        if (Episodes.Count > 0)
        {
            selectedEpisode?.Select(false);
            selectedEpisode = null;
            Episodes = [];
            PropertyChanged?.Invoke(this, new(nameof(Episodes)));
        }
        if (Candidates.Count > 0)
        {
            selectedCandidate?.Select(false);
            selectedCandidate = null;
            Candidates = [];
            PropertyChanged?.Invoke(this, new(nameof(Candidates)));
        }
    }
}

public sealed class FilmEpisodeItem : INotifyPropertyChanged
{
    private sealed record Display(int? Season, int? Number, string NumberLabel, string Title,
        string FileName, string Overview, string Image, string Duration, string Path, string Quality);
    private Display display = new(null, null, "", "", "", "", "", "", "", "");
    private bool selected;

    internal FilmEpisodeItem(string identity) { Identity = identity; }
    public string Identity { get; }

    internal void Apply(JsonElement row, string fallbackPoster)
    {
        int? season = FilmJson.Integer(row, "season");
        int? episode = FilmJson.Integer(row, "episode");
        string numberLabel = FilmJson.Text(row, "numberLabel");
        string title = FilmJson.Text(row, "title");
        string fileName = FilmJson.Text(row, "fileName");
        string summary = FilmJson.Text(row, "summary");
        string still = FilmJson.Text(row, "still");
        string duration = FilmJson.Text(row, "duration");
        string path = FilmJson.Text(row, "path");
        string quality = FilmJson.Text(row, "quality");
        bool active = FilmJson.Bool(row, "active");

        var next = new Display(
            season,
            episode,
            numberLabel,
            title.Length > 0 ? title : fileName,
            fileName,
            summary,
            still.Length > 0 ? still : fallbackPoster,
            duration,
            path,
            quality);

        if (display != next)
        {
            var old = display;
            display = next;
            if (old.Season != next.Season) Changed(nameof(Season));
            if (old.Number != next.Number) Changed(nameof(Number));
            if (old.NumberLabel != next.NumberLabel) Changed(nameof(EpisodeNumberLabel));
            if (old.Title != next.Title) { Changed(nameof(Title)); Changed(nameof(Label)); }
            if (old.FileName != next.FileName) Changed(nameof(OriginalName));
            if (old.Overview != next.Overview) Changed(nameof(Overview));
            if (old.Image != next.Image) Changed(nameof(Image));
            if (old.Duration != next.Duration) { Changed(nameof(Duration)); Changed(nameof(HasDuration)); }
            if (old.Path != next.Path) Changed(nameof(Path));
            if (old.Quality != next.Quality) Changed(nameof(Quality));
        }

        Select(active);
    }

    internal void Select(bool value)
    {
        if (selected == value) return;
        selected = value;
        Changed(nameof(IsSelected));
    }

    public int? Season => display.Season;
    public int? Number => display.Number;
    public string OriginalName => display.FileName;
    public string Title => display.Title;
    public string EpisodeNumberLabel => display.NumberLabel.Length > 0 ? $"第{display.NumberLabel}集" : display.Title;
    public string Label => display.Title;
    public string Quality => display.Quality;
    public string Overview => display.Overview;
    public string Image => display.Image;
    public string Duration => display.Duration;
    public bool HasDuration => display.Duration.Length > 0;
    public string Path => display.Path;
    public bool CanSelect => Identity.Length > 0;
    public bool IsSelected => selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
