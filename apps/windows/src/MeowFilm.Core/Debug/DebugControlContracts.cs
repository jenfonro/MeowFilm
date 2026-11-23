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
    string? Reason)
{
    public object ToDiagnosticSection()
    {
        if (!Available || StatusCode != 200 || Body is not { } body)
        {
            return new
            {
                available = false,
                httpStatus = StatusCode,
                reason = Reason ?? (StatusCode == 404 ? "NODE_DEBUG_ENDPOINT_UNAVAILABLE" : "NODE_DEBUG_ERROR")
            };
        }
        if (body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("ok", out var okProp) &&
            okProp.ValueKind == JsonValueKind.False)
        {
            var code = body.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            return new
            {
                available = false,
                httpStatus = StatusCode,
                reason = code ?? "NODE_DEBUG_ERROR"
            };
        }
        var payload = UnwrapNodeEnvelope(body);
        if (payload.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in payload.EnumerateObject())
                map[prop.Name] = prop.Value;
            map["available"] = true;
            return map;
        }
        return new { available = true, result = payload };
    }

    public static JsonElement? UnwrapValue(JsonElement? body) =>
        body is { } element ? UnwrapNodeEnvelope(element) : null;

    private static JsonElement UnwrapNodeEnvelope(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("value", out var valueProp)
            ? valueProp : element;
}

public sealed record DebugNodeInvokeResult(int StatusCode, JsonElement? JsonBody, string? RawText, double DurationMs);

public interface IDebugRuntimeHost
{
    bool IsConnected { get; }
    string? BaseUrl { get; }
    int? RuntimePid { get; }
    Task<DebugNodeInvokeResult> InvokeDebugAsync(string path, JsonElement? input, TimeSpan timeout, CancellationToken cancellationToken);
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
    Task<object> GetUiImagesAsync(CancellationToken cancellationToken);
    Task<byte[]> CaptureScreenshotPngAsync(CancellationToken cancellationToken);
    Task<byte[]> CaptureScreenshotPngAsync(string? window, CancellationToken cancellationToken) =>
        CaptureScreenshotPngAsync(cancellationToken);
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

public sealed record DebugImageSnapshot(
    string State,
    string Address,
    string? Slot,
    bool Admitted,
    bool HasSource,
    int PixelWidth,
    int PixelHeight,
    bool CanRetry,
    string? ErrorCode);

public interface IDebugImageVisual
{
    DebugImageSnapshot DescribeDebugImage();
}

/// <summary>Implemented by business modules/pages in MeowFilm.Capabilities and discovered by ShellDebugAdapter.</summary>
public interface IDebugModuleAdapterSource
{
    IDebugModuleAdapter DebugAdapter { get; }
}

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
    Task<object?> TryHandleDebugOverlayActionAsync(string target, string action, JsonElement? parameters, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
    Task<byte[]?> CaptureDebugAuxiliaryWindowPngAsync(string window, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);
    Task<object?> GetDebugPlaybackStateAsync(CancellationToken cancellationToken);
    Task<object?> TryDebugPlaybackCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken);
    Task<object?> TryDebugNovelCommandAsync(string command, JsonElement? parameters, CancellationToken cancellationToken);
}
