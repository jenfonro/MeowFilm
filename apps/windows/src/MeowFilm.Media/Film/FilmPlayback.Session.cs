using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MeowFilm.Media;

public sealed partial class FilmPlayback
{
    private sealed class Session
    {
        private readonly object gate = new();
        private readonly FilmKernelApi kernel;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenRegistration cancellation;
        private readonly INativeMediaResourceLease? lease;
        private readonly FFmpegHttpTransport transport;
        private readonly Delegate[] callbacks;
        private FilmKernelNative.Callbacks table;
        private byte[] keys;
        private byte[]? defaultKey;
        private IntPtr owner;
        private Task? release;
        private string? callbackError;
        internal RuntimeMediaPlaybackIntent Intent { get; }
        internal NativePlaybackScope Scope { get; }
        internal Uri Address { get; }
        internal NativeVideoFormat Format { get; }
        internal CancellationToken Token { get; }
        internal string? Error => transport.LastErrorCode ?? Volatile.Read(ref callbackError);
        internal bool EndNotified;

        internal Session(FilmKernelApi kernel, RuntimeMediaPlaybackIntent intent, long generation, Uri address,
            NativeVideoFormat format, byte[] keys, byte[]? defaultKey,
            IReadOnlyDictionary<string, string> headers, INativeMediaResourceLease? lease,
            CancellationToken token)
        {
            this.kernel = kernel;
            Intent = intent; Scope = new("film", intent.Identity, generation);
            Address = address; Format = format; this.keys = keys; this.defaultKey = defaultKey;
            this.lease = lease;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lease?.Revoked ?? default);
            Token = lifetime.Token;
            FFmpegHttpTransport? createdTransport = null;
            try
            {
                transport = createdTransport = new FFmpegHttpTransport(address, headers, new NativeHttpMediaOptions(), lifetime.Token);
                callbacks = [new FilmKernelNative.OpenCallback(transport.Open),
                    new FilmKernelNative.LocationCallback(transport.Location),
                    new FilmKernelNative.ReadCallback((context, handle, output, length) =>
                        transport.Read(context, handle, output, length)),
                    new FilmKernelNative.SeekCallback((context, handle, offset, whence) =>
                        transport.Seek(context, handle, offset, whence)),
                    new FilmKernelNative.CloseCallback(transport.Close),
                    new FilmKernelNative.InterruptedCallback(_ => lifetime.IsCancellationRequested ? 1 : 0),
                    new FilmKernelNative.FailedCallback((_, error) =>
                        Interlocked.CompareExchange(ref callbackError, FilmKernelNative.Code(error), null))];
                table = new FilmKernelNative.Callbacks
                {
                    Size = (uint)Marshal.SizeOf<FilmKernelNative.Callbacks>(), Abi = 1,
                    Open = Marshal.GetFunctionPointerForDelegate(callbacks[0]),
                    Location = Marshal.GetFunctionPointerForDelegate(callbacks[1]),
                    Read = Marshal.GetFunctionPointerForDelegate(callbacks[2]),
                    Seek = Marshal.GetFunctionPointerForDelegate(callbacks[3]),
                    Close = Marshal.GetFunctionPointerForDelegate(callbacks[4]),
                    Interrupted = Marshal.GetFunctionPointerForDelegate(callbacks[5]),
                    Failed = Marshal.GetFunctionPointerForDelegate(callbacks[6])
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

        internal IntPtr Handle
        {
            get { lock (gate) return owner != IntPtr.Zero ? owner : throw new PlaybackException(PlaybackError.Superseded); }
        }
        internal void Create()
        {
            lock (gate)
            {
                Token.ThrowIfCancellationRequested();
                try { owner = kernel.Create(ref table, Address, Format, keys, defaultKey); }
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
                if (owner != IntPtr.Zero) kernel.Cancel(owner);
        }
        internal Task ReleaseAsync()
        {
            lock (gate) return release ??= ReleaseCoreAsync();
        }
        private async Task ReleaseCoreAsync()
        {
            await Task.Yield();
            Cancel();
            IntPtr native;
            lock (gate) { native = owner; owner = IntPtr.Zero; }
            // No transport, callback or lease teardown until native has joined every worker.
            if (native != IntPtr.Zero)
                await Task.Run(() => kernel.Release(native)).ConfigureAwait(false);
            GC.KeepAlive(callbacks);
            try { transport.Dispose(); }
            finally
            {
                try { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    cancellation.Dispose(); lifetime.Dispose();
                    CryptographicOperations.ZeroMemory(keys);
                    if (defaultKey is not null) CryptographicOperations.ZeroMemory(defaultKey);
                }
            }
        }
    }
}
