using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Standalone debug control adapter for the music capability.</summary>
internal sealed partial class MusicDebugAdapter(MusicCapability capability) : IDebugModuleAdapter
{
    private static readonly IReadOnlyList<string> MusicTargets =
    [
        "music.home", "music.channels", "music.discovery", "music.personal", "music.daily", "music.fm",
        "music.library", "music.favorites", "music.history", "music.archives", "music.playlist",
        "music.player", "music.player.detail", "music.queue", "settings.music"
    ];

    public string DebugModuleId => capability.Id;
    public IReadOnlyList<string> SupportedDebugTargets => MusicTargets;

    public string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection)
    {
        if (settingsOpen)
        {
            settingsSection = capability.Presentation.Settings.CurrentSectionKey;
            return "settings.music";
        }
        settingsSection = null;
        if (capability.Search.IsSearchPage) return "search";
        var route = capability.CurrentRoute;
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
                await capability.NavigateAsync("catalog-home", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.channels":
            {
                var key = GetStr(parameters, "channelKey");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var route = key.StartsWith("catalog-channel/", StringComparison.Ordinal) ? key : "catalog-channel/" + key;
                    await capability.NavigateAsync(route, cancellationToken);
                    return true;
                }
                await capability.NavigateAsync("catalog-channels", cancellationToken);
                if (GetInt(parameters, "channelIndex") is { } idx)
                {
                    if (idx < 0 || idx >= capability.Catalog.ChannelTabs.Count)
                        throw DebugApiException.InvalidArgument($"channelIndex {idx} out of range (0..{Math.Max(0, capability.Catalog.ChannelTabs.Count - 1)}).");
                    await capability.Catalog.OpenCatalogCardAsync(capability.Catalog.ChannelTabs[idx]);
                }
                return true;
            }
            case "music.discovery":
                await capability.NavigateAsync("catalog-discovery", cancellationToken);
                return true;
            case "music.personal":
                await capability.NavigateAsync("catalog-personal", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.daily":
                await capability.NavigateAsync("catalog-daily", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.fm":
                await capability.NavigateAsync("catalog-fm", cancellationToken);
                await ApplyCatalogPlatformParamAsync(parameters);
                return true;
            case "music.library":
            case "music.favorites":
                await capability.NavigateAsync("favorites", cancellationToken);
                return true;
            case "music.history":
                await capability.NavigateAsync("history", cancellationToken);
                if (GetStr(parameters, "date") is { Length: > 0 } date)
                    await capability.Library.SelectHistoryDateAsync(date);
                return true;
            case "music.archives":
                await capability.NavigateAsync("archives", cancellationToken);
                return true;
            case "music.playlist":
            {
                var id = GetStr(parameters, "id");
                var platform = GetStr(parameters, "platform");
                var remoteId = GetStr(parameters, "remoteId");
                if (!string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(remoteId))
                {
                    await capability.NavigateAsync("catalog-playlist/" + platform + "/" + Uri.EscapeDataString(remoteId), cancellationToken);
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    await capability.Library.OpenPlaylistAsync(id);
                    return true;
                }
                var first = capability.Library.Playlists.FirstOrDefault() ?? capability.Library.NetworkPlaylists.FirstOrDefault();
                if (first is null) throw DebugApiException.InvalidState("No playlist available to open.");
                await capability.NavigateAsync(first.Route, cancellationToken);
                return true;
            }
            default:
                return false;
        }
    }

    public Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken) =>
        Task.FromResult(capability.Presentation.Settings.SelectDebugSection(section));

    public Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken)
    {
        if (settingsOpen) return Task.FromResult<object?>(BuildSettingsContent());
        return Task.FromResult<object?>(BuildCatalogOrLibraryContent(maxItems, includeRaw));
    }

    public async Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "setting.set":
                return await HandleSettingSetAsync(parameters);
            case "setting.action":
                return await HandleSettingActionAsync(parameters, cancellationToken);
            default:
                return await TryHandleCatalogOrLibraryActionAsync(action, parameters, settingsOpen);
        }
    }

    public Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);

    private static string? GetStr(JsonElement? parameters, string name) => DebugParams.GetString(parameters, name);
    private static int? GetInt(JsonElement? parameters, string name) => DebugParams.GetInt(parameters, name);
    private static double? GetDouble(JsonElement? parameters, string name) => DebugParams.GetDouble(parameters, name);
    private static bool? GetBool(JsonElement? parameters, string name) => DebugParams.GetBool(parameters, name);
}
