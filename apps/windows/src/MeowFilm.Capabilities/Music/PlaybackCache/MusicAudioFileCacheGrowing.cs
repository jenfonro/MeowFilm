using System.IO;
using MeowFilm.Media;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed partial class MusicAudioFileCache
{
    internal sealed partial class PreparedFile
    {
        private readonly object bytesGate = new();
        private readonly TaskCompletionSource<(string Path, long Length)?> Available =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource changed = NewSignal();
        private string? path;
        private long length, received, visiblePrefix;
        private int opened;
        private int readers;
        private bool bodyComplete, published, workFinished;
        internal bool KeepsFile { get { lock (bytesGate) return bodyComplete && (disposed == 0 || readers > 0); } }
        private Exception? failure;
        internal bool Opened => Volatile.Read(ref opened) != 0;
        internal double BufferedFraction
        {
            get { lock (bytesGate) return published ? 1 : length > 0 ? (double)visiblePrefix / length : 0; }
        }
        internal event Action? ProgressChanged;
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void BeginFile(string file, long? total)
        {
            lock (bytesGate)
            {
                path = file;
                length = total ?? 0;
                if (total is { } known) Available.TrySetResult((file, known));
            }
            Pulse();
        }
        internal void ReceivedBytes(long count)
        {
            lock (bytesGate)
            {
                received = count;
                // The final write is not a successful EOF/inspection/commit.
                if (length > 0 && count < length) visiblePrefix = count;
            }
            Pulse();
        }
        internal void CompleteBody(long count)
        {
            lock (bytesGate)
            {
                received = length = count; bodyComplete = true;
                Available.TrySetResult((path!, count));
            }
            Pulse();
        }
        internal void Published()
        {
            lock (bytesGate) published = true;
            Pulse();
        }
        internal void SourceFailed(Exception error)
        {
            lock (bytesGate)
                if (!bodyComplete) failure ??= error;
            Pulse();
        }
        internal void FinishWork()
        {
            lock (bytesGate)
            {
                workFinished = true;
                if (!bodyComplete && !SourceToken.IsCancellationRequested)
                    failure ??= new IOException(PlaybackError.HttpTruncated);
                Available.TrySetResult(null); Ready.TrySetResult(null);
            }
            Pulse();
            CleanupRetiredFile();
        }
        private void CleanupRetiredFile()
        {
            string? remove;
            lock (bytesGate)
            {
                remove = workFinished && !published && readers == 0 && disposed != 0 ? path : null;
                if (remove is not null) path = null;
            }
            if (remove is not null)
                try { File.Delete(remove); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        private void CheckSource()
        {
            SourceToken.ThrowIfCancellationRequested();
            lock (bytesGate)
                if (failure is { } error)
                    throw error is PlaybackException known ? known : new IOException(PlaybackError.ReadFailed, error);
        }
        internal void Pulse()
        {
            TaskCompletionSource previous;
            lock (bytesGate) { previous = changed; changed = NewSignal(); }
            previous.TrySetResult();
            ProgressChanged?.Invoke();
        }

        private sealed class GrowingReadLease(PreparedFile owner, FileStream file, long length,
            CancellationToken revoked) : INativeMediaReadLease
        {
            private int released;
            public Stream Content { get; } = new GrowingStream(owner, file, length, revoked);
            public long Length => length;
            public CancellationToken Revoked => revoked;
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                await Content.DisposeAsync().ConfigureAwait(false);
                lock (owner.bytesGate) owner.readers--;
                owner.CleanupRetiredFile();
            }
        }
        private sealed class GrowingStream(PreparedFile owner, FileStream file, long length,
            CancellationToken revoked) : Stream
        {
            private long position;
            private bool disposed;
            public override bool CanRead => !disposed;
            public override bool CanSeek => !disposed;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
            public override long Seek(long offset, SeekOrigin origin)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                revoked.ThrowIfCancellationRequested(); owner.CheckSource();
                long next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position,
                    SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
                if (next < 0 || next > length) throw new IOException("CACHE_READ_SEEK");
                file.Position = position = next;
                return position; // No producer cancellation, seek or network request.
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked, owner.SourceToken);
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested(); owner.CheckSource();
                    if (buffer.Length == 0 || position == length) return 0;
                    long available;
                    Task signal;
                    lock (owner.bytesGate) { available = owner.received - position; signal = owner.changed.Task; }
                    if (available > 0)
                    {
                        int count = (int)Math.Min(buffer.Length, Math.Min(available, length - position));
                        int read = await file.ReadAsync(buffer[..count], linked.Token).ConfigureAwait(false);
                        linked.Token.ThrowIfCancellationRequested(); owner.CheckSource();
                        if (read <= 0) throw new IOException(PlaybackError.ReadTruncated);
                        position += read;
                        return read;
                    }
                    await signal.WaitAsync(linked.Token).ConfigureAwait(false);
                }
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { disposed = true; if (disposing) file.Dispose(); base.Dispose(disposing); }
            public override async ValueTask DisposeAsync() { disposed = true; await file.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
        }
    }
}
