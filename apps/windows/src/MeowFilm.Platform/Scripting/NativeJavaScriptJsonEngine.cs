using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Scripting;

namespace MeowFilm.Platform.Scripting;

/// <summary>
/// One native runtime, owned by one background thread. Rule strings are JSON
/// data, never concatenated with the application-owned program.
/// </summary>
public sealed class NativeJavaScriptJsonEngine : IJavaScriptJsonEngine
{
    // Resource boundaries for in-process execution, not limits on rule counts.
    private const nuint MemoryLimit = 64 * 1024 * 1024;
    private const nuint StackLimit = 512 * 1024;
    private static readonly long ExecutionTicks = Stopwatch.Frequency * 2;
    private readonly BlockingCollection<Work> queue = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] program;
    private readonly Native.Interrupt interrupt;
    private readonly object gate = new();
    private CancellationToken current;
    private long deadline;
    private bool closed, started;

    public NativeJavaScriptJsonEngine(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        program = Utf8(source);
        interrupt = _ => lifetime.IsCancellationRequested || current.IsCancellationRequested ||
            Stopwatch.GetTimestamp() >= deadline ? 1 : 0;
    }

    public Task<JsonElement> InvokeAsync(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var work = new Work(Utf8(JsonSerializer.Serialize(request)), cancellationToken);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (!started)
            {
                new Thread(Run, 2 * 1024 * 1024)
                {
                    IsBackground = true,
                    Name = "meowfilm native JavaScript"
                }.Start();
                started = true;
            }
            queue.Add(work);
        }
        // Completion means native execution has really stopped, not just that
        // the caller stopped waiting for a still-running C regular expression.
        return work.Completion.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!closed)
            {
                closed = true;
                lifetime.Cancel();
                queue.CompleteAdding();
                if (!started)
                {
                    queue.Dispose();
                    lifetime.Dispose();
                    completion.TrySetResult();
                }
            }
        }
        return new(completion.Task);
    }

    private void Run()
    {
        nint engine = 0;
        try
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                nint output = 0;
                try
                {
                    current = work.Cancellation;
                    ThrowIfCancelled();
                    deadline = Stopwatch.GetTimestamp() + ExecutionTicks;
                    if (engine == 0)
                        engine = Native.Create(program, (nuint)(program.Length - 1),
                            MemoryLimit, StackLimit, interrupt, 0);
                    ThrowIfInterrupted();
                    if (engine == 0) throw new JavaScriptEngineException("INITIALIZATION_FAILED");
                    int status = Native.Invoke(engine, work.Json, (nuint)(work.Json.Length - 1),
                        out output, out nuint length);
                    ThrowIfInterrupted();
                    if (status != 0 || output == 0 || length > int.MaxValue)
                        throw new JavaScriptEngineException("EXECUTION_FAILED");
                    var bytes = new byte[(int)length];
                    Marshal.Copy(output, bytes, 0, bytes.Length);
                    using var document = JsonDocument.Parse(bytes);
                    ThrowIfCancelled();
                    var result = document.RootElement.Clone();
                    FreeOutput();
                    work.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    Reset();
                    FreeOutput();
                    work.Completion.TrySetCanceled(current.IsCancellationRequested ? current : lifetime.Token);
                }
                catch (Exception error)
                {
                    Reset();
                    FreeOutput();
                    work.Completion.TrySetException(error is JavaScriptEngineException ? error :
                        new JavaScriptEngineException(error is DllNotFoundException or EntryPointNotFoundException
                            or BadImageFormatException ? "ENGINE_UNAVAILABLE" : "EXECUTION_FAILED"));
                }
                finally
                {
                    FreeOutput();
                }

                void FreeOutput()
                {
                    if (output != 0) Native.Free(output);
                    output = 0;
                }
            }
        }
        finally
        {
            Reset();
            // Queue admission is already closed. Dispose does not return until
            // all accepted work has a receipt and the native runtime is freed.
            queue.Dispose();
            lifetime.Dispose();
            completion.TrySetResult();
        }

        void Reset()
        {
            if (engine == 0) return;
            Native.Destroy(engine);
            engine = 0;
        }
    }

    private void ThrowIfCancelled()
    {
        current.ThrowIfCancellationRequested();
        lifetime.Token.ThrowIfCancellationRequested();
    }

    private void ThrowIfInterrupted()
    {
        ThrowIfCancelled();
        if (Stopwatch.GetTimestamp() >= deadline)
            throw new JavaScriptEngineException("EXECUTION_TIMEOUT");
    }

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private sealed record Work(byte[] Json, CancellationToken Cancellation)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static class Native
    {
        private const string Library = "meowfilm_js";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Interrupt(nint opaque);
        [DllImport(Library, EntryPoint = "mf_js_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Create(byte[] source, nuint length, nuint memoryLimit,
            nuint stackLimit, Interrupt interrupt, nint opaque);
        [DllImport(Library, EntryPoint = "mf_js_invoke", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Invoke(nint engine, byte[] json, nuint length, out nint output, out nuint outputLength);
        [DllImport(Library, EntryPoint = "mf_js_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(nint engine);
        [DllImport(Library, EntryPoint = "mf_js_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(nint output);
    }
}
