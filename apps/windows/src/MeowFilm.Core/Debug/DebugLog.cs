using System.Collections.Concurrent;
using System.Text.Json;

namespace MeowFilm.Core.Debug;

public sealed record DebugCorrelation(string? OpId, string? TraceId);

public sealed record DebugRuntimeHandshake(
    bool Enabled,
    string SessionId,
    string SessionDir,
    int InlineLimit,
    int EventBytesLimit,
    IReadOnlyList<string> Modules);

public interface IDebugLogger
{
    bool IsEnabled { get; }
    long CurrentSeq { get; }
    string SessionId { get; }
    string SessionDir { get; }
    bool IsCategoryEnabled(string category);
    DebugRuntimeHandshake CreateRuntimeHandshake();
    long Write(string level, string category, string sub, string @event,
        object? data = null, string? opId = null, string? traceId = null,
        double? durationMs = null, Exception? error = null, object? errorDetails = null);
    Task<JsonElement?> WaitForEventAsync(string category, string? sub, string? @event,
        long? sinceSeq, CancellationToken cancellationToken);
}

public static class DebugLog
{
    private static readonly AsyncLocal<DebugCorrelation?> CurrentCorrelation = new();
    private static long opCounter;

    public static IDebugLogger? Current { get; set; }

    public static bool IsEnabled => Current is { IsEnabled: true };

    public static DebugCorrelation? Correlation => CurrentCorrelation.Value;

    public static string NextOpId() => $"op_{Interlocked.Increment(ref opCounter):D6}";

    public static string NextTraceId() => "tr_" + Guid.NewGuid().ToString("N")[..12];

    public static IDisposable PushCorrelation(string? opId = null, string? traceId = null)
    {
        var previous = CurrentCorrelation.Value;
        var next = new DebugCorrelation(
            opId ?? previous?.OpId,
            traceId ?? previous?.TraceId);
        CurrentCorrelation.Value = next;
        return new CorrelationScope(previous);
    }

    public static long Trace(string category, string sub, string @event, object? data = null,
        string? opId = null, string? traceId = null, double? durationMs = null) =>
        Write("trace", category, sub, @event, data, opId, traceId, durationMs, null, null);

    public static long Debug(string category, string sub, string @event, object? data = null,
        string? opId = null, string? traceId = null, double? durationMs = null) =>
        Write("debug", category, sub, @event, data, opId, traceId, durationMs, null, null);

    public static long Info(string category, string sub, string @event, object? data = null,
        string? opId = null, string? traceId = null, double? durationMs = null) =>
        Write("info", category, sub, @event, data, opId, traceId, durationMs, null, null);

    public static long Warn(string category, string sub, string @event, object? data = null,
        string? opId = null, string? traceId = null, double? durationMs = null, Exception? error = null, object? errorDetails = null) =>
        Write("warn", category, sub, @event, data, opId, traceId, durationMs, error, errorDetails);

    public static long Error(string category, string sub, string @event, object? data = null,
        Exception? error = null, object? errorDetails = null, string? opId = null, string? traceId = null, double? durationMs = null) =>
        Write("error", category, sub, @event, data, opId, traceId, durationMs, error, errorDetails);

    public static long Fatal(string category, string sub, string @event, object? data = null,
        Exception? error = null, object? errorDetails = null, string? opId = null, string? traceId = null, double? durationMs = null) =>
        Write("fatal", category, sub, @event, data, opId, traceId, durationMs, error, errorDetails);

    public static long Write(string level, string category, string sub, string @event,
        object? data = null, string? opId = null, string? traceId = null,
        double? durationMs = null, Exception? error = null, object? errorDetails = null)
    {
        var logger = Current;
        if (logger is not { IsEnabled: true } || !logger.IsCategoryEnabled(category)) return 0;
        var corr = CurrentCorrelation.Value;
        return logger.Write(level, category, sub, @event, data,
            opId ?? corr?.OpId, traceId ?? corr?.TraceId, durationMs, error, errorDetails);
    }

    private sealed class CorrelationScope(DebugCorrelation? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CurrentCorrelation.Value = previous;
        }
    }
}

/// <summary>Neutral registry for active native/media/cache lease snapshots reported by diag/summary.</summary>
public static class DebugLeaseRegistry
{
    private static readonly ConcurrentDictionary<string, Func<object?>> Providers = new(StringComparer.Ordinal);

    public static IDisposable Register(string name, Func<object?> provider)
    {
        Providers[name] = provider;
        return new Registration(name, provider);
    }

    public static Dictionary<string, object?> Snapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, provider) in Providers)
        {
            try { result[key] = provider(); }
            catch (Exception ex) { result[key] = new { error = ex.GetType().Name, message = ex.Message }; }
        }
        return result;
    }

    private sealed class Registration(string name, Func<object?> provider) : IDisposable
    {
        public void Dispose()
        {
            Providers.TryRemove(new KeyValuePair<string, Func<object?>>(name, provider));
        }
    }
}
