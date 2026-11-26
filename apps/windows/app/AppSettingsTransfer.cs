using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.App;

/// <summary>
/// Cross-domain coordinator for General → Data Management plaintext settings export and tolerant import.
/// </summary>
internal sealed class AppSettingsTransfer(
    IStorageDatabase database,
    AppAppearance appearance,
    AppClosePreferences closePreferences,
    AppMediaControlSettings mediaControls,
    MusicCapability music,
    FilmCapability film,
    NovelCapability novel) : IDesktopDataManagement, IAsyncDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly string[] MusicQualityCategories = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"];
    private static readonly string[] MusicSpatialCategories = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"];
    private static readonly string[] MusicQualityRequests = ["auto", "128k", "192k", "320k", "flac", "flac24bit", "hires", "master"];
    private static readonly string[] MusicThreePlatforms = ["wy", "tx", "kg"];
    private static readonly string[] MusicFivePlatforms = ["wy", "tx", "kw", "kg", "mg"];

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool busy, stopping;

    public bool IsBusy => busy;

    public async Task<bool> ExportAsync(FrameworkElement owner)
    {
        if (stopping || busy) return false;
        var window = DesktopWindow.GetWindow(owner) ?? throw new InvalidOperationException("FILE_PICKER_OWNER_NOT_ATTACHED");
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            CommitButtonText = "导出",
            SuggestedFileName = "meowfilm-settings",
            DefaultFileExtension = ".json"
        };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        var picked = await picker.PickSaveFileAsync();
        if (string.IsNullOrWhiteSpace(picked?.Path)) return false;
        await ExportToFileAsync(picked.Path, lifetime.Token);
        return true;
    }

    public async Task<bool> ImportAsync(FrameworkElement owner)
    {
        if (stopping || busy) return false;
        var window = DesktopWindow.GetWindow(owner) ?? throw new InvalidOperationException("FILE_PICKER_OWNER_NOT_ATTACHED");
        var picker = new FileOpenPicker(window.AppWindow.Id) { CommitButtonText = "导入" };
        picker.FileTypeFilter.Add(".json");
        var picked = await picker.PickSingleFileAsync();
        if (string.IsNullOrWhiteSpace(picked?.Path)) return false;
        await ImportFromFileAsync(picked.Path, lifetime.Token);
        return true;
    }

    public async Task ExportToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (stopping) return;
            busy = true;
            try
            {
                var doc = await BuildExportObjectAsync(linked.Token);
                byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(doc, WriteOptions);
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(path, utf8, linked.Token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                DebugLog.Warn(DebugCategories.Storage, "settings.transfer", "export.failed", error: error);
                throw;
            }
        }
        finally
        {
            busy = false;
            gate.Release();
        }
    }

    public async Task ImportFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            if (stopping) return;
            busy = true;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, linked.Token);
                string text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                using var json = JsonDocument.Parse(text, ReadOptions);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("SETTINGS_JSON_ROOT_NOT_OBJECT");
                await ApplyImportRootAsync(json.RootElement, linked.Token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                DebugLog.Warn(DebugCategories.Storage, "settings.transfer", "import.failed", error: error);
                throw;
            }
        }
        finally
        {
            busy = false;
            gate.Release();
        }
    }

    private async Task<JsonObject> BuildExportObjectAsync(CancellationToken ct)
    {
        await appearance.LoadAsync();
        await closePreferences.LoadAsync();
        await mediaControls.LoadAsync();

        var musicAppearance = await music.Application.Library.GetAppearanceAsync(ct);
        var catalogSource = await music.Application.Library.GetCatalogSourceAsync(ct);
        var deviceSettings = await music.Application.Library.GetDeviceSettingsAsync(ct);
        var desktopLyrics = await music.Application.Library.GetDesktopLyricsAsync(ct);
        var playbackVolumes = await ReadPlaybackVolumesAsync(deviceSettings.Volume, ct);

        var tmdb = await film.Metadata.GetSettingsAsync(ct);
        var douban = await film.Metadata.GetDoubanSettingsAsync(ct);
        var filmPrefs = await film.ExportPreferencesAsync(ct);
        await film.Rules.InitializeAsync(ct);
        var filmRules = film.Rules.GetSavedSnapshot();
        var (catSettings, videoSources) = await film.Sources.ExportSettingsAsync(ct);
        var panAccounts = await film.ExportPanAccountsAsync(ct);

        var musicAccounts = await music.ExportOfflineAccountsAsync(ct);
        int playbackMode = music.Playback.Mode;
        var qualityPolicy = (await music.Application.Library.GetQualityPolicyAsync(ct)).Settings;
        var officialSources = await music.Application.Library.GetOfficialSourcesAsync(ct);
        var metadataPolicy = await music.Application.Library.GetMetadataPolicyAsync(ct);
        var searchPolicy = await music.Application.Library.GetSearchPolicyAsync(null, ct);
        var feedbackSettings = await music.Application.Feedback.ReadSettingsAsync(refresh: true, ct);
        var syncState = await music.Application.LibrarySync.GetStateAsync(ct);
        var musicScripts = await ExportMusicNetworkScriptsAsync(ct);

        await novel.Library.InitializeAsync(ct);
        var novelAppearance = novel.Library.Appearance;
        var novelRules = novel.Library.Rules;
        var novelSources = await novel.Sources.ExportAllDefinitionsAsync(ct);
        var novelCookies = await novel.Sources.ExportCookiesAsync(ct);

        var volumesObj = new JsonObject();
        foreach (var (owner, vol) in playbackVolumes)
            volumesObj[owner] = vol;

        var bindingsArr = new JsonArray();
        foreach (var b in mediaControls.Preferences.Bindings)
        {
            bindingsArr.Add(new JsonObject
            {
                ["action"] = b.Action.ToString(),
                ["local"] = b.Local is { } l ? new JsonObject { ["key"] = l.Key.ToString(), ["modifiers"] = (int)l.Modifiers } : null,
                ["global"] = b.Global is { } g ? new JsonObject { ["key"] = g.Key.ToString(), ["modifiers"] = (int)g.Modifiers } : null
            });
        }

        var catProvidersObj = new JsonObject();
        foreach (var (provider, enabled) in catSettings.Providers)
            catProvidersObj[provider] = enabled;

        var catMappingsArr = new JsonArray();
        foreach (var m in catSettings.Mappings)
            catMappingsArr.Add(new JsonObject { ["provider"] = m.Provider, ["aliases"] = ToJsonArray(m.Aliases) });

        var catScriptsArr = new JsonArray();
        foreach (var s in catSettings.Scripts)
        {
            catScriptsArr.Add(new JsonObject
            {
                ["installationId"] = s.InstallationId,
                ["name"] = s.Name,
                ["sourceUrl"] = s.SourceUrl,
                ["enabled"] = s.Enabled,
                ["inputRevision"] = s.InputRevision
            });
        }

        var probesArr = new JsonArray();
        foreach (var p in videoSources.Probes)
        {
            var sitesArr = new JsonArray();
            foreach (var site in p.Sites)
            {
                var methodsObj = new JsonObject();
                foreach (var (k, v) in site.Methods) methodsObj[k] = v;
                sitesArr.Add(new JsonObject
                {
                    ["key"] = site.Key,
                    ["name"] = site.Name,
                    ["api"] = site.Api,
                    ["actions"] = ToJsonArray(site.Actions),
                    ["methods"] = methodsObj
                });
            }
            probesArr.Add(new JsonObject
            {
                ["installationId"] = p.InstallationId,
                ["importedAt"] = p.ImportedAt,
                ["updatedAt"] = p.UpdatedAt,
                ["updateState"] = p.UpdateState,
                ["updateError"] = p.UpdateError,
                ["sourceHash"] = p.SourceHash,
                ["inputRevision"] = p.InputRevision >= 1 ? p.InputRevision : 1,
                ["sites"] = sitesArr
            });
        }

        var videosArr = new JsonArray();
        foreach (var v in videoSources.Videos)
        {
            var methodsObj = new JsonObject();
            foreach (var (k, val) in v.Methods) methodsObj[k] = val;
            JsonObject? detObj = null;
            if (v.Detection is { } det)
            {
                var stepsArr = new JsonArray();
                foreach (var st in det.Steps)
                    stepsArr.Add(new JsonObject { ["operation"] = st.Operation, ["state"] = st.State, ["error"] = st.Error });
                detObj = new JsonObject
                {
                    ["result"] = det.Result,
                    ["checkedAt"] = det.CheckedAt,
                    ["playbackResolved"] = det.PlaybackResolved,
                    ["steps"] = stepsArr
                };
            }
            videosArr.Add(new JsonObject
            {
                ["installationId"] = v.Key.InstallationId,
                ["siteKey"] = v.Key.SiteKey,
                ["name"] = v.Name,
                ["api"] = v.Api,
                ["order"] = v.Order,
                ["enabled"] = v.Options.Enabled,
                ["homeVisible"] = v.Options.HomeVisible,
                ["searchEnabled"] = v.Options.SearchEnabled,
                ["aggregateImages"] = v.Options.AggregateImages,
                ["actions"] = ToJsonArray(v.Actions),
                ["methods"] = methodsObj,
                ["detection"] = detObj
            });
        }

        var deletedArr = new JsonArray();
        foreach (var d in videoSources.Deleted)
            deletedArr.Add(new JsonObject { ["installationId"] = d.InstallationId, ["siteKey"] = d.SiteKey });

        var panAccountsObj = new JsonObject();
        foreach (var acc in panAccounts)
        {
            var creds = new JsonObject();
            if (!string.IsNullOrEmpty(acc.Cookie)) creds["cookie"] = acc.Cookie;
            if (!string.IsNullOrEmpty(acc.Username)) creds["username"] = acc.Username;
            if (!string.IsNullOrEmpty(acc.Password)) creds["password"] = acc.Password;
            if (!string.IsNullOrEmpty(acc.Authorization)) creds["authorization"] = acc.Authorization;
            if (acc.RefreshedAtMs is { } refAt) creds["refreshedAt"] = refAt;
            if (creds.Count > 0) panAccountsObj[acc.Provider] = creds;
        }

        var musicAccountsObj = new JsonObject();
        foreach (var acc in musicAccounts)
        {
            var credsObj = new JsonObject();
            foreach (var (k, v) in acc.Credentials) credsObj[k] = v;
            musicAccountsObj[acc.Platform] = new JsonObject
            {
                ["userId"] = acc.UserId,
                ["displayName"] = acc.DisplayName,
                ["avatar"] = acc.Avatar,
                ["cookie"] = acc.Cookie,
                ["credentials"] = credsObj
            };
        }

        var musicSyncObj = new JsonObject();
        if (syncState.Platforms is not null)
        {
            foreach (string plat in MusicThreePlatforms)
            {
                if (syncState.Platforms.TryGetValue(plat, out var sp))
                    musicSyncObj[plat] = new JsonObject { ["favorites"] = sp.Favorites, ["playlists"] = sp.Playlists };
                else
                    musicSyncObj[plat] = new JsonObject { ["favorites"] = false, ["playlists"] = false };
            }
        }

        var novelRulesArr = new JsonArray();
        foreach (var r in novelRules)
        {
            novelRulesArr.Add(new JsonObject
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["group"] = r.Group,
                ["pattern"] = r.Pattern,
                ["replacement"] = r.Replacement,
                ["isRegex"] = r.IsRegex,
                ["enabled"] = r.Enabled,
                ["scopeTitle"] = r.ScopeTitle,
                ["scopeContent"] = r.ScopeContent,
                ["scope"] = r.Scope,
                ["excludeScope"] = r.ExcludeScope,
                ["order"] = r.Order,
                ["timeoutMillisecond"] = r.TimeoutMillisecond
            });
        }

        var novelSourcesArr = new JsonArray();
        foreach (var s in novelSources)
        {
            JsonNode? defNode = null;
            try { defNode = JsonNode.Parse(s.Definition); }
            catch { defNode = JsonValue.Create(s.Definition); }
            novelSourcesArr.Add(new JsonObject
            {
                ["installationId"] = s.InstallationId,
                ["name"] = s.Name,
                ["sourceUrl"] = s.SourceUrl,
                ["group"] = s.Group,
                ["order"] = s.Order,
                ["enabled"] = s.Enabled,
                ["enabledExplore"] = s.EnabledExplore,
                ["protocol"] = s.Protocol,
                ["definition"] = defNode
            });
        }

        var novelCookiesNode = JsonNode.Parse(novelCookies.GetRawText()) as JsonArray ?? new JsonArray();

        return new JsonObject
        {
            ["format"] = "meowfilm.settings",
            ["version"] = 1,
            ["general"] = new JsonObject
            {
                ["musicAppearance"] = new JsonObject
                {
                    ["playlistView"] = musicAppearance.PlaylistView,
                    ["playerTheme"] = musicAppearance.PlayerTheme,
                    ["colorMode"] = musicAppearance.ColorMode,
                    ["colorPalette"] = musicAppearance.ColorPalette
                },
                ["musicCatalogSource"] = catalogSource.Platform,
                ["playbackVolumes"] = volumesObj
            },
            ["windows"] = new JsonObject
            {
                ["themeColor"] = appearance.ThemeColor,
                ["closeBehavior"] = closePreferences.Behavior switch
                {
                    WindowCloseBehavior.MinimizeToTray => "minimizeToTray",
                    WindowCloseBehavior.Exit => "exit",
                    _ => "ask"
                },
                ["mediaControls"] = new JsonObject
                {
                    ["globalEnabled"] = mediaControls.Preferences.GlobalEnabled,
                    ["systemMediaEnabled"] = mediaControls.Preferences.SystemMediaEnabled,
                    ["bindings"] = bindingsArr
                },
                ["desktopLyrics"] = new JsonObject
                {
                    ["layout"] = desktopLyrics.Layout,
                    ["normalColor"] = desktopLyrics.NormalColor,
                    ["highlightColor"] = desktopLyrics.HighlightColor,
                    ["geometry"] = desktopLyrics.Geometry is { } geom
                        ? new JsonObject
                        {
                            ["monitor"] = geom.Monitor,
                            ["x"] = geom.X,
                            ["y"] = geom.Y,
                            ["width"] = geom.Width,
                            ["height"] = geom.Height
                        }
                        : null
                }
            },
            ["film"] = new JsonObject
            {
                ["tmdb"] = new JsonObject
                {
                    ["apiBase"] = tmdb.ApiBase,
                    ["imageBase"] = tmdb.ImageBase,
                    ["apiKey"] = tmdb.ApiKey,
                    ["accessToken"] = tmdb.AccessToken,
                    ["language"] = tmdb.Language,
                    ["region"] = tmdb.Region,
                    ["includeAdult"] = tmdb.IncludeAdult
                },
                ["douban"] = new JsonObject
                {
                    ["apiBase"] = douban.ApiBase,
                    ["searchBase"] = douban.SearchBase,
                    ["imageBase"] = douban.ImageBase,
                    ["apiProxy"] = douban.ApiProxy,
                    ["imageProxy"] = douban.ImageProxy,
                    ["cookie"] = douban.Cookie
                },
                ["preferences"] = new JsonObject
                {
                    ["ruleOrder"] = ToJsonArray(filmPrefs.RuleOrder),
                    ["panOrder"] = ToJsonArray(filmPrefs.PanOrder),
                    ["keywords"] = ToJsonArray(filmPrefs.Keywords),
                    ["siteCleanKeywords"] = ToJsonArray(filmPrefs.SiteCleanKeywords),
                    ["iosAutoPrepare"] = filmPrefs.IosAutoPrepare,
                    ["metadataViewProvider"] = filmPrefs.MetadataViewProvider
                },
                ["rules"] = new JsonObject
                {
                    ["episodeCleanRegexRules"] = ToJsonArray(filmRules.EpisodeCleanRegexRules),
                    ["episodeRules"] = ToMatchRulesArray(filmRules.EpisodeRules),
                    ["movieRules"] = ToMatchRulesArray(filmRules.MovieRules),
                    ["aggregateRegexRules"] = ToJsonArray(filmRules.AggregateRegexRules),
                    ["seasonRules"] = ToMatchRulesArray(filmRules.SeasonRules)
                },
                ["cat"] = new JsonObject
                {
                    ["port"] = catSettings.Port,
                    ["providers"] = catProvidersObj,
                    ["mappings"] = catMappingsArr,
                    ["scripts"] = catScriptsArr
                },
                ["videoSources"] = new JsonObject
                {
                    ["probes"] = probesArr,
                    ["videos"] = videosArr,
                    ["deleted"] = deletedArr
                }
            },
            ["pan"] = new JsonObject
            {
                ["accounts"] = panAccountsObj
            },
            ["music"] = new JsonObject
            {
                ["accounts"] = musicAccountsObj,
                ["playback"] = new JsonObject
                {
                    ["mode"] = playbackMode,
                    ["showTranslation"] = music.Lyrics.ShowLyricTranslation,
                    ["qualityRequest"] = qualityPolicy.Request,
                    ["qualityOrder"] = ToJsonArray(qualityPolicy.QualityOrder),
                    ["qualityEnabled"] = ToJsonArray(qualityPolicy.QualityEnabled),
                    ["spatialOrder"] = ToJsonArray(qualityPolicy.SpatialOrder),
                    ["spatialEnabled"] = ToJsonArray(qualityPolicy.SpatialEnabled),
                    ["officialPlatforms"] = ToJsonArray(officialSources.Platforms),
                    ["officialEnabled"] = ToJsonArray(officialSources.Enabled),
                    ["metadataPlatforms"] = ToJsonArray(metadataPolicy.Platforms),
                    ["metadataEnabled"] = ToJsonArray(metadataPolicy.Enabled)
                },
                ["search"] = new JsonObject
                {
                    ["songs"] = ToSearchKindObject(searchPolicy.Songs),
                    ["playlists"] = ToSearchKindObject(searchPolicy.Playlists)
                },
                ["feedback"] = new JsonObject
                {
                    ["enabled"] = ToJsonArray(feedbackSettings.Enabled)
                },
                ["sync"] = musicSyncObj,
                ["scripts"] = musicScripts
            },
            ["novel"] = new JsonObject
            {
                ["reading"] = new JsonObject
                {
                    ["mode"] = novelAppearance.Mode,
                    ["fontSize"] = novelAppearance.FontSize,
                    ["bold"] = novelAppearance.Bold,
                    ["fontFamily"] = novelAppearance.FontFamily,
                    ["lineSpacing"] = novelAppearance.LineHeight,
                    ["paragraphSpacing"] = novelAppearance.ParagraphSpacing,
                    ["margin"] = novelAppearance.Margin,
                    ["contentWidth"] = novelAppearance.ContentWidth,
                    ["conversion"] = novelAppearance.Conversion,
                    ["theme"] = novelAppearance.Theme,
                    ["foreground"] = novelAppearance.Foreground,
                    ["background"] = novelAppearance.Background,
                    ["backgroundImage"] = novelAppearance.BackgroundImage,
                    ["autoSpeed"] = novelAppearance.AutoSpeed
                },
                ["rules"] = novelRulesArr,
                ["sources"] = novelSourcesArr,
                ["cookies"] = novelCookiesNode
            }
        };
    }

    private async Task ApplyImportRootAsync(JsonElement root, CancellationToken ct)
    {
        bool musicTouched = false;

        if (TryGetObject(root, "windows", out var windowsBlock))
        {
            await ApplyWindowsBlockAsync(windowsBlock, ct);
            if (TryGetObject(windowsBlock, "desktopLyrics", out var desktopLyricsBlock))
            {
                await ApplyDesktopLyricsAsync(desktopLyricsBlock, ct);
                musicTouched = true;
            }
        }

        if (TryGetObject(root, "general", out var generalBlock))
        {
            if (await ApplyGeneralBlockAsync(generalBlock, ct))
                musicTouched = true;
        }

        if (TryGetObject(root, "pan", out var panBlock) && TryGetObject(panBlock, "accounts", out var panAccountsEl))
        {
            await ApplyPanAccountsAsync(panAccountsEl, ct);
        }

        if (TryGetObject(root, "film", out var filmBlock))
        {
            await ApplyFilmBlockAsync(filmBlock, ct);
        }
        else if (TryGetObject(root, "pan", out _))
        {
            await film.RefreshSettingsAfterImportAsync();
        }

        if (TryGetObject(root, "music", out var musicBlock))
        {
            await ApplyMusicBlockAsync(musicBlock, ct);
            musicTouched = true;
        }

        if (musicTouched)
        {
            await music.RefreshSettingsAfterImportAsync(ct);
        }

        if (TryGetObject(root, "novel", out var novelBlock))
        {
            await ApplyNovelBlockAsync(novelBlock, ct);
        }
    }

    private async Task ApplyWindowsBlockAsync(JsonElement block, CancellationToken ct)
    {
        if (TryGetString(block, "themeColor", out var colorText) && AppearanceColors.TryParse(colorText, out var parsedColor))
        {
            if (!appearance.CanEdit) await appearance.LoadAsync();
            await appearance.SaveAsync(AppearanceColors.Hex(parsedColor));
        }

        if (TryGetString(block, "closeBehavior", out var closeText))
        {
            WindowCloseBehavior? behavior = closeText.Trim() switch
            {
                "ask" => WindowCloseBehavior.Ask,
                "minimizeToTray" => WindowCloseBehavior.MinimizeToTray,
                "exit" => WindowCloseBehavior.Exit,
                _ => null
            };
            if (behavior is { } b)
            {
                if (!closePreferences.CanEdit) await closePreferences.LoadAsync();
                await closePreferences.SaveAsync(b);
            }
        }

        if (TryGetObject(block, "mediaControls", out var mediaEl))
        {
            if (!mediaControls.CanEdit) await mediaControls.LoadAsync();
            var current = mediaControls.Preferences;
            bool globalEnabled = TryGetBool(mediaEl, "globalEnabled", out bool ge) ? ge : current.GlobalEnabled;
            bool systemEnabled = TryGetBool(mediaEl, "systemMediaEnabled", out bool se) ? se : current.SystemMediaEnabled;
            var bindingsByAction = current.Bindings.ToDictionary(b => b.Action);
            if (TryGetArray(mediaEl, "bindings", out var bindingsEl))
            {
                foreach (var item in bindingsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !TryGetString(item, "action", out var actionStr) ||
                        !Enum.TryParse<MediaShortcutAction>(actionStr, ignoreCase: true, out var action) ||
                        !Enum.IsDefined(action))
                        continue;
                    MediaShortcutGesture? local = bindingsByAction.GetValueOrDefault(action)?.Local;
                    MediaShortcutGesture? global = bindingsByAction.GetValueOrDefault(action)?.Global;
                    if (item.TryGetProperty("local", out var localEl))
                        local = TryParseGesture(localEl, local);
                    if (item.TryGetProperty("global", out var globalEl))
                        global = TryParseGesture(globalEl, global);
                    var candidateBinding = new MediaShortcutBinding(action, local, global);
                    var trialMap = new Dictionary<MediaShortcutAction, MediaShortcutBinding>(bindingsByAction)
                    {
                        [action] = candidateBinding
                    };
                    var trial = new MediaControlPreferences(globalEnabled, systemEnabled, trialMap.Values.OrderBy(b => b.Action).ToArray());
                    if (MediaShortcutGestures.ValidationError(trial) is null)
                        bindingsByAction[action] = candidateBinding;
                }
            }
            var finalCandidate = new MediaControlPreferences(globalEnabled, systemEnabled, bindingsByAction.Values.OrderBy(b => b.Action).ToArray());
            if (MediaShortcutGestures.ValidationError(finalCandidate) is null)
            {
                try { await mediaControls.SaveAsync(finalCandidate); } catch { }
            }
        }
    }

    private static MediaShortcutGesture? TryParseGesture(JsonElement el, MediaShortcutGesture? fallback)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        if (!TryGetString(el, "key", out var keyText) || !Enum.TryParse<VirtualKey>(keyText, ignoreCase: true, out var key))
            return fallback;
        int mods = TryGetInt32(el, "modifiers", out int m) ? m : 0;
        var gesture = new MediaShortcutGesture(key, (VirtualKeyModifiers)mods);
        return MediaShortcutGestures.IsValid(gesture) ? gesture : fallback;
    }

    private async Task ApplyDesktopLyricsAsync(JsonElement block, CancellationToken ct)
    {
        try
        {
            var current = await music.Application.Library.GetDesktopLyricsAsync(ct);
            string layout = TryGetString(block, "layout", out var l) && l is "single" or "double" ? l : current.Layout;
            string normal = TryGetString(block, "normalColor", out var nc) && AppearanceColors.TryParse(nc, out var pnc)
                ? AppearanceColors.Hex(pnc) : current.NormalColor;
            string highlight = TryGetString(block, "highlightColor", out var hc) && AppearanceColors.TryParse(hc, out var phc)
                ? AppearanceColors.Hex(phc) : current.HighlightColor;
            bool includeGeometry = false;
            MusicDesktopLyricsGeometryRecord? geom = current.Geometry;
            if (block.TryGetProperty("geometry", out var geomEl))
            {
                if (geomEl.ValueKind == JsonValueKind.Null)
                {
                    geom = null;
                    includeGeometry = true;
                }
                else if (geomEl.ValueKind == JsonValueKind.Object &&
                         TryGetString(geomEl, "monitor", out var mon) && !string.IsNullOrWhiteSpace(mon) &&
                         TryGetDouble(geomEl, "x", out double x) &&
                         TryGetDouble(geomEl, "y", out double y) &&
                         TryGetDouble(geomEl, "width", out double w) && w > 0 &&
                         TryGetDouble(geomEl, "height", out double h) && h > 0)
                {
                    geom = new MusicDesktopLyricsGeometryRecord(mon, x, y, w, h);
                    includeGeometry = true;
                }
            }
            await music.Application.Library.SaveDesktopLyricsAsync(current.Revision, layout, normal, highlight, geom, includeGeometry, ct);
        }
        catch { }
    }

    private async Task<bool> ApplyGeneralBlockAsync(JsonElement block, CancellationToken ct)
    {
        bool musicTouched = false;

        if (TryGetObject(block, "musicAppearance", out var appEl))
        {
            try
            {
                var current = await music.Application.Library.GetAppearanceAsync(ct);
                string pv = TryGetString(appEl, "playlistView", out var pvStr) && pvStr is "grid" or "list" ? pvStr : current.PlaylistView;
                string pt = TryGetString(appEl, "playerTheme", out var ptStr) && ptStr == "starlight" ? ptStr : current.PlayerTheme;
                string cm = TryGetString(appEl, "colorMode", out var cmStr) && cmStr is "song" or "fixed" ? cmStr : current.ColorMode;
                string cp = current.ColorPalette;
                if (TryGetString(appEl, "colorPalette", out var cpStr) && MusicAppearancePalette.IsValid(cpStr))
                    cp = AppearanceColors.TryParse(cpStr, out var c) ? AppearanceColors.Hex(c) : cpStr;
                await music.Application.Library.SaveAppearanceAsync(current.Revision, pv, pt, cm, cp, ct);
                musicTouched = true;
            }
            catch { }
        }

        if (TryGetString(block, "musicCatalogSource", out var catPlat) && catPlat is "wy" or "kg")
        {
            try
            {
                var current = await music.Application.Library.GetCatalogSourceAsync(ct);
                if (current.Platform != catPlat)
                    await music.Application.Library.SaveCatalogSourceAsync(current.Revision, catPlat, ct);
                musicTouched = true;
            }
            catch { }
        }

        if (TryGetObject(block, "playbackVolumes", out var volsEl))
        {
            if (TryGetDouble(volsEl, "music", out double mv) && mv is >= 0 and <= 1)
            {
                try
                {
                    var current = await music.Application.Library.GetDeviceSettingsAsync(ct);
                    int pct = Math.Clamp((int)Math.Round(mv * 100.0, MidpointRounding.AwayFromZero), 0, 100);
                    await music.Application.Library.SaveDeviceSettingsAsync(current.Revision, pct, ct);
                    musicTouched = true;
                }
                catch { }
            }
            if (TryGetDouble(volsEl, "film", out double filmVol) && filmVol is >= 0 and <= 1)
            {
                try { await SaveOwnerPlaybackVolumeAsync("film", filmVol, ct); } catch { }
            }
        }

        return musicTouched;
    }

    private async Task ApplyPanAccountsAsync(JsonElement accountsObj, CancellationToken ct)
    {
        var map = new Dictionary<string, ExportedPanAccount?>(StringComparer.Ordinal);
        foreach (string provider in new[] { "baidu", "quark", "uc", "tianyi", "yidong" })
        {
            if (!TryGetObject(accountsObj, provider, out var item))
            {
                map[provider] = null;
                continue;
            }
            TryGetString(item, "cookie", out var cookie);
            TryGetString(item, "username", out var username);
            TryGetString(item, "password", out var password);
            TryGetString(item, "authorization", out var authorization);
            long? refreshedAt = TryGetInt64(item, "refreshedAt", out long r) && r >= 0 ? r : null;
            bool anyField = !string.IsNullOrEmpty(cookie) || !string.IsNullOrEmpty(username) ||
                            !string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(authorization) || refreshedAt is not null;
            map[provider] = anyField ? new ExportedPanAccount(provider, cookie, username, password, authorization, refreshedAt) : null;
        }
        await film.ReplacePanAccountsAsync(map, ct);
    }

    private async Task ApplyFilmBlockAsync(JsonElement block, CancellationToken ct)
    {
        if (TryGetObject(block, "tmdb", out var tmdbEl))
        {
            foreach (string key in new[] { "apiBase", "imageBase", "apiKey", "accessToken", "language", "region", "includeAdult" })
            {
                if (!tmdbEl.TryGetProperty(key, out var val)) continue;
                object? patchVal = key == "includeAdult"
                    ? (val.ValueKind is JsonValueKind.True or JsonValueKind.False ? val.GetBoolean() : null)
                    : (val.ValueKind == JsonValueKind.String ? val.GetString() : null);
                if (patchVal is null) continue;
                try
                {
                    await film.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { [key] = patchVal }, ct);
                }
                catch { }
            }
        }

        if (TryGetObject(block, "douban", out var doubanEl))
        {
            foreach (string key in new[] { "apiBase", "searchBase", "imageBase", "apiProxy", "imageProxy", "cookie" })
            {
                if (!TryGetString(doubanEl, key, out var val)) continue;
                try
                {
                    await film.Metadata.SaveDoubanSettingsAsync(new Dictionary<string, object?> { [key] = val }, ct);
                }
                catch { }
            }
        }

        if (TryGetObject(block, "preferences", out var prefsEl))
        {
            string[]? ruleOrder = TryGetStringArray(prefsEl, "ruleOrder", out var ro) ? ro : null;
            string[]? panOrder = TryGetStringArray(prefsEl, "panOrder", out var po) ? po : null;
            string[]? keywords = TryGetStringArray(prefsEl, "keywords", out var kw) ? kw : null;
            string[]? cleanKw = TryGetStringArray(prefsEl, "siteCleanKeywords", out var sc) ? sc : null;
            bool? autoPrep = TryGetBool(prefsEl, "iosAutoPrepare", out bool ap) ? ap : null;
            string? viewProvider = TryGetString(prefsEl, "metadataViewProvider", out var mp) ? mp : null;
            try
            {
                await film.SaveImportedPreferencesAsync(ruleOrder, panOrder, keywords, cleanKw, autoPrep, viewProvider, ct);
            }
            catch { }
        }

        if (TryGetObject(block, "rules", out var rulesEl))
        {
            try
            {
                await film.Rules.InitializeAsync(ct);
                var current = film.Rules.GetSavedSnapshot();
                var next = new FilmRuleSet
                {
                    EpisodeCleanRegexRules = TryGetStringArray(rulesEl, "episodeCleanRegexRules", out var ec) ? ec : current.EpisodeCleanRegexRules,
                    EpisodeRules = TryParseMatchRules(rulesEl, "episodeRules", out var er) ? er : current.EpisodeRules,
                    MovieRules = TryParseMatchRules(rulesEl, "movieRules", out var mr) ? mr : current.MovieRules,
                    AggregateRegexRules = TryGetStringArray(rulesEl, "aggregateRegexRules", out var ar) ? ar : current.AggregateRegexRules,
                    SeasonRules = TryParseMatchRules(rulesEl, "seasonRules", out var sr) ? sr : current.SeasonRules
                };
                await film.Rules.SaveAsync(next, ct);
            }
            catch { }
        }

        if (TryGetObject(block, "cat", out var catEl))
        {
            int? port = TryGetInt32(catEl, "port", out int p) && p is >= 0 and <= 65535 ? p : null;
            Dictionary<string, bool>? providers = null;
            if (TryGetObject(catEl, "providers", out var provEl))
            {
                providers = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var prop in provEl.EnumerateObject())
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        providers[prop.Name] = prop.Value.GetBoolean();
            }
            List<ExportedCatMapping>? mappings = null;
            if (TryGetArray(catEl, "mappings", out var mapEl))
            {
                mappings = [];
                foreach (var item in mapEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "provider", out var mProv)) continue;
                    string[] aliases = TryGetStringArray(item, "aliases", out var al) ? al : [];
                    mappings.Add(new ExportedCatMapping(mProv, aliases));
                }
            }
            List<ExportedCatScript>? scripts = null;
            if (TryGetArray(catEl, "scripts", out var scrEl))
            {
                scripts = [];
                foreach (var item in scrEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string url = TryGetString(item, "sourceUrl", out var u) ? u : "";
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    string name = TryGetString(item, "name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : url;
                    string id = TryGetString(item, "installationId", out var inst) ? inst : "";
                    bool enabled = !TryGetBool(item, "enabled", out bool en) || en;
                    long rev = TryGetInt64(item, "inputRevision", out long r) && r >= 1 ? r : 1;
                    scripts.Add(new ExportedCatScript(id, name, url, enabled, rev));
                }
            }
            try { await film.Sources.ReplaceCatSettingsAsync(port, providers, mappings, scripts, ct); } catch { }
        }

        if (TryGetObject(block, "videoSources", out var vsEl))
        {
            List<ExportedFilmProbe>? probes = null;
            if (TryGetArray(vsEl, "probes", out var probesEl))
            {
                probes = [];
                foreach (var item in probesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "installationId", out var id)) continue;
                    long? imp = TryGetInt64(item, "importedAt", out long iAt) ? iAt : null;
                    long? upd = TryGetInt64(item, "updatedAt", out long uAt) ? uAt : null;
                    string uState = TryGetString(item, "updateState", out var us) ? us : "idle";
                    string? uErr = TryGetString(item, "updateError", out var ue) ? ue : null;
                    string? hash = TryGetString(item, "sourceHash", out var sh) ? sh : null;
                    long inRev = TryGetInt64(item, "inputRevision", out long ir) && ir >= 1 ? ir : 1;
                    var sites = new List<FilmSourceSite>();
                    if (TryGetArray(item, "sites", out var sitesEl))
                    {
                        foreach (var sEl in sitesEl.EnumerateArray())
                        {
                            if (sEl.ValueKind != JsonValueKind.Object ||
                                !TryGetString(sEl, "key", out var sKey) ||
                                !TryGetString(sEl, "name", out var sName) ||
                                !TryGetString(sEl, "api", out var sApi)) continue;
                            string[] sActions = TryGetStringArray(sEl, "actions", out var sa) ? sa : [];
                            var sMethods = ReadStringDict(sEl, "methods");
                            sites.Add(new FilmSourceSite(sKey, sName, sApi, sActions, sMethods));
                        }
                    }
                    probes.Add(new ExportedFilmProbe(id, imp, upd, uState, uErr, hash, inRev, sites));
                }
            }

            List<FilmVideoSource>? videos = null;
            if (TryGetArray(vsEl, "videos", out var videosEl))
            {
                videos = [];
                int idx = 0;
                foreach (var item in videosEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !TryGetString(item, "installationId", out var id) ||
                        !TryGetString(item, "siteKey", out var siteKey) ||
                        !TryGetString(item, "name", out var name) ||
                        !TryGetString(item, "api", out var api)) continue;
                    int order = TryGetInt32(item, "order", out int ord) && ord >= 0 ? ord : idx;
                    bool en = !TryGetBool(item, "enabled", out bool bEn) || bEn;
                    bool home = !TryGetBool(item, "homeVisible", out bool bHome) || bHome;
                    bool search = !TryGetBool(item, "searchEnabled", out bool bSearch) || bSearch;
                    bool agg = TryGetBool(item, "aggregateImages", out bool bAgg) && bAgg;
                    string[] actions = TryGetStringArray(item, "actions", out var vAct) ? vAct : [];
                    var methods = ReadStringDict(item, "methods");
                    FilmSourceDetection? det = null;
                    if (TryGetObject(item, "detection", out var detEl) &&
                        TryGetString(detEl, "result", out var dRes) &&
                        TryGetInt64(detEl, "checkedAt", out long dChk) &&
                        TryGetBool(detEl, "playbackResolved", out bool dPlay))
                    {
                        var steps = new List<FilmSourceProbeStep>();
                        if (TryGetArray(detEl, "steps", out var stepsEl))
                        {
                            foreach (var stEl in stepsEl.EnumerateArray())
                            {
                                if (stEl.ValueKind == JsonValueKind.Object &&
                                    TryGetString(stEl, "operation", out var op) &&
                                    TryGetString(stEl, "state", out var stState))
                                {
                                    TryGetString(stEl, "error", out var stErr);
                                    steps.Add(new FilmSourceProbeStep(op, stState, stErr));
                                }
                            }
                        }
                        det = new FilmSourceDetection(dRes, dChk, dPlay, steps);
                    }
                    videos.Add(new FilmVideoSource(new FilmSourceKey(id, siteKey), name, api, actions, methods,
                        new FilmVideoOptions(en, home, search, agg), order, det));
                    idx++;
                }
            }

            List<FilmSourceKey>? deleted = null;
            if (TryGetArray(vsEl, "deleted", out var delEl))
            {
                deleted = [];
                foreach (var item in delEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        TryGetString(item, "installationId", out var id) &&
                        TryGetString(item, "siteKey", out var siteKey))
                        deleted.Add(new FilmSourceKey(id, siteKey));
                }
            }

            try { await film.Sources.ReplaceVideoSourcesAsync(probes, videos, deleted, ct); } catch { }
        }

        await film.RefreshSettingsAfterImportAsync();
    }

    private async Task ApplyMusicBlockAsync(JsonElement block, CancellationToken ct)
    {
        if (TryGetObject(block, "accounts", out var accountsEl))
        {
            var targets = new Dictionary<string, MusicOfflineAccountRecord?>(StringComparer.Ordinal);
            foreach (string plat in MusicThreePlatforms)
            {
                if (!TryGetObject(accountsEl, plat, out var item))
                {
                    targets[plat] = null;
                    continue;
                }
                TryGetString(item, "userId", out var uid);
                TryGetString(item, "displayName", out var dname);
                TryGetString(item, "avatar", out var avatar);
                TryGetString(item, "cookie", out var cookie);
                var creds = ReadStringDict(item, "credentials");
                if (creds.Count == 0 && string.IsNullOrWhiteSpace(cookie))
                    targets[plat] = null;
                else
                    targets[plat] = new MusicOfflineAccountRecord(plat, uid ?? "", dname ?? "", avatar, cookie ?? "", creds);
            }
            try { await music.ReplaceOfflineAccountsAsync(targets, ct); } catch { }
        }

        if (TryGetObject(block, "playback", out var pbEl))
        {
            if (TryGetInt32(pbEl, "mode", out int mode) && mode is >= 1 and <= 3)
            {
                try { await music.Playback.ChangeModeAsync(mode); } catch { }
            }
            if (TryGetBool(pbEl, "showTranslation", out bool st) && music.Lyrics.ShowLyricTranslation != st)
            {
                music.Lyrics.ToggleLyricTranslation();
            }

            bool hasQuality = pbEl.TryGetProperty("qualityRequest", out _) ||
                              pbEl.TryGetProperty("qualityOrder", out _) ||
                              pbEl.TryGetProperty("qualityEnabled", out _) ||
                              pbEl.TryGetProperty("spatialOrder", out _) ||
                              pbEl.TryGetProperty("spatialEnabled", out _);
            if (hasQuality)
            {
                try
                {
                    var current = (await music.Application.Library.GetQualityPolicyAsync(ct)).Settings;
                    string req = TryGetString(pbEl, "qualityRequest", out var qr) && MusicQualityRequests.Contains(qr, StringComparer.Ordinal)
                        ? qr : current.Request;
                    string[] qOrder = NormalizeOrderedSubset(
                        TryGetStringArray(pbEl, "qualityOrder", out var qo) ? qo : current.QualityOrder,
                        MusicQualityCategories, appendMissing: true);
                    string[] qEnabled = NormalizeOrderedSubset(
                        TryGetStringArray(pbEl, "qualityEnabled", out var qe) ? qe : current.QualityEnabled,
                        qOrder, appendMissing: false);
                    string[] sOrder = NormalizeOrderedSubset(
                        TryGetStringArray(pbEl, "spatialOrder", out var so) ? so : current.SpatialOrder,
                        MusicSpatialCategories, appendMissing: true);
                    string[] sEnabled = NormalizeOrderedSubset(
                        TryGetStringArray(pbEl, "spatialEnabled", out var se) ? se : current.SpatialEnabled,
                        sOrder, appendMissing: false);
                    await music.Application.Library.SaveQualityPolicyAsync(current.Revision, req, qOrder, qEnabled, sOrder, sEnabled, ct);
                }
                catch { }
            }

            if (TryGetStringArray(pbEl, "officialEnabled", out var offEn))
            {
                try
                {
                    var current = await music.Application.Library.GetOfficialSourcesAsync(ct);
                    string[] enabled = NormalizeOrderedSubset(offEn, MusicThreePlatforms, appendMissing: false);
                    await music.Application.Library.SaveOfficialSourcesAsync(current.Revision, enabled, ct);
                }
                catch { }
            }

            if (pbEl.TryGetProperty("metadataPlatforms", out _) || pbEl.TryGetProperty("metadataEnabled", out _))
            {
                try
                {
                    var current = await music.Application.Library.GetMetadataPolicyAsync(ct);
                    string[] mOrder = NormalizeOrderedSubset(
                        TryGetStringArray(pbEl, "metadataPlatforms", out var mp) ? mp : current.Platforms,
                        MusicFivePlatforms, appendMissing: true);
                    string[] mEnabled = NormalizeOrderedSubset(
                        TryGetStringArray(pbEl, "metadataEnabled", out var me) ? me : current.Enabled,
                        mOrder, appendMissing: false);
                    await music.Application.Library.SaveMetadataPolicyAsync(current.Revision, mOrder, mEnabled, ct);
                }
                catch { }
            }
        }

        if (TryGetObject(block, "search", out var searchEl))
        {
            try
            {
                var current = await music.Application.Library.GetSearchPolicyAsync(null, ct);
                var songs = TryGetObject(searchEl, "songs", out var sEl) ? ParseSearchKind(sEl, current.Songs) : current.Songs;
                var playlists = TryGetObject(searchEl, "playlists", out var pEl) ? ParseSearchKind(pEl, current.Playlists) : current.Playlists;
                await music.Application.Library.SaveSearchPolicyAsync(current.Revision, songs, playlists, ct);
            }
            catch { }
        }

        if (TryGetObject(block, "feedback", out var fbEl) && TryGetStringArray(fbEl, "enabled", out var fbEnabled))
        {
            try
            {
                var current = await music.Application.Feedback.ReadSettingsAsync(refresh: true, ct);
                string[] enabled = NormalizeOrderedSubset(fbEnabled, MusicThreePlatforms, appendMissing: false);
                await music.Application.Feedback.SaveSettingsAsync(current.Revision, enabled, ct);
            }
            catch { }
        }

        if (TryGetObject(block, "sync", out var syncEl))
        {
            try
            {
                foreach (string plat in MusicThreePlatforms)
                {
                    var state = await music.Application.LibrarySync.GetStateAsync(ct);
                    if (!state.Ok) break;
                    bool fav = false, pl = false;
                    if (TryGetObject(syncEl, plat, out var platEl))
                    {
                        fav = TryGetBool(platEl, "favorites", out bool f) && f;
                        pl = TryGetBool(platEl, "playlists", out bool p) && p;
                    }
                    var currentPlat = state.Platforms?.GetValueOrDefault(plat);
                    if (currentPlat is not null && currentPlat.Favorites == fav && currentPlat.Playlists == pl)
                        continue;
                    await music.Application.LibrarySync.SetPlatformAsync(plat, fav, pl, state.Revision, confirm: true, ct);
                }
            }
            catch { }
        }

        if (TryGetArray(block, "scripts", out var scriptsEl))
        {
            await ReplaceMusicNetworkScriptsAsync(scriptsEl, ct);
        }
    }

    private async Task ApplyNovelBlockAsync(JsonElement block, CancellationToken ct)
    {
        await novel.Library.InitializeAsync(ct);

        if (TryGetObject(block, "reading", out var readEl))
        {
            var cur = novel.Library.Appearance;
            string mode = TryGetString(readEl, "mode", out var m) && m is "cover" or "slide" or "none" or "scroll" ? m : cur.Mode;
            double fontSize = TryGetDouble(readEl, "fontSize", out double fs) && NovelAppearance.ValidSize(fs) ? fs : cur.FontSize;
            bool bold = TryGetBool(readEl, "bold", out bool b) ? b : cur.Bold;
            string family = TryGetString(readEl, "fontFamily", out var ff) && NovelAppearance.ValidFamily(ff) ? ff : cur.FontFamily;
            double lineSpacing = TryGetDouble(readEl, "lineSpacing", out double ls) && NovelAppearance.ValidLineHeight(fontSize, ls)
                ? ls
                : NovelAppearance.ValidLineHeight(fontSize, cur.LineHeight) ? cur.LineHeight : Math.Round(fontSize * 0.6, 1);
            double paraSpacing = TryGetDouble(readEl, "paragraphSpacing", out double ps) && ps >= 0 ? ps : cur.ParagraphSpacing;
            double margin = TryGetDouble(readEl, "margin", out double mg) && mg >= 0 && double.IsFinite(mg * 2) ? mg : cur.Margin;
            double width = TryGetDouble(readEl, "contentWidth", out double cw) && cw > 0 ? cw : cur.ContentWidth;
            string conv = TryGetString(readEl, "conversion", out var cv) && cv is "none" or "simplified" or "traditional" ? cv : cur.Conversion;
            int theme = TryGetInt32(readEl, "theme", out int th) && th >= 0 && th < NovelAppearance.Themes.Length ? th : cur.Theme;
            string fg = TryGetString(readEl, "foreground", out var fgStr) && NovelAppearance.TryParseColor(fgStr, out _, out _, out _, out _) ? fgStr : cur.Foreground;
            string bg = TryGetString(readEl, "background", out var bgStr) && NovelAppearance.TryParseColor(bgStr, out _, out _, out _, out _) ? bgStr : cur.Background;
            string bgImg = TryGetString(readEl, "backgroundImage", out var bi) && !bi.Any(char.IsControl) ? bi : cur.BackgroundImage;
            double autoSpeed = TryGetDouble(readEl, "autoSpeed", out double spd) && spd is >= 1 and <= 10 ? spd : cur.AutoSpeed;

            var next = new NovelAppearance
            {
                Mode = mode,
                FontSize = fontSize,
                Bold = bold,
                FontFamily = family,
                LineHeight = lineSpacing,
                ParagraphSpacing = paraSpacing,
                Margin = margin,
                ContentWidth = width,
                Conversion = conv,
                Theme = theme,
                Foreground = fg,
                Background = bg,
                BackgroundImage = bgImg,
                AutoSpeed = autoSpeed
            };
            try { await novel.Library.SaveAppearanceAsync(next, ct); } catch { }
        }

        if (TryGetArray(block, "rules", out var rulesEl))
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<NovelRule>();
            int idx = 0;
            foreach (var item in rulesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetString(item, "pattern", out var pattern) || string.IsNullOrEmpty(pattern)) continue;
                string id = TryGetString(item, "id", out var rid) && !string.IsNullOrWhiteSpace(rid) && !seenIds.Contains(rid)
                    ? rid : Guid.NewGuid().ToString("N");
                seenIds.Add(id);
                string name = TryGetString(item, "name", out var rname) ? rname : "";
                string grp = TryGetString(item, "group", out var rgrp) ? rgrp : "";
                string rep = TryGetString(item, "replacement", out var rrep) ? rrep : "";
                bool isRegex = !TryGetBool(item, "isRegex", out bool br) || br;
                bool enabled = !TryGetBool(item, "enabled", out bool ben) || ben;
                bool sTitle = TryGetBool(item, "scopeTitle", out bool bst) && bst;
                bool sContent = !TryGetBool(item, "scopeContent", out bool bsc) || bsc;
                string scope = TryGetString(item, "scope", out var rsc) ? rsc : "";
                string exScope = TryGetString(item, "excludeScope", out var rex) ? rex : "";
                long order = TryGetInt64(item, "order", out long ord) ? ord : idx;
                int timeout = TryGetInt32(item, "timeoutMillisecond", out int tm) && tm > 0 ? tm : 3000;
                list.Add(new NovelRule
                {
                    Id = id,
                    Name = name,
                    Group = grp,
                    Pattern = pattern,
                    Replacement = rep,
                    IsRegex = isRegex,
                    Enabled = enabled,
                    ScopeTitle = sTitle,
                    ScopeContent = sContent,
                    Scope = scope,
                    ExcludeScope = exScope,
                    Order = order,
                    TimeoutMillisecond = timeout
                });
                idx++;
            }
            try { await novel.Library.SaveRulesAsync(list, ct); } catch { }
        }

        if (TryGetArray(block, "sources", out var sourcesEl))
        {
            var sourceItems = new List<ExportedNovelSource>();
            int idx = 0;
            foreach (var item in sourcesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("definition", out var defProp)) continue;
                string defText = defProp.ValueKind == JsonValueKind.String ? defProp.GetString() ?? ""
                    : defProp.ValueKind == JsonValueKind.Object ? defProp.GetRawText() : "";
                if (string.IsNullOrWhiteSpace(defText)) continue;
                TryGetString(item, "installationId", out var id);
                TryGetString(item, "name", out var name);
                TryGetString(item, "sourceUrl", out var url);
                TryGetString(item, "group", out var grp);
                long order = TryGetInt64(item, "order", out long o) ? o : idx;
                bool en = !TryGetBool(item, "enabled", out bool ben) || ben;
                bool exp = !TryGetBool(item, "enabledExplore", out bool bexp) || bexp;
                TryGetString(item, "protocol", out var proto);
                sourceItems.Add(new ExportedNovelSource(id ?? "", 1, name ?? "", url ?? "", grp ?? "", order, en, exp, proto ?? "", defText));
                idx++;
            }
            try { await novel.Sources.ReplaceAllDefinitionsAsync(sourceItems, ct); } catch { }
        }

        if (TryGetArray(block, "cookies", out var cookiesEl))
        {
            try { await novel.Sources.ReplaceCookiesAsync(cookiesEl, ct); } catch { }
        }
    }

    private async Task<JsonArray> ExportMusicNetworkScriptsAsync(CancellationToken ct)
    {
        var arr = new JsonArray();
        try
        {
            var list = await music.Application.Scripts.ListSourcesAsync(ct);
            foreach (var item in list.Items)
            {
                if (item.Origin.Kind != "network") continue;
                string? url = item.Origin.Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    try
                    {
                        var detail = await music.Application.Scripts.GetSourceDetailAsync(item.InstallationId, ct);
                        url = detail.Item.Origin.Url;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(url)) continue;
                arr.Add(new JsonObject
                {
                    ["name"] = item.Name,
                    ["sourceUrl"] = url,
                    ["enabled"] = item.Enabled
                });
            }
        }
        catch { }
        return arr;
    }

    private async Task ReplaceMusicNetworkScriptsAsync(JsonElement scriptsEl, CancellationToken ct)
    {
        try
        {
            var current = await music.Application.Scripts.ListSourcesAsync(ct);
            foreach (var existing in current.Items)
            {
                ct.ThrowIfCancellationRequested();
                try { await music.Application.Scripts.RemoveSourceAsync(existing.InstallationId, ct); } catch { }
            }
        }
        catch { return; }

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scriptsEl.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) continue;
            string url = TryGetString(item, "sourceUrl", out var u) ? u.Trim() : "";
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !seenUrls.Add(url))
                continue;
            string? desiredName = TryGetString(item, "name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : null;
            bool enabled = !TryGetBool(item, "enabled", out bool en) || en;
            try
            {
                var downloaded = await music.Application.Scripts.DownloadSourceAsync(url, ct);
                string id = downloaded.Item.InstallationId;
                if (desiredName is not null && desiredName != downloaded.Item.Name)
                {
                    try { await music.Application.Scripts.EditSourceAsync(id, name: desiredName, includeUrl: false, cancellationToken: ct); } catch { }
                }
                if (enabled != downloaded.Item.Enabled)
                {
                    try { await music.Application.Scripts.SetSourceEnabledAsync(id, enabled: enabled, cancellationToken: ct); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
    }

    private async Task<IReadOnlyDictionary<string, double>> ReadPlaybackVolumesAsync(long musicVolumePct, CancellationToken ct)
    {
        var map = await database.ReadSnapshotAsync(db =>
        {
            var rows = db.Query("SELECT owner, volume FROM playback_volume WHERE owner IN ('music', 'film') ORDER BY owner",
                r => (Owner: r.GetString(0), Volume: Math.Clamp(r.GetDouble(1), 0.0, 1.0)));
            return rows.ToDictionary(x => x.Owner, x => x.Volume, StringComparer.Ordinal);
        }, ct);
        map["music"] = Math.Clamp(musicVolumePct / 100.0, 0.0, 1.0);
        return map;
    }

    private Task SaveOwnerPlaybackVolumeAsync(string owner, double volume, CancellationToken ct) =>
        database.WriteAsync(db =>
        {
            db.Execute("""
                INSERT INTO playback_volume(owner,revision,volume) VALUES($owner,1,$vol)
                ON CONFLICT(owner) DO UPDATE SET revision=playback_volume.revision+1,volume=excluded.volume
                """, ("$owner", owner), ("$vol", Math.Clamp(volume, 0.0, 1.0)));
            return 1L;
        }, ct);

    private static MusicSearchKindRecord ParseSearchKind(JsonElement el, MusicSearchKindRecord fallback)
    {
        string[] order = NormalizeOrderedSubset(
            TryGetStringArray(el, "order", out var o) ? o : fallback.Order,
            MusicFivePlatforms, appendMissing: true);
        string[] enabled = NormalizeOrderedSubset(
            TryGetStringArray(el, "enabled", out var en) ? en : fallback.Enabled,
            order, appendMissing: false);
        long pages = TryGetInt64(el, "pages", out long p) && p is >= 1 and <= int.MaxValue ? p : fallback.Pages;
        long conc = TryGetInt64(el, "concurrency", out long c) && cmBound(c) ? c : fallback.Concurrency;
        long cache = TryGetInt64(el, "cacheMinutes", out long cm) && cm is >= 1 and <= 1440 ? cm : fallback.CacheMinutes;
        return new MusicSearchKindRecord(order, enabled, pages, conc, cache);

        static bool cmBound(long v) => v is >= 1 and <= int.MaxValue;
    }

    private static JsonObject ToSearchKindObject(MusicSearchKindRecord rec) => new()
    {
        ["order"] = ToJsonArray(rec.Order),
        ["enabled"] = ToJsonArray(rec.Enabled),
        ["pages"] = rec.Pages,
        ["concurrency"] = rec.Concurrency,
        ["cacheMinutes"] = rec.CacheMinutes
    };

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (string item in items) arr.Add(item);
        return arr;
    }

    private static JsonArray ToMatchRulesArray(IEnumerable<FilmMatchRule> rules)
    {
        var arr = new JsonArray();
        foreach (var r in rules)
            arr.Add(new JsonObject { ["pattern"] = r.Pattern, ["replace"] = r.Replace, ["flags"] = r.Flags });
        return arr;
    }

    private static bool TryParseMatchRules(JsonElement obj, string prop, out FilmMatchRule[] rules)
    {
        rules = [];
        if (!TryGetArray(obj, prop, out var arr)) return false;
        var list = new List<FilmMatchRule>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "pattern", out var pat) || string.IsNullOrWhiteSpace(pat))
                continue;
            string rep = TryGetString(item, "replace", out var r) ? r : "";
            string flags = TryGetString(item, "flags", out var f) ? f : "";
            list.Add(new FilmMatchRule(pat, rep, flags));
        }
        rules = list.ToArray();
        return true;
    }

    private static string[] NormalizeOrderedSubset(IEnumerable<string> items, string[] allowed, bool appendMissing)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (string raw in items)
        {
            if (allowedSet.Contains(raw) && !list.Contains(raw, StringComparer.Ordinal))
                list.Add(raw);
        }
        if (appendMissing)
        {
            foreach (string a in allowed)
                if (!list.Contains(a, StringComparer.Ordinal))
                    list.Add(a);
        }
        return list.ToArray();
    }

    private static Dictionary<string, string> ReadStringDict(JsonElement obj, string prop)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryGetObject(obj, prop, out var sub)) return dict;
        foreach (var p in sub.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s)
                dict[p.Name] = s;
        return dict;
    }

    private static bool TryGetObject(JsonElement obj, string prop, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement obj, string prop, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out value) && value.ValueKind == JsonValueKind.Array)
            return true;
        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement obj, string prop, out string value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
            el.ValueKind == JsonValueKind.String && el.GetString() is { } s)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryGetBool(JsonElement obj, string prop, out bool value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = el.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryGetInt32(JsonElement obj, string prop, out int value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
            el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetInt64(JsonElement obj, string prop, out long value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
            el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string prop, out double value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var el) &&
            el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value) && double.IsFinite(value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetStringArray(JsonElement obj, string prop, out string[] values)
    {
        if (TryGetArray(obj, prop, out var arr))
        {
            var list = new List<string>();
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    list.Add(s);
            values = list.ToArray();
            return true;
        }
        values = [];
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (!stopping)
        {
            stopping = true;
            try { await lifetime.CancelAsync(); } catch { }
        }
        await gate.WaitAsync();
        gate.Release();
        lifetime.Dispose();
    }
}
