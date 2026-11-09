using System.Text.Json;

namespace MeowFilm.Capabilities;

public enum FilmSearchChannelState { Idle, Loading, Completed, NoSources, Unavailable, Error, Cancelled }

public sealed record FilmSearchSource(FilmCatSource Cat, FilmVideoSource Site)
{
    // A tuple encoding keeps installation/site boundaries unambiguous without a URL identity.
    public string Key => JsonSerializer.Serialize(new[] { Cat.InstallationId, Site.Key.SiteKey });
}

public interface IFilmSearchSources
{
    event EventHandler? Changed;
    IReadOnlyList<FilmSearchSource> CurrentSearchSources { get; }
    Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token);
    Task<JsonElement> SearchAsync(FilmSearchSource source, string query, int page, CancellationToken token);
    bool IsSearchSourceCurrent(FilmSearchSource source);
}

/// <summary>The adapter returns original TMDB JSON; image resolution belongs to data access.</summary>
public interface IFilmSearchMetadata
{
    Task<JsonElement> SearchAsync(string query, CancellationToken token);
    Task<JsonElement> DetailAsync(string type, long id, CancellationToken token);
    string ResolveImage(string path);
}

public sealed class FilmSearchContext
{
    internal FilmSearchContext(FilmSearchSession session, JsonElement item)
    {
        Session = session;
        Item = item.Clone();
        DisplayKey = FilmSearchJson.Text(item, "displayKey");
    }

    public FilmSearchSession Session { get; }
    public string Query => Session.Query;
    public string SessionId => Session.Id;
    public string MetadataQuery => Session.MetadataQuery;
    public JsonElement? MetadataSearchPayload => Session.MetadataSearchPayload;
    public string DisplayKey { get; }
    public JsonElement Item { get; }
    private bool IsRawSelection => DisplayKey.StartsWith("site:", StringComparison.Ordinal);
    internal bool SameSelection(FilmSearchContext other) =>
        ReferenceEquals(Session, other.Session) && Item.GetRawText() == other.Item.GetRawText();
    // Playback retains the live session, so a first-round click also sees later members/details.
    public JsonElement CurrentItem => Session.CurrentItem(Item);
    public JsonElement CarrierItem => IsRawSelection ? CurrentItem : Session.Carrier(Item);
    public IReadOnlyList<JsonElement> Sources
    {
        get
        {
            var item = CarrierItem;
            return item.TryGetProperty("aggregateItems", out var members) && members.ValueKind == JsonValueKind.Array
                ? Array.AsReadOnly(members.EnumerateArray().Select(value => value.Clone()).ToArray())
                : FilmSearchJson.Text(item, "sourceKind") == "site" ? Array.AsReadOnly(new[] { item }) : [];
        }
    }
    public JsonElement? Metadata => IsRawSelection ? null : Session.Detail(CarrierItem);
}

internal static class FilmSearchJson
{
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    internal static long Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long number) ? number : 0;
    internal static JsonElement[] Items(JsonElement value) => value.GetProperty("items").EnumerateArray()
        .Select(item => item.Clone()).ToArray();
    internal static string SiteIdentity(JsonElement item) => JsonSerializer.Serialize(new[]
        { Text(item, "siteKey"), Text(item, "siteDetail") });
    internal static string MetadataIdentity(JsonElement item) => JsonSerializer.Serialize(new object[]
        { Text(item, "tmdbType"), Number(item, "tmdbId") });
}
