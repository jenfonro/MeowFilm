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
        var cmd = (DebugParams.GetString(parameters, "command") ?? "").Trim().ToLowerInvariant();
        if (cmd is "refresh_sources" or "reload_sources")
        {
            await fsv.RefreshDebugSourcesAsync(cancellationToken);
            return new { executed = true, domain = "film", command = cmd };
        }
        if (cmd is "account.login" or "account.login.close")
        {
            var platform = DebugParams.GetString(parameters, "platform") ?? DebugParams.GetString(parameters, "provider");
            var close = cmd.EndsWith(".close", StringComparison.Ordinal) || DebugParams.GetBool(parameters, "close") == true;
            var mode = DebugParams.GetString(parameters, "mode");
            return await fsv.DebugPanAccountLoginAsync(platform, close, mode, cancellationToken);
        }
        if (cmd is "account.logout" or "account.logout.close")
        {
            var platform = DebugParams.GetString(parameters, "platform") ?? DebugParams.GetString(parameters, "provider");
            var close = cmd.EndsWith(".close", StringComparison.Ordinal) || DebugParams.GetBool(parameters, "close") == true;
            var confirm = DebugParams.GetBool(parameters, "confirm") == true;
            return await fsv.DebugPanAccountLogoutAsync(platform, close, confirm, cancellationToken);
        }
        throw DebugApiException.InvalidArgument($"Unsupported film settings command '{cmd}'.");
    }
}
