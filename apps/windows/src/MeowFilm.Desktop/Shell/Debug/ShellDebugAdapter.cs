using System.Diagnostics;
using System.Text.Json;
using MeowFilm.Core.Debug;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

internal sealed class ShellDebugAdapter(ShellWindow shell) : IDebugShellHost
{
    private static readonly IReadOnlyList<string> ShellTargets =
    [
        "settings.general", "settings.cache", "settings.media-controls"
    ];

    private static readonly IReadOnlyList<string> BaseActions =
    [
        "section.item.invoke", "section.more", "tab.select", "list.item.invoke",
        "list.load_more", "search.submit", "dialog.action", "setting.set", "setting.action",
        "window.close_request", "search.scope.open", "search.scope.select",
        "dropdown.open", "dropdown.select", "dropdown.close", "nonclient.refresh"
    ];

    public bool IsWindowReady => shell.IsLoaded && !shell.IsClosing && !shell.IsClosed;
    public IReadOnlyList<string> SupportedTargets
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            void Add(string value) { if (seen.Add(value)) list.Add(value); }
            foreach (var adapter in AllAdapters())
                foreach (var target in adapter.SupportedDebugTargets) Add(target);
            foreach (var target in ShellTargets) Add(target);
            return list;
        }
    }
    public IReadOnlyList<string> SupportedActions => BaseActions;

    private Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shell.Dispatcher.HasThreadAccess) return action();
        return shell.Dispatcher.EnqueueAsync(action, DispatcherQueuePriority.Normal, cancellationToken);
    }

    private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shell.Dispatcher.HasThreadAccess) return Task.FromResult(action());
        return shell.Dispatcher.EnqueueAsync(action, DispatcherQueuePriority.Normal, cancellationToken);
    }

    private static IDebugModuleAdapter? AsAdapter(object? candidate) =>
        candidate is IDebugModuleAdapterSource source ? source.DebugAdapter : candidate as IDebugModuleAdapter;

    private IEnumerable<IDebugModuleAdapter> AllAdapters()
    {
        foreach (var session in shell.Sessions)
            if (AsAdapter(session.Module) is { } adapter) yield return adapter;
        foreach (var page in shell.Pages)
            if (AsAdapter(page.Page) is { } adapter) yield return adapter;
    }

    private IDebugModuleAdapter? FindAdapter(string moduleId) =>
        AllAdapters().FirstOrDefault(a => string.Equals(a.DebugModuleId, moduleId, StringComparison.OrdinalIgnoreCase));

    private ModuleSession? FindSession(string moduleId) =>
        shell.Sessions.FirstOrDefault(s => string.Equals(s.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase));

    private IDebugModuleAdapter? ResolveForegroundOverlayAdapter(out string overlayTarget)
    {
        if (shell.IsQueueOpen)
        {
            overlayTarget = "music.queue";
            return AsAdapter(shell.QueueSession?.Module) ?? FindAdapter("music");
        }
        if (shell.IsPlayerDetailOpen)
        {
            overlayTarget = "music.player";
            return AsAdapter(shell.PlayerDetailSession?.Module) ?? FindAdapter("music");
        }
        overlayTarget = "";
        return null;
    }

    public Task<object> ProbeUiThreadAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.GetTimestamp();
        return OnUiAsync<object>(() =>
        {
            var latencyMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            return new
            {
                responsive = true,
                dispatchLatencyMs = Math.Round(latencyMs, 3),
                managedThreadId = Environment.CurrentManagedThreadId,
                loaded = shell.IsLoaded,
                isClosing = shell.IsClosing,
                isBusy = shell.IsRequestBusy,
                stack = Environment.StackTrace
            };
        }, cancellationToken);
    }

    public Task<object> GetUiStateAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(BuildUiStateOnUi, cancellationToken);

    private sealed record RouteState(
        string Target,
        string? ModuleId,
        string? Route,
        bool SettingsOpen,
        string? SettingsDomain,
        string? SettingsSection);

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
        var notices = shell.OwnedNotices.Snapshot();
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

    private object? BuildActiveDialogStateOnUi()
    {
        if (shell.ResetDialogElement.IsOpen)
        {
            bool confirming = shell.ResetDialogElement.IsConfirming;
            bool enabled = !shell.ResetDialogElement.IsPending;
            return DescribeDialogSurface(
                id: "user-data-reset",
                kind: "user-data-reset",
                fallbackTitle: confirming ? "清除全部本机数据" : "清除未完成",
                message: confirming ? "清空本机数据确认框" : "清空本机数据失败提示框",
                surface: shell.ResetDialogElement.SurfaceElement,
                explicitButtons: confirming
                    ? [
                        new { index = 0, label = "取消", automationName = "取消", enabled, kind = "cancel" },
                        new { index = 1, label = "确认清除", automationName = "清除全部数据", enabled, kind = "confirm" }
                    ]
                    : [
                        new { index = 0, label = "退出", automationName = "退出", enabled, kind = "exit" },
                        new { index = 1, label = "重试清除", automationName = "重试清除", enabled, kind = "confirm" }
                    ],
                stackDepth: 1);
        }
        if (shell.CloseDialogElement.IsOpen)
        {
            return DescribeDialogSurface(
                id: "close-window",
                kind: "close-window",
                fallbackTitle: "关闭主窗口",
                message: "关闭行为选择框",
                surface: shell.CloseDialogElement.SurfaceElement,
                explicitButtons:
                [
                    new { index = 0, label = "取消", automationName = "取消", enabled = !shell.CloseDialogElement.IsPending, kind = "cancel" },
                    new { index = 1, label = "最小化到托盘", automationName = "最小化到托盘", enabled = !shell.CloseDialogElement.IsPending, kind = "tray" },
                    new { index = 2, label = "退出应用", automationName = "退出", enabled = !shell.CloseDialogElement.IsPending, kind = "exit" }
                ],
                stackDepth: 1);
        }
        if (shell.ChannelDialogHostElement.IsOpen && shell.ChannelDialogHostElement.ActiveContent is { } activePanel)
        {
            return DescribeDialogSurface(
                id: "channel-dialog",
                kind: "channel-dialog",
                fallbackTitle: NullIfEmpty(AutomationProperties.GetName(activePanel)) ?? "弹出层",
                message: "",
                surface: activePanel,
                explicitButtons: null,
                stackDepth: shell.ChannelDialogHostElement.StackDepth);
        }
        return null;
    }

    private object DescribeDialogSurface(
        string id,
        string kind,
        string fallbackTitle,
        string message,
        FrameworkElement surface,
        object[]? explicitButtons,
        int stackDepth)
    {
        var visibleTitles = CollectVisibleTitles(surface, maxCount: 6);
        if (visibleTitles.Count == 0 && !string.IsNullOrWhiteSpace(fallbackTitle))
            visibleTitles.Add(fallbackTitle);

        object? bounds = null;
        object? windowCenterOffset = null;
        if (surface.IsLoaded && surface.ActualWidth > 0 && surface.ActualHeight > 0)
        {
            try
            {
                var rect = surface.TransformToVisual(shell.Root).TransformBounds(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                bounds = new
                {
                    x = Math.Round(rect.X, 1),
                    y = Math.Round(rect.Y, 1),
                    w = Math.Round(rect.Width, 1),
                    h = Math.Round(rect.Height, 1)
                };
                if (shell.Root.ActualWidth > 0 && shell.Root.ActualHeight > 0)
                {
                    double centerX = rect.X + rect.Width / 2.0;
                    double centerY = rect.Y + rect.Height / 2.0;
                    windowCenterOffset = new
                    {
                        dx = Math.Round(centerX - shell.Root.ActualWidth / 2.0, 1),
                        dy = Math.Round(centerY - shell.Root.ActualHeight / 2.0, 1)
                    };
                }
            }
            catch { }
        }

        object[] buttons;
        if (explicitButtons is not null)
        {
            buttons = explicitButtons;
        }
        else
        {
            var foundButtons = CollectVisibleButtons(surface);
            buttons = foundButtons.Count > 0
                ? foundButtons.Select((btn, index) => (object)new
                {
                    index,
                    label = DescribeButtonLabel(btn),
                    automationName = NullIfEmpty(AutomationProperties.GetName(btn)),
                    enabled = btn.IsEnabled,
                    kind = ClassifyDialogButton(btn, index, foundButtons.Count)
                }).ToArray()
                : [new { index = 0, label = "关闭", automationName = "关闭", enabled = true, kind = "dismiss" }];
        }

        return new
        {
            open = true,
            id,
            kind,
            title = visibleTitles.FirstOrDefault() ?? fallbackTitle,
            message,
            visibleTitles = visibleTitles.ToArray(),
            bounds,
            windowCenterOffset,
            stackDepth,
            buttons
        };
    }

    private static List<string> CollectVisibleTitles(DependencyObject root, int maxCount)
    {
        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(DependencyObject node)
        {
            if (titles.Count >= maxCount) return;
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is Button) return;
            if (node is TextBlock tb && tb.Visibility == Visibility.Visible && tb.Opacity > 0.001)
            {
                var text = tb.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && seen.Add(text))
                    titles.Add(text);
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count && titles.Count < maxCount; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return titles;
    }

    private static List<Button> CollectVisibleButtons(DependencyObject root)
    {
        var result = new List<Button>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is Button button && button.Visibility == Visibility.Visible && button.Opacity > 0.001)
            {
                result.Add(button);
                return;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return result;
    }

    private static string DescribeButtonLabel(Button button)
    {
        if (button.Content is string text && !string.IsNullOrWhiteSpace(text))
            return text.Trim();
        var auto = AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(auto))
            return auto.Trim();
        if (button.Content is DependencyObject contentRoot)
        {
            var texts = CollectVisibleTitles(contentRoot, 1);
            if (texts.Count > 0) return texts[0];
        }
        return button.Name ?? "";
    }

    private static string ClassifyDialogButton(Button button, int index, int total)
    {
        string label = DescribeButtonLabel(button);
        if (label is "取消" or "关闭" || label.Contains("取消", StringComparison.Ordinal))
            return "cancel";
        if (label is "确定" or "确认" or "保存" or "提交" || label.Contains("确认", StringComparison.Ordinal))
            return "confirm";
        return index == total - 1 ? "primary" : "button";
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

    private sealed record UiImagesInspection(int Total, int LoadingCount, int LoadedCount, int FailedCount, object Payload);

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
            if (DropdownPlacement.DescribeActiveDropdown(shell.Root) is not null)
                return await DesktopWindowCapture.CaptureWindowHandlePngAsync(shell.Handle, cancellationToken);

            return await DesktopWindowCapture.TryRenderElementPngAsync(shell.Root, requireVisiblePixels: false, cancellationToken)
                ?? await DesktopWindowCapture.CaptureWindowHandlePngAsync(shell.Handle, cancellationToken);
        }, cancellationToken);

    public Task<object> OpenTargetAsync(string target, JsonElement? parameters, bool waitIdle, int timeoutMs, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var normalized = target.Trim().ToLowerInvariant();
            DebugLog.Info(DebugCategories.UiNav, "debug.open", "nav.open.start",
                new { target = normalized, @params = parameters });

            if (normalized is "music.player" or "music.player.detail" or "music.queue")
            {
                var musicSession = FindSession("music") ?? throw DebugApiException.NotFound("Music module unavailable.");
                if (!musicSession.Initialized) await shell.RunRequestAsync(new(musicSession));
                if (!shell.CanPresentPlayerSurface(musicSession) &&
                    musicSession.Module.Navigation.FirstOrDefault()?.Route is { Length: > 0 } homeRoute)
                    await shell.NavigateFromUiAsync(musicSession, homeRoute);
                if (musicSession.Module is IDesktopGlobalSurfaceSource globalSource)
                {
                    if (normalized is "music.player" or "music.player.detail")
                    {
                        bool wantOpen = DebugParams.GetBool(parameters, "expanded") ?? true;
                        if (wantOpen && shell.IsQueueOpen) await shell.CloseQueueAsync();
                        if (wantOpen && !shell.IsPlayerDetailOpen) await shell.OpenPlayerDetailAsync(musicSession, globalSource);
                        else if (!wantOpen && shell.IsPlayerDetailOpen) await shell.ClosePlayerDetailAsync();
                    }
                    else
                    {
                        bool wantOpen = DebugParams.GetBool(parameters, "open") ?? true;
                        if (wantOpen && !shell.IsQueueOpen) await shell.OpenQueueAsync(musicSession, globalSource);
                        else if (!wantOpen && shell.IsQueueOpen) await shell.CloseQueueAsync();
                    }
                }
            }
            else if (normalized == "search")
            {
                await OpenSearchTargetAsync(parameters, cancellationToken);
            }
            else if (normalized is "settings.general" or "settings.cache" or "settings.media-controls")
            {
                string section = normalized == "settings.cache" ? "cache"
                    : normalized == "settings.media-controls" ? "shortcuts"
                    : DebugParams.GetString(parameters, "section") ?? "appearance";
                await OpenSettingsDomainAsync("general", section, cancellationToken);
            }
            else if (normalized.StartsWith("settings.", StringComparison.Ordinal))
            {
                var domain = normalized["settings.".Length..];
                var section = DebugParams.GetString(parameters, "section");
                await OpenSettingsDomainAsync(domain, section, cancellationToken);
            }
            else if (normalized.IndexOf('.') is int dot && dot > 0 &&
                     FindSession(normalized[..dot]) is { } matchedSession)
            {
                await OpenModuleTargetAsync(matchedSession.Module.Id, normalized, parameters, cancellationToken);
            }
            else
            {
                throw DebugApiException.NotFound($"Unknown navigation target: '{target}'.");
            }

            if (waitIdle)
                await WaitConditionOnUiAsync("idle", null, timeoutMs, cancellationToken);

            var state = BuildUiStateOnUi();
            DebugLog.Info(DebugCategories.UiNav, "debug.open", "nav.open.end",
                new { target = normalized, state },
                durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return state;
        }, cancellationToken);

    private async Task OpenSearchTargetAsync(JsonElement? parameters, CancellationToken cancellationToken)
    {
        var searchPage = shell.SearchCoordinator.SearchPage;
        if (searchPage is null)
            throw DebugApiException.NotFound("Search page is not registered.");
        if (shell.IsPlayerDetailOpen) await shell.ClosePlayerDetailAsync();
        if (shell.IsQueueOpen) await shell.CloseQueueAsync();
        if (shell.CurrentSession is { IsSettingsOpen: true }) await shell.GoBackAsync();

        var pageSession = shell.Pages.Single(p => ReferenceEquals(p.Page, searchPage));
        var query = DebugParams.GetString(parameters, "query");
        var domain = DebugParams.GetString(parameters, "target") ?? DebugParams.GetString(parameters, "domain") ?? "music";
        var tab = DebugParams.GetString(parameters, "tab");

        if (!string.IsNullOrWhiteSpace(query))
        {
            shell.SearchTargetPickerElement.SelectedValue = domain;
            shell.SearchCoordinator.SetBoxText(query);
            pageSession.SearchDraft = query;
            await shell.ActivatePageAsync(pageSession, token => searchPage.SubmitAsync(query.Trim(), domain, token));
        }
        else
        {
            await shell.ActivatePageAsync(pageSession);
        }
        if (!string.IsNullOrWhiteSpace(tab) && AsAdapter(searchPage) is { } searchAdapter)
            await searchAdapter.TryOpenDebugTargetAsync("search:" + tab, parameters, cancellationToken);
    }

    private async Task OpenSettingsDomainAsync(string domain, string? section, CancellationToken cancellationToken)
    {
        if (shell.IsPlayerDetailOpen) await shell.ClosePlayerDetailAsync();
        if (shell.IsQueueOpen) await shell.CloseQueueAsync();
        if (domain == "music" && string.Equals(section, "home-source", StringComparison.OrdinalIgnoreCase))
        {
            domain = "general";
            section = "sources";
        }
        else if (domain is "music" or "film" && string.Equals(section, "cache", StringComparison.OrdinalIgnoreCase))
        {
            domain = "general";
            section = "cache";
        }
        if (domain == "general")
        {
            shell.EnterGeneralSettings();
            if (!string.IsNullOrWhiteSpace(section) && shell.GeneralSettingsHost.Content is GeneralSettingsView general)
            {
                if (!general.SelectArea(section))
                    throw DebugApiException.NotFound($"Unknown general settings section: '{section}'.");
            }
            return;
        }

        var targetSession = FindSession(domain)
            ?? throw DebugApiException.NotFound($"Settings domain not found: '{domain}'.");
        await shell.EnterModuleSettingsAsync(targetSession);
        if (!string.IsNullOrWhiteSpace(section) && AsAdapter(targetSession.Module) is { } adapter)
        {
            bool ok = await adapter.TryOpenDebugSettingsSectionAsync(section, cancellationToken);
            if (!ok) throw DebugApiException.NotFound($"Unknown settings section '{section}' in '{domain}'.");
        }
    }

    private async Task OpenModuleTargetAsync(string moduleId, string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var session = FindSession(moduleId) ?? throw DebugApiException.NotFound($"Module not found: '{moduleId}'.");
        if (shell.IsPlayerDetailOpen) await shell.ClosePlayerDetailAsync();
        if (shell.IsQueueOpen) await shell.CloseQueueAsync();
        await shell.PrepareModuleTargetAsync(session);
        if (AsAdapter(session.Module) is { } adapter)
        {
            bool handled = await adapter.TryOpenDebugTargetAsync(target, parameters, cancellationToken);
            if (!handled) throw DebugApiException.NotFound($"Unsupported target '{target}' for module '{moduleId}'.");
            shell.CommitDebugDestination(session);
        }
        else
        {
            throw DebugApiException.NotFound($"Module '{moduleId}' does not expose debug navigation.");
        }
    }

    public Task<object> NavigateBackAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            DebugLog.Info(DebugCategories.UiNav, "debug.back", "nav.back");
            await shell.GoBackAsync();
            return BuildUiStateOnUi();
        }, cancellationToken);

    public Task<object> NavigateForwardAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            DebugLog.Info(DebugCategories.UiNav, "debug.forward", "nav.forward");
            await shell.GoForwardAsync();
            return BuildUiStateOnUi();
        }, cancellationToken);

    public Task<object> RefreshAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            DebugLog.Info(DebugCategories.UiNav, "debug.refresh", "nav.refresh");
            if (ResolveForegroundOverlayAdapter(out var overlayTarget) is { } overlayAdapter)
            {
                var overlayHandled = await overlayAdapter.TryHandleDebugOverlayActionAsync(overlayTarget, "refresh", null, cancellationToken);
                if (overlayHandled is not null) return new { refreshed = true, result = overlayHandled, state = BuildUiStateOnUi() };
            }
            if (shell.CurrentPage is { } page && AsAdapter(page.Page) is { } pageAdapter)
            {
                var pageHandled = await pageAdapter.TryHandleDebugActionAsync("refresh", null, false, cancellationToken);
                if (pageHandled is not null) return new { refreshed = true, result = pageHandled, state = BuildUiStateOnUi() };
            }
            if (shell.CurrentSession is { } current && !(current.IsSettingsOpen && shell.IsGeneralSettingsOpen) &&
                AsAdapter(current.Module) is { } adapter)
            {
                var handled = await adapter.TryHandleDebugActionAsync("refresh", null, current.IsSettingsOpen, cancellationToken);
                if (handled is not null) return new { refreshed = true, result = handled, state = BuildUiStateOnUi() };
            }
            if (shell.CurrentSession is { IsSettingsOpen: false, Current: { } dest } active)
                await shell.RunRequestAsync(new(active, dest));
            return new { refreshed = true, state = BuildUiStateOnUi() };
        }, cancellationToken);

    public Task<object> PerformActionAsync(string action, JsonElement? parameters, bool waitIdle, int timeoutMs, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var normalized = action.Trim().ToLowerInvariant();
            DebugLog.Info(DebugCategories.UiAction, normalized, "action.start", new { action = normalized, @params = parameters });

            object? outcome;
            if (normalized == "search.submit")
            {
                var query = DebugParams.GetString(parameters, "query");
                if (string.IsNullOrWhiteSpace(query))
                    throw DebugApiException.InvalidArgument("search.submit requires non-empty 'query'.");
                await OpenSearchTargetAsync(parameters, cancellationToken);
                outcome = new { submitted = true, query };
            }
            else if (normalized == "dialog.action")
            {
                outcome = HandleDialogAction(parameters);
            }
            else if (normalized == "window.close_request")
            {
                outcome = await shell.CloseCoordinator.HandleDebugCloseRequestAsync();
            }
            else if (normalized == "search.scope.open")
            {
                outcome = HandleSearchScopeOpen();
            }
            else if (normalized == "search.scope.select")
            {
                outcome = HandleSearchScopeSelect(parameters, includeKind: false);
            }
            else if (normalized == "dropdown.open")
            {
                outcome = HandleDropdownOpen(parameters);
            }
            else if (normalized == "dropdown.select")
            {
                outcome = HandleDropdownSelect(parameters);
            }
            else if (normalized == "dropdown.close")
            {
                outcome = HandleDropdownClose();
            }
            else if (normalized == "nonclient.refresh")
            {
                shell.NativeCaption?.RefreshImmediate();
                outcome = shell.NativeCaption?.InspectDebugState() ?? new { refreshed = false };
            }
            else if (normalized == "tab.select" &&
                     !shell.IsQueueOpen && !shell.IsPlayerDetailOpen &&
                     string.Equals(DebugParams.GetString(parameters, "group"), "settings-section", StringComparison.OrdinalIgnoreCase) &&
                     shell.CurrentSession is { IsSettingsOpen: true } && shell.IsGeneralSettingsOpen &&
                     shell.GeneralSettingsHost.Content is GeneralSettingsView generalView)
            {
                var id = DebugParams.GetString(parameters, "id");
                if (string.IsNullOrWhiteSpace(id) && DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    id = generalView.SelectAreaByIndex(idx)
                        ?? throw DebugApiException.InvalidArgument($"General settings tab index {idx} out of range.");
                    outcome = new { selected = id };
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(id) || !generalView.SelectArea(id))
                        throw DebugApiException.NotFound($"Unable to select general settings tab '{id}'.");
                    outcome = new { selected = id };
                }
            }
            else if (normalized == "setting.set" &&
                     string.Equals(DebugParams.GetString(parameters, "domain"), "general", StringComparison.OrdinalIgnoreCase))
            {
                var key = DebugParams.GetString(parameters, "key") ?? throw DebugApiException.InvalidArgument("Missing 'key'.");
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                if (shell.GeneralSettingsHost.Content is not GeneralSettingsView gv || !await gv.TryDebugSetAsync(key, val))
                    throw DebugApiException.InvalidArgument($"Unsupported general setting '{key}'.");
                outcome = new { saved = true, domain = "general", key };
            }
            else if (normalized == "setting.action" &&
                     string.Equals(DebugParams.GetString(parameters, "domain"), "general", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = DebugParams.GetString(parameters, "command") ?? throw DebugApiException.InvalidArgument("Missing 'command'.");
                if (string.Equals(cmd, "reset_user_data", StringComparison.OrdinalIgnoreCase))
                {
                    shell.ConfirmUserDataReset();
                    outcome = new { dialogOpened = true };
                }
                else throw DebugApiException.InvalidArgument($"Unsupported general settings command '{cmd}'.");
            }
            else if (normalized is not ("setting.set" or "setting.action") &&
                     ResolveForegroundOverlayAdapter(out var overlayTarget) is { } overlayAdapter)
            {
                outcome = await overlayAdapter.TryHandleDebugOverlayActionAsync(overlayTarget, normalized, parameters, cancellationToken)
                    ?? await overlayAdapter.TryHandleDebugActionAsync(normalized, parameters, settingsOpen: false, cancellationToken)
                    ?? throw DebugApiException.InvalidArgument($"Action '{normalized}' is not supported on foreground overlay '{overlayTarget}'.");
            }
            else
            {
                IDebugModuleAdapter? targetAdapter = null;
                bool settingsOpen = false;
                if (normalized is "setting.set" or "setting.action" && DebugParams.GetString(parameters, "domain") is { Length: > 0 } dom)
                {
                    targetAdapter = FindAdapter(dom);
                    settingsOpen = true;
                }
                else if (shell.CurrentPage is { } page && AsAdapter(page.Page) is { } pageAdapter)
                {
                    targetAdapter = pageAdapter;
                }
                else if (shell.CurrentSession is { } current && !(current.IsSettingsOpen && shell.IsGeneralSettingsOpen) &&
                         AsAdapter(current.Module) is { } modAdapter)
                {
                    targetAdapter = modAdapter;
                    settingsOpen = current.IsSettingsOpen;
                }
                if (targetAdapter is null)
                    throw DebugApiException.InvalidState($"No active module adapter can handle action '{normalized}'.");
                outcome = await targetAdapter.TryHandleDebugActionAsync(normalized, parameters, settingsOpen, cancellationToken)
                    ?? throw DebugApiException.InvalidArgument($"Action '{normalized}' is not supported on current surface '{targetAdapter.DebugModuleId}'.");
            }

            if (waitIdle)
                await WaitConditionOnUiAsync("idle", null, timeoutMs, cancellationToken);

            var state = BuildUiStateOnUi();
            DebugLog.Info(DebugCategories.UiAction, normalized, "action.end",
                new { action = normalized, outcome },
                durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new { action = normalized, result = outcome, state };
        }, cancellationToken);

    private object HandleSearchScopeOpen()
    {
        if (!DesktopVisual.IsVisible(shell.SearchTargetPickerElement) || !shell.SearchTargetPickerElement.IsEnabled)
            throw DebugApiException.InvalidState("Search scope picker is not visible or enabled.");
        shell.SearchTargetPickerElement.Open();
        return DropdownPlacement.DescribeSearchScope(shell.SearchTargetPickerElement, shell.Root);
    }

    private object HandleSearchScopeSelect(JsonElement? parameters, bool includeKind)
    {
        if (!DesktopVisual.IsVisible(shell.SearchTargetPickerElement) || !shell.SearchTargetPickerElement.IsEnabled)
            throw DebugApiException.InvalidState("Search scope picker is not visible or enabled.");
        var id = DebugParams.GetString(parameters, "id") ?? DebugParams.GetString(parameters, "value") ?? DebugParams.GetString(parameters, "target");
        var label = DebugParams.GetString(parameters, "label");
        var index = DebugParams.GetInt(parameters, "optionIndex") ?? DebugParams.GetInt(parameters, "itemIndex") ?? DebugParams.GetInt(parameters, "index");
        if (!shell.SearchTargetPickerElement.TrySelect(id, label, index))
            throw DebugApiException.NotFound("No matching search scope option found.");
        return includeKind
            ? new
            {
                kind = "search-scope",
                selectedValue = shell.SearchTargetPickerElement.SelectedValue,
                selectedLabel = shell.SearchTargetPickerElement.SelectedItem?.Label
            }
            : new
            {
                selectedValue = shell.SearchTargetPickerElement.SelectedValue,
                selectedLabel = shell.SearchTargetPickerElement.SelectedItem?.Label
            };
    }

    private object HandleDropdownOpen(JsonElement? parameters)
    {
        var kind = DebugParams.GetString(parameters, "kind")?.Trim().ToLowerInvariant();
        var name = DebugParams.GetString(parameters, "name");
        var automationName = DebugParams.GetString(parameters, "automationName");
        var index = DebugParams.GetInt(parameters, "index");

        if (kind == "search-scope" || string.Equals(name, "SearchTargetPicker", StringComparison.OrdinalIgnoreCase))
            return HandleSearchScopeOpen();

        var combo = FindVisibleComboBox(name, automationName, index)
            ?? throw DebugApiException.NotFound("No matching visible ComboBox found.");
        if (!combo.IsEnabled)
            throw DebugApiException.InvalidState("Target ComboBox is disabled.");
        combo.IsDropDownOpen = true;
        combo.UpdateLayout();
        return DropdownPlacement.DescribeComboBox(combo, shell.Root);
    }

    private object HandleDropdownSelect(JsonElement? parameters)
    {
        var kind = DebugParams.GetString(parameters, "kind")?.Trim().ToLowerInvariant();
        if (kind == "search-scope" || shell.SearchTargetPickerElement.IsDropDownOpen)
            return HandleSearchScopeSelect(parameters, includeKind: true);

        var name = DebugParams.GetString(parameters, "name");
        var automationName = DebugParams.GetString(parameters, "automationName");
        var comboIndex = DebugParams.GetInt(parameters, "comboIndex");
        var combo = DropdownPlacement.ActiveComboBox
            ?? FindVisibleComboBox(name, automationName, comboIndex)
            ?? throw DebugApiException.InvalidState("No active or matching ComboBox found.");
        if (!combo.IsEnabled)
            throw DebugApiException.InvalidState("Target ComboBox is disabled.");

        int? targetIndex = DebugParams.GetInt(parameters, "optionIndex") ?? DebugParams.GetInt(parameters, "itemIndex") ?? DebugParams.GetInt(parameters, "index");
        string? wantedValue = DebugParams.GetString(parameters, "value") ?? DebugParams.GetString(parameters, "id");
        string? wantedLabel = DebugParams.GetString(parameters, "label");

        int matchedIndex = -1;
        if (targetIndex is { } idx && idx >= 0 && idx < combo.Items.Count)
        {
            matchedIndex = idx;
        }
        else
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i];
                var itemValue = DropdownPlacement.DescribeItemValue(combo, item);
                var itemLabel = DropdownPlacement.DescribeItemLabel(combo, item);
                if ((!string.IsNullOrWhiteSpace(wantedValue) && string.Equals(itemValue, wantedValue, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(wantedLabel) && string.Equals(itemLabel, wantedLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedIndex = i;
                    break;
                }
            }
        }
        if (matchedIndex < 0)
            throw DebugApiException.NotFound("No matching dropdown option found.");

        combo.SelectedIndex = matchedIndex;
        combo.IsDropDownOpen = false;
        return new
        {
            kind = combo is EqualWidthComboBox ? "equal-width-combobox" : "combobox",
            selectedIndex = combo.SelectedIndex,
            selectedValue = DropdownPlacement.DescribeItemValue(combo, combo.SelectedItem),
            selectedLabel = DropdownPlacement.DescribeItemLabel(combo, combo.SelectedItem)
        };
    }

    private object HandleDropdownClose()
    {
        bool closedAny = false;
        if (shell.SearchTargetPickerElement.IsDropDownOpen)
        {
            shell.SearchTargetPickerElement.Close();
            closedAny = true;
        }
        if (DropdownPlacement.ActiveComboBox is { IsDropDownOpen: true } active)
        {
            active.IsDropDownOpen = false;
            closedAny = true;
        }
        foreach (var combo in CollectVisibleComboBoxes())
        {
            if (combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = false;
                closedAny = true;
            }
        }
        return new { closed = closedAny };
    }

    private ComboBox? FindVisibleComboBox(string? name, string? automationName, int? index)
    {
        var combos = CollectVisibleComboBoxes();
        if (!string.IsNullOrWhiteSpace(name))
            return combos.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(automationName))
            return combos.FirstOrDefault(c => string.Equals(AutomationProperties.GetName(c), automationName, StringComparison.OrdinalIgnoreCase));
        int targetIndex = index ?? 0;
        return targetIndex >= 0 && targetIndex < combos.Count ? combos[targetIndex] : null;
    }

    private List<ComboBox> CollectVisibleComboBoxes()
    {
        var list = new List<ComboBox>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is ComboBox combo && combo.IsLoaded && DesktopVisual.IsVisible(combo))
            {
                list.Add(combo);
                return;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(shell.Root);
        return list;
    }

    private object HandleDialogAction(JsonElement? parameters)
    {
        var act = (DebugParams.GetString(parameters, "action") ?? "dismiss").Trim().ToLowerInvariant();
        var btnIndex = DebugParams.GetInt(parameters, "index");
        var btnLabel = DebugParams.GetString(parameters, "label")?.Trim();

        if (shell.ResetDialogElement.IsOpen)
        {
            bool confirming = shell.ResetDialogElement.IsConfirming;
            if (act == "button")
            {
                if (confirming)
                {
                    if (btnIndex == 0 || string.Equals(btnLabel, "取消", StringComparison.OrdinalIgnoreCase)) act = "cancel";
                    else if (btnIndex == 1 || string.Equals(btnLabel, "确认清除", StringComparison.OrdinalIgnoreCase) || string.Equals(btnLabel, "清除全部数据", StringComparison.OrdinalIgnoreCase)) act = "confirm";
                }
                else
                {
                    if (btnIndex == 0 || string.Equals(btnLabel, "退出", StringComparison.OrdinalIgnoreCase)) act = "exit";
                    else if (btnIndex == 1 || string.Equals(btnLabel, "重试清除", StringComparison.OrdinalIgnoreCase) || string.Equals(btnLabel, "确认清除", StringComparison.OrdinalIgnoreCase)) act = "confirm";
                }
            }
            if (act is "cancel" or "dismiss" or "close")
            {
                if (!confirming || shell.ResetDialogElement.IsPending)
                    throw DebugApiException.InvalidState("User data reset dialog cannot be cancelled in its current state.");
                shell.CancelUserDataReset();
                return new { dialog = "user-data-reset", action = "cancel" };
            }
            if (act == "exit")
            {
                if (confirming || shell.ResetDialogElement.IsPending)
                    throw DebugApiException.InvalidState("Exit action is only available after reset failure.");
                shell.OnUserDataResetExit();
                return new { dialog = "user-data-reset", action = "exit" };
            }
            if (act == "confirm")
            {
                if (shell.ResetDialogElement.IsPending)
                    throw DebugApiException.InvalidState("User data reset is currently in progress.");
                shell.ConfirmUserDataResetAction();
                return new { dialog = "user-data-reset", action = "confirm" };
            }
            throw DebugApiException.InvalidArgument($"Unsupported action '{act}' for dialog 'user-data-reset'.");
        }
        else if (shell.CloseDialogElement.IsOpen)
        {
            if (act == "button")
            {
                if (btnIndex == 0 || string.Equals(btnLabel, "取消", StringComparison.OrdinalIgnoreCase)) act = "cancel";
                else if (btnIndex == 1 || string.Equals(btnLabel, "最小化到托盘", StringComparison.OrdinalIgnoreCase)) act = "tray";
                else if (btnIndex == 2 || string.Equals(btnLabel, "退出应用", StringComparison.OrdinalIgnoreCase) || string.Equals(btnLabel, "退出", StringComparison.OrdinalIgnoreCase)) act = "exit";
            }
            if (act is "cancel" or "dismiss" or "close")
            {
                shell.CloseCoordinator.DismissCloseChoice(restoreFocus: false);
                return new { dialog = "close-window", action = "cancel" };
            }
            if (act is "confirm" or "exit")
            {
                shell.CloseCoordinator.OnCloseChoiceRequested(WindowCloseBehavior.Exit);
                return new { dialog = "close-window", action = "exit" };
            }
            if (act is "tray" or "minimize")
            {
                shell.CloseCoordinator.OnCloseChoiceRequested(WindowCloseBehavior.MinimizeToTray);
                return new { dialog = "close-window", action = "tray" };
            }
            throw DebugApiException.InvalidArgument($"Unsupported action '{act}' for dialog 'close-window'.");
        }
        else if (shell.ChannelDialogHostElement.IsOpen)
        {
            if (act is "cancel" or "dismiss" or "close")
            {
                shell.ChannelDialogHostElement.RequestClose();
                return new { dialog = "channel-dialog", action = "dismiss" };
            }
            if (shell.ChannelDialogHostElement.ActiveContent is { } activePanel)
            {
                var buttons = CollectVisibleButtons(activePanel);
                Button? matched = null;
                if (btnIndex is { } bi && bi >= 0 && bi < buttons.Count)
                {
                    matched = buttons[bi];
                }
                else if (!string.IsNullOrWhiteSpace(btnLabel))
                {
                    matched = buttons.FirstOrDefault(b =>
                        string.Equals(DescribeButtonLabel(b), btnLabel, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(AutomationProperties.GetName(b), btnLabel, StringComparison.OrdinalIgnoreCase));
                }
                else if (act is "confirm" or "primary")
                {
                    matched = buttons.LastOrDefault(b => b.IsEnabled);
                }
                if (matched is not null)
                {
                    if (!matched.IsEnabled)
                        throw DebugApiException.InvalidState($"Dialog button '{DescribeButtonLabel(matched)}' is disabled.");
                    if (FrameworkElementAutomationPeer.CreatePeerForElement(matched)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                        invoke.Invoke();
                    return new { dialog = "channel-dialog", action = act, label = DescribeButtonLabel(matched) };
                }
            }
            throw DebugApiException.InvalidArgument($"No matching button for action '{act}' on 'channel-dialog'.");
        }
        throw DebugApiException.InvalidState("No matching active dialog for dialog.action.");
    }

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
                    var wantedState = DebugParams.GetString(parameters, "state") ?? "playing";
                    var pb = await GetPlaybackSnapshotOnUiAsync(cancellationToken);
                    JsonElement? domObj = string.Equals(domain, "music", StringComparison.OrdinalIgnoreCase) ? pb.Music
                        : string.Equals(domain, "film", StringComparison.OrdinalIgnoreCase) ? pb.Film : null;
                    if (domObj is { ValueKind: JsonValueKind.Object } obj &&
                        obj.TryGetProperty("state", out var st) && string.Equals(st.GetString(), wantedState, StringComparison.OrdinalIgnoreCase))
                    { matched = true; snapshot = pb.Payload; }
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
                throw DebugApiException.Timeout($"Timed out waiting for condition '{normalized}' after {timeoutMs}ms.");
            await Task.Delay(100, cancellationToken);
        }
    }

    private sealed record PlaybackSnapshot(JsonElement? Music, JsonElement? Film, object Payload);

    private async Task<PlaybackSnapshot> GetPlaybackSnapshotOnUiAsync(CancellationToken cancellationToken)
    {
        object? music = FindAdapter("music") is { } m ? await m.GetDebugPlaybackStateAsync(cancellationToken) : null;
        object? film = FindAdapter("film") is { } f ? await f.GetDebugPlaybackStateAsync(cancellationToken) : null;
        JsonElement? musicJson = music is not null ? JsonSerializer.SerializeToElement(music) : null;
        JsonElement? filmJson = film is not null ? JsonSerializer.SerializeToElement(film) : null;
        string? activeOwner = null;
        if (musicJson is { ValueKind: JsonValueKind.Object } mj && mj.TryGetProperty("state", out var ms) &&
            ms.GetString() is "playing" or "buffering")
            activeOwner = "music";
        else if (filmJson is { ValueKind: JsonValueKind.Object } fj && fj.TryGetProperty("state", out var fs) &&
            fs.GetString() is "playing" or "buffering")
            activeOwner = "film";
        return new PlaybackSnapshot(musicJson, filmJson, new { activeOwner, music = musicJson, film = filmJson });
    }

    public Task<object> GetPlaybackStateAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () => (await GetPlaybackSnapshotOnUiAsync(cancellationToken)).Payload, cancellationToken);

    public Task<object> PlaybackCommandAsync(string domain, string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            var normalized = domain.Trim().ToLowerInvariant();
            if (normalized is not ("music" or "film"))
                throw DebugApiException.InvalidArgument($"Invalid playback domain: '{domain}' (expected 'music' or 'film').");
            var adapter = FindAdapter(normalized) ?? throw DebugApiException.NotFound($"Module '{normalized}' not found.");
            var res = await adapter.TryDebugPlaybackCommandAsync(command.Trim().ToLowerInvariant(), parameters, cancellationToken)
                ?? throw DebugApiException.InvalidArgument($"Unsupported playback command '{command}' for '{normalized}'.");
            return new { domain = normalized, command, result = res };
        }, cancellationToken);

    public Task<object> NovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            var adapter = FindAdapter("novel") ?? throw DebugApiException.NotFound("Novel module not found.");
            var res = await adapter.TryDebugNovelCommandAsync(command.Trim().ToLowerInvariant(), parameters, cancellationToken)
                ?? throw DebugApiException.InvalidArgument($"Unsupported novel command '{command}'.");
            return new { command, result = res };
        }, cancellationToken);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
