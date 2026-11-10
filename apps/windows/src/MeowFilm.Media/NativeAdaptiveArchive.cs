using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowFilm.Media;

/// <summary>Finite native VOD compiler/reader, not a scheduler, filesystem owner or download service.
/// One owned attempt
/// produces one reproducible stream; URLs, headers and clear-text upstream validators are not archived.</summary>
public sealed class NativeAdaptiveArchive : IAsyncDisposable
{
    internal static ReadOnlySpan<byte> Magic => "MEOWVOD1"u8;
    internal static readonly Uri ArchiveOrigin = new("http://127.0.0.1:1/");
    internal const int MaximumIndexBytes = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8
    };
    internal sealed record Entry(string Path, int Kind, long Length, string? Manifest, string Binding);
    internal sealed record Index(int Version, string Root, Entry[] Entries);
    private readonly CancellationTokenSource lifetime;
    private readonly AdaptiveHttpTransport transport;
    private readonly AdaptiveManifest graph;
    private readonly List<(Entry Entry, AdaptiveResource Resource)> resources = [];
    private byte[] header = [];
    private ArchiveStream? reader;
    private readonly object sync = new();
    private Task? disposal;
    public long Length { get; private set; }
    public string Validator { get; private set; } = "";

    private NativeAdaptiveArchive(IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, CancellationToken token)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        transport = new(options, new(headers, StringComparer.OrdinalIgnoreCase), lifetime.Token);
        graph = new(transport, ArchiveOrigin, deterministic: true);
    }
    public static async Task<NativeAdaptiveArchive> CreateAsync(byte[] manifest, Uri resolvedAddress,
        IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, long maximumBytes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(manifest); ArgumentNullException.ThrowIfNull(options);
        if (maximumBytes is <= 0 or > ReadLeaseMediaInput.MaximumLength)
            throw new PlaybackException(PlaybackError.AdaptiveBudget);
        // Validate/copy headers using the same public playback boundary; never accept VLC options.
        var safeHeaders = HttpMediaInput.ValidateRequest(resolvedAddress, headers, allowAdaptive: true);
        var plan = new NativeAdaptiveArchive(safeHeaders, options, token);
        try
        {
            var root = await plan.graph.BuildAsync(manifest, resolvedAddress, plan.lifetime.Token).ConfigureAwait(false);
            long payloadBytes = 0;
            foreach (var pair in plan.graph.Resources)
            {
                plan.lifetime.Token.ThrowIfCancellationRequested();
                var resource = pair.Value;
                if (resource.Manifest is { } text)
                {
                    plan.resources.Add((new(pair.Key, (int)resource.Kind, text.Length, Encoding.UTF8.GetString(text), ""), resource));
                    continue;
                }
                var response = await plan.transport.ProbePayloadAsync(resource, plan.lifetime.Token).ConfigureAwait(false);
                if (response.ETag is not { } tag) throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                payloadBytes = checked(payloadBytes + response.Length);
                if (payloadBytes > maximumBytes) throw new PlaybackException(PlaybackError.AdaptiveBudget);
                // A validator is scoped to its exact origin+URI, not interchangeable across signed links.
                string binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { Uri = resource.Uri.AbsoluteUri, Tag = tag, response.Total,
                        resource.Offset, resource.Count }))));
                if (response.EncryptionBinding is { } encryptionBinding)
                    binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(binding + ":" + encryptionBinding)));
                plan.resources.Add((new(pair.Key, (int)resource.Kind, response.Length, null, binding), resource));
            }
            byte[] index = JsonSerializer.SerializeToUtf8Bytes(new Index(1, root.AbsolutePath,
                plan.resources.Select(r => r.Entry).ToArray()), Json);
            if (index.Length > MaximumIndexBytes) throw new PlaybackException(PlaybackError.AdaptiveBudget);
            plan.header = new byte[12 + index.Length];
            Magic.CopyTo(plan.header); BinaryPrimitives.WriteInt32LittleEndian(plan.header.AsSpan(8), index.Length);
            index.CopyTo(plan.header, 12);
            plan.Length = checked(plan.header.Length + payloadBytes);
            if (plan.Length > maximumBytes) throw new PlaybackException(PlaybackError.AdaptiveBudget);
            plan.Validator = "\"meow-vod-" + Convert.ToHexStringLower(SHA256.HashData(plan.header)) + "\"";
            return plan;
        }
        catch { await plan.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <summary>One reader per prepared attempt. Resume revalidates the entire graph/ETag set.
    /// At a partial segment, at most that segment is replayed through the structural validator;
    /// completed segments are not downloaded again. No complete media buffering is performed.</summary>
    public Task<Stream> OpenReadAsync(long offset, CancellationToken token = default)
    {
        lock (sync)
        {
            lifetime.Token.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
            if (offset < 0 || offset > Length || reader is not null || disposal is not null)
                throw new PlaybackException(PlaybackError.InvalidRequest);
            reader = new(this, offset, token);
            return Task.FromResult<Stream>(reader);
        }
    }
    public ValueTask DisposeAsync() { lock (sync) return new(disposal ??= ReleaseAsync()); }
    private async Task ReleaseAsync()
    {
        lifetime.Cancel();
        if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
        transport.Dispose(); graph.Clear(); resources.Clear(); Array.Clear(header); lifetime.Dispose();
    }
    private sealed class ArchiveStream : Stream
    {
        private readonly NativeAdaptiveArchive plan;
        private readonly CancellationTokenSource cancel;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly object ownership = new();
        private readonly byte[] buffer = new byte[65536];
        private Task? release;
        private long position, resourceStart, resourceRead;
        private int resourceIndex = -1, disposed;
        private AdaptiveHttpTransport.Response? response;
        private MemoryStream? init;
        private AdaptivePayloadValidator? validator;
        internal ArchiveStream(NativeAdaptiveArchive plan, long position, CancellationToken token)
        { this.plan = plan; this.position = position; cancel = CancellationTokenSource.CreateLinkedTokenSource(plan.lifetime.Token, token); }
        public override bool CanRead => disposed == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => plan.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, token);
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            // A response can span many reads. Its original caller token belongs to the
            // opening read, so each continuation must abort the currently held response
            // on its own cancellation (including stream disposal), not wait for idle timeout.
            using var abort = linked.Token.Register(() => response?.Dispose());
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (disposed != 0) throw new ObjectDisposedException(nameof(ArchiveStream));
                if (destination.IsEmpty || position == plan.Length) return 0;
                if (position < plan.header.Length)
                {
                    int count = (int)Math.Min(Math.Min(65536, destination.Length), plan.header.Length - position);
                    plan.header.AsMemory((int)position, count).CopyTo(destination); position += count; return count;
                }
                long start = plan.header.Length;
                int index = 0;
                for (; index < plan.resources.Count; index++)
                {
                    var item = plan.resources[index].Entry;
                    if (item.Manifest is not null) continue;
                    if (position < start + item.Length) break;
                    start += item.Length;
                }
                var (entry, resource) = plan.resources[index];
                if (resourceIndex != index)
                {
                    response?.Dispose(); init?.Dispose(); init = null;
                    response = await plan.transport.OpenPayloadAsync(resource, 0, null, linked.Token).ConfigureAwait(false);
                    if (response.Length != entry.Length) throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                    resourceIndex = index; resourceStart = start; resourceRead = 0;
                    validator = new((AdaptiveResourceKind)entry.Kind, entry.Length);
                    if (resource.Kind == AdaptiveResourceKind.Mp4Init)
                    {
                        byte[] bytes = new byte[checked((int)entry.Length)];
                        int read = 0;
                        while (read < bytes.Length) read += await response.ReadAsync(bytes.AsMemory(read)).ConfigureAwait(false);
                        AdaptiveMediaSession.ValidateInit(bytes);
                        init = new MemoryStream(bytes, writable: false);
                        response.Dispose(); response = null;
                    }
                }
                while (resourceRead < position - resourceStart)
                {
                    int skip = (int)Math.Min(buffer.Length, position - resourceStart - resourceRead);
                    int n = await ReadResourceAsync(buffer.AsMemory(0, skip), linked.Token).ConfigureAwait(false);
                    if (n == 0) throw new PlaybackException(PlaybackError.HttpTruncated);
                }
                int wanted = (int)Math.Min(Math.Min(65536, destination.Length), entry.Length - resourceRead);
                int countRead = await ReadResourceAsync(destination[..wanted], linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                position += countRead; return countRead;
            }
            catch (Exception) when (linked.IsCancellationRequested)
            { throw new OperationCanceledException(linked.Token); }
            finally { gate.Release(); }
        }
        private async Task<int> ReadResourceAsync(Memory<byte> destination, CancellationToken token)
        {
            int n = init is not null ? await init.ReadAsync(destination, token).ConfigureAwait(false) :
                await response!.ReadAsync(destination).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (n <= 0) throw new PlaybackException(PlaybackError.HttpTruncated);
            if (init is null) validator!.Append(destination.Span[..n]);
            resourceRead += n; return n;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask DisposeAsync()
        { lock (ownership) return new(release ??= ReleaseAsync()); }
        private async Task ReleaseAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            cancel.Cancel(); await gate.WaitAsync().ConfigureAwait(false);
            try { response?.Dispose(); init?.Dispose(); Array.Clear(buffer); cancel.Dispose(); }
            finally { gate.Release(); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
    }
}

// Same bounded top-level fMP4 box whitelist as the online native-session path.
internal sealed class AdaptivePayloadValidator(AdaptiveResourceKind kind, long length)
{
    private readonly byte[] header = new byte[16];
    private int filled, wanted = 8;
    private long position, body;
    internal void Append(ReadOnlySpan<byte> bytes)
    {
        if (kind == AdaptiveResourceKind.TransportStream)
        {
            for (int i = 0; i < bytes.Length; i++)
                if (position + i is 0 or 188 && bytes[i] != 0x47) Reject();
            position += bytes.Length;
            if (position == length && length < 376) Reject();
            return;
        }
        while (!bytes.IsEmpty)
        {
            if (body > 0)
            {
                int skip = (int)Math.Min(body, bytes.Length);
                body -= skip; position += skip; bytes = bytes[skip..]; continue;
            }
            int copy = Math.Min(wanted - filled, bytes.Length);
            bytes[..copy].CopyTo(header.AsSpan(filled)); filled += copy; bytes = bytes[copy..]; position += copy;
            if (filled < wanted) continue;
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string box = Encoding.ASCII.GetString(header, 4, 4);
            if (box is not ("styp" or "moof" or "mdat" or "sidx" or "free")) Reject();
            if (size == 1 && wanted == 8) { wanted = 16; continue; }
            if (size == 1) size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            if (size == 0 && box == "mdat") size = (ulong)(length - position + wanted);
            if (size < (ulong)wanted || size > (ulong)(length - position + wanted)) Reject();
            body = (long)size - wanted; filled = 0; wanted = 8;
        }
        if (position == length && (body != 0 || filled != 0)) Reject();
    }
    private static void Reject() => throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
}
