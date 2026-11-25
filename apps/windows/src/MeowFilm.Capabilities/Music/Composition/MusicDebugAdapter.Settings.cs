using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

internal sealed partial class MusicDebugAdapter
{
    private object BuildSettingsContent()
    {
        var settingsController = capability.Settings;
        var accounts = capability.Accounts;
        var settings = new
        {
            domain = "music",
            section = capability.Presentation.Settings.CurrentSectionKey,
            tabs = capability.Presentation.Settings.DescribeDebugTabs(),
            playbackQualityRequest = settingsController.PlaybackQualityRequest,
            playbackQualityOptions = settingsController.PlaybackQualityOptions,
            accounts = accounts.Accounts.Select(a => new
            {
                platform = a.Platform,
                name = a.Name,
                state = a.State,
                stateText = a.StateText,
                displayName = a.DisplayName,
                userId = a.UserId,
                hasSession = a.HasSession
            }).ToArray(),
            sources = settingsController.Sources.Select((s, idx) => new
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
        return new
        {
            target = "settings.music",
            kind = "settings",
            loading = false,
            error = string.IsNullOrWhiteSpace(capability.RawStatus) ? null : capability.RawStatus,
            tabs = capability.Presentation.Settings.DescribeDebugTabs(),
            filters = Array.Empty<object>(),
            sections = Array.Empty<object>(),
            rows = Array.Empty<object>(),
            hasMore = false,
            detail = (object?)null,
            player = (object?)null,
            reader = (object?)null,
            settings
        };
    }

    private object HandleSettingsSectionSelect(string? id, int? index)
    {
        string targetKey = id ?? (index is { } idx && idx >= 0 && idx < 5
            ? new[] { "accounts", "search", "quality", "metadata", "sources" }[idx]
            : throw DebugApiException.InvalidArgument("Provide 'id' or valid 'index' for settings-section."));
        if (!capability.Presentation.Settings.SelectDebugSection(targetKey))
            throw DebugApiException.NotFound($"Music settings section not found: '{targetKey}'.");
        return new { group = "settings-section", selected = targetKey };
    }

    private async Task<object> HandleSettingSetAsync(JsonElement? parameters)
    {
        var key = (GetStr(parameters, "key") ?? "").Trim().ToLowerInvariant();
        if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
            throw DebugApiException.InvalidArgument("Missing 'value'.");
        if (key is "playbackquality" or "quality" && val.ValueKind == JsonValueKind.String)
        {
            var requested = val.GetString()!;
            if (!capability.Settings.PlaybackQualityOptions.Contains(requested))
                throw DebugApiException.InvalidArgument($"Unsupported playbackQuality '{requested}'.");
            await capability.Settings.ActivateQualitySettingsAsync();
            await capability.Settings.SetPlaybackQualityRequestAsync(requested);
            return new { saved = capability.Settings.PlaybackQualityRequest == requested, key, value = capability.Settings.PlaybackQualityRequest };
        }
        if (key == "volume" && val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var vol))
        {
            capability.Playback.Volume = vol;
            await capability.Settings.SaveVolumeAsync();
            return new { saved = true, key, value = capability.Playback.Volume };
        }
        throw DebugApiException.InvalidArgument($"Unsupported music setting key '{key}'.");
    }

    private async Task<object> HandleSettingActionAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        var cmd = (GetStr(parameters, "command") ?? "").Trim().ToLowerInvariant();
        if (cmd == "clear_search_history")
        {
            await capability.Settings.ClearSearchHistoryAsync();
            return new { executed = true, command = cmd };
        }
        if (cmd == "clear_cache")
        {
            await capability.ClearAllMusicCachesAsync(cancellationToken);
            var stats = await capability.GetCacheStatsAsync(cancellationToken);
            return new { executed = true, command = cmd, stats };
        }
        if (cmd is "account.login" or "account.login.close")
        {
            var platform = GetStr(parameters, "platform") ?? GetStr(parameters, "provider");
            var close = cmd.EndsWith(".close", StringComparison.Ordinal) || GetBool(parameters, "close") == true;
            var mode = GetStr(parameters, "mode");
            return await capability.Presentation.Settings.DebugAccountLoginAsync(platform, close, mode, cancellationToken);
        }
        if (cmd is "account.logout" or "account.logout.close")
        {
            var platform = GetStr(parameters, "platform") ?? GetStr(parameters, "provider");
            var close = cmd.EndsWith(".close", StringComparison.Ordinal) || GetBool(parameters, "close") == true;
            var confirm = GetBool(parameters, "confirm") == true;
            return await capability.Presentation.Settings.DebugAccountLogoutAsync(platform, close, confirm, cancellationToken);
        }
        throw DebugApiException.InvalidArgument($"Unsupported music settings command '{cmd}'.");
    }
}
