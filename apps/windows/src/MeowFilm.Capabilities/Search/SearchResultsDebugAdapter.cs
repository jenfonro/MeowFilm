using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed class SearchResultsDebugAdapter(SearchResultsPage page) : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> SearchTargets = ["search"];

    public string DebugModuleId => page.Id;
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
            var matched = page.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Unknown search tab: '{tabId}'.");
            await page.OpenTabAsync(matched.Id);
            return true;
        }
        return target == "search";
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        var tabs = page.Tabs.Select((t, idx) => new
        {
            group = "search-tab",
            index = idx,
            id = t.Id,
            title = t.Label,
            selected = t.Selected,
            enabled = true
        }).ToArray();

        if (page.SelectedTab == "film")
        {
            var rows = page.Cards.Take(maxItems).Select((c, idx) => new
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
                query = page.Query,
                searchTarget = "film",
                selectedTab = page.SelectedTab,
                loading = page.FilmLoading,
                error = string.IsNullOrWhiteSpace(page.Error) ? null : page.Error,
                metadataStatus = page.MetadataStatus,
                tabs,
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = page.HasMore,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings = (object?)null
            });
        }

        if (page.SelectedTab == "novel" && page.Novel is { } novel)
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
                query = page.Query,
                searchTarget = "novel",
                selectedTab = page.SelectedTab,
                loading = novel.Search.Loading,
                error = string.IsNullOrWhiteSpace(page.Error) ? null : page.Error,
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

        var musicSections = page.Music.DebugAdapterInstance.ProjectCatalogSections(page.Music.Catalog.DisplayCatalogSections, maxItems, includeRaw);
        var musicRows = page.Music.ActiveSongs.Take(maxItems).Select((song, idx) => new
        {
            index = idx,
            id = song.Id,
            title = song.Title,
            subtitle = string.IsNullOrEmpty(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}",
            duration = song.Duration,
            selected = ReferenceEquals(song, page.Music.Library.SelectedSong),
            state = "song",
            actions = new[] { "play", "select", "favorite" }
        }).ToArray();
        return Task.FromResult<object?>(new
        {
            target = "search",
            kind = "search",
            query = page.Query,
            searchTarget = "music",
            selectedTab = page.SelectedTab,
            loading = false,
            error = string.IsNullOrWhiteSpace(page.Error) ? null : page.Error,
            tabs,
            filters = Array.Empty<object>(),
            sections = musicSections,
            rows = musicRows,
            hasMore = page.Music.Library.CanNextPage,
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
                if (string.IsNullOrWhiteSpace(page.Query)) return new { refreshed = false, reason = "EMPTY_QUERY" };
                var targetDomain = page.SelectedTab is "film" or "novel" ? page.SelectedTab : "music";
                await page.SubmitAsync(page.Query, targetDomain, cancellationToken);
                return new { refreshed = true, query = page.Query, tab = page.SelectedTab };
            }

            case "tab.select":
            {
                var id = DebugParams.GetString(parameters, "id");
                var idx = DebugParams.GetInt(parameters, "index");
                string? picked;
                if (idx is { } i)
                {
                    if (i < 0 || i >= page.Tabs.Count)
                        throw DebugApiException.InvalidArgument($"Search tab index {i} out of range (0..{Math.Max(0, page.Tabs.Count - 1)}).");
                    picked = page.Tabs[i].Id;
                }
                else picked = id;
                var matched = !string.IsNullOrWhiteSpace(picked)
                    ? page.Tabs.FirstOrDefault(t => string.Equals(t.Id, picked, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (matched is null)
                    throw DebugApiException.NotFound($"Search tab not found (id='{id}', index={idx}).");
                await page.OpenTabAsync(matched.Id);
                DebugLog.Info(DebugCategories.Search, "debug.action", "tab.select", new { query = page.Query, tab = matched.Id });
                return new { group = "search-tab", selected = matched.Id };
            }

            case "list.item.invoke":
            {
                var id = DebugParams.GetString(parameters, "id");
                var idxParam = DebugParams.GetInt(parameters, "index");
                if (page.SelectedTab == "film")
                {
                    FilmSearchCardItem card;
                    if (idxParam is { } fi)
                    {
                        if (fi < 0 || fi >= page.Cards.Count) throw DebugApiException.InvalidArgument("Film search card index out of range.");
                        card = page.Cards[fi];
                    }
                    else if (!string.IsNullOrWhiteSpace(id))
                    {
                        card = page.Cards.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase))
                            ?? throw DebugApiException.NotFound($"Film search item '{id}' not found.");
                    }
                    else
                    {
                        if (page.Cards.Count == 0) throw DebugApiException.InvalidState("No film search results.");
                        card = page.Cards[0];
                    }
                    await page.OpenCardAsync(card);
                    return new { invoked = true, domain = "film", title = card.Title };
                }
                if (page.SelectedTab == "novel" && page.Novel is { } novel)
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
                    await page.OpenNovelAsync(book);
                    return new { invoked = true, domain = "novel", title = book.Name };
                }
                return await page.Music.DebugAdapter.TryHandleDebugActionAsync(action, parameters, false, cancellationToken);
            }

            case "section.item.invoke":
            case "section.more":
                if (page.SelectedTab is "film" or "novel")
                    throw DebugApiException.InvalidState($"'{action}' is only supported on music search tabs; use list.item.invoke on '{page.SelectedTab}' tab.");
                return await page.Music.DebugAdapter.TryHandleDebugActionAsync(action, parameters, false, cancellationToken);

            case "list.load_more":
            {
                if (page.SelectedTab == "film") { await page.LoadMoreAsync(); return new { loadedMore = true, domain = "film" }; }
                if (page.SelectedTab == "novel" && page.Novel is not null) { await page.LoadMoreNovelAsync(); return new { loadedMore = true, domain = "novel" }; }
                return await page.Music.DebugAdapter.TryHandleDebugActionAsync(action, parameters, false, cancellationToken);
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
}
