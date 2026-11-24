using System.Collections;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed class FilmHomeException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed class FilmHomePage(IReadOnlyList<FilmCardItem> items, FilmMetadataCache? cache) : IReadOnlyList<FilmCardItem>
{
    internal FilmMetadataCache? Cache { get; } = cache;
    public int Count => items.Count;
    public FilmCardItem this[int index] => items[index];
    public IEnumerator<FilmCardItem> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class DoubanFilmHomeSource(IRuntimeClient runtime)
{
    private readonly FilmPlaybackClient client = new(runtime);

    public Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        CancellationToken cancellationToken) =>
        LoadAsync(section, 0, 12, cancellationToken);

    public async Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        int start,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var entry = await client.CallAsync("data.metadata.entry", new
            {
                provider = "douban",
                path = "/rexxar/api/v2/subject/recent_hot/" + section.Kind,
                query = new { start, limit, category = section.Category, type = section.Type },
                refresh = section.Refresh ? true : (bool?)null
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var cache = FilmMetadataCache.Read(FilmJson.Member(entry, "cache"));
            return new FilmHomePage(Parse(FilmJson.Member(entry, "value")), cache);
        }
        catch (FilmPlaybackException error)
        {
            throw new FilmHomeException(error.Code);
        }
        catch (BridgeException error)
        {
            throw new FilmHomeException(error.Code);
        }
    }

    private static IReadOnlyList<FilmCardItem> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new FilmHomeException("INVALID_RESPONSE");

        var result = new List<FilmCardItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string title = Text(item, "title");
            if (title.Length == 0) continue;
            try
            {
                string id = FilmJson.RequiredText(item, "businessId");
                var target = FilmJson.Target(FilmJson.Member(item, "playbackTarget"));
                target.Validate();
                result.Add(new(id, title, Poster(item), Score(item))
                {
                    PlaybackContext = new(target, Metadata: item.Clone(), EntrySurface: "home")
                });
            }
            catch (FilmPlaybackException error) when (error.Code is "INVALID_RESPONSE" or "INVALID_REQUEST")
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "douban", "row.skipped", new { title, code = error.Code }, error: error);
            }
        }
        if (items.GetArrayLength() > 0 && result.Count == 0) throw new FilmHomeException("INVALID_RESPONSE");
        return result;
    }

    private static string Poster(JsonElement item)
    {
        // Per docs/runtime/metadata-data.md, cover.url is a web page link; only use image fields.
        var coverImage = NestedObject(NestedObject(item, "cover"), "image");
        string[] candidates =
        [
            NestedText(item, "pic", "normal"),
            NestedText(item, "pic", "large"),
            NestedText(coverImage, "normal", "url"),
            NestedText(coverImage, "large", "url")
        ];
        return candidates.FirstOrDefault(value => value.Length > 0) ?? "";
    }

    private static string Score(JsonElement item)
    {
        if (!item.TryGetProperty("rating", out var rating) ||
            rating.ValueKind != JsonValueKind.Object ||
            !rating.TryGetProperty("value", out var value))
            return "";
        return FilmJson.Score(value);
    }

    private static JsonElement NestedObject(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? nested
            : default;

    private static string NestedText(JsonElement item, string owner, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(owner, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? Text(nested, property)
            : "";

    private static string Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
}
