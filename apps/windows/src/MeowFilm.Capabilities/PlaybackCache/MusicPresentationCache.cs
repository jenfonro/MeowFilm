using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>One evictable document for original image bytes and song-referenced lyrics.
/// Its total serialized disk occupancy uses the existing 8 MiB presentation budget.</summary>
internal sealed class MusicPresentationCache(CacheDocumentStore store, TimeProvider? time = null) : IArtworkByteCache
{
    internal const string Namespace = "music.presentation-resources.v1";
    internal const long Ttl = 30L * 86400000;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object gate = new();
    private long imageEpoch, lyricEpoch;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public long Epoch { get { lock (gate) return imageEpoch; } }
    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(Ttl);
    internal long LyricEpoch { get { lock (gate) return lyricEpoch; } }
    internal static string SongKey(JsonObject song) => LJ.S(song["songId"]);
    private static string Key(string kind, string resource) => LJ.Digest(new JsonArray(kind, resource));

    private JsonObject Decode(byte[]? bytes)
    {
        try
        {
            var value = bytes is null ? null : JsonNode.Parse(bytes)?.AsObject();
            if (value?["version"]?.GetValue<int>() != 1 || value["entries"] is not JsonObject entries) return new();
            long now = Now;
            foreach (var pair in entries.ToArray())
            {
                if (pair.Value is not JsonObject row || row["kind"] is not JsonValue kind ||
                    !kind.TryGetValue<string>(out var type) || type is not ("image" or "lyrics") ||
                    row["resource"] is not JsonValue resource || !resource.TryGetValue<string>(out var key) ||
                    pair.Key != Key(type, key) || row["updatedAt"] is not JsonValue stamp ||
                    !stamp.TryGetValue<long>(out var at) || at <= 0 || at > now || now - at >= Ttl)
                    entries.Remove(pair.Key);
            }
            return entries;
        }
        catch { return new(); }
    }

    internal Task LoadAsync(CancellationToken token) => store.ReadAsync(Namespace, token);
    internal JsonObject? ReadLyricsMemory(string songId) =>
        Decode(store.ReadMemory(Namespace).Data)[Key("lyrics", songId)]?.DeepClone() as JsonObject;
    internal Task SaveLyricsAsync(string songId, string sourceKey, JsonArray lines, long epoch, CancellationToken token) =>
        UpdateAsync(entries =>
        {
            entries[Key("lyrics", songId)] = LJ.Obj(("kind", "lyrics"), ("resource", songId),
                ("sourceKey", sourceKey), ("lines", lines), ("updatedAt", Now));
        }, Key("lyrics", songId), "lyrics", epoch, token);

    public async Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        try
        {
            var row = Decode(store.ReadMemory(Namespace).Data)[Key("image", address)];
            if (row is null) return null;
            var bytes = Convert.FromBase64String(row["content"]!.GetValue<string>());
            return bytes.Length <= ArtworkService.MaximumBytes
                ? new(bytes, DateTimeOffset.FromUnixTimeMilliseconds(row["updatedAt"]!.GetValue<long>() + Ttl)) : null;
        }
        catch { token.ThrowIfCancellationRequested(); return null; }
    }
    public Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token) =>
        UpdateAsync(entries =>
        {
            entries[Key("image", address)] = LJ.Obj(("kind", "image"), ("resource", address),
                ("content", Convert.ToBase64String(bytes.Span)), ("updatedAt", Now));
        }, Key("image", address), "image", epoch, token);
    public Task RemoveAsync(string address, long epoch, CancellationToken token) =>
        UpdateAsync(entries => entries.Remove(Key("image", address)), null, "image", epoch, token);

    private async Task UpdateAsync(Action<JsonObject> update, string? retained, string? kind,
        long? expectedEpoch, CancellationToken token)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var snapshot = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
                var entries = Decode(snapshot.Data);
                update(entries);
                byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(LJ.Obj(("version", 1), ("entries", entries)));
                byte[] bytes = Encode();
                while (bytes.Length > LocalDocumentStore.MaximumDocumentBytes)
                {
                    var oldest = entries.Where(pair => pair.Key != retained)
                        .OrderBy(pair => pair.Value!["updatedAt"]!.GetValue<long>()).FirstOrDefault();
                    if (oldest.Key is null) throw new DocumentStoreException(DocumentStoreError.Limit);
                    entries.Remove(oldest.Key); bytes = Encode();
                }
                try
                {
                    Task<DocumentFingerprint> commit;
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (expectedEpoch is { } epoch && epoch != (kind == "image" ? imageEpoch : lyricEpoch)) return;
                        commit = store.WriteAsync(Namespace, bytes, snapshot.Fingerprint, token);
                    }
                    await commit.ConfigureAwait(false);
                    return;
                }
                catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
            }
        }
        finally { writes.Release(); }
    }

    internal Task ClearImagesAsync(CancellationToken token) => ClearAsync("image", token);
    internal Task ClearAllAsync(CancellationToken token) => ClearAsync(null, token);
    private Task ClearAsync(string? kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (kind is null or "image") imageEpoch++;
            if (kind is null or "lyrics") lyricEpoch++;
        }
        return UpdateAsync(entries =>
        {
            foreach (var pair in entries.ToArray())
                if (kind is null || LJ.S(pair.Value!["kind"]) == kind) entries.Remove(pair.Key);
        }, null, null, null, token);
    }
    internal async Task TouchPlayedAsync(string songId, string image, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        long images = Epoch, lyrics = LyricEpoch;
        await UpdateAsync(entries =>
        {
            if (entries[Key("lyrics", songId)] is JsonObject row) row["updatedAt"] = Now;
        }, null, "lyrics", lyrics, token).ConfigureAwait(false);
        if (image.Length > 0)
            await UpdateAsync(entries =>
            {
                if (entries[Key("image", image)] is JsonObject row) row["updatedAt"] = Now;
            }, null, "image", images, token).ConfigureAwait(false);
    }
}
