using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicMetadataPolicyTests
{
    private static readonly string[] Platforms = ["wy", "tx", "kg", "kw", "mg"];
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("missing optional policy preserves original defaults without a write", async (owner, store) =>
        {
            var settings = Ok(await owner.ExecuteAsync("music.metadata.settings.get")).GetProperty("settings");
            Equal(string.Join(',', Platforms), string.Join(',', settings.GetProperty("platforms").EnumerateArray().Select(x => x.GetString())));
            Equal(5, settings.GetProperty("enabled").GetArrayLength()); Equal(0, store.Writes);
        });
        yield return Case("reorder and empty enabled set persist through a new sole owner", async (owner, store) =>
        {
            var reverse = Platforms.Reverse().ToArray();
            Ok(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = reverse, enabled = Array.Empty<string>() }));
            await using var reopened = new MusicLibraryOwner(store);
            var settings = Ok(await reopened.ExecuteAsync("music.metadata.settings.get")).GetProperty("settings");
            Equal(string.Join(',', reverse), string.Join(',', settings.GetProperty("platforms").EnumerateArray().Select(x => x.GetString())));
            Equal(0, settings.GetProperty("enabled").GetArrayLength()); Equal(1L, settings.GetProperty("revision").GetInt64());
        });
        yield return Case("duplicate missing unknown and nonstring platforms are all rejected without writes", async (owner, store) =>
        {
            foreach (var order in new object[] { new[] { "wy" }, new[] { "wy", "wy", "kg", "kw", "mg" },
                new[] { "wy", "tx", "kg", "kw", "other" }, new object[] { "wy", "tx", "kg", "kw", 1 } })
                Code(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = order, enabled = Platforms }), "INVALID_REQUEST");
            foreach (var enabled in new[] { new[] { "wy", "wy" }, new[] { "other" } })
                Code(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled }), "INVALID_REQUEST");
            Equal(0, store.Writes);
        });
        yield return Case("unknown fields and a stale policy revision cannot overwrite the chosen order", async (owner, store) =>
        {
            Code(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = Platforms, url = "synthetic.invalid" }), "INVALID_REQUEST");
            Ok(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = Array.Empty<string>() }));
            Code(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = Platforms }), "REVISION_CONFLICT");
            Equal(1, store.Writes);
        });
        yield return Case("disabled cached source does not suppress an enabled exact alternative", async (owner, _) =>
        {
            var qq = Ok(await owner.ExecuteAsync("music.online.song", new {
                platform = "tx", musicInfo = new { musicId = "qq42", songmid = "qq42" },
                title = "same", artist = "same", duration = 120
            })).GetProperty("song");
            var song = MusicSongFixture.Online("wy", "42", "same", "same", duration: 120);
            song["sources"]!.AsArray().Add(JsonNode.Parse(qq.GetProperty("sources")[0].GetRawText()));
            Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            var normalized = Ok(await owner.ExecuteAsync("music.queue.get")).GetProperty("current").GetProperty("song");
            var key = normalized.GetProperty("sources").EnumerateArray().Single(source => source.GetProperty("source").GetString() == "wy")
                .GetProperty("sourceKey").GetString();
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song = normalized, sourceKey = key, lines = MusicLyricTiming.Parse("[00:01]old") }));
            Ok(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = new[] { "tx" } }));
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song = normalized })).GetProperty("state").GetString());
            Equal("disabled", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song = normalized, sourceKey = key })).GetProperty("state").GetString());
        });
        yield return Case("unknown policy CAS reconciles on its current read and blocks unrelated consumers until then", async (owner, store) =>
        {
            store.FailAfterWrite = true;
            Code(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = Array.Empty<string>() }), "COMMIT_UNKNOWN");
            Code(await owner.ExecuteAsync("music.lyrics.local.get", new { song = LocalSong() }), "PENDING_WRITE");
            Equal(0, Ok(await owner.ExecuteAsync("music.metadata.settings.get")).GetProperty("settings").GetProperty("enabled").GetArrayLength());
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song = LocalSong() })).GetProperty("state").GetString());
            Equal(1, store.Writes);
        });
        yield return Case("a malformed durable optional policy is corruption not silent defaults", async (owner, store) =>
        {
            Ok(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = Platforms }));
            var document = JsonNode.Parse(store.Data!)!.AsObject();
            var entry = document["entries"]!.AsArray().OfType<JsonObject>().Single();
            entry["value"]!["enabled"] = new JsonArray("not-a-platform");
            store.Data = Encoding.UTF8.GetBytes(document.ToJsonString());
            await using var reopened = new MusicLibraryOwner(store);
            Code(await reopened.ExecuteAsync("music.metadata.settings.get"), "CORRUPT_STORAGE");
        });
        yield return Case("local lyrics use an exact local identity with no online source key", async (owner, _) =>
        {
            var song = LocalSong();
            Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            Ok(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text = "[00:01]first\n[00:05]second" }));
            var result = Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song }));
            Equal("local", result.GetProperty("state").GetString()); Equal(2, result.GetProperty("lines").GetArrayLength());
            Check(!result.TryGetProperty("sourceKey", out var ignoredSourceKey));
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song = LocalSong("different") })).GetProperty("state").GetString());
        });
        yield return Case("local lyrics require the currently selected queue identity and valid timed text", async (owner, store) =>
        {
            var song = LocalSong();
            Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            int writes = store.Writes;
            Code(await owner.ExecuteAsync("music.lyrics.local.save", new { song = LocalSong("other"), text = "[00:01]x" }), "TARGET_CHANGED");
            foreach (var text in new[] { "no timing", "[00:01]bad\0", new string('x', 65537) })
                Code(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text }), "INVALID_LYRIC_RESPONSE");
            Equal(writes, store.Writes);
        });
        yield return Case("local lyric replacement and removal do not retain obsolete text", async (owner, store) =>
        {
            var song = LocalSong();
            Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            Ok(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text = "[00:01]old" }));
            Ok(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text = "[00:01]new" }));
            Equal("new", Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song })).GetProperty("lines")[0].GetProperty("text").GetString());
            int writes = store.Writes;
            Ok(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text = "[00:01]new" }));
            Equal(writes, store.Writes);
            Ok(await owner.ExecuteAsync("music.lyrics.local.remove", new { song }));
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song })).GetProperty("state").GetString());
        });
        yield return Case("local lyric unknown CAS never replays after cancellation or reconciliation", async (owner, store) =>
        {
            var song = LocalSong();
            Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            store.FailAfterWrite = true;
            Code(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text = "[00:01]saved" }), "COMMIT_UNKNOWN");
            int writes = store.Writes;
            Code(await owner.ExecuteAsync("music.lyrics.local.remove", new { song }), "PENDING_WRITE");
            await owner.ReconcileAsync();
            Equal("local", Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song })).GetProperty("state").GetString());
            Equal(writes, store.Writes);
        });
        yield return Case("global lyric disable hides local text but retains explicit remove capability", async (owner, _) =>
        {
            var song = LocalSong();
            Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            Ok(await owner.ExecuteAsync("music.lyrics.local.save", new { song, text = "[00:01]saved" }));
            Ok(await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false, target = (object?)null }));
            var result = Ok(await owner.ExecuteAsync("music.lyrics.local.get", new { song }));
            Equal("disabled", result.GetProperty("state").GetString()); Equal(0, result.GetProperty("lines").GetArrayLength());
            Check(result.GetProperty("available").GetBoolean());
        });
        yield return Case("cancellation before admission cannot create local text or change metadata policy", async (owner, store) =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Code(await owner.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0, platforms = Platforms, enabled = Platforms }, cancel.Token), "CANCELLED");
            Code(await owner.ExecuteAsync("music.lyrics.local.save", new { song = LocalSong(), text = "[00:01]x" }, cancel.Token), "CANCELLED");
            Equal(0, store.Writes);
        });
    }
    private static TestCase Case(string name, Func<MusicLibraryOwner, Store, Task> body) =>
        new("Native metadata policy " + name, async () =>
        {
            await using var store = new Store(); var owner = new MusicLibraryOwner(store);
            try { await body(owner, store); } finally { await owner.AbortAsync(); }
        });
    private static JsonElement LocalSong(string id = "one") =>
        MusicSongFixture.Element(MusicSongFixture.Local("library:local:" + id, "Local " + id, "Synthetic", duration: 80));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Metadata policy assertion failed"); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static JsonElement Ok(JsonElement result)
    { if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetProperty("code").GetString()); return result; }
    private static void Code(JsonElement result, string code)
    { Check(!result.GetProperty("ok").GetBoolean()); Equal(code, result.GetProperty("code").GetString()); }
    private sealed class Store : IProtectedDocumentStore
    {
        internal byte[]? Data;
        internal int Writes;
        internal bool FailAfterWrite;
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, ns);
            return Task.FromResult(new ProtectedDocument(Data?.ToArray(), Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data)));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, ns);
            Equal(Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data), expected);
            Data = data.ToArray(); Writes++;
            if (FailAfterWrite) { FailAfterWrite = false; throw new ProtectedDocumentException(DocumentStoreError.Unavailable); }
            return Task.FromResult(DocumentFingerprint.FromBytes(Data));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
