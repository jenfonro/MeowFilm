using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Lyrics and the already decoded cover share a recording's evictable presentation.
/// The single document uses the store's 8 MiB resource bound, not a fixed song count.</summary>
internal sealed class MusicRecordingPresentationCache(CacheDocumentStore store, TimeProvider? time = null)
{
    internal const string Namespace = "music.recording-presentation.v2";
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    internal static string Key(JsonObject song) => LJ.Digest(LJ.Arr([song["songId"], song["duration"]]));

    private JsonObject Decode(byte[]? bytes)
    {
        try
        {
            var value = bytes is null ? null : JsonNode.Parse(bytes)?.AsObject();
            if (value?["version"]?.GetValue<int>() != 1 || value["entries"] is not JsonObject entries)
                return new();
            foreach (var pair in entries.ToArray())
            {
                if (pair.Key.Length != 64 || !pair.Key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    pair.Value is not JsonObject row || row["songId"] is not JsonValue song ||
                    !song.TryGetValue<string>(out var id) || string.IsNullOrEmpty(id) ||
                    row["updatedAt"] is not JsonValue stamp || !stamp.TryGetValue<long>(out var at) ||
                    at <= 0 || at > Now || Now - at >= MusicAudioFileCache.Ttl)
                    entries.Remove(pair.Key);
            }
            return entries;
        }
        catch { return new(); }
    }
    internal Task LoadAsync(CancellationToken token) => store.ReadAsync(Namespace, token);
    internal JsonObject? ReadMemory(string key) =>
        Decode(store.ReadMemory(Namespace).Data)[key]?.DeepClone() as JsonObject;
    internal async Task<JsonObject?> ReadAsync(string key, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        return ReadMemory(key);
    }
    internal Task PatchAsync(string key, string songID, Action<JsonObject> patch, CancellationToken token) =>
        UpdateAsync(entries =>
        {
            var value = entries[key] as JsonObject ?? LJ.Obj(("songId", songID));
            if (value.Parent is null) entries[key] = value;
            patch(value); value["updatedAt"] = Now;
        }, key, token);

    private async Task UpdateAsync(Action<JsonObject> update, string? retained, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
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
                await store.WriteAsync(Namespace, bytes, snapshot.Fingerprint, token).ConfigureAwait(false);
                return;
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
        }
    }
    internal Task ClearAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        store.Save(Namespace, JsonSerializer.SerializeToUtf8Bytes(LJ.Obj(("version", 1), ("entries", new JsonObject()))));
        return Task.CompletedTask;
    }
    internal Task RemoveAsync(IReadOnlySet<string> songs, long through, CancellationToken token) =>
        UpdateAsync(entries =>
        {
            foreach (var pair in entries.ToArray())
                if (songs.Contains(pair.Value!["songId"]!.GetValue<string>()) &&
                    pair.Value!["updatedAt"]!.GetValue<long>() <= through) entries.Remove(pair.Key);
        }, null, token);
    internal async Task TouchAsync(string song, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        if (!Decode(store.ReadMemory(Namespace).Data).Any(pair => pair.Value!["songId"]!.GetValue<string>() == song)) return;
        await UpdateAsync(entries =>
        {
            foreach (var row in entries.Select(pair => pair.Value!).Where(row => row["songId"]!.GetValue<string>() == song))
                row["updatedAt"] = Now;
        }, null, token).ConfigureAwait(false);
    }
}
