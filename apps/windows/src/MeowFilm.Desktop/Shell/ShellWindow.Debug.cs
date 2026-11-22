using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow : IDebugShellHost
{
    private static readonly IReadOnlyList<string> BaseTargets =
    [
        "music.home", "music.channels", "music.discovery", "music.personal", "music.daily", "music.fm",
        "music.library", "music.favorites", "music.history", "music.archives", "music.playlist", "music.player", "music.queue",
        "film.home", "film.data", "film.detail", "film.playback",
        "novel.home", "novel.categories", "novel.shelf", "novel.book", "novel.reader",
        "search",
        "settings.general", "settings.music", "settings.film", "settings.novel", "settings.cache", "settings.media-controls"
    ];

    private static readonly IReadOnlyList<string> BaseActions =
    [
        "section.item.invoke", "section.more", "tab.select", "list.item.invoke",
        "list.load_more", "search.submit", "dialog.action", "setting.set", "setting.action"
    ];

    public bool IsWindowReady => _loaded && !_isClosing && !_isClosed;
    public IReadOnlyList<string> SupportedTargets => BaseTargets;
    public IReadOnlyList<string> SupportedActions => BaseActions;

    private Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.HasThreadAccess) return action();
        return Dispatcher.EnqueueAsync(action, DispatcherQueuePriority.Normal, cancellationToken);
    }

    private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken) =>
        OnUiAsync(() => Task.FromResult(action()), cancellationToken);

    private IEnumerable<IDebugModuleAdapter> AllAdapters()
    {
        foreach (var session in _sessions)
            if (session.Module is IDebugModuleAdapter adapter) yield return adapter;
        foreach (var page in _pages)
            if (page.Page is IDebugModuleAdapter adapter) yield return adapter;
    }

    private IDebugModuleAdapter? FindAdapter(string moduleId) =>
        AllAdapters().FirstOrDefault(a => string.Equals(a.DebugModuleId, moduleId, StringComparison.OrdinalIgnoreCase));

    private ModuleSession? FindSession(string moduleId) =>
        _sessions.FirstOrDefault(s => string.Equals(s.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase));

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
                loaded = _loaded,
                isClosing = _isClosing,
                isBusy = _state.IsBusy || _activeRequest is not null || _pageRequest is not null,
                stack = Environment.StackTrace
            };
        }, cancellationToken);
    }

    public Task<object> GetUiStateAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(BuildUiStateOnUi, cancellationToken);

    private object BuildUiStateOnUi()
    {
        var current = _current;
        bool settingsOpen = _currentPage is null && current is { IsSettingsOpen: true };
        string? settingsDomain = null;
        string? settingsSection = null;
        string? moduleId = _currentPage is not null ? _currentPage.Page.Id : current?.Module.Id;
        string? route = _currentPage is not null ? _currentPage.Page.Id : current?.Current?.Value ?? current?.Module.CaptureNavigationLocation();
        string target;

        if (_queueOpen)
        {
            target = "music.queue";
        }
        else if (_playerDetailOpen)
        {
            target = "music.player";
        }
        else if (_currentPage is { Page: var page })
        {
            target = page is IDesktopSearchPage ? "search" : page.Id;
        }
        else if (settingsOpen)
        {
            if (_generalSettingsOpen)
            {
                settingsDomain = "general";
                settingsSection = (_generalSettingsHost.Content as GeneralSettingsView)?.CurrentArea ?? "appearance";
                target = "settings.general";
            }
            else
            {
                settingsDomain = current!.Module.Id;
                target = "settings." + current.Module.Id;
                if (current.Module is IDebugModuleAdapter adapter)
                    adapter.DescribeCurrentTarget(settingsOpen: true, out settingsSection);
            }
        }
        else if (current?.Module is IDebugModuleAdapter activeAdapter &&
                 activeAdapter.DescribeCurrentTarget(settingsOpen: false, out _) is { Length: > 0 } described)
        {
            target = described;
        }
        else
        {
            target = route ?? moduleId ?? "";
        }

        var stack = _history.Select(entry => new
        {
            target = entry.Destination.PageId is { Length: > 0 } pid ? (pid == "search-results" ? "search" : pid) : entry.Destination.Value,
            route = entry.Destination.Location ?? entry.Destination.Value,
            title = entry.Session.Module.Title
        }).ToArray();

        object? activeDialog = null;
        if (ResetDialog.IsOpen)
        {
            activeDialog = new
            {
                id = "user-data-reset",
                title = "清除全部本机数据",
                message = "清空本机数据确认框",
                buttons = new object[]
                {
                    new { index = 0, label = "取消", kind = "cancel" },
                    new { index = 1, label = "确认清除", kind = "confirm" }
                }
            };
        }
        else if (CloseDialog.IsOpen)
        {
            activeDialog = new
            {
                id = "close-window",
                title = "关闭主窗口",
                message = "关闭行为选择框",
                buttons = new object[]
                {
                    new { index = 0, label = "取消", kind = "cancel" },
                    new { index = 1, label = "最小化到托盘", kind = "tray" },
                    new { index = 2, label = "退出应用", kind = "exit" }
                }
            };
        }
        else if (ChannelDialogHost.IsOpen)
        {
            activeDialog = new
            {
                id = "channel-dialog",
                title = "弹出层",
                message = "",
                buttons = new object[]
                {
                    new { index = 0, label = "关闭", kind = "dismiss" }
                }
            };
        }

        var notices = new List<object>();
        foreach (var (source, _, _) in ownedNoticeSubscriptions)
        {
            foreach (var n in source.OwnedNotices)
                notices.Add(new { id = n.Owner.GetHashCode().ToString(), level = "info", message = n.Message });
        }

        var size = AppWindow.Size;
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var regions = _nativeCaption?.InspectRegions() ?? ([], []);
        return new
        {
            target,
            moduleId,
            route,
            title = Title,
            settingsOpen,
            settingsDomain,
            settingsSection,
            sidebarVisible = SidebarBackground.Visibility == Visibility.Visible,
            playerBarVisible = PlayerHost.Visibility == Visibility.Visible,
            playerDetailOpen = _playerDetailOpen,
            queueOpen = _queueOpen,
            navigationStack = stack,
            canGoBack = _state.CanGoBack,
            canGoForward = _state.CanGoForward,
            isBusy = _state.IsBusy || _activeRequest is not null || _pageRequest is not null,
            statusText = _state.Status,
            isError = _state.IsError,
            errorText = _state.IsError ? _state.Status : null,
            activeDialog,
            notices,
            caption = new
            {
                titleBarHeight = AppWindow.TitleBar.Height,
                captionRects = regions.Caption.Select(r => new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray(),
                passthroughRects = regions.Passthrough.Select(r => new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray()
            },
            window = new
            {
                visible = IsVisible,
                active = IsActive,
                width = size.Width,
                height = size.Height,
                scale
            }
        };
    }

    public Task<object> GetUiContentAsync(int maxItems, bool includeRaw, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            if (_currentPage is { Page: IDebugModuleAdapter pageAdapter })
            {
                var content = await pageAdapter.BuildDebugContentAsync(false, maxItems, includeRaw, cancellationToken);
                if (content is not null) return content;
            }
            if (_current is { IsSettingsOpen: true } && _generalSettingsOpen &&
                _generalSettingsHost.Content is GeneralSettingsView general)
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
            if (_playerDetailOpen || _queueOpen)
            {
                var music = FindAdapter("music");
                if (music is not null)
                {
                    var overlayTarget = _queueOpen ? "music.queue" : "music.player";
                    var proj = await music.BuildDebugOverlayContentAsync(overlayTarget, maxItems, includeRaw, cancellationToken)
                        ?? await music.BuildDebugContentAsync(false, maxItems, includeRaw, cancellationToken);
                    if (proj is not null) return proj;
                }
            }
            if (_current is { Module: IDebugModuleAdapter adapter } current)
            {
                var proj = await adapter.BuildDebugContentAsync(current.IsSettingsOpen, maxItems, includeRaw, cancellationToken);
                if (proj is not null) return proj;
            }
            return new
            {
                target = _current?.Module.Id ?? "",
                kind = "empty",
                loading = _state.IsBusy,
                error = _state.IsError ? _state.Status : null,
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
        OnUiAsync<object>(() => WalkVisualTree(Root, 0, maxDepth, visibleOnly) ?? new { }, cancellationToken);

    private object? WalkVisualTree(DependencyObject element, int depth, int maxDepth, bool visibleOnly)
    {
        if (element is FrameworkElement fe)
        {
            bool visible = fe.Visibility == Visibility.Visible && fe.Opacity > 0.001;
            if (visibleOnly && !visible) return null;
            Rect bounds = default;
            try
            {
                var pt = fe.TransformToVisual(Root).TransformPoint(new Point(0, 0));
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
        OnUiAsync(async () =>
        {
            try
            {
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(Root);
                var buffer = await rtb.GetPixelsAsync();
                var pixels = buffer.ToArray();
                if (rtb.PixelWidth > 0 && rtb.PixelHeight > 0 && pixels.Length > 0)
                {
                    using var ras = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        (uint)rtb.PixelWidth,
                        (uint)rtb.PixelHeight,
                        96, 96, pixels);
                    await encoder.FlushAsync();
                    ras.Seek(0);
                    using var stream = ras.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, cancellationToken);
                    return ms.ToArray();
                }
            }
            catch { }

            return await CaptureWindowViaPrintWindowAsync(cancellationToken);
        }, cancellationToken);

    private async Task<byte[]> CaptureWindowViaPrintWindowAsync(CancellationToken cancellationToken)
    {
        if (!GetWindowRect(Handle, out var rect))
            throw DebugApiException.Internal("Unable to query window bounds for screenshot.");
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        nint screenDc = GetDC(0);
        if (screenDc == 0)
            throw DebugApiException.Internal("GetDC failed while capturing screenshot.");
        nint memDc = CreateCompatibleDC(screenDc);
        if (memDc == 0)
        {
            ReleaseDC(0, screenDc);
            throw DebugApiException.Internal("CreateCompatibleDC failed while capturing screenshot.");
        }
        var bmi = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        nint dib = CreateDIBSection(memDc, ref bmi, 0, out nint bits, 0, 0);
        if (dib == 0 || bits == 0)
        {
            if (dib != 0) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
            throw DebugApiException.Internal("CreateDIBSection failed while capturing screenshot.");
        }
        nint old = SelectObject(memDc, dib);
        try
        {
            PrintWindow(Handle, memDc, 0x00000002); // PW_RENDERFULLCONTENT
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            using var ras = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            ras.Seek(0);
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            return ms.ToArray();
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    public Task<object> OpenTargetAsync(string target, JsonElement? parameters, bool waitIdle, int timeoutMs, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var normalized = target.Trim().ToLowerInvariant();
            DebugLog.Info(DebugCategories.UiNav, "debug.open", "nav.open.start",
                new { target = normalized, @params = parameters });

            if (normalized is "music.player" or "music.queue")
            {
                var musicSession = FindSession("music") ?? throw DebugApiException.NotFound("Music module unavailable.");
                if (!musicSession.Initialized) await RunRequestAsync(new(musicSession));
                if (!CanPresentPlayerSurface(musicSession))
                    await NavigateFromUiAsync(musicSession, "catalog-home");
                if (musicSession.Module is IDesktopGlobalSurfaceSource globalSource)
                {
                    if (normalized == "music.player")
                    {
                        bool wantOpen = parameters is not { ValueKind: JsonValueKind.Object } p ||
                            !p.TryGetProperty("expanded", out var exp) || exp.ValueKind != JsonValueKind.False;
                        if (wantOpen && _queueOpen) await CloseQueueAsync();
                        if (wantOpen && !_playerDetailOpen) await OpenPlayerDetailAsync(musicSession, globalSource);
                        else if (!wantOpen && _playerDetailOpen) await ClosePlayerDetailAsync();
                    }
                    else
                    {
                        bool wantOpen = parameters is not { ValueKind: JsonValueKind.Object } p ||
                            !p.TryGetProperty("open", out var op) || op.ValueKind != JsonValueKind.False;
                        if (wantOpen && !_queueOpen) await OpenQueueAsync(musicSession, globalSource);
                        else if (!wantOpen && _queueOpen) await CloseQueueAsync();
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
                    : GetStringParam(parameters, "section") ?? "appearance";
                await OpenSettingsDomainAsync("general", section, cancellationToken);
            }
            else if (normalized is "settings.music" or "settings.film" or "settings.novel")
            {
                var domain = normalized["settings.".Length..];
                var section = GetStringParam(parameters, "section");
                if (domain == "music" && section is "home-source" or "cache")
                    await OpenSettingsDomainAsync("general", section == "cache" ? "cache" : "sources", cancellationToken);
                else if (domain == "film" && section == "cache")
                    await OpenSettingsDomainAsync("general", "cache", cancellationToken);
                else
                    await OpenSettingsDomainAsync(domain, section, cancellationToken);
            }
            else if (normalized.StartsWith("music.", StringComparison.Ordinal))
            {
                await OpenModuleTargetAsync("music", normalized, parameters, cancellationToken);
            }
            else if (normalized.StartsWith("film.", StringComparison.Ordinal))
            {
                await OpenModuleTargetAsync("film", normalized, parameters, cancellationToken);
            }
            else if (normalized.StartsWith("novel.", StringComparison.Ordinal))
            {
                await OpenModuleTargetAsync("novel", normalized, parameters, cancellationToken);
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
        if (_searchPage is null)
            throw DebugApiException.NotFound("Search page is not registered.");
        if (_playerDetailOpen) await ClosePlayerDetailAsync();
        if (_queueOpen) await CloseQueueAsync();
        if (_current is { IsSettingsOpen: true }) await GoBackAsync();

        var pageSession = _pages.Single(p => ReferenceEquals(p.Page, _searchPage));
        var query = GetStringParam(parameters, "query");
        var domain = GetStringParam(parameters, "target") ?? GetStringParam(parameters, "domain") ?? "music";
        var tab = GetStringParam(parameters, "tab");

        if (!string.IsNullOrWhiteSpace(query))
        {
            SearchTargetPicker.SelectedValue = domain;
            _suppressSearch = true;
            try { SearchBox.Text = query; } finally { _suppressSearch = false; }
            pageSession.SearchDraft = query;
            await ActivatePageAsync(pageSession, token => _searchPage.SubmitAsync(query.Trim(), domain, token));
        }
        else
        {
            await ActivatePageAsync(pageSession);
        }
        if (!string.IsNullOrWhiteSpace(tab) && _searchPage is IDebugModuleAdapter searchAdapter)
            await searchAdapter.TryOpenDebugTargetAsync("search:" + tab, parameters, cancellationToken);
    }

    private async Task OpenSettingsDomainAsync(string domain, string? section, CancellationToken cancellationToken)
    {
        if (_playerDetailOpen) await ClosePlayerDetailAsync();
        if (_queueOpen) await CloseQueueAsync();
        var current = _current ?? _sessions[0];
        if (!current.IsSettingsOpen || _currentPage is not null)
        {
            LeavePublicPage();
            CancelActive();
            RetireHeaderPresentation();
            _settingsReturn = current;
            current.IsSettingsOpen = true;
        }
        if (domain == "general")
        {
            _generalSettingsOpen = _generalSettingsHost.Content is not null;
            SetStatus();
            ApplyPresentation();
            if (!string.IsNullOrWhiteSpace(section) && _generalSettingsHost.Content is GeneralSettingsView general)
            {
                if (!general.SelectArea(section))
                    throw DebugApiException.NotFound($"Unknown general settings section: '{section}'.");
            }
            return;
        }
        if (domain == "film" && string.Equals(section, "cache", StringComparison.OrdinalIgnoreCase))
        {
            await OpenSettingsDomainAsync("general", "cache", cancellationToken);
            return;
        }
        if (domain == "music" && string.Equals(section, "cache", StringComparison.OrdinalIgnoreCase))
        {
            await OpenSettingsDomainAsync("general", "cache", cancellationToken);
            return;
        }
        if (domain == "music" && string.Equals(section, "home-source", StringComparison.OrdinalIgnoreCase))
        {
            await OpenSettingsDomainAsync("general", "sources", cancellationToken);
            return;
        }

        var targetSession = FindSession(domain)
            ?? throw DebugApiException.NotFound($"Settings domain not found: '{domain}'.");
        _generalSettingsOpen = false;
        if (!ReferenceEquals(_current, targetSession))
        {
            if (_current is { } prev) prev.IsSettingsOpen = false;
            targetSession.IsSettingsOpen = true;
            PreparePresentation(targetSession);
        }
        else
        {
            targetSession.IsSettingsOpen = true;
        }
        SetStatus();
        ApplyPresentation();
        if (!targetSession.Initialized) await RunRequestAsync(new(targetSession));
        if (!string.IsNullOrWhiteSpace(section) && targetSession.Module is IDebugModuleAdapter adapter)
        {
            bool ok = await adapter.TryOpenDebugSettingsSectionAsync(section, cancellationToken);
            if (!ok) throw DebugApiException.NotFound($"Unknown settings section '{section}' in '{domain}'.");
        }
    }

    private async Task OpenModuleTargetAsync(string moduleId, string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var session = FindSession(moduleId) ?? throw DebugApiException.NotFound($"Module not found: '{moduleId}'.");
        if (_playerDetailOpen) await ClosePlayerDetailAsync();
        if (_queueOpen) await CloseQueueAsync();
        _generalSettingsOpen = false;
        if (_current is { } prev) prev.IsSettingsOpen = false;
        session.IsSettingsOpen = false;
        LeavePublicPage();
        PreparePresentation(session);
        if (!session.Initialized) await RunRequestAsync(new(session));
        if (session.Module is IDebugModuleAdapter adapter)
        {
            bool handled = await adapter.TryOpenDebugTargetAsync(target, parameters, cancellationToken);
            if (!handled) throw DebugApiException.NotFound($"Unsupported target '{target}' for module '{moduleId}'.");
            var location = session.Module.CaptureNavigationLocation();
            if (!string.IsNullOrEmpty(location))
            {
                var destination = new Destination(location, Location: location);
                CommitDestination(new(session, destination), destination);
                ApplyPresentation();
            }
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
            await GoBackAsync();
            return BuildUiStateOnUi();
        }, cancellationToken);

    public Task<object> NavigateForwardAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            DebugLog.Info(DebugCategories.UiNav, "debug.forward", "nav.forward");
            await GoForwardAsync();
            return BuildUiStateOnUi();
        }, cancellationToken);

    public Task<object> RefreshAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            DebugLog.Info(DebugCategories.UiNav, "debug.refresh", "nav.refresh");
            if (_currentPage is { Page: IDebugModuleAdapter pageAdapter })
            {
                var pageHandled = await pageAdapter.TryHandleDebugActionAsync("refresh", null, false, cancellationToken);
                if (pageHandled is not null) return new { refreshed = true, result = pageHandled, state = BuildUiStateOnUi() };
            }
            if (_current is { Module: IDebugModuleAdapter adapter } current)
            {
                var handled = await adapter.TryHandleDebugActionAsync("refresh", null, current.IsSettingsOpen, cancellationToken);
                if (handled is not null) return new { refreshed = true, result = handled, state = BuildUiStateOnUi() };
            }
            if (_current is { IsSettingsOpen: false, Current: { } dest } active)
                await RunRequestAsync(new(active, dest));
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
                var query = GetStringParam(parameters, "query");
                if (string.IsNullOrWhiteSpace(query))
                    throw DebugApiException.InvalidArgument("search.submit requires non-empty 'query'.");
                await OpenSearchTargetAsync(parameters, cancellationToken);
                outcome = new { submitted = true, query };
            }
            else if (normalized == "dialog.action")
            {
                outcome = HandleDialogAction(parameters);
            }
            else if (normalized == "tab.select" &&
                     string.Equals(GetStringParam(parameters, "group"), "settings-section", StringComparison.OrdinalIgnoreCase) &&
                     _current is { IsSettingsOpen: true } && _generalSettingsOpen &&
                     _generalSettingsHost.Content is GeneralSettingsView generalView)
            {
                var id = GetStringParam(parameters, "id");
                if (string.IsNullOrWhiteSpace(id) && GetIntParam(parameters, "index") is { } idx)
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
                     string.Equals(GetStringParam(parameters, "domain"), "general", StringComparison.OrdinalIgnoreCase))
            {
                var key = GetStringParam(parameters, "key") ?? throw DebugApiException.InvalidArgument("Missing 'key'.");
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                if (_generalSettingsHost.Content is not GeneralSettingsView gv || !await gv.TryDebugSetAsync(key, val))
                    throw DebugApiException.InvalidArgument($"Unsupported general setting '{key}'.");
                outcome = new { saved = true, domain = "general", key };
            }
            else if (normalized == "setting.action" &&
                     string.Equals(GetStringParam(parameters, "domain"), "general", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = GetStringParam(parameters, "command") ?? throw DebugApiException.InvalidArgument("Missing 'command'.");
                if (string.Equals(cmd, "reset_user_data", StringComparison.OrdinalIgnoreCase))
                {
                    var confirm = GetStringParam(parameters, "confirm");
                    if (confirm != "RESET_ALL_USER_DATA")
                        throw DebugApiException.InvalidState("Destructive reset requires confirm='RESET_ALL_USER_DATA'.");
                    ConfirmUserDataReset();
                    outcome = new { dialogOpened = true };
                }
                else throw DebugApiException.InvalidArgument($"Unsupported general settings command '{cmd}'.");
            }
            else
            {
                IDebugModuleAdapter? targetAdapter = null;
                bool settingsOpen = false;
                if (normalized is "setting.set" or "setting.action" && GetStringParam(parameters, "domain") is { Length: > 0 } dom)
                {
                    targetAdapter = FindAdapter(dom);
                    settingsOpen = true;
                }
                else if (_currentPage is { Page: IDebugModuleAdapter pageAdapter })
                {
                    targetAdapter = pageAdapter;
                }
                else if (_current is { Module: IDebugModuleAdapter modAdapter } current)
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

    private object HandleDialogAction(JsonElement? parameters)
    {
        var act = (GetStringParam(parameters, "action") ?? "dismiss").Trim().ToLowerInvariant();
        var btnIndex = GetIntParam(parameters, "index");
        var btnLabel = GetStringParam(parameters, "label")?.Trim();
        if (act == "button")
        {
            if (ResetDialog.IsOpen)
            {
                if (btnIndex == 0 || string.Equals(btnLabel, "取消", StringComparison.OrdinalIgnoreCase)) act = "cancel";
                else if (btnIndex == 1 || string.Equals(btnLabel, "确认清除", StringComparison.OrdinalIgnoreCase)) act = "confirm";
            }
            else if (CloseDialog.IsOpen)
            {
                if (btnIndex == 0 || string.Equals(btnLabel, "取消", StringComparison.OrdinalIgnoreCase)) act = "cancel";
                else if (btnIndex == 1 || string.Equals(btnLabel, "最小化到托盘", StringComparison.OrdinalIgnoreCase)) act = "tray";
                else if (btnIndex == 2 || string.Equals(btnLabel, "退出应用", StringComparison.OrdinalIgnoreCase)) act = "exit";
            }
            else if (ChannelDialogHost.IsOpen && (btnIndex == 0 || string.Equals(btnLabel, "关闭", StringComparison.OrdinalIgnoreCase)))
            {
                act = "dismiss";
            }
        }
        if (ResetDialog.IsOpen)
        {
            if (act is "cancel" or "dismiss")
            {
                CancelUserDataReset();
                return new { dialog = "user-data-reset", action = "cancel" };
            }
            if (act == "confirm")
            {
                if (GetStringParam(parameters, "confirm") != "RESET_ALL_USER_DATA")
                    throw DebugApiException.InvalidState("Confirming user data reset requires confirm='RESET_ALL_USER_DATA'.");
                OnUserDataResetConfirmed();
                return new { dialog = "user-data-reset", action = "confirm" };
            }
        }
        if (CloseDialog.IsOpen)
        {
            if (act is "cancel" or "dismiss")
            {
                DismissCloseChoice(restoreFocus: false);
                return new { dialog = "close-window", action = "cancel" };
            }
            if (act is "confirm" or "exit")
            {
                OnCloseChoiceRequested(WindowCloseBehavior.Exit);
                return new { dialog = "close-window", action = "exit" };
            }
            if (act is "tray" or "minimize")
            {
                OnCloseChoiceRequested(WindowCloseBehavior.MinimizeToTray);
                return new { dialog = "close-window", action = "tray" };
            }
        }
        if (ChannelDialogHost.IsOpen && act is "cancel" or "dismiss")
        {
            ChannelDialogHost.Close();
            return new { dialog = "channel-dialog", action = "dismiss" };
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
                    bool busy = _state.IsBusy || _activeRequest is not null || _pageRequest is not null;
                    if (!busy) { matched = true; snapshot = BuildUiStateOnUi(); }
                    break;
                }
                case "route":
                {
                    var wantedTarget = GetStringParam(parameters, "target");
                    var wantedRoute = GetStringParam(parameters, "route");
                    if (wantedTarget is null && wantedRoute is null)
                        throw DebugApiException.InvalidArgument("Wait condition 'route' requires 'target' or 'route' in params.");
                    var stateObj = BuildUiStateOnUi();
                    var stateJson = JsonSerializer.SerializeToElement(stateObj);
                    var curTarget = stateJson.TryGetProperty("target", out var tp) ? tp.GetString() : null;
                    var curRoute = stateJson.TryGetProperty("route", out var rp) ? rp.GetString() : null;
                    var curDomain = stateJson.TryGetProperty("settingsDomain", out var dp) ? dp.GetString() : null;
                    var curSection = stateJson.TryGetProperty("settingsSection", out var sp) ? sp.GetString() : null;
                    bool targetMatches = wantedTarget is not null && (
                        string.Equals(curTarget, wantedTarget, StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(wantedTarget, "settings.cache", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(curDomain, "general", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(curSection, "cache", StringComparison.OrdinalIgnoreCase)) ||
                        (string.Equals(wantedTarget, "settings.media-controls", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(curDomain, "general", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(curSection, "shortcuts", StringComparison.OrdinalIgnoreCase)));
                    if (targetMatches ||
                        (wantedRoute is not null && string.Equals(curRoute, wantedRoute, StringComparison.OrdinalIgnoreCase)))
                    {
                        matched = true; snapshot = stateObj;
                    }
                    break;
                }
                case "section_count":
                {
                    int minSections = GetIntParam(parameters, "minSections") ?? 1;
                    int? sectionIndex = GetIntParam(parameters, "sectionIndex");
                    int minItems = GetIntParam(parameters, "minItems") ?? 0;
                    var content = await GetUiContentAsync(200, false, cancellationToken);
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
                    int minRows = GetIntParam(parameters, "minRows") ?? 1;
                    var content = await GetUiContentAsync(200, false, cancellationToken);
                    var json = JsonSerializer.SerializeToElement(content);
                    if (json.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() >= minRows)
                    { matched = true; snapshot = new { rows = rows.GetArrayLength() }; }
                    break;
                }
                case "playback_state":
                {
                    var domain = GetStringParam(parameters, "domain") ?? "music";
                    var wantedState = GetStringParam(parameters, "state") ?? "playing";
                    var pb = await GetPlaybackStateAsync(cancellationToken);
                    var json = JsonSerializer.SerializeToElement(pb);
                    if (json.TryGetProperty(domain, out var domObj) && domObj.ValueKind == JsonValueKind.Object &&
                        domObj.TryGetProperty("state", out var st) && string.Equals(st.GetString(), wantedState, StringComparison.OrdinalIgnoreCase))
                    { matched = true; snapshot = pb; }
                    break;
                }
                case "dialog":
                {
                    bool wantOpen = parameters is not { ValueKind: JsonValueKind.Object } dp ||
                        !dp.TryGetProperty("open", out var op) || op.ValueKind != JsonValueKind.False;
                    bool isOpen = ResetDialog.IsOpen || CloseDialog.IsOpen || ChannelDialogHost.IsOpen;
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

    public Task<object> GetPlaybackStateAsync(CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            object? music = FindAdapter("music") is { } m ? await m.GetDebugPlaybackStateAsync(cancellationToken) : null;
            object? film = FindAdapter("film") is { } f ? await f.GetDebugPlaybackStateAsync(cancellationToken) : null;
            string? activeOwner = null;
            if (music is not null && JsonSerializer.SerializeToElement(music).TryGetProperty("state", out var ms) &&
                ms.GetString() is "playing" or "buffering")
                activeOwner = "music";
            else if (film is not null && JsonSerializer.SerializeToElement(film).TryGetProperty("state", out var fs) &&
                fs.GetString() is "playing" or "buffering")
                activeOwner = "film";
            return new { activeOwner, music, film };
        }, cancellationToken);

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

    private static string? GetStringParam(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static int? GetIntParam(JsonElement? parameters, string name) =>
        parameters is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hWnd, out RectNative rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);
}
