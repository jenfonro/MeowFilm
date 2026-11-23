using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed class FilmDebugAdapter(FilmCapability capability) : IDebugModuleAdapter
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
            settingsSection = (capability.DebugSettings as FilmSettingsView)?.CurrentDebugSection ?? "rules";
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
                var category = GetStr(parameters, "category");
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

    public async Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken)
    {
        if (capability.DebugSettings is not FilmSettingsView fsv) return false;
        return await fsv.SelectDebugSectionAsync(section);
    }

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (settingsOpen && capability.DebugSettings is FilmSettingsView fsv)
        {
            return Task.FromResult<object?>(new
            {
                target = "settings.film",
                kind = "settings",
                loading = false,
                error = (string?)null,
                tabs = fsv.DescribeDebugTabs(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings = fsv.BuildDebugSnapshot()
            });
        }

        if (capability.IsHome)
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
            return Task.FromResult<object?>(new
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
            });
        }

        if (capability.IsData)
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
            return Task.FromResult<object?>(new
            {
                target = "film.data",
                kind = "sections",
                loading = capability.ActiveData.State == FilmDataSectionState.Loading,
                error = capability.ActiveData.State == FilmDataSectionState.Failed ? "LOAD_FAILED" : null,
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
            });
        }

        var pb = BuildFilmPlayerSnapshot();
        var playback = capability.Playback;
        return Task.FromResult<object?>(new
        {
            target = "film.playback",
            kind = "player",
            loading = playback?.PlayerBusy ?? false,
            error = playback is { ExecutionFailed: true } or { ConnectionFailed: true } ? playback.StatusText : null,
            tabs = Array.Empty<object>(),
            filters = Array.Empty<object>(),
            sections = Array.Empty<object>(),
            rows = Array.Empty<object>(),
            hasMore = false,
            detail = playback is null ? null : new
            {
                title = playback.Title,
                sources = playback.Sources.Select((s, i) => new { index = i, id = s.Id, label = s.Label, selected = ReferenceEquals(s, playback.SelectedSource) }).ToArray(),
                lines = playback.Lines.Select((l, i) => new { index = i, id = l.Id, label = l.Label, selected = ReferenceEquals(l, playback.SelectedLine) }).ToArray(),
                episodes = playback.Episodes.Take(maxItems).Select((e, i) => new { index = i, id = e.Identity, title = e.Title, number = e.Number, selected = e.IsSelected }).ToArray()
            },
            player = pb,
            reader = (object?)null,
            settings = (object?)null
        });
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
                var group = (GetStr(parameters, "group") ?? "category").Trim().ToLowerInvariant();
                var id = GetStr(parameters, "id");
                var idx = GetInt(parameters, "index");
                if (group == "settings-section" || settingsOpen)
                {
                    if (capability.DebugSettings is not FilmSettingsView fsv) throw DebugApiException.InvalidState("Film settings unavailable.");
                    string key;
                    if (id is not null)
                    {
                        key = id;
                    }
                    else if (idx is { } i)
                    {
                        var tabIds = fsv.GetDebugTabIds();
                        if (i < 0 || i >= tabIds.Count)
                            throw DebugApiException.InvalidArgument($"Film settings tab index {i} out of range (0..{tabIds.Count - 1}).");
                        key = tabIds[i];
                    }
                    else
                    {
                        throw DebugApiException.InvalidArgument("Invalid film settings tab selection; specify 'id' or 'index'.");
                    }
                    if (!await fsv.SelectDebugSectionAsync(key))
                        throw DebugApiException.NotFound($"Film settings section not found: '{key}'.");
                    return new { group = "settings-section", selected = key };
                }
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
            {
                if (capability.DebugSettings is not FilmSettingsView fsv) throw DebugApiException.InvalidState("Film settings unavailable.");
                var key = (GetStr(parameters, "key") ?? "").Trim().ToLowerInvariant();
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                if (key.StartsWith("paninterception.", StringComparison.Ordinal) || key.StartsWith("pan.", StringComparison.Ordinal))
                {
                    var provider = key[(key.IndexOf('.') + 1)..];
                    if (val.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw DebugApiException.InvalidArgument("Boolean 'value' is required for pan interception setting.");
                    bool ok = await fsv.TryDebugSetPanInterceptionAsync(provider, val.GetBoolean(), cancellationToken);
                    if (!ok) throw DebugApiException.InvalidArgument($"Unsupported pan provider '{provider}'.");
                    return new { saved = true, domain = "film", key, value = val.GetBoolean() };
                }
                throw DebugApiException.InvalidArgument($"Unsupported film setting key '{key}'.");
            }

            case "setting.action":
            {
                if (capability.DebugSettings is not FilmSettingsView fsv) throw DebugApiException.InvalidState("Film settings unavailable.");
                var cmd = (GetStr(parameters, "command") ?? "").Trim().ToLowerInvariant();
                if (cmd is "refresh_sources" or "reload_sources")
                {
                    await fsv.RefreshDebugSourcesAsync(cancellationToken);
                    return new { executed = true, domain = "film", command = cmd };
                }
                if (cmd is "account.login" or "account.login.close")
                {
                    var platform = GetStr(parameters, "platform") ?? GetStr(parameters, "provider");
                    var close = cmd.EndsWith(".close", StringComparison.Ordinal) || GetBool(parameters, "close") == true;
                    var mode = GetStr(parameters, "mode");
                    return await fsv.DebugPanAccountLoginAsync(platform, close, mode, cancellationToken);
                }
                if (cmd is "account.logout" or "account.logout.close")
                {
                    var platform = GetStr(parameters, "platform") ?? GetStr(parameters, "provider");
                    var close = cmd.EndsWith(".close", StringComparison.Ordinal) || GetBool(parameters, "close") == true;
                    var confirm = GetBool(parameters, "confirm") == true;
                    return await fsv.DebugPanAccountLogoutAsync(platform, close, confirm, cancellationToken);
                }
                throw DebugApiException.InvalidArgument($"Unsupported film settings command '{cmd}'.");
            }

            default:
                return null;
        }
    }

    public Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object?>(BuildFilmPlayerSnapshot());

    private object BuildFilmPlayerSnapshot()
    {
        var nativeFilmPlayback = capability.DebugNativeFilmPlayback;
        var filmPlayback = capability.DebugFilmPlayback;
        var tracks = nativeFilmPlayback?.Tracks;
        var page = capability.Playback;
        string state = filmPlayback is null || !filmPlayback.HasOutput ? "stopped"
            : filmPlayback.IsBuffering ? "buffering"
            : filmPlayback.IsPlaying ? "playing" : "paused";
        return new
        {
            state,
            title = page?.Title ?? filmPlayback?.MediaTitle ?? "",
            source = page?.SelectedSource?.Label,
            episode = page?.SelectedEpisode?.Title,
            line = page?.SelectedLine?.Label,
            positionSec = Math.Round(filmPlayback?.Position ?? 0, 3),
            durationSec = Math.Round(filmPlayback?.Duration ?? 0, 3),
            bufferedSec = (double?)null,
            videoSize = (object?)null,
            mediaUrl = (string?)null,
            rate = filmPlayback?.Rate ?? 1.0,
            aspect = filmPlayback?.Aspect,
            statusText = page?.PlayerStatusText,
            audioTracks = tracks?.Audio
                .Select(t => new { index = t.Index, codec = t.Codec, language = t.Language, title = t.Title, selected = t.Selected }).ToArray(),
            subtitleTracks = tracks?.Subtitles
                .Select(t => new { index = t.Index, codec = t.Codec, language = t.Language, title = t.Title, selected = t.Selected }).ToArray(),
            selectedAudioTrack = tracks?.AudioIndex,
            selectedSubtitleTrack = tracks?.SubtitleIndex,
            lastError = !string.IsNullOrEmpty(nativeFilmPlayback?.LastErrorCode) ? nativeFilmPlayback.LastErrorCode
                : !string.IsNullOrEmpty(filmPlayback?.ExecutionError) ? filmPlayback.ExecutionError : null
        };
    }

    public async Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken)
    {
        DebugLog.Info(DebugCategories.FilmPlayback, "debug.command", "playback." + command, new { @params = parameters });
        switch (command)
        {
            case "play":
                await capability.ResumePlaybackAsync();
                return BuildFilmPlayerSnapshot();
            case "pause":
                await capability.PausePlaybackAsync();
                return BuildFilmPlayerSnapshot();
            case "toggle":
                if (capability.DebugFilmPlayback?.IsPlaying == true) await capability.PausePlaybackAsync();
                else await capability.ResumePlaybackAsync();
                return BuildFilmPlayerSnapshot();
            case "stop":
                await capability.StopPlaybackAsync();
                return BuildFilmPlayerSnapshot();
            case "next":
                await capability.StepAsync(true);
                return BuildFilmPlayerSnapshot();
            case "previous":
            case "prev":
                await capability.StepAsync(false);
                return BuildFilmPlayerSnapshot();
            case "seek":
            {
                double sec = parameters is { ValueKind: JsonValueKind.Object } obj &&
                    obj.TryGetProperty("seconds", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetDouble(out var v)
                    ? v : throw DebugApiException.InvalidArgument("seek requires numeric 'seconds'.");
                await capability.SeekPlaybackAsync(sec);
                return BuildFilmPlayerSnapshot();
            }
            case "select_episode":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                FilmEpisodeItem ep;
                if (GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Episodes.Count)
                        throw DebugApiException.InvalidArgument($"Episode index {idx} out of range.");
                    ep = page.Episodes[idx];
                }
                else if (GetStr(parameters, "id") is { Length: > 0 } eid)
                {
                    ep = page.Episodes.FirstOrDefault(e => string.Equals(e.Identity, eid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Title, eid, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Episode '{eid}' not found.");
                }
                else throw DebugApiException.InvalidArgument("select_episode requires 'index' or 'id'.");
                await page.SelectEpisodeAsync(ep);
                return BuildFilmPlayerSnapshot();
            }
            case "select_source":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                FilmSelection src;
                if (GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Sources.Count)
                        throw DebugApiException.InvalidArgument($"Source index {idx} out of range.");
                    src = page.Sources[idx];
                }
                else if (GetStr(parameters, "id") is { Length: > 0 } sid)
                {
                    src = page.Sources.FirstOrDefault(s => string.Equals(s.Id, sid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Label, sid, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Source '{sid}' not found.");
                }
                else throw DebugApiException.InvalidArgument("select_source requires 'index' or 'id'.");
                await page.BrowseSourceAsync(src);
                return BuildFilmPlayerSnapshot();
            }
            case "select_line":
            {
                var page = capability.Playback ?? throw DebugApiException.InvalidState("No active film playback page.");
                FilmSelection line;
                if (GetInt(parameters, "index") is { } idx)
                {
                    if (idx < 0 || idx >= page.Lines.Count)
                        throw DebugApiException.InvalidArgument($"Line index {idx} out of range.");
                    line = page.Lines[idx];
                }
                else if (GetStr(parameters, "id") is { Length: > 0 } lid)
                {
                    line = page.Lines.FirstOrDefault(l => string.Equals(l.Id, lid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l.Label, lid, StringComparison.OrdinalIgnoreCase))
                        ?? throw DebugApiException.NotFound($"Line '{lid}' not found.");
                }
                else throw DebugApiException.InvalidArgument("select_line requires 'index' or 'id'.");
                await page.BrowseLineAsync(line);
                return BuildFilmPlayerSnapshot();
            }
            case "select_audio_track":
            {
                if (capability.DebugNativeFilmPlayback?.Scope is not { } scope)
                    throw DebugApiException.InvalidState("No active native film output.");
                int idx = GetInt(parameters, "index") ?? throw DebugApiException.InvalidArgument("select_audio_track requires 'index'.");
                await capability.DebugNativeFilmPlayback.SelectAudioTrackAsync(scope, idx, cancellationToken);
                return BuildFilmPlayerSnapshot();
            }
            case "select_subtitle_track":
            {
                if (capability.DebugNativeFilmPlayback?.Scope is not { } scope)
                    throw DebugApiException.InvalidState("No active native film output.");
                int? idx = GetInt(parameters, "index");
                await capability.DebugNativeFilmPlayback.SelectSubtitleTrackAsync(scope, idx, cancellationToken);
                return BuildFilmPlayerSnapshot();
            }
            default:
                return null;
        }
    }

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

    private static bool? GetBool(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean() : null;

    private FilmHomeSection ResolveFilmSection(JsonElement? parameters)
    {
        if (capability.Sections.Count == 0) throw DebugApiException.InvalidState("No film sections available.");
        if (GetInt(parameters, "sectionIndex") is { } si)
        {
            if (si < 0 || si >= capability.Sections.Count)
                throw DebugApiException.InvalidArgument($"sectionIndex {si} out of range (0..{capability.Sections.Count - 1}).");
            return capability.Sections[si];
        }
        if (GetStr(parameters, "sectionId") is { Length: > 0 } sid)
        {
            return capability.Sections.FirstOrDefault(s =>
                string.Equals(s.Key, sid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Title, sid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Film section '{sid}' not found.");
        }
        return capability.Sections[0];
    }

    private static FilmCardItem ResolveFilmCard(IReadOnlyList<FilmCardItem> items, JsonElement? parameters)
    {
        if (items.Count == 0) throw DebugApiException.InvalidState("Target film section has no items.");
        if (GetInt(parameters, "itemIndex") is { } ii)
        {
            if (ii < 0 || ii >= items.Count)
                throw DebugApiException.InvalidArgument($"itemIndex {ii} out of range (0..{items.Count - 1}).");
            return items[ii];
        }
        if (GetStr(parameters, "itemId") is { Length: > 0 } iid)
        {
            return items.FirstOrDefault(c =>
                string.Equals(c.Id, iid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Title, iid, StringComparison.OrdinalIgnoreCase))
                ?? throw DebugApiException.NotFound($"Film item '{iid}' not found.");
        }
        return items[0];
    }
}
