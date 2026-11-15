using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Recoverable discovery timestamps and source exclusions. Song facts live only in the library.</summary>
internal sealed class MusicSongMatchStore(CacheDocumentStore store)
{
    internal const string Namespace = "music.source-discovery.v1";
    private const int MaximumBytes = 4 * 1024 * 1024;
    internal async Task<IReadOnlyList<JsonObject>> ReadAsync(CancellationToken token)
    {
        byte[]? bytes = null;
        try
        {
            var saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            bytes = saved.Data;
            if (bytes is null || bytes.Length > MaximumBytes) return [];
            var root = JsonNode.Parse(bytes)!.AsObject();
            return root["entries"]!.AsArray().Select(value => Entry(value!.AsObject())).ToArray();
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return []; }
        finally { if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    internal Task WriteAsync(IReadOnlyList<JsonObject> entries, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var retained = new Queue<JsonObject>(); long bytes = 16;
        foreach (var entry in entries)
        {
            var current = Entry(entry);
            retained.Enqueue(current); bytes += LJ.Bytes(current).Length + 1;
            while (bytes > MaximumBytes && retained.Count > 0) bytes -= LJ.Bytes(retained.Dequeue()).Length + 1;
        }
        var encoded = LJ.Bytes(LJ.Obj(("entries", LJ.Arr(retained))));
        try { store.Save(Namespace, encoded); }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded); }
        return Task.CompletedTask;
    }
    private static JsonObject Entry(JsonObject entry)
    {
        var platforms = new JsonObject();
        foreach (var pair in LJ.Object(entry["platforms"]))
        {
            var state = LJ.Object(pair.Value);
            platforms[pair.Key] = LJ.Obj(("state", LJ.Text(state["state"])),
                ("checkedAt", LJ.I(state["checkedAt"])), ("context", LJ.S(state["context"])),
                ("query", LJ.S(state["query"])), ("code", LJ.S(state["code"])),
                ("matchedAt", state["matchedAt"] is null ? null : JsonValue.Create(LJ.I(state["matchedAt"]))));
        }
        return LJ.Obj(("songId", LJ.Text(entry["songId"])), ("platforms", platforms),
            ("excluded", LJ.Arr(LJ.Array(entry["excluded"]).Select(value => value!.GetValue<string>()))));
    }
}
