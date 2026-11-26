namespace MeowFilm.Capabilities;

public sealed partial class FilmPlaybackPage
{
    public string Title => workInfo?.Title is { Length: > 0 } t ? t : Entry.Title;

    public string Poster => workInfo?.Poster is { Length: > 0 } p ? p :
        Entry.Poster.Length > 0 ? Entry.Poster : Item.Poster;

    public string Backdrop => workInfo?.Backdrop is { Length: > 0 } b ? b :
        Entry.Backdrop.Length > 0 ? Entry.Backdrop : Poster;

    public string Metadata
    {
        get
        {
            var values = new List<string>(4);
            string year = workInfo?.Year is { Length: > 0 } wy ? wy :
                Entry.Year is > 0 and var ey ? ey.ToString() : "";
            if (year.Length > 0) values.Add(year);
            if (workInfo?.Countries is { Length: > 0 } countries) values.Add(countries);
            if (workInfo?.Duration is { Length: > 0 } duration) values.Add(duration);
            if (workInfo is { Kind: "tv" } tv)
            {
                string totals = tv.SeasonCount is > 0 and var sc ? $"共 {sc} 季" : "";
                if (tv.EpisodeCount is > 0 and var ec) totals += $" {ec} 集";
                if (totals.Length > 0) values.Add(totals.Trim());
            }
            return string.Join(" / ", values);
        }
    }

    public string Genres => workInfo is { Genres.Count: > 0 } info ? string.Join(" / ", info.Genres) : "";

    public string Score => workInfo?.Score is { Length: > 0 } s ? s :
        Entry.Score.Length > 0 ? Entry.Score : Item.Score;

    public bool HasScore => Score.Length > 0;

    public string ScoreProvider => workInfo?.ScoreProvider switch
    {
        "tmdb" => "TMDB",
        "douban" => "豆瓣",
        _ => ""
    };

    public string Directors => workInfo?.Directors ?? "";
    public bool HasDirectors => Directors.Length > 0;

    public string Cast => workInfo?.Cast ?? "";
    public bool HasCast => Cast.Length > 0;

    public string Overview => workInfo?.Summary ?? "";
    public bool HasOverview => Overview.Length > 0;
}
