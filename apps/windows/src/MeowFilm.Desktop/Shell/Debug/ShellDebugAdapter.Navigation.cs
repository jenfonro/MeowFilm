using System.Diagnostics;
using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Desktop;

internal sealed partial class ShellDebugAdapter
{
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
            else if (FindPageSessionByTarget(normalized) is { } matchedPage)
            {
                await OpenPageTargetAsync(matchedPage, normalized, parameters, cancellationToken);
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

    private ShellPageSession? FindPageSessionByTarget(string target)
    {
        foreach (var page in shell.Pages)
        {
            if (page.Page is IDesktopSearchPage) continue;
            if (string.Equals(page.Page.Id, target, StringComparison.OrdinalIgnoreCase)) return page;
            if (AsAdapter(page.Page) is { } adapter &&
                adapter.SupportedDebugTargets.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase)))
                return page;
        }
        return null;
    }

    private async Task OpenPageTargetAsync(ShellPageSession pageSession, string target, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (shell.IsPlayerDetailOpen) await shell.ClosePlayerDetailAsync();
        if (shell.IsQueueOpen) await shell.CloseQueueAsync();
        if (shell.CurrentSession is { IsSettingsOpen: true }) await shell.GoBackAsync();

        if (!ReferenceEquals(shell.CurrentPage, pageSession))
            await shell.ActivatePageAsync(pageSession);

        if (AsAdapter(pageSession.Page) is { } adapter)
        {
            bool handled = await adapter.TryOpenDebugTargetAsync(target, parameters, cancellationToken);
            if (!handled) throw DebugApiException.NotFound($"Unsupported target '{target}' for page '{pageSession.Page.Id}'.");
        }
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
}
