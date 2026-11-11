using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public enum FilmSearchChannelState { Idle, Loading, Completed, NoSources, Unavailable, Error, Cancelled }

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

/// <summary>The successful response and its actual producer; cache receipts keep their original provenance.</summary>
public sealed record FilmSearchData(JsonElement Value, RuntimeConnection? Owner = null, JsonElement? CacheEntry = null);

public interface IFilmSearchSources
{
    event EventHandler? Changed;
    IReadOnlyList<FilmSearchSource> CurrentSearchSources { get; }
    Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token);
}

/// <summary>The adapter returns original TMDB JSON; image resolution belongs to data access.</summary>
public interface IFilmSearchMetadata
{
    Task<FilmSearchData> SearchAsync(string query, CancellationToken token);
    Task<FilmSearchData> DetailAsync(string type, long id, CancellationToken token);
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
    public long Revision => Session.ReferenceRevision;
    public string MetadataQuery => Session.MetadataQuery;
    public JsonElement? MetadataSearchPayload => Session.MetadataSearchPayload;
    public string DisplayKey { get; }
    public JsonElement Item { get; }
    internal bool SameSelection(FilmSearchContext other) =>
        ReferenceEquals(Session, other.Session) && DisplayKey == other.DisplayKey;
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
        var units = field.EnumerateArray().Select(unit => (char)unit.GetUInt16()).ToArray();
        // WPF text is a projection only; the original JSON remains the transport identity.
        return System.Text.Encoding.Unicode.GetString(System.Text.Encoding.Unicode.GetBytes(units));
    }
}
