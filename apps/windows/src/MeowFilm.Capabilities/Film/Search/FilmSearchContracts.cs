using System.Text.Json;

namespace MeowFilm.Capabilities;

public enum FilmSearchChannelState { Idle, Loading, Completed, NoSources, Unavailable, Error, Cancelled, Unknown }

public sealed record FilmSearchSource(FilmCatSource Cat, FilmVideoSource Site)
{
    // A tuple encoding keeps installation/site boundaries unambiguous without a URL identity.
    public string Key => JsonSerializer.Serialize(new[] { Cat.InstallationId, Site.Key.SiteKey });
    internal JsonElement Wire => FilmJson.Encode(new
    {
        key = Key, engine = "cat", installationId = Cat.InstallationId, siteId = Site.Key.SiteKey,
        generation = Cat.Generation, name = Site.Name, enabled = Site.Options.Enabled,
        searchable = Site.Options.SearchEnabled, aggregateImages = Site.Options.AggregateImages,
        order = Site.Order
    });
}

public interface IFilmSearchSources
{
    event EventHandler? Changed;
    IReadOnlyList<FilmSearchSource> CurrentSearchSources { get; }
    Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token);
}

public sealed class FilmSearchContext
{
    internal FilmSearchContext(FilmSearchSession session, JsonElement item)
        : this(session, FilmSearchJson.Text(item, "displayKey"), item) { }
    internal FilmSearchContext(FilmSearchSession session, string displayKey, JsonElement item)
    {
        Session = session;
        Item = item;
        DisplayKey = displayKey;
    }

    public FilmSearchSession Session { get; }
    public string Query => Session.Query;
    public string SessionId => Session.Id;
    public long Revision => Session.ReferenceRevision;
    public string MetadataQuery => Session.MetadataQuery;
    public JsonElement? MetadataSearchPayload => Session.MetadataSearchPayload;
    public string DisplayKey { get; private set; }
    public JsonElement Item { get; private set; }
    internal void UpdateItem(JsonElement item)
    {
        Item = item;
        if (FilmSearchJson.Text(item, "displayKey") is { Length: > 0 } key) DisplayKey = key;
    }
    internal FilmPlaybackRequest PlaybackRequest => new(FilmJson.Target(Item.GetProperty("playbackTarget")),
        Sources: Session.SourceSnapshot, Query: Query, EntrySurface: "search");
    internal object Reference => new { sessionId = SessionId, selectionId = DisplayKey };
    internal Task<FilmPlaybackRequest> HandoffAsync(CancellationToken token) => Session.Service.HandoffAsync(this, token);
}

internal static class FilmSearchJson
{
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var field) ? DisplayText(field) : "";
    internal static string DisplayText(JsonElement field)
    {
        if (field.ValueKind == JsonValueKind.String) return field.GetString()!;
        if (field.ValueKind != JsonValueKind.Array) return "";
        var units = new char[field.GetArrayLength()];
        int index = 0;
        foreach (var unit in field.EnumerateArray())
        {
            if (unit.ValueKind != JsonValueKind.Number || !unit.TryGetUInt16(out ushort value)) return "";
            units[index++] = (char)value;
        }
        // WinUI text is a projection only; the original JSON remains the transport identity.
        return System.Text.Encoding.Unicode.GetString(System.Text.Encoding.Unicode.GetBytes(units));
    }
}
