using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> MusicTargets =
    [
        "music.home", "music.channels", "music.discovery", "music.personal", "music.daily", "music.fm",
        "music.library", "music.favorites", "music.history", "music.archives", "music.playlist",
        "music.player", "music.queue", "settings.music"
    ];

    public string DebugModuleId => Id;
    public IReadOnlyList<string> SupportedDebugTargets => MusicTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        if (settingsOpen)
        {
            settingsSection = Presentation.Settings.CurrentSectionKey;
            return "settings.music";
        }
        settingsSection = null;
        var route = location.Route;
        if (route == "catalog-home") return "music.home";
        if (route == "catalog-channels" || route.StartsWith("catalog-channel/", StringComparison.Ordinal)) return "music.channels";
        if (route == "catalog-discovery") return "music.discovery";
        if (route == "catalog-personal") return "music.personal";
        if (route == "catalog-daily") return "music.daily";
        if (route == "catalog-fm") return "music.fm";
        if (route == "favorites") return "music.favorites";
        if (route == "history") return "music.history";
        if (route == "archives") return "music.archives";
        if (route.StartsWith("playlist/", StringComparison.Ordinal) || route.StartsWith("catalog-playlist/", StringComparison.Ordinal))
            return "music.playlist";
        return "music." + route;
    }

    public async Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.MusicCatalog, "debug.nav", "open.target", new { target, @params = parameters });
        switch (target)
        {
            case "music.home":
                await NavigateAsync("catalog-home", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.channels":
            {
                var key = GetStr(parameters, "channelKey");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var route = key.StartsWith("catalog-channel/", StringComparison.Ordinal) ? key : "catalog-channel/" + key;
                    await NavigateAsync(route, cancellationToken);
                    return true;
                }
                await NavigateAsync("catalog-channels", cancellationToken);
                if (GetInt(parameters, "channelIndex") is { } idx)
                {
                    if (idx < 0 || idx >= ChannelTabs.Count)
                        throw DebugApiException.InvalidArgument($"channelIndex {idx} out of range (0..{Math.Max(0, ChannelTabs.Count - 1)}).");
                    await OpenCatalogCardAsync(ChannelTabs[idx]);
                }
                return true;
            }
            case "music.discovery":
                await NavigateAsync("catalog-discovery", cancellationToken);
                return true;
            case "music.personal":
                await NavigateAsync("catalog-personal", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.daily":
                await NavigateAsync("catalog-daily", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.fm":
                await NavigateAsync("catalog-fm", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.library":
            case "music.favorites":
                await NavigateAsync("favorites", cancellationToken);
                return true;
            case "music.history":
                await NavigateAsync("history", cancellationToken);
                if (GetStr(parameters, "date") is { Length: > 0 } date)
                    await SelectHistoryDateAsync(date);
                return true;
            case "music.archives":
                await NavigateAsync("archives", cancellationToken);
                return true;
            case "music.playlist":
            {
                var id = GetStr(parameters, "id");
                var platform = GetStr(parameters, "platform");
                var remoteId = GetStr(parameters, "remoteId");
                if (!string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(remoteId))
                {
                    await NavigateAsync("catalog-playlist/" + platform + "/" + Uri.EscapeDataString(remoteId), cancellationToken);
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    await OpenPlaylistAsync(id);
                    return true;
                }
                var first = Playlists.FirstOrDefault() ?? NetworkPlaylists.FirstOrDefault();
                if (first is null) throw DebugApiException.InvalidState("No playlist available to open.");
                await NavigateAsync(first.Route, cancellationToken);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task ApplyCatalogPlatformParamAsync(JsonElement? parameters)
    {
        if (GetStr(parameters, "platform") is not { Length: > 0 } platformKey) return;
        var p = CatalogPlatforms.FirstOrDefault(x => string.Equals(x.Key, platformKey, StringComparison.OrdinalIgnoreCase));
        if (p is null) throw DebugApiException.NotFound($"Music catalog platform not found: '{platformKey}'.");
        await ChangeCatalogPlatformAsync(p);
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        Task.FromResult(Presentation.Settings.SelectDebugSection(section));

    public Task<object?> BuildDebugOverlayContentAsync(string target, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (target == "music.player")
        {
            return Task.FromResult<object?>(new
            {
                target = "music.player",
                kind = "player",
                loading = false,
                error = string.IsNullOrWhiteSpace(status) ? null : status,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }
        if (target == "music.queue")
        {
            var rows = QueueSurfaceRows.Take(maxItems).Select((row, idx) => new
            {
                index = idx,
                id = row.EntryId.Length > 0 ? row.EntryId : row.Song.Id,
                title = row.Title,
                subtitle = row.Artist,
                duration = row.Duration,
                selected = row.IsCurrent,
                state = row.IsCurrent ? (IsPlaying ? "playing" : "current") : "queued",
                actions = new[] { "play", "delete" },
                raw = includeRaw ? (JsonElement?)row.Song.Raw : null
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target = "music.queue",
                kind = "list",
                loading = false,
                error = string.IsNullOrWhiteSpace(status) ? null : status,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = CanLoadMoreQueueSurface,
                detail = new { totalText = QueueSurfaceCountText, loaded = QueueSurfaceRows.Count },
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }
        return Task.FromResult<object?>(null);
    }

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (settingsOpen)
        {
            var settings = new
            {
                domain = "music",
                section = Presentation.Settings.CurrentSectionKey,
                tabs = Presentation.Settings.DescribeDebugTabs(),
                playbackQualityRequest = PlaybackQualityRequest,
                playbackQualityOptions = PlaybackQualityOptions,
                accounts = Accounts.Select(a => new
                {
                    platform = a.Platform,
                    name = a.Name,
                    state = a.State,
                    stateText = a.StateText,
                    displayName = a.DisplayName,
                    userId = a.UserId,
                    hasSession = a.HasSession
                }).ToArray(),
                sources = Sources.Select((s, idx) => new
                {
                    index = idx,
                    key = s.Key,
                    installationId = s.InstallationId,
                    name = s.Name,
                    version = s.Version,
                    enabled = s.Enabled,
                    state = s.State,
                    summary = s.Summary
                }).ToArray()
            };
            return Task.FromResult<object?>(new
            {
                target = "settings.music",
                kind = "settings",
                loading = false,
                error = string.IsNullOrWhiteSpace(status) ? null : status,
                tabs = Presentation.Settings.DescribeDebugTabs(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings
            });
        }

        var currentTarget = DescribeCurrentTarget(false, out _) ?? "music.home";

        if (IsCatalogLanding || IsCatalogContent && !IsCatalogDetailPage && !IsSongList)
        {
            var tabs = new List<object>();
            for (int i = 0; i < ChannelTabs.Count; i++)
            {
                var ch = ChannelTabs[i];
                tabs.Add(new
                {
                    group = "channel",
                    index = i,
                    id = ch.ActionId.Length > 0 ? ch.ActionId : ch.Id,
                    title = ch.Title,
                    selected = !string.IsNullOrEmpty(CurrentChannelKey) &&
                        (string.Equals(ch.ActionId, CurrentChannelKey, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(ch.Id, CurrentChannelKey, StringComparison.OrdinalIgnoreCase)),
                    enabled = ch.Available
                });
            }
            for (int i = 0; i < CatalogPlatforms.Count; i++)
            {
                var plat = CatalogPlatforms[i];
                tabs.Add(new
                {
                    group = "platform",
                    index = i,
                    id = plat.Key,
                    title = plat.Name,
                    selected = plat.Key == SelectedCatalogPlatform.Key,
                    enabled = true
                });
            }

            var filters = CatalogTags.Select((tag, idx) => new
            {
                group = "filter",
                index = idx,
                id = tag.Id,
                title = tag.Name,
                selected = tag.Selected
            }).ToArray();

            var sections = ProjectCatalogSections(DisplayCatalogSections, maxItems, includeRaw);
            return Task.FromResult<object?>(new
            {
                target = currentTarget,
                kind = "sections",
                loading = loadCancellation is not null,
                error = string.IsNullOrWhiteSpace(status) ? null : status,
                tabs,
                filters,
                sections,
                rows = Array.Empty<object>(),
                hasMore = CanLoadMoreCatalogPlaylist || CanCatalogNext,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }

        if (IsArchives)
        {
            var rows = Archives.Take(maxItems).Select((a, idx) => new
            {
                index = idx,
                id = a.Id,
                title = a.Title,
                subtitle = $"{a.Count} 首",
                selected = false,
                state = "archived",
                actions = new[] { "open", "delete" }
            }).ToArray();
            return Task.FromResult<object?>(new
            {
                target = currentTarget,
                kind = "list",
                loading = loadCancellation is not null,
                error = string.IsNullOrWhiteSpace(status) ? null : status,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows,
                hasMore = false,
                detail = (object?)null,
                player = BuildMusicPlayerSnapshot(),
                reader = (object?)null,
                settings = (object?)null
            });
        }

        var songRows = Songs.Take(maxItems).Select((song, idx) => new
        {
            index = idx,
            id = song.Id,
            title = song.Title,
            subtitle = string.IsNullOrEmpty(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}",
            duration = song.Duration,
            selected = ReferenceEquals(song, selectedSong),
            state = song.Id == CurrentSongId ? (IsPlaying ? "playing" : "current") : "idle",
            actions = new[] { "play", "select", "favorite" },
            raw = includeRaw ? (JsonElement?)song.Raw : null
        }).ToArray();

        var dateFilters = IsHistory
            ? HistoryDates.Select((d, idx) => new { group = "filter", index = idx, id = d, title = d, selected = d == HistoryDate }).ToArray()
            : Array.Empty<object>();

        return Task.FromResult<object?>(new
        {
            target = currentTarget,
            kind = "list",
            loading = loadCancellation is not null,
            error = string.IsNullOrWhiteSpace(status) ? null : status,
            tabs = Array.Empty<object>(),
            filters = dateFilters,
            sections = ProjectCatalogSections(DisplayCatalogSections, maxItems, includeRaw),
            rows = songRows,
            hasMore = CanLoadMoreCollection || CanLoadMoreCatalogPlaylist || CanNextPage,
            detail = collection is null ? null : new { id = collection.Id, name = collection.Name, kind = collection.Kind, count = collection.Count },
            player = BuildMusicPlayerSnapshot(),
            reader = (object?)null,
            settings = (object?)null
        });
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
                        raw = includeRaw ? card.Song?.Raw : null
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

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "refresh":
                await RefreshAsync();
                return new { refreshed = true, route = location.Route };

            case "section.item.invoke":
            {
                var sections = DisplayCatalogSections;
                var sec = ResolveSection(sections, parameters);
                var card = ResolveCard(sec.Items, parameters);
                var verb = (GetStr(parameters, "verb") ?? "open").Trim().ToLowerInvariant();
                DebugLog.Info(DebugCategories.MusicCatalog, "debug.action", "section.item.invoke",
                    new { section = sec.Title, item = card.Title, kind = card.Kind, verb });
                if (verb == "favorite")
                {
                    await FavoriteCatalogCardAsync(card);
                    return new { invoked = true, verb, title = card.Title };
                }
                if (verb == "play" && card.CanPlay)
                {
                    await PlayCatalogCardAsync(card);
                    return new { invoked = true, verb, title = card.Title, kind = card.Kind, route = location.Route };
                }
                await OpenCatalogCardAsync(card);
                return new { invoked = true, verb, title = card.Title, kind = card.Kind, route = location.Route };
            }

            case "section.more":
            {
                var sec = ResolveSection(DisplayCatalogSections, parameters);
                if (sec.Action is not { CanOpen: true } moreCard)
                    throw DebugApiException.InvalidState($"Section '{sec.Title}' has no 'more' action.");
                await OpenCatalogCardAsync(moreCard);
                return new { invoked = true, section = sec.Title, route = location.Route };
            }

            case "tab.select":
            {
                var group = (GetStr(parameters, "group") ?? "channel").Trim().ToLowerInvariant();
                var id = GetStr(parameters, "id");
                var index = GetInt(parameters, "index");
                if (group == "settings-section" || settingsOpen)
                {
                    string targetKey = id ?? (index is { } idx && idx >= 0 && idx < 5
                        ? new[] { "accounts", "search", "quality", "metadata", "sources" }[idx]
                        : throw DebugApiException.InvalidArgument("Provide 'id' or valid 'index' for settings-section."));
                    if (!Presentation.Settings.SelectDebugSection(targetKey))
                        throw DebugApiException.NotFound($"Music settings section not found: '{targetKey}'.");
                    return new { group = "settings-section", selected = targetKey };
                }
                if (group == "channel")
                {
                    MusicCatalogCard? tab;
                    if (index is { } idx)
                    {
                        if (idx < 0 || idx >= ChannelTabs.Count)
                            throw DebugApiException.InvalidArgument($"Channel tab index {idx} out of range (0..{Math.Max(0, ChannelTabs.Count - 1)}).");
                        tab = ChannelTabs[idx];
                    }
                    else
                    {
                        tab = ChannelTabs.FirstOrDefault(c =>
                            string.Equals(c.ActionId, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tab is null) throw DebugApiException.NotFound($"Channel tab not found (index={index}, id='{id}').");
                    await OpenCatalogCardAsync(tab);
                    return new { group = "channel", selected = tab.Title, route = location.Route };
                }
                if (group == "platform")
                {
                    MusicCatalogPlatform? plat;
                    if (index is { } idx)
                    {
                        if (idx < 0 || idx >= CatalogPlatforms.Count)
                            throw DebugApiException.InvalidArgument($"Platform tab index {idx} out of range (0..{Math.Max(0, CatalogPlatforms.Count - 1)}).");
                        plat = CatalogPlatforms[idx];
                    }
                    else
                    {
                        plat = CatalogPlatforms.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (plat is null) throw DebugApiException.NotFound($"Catalog platform not found: '{id}'.");
                    await ChangeCatalogPlatformAsync(plat);
                    return new { group = "platform", selected = plat.Key };
                }
                if (group == "filter")
                {
                    if (IsHistory)
                    {
                        string? date;
                        if (index is { } idx)
                        {
                            if (idx < 0 || idx >= HistoryDates.Count)
                                throw DebugApiException.InvalidArgument($"History date index {idx} out of range (0..{Math.Max(0, HistoryDates.Count - 1)}).");
                            date = HistoryDates[idx];
                        }
                        else date = id;
                        if (string.IsNullOrWhiteSpace(date)) throw DebugApiException.NotFound("History date not found.");
                        await SelectHistoryDateAsync(date);
                        return new { group = "filter", selected = date };
                    }
                    MusicCatalogTag? tag;
                    if (index is { } ti)
                    {
                        if (ti < 0 || ti >= CatalogTags.Count)
                            throw DebugApiException.InvalidArgument($"Catalog tag index {ti} out of range (0..{Math.Max(0, CatalogTags.Count - 1)}).");
                        tag = CatalogTags[ti];
                    }
                    else
                    {
                        tag = CatalogTags.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase));
                    }
                    if (tag is null) throw DebugApiException.NotFound($"Catalog tag not found (index={index}, id='{id}').");
                    await CatalogTagAsync(tag);
                    return new { group = "filter", selected = tag.Name };
                }
                if (group == "section-tab")
                {
                    var sec = ResolveSection(DisplayCatalogSections, parameters);
                    if (sec.Tabs is not { Count: > 0 } stabs)
                        throw DebugApiException.InvalidState($"Section '{sec.Title}' has no tabs.");
                    if (index is { } si && (si < 0 || si >= stabs.Count))
                        throw DebugApiException.InvalidArgument($"Section tab index {si} out of range (0..{stabs.Count - 1}).");
                    var picked = index is { } validIdx
                        ? stabs[validIdx]
                        : stabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Title, id, StringComparison.OrdinalIgnoreCase));
                    if (picked is null) throw DebugApiException.NotFound("Section tab not found.");
                    SelectCatalogSectionTab(picked);
                    return new { group = "section-tab", section = sec.Title, selected = picked.Id };
                }
                throw DebugApiException.InvalidArgument($"Unsupported tab group '{group}' for music.");
            }

            case "list.item.invoke":
            {
                var verb = (GetStr(parameters, "verb") ?? "play").Trim().ToLowerInvariant();
                var idx = GetInt(parameters, "index");
                var id = GetStr(parameters, "id");
                if (IsArchives)
                {
                    ArchivedPlaylist? archive;
                    if (idx is { } ai)
                    {
                        if (ai < 0 || ai >= Archives.Count)
                            throw DebugApiException.InvalidArgument($"Archive index {ai} out of range (0..{Math.Max(0, Archives.Count - 1)}).");
                        archive = Archives[ai];
                    }
                    else archive = Archives.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
                    if (archive is null) throw DebugApiException.NotFound("Archive item not found.");
                    if (verb == "delete") await DeleteArchiveAsync(archive.Id);
                    else await RestoreArchiveAsync(archive.Id);
                    return new { invoked = true, verb, id = archive.Id };
                }
                SongRow? song;
                if (idx is { } si)
                {
                    if (si < 0 || si >= Songs.Count)
                        throw DebugApiException.InvalidArgument($"Song index {si} out of range (0..{Math.Max(0, Songs.Count - 1)}).");
                    song = Songs[si];
                }
                else song = Songs.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
                if (song is null) throw DebugApiException.NotFound("Song row not found.");
                SelectedSong = song;
                if (verb == "select") return new { invoked = true, verb, id = song.Id, title = song.Title };
                if (verb == "favorite") { await FavoriteAsync(); return new { invoked = true, verb, id = song.Id }; }
                if (verb == "delete") { await RemoveSongAsync(); return new { invoked = true, verb, id = song.Id }; }
                await PlaySelectionAsync();
                return new { invoked = true, verb = "play", id = song.Id, title = song.Title };
            }

            case "list.load_more":
            {
                if (CanLoadMoreCollection) { await LoadMoreCollectionAsync(); return new { loadedMore = true }; }
                if (CanLoadMoreCatalogPlaylist) { await LoadMoreCatalogPlaylistAsync(); return new { loadedMore = true }; }
                if (CanLoadMoreSearchSongs) { await MoreSearchAsync(); return new { loadedMore = true }; }
                if (CanNextPage) { await PageAsync(1); return new { loadedMore = true }; }
                if (CanCatalogNext) { await CatalogPageAsync(1); return new { loadedMore = true }; }
                if (CanLoadMoreQueueSurface) { await LoadMoreQueueSurfaceAsync(); return new { loadedMore = true }; }
                return new { loadedMore = false, reason = "NO_MORE_ITEMS" };
            }

            case "setting.set":
            {
                var key = (GetStr(parameters, "key") ?? "").Trim().ToLowerInvariant();
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                if (key is "playbackquality" or "quality" && val.ValueKind == JsonValueKind.String)
                {
                    await SetPlaybackQualityRequestAsync(val.GetString()!);
                    return new { saved = true, key, value = PlaybackQualityRequest };
                }
                if (key == "volume" && val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var vol))
                {
                    Volume = vol;
                    return new { saved = true, key, value = Volume };
                }
                throw DebugApiException.InvalidArgument($"Unsupported music setting key '{key}'.");
            }

            case "setting.action":
            {
                var cmd = (GetStr(parameters, "command") ?? "").Trim().ToLowerInvariant();
                if (cmd == "clear_search_history")
                {
                    await ClearSearchHistoryAsync();
                    return new { executed = true, command = cmd };
                }
                throw DebugApiException.InvalidArgument($"Unsupported music settings command '{cmd}'.");
            }

            default:
                return null;
        }
    }

    public Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(BuildMusicPlayerSnapshot());

    private object BuildMusicPlayerSnapshot()
    {
        int lyricIdx = ActiveLyricIndex;
        string? activeLyric = lyricIdx >= 0 && lyricIdx < Lyrics.Count ? Lyrics[lyricIdx].Text : null;
        int queueIdx = -1;
        for (int i = 0; i < QueueSurfaceRows.Count; i++)
        {
            if (QueueSurfaceRows[i].IsCurrent) { queueIdx = i; break; }
        }
        return new
        {
            state = !HasCurrent ? "stopped" : IsPlaying ? "playing" : "paused",
            track = current is null ? null : new
            {
                id = current.Id,
                entryId = current.EntryId,
                title = current.Title,
                artist = current.Artist,
                album = current.Album,
                cover = current.Cover,
                isOnline = current.IsOnline
            },
            source = CurrentSource,
            quality = PlaybackQualityRequest,
            positionSec = Math.Round(PositionSeconds, 3),
            durationSec = Math.Round(DurationSeconds, 3),
            bufferedSec = (double?)null,
            volume = Volume,
            mode = Mode,
            modeText = ModeText,
            queueIndex = queueIdx,
            queueCount = QueueSurfaceRows.Count,
            queueTotalText = QueueSurfaceCountText,
            lyricLine = activeLyric,
            lyricCount = Lyrics.Count,
            lastError = string.IsNullOrWhiteSpace(status) ? null : status
        };
    }

    public async Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.MusicPlayback, "debug.command", "playback." + command, new { @params = parameters });
        switch (command)
        {
            case "play":
                await ResumePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "pause":
                if (IsPlaying) await TogglePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "toggle":
                await TogglePlaybackAsync();
                return BuildMusicPlayerSnapshot();
            case "stop":
                await playback.StopAsync();
                return BuildMusicPlayerSnapshot();
            case "next":
                await StepAsync(true, "debug");
                return BuildMusicPlayerSnapshot();
            case "previous":
            case "prev":
                await StepAsync(false, "debug");
                return BuildMusicPlayerSnapshot();
            case "seek":
            {
                double sec = parameters is { ValueKind: JsonValueKind.Object } obj &&
                    obj.TryGetProperty("seconds", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetDouble(out var v)
                    ? v : throw DebugApiException.InvalidArgument("seek requires numeric 'seconds'.");
                await SeekAsync(sec);
                return BuildMusicPlayerSnapshot();
            }
            case "mode":
            {
                int m = GetInt(parameters, "mode") ?? throw DebugApiException.InvalidArgument("mode requires integer 'mode'.");
                await ChangeModeAsync(m);
                return BuildMusicPlayerSnapshot();
            }
            case "play_queue_index":
            {
                int idx = GetInt(parameters, "index") ?? throw DebugApiException.InvalidArgument("play_queue_index requires 'index'.");
                if (idx < 0 || idx >= QueueSurfaceRows.Count)
                    throw DebugApiException.InvalidArgument($"Queue index {idx} out of range (0..{QueueSurfaceRows.Count - 1}).");
                await PlayQueueSurfaceItemAsync(QueueSurfaceRows[idx]);
                return BuildMusicPlayerSnapshot();
            }
            default:
                return null;
        }
    }

    public Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

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

    private static string? GetStr(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static int? GetInt(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val : null;
}
