using System.Diagnostics;
using System.Text.Json;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

internal sealed partial class ShellDebugAdapter
{
    private sealed record RouteState(
        string Target,
        string? ModuleId,
        string? Route,
        bool SettingsOpen,
        string? SettingsDomain,
        string? SettingsSection);

    private sealed record UiImagesInspection(int Total, int LoadingCount, int LoadedCount, int FailedCount, object Payload);

    public Task<object> GetUiStateAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(BuildUiStateOnUi, cancellationToken);

    private RouteState ResolveRouteStateOnUi()
    {
        var current = shell.CurrentSession;
        var currentPage = shell.CurrentPage;
        bool settingsOpen = currentPage is null && current is { IsSettingsOpen: true };
        string? settingsDomain = null;
        string? settingsSection = null;
        string? moduleId = currentPage is not null ? currentPage.Page.Id : current?.Module.Id;
        string? route = currentPage is not null ? currentPage.Page.Id : current?.Current?.Value ?? current?.Module.CaptureNavigationLocation();
        string target;

        if (shell.IsQueueOpen)
        {
            target = "music.queue";
        }
        else if (shell.IsPlayerDetailOpen)
        {
            target = "music.player";
        }
        else if (currentPage is { Page: var page })
        {
            target = page is IDesktopSearchPage ? "search" : page.Id;
        }
        else if (settingsOpen)
        {
            if (shell.IsGeneralSettingsOpen)
            {
                settingsDomain = "general";
                settingsSection = (shell.GeneralSettingsHost.Content as GeneralSettingsView)?.CurrentArea ?? "appearance";
                target = "settings.general";
            }
            else
            {
                settingsDomain = current!.Module.Id;
                target = "settings." + current.Module.Id;
                if (AsAdapter(current.Module) is { } adapter)
                    adapter.DescribeCurrentTarget(settingsOpen: true, out settingsSection);
            }
        }
        else if (AsAdapter(current?.Module) is { } activeAdapter &&
                 activeAdapter.DescribeCurrentTarget(settingsOpen: false, out _) is { Length: > 0 } described)
        {
            target = described;
        }
        else
        {
            target = route ?? moduleId ?? "";
        }

        return new RouteState(target, moduleId, route, settingsOpen, settingsDomain, settingsSection);
    }

    private object BuildUiStateOnUi()
    {
        var resolved = ResolveRouteStateOnUi();
        var searchPageId = shell.SearchCoordinator.SearchPage?.Id;

        var stack = shell.History.Entries.Select(entry => new
        {
            target = entry.Destination.PageId is { Length: > 0 } pid
                ? (searchPageId is not null && string.Equals(pid, searchPageId, StringComparison.Ordinal) ? "search" : pid)
                : entry.Destination.Value,
            route = entry.Destination.Location ?? entry.Destination.Value,
            title = entry.Session.Module.Title
        }).ToArray();

        object? activeDialog = BuildActiveDialogStateOnUi();
        var notices = Array.Empty<object>();
        var size = shell.AppWindow.Size;
        double scale = shell.Root.XamlRoot?.RasterizationScale ?? 1.0;
        var regions = shell.NativeCaption?.InspectRegions() ?? ([], []);
        var nonClient = shell.NativeCaption?.InspectDebugState();
        var activeDropdown = DropdownPlacement.DescribeActiveDropdown(shell.Root);

        var header = new
        {
            visible = shell.HeaderChromeElement.Visibility == Visibility.Visible,
            searchVisible = shell.SearchInputElement.Visibility == Visibility.Visible,
            searchEnabled = shell.SearchBoxElement.IsEnabled,
            searchText = shell.SearchBoxElement.Text,
            searchScopeVisible = DesktopVisual.IsVisible(shell.SearchTargetPickerElement),
            searchScopeEnabled = shell.SearchTargetPickerElement.IsEnabled,
            searchScopeOpen = shell.SearchTargetPickerElement.IsDropDownOpen,
            searchScopeValue = shell.SearchTargetPickerElement.SelectedValue,
            searchScopeLabel = shell.SearchTargetPickerElement.SelectedItem?.Label,
            searchScopes = shell.SearchTargetPickerElement.ItemsSource.Select((item, index) => new
            {
                index,
                id = item.Id,
                label = item.Label,
                selected = string.Equals(item.Id, shell.SearchTargetPickerElement.SelectedValue, StringComparison.Ordinal)
            }).ToArray(),
            captionButtonsTheme = shell.WindowCaptionButtonsElement.RequestedTheme.ToString(),
            windowState = shell.WindowState.ToString().ToLowerInvariant()
        };

        return new
        {
            target = resolved.Target,
            moduleId = resolved.ModuleId,
            route = resolved.Route,
            title = shell.Title,
            settingsOpen = resolved.SettingsOpen,
            settingsDomain = resolved.SettingsDomain,
            settingsSection = resolved.SettingsSection,
            sidebarVisible = shell.SidebarBackgroundElement.Visibility == Visibility.Visible,
            playerBarVisible = shell.PlayerHostElement.Visibility == Visibility.Visible,
            playerDetailOpen = shell.IsPlayerDetailOpen,
            queueOpen = shell.IsQueueOpen,
            navigationStack = stack,
            canGoBack = shell.State.CanGoBack,
            canGoForward = shell.State.CanGoForward,
            isBusy = shell.IsRequestBusy,
            statusText = shell.State.Status,
            isError = shell.State.IsError,
            errorText = shell.State.IsError ? shell.State.Status : null,
            header,
            activeDropdown,
            activeDialog,
            notices,
            nonClient,
            caption = new
            {
                titleBarHeight = shell.NativeCaption?.CaptionHeightPx ?? 0,
                captionRects = regions.Caption.Select(r => new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray(),
                passthroughRects = regions.Passthrough.Select(r => new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray()
            },
            window = new
            {
                visible = shell.IsVisible,
                active = shell.IsActive,
                state = shell.WindowState.ToString().ToLowerInvariant(),
                width = size.Width,
                height = size.Height,
                scale
            }
        };
    }

    public Task<object> GetUiContentAsync(int maxItems, bool includeRaw, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            if (ResolveForegroundOverlayAdapter(out var overlayTarget) is { } overlayAdapter)
            {
                var proj = await overlayAdapter.BuildDebugOverlayContentAsync(overlayTarget, maxItems, includeRaw, cancellationToken)
                    ?? await overlayAdapter.BuildDebugContentAsync(false, maxItems, includeRaw, cancellationToken);
                if (proj is not null) return proj;
            }
            if (shell.CurrentPage is { } currentPage && AsAdapter(currentPage.Page) is { } pageAdapter)
            {
                var content = await pageAdapter.BuildDebugContentAsync(false, maxItems, includeRaw, cancellationToken);
                if (content is not null) return content;
            }
            if (shell.CurrentSession is { IsSettingsOpen: true } && shell.IsGeneralSettingsOpen &&
                shell.GeneralSettingsHost.Content is GeneralSettingsView general)
            {
                return new
                {
                    target = "settings.general",
                    kind = "settings",
                    loading = false,
                    error = (string?)null,
                    tabs = general.DescribeDebugTabs(),
                    filters = Array.Empty<object>(),
                    sections = Array.Empty<object>(),
                    rows = Array.Empty<object>(),
                    hasMore = false,
                    detail = (object?)null,
                    player = (object?)null,
                    reader = (object?)null,
                    settings = general.BuildDebugSnapshot()
                };
            }
            if (shell.CurrentSession is { } current && AsAdapter(current.Module) is { } adapter)
            {
                var proj = await adapter.BuildDebugContentAsync(current.IsSettingsOpen, maxItems, includeRaw, cancellationToken);
                if (proj is not null) return proj;
            }
            return new
            {
                target = shell.CurrentSession?.Module.Id ?? "",
                kind = "empty",
                loading = shell.State.IsBusy,
                error = shell.State.IsError ? shell.State.Status : null,
                tabs = Array.Empty<object>(),
                filters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                hasMore = false,
                detail = (object?)null,
                player = (object?)null,
                reader = (object?)null,
                settings = (object?)null
            };
        }, cancellationToken);

    public Task<object> GetUiTreeAsync(int maxDepth, bool visibleOnly, CancellationToken cancellationToken) =>
        OnUiAsync<object>(() => WalkVisualTree(shell.Root, 0, maxDepth, visibleOnly) ?? new { }, cancellationToken);

    public Task<object> GetUiImagesAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(() => InspectUiImagesOnUi().Payload, cancellationToken);

    private UiImagesInspection InspectUiImagesOnUi()
    {
        var byState = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["idle"] = 0,
            ["loading"] = 0,
            ["loaded"] = 0,
            ["failed"] = 0
        };
        var items = new List<object>();
        var zeroSizeAnomalies = new List<object>();
        var failedItems = new List<object>();
        int loadingCount = 0;
        int loadedCount = 0;
        int failedCount = 0;

        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is FrameworkElement fe && fe.Opacity <= 0.001) return;
            if (node is IDebugImageVisual imageVisual && node is FrameworkElement visualElement &&
                visualElement.IsLoaded && DesktopVisual.IsVisible(visualElement))
            {
                var snap = imageVisual.DescribeDebugImage();
                string state = string.IsNullOrWhiteSpace(snap.State) ? "idle" : snap.State;
                byState[state] = byState.GetValueOrDefault(state) + 1;

                Rect bounds = default;
                try
                {
                    var pt = visualElement.TransformToVisual(shell.Root).TransformPoint(new Point(0, 0));
                    bounds = new Rect(pt.X, pt.Y, visualElement.ActualWidth, visualElement.ActualHeight);
                }
                catch { }

                var entry = new
                {
                    state,
                    address = snap.Address,
                    slot = snap.Slot,
                    admitted = snap.Admitted,
                    hasSource = snap.HasSource,
                    pixelWidth = snap.PixelWidth,
                    pixelHeight = snap.PixelHeight,
                    canRetry = snap.CanRetry,
                    errorCode = snap.ErrorCode,
                    name = NullIfEmpty(visualElement.Name),
                    automationName = NullIfEmpty(AutomationProperties.GetName(visualElement)),
                    bounds = new
                    {
                        x = Math.Round(bounds.X, 1),
                        y = Math.Round(bounds.Y, 1),
                        w = Math.Round(bounds.Width, 1),
                        h = Math.Round(bounds.Height, 1)
                    }
                };
                items.Add(entry);

                if (string.Equals(state, "loading", StringComparison.OrdinalIgnoreCase))
                    loadingCount++;
                else if (string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase))
                    loadedCount++;
                else if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    failedCount++;
                    failedItems.Add(entry);
                }

                if ((string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase) || snap.HasSource) &&
                    (snap.PixelWidth <= 0 || snap.PixelHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0))
                {
                    zeroSizeAnomalies.Add(entry);
                }
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }

        Walk(shell.Root);
        var payload = new
        {
            total = items.Count,
            settled = loadingCount == 0,
            byState,
            loadingCount,
            loadedCount,
            failedCount,
            zeroSizeAnomalyCount = zeroSizeAnomalies.Count,
            zeroSizeAnomalies = zeroSizeAnomalies.ToArray(),
            failedItems = failedItems.ToArray(),
            items = items.ToArray()
        };
        return new UiImagesInspection(items.Count, loadingCount, loadedCount, failedCount, payload);
    }

    private object? WalkVisualTree(DependencyObject element, int depth, int maxDepth, bool visibleOnly)
    {
        if (element is FrameworkElement fe)
        {
            bool visible = fe.Visibility == Visibility.Visible && fe.Opacity > 0.001;
            if (visibleOnly && !visible) return null;
            Rect bounds = default;
            try
            {
                var pt = fe.TransformToVisual(shell.Root).TransformPoint(new Point(0, 0));
                bounds = new Rect(pt.X, pt.Y, fe.ActualWidth, fe.ActualHeight);
            }
            catch { }
            string? text = element switch
            {
                TextBlock tb => tb.Text,
                TextBox box => box.Text,
                ContentControl cc when cc.Content is string s => s,
                _ => null
            };
            var children = new List<object>();
            if (depth < maxDepth)
            {
                int count = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < count; i++)
                {
                    var child = WalkVisualTree(VisualTreeHelper.GetChild(element, i), depth + 1, maxDepth, visibleOnly);
                    if (child is not null) children.Add(child);
                }
            }
            return new
            {
                type = element.GetType().Name,
                name = string.IsNullOrEmpty(fe.Name) ? null : fe.Name,
                automationId = NullIfEmpty(AutomationProperties.GetAutomationId(fe)),
                automationName = NullIfEmpty(AutomationProperties.GetName(fe)),
                visible,
                enabled = element is Control ctrl ? ctrl.IsEnabled : true,
                bounds = new
                {
                    x = Math.Round(bounds.X, 1),
                    y = Math.Round(bounds.Y, 1),
                    w = Math.Round(bounds.Width, 1),
                    h = Math.Round(bounds.Height, 1)
                },
                text = NullIfEmpty(text),
                children
            };
        }
        return null;
    }

    public Task<byte[]> CaptureScreenshotPngAsync(CancellationToken cancellationToken) =>
        CaptureScreenshotPngAsync(window: null, cancellationToken);

    public Task<byte[]> CaptureScreenshotPngAsync(string? window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window) || string.Equals(window.Trim(), "main", StringComparison.OrdinalIgnoreCase))
            return CaptureMainWindowScreenshotPngAsync(cancellationToken);

        var targetWindow = window.Trim();
        return OnUiAsync(async () =>
        {
            foreach (var adapter in AllAdapters())
            {
                var png = await adapter.CaptureDebugAuxiliaryWindowPngAsync(targetWindow, cancellationToken);
                if (png is { Length: > 0 }) return png;
            }
            throw DebugApiException.NotFound($"Auxiliary window '{targetWindow}' not found or not open.");
        }, cancellationToken);
    }

    private Task<byte[]> CaptureMainWindowScreenshotPngAsync(CancellationToken cancellationToken) =>
        OnUiAsync(async () =>
        {
            if (DropdownPlacement.DescribeActiveDropdown(shell.Root) is not null ||
                string.Equals(ResolveRouteStateOnUi().Target, "film.playback", StringComparison.OrdinalIgnoreCase))
                return await DesktopWindowCapture.CaptureWindowHandlePngAsync(shell.Handle, cancellationToken);

            return await DesktopWindowCapture.TryRenderElementPngAsync(shell.Root, requireVisiblePixels: false, cancellationToken)
                ?? await DesktopWindowCapture.CaptureWindowHandlePngAsync(shell.Handle, cancellationToken);
        }, cancellationToken);

    public Task<object> WaitAsync(string condition, JsonElement? parameters, int timeoutMs, CancellationToken cancellationToken) =>
        OnUiAsync<object>(() => WaitConditionOnUiAsync(condition, parameters, timeoutMs, cancellationToken), cancellationToken);

    private async Task<object> WaitConditionOnUiAsync(string condition, JsonElement? parameters, int timeoutMs, CancellationToken cancellationToken)
    {
        var normalized = condition.Trim().ToLowerInvariant();
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool matched = false;
            object? snapshot = null;
            switch (normalized)
            {
                case "idle":
                {
                    if (!shell.IsRequestBusy) { matched = true; snapshot = BuildUiStateOnUi(); }
                    break;
                }
                case "images_settled":
                {
                    int minLoaded = DebugParams.GetInt(parameters, "minLoaded") ?? 0;
                    bool allowFailed = DebugParams.GetBool(parameters, "allowFailed") ?? true;
                    var inspection = InspectUiImagesOnUi();
                    if (inspection.LoadingCount == 0 &&
                        inspection.LoadedCount >= minLoaded &&
                        (allowFailed || inspection.FailedCount == 0))
                    {
                        matched = true;
                        snapshot = inspection.Payload;
                    }
                    break;
                }
                case "route":
                {
                    var wantedTarget = DebugParams.GetString(parameters, "target");
                    var wantedRoute = DebugParams.GetString(parameters, "route");
                    if (wantedTarget is null && wantedRoute is null)
                        throw DebugApiException.InvalidArgument("Wait condition 'route' requires 'target' or 'route' in params.");
                    var resolved = ResolveRouteStateOnUi();
                    bool targetMatches = wantedTarget is not null && (
                        string.Equals(resolved.Target, wantedTarget, StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(wantedTarget, "settings.cache", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(resolved.SettingsDomain, "general", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(resolved.SettingsSection, "cache", StringComparison.OrdinalIgnoreCase)) ||
                        (string.Equals(wantedTarget, "settings.media-controls", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(resolved.SettingsDomain, "general", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(resolved.SettingsSection, "shortcuts", StringComparison.OrdinalIgnoreCase)));
                    if (targetMatches ||
                        (wantedRoute is not null && string.Equals(resolved.Route, wantedRoute, StringComparison.OrdinalIgnoreCase)))
                    {
                        matched = true;
                        snapshot = BuildUiStateOnUi();
                    }
                    break;
                }
                case "section_count":
                {
                    int minSections = DebugParams.GetInt(parameters, "minSections") ?? 1;
                    int? sectionIndex = DebugParams.GetInt(parameters, "sectionIndex");
                    int minItems = DebugParams.GetInt(parameters, "minItems") ?? 0;
                    var content = await GetUiContentAsync(Math.Max(200, minItems), false, cancellationToken);
                    var json = JsonSerializer.SerializeToElement(content);
                    if (json.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array && secs.GetArrayLength() >= minSections)
                    {
                        if (sectionIndex is { } si)
                        {
                            if (si >= 0 && si < secs.GetArrayLength() &&
                                secs[si].TryGetProperty("items", out var items) &&
                                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() >= minItems)
                            { matched = true; snapshot = new { sections = secs.GetArrayLength(), itemCount = items.GetArrayLength() }; }
                        }
                        else { matched = true; snapshot = new { sections = secs.GetArrayLength() }; }
                    }
                    break;
                }
                case "row_count":
                {
                    int minRows = DebugParams.GetInt(parameters, "minRows") ?? 1;
                    var content = await GetUiContentAsync(Math.Max(200, minRows), false, cancellationToken);
                    var json = JsonSerializer.SerializeToElement(content);
                    if (json.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() >= minRows)
                    { matched = true; snapshot = new { rows = rows.GetArrayLength() }; }
                    break;
                }
                case "playback_state":
                {
                    var domain = DebugParams.GetString(parameters, "domain") ?? "music";
                    if (!string.Equals(domain, "music", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(domain, "film", StringComparison.OrdinalIgnoreCase))
                        throw DebugApiException.InvalidArgument($"Unsupported playback_state domain: '{domain}' (expected 'music' or 'film').");
                    var wantedState = DebugParams.GetString(parameters, "state") ?? "playing";
                    var pb = await GetPlaybackSnapshotOnUiAsync(cancellationToken);
                    snapshot = pb.Payload;
                    JsonElement? domObj = string.Equals(domain, "music", StringComparison.OrdinalIgnoreCase) ? pb.Music : pb.Film;
                    if (domObj is { ValueKind: JsonValueKind.Object } obj &&
                        obj.TryGetProperty("state", out var st) && string.Equals(st.GetString(), wantedState, StringComparison.OrdinalIgnoreCase))
                    { matched = true; }
                    break;
                }
                case "dialog":
                {
                    bool wantOpen = DebugParams.GetBool(parameters, "open") ?? true;
                    bool isOpen = shell.IsAnyModalOpen;
                    if (isOpen == wantOpen) { matched = true; snapshot = BuildUiStateOnUi(); }
                    break;
                }
                default:
                    throw DebugApiException.InvalidArgument($"Unsupported wait condition: '{condition}'.");
            }
            if (matched) return new { matched = true, condition = normalized, snapshot };
            if (Stopwatch.GetTimestamp() >= deadline)
                throw DebugApiException.Timeout($"Timed out waiting for condition '{normalized}' after {timeoutMs}ms.",
                    snapshot is not null ? new { lastSnapshot = snapshot } : null);
            await Task.Delay(100, cancellationToken);
        }
    }
}
