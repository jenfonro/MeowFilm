using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class SearchResultsPage : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> SearchTargets = ["search"];

    public string DebugModuleId => Id;
    public IReadOnlyList<string> SupportedDebugTargets => SearchTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        settingsSection = null;
        return "search";
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (target.StartsWith("search:", StringComparison.Ordinal))
        {
            var tabId = target["search:".Length..];
            if (!Tabs.Any(t => string.Equals(t.Id, tabId, StringComparison.OrdinalIgnoreCase)))
                throw DebugApiException.NotFound($"Unknown search tab: '{tabId}'.");
            await OpenTabAsync(tabId);
            return true;
        }
        return target == "search";
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        var tabs = Tabs.Select((t, idx) => new
        {
            group = "search-tab",
            index = idx,
            id = t.Id,
            title = t.Label,
            selected = t.Selected,
            enabled = true
        }).ToArray();

        if (tab == "film")
        {
            var rows = Cards.Take(maxItems).Select((c, idx) => new
            {
                index = idx,
                id = c.Id,
                title = c.Title,
                subtitle = c.Score,
                selected = false,
                state = "film",
                actions = new[] { "open" }
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target = "search",
                kind = "search",
                query,
                searchTarget = "film",
                selectedTab = tab,
                loading = session?.Loading ?? false,
                error = string.IsNullOrWhiteSpace(error) ? null : error,
                metadataStatus = MetadataStatus,
                tabs,
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = HasMore,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings = (object?)null
            });
        }

        if (tab == "novel" && novel is not null)
        {
            var rows = novel.Search.Books.Take(maxItems).Select((b, idx) => new
            {
                index = idx,
                id = b.SourceIdentity,
                title = b.Name,
                subtitle = b.Author,
                selected = false,
                state = "novel",
                actions = new[] { "open" }
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target = "search",
                kind = "search",
                query,
                searchTarget = "novel",
                selectedTab = tab,
                loading = novel.Search.Loading,
                error = string.IsNullOrWhiteSpace(error) ? null : error,
                tabs,
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = novel.Search.CanLoadMore,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings = (object?)null
            });
        }

        var musicSections = Music.ProjectCatalogSections(Music.DisplayCatalogSections, maxItems, includeRaw);
        var musicRows = Music.Songs.Take(maxItems).Select((song, idx) => new
        {
            index = idx,
            id = song.Id,
            title = song.Title,
            subtitle = string.IsNullOrEmpty(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}",
            duration = song.Duration,
            selected = ReferenceEquals(song, Music.SelectedSong),
            state = "song",
            actions = new[] { "play", "select", "favorite" }
        }).ToArray();
        return Task.FromResult<object?>(new
        {
            target = "search",
            kind = "search",
            query,
            searchTarget = "music",
            selectedTab = tab,
            loading = false,
            error = string.IsNullOrWhiteSpace(error) ? null : error,
            tabs,
            filters = Array.Empty<object>(),
            sections = musicSections,
            rows = musicRows,
            hasMore = Music.CanNextPage,
            detail = (object?)null,
            player = (object?)null,
            reader = (object?)null,
            settings = (object?)null
        });
    }

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "refresh":
            {
                if (string.IsNullOrWhiteSpace(query)) return new { refreshed = false, reason = "EMPTY_QUERY" };
                var targetDomain = tab is "film" or "novel" ? tab : "music";
                await SubmitAsync(query, targetDomain, cancellationToken);
                return new { refreshed = true, query, tab };
            }

            case "tab.select":
            {
                var id = GetStr(parameters, "id");
                var idx = GetInt(parameters, "index");
                string? picked;
                if (idx is { } i)
                {
                    if (i < 0 || i >= Tabs.Count)
                        throw DebugApiException.InvalidArgument($"Search tab index {i} out of range (0..{Math.Max(0, Tabs.Count - 1)}).");
                    picked = Tabs[i].Id;
                }
                else picked = id;
                if (string.IsNullOrWhiteSpace(picked) || !Tabs.Any(t => string.Equals(t.Id, picked, StringComparison.OrdinalIgnoreCase)))
                    throw DebugApiException.NotFound($"Search tab not found (id='{id}', index={idx}).");
                await OpenTabAsync(picked);
                DebugLog.Info(DebugCategories.Search, "debug.action", "tab.select", new { query, tab = picked });
                return new { group = "search-tab", selected = picked };
            }

            case "list.item.invoke":
            {
                var id = GetStr(parameters, "id");
                var idxParam = GetInt(parameters, "index");
                if (tab == "film")
                {
                    FilmSearchCardItem card;
                    if (idxParam is { } fi)
                    {
                        if (fi < 0 || fi >= Cards.Count) throw DebugApiException.InvalidArgument("Film search card index out of range.");
                        card = Cards[fi];
                    }
                    else if (!string.IsNullOrWhiteSpace(id))
                    {
                        card = Cards.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase))
                            ?? throw DebugApiException.NotFound($"Film search item '{id}' not found.");
                    }
                    else
                    {
                        if (Cards.Count == 0) throw DebugApiException.InvalidState("No film search results.");
                        card = Cards[0];
                    }
                    await OpenCardAsync(card);
                    return new { invoked = true, domain = "film", title = card.Title };
                }
                if (tab == "novel" && novel is not null)
                {
                    NovelBook book;
                    if (idxParam is { } ni)
                    {
                        if (ni < 0 || ni >= novel.Search.Books.Count) throw DebugApiException.InvalidArgument("Novel search index out of range.");
                        book = novel.Search.Books[ni];
                    }
                    else if (!string.IsNullOrWhiteSpace(id))
                    {
                        book = novel.Search.Books.FirstOrDefault(b => string.Equals(b.SourceIdentity, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(b.Name, id, StringComparison.OrdinalIgnoreCase))
                            ?? throw DebugApiException.NotFound($"Novel search item '{id}' not found.");
                    }
                    else
                    {
                        if (novel.Search.Books.Count == 0) throw DebugApiException.InvalidState("No novel search results.");
                        book = novel.Search.Books[0];
                    }
                    await OpenNovelAsync(book);
                    return new { invoked = true, domain = "novel", title = book.Name };
                }
                return await Music.TryHandleDebugActionAsync(action, parameters, false, cancellationToken);
            }

            case "section.item.invoke":
            case "section.more":
                if (tab is "film" or "novel")
                    throw DebugApiException.InvalidState($"'{action}' is only supported on music search tabs; use list.item.invoke on '{tab}' tab.");
                return await Music.TryHandleDebugActionAsync(action, parameters, false, cancellationToken);

            case "list.load_more":
            {
                if (tab == "film") { await LoadMoreAsync(); return new { loadedMore = true, domain = "film" }; }
                if (tab == "novel" && novel is not null) { await LoadMoreNovelAsync(); return new { loadedMore = true, domain = "novel" }; }
                return await Music.TryHandleDebugActionAsync(action, parameters, false, cancellationToken);
            }

            default:
                return null;
        }
    }

    public Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    public Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    public Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    private static string? GetStr(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static int? GetInt(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val : null;
}
