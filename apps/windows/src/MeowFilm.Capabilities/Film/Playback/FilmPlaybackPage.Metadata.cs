using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities;

public sealed partial class FilmPlaybackPage
{
    internal FilmPlaybackRequest CaptureRequest
    {
        get
        {
            var target = ActiveTarget;
            var browsing = Request.Browsing;
            if (snapshot is not null)
            {
                var selection = new JsonObject { ["sourceId"] = SourceId };
                if (FilmJson.IsIdentity(FilmJson.Member(RawBrowsing, "lineId")))
                    selection["lineId"] = JsonNode.Parse(RawBrowsing.GetProperty("lineId").GetRawText());
                if (MetadataSourceId is { } metadataSourceId) selection["metadataSourceId"] = metadataSourceId;
                if (Season is { } season) selection["season"] = season;
                var selected = FilmJson.Array(snapshot.Raw, "sources").FirstOrDefault(row =>
                    FilmJson.Text(row, "id") == SourceId);
                var source = FilmJson.Member(selected, "source");
                if (source.ValueKind == JsonValueKind.Object)
                {
                    var pointer = new JsonObject();
                    foreach (string key in new[] { "key", "engine", "installationId", "siteId", "detailId" })
                        if (source.TryGetProperty(key, out var value))
                            pointer[key] = JsonNode.Parse(value.GetRawText());
                    selection["source"] = pointer;
                }
                browsing = JsonSerializer.SerializeToElement(selection, FilmJson.Options);
            }
            if (IsDoubanEntry)
            {
                var request = Request with
                {
                    Target = EntryTarget, EntrySurface = EntrySurface, Metadata = MetadataFor(EntryTarget),
                    ResumeTarget = snapshot is null ? Request.ResumeTarget : HasUnresolvedEntry ? null : target,
                    Browsing = browsing
                };
                if (snapshot is not null && target.Kind == "group")
                {
                    var remembered = FilmPlaybackOpen.RememberResults(FilmJson.Encode(request), snapshot);
                    request = request with { ResultHints = FilmJson.Member(remembered, "resultHints").Clone() };
                }
                return request;
            }
            bool metadataSourceResume = target.Kind == "source" && EntryTarget.Kind is ("movie" or "tv") &&
                EntryTarget.MetadataProvider == "tmdb";
            var metadata = MetadataFor(metadataSourceResume ? EntryTarget : target);
            return Request with
            {
                Target = metadataSourceResume ? EntryTarget : target,
                EntrySurface = EntrySurface,
                ResumeTarget = metadataSourceResume ||
                    snapshot is not null && (Request.SearchReference is not null || Request.ResumeTarget is not null)
                    ? target : Request.ResumeTarget,
                Metadata = metadata,
                Browsing = browsing
            };
        }
    }

    private JsonElement? MetadataFor(FilmPlaybackTarget target) =>
        workInfo is { } work && work.Provider == target.MetadataProvider && work.MetadataId == target.MetadataId
            ? work.Metadata.Clone()
            : Request.Target.MetadataProvider == target.MetadataProvider && Request.Target.MetadataId == target.MetadataId
                ? Request.Metadata?.Clone() : null;

    private JsonElement DisplayMetadata => display?.Metadata ?? Request.Metadata ?? default;
    private JsonElement DisplaySource => display?.Source ?? default;
    private string DisplayKind => display?.Kind ?? ActiveTarget.Kind;
    private JsonElement WorkMetadata => workInfo?.Metadata ?? default;
    public string Title => workInfo?.Title ?? "";

    public string Poster
    {
        get
        {
            var detail = WorkMetadata;
            string poster = FilmJson.Text(detail, "poster_path");
            if (poster.Length == 0) poster = FilmJson.Text(FilmJson.Member(detail, "pic"), "normal");
            return poster;
        }
    }

    public string Backdrop => FilmJson.Text(WorkMetadata, "backdrop_path") is { Length: > 0 } backdrop ? backdrop : Poster;

    public string Metadata
    {
        get
        {
            var detail = WorkMetadata;
            var values = new List<string>();
            string year = FilmJson.Text(detail, "year");
            if (year.Length == 0)
            {
                string date = FilmJson.Text(detail, workInfo?.Kind == "movie" ? "release_date" : "first_air_date");
                if (date.Length >= 4) year = date[..4];
            }
            if (year.Length > 0) values.Add(year);
            string countries = FilmJson.Names(FilmJson.Member(detail, "production_countries"));
            if (countries.Length == 0) countries = FilmJson.Names(FilmJson.Member(detail, "countries"));
            if (countries.Length > 0) values.Add(countries);
            if (FilmJson.Integer(detail, "runtime") is > 0 and var minutes) values.Add($"{minutes}分钟");
            if (workInfo is { Kind: "tv" } work)
            {
                string totals = work.SeasonCount is { } seasons ? $"共 {seasons} 季" : "";
                if (work.EpisodeCount is { } episodes) totals += $" {episodes} 集";
                if (totals.Length > 0) values.Add(totals.Trim());
            }
            return string.Join(" / ", values);
        }
    }

    public string Genres => FilmJson.Names(FilmJson.Member(WorkMetadata, "genres"));

    public string Score => FilmJson.Score(FilmJson.Member(WorkMetadata, "vote_average")) is { Length: > 0 } vote ? vote :
        FilmJson.Score(FilmJson.Member(FilmJson.Member(WorkMetadata, "rating"), "value"));

    public bool HasScore => Score.Length > 0;
    public string ScoreProvider => workInfo?.Provider switch { "tmdb" => "TMDB", "douban" => "豆瓣", _ => "" };

    public string Directors
    {
        get
        {
            var detail = WorkMetadata;
            string directors = FilmJson.Names(FilmJson.Member(detail, "directors"));
            if (directors.Length == 0) directors = CreditDirectors(detail);
            return directors;
        }
    }

    public string Cast
    {
        get
        {
            var detail = WorkMetadata;
            string cast = FilmJson.Names(FilmJson.Member(FilmJson.Member(detail, "credits"), "cast"));
            if (cast.Length == 0) cast = FilmJson.Names(FilmJson.Member(detail, "actors"));
            return cast;
        }
    }

    private static string CreditDirectors(JsonElement detail) => string.Join(" / ",
        FilmJson.Array(FilmJson.Member(detail, "credits"), "crew")
            .Where(value => FilmJson.Text(value, "job") == "Director")
            .Select(value => FilmJson.Text(value, "name")).Where(name => name.Length > 0).Distinct());

    public string Overview => workInfo?.Summary ?? "";

    private static IReadOnlyList<FilmCardItem> Similar(JsonElement detail, string provider, string kind)
    {
        var rows = FilmJson.Array(FilmJson.Member(detail, "similar"), "results");
        return rows.Select(row =>
        {
            string id = FilmJson.RequiredText(row, "businessId"), title = FilmJson.Text(row, provider == "douban" || kind == "movie" ? "title" : "name");
            if (id.Length == 0 || title.Length == 0) return null;
            string poster = FilmJson.Text(row, "poster_path"), score = FilmJson.Score(FilmJson.Member(row, "vote_average"));
            if (provider == "douban")
            {
                poster = FilmJson.Text(FilmJson.Member(row, "pic"), "normal");
                score = FilmJson.Score(FilmJson.Member(FilmJson.Member(row, "rating"), "value"));
            }
            var target = FilmJson.Target(row.GetProperty("playbackTarget"));
            target.Validate();
            return new FilmCardItem(id, title, poster, score)
            {
                PlaybackContext = new(target)
            };
        }).OfType<FilmCardItem>().ToArray();
    }
}
