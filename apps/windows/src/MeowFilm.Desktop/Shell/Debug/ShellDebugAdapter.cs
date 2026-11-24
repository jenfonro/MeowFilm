using System.Diagnostics;
using System.Text.Json;
using MeowFilm.Core.Debug;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Desktop;

internal sealed partial class ShellDebugAdapter(ShellWindow shell) : IDebugShellHost
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

    private sealed record PlaybackSnapshot(JsonElement? Music, JsonElement? Film, object Payload);

    private async Task<PlaybackSnapshot> GetPlaybackSnapshotOnUiAsync(CancellationToken cancellationToken)
    {
        object? music = FindAdapter("music") is { } m ? await m.GetDebugPlaybackStateAsync(cancellationToken) : null;
        object? film = FindAdapter("film") is { } f ? await f.GetDebugPlaybackStateAsync(cancellationToken) : null;
        JsonElement? musicJson = music is not null ? JsonSerializer.SerializeToElement(music) : null;
        JsonElement? filmJson = film is not null ? JsonSerializer.SerializeToElement(film) : null;
        string? activeOwner = shell.ActivePlaybackOwnerProvider?.Invoke();
        if (string.IsNullOrEmpty(activeOwner))
        {
            if (musicJson is { ValueKind: JsonValueKind.Object } mj && mj.TryGetProperty("state", out var ms) &&
                ms.GetString() is "playing" or "buffering")
                activeOwner = "music";
            else if (filmJson is { ValueKind: JsonValueKind.Object } fj && fj.TryGetProperty("state", out var fs) &&
                fs.GetString() is "playing" or "buffering")
                activeOwner = "film";
            else if (musicJson is { ValueKind: JsonValueKind.Object } mPaused && mPaused.TryGetProperty("state", out var mps) &&
                mps.GetString() is "paused" or "error" &&
                mPaused.TryGetProperty("track", out var mTrack) && mTrack.ValueKind == JsonValueKind.Object)
                activeOwner = "music";
            else if (filmJson is { ValueKind: JsonValueKind.Object } fPaused && fPaused.TryGetProperty("state", out var fps) &&
                fps.GetString() is "paused" or "error")
                activeOwner = "film";
        }
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
            return new { domain = normalized, command, state = res };
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
