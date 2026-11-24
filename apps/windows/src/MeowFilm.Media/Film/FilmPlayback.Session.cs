using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MeowFilm.Core.Debug;

namespace MeowFilm.Media;

public sealed partial class FilmPlayback
{
    private sealed class Session
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenRegistration cancellation;
        private readonly INativeMediaResourceLease? lease;
        private readonly FFmpegHttpTransport transport;
        private readonly Delegate[] callbacks;
        private FilmKernelNative.Callbacks table;
        private byte[] keys;
        private byte[]? defaultKey;
        private IntPtr owner;
        private int activeBorrows;
        private bool releasing;
        private TaskCompletionSource? borrowsDrained;
        private Task? release;
        private string? callbackError;

        internal RuntimeMediaPlaybackIntent Intent { get; }
        internal NativePlaybackScope Scope { get; }
        internal Uri Address { get; }
        internal NativeVideoFormat Format { get; }
        internal CancellationToken Token { get; }
        internal bool EndNotified;

        // Only enrich IO-related native errors (OPEN_FAILED=1, READ_FAILED=4, SEEK_FAILED=5)
        // with transport.LastErrorCode so a recovered transient HTTP error never masks
        // subsequent device-lost, decode, audio, render, or format errors.
        internal string ResolveError(int nativeCode)
        {
            if (nativeCode is 1 or 4 or 5 && transport.LastErrorCode is { Length: > 0 } ioCode)
                return ioCode;
            if (nativeCode != 0)
                return FilmKernelNative.Code(nativeCode);
            return Volatile.Read(ref callbackError) ?? transport.LastErrorCode ?? PlaybackError.OpenFailed;
        }

        internal Session(RuntimeMediaPlaybackIntent intent, long generation, Uri address,
            NativeVideoFormat format, byte[] keys, byte[]? defaultKey,
            IReadOnlyDictionary<string, string> headers, INativeMediaResourceLease? lease,
            CancellationToken token)
        {
            Intent = intent; Scope = new("film", intent.Identity, generation);
            Address = address; Format = format; this.keys = keys; this.defaultKey = defaultKey;
            this.lease = lease;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lease?.Revoked ?? default);
            Token = lifetime.Token;
            FFmpegHttpTransport? createdTransport = null;
            try
            {
                transport = createdTransport = new FFmpegHttpTransport(address, headers, new NativeHttpMediaOptions(), lifetime.Token);
                callbacks = [
                    new FilmKernelNative.OpenCallback(transport.Open),
                    new FilmKernelNative.LocationCallback(transport.Location),
                    new FilmKernelNative.ReadCallback((context, handle, output, length) =>
                        transport.Read(context, handle, output, length)),
                    new FilmKernelNative.SeekCallback((context, handle, offset, whence) =>
                        transport.Seek(context, handle, offset, whence)),
                    new FilmKernelNative.CloseCallback(transport.Close),
                    new FilmKernelNative.InterruptedCallback(_ => lifetime.IsCancellationRequested ? 1 : 0),
                    new FilmKernelNative.FailedCallback((_, error) =>
                        Interlocked.CompareExchange(ref callbackError, FilmKernelNative.Code(error), null)),
                    new FilmKernelNative.LogCallback((_, level, componentPtr, messagePtr) =>
                        ForwardNativeLog(level, componentPtr, messagePtr))
                ];
                table = new FilmKernelNative.Callbacks
                {
                    Size = (uint)Marshal.SizeOf<FilmKernelNative.Callbacks>(),
                    Abi = FilmKernelNative.Abi,
                    Open = Marshal.GetFunctionPointerForDelegate(callbacks[0]),
                    Location = Marshal.GetFunctionPointerForDelegate(callbacks[1]),
                    Read = Marshal.GetFunctionPointerForDelegate(callbacks[2]),
                    Seek = Marshal.GetFunctionPointerForDelegate(callbacks[3]),
                    Close = Marshal.GetFunctionPointerForDelegate(callbacks[4]),
                    Interrupted = Marshal.GetFunctionPointerForDelegate(callbacks[5]),
                    Failed = Marshal.GetFunctionPointerForDelegate(callbacks[6]),
                    Log = Marshal.GetFunctionPointerForDelegate(callbacks[7])
                };
                cancellation = lifetime.Token.Register(CancelNative);
            }
            catch
            {
                try { createdTransport?.Dispose(); }
                finally { lifetime.Dispose(); }
                throw;
            }
        }

        private void ForwardNativeLog(int level, IntPtr componentPtr, IntPtr messagePtr)
        {
            try
            {
                if (!DebugLog.IsEnabled) return;
                string component = Marshal.PtrToStringUTF8(componentPtr) ?? "film-kernel";
                string message = Marshal.PtrToStringUTF8(messagePtr) ?? "";
                if (string.IsNullOrEmpty(message)) return;
                string lvl = level switch { 1 => "error", 2 => "warn", 3 => "info", _ => "debug" };
                DebugLog.Write(lvl, DebugCategories.FilmPlayback, component, "native.log",
                    new { identity = Scope.Identity, message });
            }
            catch
            {
                // Never allow a managed exception to cross the native C callback boundary.
            }
        }

        internal IntPtr Handle
        {
            get { lock (gate) return !releasing && owner != IntPtr.Zero ? owner : throw new PlaybackException(PlaybackError.Superseded); }
        }

        internal bool TryWithHandle(Action<IntPtr> action)
        {
            IntPtr native;
            lock (gate)
            {
                if (releasing || owner == IntPtr.Zero) return false;
                activeBorrows++;
                native = owner;
            }
            try
            {
                action(native);
                return true;
            }
            finally
            {
                lock (gate)
                {
                    if (--activeBorrows == 0 && releasing)
                        borrowsDrained?.TrySetResult();
                }
            }
        }

        internal bool TryWithHandle<T>(Func<IntPtr, T> action, out T result)
        {
            IntPtr native;
            lock (gate)
            {
                if (releasing || owner == IntPtr.Zero) { result = default!; return false; }
                activeBorrows++;
                native = owner;
            }
            try
            {
                result = action(native);
                return true;
            }
            finally
            {
                lock (gate)
                {
                    if (--activeBorrows == 0 && releasing)
                        borrowsDrained?.TrySetResult();
                }
            }
        }

        internal void Create()
        {
            lock (gate)
            {
                Token.ThrowIfCancellationRequested();
                try { owner = FilmKernelNative.Create(ref table, Address, Format, keys, defaultKey); }
                finally
                {
                    CryptographicOperations.ZeroMemory(keys);
                    if (defaultKey is not null) CryptographicOperations.ZeroMemory(defaultKey);
                    keys = []; defaultKey = null;
                }
            }
        }

        internal void Cancel()
        {
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void CancelNative()
        {
            transport.Cancel();
            lock (gate)
                if (owner != IntPtr.Zero) FilmKernelNative.Cancel(owner);
        }

        internal Task ReleaseAsync()
        {
            lock (gate) return release ??= ReleaseCoreAsync();
        }

        private async Task ReleaseCoreAsync()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            Cancel();
            Task? drainBorrows = null;
            lock (gate)
            {
                releasing = true;
                if (activeBorrows > 0)
                {
                    borrowsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    drainBorrows = borrowsDrained.Task;
                }
            }
            if (drainBorrows is not null)
                await drainBorrows.ConfigureAwait(false);
            IntPtr native;
            lock (gate) { native = owner; owner = IntPtr.Zero; }
            // Wait for actual worker/GPU/AVIO drain before releasing callbacks, transport or lease.
            if (native != IntPtr.Zero)
                await Task.Run(() => FilmKernelNative.Release(native)).ConfigureAwait(false);
            GC.KeepAlive(callbacks);
            try { transport.Dispose(); }
            finally
            {
                try
                {
                    if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception leaseError)
                {
                    // Local native & transport resources are fully drained; log remote RPC lease failure
                    // without poisoning future local playback sessions.
                    if (DebugLog.IsEnabled)
                        DebugLog.Warn(DebugCategories.FilmPlayback, "filmkernel", "lease.remote_release_failed",
                            new { identity = Scope.Identity }, error: leaseError);
                }
                finally
                {
                    cancellation.Dispose();
                    lifetime.Dispose();
                    CryptographicOperations.ZeroMemory(keys);
                    if (defaultKey is not null) CryptographicOperations.ZeroMemory(defaultKey);
                }
            }
        }
    }
}
