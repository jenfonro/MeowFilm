using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MeowFilm.Media;

// Borrowed authorized stream only. There is deliberately no HttpClient, path opener,
// resolver callback or upstream URL in this resource catalog.
internal sealed class OfflineAdaptiveArchive(Stream content, long length)
{
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly Dictionary<string, (NativeAdaptiveArchive.Entry Entry, long Offset)> entries = new(StringComparer.Ordinal);
    internal Uri Root { get; private set; } = null!;
    internal byte[] RootBytes => Encoding.UTF8.GetBytes(entries[Root.AbsolutePath].Entry.Manifest!);
    internal async Task LoadAsync(CancellationToken token)
    {
        byte[] prefix = new byte[12];
        await ReadExactlyAtAsync(0, prefix, token).ConfigureAwait(false);
        if (!prefix.AsSpan(0, 8).SequenceEqual(NativeAdaptiveArchive.Magic)) Invalid();
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (size <= 0 || size > NativeAdaptiveArchive.MaximumIndexBytes || 12L + size > length) Invalid();
        byte[] bytes = new byte[size];
        await ReadExactlyAtAsync(12, bytes, token).ConfigureAwait(false);
        NativeAdaptiveArchive.Index index;
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            DuplicateCheck(parsed.RootElement);
            index = JsonSerializer.Deserialize<NativeAdaptiveArchive.Index>(bytes, NativeAdaptiveArchive.Json)!;
        }
        catch { throw new PlaybackException(PlaybackError.AdaptiveInvalid); }
        finally { Array.Clear(bytes); }
        if (index is null || index.Version != 1 || index.Entries is not { Length: > 0 and <= 4096 }) Invalid();
        long offset = 12L + size, manifestBytes = 0;
        foreach (var entry in index!.Entries)
        {
            if (entry is null || !PathValid(entry.Path) || entry.Kind is < 0 or > (int)AdaptiveResourceKind.Mp4Segment ||
                entry.Length <= 0 || entry.Length > (entry.Kind is 0 or 2 ? 1024 * 1024 : 64 * 1024 * 1024)) Invalid();
            if (entry!.Kind == (int)AdaptiveResourceKind.Manifest)
            {
                if (entry.Manifest is null || Encoding.UTF8.GetByteCount(entry.Manifest) != entry.Length || entry.Binding != "") Invalid();
                manifestBytes += entry.Length;
                if (manifestBytes > 4 * AdaptiveManifest.MaximumBytes) Invalid();
            }
            else if (entry.Manifest is not null || entry.Binding is not { Length: 64 } ||
                entry.Binding.Any(c => !char.IsAsciiHexDigit(c))) Invalid();
            if (!entries.TryAdd(entry.Path, (entry, offset))) Invalid();
            if (entry.Manifest is null) offset = checked(offset + entry.Length);
        }
        if (offset != length || !PathValid(index.Root) || !entries.TryGetValue(index.Root, out var root) || root.Entry.Manifest is null) Invalid();
        Root = new(NativeAdaptiveArchive.ArchiveOrigin, index.Root);
    }
    private static bool PathValid(string? path) => path is { Length: 35 } && path.StartsWith("/r/", StringComparison.Ordinal) &&
        path.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static void DuplicateCheck(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) Invalid(); DuplicateCheck(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) DuplicateCheck(child);
    }
    internal Uri Resolve(Uri parent, string reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Any(c => char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(parent, reference, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
            uri.Port != 1 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !PathValid(uri.AbsolutePath) || !entries.ContainsKey(uri.AbsolutePath))
            throw new PlaybackException(PlaybackError.AdaptiveInvalid);
        return uri;
    }
    internal Task<AdaptiveHttpTransport.Response> OpenAsync(AdaptiveResource resource, long start, long? end,
        CancellationToken token, CancellationToken session)
    {
        token.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        Uri uri = Resolve(NativeAdaptiveArchive.ArchiveOrigin, resource.Uri.AbsoluteUri);
        var (entry, offset) = entries[uri.AbsolutePath];
        if ((int)resource.Kind != entry.Kind || resource.Offset is not null || resource.Count is not null ||
            start < 0 || start >= entry.Length || end < start || end >= entry.Length)
            throw new PlaybackException(PlaybackError.HttpInvalidRange);
        long count = (end ?? entry.Length - 1) - start + 1;
        Stream stream = entry.Manifest is { } manifest
            ? new MemoryStream(Encoding.UTF8.GetBytes(manifest), (int)start, (int)count, writable: false)
            : new WindowStream(this, offset + start, count);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        return Task.FromResult(new AdaptiveHttpTransport.Response(response, stream, count, entry.Length, start,
            TimeSpan.FromSeconds(15), session, token, null));
    }
    private async Task ReadExactlyAtAsync(long offset, Memory<byte> destination, CancellationToken token)
    {
        int count = 0;
        while (count < destination.Length)
            count += await ReadAtAsync(offset + count, destination[count..], token).ConfigureAwait(false);
    }
    private async Task<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken token)
    {
        await io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (content.Length != length || offset < 0 || offset + buffer.Length > length ||
                content.Seek(offset, SeekOrigin.Begin) != offset) throw new PlaybackException(PlaybackError.ReadChanged);
            int n = await content.ReadAsync(buffer[..Math.Min(65536, buffer.Length)], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (n <= 0) throw new PlaybackException(PlaybackError.ReadTruncated);
            return n;
        }
        finally { io.Release(); }
    }
    private static void Invalid() => throw new PlaybackException(PlaybackError.AdaptiveInvalid);
    private sealed class WindowStream(OfflineAdaptiveArchive archive, long offset, long count) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => count;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (position == count || buffer.IsEmpty) return 0;
            int n = await archive.ReadAtAsync(offset + position, buffer[..(int)Math.Min(buffer.Length, count - position)], token).ConfigureAwait(false);
            position += n; return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
