using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeCatalogTests
{
    private static IEnumerable<TestCase> NetworkPlaylistCases()
    {
        yield return new("Native network references deduplicate platform plus ID, not title", NetworkIdentities);
        yield return new("Native network details and content edits are read-only, removal is reference-only", NetworkReadOnly);
        yield return new("Native network open reads fresh remote changes and failure keeps saved identity", NetworkRefresh);
        yield return new("Native network current format reopens, member fields and old format are rejected", NetworkFormat);
        yield return new("Native network reference save respects pre-CAS cancellation and post-CAS receipt", NetworkCancellation);
        foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
        {
            string p = platform;
            yield return new($"Native network {p} unbound reference recreation rejects old remove handles across reopen", () => NetworkRecreateCas(p));
        }
    }
    private static async Task NetworkIdentities()
    {
        await using var f = new Fixture();
        Ok(await f.Library.ExecuteAsync("music.library.create", new { name = "Same title" }));
        foreach (var (platform, id) in new[] { ("wy", "42"), ("wy", "43"), ("tx", "42"), ("kg", "global-42"),
            ("kw", "42"), ("mg", "mg-42") })
        {
            var saved = Ok(await f.Run("playlist.save", new { platform, id, name = "Same title" }));
            Check(S(saved.GetProperty("collection"), "platform") == platform && S(saved.GetProperty("collection"), "remoteId") == id);
            Check(S(saved.GetProperty("sync"), "state") == "local-only");
        }
        var list = Ok(await f.Library.ExecuteAsync("music.library.list", new { limit = 100 })).GetProperty("items");
        Check(list.GetArrayLength() == 8 && list.EnumerateArray().Count(item => S(item, "kind") == "network") == 6 &&
            list.EnumerateArray().Count(item => S(item, "kind") == "playlist") == 1 && f.Platform.Calls.Length == 0);
        var snapshot = await f.Library.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("songs").EnumerateObject().Count() == 0);
    }
    private static async Task NetworkReadOnly()
    {
        await using var f = new Fixture();
        var reference = Ok(await f.Run("playlist.save", new { platform = "wy", id = "42", name = "Reference" })).GetProperty("collection");
        string id = S(reference, "id"); long revision = reference.GetProperty("revision").GetInt64(); int writes = f.Store.Writes;
        foreach (var (method, parameters) in new (string, object)[]
        {
            ("music.library.edit", new { id, ifRevision = revision, name = "Renamed" }),
            ("music.library.addSongs", new { id, ifRevision = revision, songs = new[] { Song("wy", 1) } }),
            ("music.library.removeSong", new { id, ifRevision = revision, songId = "unknown" }),
            ("music.library.reorderSong", new { id, ifRevision = revision, songId = "one", targetId = "two", after = true }),
            ("music.library.remove", new { id, ifRevision = revision })
        }) Error(await f.Library.ExecuteAsync(method, parameters), "READ_ONLY");
        Error(await f.Library.ExecuteAsync("music.library.detail", new { id }), "READ_ONLY");
        Check(f.Store.Writes == writes && f.Platform.Calls.Length == 0);
        Error(await f.Library.ExecuteAsync("music.library.network.remove", new { id, ifRevision = revision + 1 }), "REVISION_CONFLICT");
        var removed = Ok(await f.Library.ExecuteAsync("music.library.network.remove", new { id, ifRevision = revision }));
        Check(removed.GetProperty("localSaved").GetBoolean() && !removed.GetProperty("present").GetBoolean());
        Check(!Ok(await f.Library.ExecuteAsync("music.library.network.get", new { platform = "wy", id = "42" }))
            .GetProperty("present").GetBoolean());
        Check(f.Platform.Calls.Length == 0 && N(Ok(await f.Library.ExecuteAsync("music.library.list")), "total") == 1);
    }
    private static async Task NetworkRefresh()
    {
        await using var f = new Fixture();
        Ok(await f.Run("playlist.save", new { platform = "wy", id = "42", name = "Reference", count = 1 }));
        f.Platform.Total = 1;
        var first = Ok(await f.Run("playlist", new { platform = "wy", id = "42", refresh = true }));
        Check(N(first, "total") == 1);
        f.Platform.Total = 2;
        var second = Ok(await f.Run("playlist", new { platform = "wy", id = "42", refresh = true }));
        Check(N(second, "total") == 2 && f.Platform.Calls.All(call => call.Request is MusicPlaylistRequest { Id: "42", Refresh: true }));
        f.Platform.Handler = _ => throw new MusicPlatformException("NETWORK_ERROR");
        Error(await f.Run("playlist", new { platform = "wy", id = "42", refresh = true }), "NETWORK_ERROR");
        var saved = Ok(await f.Library.ExecuteAsync("music.library.network.get", new { platform = "wy", id = "42" }));
        Check(saved.GetProperty("present").GetBoolean() && S(saved.GetProperty("collection"), "remoteId") == "42");
        var snapshot = await f.Library.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("songs").EnumerateObject().Count() == 0 &&
            snapshot.Library.GetProperty("collections").GetArrayLength() == 1);
    }
    private static async Task NetworkRecreateCas(string platform)
    {
        await using var f = new Fixture();
        string remoteId = platform == "kg" ? "global-42" : platform == "mg" ? "mg-42" : "42";
        var first = Ok(await f.Run("playlist.save", new { platform, id = remoteId, name = "Reference" })).GetProperty("collection");
        string id = S(first, "id"); long oldRevision = first.GetProperty("revision").GetInt64();
        var stale = new { id, ifRevision = oldRevision };
        Ok(await f.Library.ExecuteAsync("music.library.network.remove", stale));
        var deleted = await f.Library.ReadLibrarySyncAsync();
        Check(deleted.Library.GetProperty("networkPlaylists").GetArrayLength() == 0 &&
            !deleted.Library.GetProperty("accounts").EnumerateObject().Any());
        var saved = Ok(await f.Run("playlist.save", new { platform, id = remoteId, name = "Reference" }));
        var current = saved.GetProperty("collection"); long revision = current.GetProperty("revision").GetInt64();
        Check(S(current, "id") == id && revision > oldRevision && S(saved.GetProperty("sync"), "state") == "local-only");
        int writes = f.Store.Writes; var bytes = f.Store.Bytes!.ToArray();
        Error(await f.Library.ExecuteAsync("music.library.network.remove", stale), "REVISION_CONFLICT");
        Check(f.Store.Writes == writes && f.Store.Bytes.SequenceEqual(bytes));
        await f.Owner.DisposeAsync(); await f.Library.AbortAsync();
        await using var reader = new MusicLibraryOwner(f.Store);
        var reopened = Ok(await reader.ExecuteAsync("music.library.network.get", new { platform, id = remoteId }));
        Check(reopened.GetProperty("present").GetBoolean() &&
            reopened.GetProperty("collection").GetRawText() == current.GetRawText());
        Error(await reader.ExecuteAsync("music.library.network.remove", stale), "REVISION_CONFLICT");
        Check(f.Store.Writes == writes && f.Store.Bytes.SequenceEqual(bytes));
        var removed = Ok(await reader.ExecuteAsync("music.library.network.remove", new { id, ifRevision = revision }));
        Check(removed.GetProperty("localSaved").GetBoolean() && !removed.GetProperty("present").GetBoolean() &&
            (await reader.ReadLibrarySyncAsync()).Library.GetProperty("networkPlaylists").GetArrayLength() == 0 &&
            f.Platform.Calls.Length == 0);
    }
    private static async Task NetworkFormat()
    {
        foreach (string defect in new[] { "none", "members", "old" })
        {
            await using var f = new Fixture();
            Ok(await f.Run("playlist.save", new { platform = "wy", id = "42", name = "Reference" }));
            var document = JsonNode.Parse(f.Store.Bytes!)!.AsObject();
            var wrapper = document["entries"]!.AsArray().Select(entry => entry!["value"])
                .OfType<JsonObject>().Single(value => value.ContainsKey("library"));
            if (defect == "members") wrapper["library"]!["networkPlaylists"]![0]!["order"] = new JsonArray("1");
            if (defect == "old") { wrapper["schema"] = 1; wrapper["library"]!["version"] = 1; }
            f.Store.Bytes = JsonSerializer.SerializeToUtf8Bytes(document, LJ.Json);
            var original = f.Store.Bytes.ToArray(); int writes = f.Store.Writes;
            await using var reader = new MusicLibraryOwner(f.Store);
            var result = await reader.ExecuteAsync("music.library.list");
            if (defect == "none") Check(N(Ok(result), "total") == 2);
            else Error(result, "CORRUPT_LIBRARY");
            Check(f.Store.Writes == writes && f.Store.Bytes.SequenceEqual(original));
        }
    }
    private static async Task NetworkCancellation()
    {
        await using var f = new Fixture();
        using var before = new CancellationTokenSource(); before.Cancel();
        Error(await f.Run("playlist.save", new { platform = "wy", id = "42", name = "Reference" }, before.Token), "CANCELLED");
        Check(f.Store.Writes == 0 && f.Platform.Calls.Length == 0);
        f.Store.Block(); using var after = new CancellationTokenSource();
        var saving = f.Run("playlist.save", new { platform = "wy", id = "42", name = "Reference" }, after.Token);
        await f.Store.Entered!.Task; after.Cancel(); f.Store.Release!.TrySetResult();
        var receipt = Ok(await saving);
        Check(receipt.GetProperty("localSaved").GetBoolean() && f.Store.Writes == 1 && f.Store.WriteWasUncancellable);
    }
}
