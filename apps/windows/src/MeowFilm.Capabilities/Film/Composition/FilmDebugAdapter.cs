using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed partial class FilmDebugAdapter(FilmCapability capability) : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> FilmTargets =
    [
        "film.home", "film.data", "film.detail", "film.playback", "settings.film"
    ];

    public string DebugModuleId => capability.Id;
    public IReadOnlyList<string> SupportedDebugTargets => FilmTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        if (settingsOpen)
        {
            settingsSection = capability.DebugSettings.CurrentDebugSection ?? "rules";
            return "settings.film";
        }
        settingsSection = null;
        return capability.DebugRoute switch
        {
            FilmCapability.HomeRoute => "film.home",
            FilmCapability.DataRoute => "film.data",
            FilmCapability.PlaybackRoute => "film.playback",
            _ => "film.home"
        };
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.FilmCatalog, "debug.nav", "open.target", new { target, @params = parameters });
        switch (target)
        {
            case "film.home":
                await capability.NavigateAsync(FilmCapability.HomeRoute, cancellationToken);
                return true;
            case "film.data":
            {
                var category = DebugParams.GetString(parameters, "category");
                FilmDataSection selected = capability.ActiveData;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    selected = capability.DataSections.FirstOrDefault(s =>
                        string.Equals(s.Key, category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Title, category, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Film category not found: '{category}'.");
                }
                await capability.SwitchToDataSectionAsync(selected, cancellationToken);
                return true;
            }
            case "film.detail":
            case "film.playback":
                if (capability.Playback is null)
                    throw DebugApiException.InvalidState("No active film playback session; open a film card first.");
                await capability.NavigateAsync(FilmCapability.PlaybackRoute, cancellationToken);
                return true;
            default:
                return false;
        }
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        capability.DebugSettings.SelectDebugSectionAsync(section);

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (settingsOpen) return Task.FromResult<object?>(BuildSettingsDebugContent());
        if (capability.IsHome) return Task.FromResult<object?>(BuildHomeDebugContent(maxItems));
        if (capability.IsData) return Task.FromResult<object?>(BuildDataDebugContent(maxItems));
        return Task.FromResult<object?>(BuildPlaybackDebugContent(maxItems));
    }

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "refresh":
                if (capability.IsData) { await capability.RefreshDataAsync(); return new { refreshed = true, route = capability.DebugRoute }; }
                if (capability.IsHome)
                {
                    foreach (var sec in capability.Sections) await capability.RefreshSectionAsync(sec);
                    return new { refreshed = true, route = capability.DebugRoute };
                }
                if (capability.IsPlayback && capability.Playback is not null) { await capability.Playback.RetryLoadingAsync(); return new { refreshed = true, route = capability.DebugRoute }; }
                return new { refreshed = false };

            case "section.item.invoke":
            {
                FilmCardItem card;
                if (capability.IsData)
                {
                    card = ResolveFilmCard(capability.ActiveData.Items, parameters);
                }
                else if (capability.IsHome)
                {
                    var sec = ResolveFilmSection(parameters);
                    card = ResolveFilmCard(sec.Items, parameters);
                }
                else
                {
                    throw DebugApiException.InvalidState("section.item.invoke is only supported on film.home or film.data.");
                }
                DebugLog.Info(DebugCategories.FilmCatalog, "debug.action", "section.item.invoke", new { id = card.Id, title = card.Title });
                await capability.OpenPlaybackAsync(card);
                return new { invoked = true, id = card.Id, title = card.Title, route = capability.DebugRoute };
            }

            case "section.more":
            {
                if (!capability.IsHome)
                    throw DebugApiException.InvalidState("section.more is only supported on film.home.");
                var sec = ResolveFilmSection(parameters);
                var dataSec = capability.DataSections.FirstOrDefault(d => d.Key == sec.Key) ?? capability.DataSections[0];
                await capability.SwitchToDataSectionAsync(dataSec, cancellationToken);
                return new { invoked = true, section = sec.Title, route = capability.DebugRoute };
            }

            case "tab.select":
            {
                var group = (DebugParams.GetString(parameters, "group") ?? "category").Trim().ToLowerInvariant();
                if (group == "settings-section" || settingsOpen)
                    return await TryHandleSettingsTabSelectAsync(parameters);
                var id = DebugParams.GetString(parameters, "id");
                var idx = DebugParams.GetInt(parameters, "index");
                FilmDataSection? target;
                if (idx is { } ci)
                {
                    if (ci < 0 || ci >= capability.DataSections.Count)
                        throw DebugApiException.InvalidArgument($"Film category index {ci} out of range (0..{capability.DataSections.Count - 1}).");
                    target = capability.DataSections[ci];
                }
                else
                {
                    target = capability.DataSections.FirstOrDefault(d => string.Equals(d.Key, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.Title, id, StringComparison.OrdinalIgnoreCase));
                }
                if (target is null) throw DebugApiException.NotFound("Film category tab not found.");
                await capability.SwitchToDataSectionAsync(target, cancellationToken);
                return new { group = "category", selected = target.Title };
            }

            case "list.load_more":
                if (capability.IsData) { await capability.LoadMoreDataAsync(); return new { loadedMore = true }; }
                return new { loadedMore = false };

            case "setting.set":
                return await TryHandleSettingSetAsync(parameters, cancellationToken);

            case "setting.action":
                return await TryHandleSettingActionAsync(parameters, cancellationToken);

            default:
                return null;
        }
    }

    public async Task<byte[]?> CaptureDebugAuxiliaryWindowPngAsync(string window, CancellationToken cancellationToken)
    {
        if (string.Equals(window, "film-player", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(window, "pip", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(window, "fullscreen", StringComparison.OrdinalIgnoreCase))
        {
            return await capability.DebugView.PlaybackViewControl.CapturePlayerWindowPngAsync(cancellationToken);
        }
        return null;
    }

    public Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    private object BuildHomeDebugContent(int maxItems)
    {
        var sections = capability.Sections.Select((sec, si) => new
        {
            index = si,
            id = sec.Key,
            title = sec.Title,
            kind = "film",
            layout = "rail",
            hasMore = true,
            itemCount = sec.Items.Count,
            items = sec.Items.Take(maxItems).Select((card, ci) => new
            {
                index = ci,
                id = card.Id,
                kind = "film",
                title = card.Title,
                subtitle = card.Score,
                badge = card.Score,
                coverUrl = card.Poster,
                actions = new[] { "open", "play" },
                meta = new { }
            }).ToArray()
        }).ToArray();
        return new
        {
            target = "film.home",
            kind = "sections",
            loading = false,
            error = (string?)null,
            tabs = Array.Empty<object>(),
            filters = Array.Empty<object>(),
            sections,
            rows = Array.Empty<object>(),
            hasMore = false,
            detail = (object?)null,
            player = BuildFilmPlayerSnapshot(),
            reader = (object?)null,
            settings = (object?)null
        };
    }

    private object BuildDataDebugContent(int maxItems)
    {
        var tabs = capability.DataSections.Select((ds, idx) => new
        {
            group = "category",
            index = idx,
            id = ds.Key,
            title = ds.Title,
            selected = ReferenceEquals(ds, capability.ActiveData),
            enabled = true
        }).ToArray();
        var items = capability.ActiveData.Items.Take(maxItems).Select((card, ci) => new
        {
            index = ci,
            id = card.Id,
            kind = "film",
            title = card.Title,
            subtitle = card.Score,
            badge = card.Score,
            coverUrl = card.Poster,
            actions = new[] { "open", "play" },
            meta = new { }
        }).ToArray();
        return new
        {
            target = "film.data",
            kind = "sections",
            loading = capability.ActiveData.State == CatalogSectionState.Loading,
            error = capability.ActiveData.State == CatalogSectionState.Failed ? "LOAD_FAILED" : null,
            tabs,
            filters = Array.Empty<object>(),
            sections = new object[]
            {
                new
                {
                    index = 0,
                    id = capability.ActiveData.Key,
                    title = capability.ActiveData.Title,
                    kind = "film",
                    layout = "grid",
                    hasMore = capability.ActiveData.ShowPageAction,
                    itemCount = capability.ActiveData.Items.Count,
                    items
                }
            },
            rows = Array.Empty<object>(),
            hasMore = capability.ActiveData.ShowPageAction,
            detail = (object?)null,
            player = BuildFilmPlayerSnapshot(),
            reader = (object?)null,
            settings = (object?)null
        };
    }

    private FilmHomeSection ResolveFilmSection(JsonElement? parameters)
    {
        if (capability.Sections.Count == 0) throw DebugApiException.InvalidState("No film sections available.");
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
                ?? throw DebugApiException.NotFound($"Film section '{sid}' not found.");
        }
        return capability.Sections[0];
    }

    private static FilmCardItem ResolveFilmCard(IReadOnlyList<ICatalogCardDisplay> rawItems, JsonElement? parameters)
    {
        var items = rawItems.OfType<FilmCardItem>().ToArray();
        if (items.Length == 0) throw DebugApiException.InvalidState("Target film section has no items.");
        if (DebugParams.GetInt(parameters, "itemIndex") is { } ii)
        {
            if (ii < 0 || ii >= items.Length)
                throw DebugApiException.InvalidArgument($"itemIndex {ii} out of range (0..{items.Length - 1}).");
            return items[ii];
        }
        if (DebugParams.GetString(parameters, "itemId") is { Length: > 0 } iid)
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Id, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Title, iid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Film item '{iid}' not found.");
        }
        return items[0];
    }
}
