using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class NovelCapability : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> NovelTargets =
    [
        "novel.home", "novel.categories", "novel.shelf", "novel.book", "novel.reader", "settings.novel"
    ];

    public string DebugModuleId => Id;
    public IReadOnlyList<string> SupportedDebugTargets => NovelTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        if (settingsOpen)
        {
            settingsSection = settings.CurrentDebugTab;
            return "settings.novel";
        }
        settingsSection = null;
        return route switch
        {
            HomeRoute => "novel.home",
            CategoriesRoute => "novel.categories",
            BookRoute => "novel.book",
            ReaderRoute => "novel.reader",
            _ => "novel.home"
        };
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.Novel, "debug.nav", "open.target", new { target, @params = parameters });
        switch (target)
        {
            case "novel.home":
            case "novel.shelf":
                await NavigateAsync(HomeRoute, cancellationToken);
                await SelectKindFromParamsAsync(parameters, cancellationToken);
                return true;
            case "novel.categories":
                await NavigateAsync(CategoriesRoute, cancellationToken);
                await SelectKindFromParamsAsync(parameters, cancellationToken);
                return true;
            case "novel.book":
            {
                if (GetStr(parameters, "bookId") is { Length: > 0 } bookId)
                {
                    var entry = Library.Entries.FirstOrDefault(e => string.Equals(e.BookId, bookId, StringComparison.Ordinal))
                        ?? throw DebugApiException.NotFound($"Novel bookId not found in library: '{bookId}'.");
                    await OpenBookAsync(entry.Book, cancellationToken);
                    return true;
                }
                if (Session is null) throw DebugApiException.InvalidState("No active novel book session.");
                await NavigateAsync(BookRoute, cancellationToken);
                return true;
            }
            case "novel.reader":
            {
                if (Session is null) throw DebugApiException.InvalidState("No active novel session to read.");
                await StartReadingAsync(cancellationToken);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task SelectKindFromParamsAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        var url = GetStr(parameters, "kindUrl");
        var idx = GetInt(parameters, "kindIndex");
        if (url is null && idx is null) return;
        NovelKind? picked;
        if (idx is { } i)
        {
            if (i < 0 || i >= Kinds.Count)
                throw DebugApiException.InvalidArgument($"kindIndex {i} out of range (0..{Math.Max(0, Kinds.Count - 1)}).");
            picked = Kinds[i];
        }
        else
        {
            picked = Kinds.FirstOrDefault(k => string.Equals(k.Url, url, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k.Title, url, StringComparison.OrdinalIgnoreCase));
            if (picked is null)
                throw DebugApiException.NotFound($"Novel explore kind not found: '{url}'.");
        }
        await LoadDiscoveryAsync(ExploreSource, picked, reset: false, cancellationToken);
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken)
    {
        if (string.Equals(section, "appearance", StringComparison.OrdinalIgnoreCase))
        {
            // Windows novel reading typography lives as a dialog trigger inside NovelSettingsView
            // (and is directly inspectable via settings.novel ui/content.settings.appearance).
            return Task.FromResult(true);
        }
        return Task.FromResult(settings.SelectDebugTab(section));
    }

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (settingsOpen)
        {
            var snap = settings.BuildDebugSnapshot();
            return Task.FromResult<object?>(new
            {
                target = "settings.novel",
                kind = "settings",
                loading = false,
                error = string.IsNullOrWhiteSpace(Notice) ? null : Notice,
                tabs = new object[]
                {
                    new { group = "settings-section", index = 0, id = "sources", title = "书源管理", selected = settings.CurrentDebugTab == "sources", enabled = true },
                    new { group = "settings-section", index = 1, id = "rules", title = "替换净化", selected = settings.CurrentDebugTab == "rules", enabled = true }
                },
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings = snap
            });
        }

        if (route == ReaderRoute && Session is { } readerSession)
        {
            return Task.FromResult<object?>(new
            {
                target = "novel.reader",
                kind = "reader",
                loading = readerSession.Loading,
                error = string.IsNullOrWhiteSpace(Notice) ? null : Notice,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = (object?)null,
                reader = new
                {
                    bookId = readerSession.Entry.BookId,
                    bookName = readerSession.Book.Name,
                    author = readerSession.Book.Author,
                    sourceId = readerSession.Book.InstallationId,
                    chapterIndex = readerSession.Chapter?.Index,
                    chapterTitle = readerSession.Chapter?.Name ?? readerSession.ContentTitle,
                    chapterCount = readerSession.Chapters.Count,
                    pageStatus = view.DebugReaderPageStatus,
                    onShelf = readerSession.Entry.OnShelf,
                    ruleCount = Library.Rules.Count
                },
                settings = (object?)null
            });
        }

        if (route == BookRoute && Session is { } bookSession)
        {
            return Task.FromResult<object?>(new
            {
                target = "novel.book",
                kind = "detail",
                loading = bookSession.Loading,
                error = string.IsNullOrWhiteSpace(Notice) ? null : Notice,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = bookSession.Chapters.Take(maxItems).Select((c, idx) => new
                {
                    index = idx,
                    id = c.ChapterUrl,
                    title = c.Name,
                    subtitle = c.IsVip ? "VIP" : "",
                    selected = bookSession.Chapter?.Index == c.Index,
                    state = "chapter",
                    actions = new[] { "open" }
                }).ToArray(),
                hasMore = false,
                detail = new
                {
                    bookId = bookSession.Entry.BookId,
                    name = bookSession.Book.Name,
                    author = bookSession.Book.Author,
                    coverUrl = bookSession.Book.CoverUrl,
                    intro = bookSession.Book.Intro,
                    latestChapterTitle = bookSession.Book.LatestChapterTitle,
                    onShelf = bookSession.Entry.OnShelf,
                    chapterCount = bookSession.Chapters.Count
                },
                player = (object?)null,
                reader = (object?)null,
                settings = (object?)null
            });
        }

        var tabs = Kinds.Select((k, idx) => new
        {
            group = "category",
            index = idx,
            id = k.Url,
            title = k.Title,
            selected = ExploreKind?.Url == k.Url,
            enabled = !string.IsNullOrWhiteSpace(k.Url)
        }).ToArray();

        var shelfItems = Library.Entries.Where(e => e.OnShelf).Take(maxItems).Select((entry, idx) => new
        {
            index = idx,
            id = entry.BookId,
            kind = "novel",
            title = entry.Book.Name,
            subtitle = entry.Book.Author,
            badge = entry.Book.LatestChapterTitle,
            coverUrl = entry.Book.CoverUrl,
            actions = new[] { "open" },
            meta = new { sourceId = entry.Book.InstallationId, bookUrl = entry.Book.BookUrl, onShelf = true }
        }).ToArray();

        var discoverItems = DiscoverBooks.Take(maxItems).Select((book, idx) => new
        {
            index = idx,
            id = book.SourceIdentity,
            kind = "novel",
            title = book.Name,
            subtitle = book.Author,
            badge = book.LatestChapterTitle,
            coverUrl = book.CoverUrl,
            actions = new[] { "open" },
            meta = new { sourceId = book.InstallationId, bookUrl = book.BookUrl }
        }).ToArray();

        var sections = new object[]
        {
            new
            {
                index = 0,
                id = "discover",
                title = ExploreKind?.Title ?? "发现",
                kind = "novel",
                layout = "grid",
                hasMore = MoreDiscovery,
                itemCount = DiscoverBooks.Count,
                items = discoverItems
            },
            new
            {
                index = 1,
                id = "shelf",
                title = "书架",
                kind = "novel",
                layout = "list",
                hasMore = false,
                itemCount = shelfItems.Length,
                items = shelfItems
            }
        };

        return Task.FromResult<object?>(new
        {
            target = route == CategoriesRoute ? "novel.categories" : "novel.home",
            kind = "sections",
            loading = LoadingDiscovery,
            error = string.IsNullOrWhiteSpace(DiscoveryError) ? null : DiscoveryError,
            tabs,
            filters = Array.Empty<object>(),
            sections,
            rows = Array.Empty<object>(),
            hasMore = MoreDiscovery,
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
                await LoadDiscoveryAsync(ExploreSource, ExploreKind, reset: true, cancellationToken);
                return new { refreshed = true, count = DiscoverBooks.Count };

            case "section.item.invoke":
            {
                var sid = GetStr(parameters, "sectionId");
                int? siParam = GetInt(parameters, "sectionIndex");
                bool useShelf;
                if (siParam is { } si)
                {
                    if (si is not (0 or 1))
                        throw DebugApiException.InvalidArgument($"sectionIndex {si} out of range (0..1).");
                    useShelf = si == 1;
                }
                else if (!string.IsNullOrWhiteSpace(sid))
                {
                    if (string.Equals(sid, "shelf", StringComparison.OrdinalIgnoreCase) || sid == "书架") useShelf = true;
                    else if (string.Equals(sid, "discover", StringComparison.OrdinalIgnoreCase) || sid == "发现") useShelf = false;
                    else throw DebugApiException.NotFound($"Novel section not found: '{sid}'.");
                }
                else
                {
                    useShelf = false;
                }

                var itemId = GetStr(parameters, "itemId");
                int? iiParam = GetInt(parameters, "itemIndex");
                if (useShelf)
                {
                    var shelf = Library.Entries.Where(e => e.OnShelf).ToArray();
                    NovelEntry chosen;
                    if (iiParam is { } ii)
                    {
                        if (ii < 0 || ii >= shelf.Length) throw DebugApiException.InvalidArgument("Shelf itemIndex out of range.");
                        chosen = shelf[ii];
                    }
                    else if (!string.IsNullOrWhiteSpace(itemId))
                    {
                        chosen = shelf.FirstOrDefault(e => string.Equals(e.BookId, itemId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(e.Book.Name, itemId, StringComparison.OrdinalIgnoreCase))
                            ?? throw DebugApiException.NotFound($"Shelf item '{itemId}' not found.");
                    }
                    else
                    {
                        if (shelf.Length == 0) throw DebugApiException.InvalidState("Shelf is empty.");
                        chosen = shelf[0];
                    }
                    await OpenBookAsync(chosen.Book, cancellationToken);
                    return new { invoked = true, section = "shelf", title = chosen.Book.Name };
                }
                NovelBook book;
                if (iiParam is { } di)
                {
                    if (di < 0 || di >= DiscoverBooks.Count) throw DebugApiException.InvalidArgument("Discover itemIndex out of range.");
                    book = DiscoverBooks[di];
                }
                else if (!string.IsNullOrWhiteSpace(itemId))
                {
                    book = DiscoverBooks.FirstOrDefault(b => string.Equals(b.SourceIdentity, itemId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.Name, itemId, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Discover item '{itemId}' not found.");
                }
                else
                {
                    if (DiscoverBooks.Count == 0) throw DebugApiException.InvalidState("Discover list is empty.");
                    book = DiscoverBooks[0];
                }
                await OpenBookAsync(book, cancellationToken);
                return new { invoked = true, section = "discover", title = book.Name };
            }

            case "tab.select":
            {
                var group = (GetStr(parameters, "group") ?? "category").Trim().ToLowerInvariant();
                var id = GetStr(parameters, "id");
                var idx = GetInt(parameters, "index");
                if (group == "settings-section" || settingsOpen)
                {
                    string key;
                    if (!string.IsNullOrWhiteSpace(id)) key = id;
                    else if (idx is 0) key = "sources";
                    else if (idx is 1) key = "rules";
                    else throw DebugApiException.InvalidArgument("Novel settings tab index must be 0 or 1.");
                    if (!settings.SelectDebugTab(key)) throw DebugApiException.NotFound($"Unknown novel settings tab '{key}'.");
                    return new { group = "settings-section", selected = key };
                }
                NovelKind? kind;
                if (idx is { } ki)
                {
                    if (ki < 0 || ki >= Kinds.Count)
                        throw DebugApiException.InvalidArgument($"Novel category index {ki} out of range (0..{Math.Max(0, Kinds.Count - 1)}).");
                    kind = Kinds[ki];
                }
                else
                {
                    kind = Kinds.FirstOrDefault(k => string.Equals(k.Url, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(k.Title, id, StringComparison.OrdinalIgnoreCase));
                }
                if (kind is null) throw DebugApiException.NotFound("Novel category tab not found.");
                await LoadDiscoveryAsync(ExploreSource, kind, reset: false, cancellationToken);
                return new { group = "category", selected = kind.Title };
            }

            case "list.item.invoke":
            {
                var id = GetStr(parameters, "id");
                var idxParam = GetInt(parameters, "index");
                if (route == BookRoute && Session is { } s)
                {
                    int idx;
                    if (idxParam is { } ci)
                    {
                        if (ci < 0 || ci >= s.Chapters.Count) throw DebugApiException.InvalidArgument("Chapter index out of range.");
                        idx = ci;
                    }
                    else if (!string.IsNullOrWhiteSpace(id))
                    {
                        idx = -1;
                        for (int i = 0; i < s.Chapters.Count; i++)
                            if (string.Equals(s.Chapters[i].ChapterUrl, id, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Chapters[i].Name, id, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                        if (idx < 0) throw DebugApiException.NotFound($"Chapter '{id}' not found.");
                    }
                    else idx = 0;
                    if (idx < 0 || idx >= s.Chapters.Count) throw DebugApiException.InvalidArgument("Chapter index out of range.");
                    await s.ReadAsync(idx, token: cancellationToken);
                    if (s.HasContent) Request(ReaderRoute);
                    return new { invoked = true, chapterIndex = idx, title = s.Chapters[idx].Name };
                }
                if (idxParam is { } di)
                {
                    if (di < 0 || di >= DiscoverBooks.Count) throw DebugApiException.InvalidArgument("List index out of range.");
                    await OpenBookAsync(DiscoverBooks[di], cancellationToken);
                    return new { invoked = true, title = DiscoverBooks[di].Name };
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var found = DiscoverBooks.FirstOrDefault(b => string.Equals(b.SourceIdentity, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.Name, id, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Book '{id}' not found.");
                    await OpenBookAsync(found, cancellationToken);
                    return new { invoked = true, title = found.Name };
                }
                throw DebugApiException.InvalidArgument("List index out of range.");
            }

            case "list.load_more":
                if (!MoreDiscovery) return new { loadedMore = false };
                await LoadDiscoveryAsync(ExploreSource, ExploreKind, reset: false, cancellationToken);
                return new { loadedMore = true, count = DiscoverBooks.Count };

            case "setting.set":
            {
                var key = (GetStr(parameters, "key") ?? "").Trim().ToLowerInvariant();
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                var cur = Library.Appearance;
                NovelAppearance next = key switch
                {
                    "fontsize" or "font_size" when val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var fs) => cur with { FontSize = fs },
                    "lineheight" or "line_height" when val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var lh) => cur with { LineHeight = lh },
                    "theme" when val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var th) => cur with { Theme = th },
                    "mode" when val.ValueKind == JsonValueKind.String => cur with { Mode = val.GetString()! },
                    "conversion" when val.ValueKind == JsonValueKind.String => cur with { Conversion = val.GetString()! },
                    "bold" when val.ValueKind is JsonValueKind.True or JsonValueKind.False => cur with { Bold = val.GetBoolean() },
                    _ => throw DebugApiException.InvalidArgument($"Unsupported novel setting key/value for '{key}'.")
                };
                await Library.SaveAppearanceAsync(next, cancellationToken);
                return new { saved = true, domain = "novel", key, appearance = Library.Appearance };
            }

            case "setting.action":
            {
                var cmd = (GetStr(parameters, "command") ?? "").Trim().ToLowerInvariant();
                if (cmd is "refresh_sources" or "reload_sources")
                {
                    await Sources.RefreshAsync(cancellationToken);
                    return new { executed = true, domain = "novel", command = cmd, sourceCount = Sources.Sources.Count };
                }
                throw DebugApiException.InvalidArgument($"Unsupported novel settings command '{cmd}'.");
            }

            default:
                return null;
        }
    }

    public Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    public Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    public async Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.Novel, "debug.command", "novel." + command, new { @params = parameters });
        switch (command)
        {
            case "open_book":
            {
                var bookId = GetStr(parameters, "bookId") ?? throw DebugApiException.InvalidArgument("open_book requires 'bookId'.");
                var entry = Library.Entries.FirstOrDefault(e => string.Equals(e.BookId, bookId, StringComparison.Ordinal))
                    ?? throw DebugApiException.NotFound($"BookId not found: '{bookId}'.");
                await OpenBookAsync(entry.Book, cancellationToken);
                return new { opened = true, bookId, name = entry.Book.Name };
            }
            case "start_reading":
                if (Session is null) throw DebugApiException.InvalidState("No active novel book session.");
                await StartReadingAsync(cancellationToken);
                return new { reading = route == ReaderRoute, chapter = Session.Chapter?.Name };
            case "select_chapter":
            {
                var s = Session ?? throw DebugApiException.InvalidState("No active novel session.");
                int idx = GetInt(parameters, "index") ?? throw DebugApiException.InvalidArgument("select_chapter requires 'index'.");
                if (idx < 0 || idx >= s.Chapters.Count) throw DebugApiException.InvalidArgument("Chapter index out of range.");
                await s.ReadAsync(idx, token: cancellationToken);
                return new { chapterIndex = idx, title = s.Chapter?.Name };
            }
            case "next_chapter":
            {
                var s = Session ?? throw DebugApiException.InvalidState("No active novel session.");
                int next = (s.Chapter?.Index ?? -1) + 1;
                if (next < 0 || next >= s.Chapters.Count) throw DebugApiException.InvalidState("Already at last chapter.");
                await s.ReadAsync(next, token: cancellationToken);
                return new { chapterIndex = next, title = s.Chapter?.Name };
            }
            case "prev_chapter":
            {
                var s = Session ?? throw DebugApiException.InvalidState("No active novel session.");
                int prev = (s.Chapter?.Index ?? 0) - 1;
                if (prev < 0 || prev >= s.Chapters.Count) throw DebugApiException.InvalidState("Already at first chapter.");
                await s.ReadAsync(prev, token: cancellationToken);
                return new { chapterIndex = prev, title = s.Chapter?.Name };
            }
            case "next_page":
            {
                if (route != ReaderRoute) throw DebugApiException.InvalidState("Not currently in novel reader.");
                bool turned = await view.DebugTurnReaderPageAsync(1);
                return new { turned, pageStatus = view.DebugReaderPageStatus };
            }
            case "prev_page":
            {
                if (route != ReaderRoute) throw DebugApiException.InvalidState("Not currently in novel reader.");
                bool turned = await view.DebugTurnReaderPageAsync(-1);
                return new { turned, pageStatus = view.DebugReaderPageStatus };
            }
            case "switch_source":
            {
                var s = Session ?? throw DebugApiException.InvalidState("No active novel session.");
                var sourceId = GetStr(parameters, "sourceId");
                var sourceIdx = GetInt(parameters, "sourceIndex");
                if (sourceId is null && sourceIdx is null)
                    throw DebugApiException.InvalidArgument("switch_source requires 'sourceId' or 'sourceIndex'.");
                var search = new NovelSearch(Sources, Rules);
                try
                {
                    await search.StartAsync(s.Book.Name, cancellationToken, restart: true);
                    var candidates = search.Groups
                        .SelectMany(g => g.GetProperty("candidates").EnumerateArray())
                        .Select(g =>
                        {
                            var instId = NovelJson.Text(g, "installationId");
                            var src = Sources.Sources.FirstOrDefault(x => x.InstallationId == instId);
                            return src is null ? null : NovelBook.From(g, src);
                        })
                        .OfType<NovelBook>()
                        .ToArray();
                    NovelBook? picked;
                    if (sourceIdx is { } si)
                    {
                        if (si < 0 || si >= candidates.Length)
                            throw DebugApiException.InvalidArgument($"sourceIndex {si} out of range (0..{Math.Max(0, candidates.Length - 1)}).");
                        picked = candidates[si];
                    }
                    else
                    {
                        picked = candidates.FirstOrDefault(c => string.Equals(c.InstallationId, sourceId, StringComparison.OrdinalIgnoreCase));
                        if (picked is null)
                            throw DebugApiException.NotFound($"No matching book candidate found for sourceId '{sourceId}'.");
                    }
                    await s.SwitchSourceAsync(picked, cancellationToken);
                    return new { switched = true, sourceId = s.Book.InstallationId, bookName = s.Book.Name, chapter = s.Chapter?.Name };
                }
                finally
                {
                    await search.PrepareShutdownAsync();
                }
            }
            case "toggle_shelf":
            {
                var s = Session ?? throw DebugApiException.InvalidState("No active novel session.");
                await s.ShelfAsync(cancellationToken);
                return new { onShelf = s.Entry.OnShelf };
            }
            default:
                return null;
        }
    }

    private static string? GetStr(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static int? GetInt(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val : null;
}
