using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed partial class MusicDebugAdapter
{
    private async Task ApplyCatalogPlatformParamAsync(JsonElement? parameters)
    {
        if (GetStr(parameters, "platform") is not { Length: > 0 } platformKey) return;
        var p = capability.Catalog.CatalogPlatforms.FirstOrDefault(x => string.Equals(x.Key, platformKey, StringComparison.OrdinalIgnoreCase));
        if (p is null) throw DebugApiException.NotFound($"Music catalog platform not found: '{platformKey}'.");
        await capability.Catalog.ChangeCatalogPlatformAsync(p);
    }

    private object BuildCatalogOrLibraryContent(int maxItems, bool includeRaw)
    {
        var catalog = capability.Catalog;
        var library = capability.Library;
        var search = capability.Search;
        var playback = capability.Playback;
        var currentTarget = DescribeCurrentTarget(false, out _) ?? "music.home";

        if (catalog.IsCatalogLanding || catalog.IsCatalogContent && !catalog.IsCatalogDetailPage && !library.IsSongList)
        {
            var tabs = new List<object>();
            for (int i = 0; i < catalog.ChannelTabs.Count; i++)
            {
                var ch = catalog.ChannelTabs[i];
                tabs.Add(new
                {
                    group = "channel",
                    index = i,
                    id = ch.ActionId.Length > 0 ? ch.ActionId : ch.Id,
                    title = ch.Title,
                    selected = !string.IsNullOrEmpty(catalog.CurrentChannelKey) &&
                        (string.Equals(ch.ActionId, catalog.CurrentChannelKey, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(ch.Id, catalog.CurrentChannelKey, StringComparison.OrdinalIgnoreCase)),
                    enabled = ch.Available
                });
            }
            for (int i = 0; i < catalog.CatalogPlatforms.Count; i++)
            {
                var plat = catalog.CatalogPlatforms[i];
                tabs.Add(new
                {
                    group = "platform",
                    index = i,
                    id = plat.Key,
                    title = plat.Name,
                    selected = plat.Key == catalog.SelectedCatalogPlatform.Key,
                    enabled = true
                });
            }

            var filters = catalog.CatalogTags.Select((tag, idx) => new
            {
                group = "filter",
                index = idx,
                id = tag.Id,
                title = tag.Name,
                selected = tag.Selected
            }).ToArray();

            var sections = ProjectCatalogSections(catalog.DisplayCatalogSections, maxItems, includeRaw);
            return new
            {
                target = currentTarget,
                kind = "sections",
                loading = capability.LoadCancellation is not null || catalog.ShowCatalogLoading,
                error = string.IsNullOrWhiteSpace(catalog.Status) ? null : catalog.Status,
                tabs,
                filters,
                sections,
                rows = Array.Empty<object>(),
                hasMore = catalog.CanLoadMoreCatalogPlaylist || catalog.CanCatalogNext,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            };
        }

        if (library.IsArchives)
        {
            var rows = library.Archives.Take(maxItems).Select((a, idx) => new
            {
                index = idx,
                id = a.Id,
                title = a.Title,
                subtitle = $"{a.Count} 首",
                selected = false,
                state = "archived",
                actions = new[] { "open", "delete" }
            }).ToArray();
            return new
            {
                target = currentTarget,
                kind = "list",
                loading = capability.LoadCancellation is not null,
                error = string.IsNullOrWhiteSpace(library.Status) ? null : library.Status,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = false,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            };
        }

        var activeSongs = search.IsSearchPage ? search.SearchSongsSource :
            catalog.IsCatalogPage ? catalog.CatalogSongsSource : library.LibrarySongsSource;
        var activeSelection = search.IsSearchPage ? search.SearchSelectedSong :
            catalog.IsCatalogPage ? catalog.CatalogSelectedSong : library.SelectedSong;
        var songRows = activeSongs.Take(maxItems).Select((song, idx) => new
        {
            index = idx,
            id = song.Id,
            title = song.Title,
            subtitle = string.IsNullOrEmpty(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}",
            duration = song.Duration,
            selected = ReferenceEquals(song, activeSelection),
            state = song.Id == playback.CurrentSongId ? (playback.IsPlaying ? "playing" : "current") : "idle",
            actions = new[] { "play", "select", "favorite" },
            raw = includeRaw ? (JsonElement?)Library.MusicNodeSongCodec.SongToElement(song.Record) : null
        }).ToArray();

        var dateFilters = library.IsHistory
            ? library.HistoryDates.Select((d, idx) => new { group = "filter", index = idx, id = d, title = d, selected = d == library.HistoryDate }).ToArray()
            : Array.Empty<object>();

        var collection = library.CurrentCollection;
        return new
        {
            target = currentTarget,
            kind = "list",
            loading = capability.LoadCancellation is not null || search.ShowSearchLoading || catalog.ShowDetailLoading,
            error = string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus,
            tabs = Array.Empty<object>(),
            filters = dateFilters,
            sections = ProjectCatalogSections(catalog.DisplayCatalogSections, maxItems, includeRaw),
            rows = songRows,
            hasMore = library.CanLoadMoreCollection || catalog.CanLoadMoreCatalogPlaylist || library.CanNextPage,
            detail = collection is null ? null : new { id = collection.Id, name = collection.Name, kind = collection.Kind, count = collection.Count },
            player = BuildMusicPlayerSnapshot(),
            reader = (object?)null,
            settings = (object?)null
        };
    }

    internal object[] ProjectCatalogSections(IEnumerable<MusicCatalogSection> source, int maxItems, bool includeRaw)
    {
        var result = new List<object>();
        int secIdx = 0;
        foreach (var sec in source)
        {
            var items = new List<object>();
            int itemIdx = 0;
            foreach (var card in sec.Items.Take(maxItems))
            {
                var actions = new List<string>();
                if (card.CanOpen) actions.Add("open");
                if (card.CanPlay) actions.Add("play");
                if (card.CanFavorite) actions.Add("favorite");
                items.Add(new
                {
                    index = itemIdx++,
                    id = card.Id.Length > 0 ? card.Id : card.Key,
                    kind = card.Kind,
                    title = card.Title,
                    subtitle = card.Subtitle,
                    badge = card.Label.Length > 0 ? card.Label : card.CoverLabel.Length > 0 ? card.CoverLabel : card.PlayCountText,
                    coverUrl = card.Cover,
                    actions,
                    meta = new
                    {
                        platform = card.Platform,
                        actionId = card.ActionId,
                        available = card.Available,
                        errorCode = card.ErrorCode,
                        raw = includeRaw && card.Song is { } cardSong ? (JsonElement?)Library.MusicNodeSongCodec.SongToElement(cardSong.Record) : null
                    }
                });
            }
            result.Add(new
            {
                index = secIdx++,
                id = sec.Id.Length > 0 ? sec.Id : $"section_{secIdx - 1}",
                title = sec.Title,
                kind = sec.Type.Length > 0 ? sec.Type : "mixed",
                layout = sec.Layout.Length > 0 ? sec.Layout : sec.Presentation.ToString().ToLowerInvariant(),
                hasMore = sec.Action is { CanOpen: true },
                isLoading = sec.IsLoading,
                errorCode = sec.ErrorCode,
                selectedTab = sec.SelectedTab,
                sectionTabs = sec.Tabs?.Select((t, ti) => new { index = ti, id = t.Id, title = t.Title, selected = t.Id == sec.SelectedTab }).ToArray(),
                itemCount = sec.Items.Count,
                items
            });
        }
        return result.ToArray();
    }

    private async Task<object?> TryHandleCatalogOrLibraryActionAsync(string action, JsonElement? parameters, bool settingsOpen)
    {
        var catalog = capability.Catalog;
        var library = capability.Library;
        var search = capability.Search;
        var queue = capability.Queue;

        switch (action)
        {
            case "refresh":
                await library.RefreshAsync();
                return new { refreshed = true, route = capability.CurrentRoute };

            case "section.item.invoke":
            {
                var sections = catalog.DisplayCatalogSections;
                var sec = ResolveSection(sections, parameters);
                var card = ResolveCard(sec.Items, parameters);
                var verb = (GetStr(parameters, "verb") ?? "open").Trim().ToLowerInvariant();
                DebugLog.Info(DebugCategories.MusicCatalog, "debug.action", "section.item.invoke",
                    new { section = sec.Title, item = card.Title, kind = card.Kind, verb });
                if (verb == "favorite")
                {
                    await catalog.FavoriteCatalogCardAsync(card);
                    return new { invoked = true, verb, title = card.Title };
                }
                if (verb == "play" && card.CanPlay)
                {
                    await catalog.PlayCatalogCardAsync(card);
                    return new { invoked = true, verb, title = card.Title, kind = card.Kind, route = capability.CurrentRoute };
                }
                await catalog.OpenCatalogCardAsync(card);
                return new { invoked = true, verb, title = card.Title, kind = card.Kind, route = capability.CurrentRoute };
            }

            case "section.more":
            {
                var sec = ResolveSection(catalog.DisplayCatalogSections, parameters);
                if (sec.Action is not { CanOpen: true } moreCard)
                    throw DebugApiException.InvalidState($"Section '{sec.Title}' has no 'more' action.");
                await catalog.OpenCatalogCardAsync(moreCard);
                return new { invoked = true, section = sec.Title, route = capability.CurrentRoute };
            }

            case "tab.select":
            {
                var group = (GetStr(parameters, "group") ?? "channel").Trim().ToLowerInvariant();
                var id = GetStr(parameters, "id");
                var index = GetInt(parameters, "index");
                if (group == "settings-section" || settingsOpen)
                    return HandleSettingsSectionSelect(id, index);
                if (group == "channel")
                {
                    MusicCatalogCard? tab;
                    if (index is { } idx)
                    {
                        if (idx < 0 || idx >= catalog.ChannelTabs.Count)
                            throw DebugApiException.InvalidArgument($"Channel tab index {idx} out of range (0..{Math.Max(0, catalog.ChannelTabs.Count - 1)}).");
                        tab = catalog.ChannelTabs[idx];
                    }
                    else
                    {
                        tab = catalog.ChannelTabs.FirstOrDefault(c =>
                            string.Equals(c.ActionId, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tab is null) throw DebugApiException.NotFound($"Channel tab not found (index={index}, id='{id}').");
                    await catalog.OpenCatalogCardAsync(tab);
                    return new { group = "channel", selected = tab.Title, route = capability.CurrentRoute };
                }
                if (group == "platform")
                {
                    MusicCatalogPlatform? plat;
                    if (index is { } idx)
                    {
                        if (idx < 0 || idx >= catalog.CatalogPlatforms.Count)
                            throw DebugApiException.InvalidArgument($"Platform tab index {idx} out of range (0..{Math.Max(0, catalog.CatalogPlatforms.Count - 1)}).");
                        plat = catalog.CatalogPlatforms[idx];
                    }
                    else
                    {
                        plat = catalog.CatalogPlatforms.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (plat is null) throw DebugApiException.NotFound($"Catalog platform not found: '{id}'.");
                    await catalog.ChangeCatalogPlatformAsync(plat);
                    return new { group = "platform", selected = plat.Key };
                }
                if (group == "filter")
                {
                    if (library.IsHistory)
                    {
                        string? date;
                        if (index is { } idx)
                        {
                            if (idx < 0 || idx >= library.HistoryDates.Count)
                                throw DebugApiException.InvalidArgument($"History date index {idx} out of range (0..{Math.Max(0, library.HistoryDates.Count - 1)}).");
                            date = library.HistoryDates[idx];
                        }
                        else date = id;
                        if (string.IsNullOrWhiteSpace(date)) throw DebugApiException.NotFound("History date not found.");
                        await library.SelectHistoryDateAsync(date);
                        return new { group = "filter", selected = date };
                    }
                    MusicCatalogTag? tag;
                    if (index is { } ti)
                    {
                        if (ti < 0 || ti >= catalog.CatalogTags.Count)
                            throw DebugApiException.InvalidArgument($"Catalog tag index {ti} out of range (0..{Math.Max(0, catalog.CatalogTags.Count - 1)}).");
                        tag = catalog.CatalogTags[ti];
                    }
                    else
                    {
                        tag = catalog.CatalogTags.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tag is null) throw DebugApiException.NotFound($"Catalog tag not found (index={index}, id='{id}').");
                    await catalog.CatalogTagAsync(tag);
                    return new { group = "filter", selected = tag.Name };
                }
                if (group == "section-tab")
                {
                    var sec = ResolveSection(catalog.DisplayCatalogSections, parameters);
                    if (sec.Tabs is not { Count: > 0 } stabs)
                        throw DebugApiException.InvalidState($"Section '{sec.Title}' has no tabs.");
                    if (index is { } si && (si < 0 || si >= stabs.Count))
                        throw DebugApiException.InvalidArgument($"Section tab index {si} out of range (0..{stabs.Count - 1}).");
                    var picked = index is { } validIdx
                        ? stabs[validIdx]
                        : stabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Title, id, StringComparison.OrdinalIgnoreCase));
                    if (picked is null) throw DebugApiException.NotFound("Section tab not found.");
                    catalog.SelectCatalogSectionTab(picked);
                    return new { group = "section-tab", section = sec.Title, selected = picked.Id };
                }
                throw DebugApiException.InvalidArgument($"Unsupported tab group '{group}' for music.");
            }

            case "list.item.invoke":
            {
                var verb = (GetStr(parameters, "verb") ?? "play").Trim().ToLowerInvariant();
                var idx = GetInt(parameters, "index");
                var id = GetStr(parameters, "id");
                if (library.IsArchives)
                {
                    ArchivedPlaylist? archive;
                    if (idx is { } ai)
                    {
                        if (ai < 0 || ai >= library.Archives.Count)
                            throw DebugApiException.InvalidArgument($"Archive index {ai} out of range (0..{Math.Max(0, library.Archives.Count - 1)}).");
                        archive = library.Archives[ai];
                    }
                    else archive = library.Archives.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
                    if (archive is null) throw DebugApiException.NotFound("Archive item not found.");
                    if (verb == "delete") await library.DeleteArchiveAsync(archive.Id);
                    else await library.RestoreArchiveAsync(archive.Id);
                    return new { invoked = true, verb, id = archive.Id };
                }
                var songs = capability.ActiveSongs;
                SongRow? song;
                if (idx is { } si)
                {
                    if (si < 0 || si >= songs.Count)
                        throw DebugApiException.InvalidArgument($"Song index {si} out of range (0..{Math.Max(0, songs.Count - 1)}).");
                    song = songs[si];
                }
                else song = songs.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
                if (song is null) throw DebugApiException.NotFound("Song row not found.");
                if (search.IsSearchPage) search.SearchSelectedSong = song;
                else if (catalog.IsCatalogPage) catalog.CatalogSelectedSong = song;
                else library.SelectedSong = song;
                if (verb == "select") return new { invoked = true, verb, id = song.Id, title = song.Title };
                if (verb == "favorite") { await library.FavoriteSongAsync(song); return new { invoked = true, verb, id = song.Id }; }
                if (verb == "delete") { await library.RemoveSongAsync(); return new { invoked = true, verb, id = song.Id }; }
                if (search.IsSearchPage)
                {
                    var playCtx = search.SearchKeyword.Length > 0
                        ? Library.MusicPlayContextRecord.Empty with { SourceType = "search", SearchKeyword = search.SearchKeyword }
                        : null;
                    var src = search.SearchKeyword.Length > 0
                        ? new Library.MusicQueueSourceRecord("catalog-search", search.SearchKeyword, "search", "")
                        : null;
                    await library.PlaySingleSongAsync(song, searchDirect: true, playCtx, src);
                }
                else if (catalog.IsCatalogDetailPage)
                {
                    await catalog.PlaySelectedCatalogSongAsync();
                }
                else
                {
                    await library.PlaySelectionAsync();
                }
                return new { invoked = true, verb = "play", id = song.Id, title = song.Title };
            }

            case "list.load_more":
            {
                if (library.CanLoadMoreCollection) { await library.LoadMoreCollectionAsync(); return new { loadedMore = true }; }
                if (catalog.CanLoadMoreCatalogPlaylist) { await catalog.LoadMoreCatalogPlaylistAsync(); return new { loadedMore = true }; }
                if (search.CanLoadMoreSearchSongs) { await search.MoreSearchAsync(); return new { loadedMore = true }; }
                if (library.CanNextPage) { await library.PageAsync(1); return new { loadedMore = true }; }
                if (catalog.CanCatalogNext) { await catalog.CatalogPageAsync(1); return new { loadedMore = true }; }
                if (queue.CanLoadMoreQueueSurface) { await queue.LoadMoreQueueSurfaceAsync(); return new { loadedMore = true }; }
                return new { loadedMore = false, reason = "NO_MORE_ITEMS" };
            }

            default:
                return null;
        }
    }

    private static MusicCatalogSection ResolveSection(IList<MusicCatalogSection> sections, JsonElement? parameters)
    {
        if (sections.Count == 0) throw DebugApiException.InvalidState("No catalog sections are currently loaded.");
        if (GetInt(parameters, "sectionIndex") is { } idx)
        {
            if (idx < 0 || idx >= sections.Count)
                throw DebugApiException.InvalidArgument($"sectionIndex {idx} out of range (0..{sections.Count - 1}).");
            return sections[idx];
        }
        if (GetStr(parameters, "sectionId") is { Length: > 0 } sid)
        {
            return sections.FirstOrDefault(s =>
                string.Equals(s.Id, sid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, sid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Section '{sid}' not found.");
        }
        return sections[0];
    }

    private static MusicCatalogCard ResolveCard(IReadOnlyList<MusicCatalogCard> items, JsonElement? parameters)
    {
        if (items.Count == 0) throw DebugApiException.InvalidState("Target section has no items.");
        if (GetInt(parameters, "itemIndex") is { } idx)
        {
            if (idx < 0 || idx >= items.Count)
                throw DebugApiException.InvalidArgument($"itemIndex {idx} out of range (0..{items.Count - 1}).");
            return items[idx];
        }
        if (GetStr(parameters, "itemId") is { Length: > 0 } iid)
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Id, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Key, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Title, iid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Item '{iid}' not found.");
        }
        return items[0];
    }
}
