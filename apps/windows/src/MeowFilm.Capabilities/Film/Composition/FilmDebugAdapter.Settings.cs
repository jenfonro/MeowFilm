using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed partial class FilmDebugAdapter
{
    private object BuildSettingsDebugContent()
    {
        var fsv = capability.DebugSettings;
        return new
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
        };
    }

    private async Task<object> TryHandleSettingsTabSelectAsync(JsonElement? parameters)
    {
        var fsv = capability.DebugSettings;
        var id = DebugParams.GetString(parameters, "id");
        var idx = DebugParams.GetInt(parameters, "index");
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

    private async Task<object> TryHandleSettingSetAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        var fsv = capability.DebugSettings;
        var key = (DebugParams.GetString(parameters, "key") ?? "").Trim().ToLowerInvariant();
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

    private async Task<object?> TryHandleSettingActionAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        var fsv = capability.DebugSettings;
        var sources = capability.DebugSources;
        var metadata = capability.DebugMetadata;
        var cmd = (DebugParams.GetString(parameters, "command") ?? "").Trim().ToLowerInvariant();
        if (cmd == "refresh_sources")
        {
            await sources.RefreshAsync(cancellationToken);
            fsv.SyncDebugSources();
            return new
            {
                executed = true,
                domain = "film",
                command = cmd,
                catsCount = sources.Cats.Count,
                videosCount = sources.Videos.Count
            };
        }
        if (cmd == "cat.import")
        {
            var filePath = DebugParams.GetString(parameters, "filePath");
            var sourceType = (DebugParams.GetString(parameters, "sourceType") ??
                (filePath is not null ? "local" : "network")).Trim().ToLowerInvariant();
            var sourceUrl = DebugParams.GetString(parameters, "sourceUrl");
            var name = DebugParams.GetString(parameters, "name") ?? "猫源";
            var installationId = DebugParams.GetString(parameters, "installationId");
            await sources.InitializeAsync(cancellationToken);
            var cat = sourceType == "local"
                ? await sources.ImportLocalAsync(name, filePath ?? "", installationId, cancellationToken)
                : await sources.ImportNetworkAsync(name, sourceUrl ?? "", installationId, cancellationToken);
            fsv.SyncDebugSources();
            return new
            {
                executed = true,
                domain = "film",
                command = cmd,
                installationId = cat.InstallationId,
                name = cat.Name,
                sourceType = cat.SourceType,
                state = cat.State,
                generation = cat.Generation,
                error = cat.Error,
                videoCount = sources.Videos.Count(v => v.Key.InstallationId == cat.InstallationId)
            };
        }
        if (cmd is "cat.delete" or "cat.restart" or "cat.update" or "cat.enable")
        {
            var installationId = DebugParams.GetString(parameters, "installationId")
                ?? throw DebugApiException.InvalidArgument($"'{cmd}' requires 'installationId'.");
            var enabled = DebugParams.GetBool(parameters, "enabled");
            var filePath = DebugParams.GetString(parameters, "filePath");
            await sources.InitializeAsync(cancellationToken);
            if (cmd == "cat.delete")
            {
                await sources.DeleteCatAsync(installationId, cancellationToken);
                fsv.SyncDebugSources();
                return new { executed = true, domain = "film", command = cmd, installationId, deleted = true };
            }
            var cat = cmd switch
            {
                "cat.restart" => await sources.RestartCatAsync(installationId, cancellationToken),
                "cat.update" => await sources.UpdateCatAsync(installationId, filePath, cancellationToken),
                "cat.enable" => await sources.EnableCatAsync(installationId, enabled ?? true, cancellationToken),
                _ => throw DebugApiException.InvalidArgument($"Unsupported cat command '{cmd}'.")
            };
            fsv.SyncDebugSources();
            return new
            {
                executed = true,
                domain = "film",
                command = cmd,
                installationId = cat.InstallationId,
                state = cat.State,
                generation = cat.Generation,
                enabled = cat.Enabled,
                error = cat.Error
            };
        }
        if (cmd == "account.save")
        {
            var platform = (DebugParams.GetString(parameters, "platform") ?? "").Trim().ToLowerInvariant();
            var descriptor = PanSettingsStore.FindProvider(platform)
                ?? throw DebugApiException.InvalidArgument($"Unsupported pan platform '{platform}'.");
            var credentials = new Dictionary<string, object>(StringComparer.Ordinal);
            if (parameters is { ValueKind: JsonValueKind.Object } obj &&
                obj.TryGetProperty("credentials", out var creds) && creds.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in creds.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } s && s.Length > 0)
                        credentials[prop.Name] = s;
                    else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out long n))
                        credentials[prop.Name] = n;
                }
            }
            foreach (var field in descriptor.CredentialFields)
            {
                if (credentials.ContainsKey(field)) continue;
                if (field == "refreshedAt")
                {
                    if (parameters is { ValueKind: JsonValueKind.Object } pObj &&
                        pObj.TryGetProperty(field, out var rProp) &&
                        rProp.ValueKind == JsonValueKind.Number && rProp.TryGetInt64(out long ts))
                    {
                        credentials[field] = ts;
                    }
                }
                else if (DebugParams.GetString(parameters, field) is { Length: > 0 } val)
                {
                    credentials[field] = val;
                }
            }
            var panStore = new PanSettingsStore(sources.Runtime, capability.DebugStorage);
            var saved = await panStore.SaveAsync(descriptor.Provider, credentials, cancellationToken);
            await fsv.DebugPanAccounts.ReloadAsync();
            return new
            {
                executed = true,
                domain = "film",
                command = cmd,
                platform = saved.Provider,
                name = descriptor.Name,
                status = saved.Status,
                configured = saved.Configured,
                revision = saved.Revision
            };
        }
        if (cmd == "metadata.save")
        {
            var provider = (DebugParams.GetString(parameters, "provider") ?? "").Trim().ToLowerInvariant();
            if (provider == "tmdb")
            {
                var current = await metadata.GetSettingsAsync(cancellationToken);
                var patch = FilmSettingsLogic.BuildTmdbPatch(
                    DebugParams.GetString(parameters, "apiBase") ?? (current.ApiBase == FilmMetadataSettingsStore.TmdbDefaults.ApiBase ? "" : current.ApiBase),
                    DebugParams.GetString(parameters, "imageBase") ?? (current.ImageBase == FilmMetadataSettingsStore.TmdbDefaults.ImageBase ? "" : current.ImageBase),
                    DebugParams.GetString(parameters, "apiToken") ?? current.ApiToken,
                    DebugParams.GetString(parameters, "language") ?? current.Language,
                    DebugParams.GetString(parameters, "region") ?? current.Region,
                    DebugParams.GetBool(parameters, "includeAdult") ?? current.IncludeAdult);
                var saved = await metadata.SaveSettingsAsync(patch, cancellationToken);
                fsv.SyncDebugMetadata(saved);
                return new
                {
                    executed = true,
                    domain = "film",
                    command = cmd,
                    provider = "tmdb",
                    apiBase = saved.ApiBase,
                    imageBase = saved.ImageBase,
                    configured = saved.ApiToken.Length > 0
                };
            }
            if (provider == "douban")
            {
                var current = await metadata.GetDoubanSettingsAsync(cancellationToken);
                var dataRouteId = DebugParams.GetString(parameters, "dataRoute") ?? FilmSettingsLogic.ResolveDoubanDataRouteId(current);
                var imageRouteId = DebugParams.GetString(parameters, "imageRoute") ?? FilmSettingsLogic.ResolveDoubanImageRouteId(current);
                var dataRoute = FilmSettingsLogic.DoubanDataRoutes.FirstOrDefault(r => r.Id == dataRouteId) ?? FilmSettingsLogic.DoubanDataRoutes[0];
                var imageRoute = FilmSettingsLogic.DoubanImageRoutes.FirstOrDefault(r => r.Id == imageRouteId) ?? FilmSettingsLogic.DoubanImageRoutes[0];
                var patch = FilmSettingsLogic.BuildDoubanPatch(
                    current,
                    dataRoute,
                    DebugParams.GetString(parameters, "apiProxy") ?? current.ApiProxy,
                    imageRoute,
                    DebugParams.GetString(parameters, "imageProxy") ?? current.ImageProxy,
                    DebugParams.GetString(parameters, "cookie") ?? current.Cookie);
                var saved = await metadata.SaveDoubanSettingsAsync(patch, cancellationToken);
                fsv.SyncDebugDouban(saved);
                return new
                {
                    executed = true,
                    domain = "film",
                    command = cmd,
                    provider = "douban",
                    apiBase = saved.ApiBase,
                    imageBase = saved.ImageBase,
                    hasCookie = saved.Cookie.Length > 0
                };
            }
            throw DebugApiException.InvalidArgument($"Unsupported metadata provider '{provider}'.");
        }
        if (cmd is "account.login" or "account.login.close")
        {
            var platform = DebugParams.GetString(parameters, "platform");
            var close = cmd.EndsWith(".close", StringComparison.Ordinal) || DebugParams.GetBool(parameters, "close") == true;
            var mode = DebugParams.GetString(parameters, "mode");
            return await fsv.DebugPanAccountLoginAsync(platform, close, mode, cancellationToken);
        }
        if (cmd is "account.logout" or "account.logout.close")
        {
            var platform = DebugParams.GetString(parameters, "platform");
            var close = cmd.EndsWith(".close", StringComparison.Ordinal) || DebugParams.GetBool(parameters, "close") == true;
            var confirm = DebugParams.GetBool(parameters, "confirm") == true;
            return await fsv.DebugPanAccountLogoutAsync(platform, close, confirm, cancellationToken);
        }
        throw DebugApiException.InvalidArgument($"Unsupported film settings command '{cmd}'.");
    }
}
