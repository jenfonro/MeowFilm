using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed class NovelDebugAdapter(NovelCapability capability) : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> NovelTargets =
    [
        "novel.home", "novel.data", "novel.book", "novel.reader", "settings.novel"
    ];

    public string DebugModuleId => capability.Id;
    public IReadOnlyList<string> SupportedDebugTargets => NovelTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        if (settingsOpen)
        {
            settingsSection = capability.DebugSettings.CurrentDebugTab;
            return "settings.novel";
        }
        settingsSection = null;
        return capability.Route switch
        {
            NovelCapability.HomeRoute => "novel.home",
            NovelCapability.DataRoute => "novel.data",
            NovelCapability.BookRoute => "novel.book",
            NovelCapability.ReaderRoute => "novel.reader",
            _ => "novel.home"
        };
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.Novel, "debug.nav", "open.target", new { target, @params = parameters });
        switch (target)
        {
            case "novel.home":
            {
                if (DebugParams.GetString(parameters, "sourceId") is { Length: > 0 } sourceId)
                {
                    await capability.Sources.EnsureReadyAsync(cancellationToken);
                    var source = capability.Sources.Sources.FirstOrDefault(s =>
                        string.Equals(s.InstallationId, sourceId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Name, sourceId, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Novel source not found: '{sourceId}'.");
                    capability.SelectExploreSource(source);
                }
                await capability.NavigateAsync(NovelCapability.HomeRoute, cancellationToken);
                return true;
            }
            case "novel.data":
            {
                if (DebugParams.GetString(parameters, "sourceId") is { Length: > 0 } sourceId)
                {
                    await capability.Sources.EnsureReadyAsync(cancellationToken);
                    var source = capability.Sources.Sources.FirstOrDefault(s =>
                        string.Equals(s.InstallationId, sourceId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Name, sourceId, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Novel source not found: '{sourceId}'.");
                    capability.SelectExploreSource(source);
                }
                await capability.EnsureHomeLoadedAsync(cancellationToken);
                if (capability.DataSections.Count == 0)
                    throw DebugApiException.InvalidState("No novel discovery sections available.");
                var category = DebugParams.GetString(parameters, "category") ?? DebugParams.GetString(parameters, "sectionId");
                int? sectionIdx = DebugParams.GetInt(parameters, "sectionIndex");
                NovelDataSection selected = capability.DataSections.Contains(capability.ActiveNovelData)
                    ? capability.ActiveNovelData
                    : capability.DataSections[0];
                if (sectionIdx is { } si)
                {
                    if (si < 0 || si >= capability.DataSections.Count)
                        throw DebugApiException.InvalidArgument($"sectionIndex {si} out of range (0..{capability.DataSections.Count - 1}).");
                    selected = capability.DataSections[si];
                }
                else if (!string.IsNullOrWhiteSpace(category))
                {
                    selected = capability.DataSections.FirstOrDefault(s =>
                        string.Equals(s.Key, category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Title, category, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Novel section not found: '{category}'.");
                }
                await capability.SwitchToDataSectionAsync(selected, cancellationToken);
                return true;
            }
            case "novel.book":
            {
                if (DebugParams.GetString(parameters, "bookId") is { Length: > 0 } bookId)
                {
                    var entry = capability.Library.Entries.FirstOrDefault(e =>
                        string.Equals(e.BookId, bookId, StringComparison.Ordinal) ||
                        string.Equals(e.Book.SourceIdentity, bookId, StringComparison.Ordinal) ||
                        string.Equals(e.Book.BookUrl, bookId, StringComparison.Ordinal));
                    if (entry is not null)
                    {
                        await capability.OpenEntryDirectAsync(entry, cancellationToken);
                        return true;
                    }
                    var card = capability.ActiveNovelData.Items.Concat(capability.Sections.SelectMany(s => s.Items))
                        .OfType<NovelCardItem>()
                        .FirstOrDefault(c =>
                            string.Equals(c.Book.SourceIdentity, bookId, StringComparison.Ordinal) ||
                            string.Equals(c.Book.BookUrl, bookId, StringComparison.Ordinal));
                    if (card is null)
                        throw DebugApiException.NotFound($"Novel bookId not found: '{bookId}'.");
                    await capability.ReceiveBookAsync(card.Book, cancellationToken);
                    await capability.NavigateAsync(NovelCapability.BookRoute, cancellationToken);
                    return true;
                }
                if (capability.Session is null) throw DebugApiException.InvalidState("No active novel book session.");
                await capability.NavigateAsync(NovelCapability.BookRoute, cancellationToken);
                return true;
            }
            case "novel.reader":
            {
                if (capability.Session is null) throw DebugApiException.InvalidState("No active novel session to read.");
                await capability.StartReadingDirectAsync(cancellationToken);
                return true;
            }
            default:
                return false;
        }
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken)
    {
        if (string.Equals(section, "appearance", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(true);
        return Task.FromResult(capability.DebugSettings.SelectDebugTab(section));
    }

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (settingsOpen)
        {
            var snap = capability.DebugSettings.BuildDebugSnapshot();
            return Task.FromResult<object?>(new
            {
                target = "settings.novel",
                kind = "settings",
                loading = false,
                error = string.IsNullOrWhiteSpace(capability.Notice) ? null : capability.Notice,
                tabs = new object[]
                {
                    new { group = "settings-section", index = 0, id = "sources", title = "书源管理", selected = capability.DebugSettings.CurrentDebugTab == "sources", enabled = true },
                    new { group = "settings-section", index = 1, id = "rules", title = "替换净化", selected = capability.DebugSettings.CurrentDebugTab == "rules", enabled = true }
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

        if (capability.Route == NovelCapability.ReaderRoute && capability.Session is { } readerSession)
        {
            return Task.FromResult<object?>(new
            {
                target = "novel.reader",
                kind = "reader",
                loading = readerSession.Loading,
                error = string.IsNullOrWhiteSpace(capability.Notice) ? null : capability.Notice,
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
                    pageStatus = capability.DebugView.DebugReaderPageStatus,
                    onShelf = readerSession.Entry.OnShelf,
                    ruleCount = capability.Library.Rules.Count
                },
                settings = (object?)null
            });
        }

        if (capability.Route == NovelCapability.BookRoute && capability.Session is { } bookSession)
        {
            return Task.FromResult<object?>(new
            {
                target = "novel.book",
                kind = "detail",
                loading = bookSession.Loading,
                error = string.IsNullOrWhiteSpace(capability.Notice) ? null : capability.Notice,
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

        if (capability.IsData)
            return Task.FromResult<object?>(BuildDataDebugContent(maxItems));

        return Task.FromResult<object?>(BuildHomeDebugContent(maxItems));
    }

    private object BuildHomeDebugContent(int maxItems)
    {
        var sections = capability.Sections.Select((sec, si) => new
        {
            index = si,
            id = sec.Key,
            title = sec.Title,
            kind = "novel",
            layout = "rail",
            hasMore = sec.Definition.Options.Count > 0,
            itemCount = sec.Items.Count,
            items = sec.Items.OfType<NovelCardItem>().Take(maxItems).Select((card, ci) => new
            {
                index = ci,
                id = card.Book.SourceIdentity,
                kind = "novel",
                title = card.Book.Name,
                subtitle = card.Book.Author,
                badge = card.Book.LatestChapterTitle,
                coverUrl = card.Book.CoverUrl,
                actions = new[] { "open" },
                meta = new { sourceId = card.Book.InstallationId, bookUrl = card.Book.BookUrl }
            }).ToArray()
        }).ToArray();

        return new
        {
            target = "novel.home",
            kind = sections.Length == 0 ? "empty" : "sections",
            loading = capability.IsDiscoveryBusy,
            error = capability.HomeState == NovelDiscoveryState.Failed
                ? (capability.DiscoveryError.Length > 0 ? capability.DiscoveryError : "LOAD_FAILED")
                : null,
            tabs = Array.Empty<object>(),
            filters = Array.Empty<object>(),
            sections,
            rows = Array.Empty<object>(),
            hasMore = false,
            detail = (object?)null,
            player = (object?)null,
            reader = (object?)null,
            settings = (object?)null,
            selectedSource = capability.ExploreSource is { } src
                ? new { id = src.InstallationId, name = src.Name }
                : null
        };
    }

    private object BuildDataDebugContent(int maxItems)
    {
        var active = capability.DataSections.Contains(capability.ActiveNovelData)
            ? capability.ActiveNovelData
            : null;
        var tabs = capability.DataSections.Select((ds, idx) => new
        {
            group = "category",
            index = idx,
            id = ds.Key,
            title = ds.Title,
            selected = ReferenceEquals(ds, active),
            enabled = true
        }).ToArray();

        var filters = (active?.FilterRows ?? []).SelectMany(row =>
            row.Options.Select((opt, idx) => new
            {
                group = row.Key,
                index = idx,
                id = opt.Value,
                title = opt.Label,
                selected = opt.IsSelected
            })).ToArray();

        var items = (active?.Items ?? []).OfType<NovelCardItem>().Take(maxItems).Select((card, ci) => new
        {
            index = ci,
            id = card.Book.SourceIdentity,
            kind = "novel",
            title = card.Book.Name,
            subtitle = card.Book.Author,
            badge = card.Book.LatestChapterTitle,
            coverUrl = card.Book.CoverUrl,
            actions = new[] { "open" },
            meta = new { sourceId = card.Book.InstallationId, bookUrl = card.Book.BookUrl }
        }).ToArray();

        var sections = active is null
            ? Array.Empty<object>()
            : new object[]
            {
                new
                {
                    index = 0,
                    id = active.Key,
                    title = active.Title,
                    kind = "novel",
                    layout = "grid",
                    hasMore = active.ShowPageAction,
                    itemCount = active.Items.Count,
                    items
                }
            };

        return new
        {
            target = "novel.data",
            kind = "sections",
            loading = active?.State == CatalogSectionState.Loading,
            error = active?.State == CatalogSectionState.Failed ? active.StatusText : null,
            tabs,
            filters,
            sections,
            rows = Array.Empty<object>(),
            hasMore = active?.ShowPageAction ?? false,
            detail = (object?)null,
            player = (object?)null,
            reader = (object?)null,
            settings = (object?)null,
            selectedSource = capability.ExploreSource is { } src
                ? new { id = src.InstallationId, name = src.Name }
                : null
        };
    }

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "refresh":
                if (capability.IsData)
                {
                    await capability.RefreshNovelDataAsync();
                    return new { refreshed = true, route = capability.Route };
                }
                if (capability.IsHome)
                {
                    await capability.RefreshNovelHomeAsync(cancellationToken);
                    return new { refreshed = true, route = capability.Route, sections = capability.Sections.Count };
                }
                return new { refreshed = false, route = capability.Route };

            case "section.more":
            {
                if (!capability.IsHome)
                    throw DebugApiException.InvalidState("section.more is only supported on novel.home.");
                var sec = ResolveHomeSection(parameters);
                await capability.OpenNovelDataAsync(sec);
                return new { invoked = true, section = sec.Title, route = capability.Route };
            }

            case "section.item.invoke":
            {
                NovelBook book;
                if (capability.IsData)
                {
                    book = ResolveNovelCard(capability.ActiveNovelData.Items, parameters);
                }
                else if (capability.IsHome)
                {
                    var sec = ResolveHomeSection(parameters);
                    book = ResolveNovelCard(sec.Items, parameters);
                }
                else
                {
                    throw DebugApiException.InvalidState("section.item.invoke is only supported on novel.home or novel.data.");
                }
                await capability.OpenBookAsync(book, cancellationToken);
                return new { invoked = true, id = book.SourceIdentity, title = book.Name, route = capability.Route };
            }

            case "tab.select":
            {
                var group = (DebugParams.GetString(parameters, "group") ?? "").Trim().ToLowerInvariant();
                var id = DebugParams.GetString(parameters, "id");
                var idx = DebugParams.GetInt(parameters, "index");
                if (group == "settings-section" || settingsOpen)
                {
                    string key;
                    if (!string.IsNullOrWhiteSpace(id)) key = id;
                    else if (idx is 0) key = "sources";
                    else if (idx is 1) key = "rules";
                    else throw DebugApiException.InvalidArgument("Novel settings tab index must be 0 or 1.");
                    if (!capability.DebugSettings.SelectDebugTab(key)) throw DebugApiException.NotFound($"Unknown novel settings tab '{key}'.");
                    return new { group = "settings-section", selected = key };
                }

                if (capability.IsData && capability.ActiveNovelData.FilterRows.Count > 0 &&
                    (group == "" || group == "filter" || group == "category" && capability.ActiveNovelData.FilterRows.Any(r => r.Key == "category")))
                {
                    // Prefer filter option selection when group is "filter" or matches an option in ActiveNovelData.
                    var row = capability.ActiveNovelData.FilterRows[0];
                    if (group == "filter" ||
                        (group != "section" && (idx is not null || row.Options.Any(o =>
                            string.Equals(o.Value, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(o.Label, id, StringComparison.OrdinalIgnoreCase)))))
                    {
                        CatalogFilterOption? option = null;
                        if (idx is { } fi)
                        {
                            if (fi < 0 || fi >= row.Options.Count)
                                throw DebugApiException.InvalidArgument($"Filter index {fi} out of range (0..{row.Options.Count - 1}).");
                            option = row.Options[fi];
                        }
                        else if (!string.IsNullOrWhiteSpace(id))
                        {
                            option = row.Options.FirstOrDefault(o =>
                                string.Equals(o.Value, id, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(o.Label, id, StringComparison.OrdinalIgnoreCase));
                        }
                        if (option is null) throw DebugApiException.NotFound($"Novel filter option not found: '{id}'.");
                        await capability.SelectNovelDataFilterAsync(option);
                        return new { group = row.Key, selected = option.Label, id = option.Value };
                    }
                }

                if (capability.DataSections.Count == 0)
                    throw DebugApiException.InvalidState("No novel sections available.");
                NovelDataSection? target = null;
                if (idx is { } ci)
                {
                    if (ci < 0 || ci >= capability.DataSections.Count)
                        throw DebugApiException.InvalidArgument($"Novel section index {ci} out of range (0..{capability.DataSections.Count - 1}).");
                    target = capability.DataSections[ci];
                }
                else if (!string.IsNullOrWhiteSpace(id))
                {
                    target = capability.DataSections.FirstOrDefault(d =>
                        string.Equals(d.Key, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Title, id, StringComparison.OrdinalIgnoreCase));
                }
                if (target is null) throw DebugApiException.NotFound("Novel section tab not found.");
                await capability.SwitchToDataSectionAsync(target, cancellationToken);
                return new { group = "section", selected = target.Title };
            }

            case "list.item.invoke":
            {
                var id = DebugParams.GetString(parameters, "id");
                var idxParam = DebugParams.GetInt(parameters, "index");
                if (capability.Route == NovelCapability.BookRoute && capability.Session is { } s)
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
                    if (s.HasContent) await capability.OpenReaderDirectAsync(cancellationToken);
                    return new { invoked = true, chapterIndex = idx, title = s.Chapters[idx].Name };
                }
                if (capability.IsData)
                {
                    var book = ResolveNovelCardByListParams(capability.ActiveNovelData.Items, id, idxParam);
                    await capability.OpenBookAsync(book, cancellationToken);
                    return new { invoked = true, title = book.Name };
                }
                throw DebugApiException.InvalidState($"list.item.invoke is not supported on route '{capability.Route}'.");
            }

            case "list.load_more":
                if (capability.IsData)
                {
                    await capability.LoadMoreNovelDataAsync();
                    return new { loadedMore = true, count = capability.ActiveNovelData.Items.Count };
                }
                return new { loadedMore = false };

            case "setting.set":
            {
                var key = (DebugParams.GetString(parameters, "key") ?? "").Trim().ToLowerInvariant();
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                var cur = capability.Library.Appearance;
                NovelAppearance next = key switch
                {
                    "fontsize" or "font_size" when val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var fs) => cur with { FontSize = fs },
                    "lineheight" or "line_height" when val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var lh) => cur with { LineHeight = lh },
                    "theme" when val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var th) &&
                        th >= 0 && th < NovelAppearance.Themes.Length =>
                        cur with { Theme = th, Background = NovelAppearance.Themes[th].Background, Foreground = NovelAppearance.Themes[th].Foreground },
                    "mode" when val.ValueKind == JsonValueKind.String => cur with { Mode = val.GetString()! },
                    "conversion" when val.ValueKind == JsonValueKind.String => cur with { Conversion = val.GetString()! },
                    "bold" when val.ValueKind is JsonValueKind.True or JsonValueKind.False => cur with { Bold = val.GetBoolean() },
                    _ => throw DebugApiException.InvalidArgument($"Unsupported novel setting key/value for '{key}'.")
                };
                await capability.Library.SaveAppearanceAsync(next, cancellationToken);
                return new { saved = true, domain = "novel", key, appearance = capability.Library.Appearance };
            }

            case "setting.action":
            {
                var cmd = (DebugParams.GetString(parameters, "command") ?? "").Trim().ToLowerInvariant();
                if (cmd is "refresh_sources" or "reload_sources")
                {
                    await capability.Sources.RefreshAsync(cancellationToken);
                    return new { executed = true, domain = "novel", command = cmd, sourceCount = capability.Sources.Sources.Count };
                }
                if (cmd is "import_sources")
                {
                    var text = DebugParams.GetString(parameters, "text");
                    var url = DebugParams.GetString(parameters, "url");
                    var fileName = DebugParams.GetString(parameters, "fileName");
                    var imported = await capability.Sources.ImportAsync(text, url, fileName, cancellationToken);
                    return new { executed = true, domain = "novel", command = cmd, imported, sourceCount = capability.Sources.Sources.Count };
                }
                throw DebugApiException.InvalidArgument($"Unsupported novel settings command '{cmd}'.");
            }

            default:
                return null;
        }
    }

    private NovelHomeSection ResolveHomeSection(JsonElement? parameters)
    {
        if (capability.Sections.Count == 0) throw DebugApiException.InvalidState("No novel sections available.");
        if (DebugParams.GetInt(parameters, "sectionIndex") is { } si)
        {
            if (si < 0 || si >= capability.Sections.Count)
                throw DebugApiException.InvalidArgument($"sectionIndex {si} out of range (0..{capability.Sections.Count - 1}).");
            return capability.Sections[si];
        }
        if (DebugParams.GetString(parameters, "sectionId") is { Length: > 0 } sid)
        {
            return capability.Sections.FirstOrDefault(s =>
                string.Equals(s.Key, sid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, sid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Novel section '{sid}' not found.");
        }
        return capability.Sections[0];
    }

    private static NovelBook ResolveNovelCard(IReadOnlyList<ICatalogCardDisplay> rawItems, JsonElement? parameters)
    {
        var items = rawItems.OfType<NovelCardItem>().ToArray();
        if (items.Length == 0) throw DebugApiException.InvalidState("Target novel section has no items.");
        if (DebugParams.GetInt(parameters, "itemIndex") is { } ii)
        {
            if (ii < 0 || ii >= items.Length)
                throw DebugApiException.InvalidArgument($"itemIndex {ii} out of range (0..{items.Length - 1}).");
            return items[ii].Book;
        }
        if (DebugParams.GetString(parameters, "itemId") is { Length: > 0 } iid)
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Book.SourceIdentity, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Book.BookUrl, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Book.Name, iid, StringComparison.OrdinalIgnoreCase))?.Book
                ?? throw DebugApiException.NotFound($"Novel item '{iid}' not found.");
        }
        return items[0].Book;
    }

    private static NovelBook ResolveNovelCardByListParams(IReadOnlyList<ICatalogCardDisplay> rawItems, string? id, int? index)
    {
        var items = rawItems.OfType<NovelCardItem>().ToArray();
        if (items.Length == 0) throw DebugApiException.InvalidState("Target novel section has no items.");
        if (index is { } ii)
        {
            if (ii < 0 || ii >= items.Length)
                throw DebugApiException.InvalidArgument($"index {ii} out of range (0..{items.Length - 1}).");
            return items[ii].Book;
        }
        if (!string.IsNullOrWhiteSpace(id))
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Book.SourceIdentity, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Book.BookUrl, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Book.Name, id, StringComparison.OrdinalIgnoreCase))?.Book
                ?? throw DebugApiException.NotFound($"Novel item '{id}' not found.");
        }
        return items[0].Book;
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
                var bookId = DebugParams.GetString(parameters, "bookId") ?? throw DebugApiException.InvalidArgument("open_book requires 'bookId'.");
                var entry = capability.Library.Entries.FirstOrDefault(e => string.Equals(e.BookId, bookId, StringComparison.Ordinal))
                    ?? throw DebugApiException.NotFound($"BookId not found: '{bookId}'.");
                await capability.OpenEntryDirectAsync(entry, cancellationToken);
                return new { opened = true, bookId, name = entry.Book.Name };
            }
            case "start_reading":
                if (capability.Session is null) throw DebugApiException.InvalidState("No active novel book session.");
                await capability.StartReadingDirectAsync(cancellationToken);
                return new { reading = capability.Route == NovelCapability.ReaderRoute, chapter = capability.Session.Chapter?.Name };
            case "select_chapter":
            {
                var s = capability.Session ?? throw DebugApiException.InvalidState("No active novel session.");
                int idx = DebugParams.GetInt(parameters, "index") ?? throw DebugApiException.InvalidArgument("select_chapter requires 'index'.");
                if (idx < 0 || idx >= s.Chapters.Count) throw DebugApiException.InvalidArgument("Chapter index out of range.");
                await s.ReadAsync(idx, token: cancellationToken);
                return new { chapterIndex = idx, title = s.Chapter?.Name };
            }
            case "next_chapter":
            {
                var s = capability.Session ?? throw DebugApiException.InvalidState("No active novel session.");
                int next = (s.Chapter?.Index ?? -1) + 1;
                if (next < 0 || next >= s.Chapters.Count) throw DebugApiException.InvalidState("Already at last chapter.");
                await s.ReadAsync(next, token: cancellationToken);
                return new { chapterIndex = next, title = s.Chapter?.Name };
            }
            case "prev_chapter":
            {
                var s = capability.Session ?? throw DebugApiException.InvalidState("No active novel session.");
                int prev = (s.Chapter?.Index ?? 0) - 1;
                if (prev < 0 || prev >= s.Chapters.Count) throw DebugApiException.InvalidState("Already at first chapter.");
                await s.ReadAsync(prev, token: cancellationToken);
                return new { chapterIndex = prev, title = s.Chapter?.Name };
            }
            case "next_page":
            {
                if (capability.Route != NovelCapability.ReaderRoute) throw DebugApiException.InvalidState("Not currently in novel reader.");
                bool turned = await capability.DebugView.DebugTurnReaderPageAsync(1);
                return new { turned, pageStatus = capability.DebugView.DebugReaderPageStatus };
            }
            case "prev_page":
            {
                if (capability.Route != NovelCapability.ReaderRoute) throw DebugApiException.InvalidState("Not currently in novel reader.");
                bool turned = await capability.DebugView.DebugTurnReaderPageAsync(-1);
                return new { turned, pageStatus = capability.DebugView.DebugReaderPageStatus };
            }
            case "switch_source":
            {
                var s = capability.Session ?? throw DebugApiException.InvalidState("No active novel session.");
                var sourceId = DebugParams.GetString(parameters, "sourceId");
                var sourceIdx = DebugParams.GetInt(parameters, "sourceIndex");
                if (sourceId is null && sourceIdx is null)
                    throw DebugApiException.InvalidArgument("switch_source requires 'sourceId' or 'sourceIndex'.");
                var search = new NovelSearch(capability.Sources, capability.Rules, s.Book);
                try
                {
                    await search.StartAsync(s.Book.Name, cancellationToken, restart: true);
                    var candidates = search.Groups
                        .SelectMany(g => g.GetProperty("candidates").EnumerateArray())
                        .Select(g =>
                        {
                            var instId = NovelJson.Text(g, "installationId");
                            var src = capability.Sources.Sources.FirstOrDefault(x => x.InstallationId == instId);
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
                    bool confirmedUnknown = DebugParams.GetBool(parameters, "confirmUnknown") ?? false;
                    await s.SwitchSourceAsync(picked, cancellationToken, confirmedUnknown: confirmedUnknown);
                    return new { switched = true, sourceId = s.Book.InstallationId, bookName = s.Book.Name, chapter = s.Chapter?.Name };
                }
                finally
                {
                    await search.PrepareShutdownAsync();
                }
            }
            case "toggle_shelf":
            {
                var s = capability.Session ?? throw DebugApiException.InvalidState("No active novel session.");
                await s.ShelfAsync(cancellationToken);
                return new { onShelf = s.Entry.OnShelf };
            }
            default:
                return null;
        }
    }
}
