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
            var cardObj = FilmJson.Member(item, "card");
            if (cardObj.ValueKind != JsonValueKind.Object) continue;
            try
            {
                string id = FilmJson.RequiredText(cardObj, "id");
                var entry = FilmEntry.Read(cardObj);
                result.Add(new(id, entry.Title, entry.Poster, entry.Score)
                {
                    TextBadge = entry.TextBadge,
                    Subtitle = entry.Subtitle,
                    Entry = entry
                });
            }
            catch (FilmPlaybackException error) when (error.Code is "INVALID_RESPONSE" or "INVALID_REQUEST")
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "douban", "row.skipped", new { code = error.Code }, error: error);
            }
        }
        if (items.GetArrayLength() > 0 && result.Count == 0) throw new FilmHomeException("INVALID_RESPONSE");
        return result;
    }
}
