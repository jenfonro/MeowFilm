using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed class HistoryDebugAdapter(HistoryPage page) : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> Targets =
    [
        "history", "history.film", "history.novel"
    ];

    public string DebugModuleId => page.Id;
    public IReadOnlyList<string> SupportedDebugTargets => Targets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        settingsSection = null;
        return "history";
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        string? wantedTab = target switch
        {
            "history.film" => "film",
            "history.novel" => "novel",
            "history" => DebugParams.GetString(parameters, "tab") ?? DebugParams.GetString(parameters, "domain"),
            _ => null
        };
        if (target is not ("history" or "history.film" or "history.novel"))
            return false;

        if (!string.IsNullOrWhiteSpace(wantedTab))
        {
            var normalizedTab = wantedTab.Trim().ToLowerInvariant();
            if (normalizedTab is not ("film" or "novel"))
                throw DebugApiException.InvalidArgument($"Unsupported history tab '{wantedTab}' (expected 'film' or 'novel').");
            await page.SelectTabAsync(normalizedTab, cancellationToken);
        }
        else
        {
            page.ActiveController.TryExitData();
        }

        if (DebugParams.GetString(parameters, "section") is { Length: > 0 } section)
            page.ActiveController.OpenDataSection(section);
        return true;
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        var controller = page.ActiveController;
        var tabs = page.Tabs.Select((t, idx) => new
        {
            group = "domain",
            index = idx,
            id = t.Id,
            title = t.Label,
            selected = t.Selected,
            enabled = true
        }).ToArray();

        string domainKind = controller.Domain;
        object[] sections;
        if (controller.IsData)
        {
            var active = controller.ActiveData;
            sections =
            [
                new
                {
                    index = 0,
                    id = active.Key,
                    title = active.Title,
                    kind = domainKind,
                    layout = "grid",
                    hasMore = active.ShowPageAction,
                    itemCount = active.Items.Count,
                    items = active.Items.Take(maxItems).Select((card, ci) => ProjectCard(card, ci, domainKind)).ToArray()
                }
            ];
        }
        else
        {
            sections = controller.Sections.Select((sec, si) => (object)new
            {
                index = si,
                id = sec.Key,
                title = sec.Title,
                kind = domainKind,
                layout = "rail",
                hasMore = true,
                itemCount = sec.Items.Count,
                items = sec.Items.Take(maxItems).Select((card, ci) => ProjectCard(card, ci, domainKind)).ToArray()
            }).ToArray();
        }

        var sectionError = controller.IsData
            ? (controller.ActiveData.State == CatalogSectionState.Failed ? controller.ActiveData.StatusText : "")
            : controller.Sections.FirstOrDefault(s => s.State == CatalogSectionState.Failed)?.StatusText ?? "";

        return Task.FromResult<object?>(new
        {
            target = "history",
            kind = "sections",
            selectedTab = page.SelectedTab,
            viewMode = controller.IsData ? "grid" : "home",
            loading = controller.IsLoading,
            error = sectionError.Length > 0 ? sectionError : null,
            tabs,
            filters = Array.Empty<object>(),
            sections,
            rows = Array.Empty<object>(),
            hasMore = controller.IsData && controller.ActiveData.ShowPageAction,
            detail = (object?)null,
            player = (object?)null,
            reader = (object?)null,
            settings = (object?)null
        });
    }

    private static object ProjectCard(ICatalogCardDisplay card, int index, string kind) => new
    {
        index,
        id = card.Id,
        kind,
        title = card.Title,
        subtitle = card.Subtitle,
        badge = card.TextBadge,
        score = card.Score,
        poster = card.NovelBook is not null ? card.NovelBook.CoverUrl : card.Poster,
        actions = new[] { "open" }
    };

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        var controller = page.ActiveController;
        switch (action)
        {
            case "refresh":
                await controller.ReloadAsync(cancellationToken);
                return new { refreshed = true, tab = page.SelectedTab };

            case "tab.select":
            {
                var id = DebugParams.GetString(parameters, "id");
                var idx = DebugParams.GetInt(parameters, "index");
                string? targetId = idx switch
                {
                    0 => "film",
                    1 => "novel",
                    not null => throw DebugApiException.InvalidArgument($"History tab index {idx} out of range (0..1)."),
                    null => id
                };
                if (string.IsNullOrWhiteSpace(targetId))
                    throw DebugApiException.InvalidArgument("Provide 'id' or 'index' for tab.select.");
                var matched = page.Tabs.FirstOrDefault(t =>
                    string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Label, targetId, StringComparison.OrdinalIgnoreCase))
                    ?? throw DebugApiException.NotFound($"History tab not found: '{targetId}'.");
                await page.SelectTabAsync(matched.Id, cancellationToken);
                return new { group = "domain", selected = matched.Id };
            }

            case "section.more":
            {
                if (!controller.IsHome)
                    throw DebugApiException.InvalidState("section.more is only supported on history home view.");
                var sec = ResolveSection(controller, parameters);
                await controller.OpenSectionAsync(sec);
                return new { invoked = true, tab = page.SelectedTab, section = sec.Key, title = sec.Title };
            }

            case "section.item.invoke":
            {
                ICatalogCardDisplay card;
                if (controller.IsData)
                {
                    card = ResolveCard(controller.ActiveData.Items, parameters, "itemIndex", "itemId");
                }
                else
                {
                    var sec = ResolveSection(controller, parameters);
                    card = ResolveCard(sec.Items, parameters, "itemIndex", "itemId");
                }
                await controller.ActivateCardAsync(card);
                return new { invoked = true, tab = page.SelectedTab, id = card.Id, title = card.Title };
            }

            case "list.item.invoke":
            {
                if (!controller.IsData)
                    throw DebugApiException.InvalidState("list.item.invoke is only supported in secondary grid mode; use section.item.invoke on home mode.");
                var card = ResolveCard(controller.ActiveData.Items, parameters, "index", "id");
                await controller.ActivateCardAsync(card);
                return new { invoked = true, tab = page.SelectedTab, id = card.Id, title = card.Title };
            }

            case "list.load_more":
                if (controller.IsData)
                {
                    await controller.LoadMoreDataAsync();
                    return new { loadedMore = true, count = controller.ActiveData.Items.Count };
                }
                return new { loadedMore = false };

            case "setting.action":
            {
                var cmd = (DebugParams.GetString(parameters, "command") ?? "").Trim().ToLowerInvariant();
                if (cmd == "clear_history")
                {
                    var dom = DebugParams.GetString(parameters, "domain")?.Trim().ToLowerInvariant() ?? page.SelectedTab;
                    var targetController = dom == "novel" ? page.NovelController : page.FilmController;
                    await targetController.ClearHistoryDirectAsync(cancellationToken);
                    return new { executed = true, domain = targetController.Domain, command = cmd };
                }
                if (cmd == "request_clear_history")
                {
                    var dom = DebugParams.GetString(parameters, "domain")?.Trim().ToLowerInvariant() ?? page.SelectedTab;
                    var targetController = dom == "novel" ? page.NovelController : page.FilmController;
                    _ = targetController.InvokeSectionActionAsync(targetController.HistorySection, page.DialogOwner);
                    return new { requested = true, domain = targetController.Domain, command = cmd };
                }
                return null;
            }

            default:
                return null;
        }
    }

    private static HistoryHomeSection ResolveSection(HistoryDomainController controller, JsonElement? parameters)
    {
        if (DebugParams.GetInt(parameters, "sectionIndex") is { } si)
        {
            if (si < 0 || si >= controller.Sections.Count)
                throw DebugApiException.InvalidArgument($"sectionIndex {si} out of range (0..{controller.Sections.Count - 1}).");
            return controller.Sections[si];
        }
        if (DebugParams.GetString(parameters, "sectionId") is { Length: > 0 } sid)
        {
            return controller.Sections.FirstOrDefault(s =>
                string.Equals(s.Key, sid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, sid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"History section '{sid}' not found.");
        }
        return controller.Sections[0];
    }

    private static ICatalogCardDisplay ResolveCard(
        IReadOnlyList<ICatalogCardDisplay> items,
        JsonElement? parameters,
        string indexKey,
        string idKey)
    {
        if (items.Count == 0)
            throw DebugApiException.InvalidState("Target history section has no items.");
        if (DebugParams.GetInt(parameters, indexKey) is { } idx)
        {
            if (idx < 0 || idx >= items.Count)
                throw DebugApiException.InvalidArgument($"{indexKey} {idx} out of range (0..{items.Count - 1}).");
            return items[idx];
        }
        if (DebugParams.GetString(parameters, idKey) is { Length: > 0 } id)
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"History item '{id}' not found.");
        }
        return items[0];
    }

    public Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    public Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    public Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
}
