using System.Globalization;
using System.Collections;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal interface IFilmHomeSource : IDisposable
{
    Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        int start,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        CancellationToken cancellationToken) =>
        LoadAsync(section, 0, 12, cancellationToken);
}

internal sealed class FilmHomeException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed class FilmHomePage(IReadOnlyList<FilmCardItem> items, FilmMetadataCache cache) : IReadOnlyList<FilmCardItem>
{
    internal FilmMetadataCache Cache { get; } = cache;
    public int Count => items.Count;
    public FilmCardItem this[int index] => items[index];
    public IEnumerator<FilmCardItem> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class DoubanFilmHomeSource(IRuntimeClient runtime) : IFilmHomeSource
{
    private readonly FilmPlaybackClient client = new(runtime);
    private bool disposed;

    public async Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        int start,
        int limit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(section);
        if (!FilmDataFilters.IsAllowedRequest(section))
            throw new ArgumentException("Unknown film section.", nameof(section));
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
            var cache = FilmMetadataCache.Read(FilmJson.Member(entry, "cache")) ??
                throw new FilmHomeException("INVALID_RESPONSE");
            return new FilmHomePage(Parse(FilmJson.Member(entry, "value"), section, start), cache);
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

    private static IReadOnlyList<FilmCardItem> Parse(
        JsonElement root,
        FilmSectionDefinition section,
        int start)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new FilmHomeException("INVALID_RESPONSE");

        var result = new List<FilmCardItem>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            int itemIndex = checked(start + index++);
            if (item.ValueKind != JsonValueKind.Object) continue;
            string title = Text(item, "title");
            if (title.Length == 0) continue;
            string id = Scalar(item, "id");
            string metadataId = id;
            if (id.Length == 0)
                id = $"{section.Key}:{itemIndex.ToString(CultureInfo.InvariantCulture)}:{title}";
            result.Add(new(id, title, Poster(item), Score(item))
            {
                PlaybackContext = metadataId.Length == 0 ? null : new(new(
                    $"douban:{section.Kind}:{metadataId}", section.Kind == "movie" ? "movie" : "tv",
                    title, "douban", metadataId), Metadata: item.Clone(), EntrySurface: "home")
            });
        }
        return result;
    }

    private static string Poster(JsonElement item)
    {
        string[] candidates =
        [
            NestedText(item, "pic", "normal"),
            NestedText(item, "pic", "large"),
            NestedText(item, "cover", "url"),
            Text(item, "cover_url"),
            Text(item, "poster"),
            Text(item, "image")
        ];
        return candidates.FirstOrDefault(value => value.Length > 0) ?? "";
    }

    private static string Score(JsonElement item)
    {
        if (!item.TryGetProperty("rating", out var rating) ||
            rating.ValueKind != JsonValueKind.Object ||
            !rating.TryGetProperty("value", out var value))
            return "";
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) &&
            double.IsFinite(number) &&
            number > 0)
            return number.ToString("0.#", CultureInfo.InvariantCulture);
        if (value.ValueKind != JsonValueKind.String) return "";
        string text = value.GetString()?.Trim() ?? "";
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed > 0
            ? parsed.ToString("0.#", CultureInfo.InvariantCulture)
            : "";
    }

    private static string NestedText(JsonElement item, string owner, string property) =>
        item.TryGetProperty(owner, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, property)
            : "";

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static string Scalar(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
    }
}
