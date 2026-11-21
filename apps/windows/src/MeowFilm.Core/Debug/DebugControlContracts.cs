using System.Text.Json;

namespace MeowFilm.Core.Debug;

public static class DebugErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string NotFound = "NOT_FOUND";
    public const string UnsupportedOnPlatform = "UNSUPPORTED_ON_PLATFORM";
    public const string InvalidState = "INVALID_STATE";
    public const string Timeout = "TIMEOUT";
    public const string RuntimeUnavailable = "RUNTIME_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed class DebugApiException(int httpStatus, string code, string message, object? details = null)
    : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;
    public string Code { get; } = code;
    public object? Details { get; } = details;

    public static DebugApiException InvalidArgument(string message, object? details = null) =>
        new(400, DebugErrorCodes.InvalidArgument, message, details);
    public static DebugApiException NotFound(string message, object? details = null) =>
        new(404, DebugErrorCodes.NotFound, message, details);
    public static DebugApiException Unsupported(string message, object? details = null) =>
        new(501, DebugErrorCodes.UnsupportedOnPlatform, message, details);
    public static DebugApiException InvalidState(string message, object? details = null) =>
        new(409, DebugErrorCodes.InvalidState, message, details);
    public static DebugApiException Timeout(string message, object? details = null) =>
        new(504, DebugErrorCodes.Timeout, message, details);
    public static DebugApiException RuntimeUnavailable(string message, object? details = null) =>
        new(503, DebugErrorCodes.RuntimeUnavailable, message, details);
    public static DebugApiException Internal(string message, object? details = null) =>
        new(500, DebugErrorCodes.InternalError, message, details);
}

public sealed record DebugNodeForwardResult(
    bool Available,
    int StatusCode,
    JsonElement? Body,
    string? Reason);

public interface IDebugRuntimeHost
{
    bool IsConnected { get; }
    string? BaseUrl { get; }
    int? RuntimePid { get; }
    Task<JsonElement> InvokeMethodAsync(string method, JsonElement? input, TimeSpan timeout, CancellationToken cancellationToken);
    Task<DebugNodeForwardResult> SendNodeDebugRequestAsync(HttpMethod method, string path, object? body,
        TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IDebugStorageQueryHost
{
    Task<object> ExecuteReadOnlyQueryAsync(string sql, JsonElement? parameters, int maxRows, CancellationToken cancellationToken);
}

public interface IDebugShellHost
{
    bool IsWindowReady { get; }
    IReadOnlyList<string> SupportedTargets { get; }
    IReadOnlyList<string> SupportedActions { get; }
    Task<object> GetUiStateAsync(CancellationToken cancellationToken);
    Task<object> GetUiContentAsync(int maxItems, bool includeRaw, CancellationToken cancellationToken);
    Task<object> GetUiTreeAsync(int maxDepth, bool visibleOnly, CancellationToken cancellationToken);
    Task<byte[]> CaptureScreenshotPngAsync(CancellationToken cancellationToken);
    Task<object> OpenTargetAsync(string target, JsonElement? parameters, bool waitIdle, int timeoutMs, CancellationToken cancellationToken);
    Task<object> NavigateBackAsync(CancellationToken cancellationToken);
    Task<object> NavigateForwardAsync(CancellationToken cancellationToken);
    Task<object> RefreshAsync(CancellationToken cancellationToken);
    Task<object> PerformActionAsync(string action, JsonElement? parameters, bool waitIdle, int timeoutMs, CancellationToken cancellationToken);
    Task<object> WaitAsync(string condition, JsonElement? parameters, int timeoutMs, CancellationToken cancellationToken);
    Task<object> GetPlaybackStateAsync(CancellationToken cancellationToken);
    Task<object> PlaybackCommandAsync(string domain, string command, JsonElement? parameters, CancellationToken cancellationToken);
    Task<object> NovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken);
    Task<object> ProbeUiThreadAsync(CancellationToken cancellationToken);
}

/// <summary>Implemented by business modules/pages in MeowFilm.Capabilities and discovered by ShellWindow.</summary>
public interface IDebugModuleAdapter
{
    string DebugModuleId { get; }
    IReadOnlyList<string> SupportedDebugTargets { get; }
    string? DescribeCurrentTarget(bool settingsOpen, out string? settingsSection);
    Task<bool> TryOpenDebugTargetAsync(string target, JsonElement? parameters, CancellationToken cancellationToken);
    Task<bool> TryOpenDebugSettingsSectionAsync(string section, CancellationToken cancellationToken);
    Task<object?> BuildDebugContentAsync(bool settingsOpen, int maxItems, bool includeRaw, CancellationToken cancellationToken);
    Task<object?> BuildDebugOverlayContentAsync(string target, int maxItems, bool includeRaw, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
    Task<object?> TryHandleDebugActionAsync(string action, JsonElement? parameters, bool settingsOpen, CancellationToken cancellationToken);
    Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken);
    Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken);
    Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken);
}
