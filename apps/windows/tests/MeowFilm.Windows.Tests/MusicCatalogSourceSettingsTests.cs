using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicCatalogSourceSettingsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("default reads are WY without manufacturing a document", async (owner, store) =>
        {
            Settings(await Ok(owner, "music.catalogSource.settings.get"), "wy", 0);
            Settings(await Ok(owner, "music.catalogSource.settings.get", new { }), "wy", 0);
            Equal(0, store.Writes);
            Equal<byte[]?>(null, store.Data);
        });
        yield return Case("saving the current platform never writes or increments revision", async (owner, store) =>
        {
            Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "wy" }), "wy", 0);
            Equal(0, store.Writes);
            Equal<byte[]?>(null, store.Data);
            Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }), "kg", 1);
            byte[] saved = store.Data!.ToArray();
            Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 1, platform = "kg" }), "kg", 1);
            Settings(await Ok(owner, "music.catalogSource.settings.get"), "kg", 1);
            Equal(1, store.Writes);
            Check(saved.SequenceEqual(store.Data!), "same-value save changed durable bytes");
        });
        yield return Case("platform and revision survive owner restarts in the fixed area", async (owner, store) =>
        {
            Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }), "kg", 1);
            var entry = JsonNode.Parse(store.Data!)!["entries"]!.AsArray().Single()!;
            Equal("""["meowfilm","music",1,"local","native-catalog-source"]""", entry["key"]!.GetValue<string>());
            Equal(3, entry["value"]!.AsObject().Count);
            Equal(1, entry["value"]!["schema"]!.GetValue<int>());
            await owner.AbortAsync();
            await using (var reopened = new MusicLibraryOwner(store))
            {
                Settings(await Ok(reopened, "music.catalogSource.settings.get"), "kg", 1);
                Equal(1, store.Writes);
                Settings(await Ok(reopened, "music.catalogSource.settings.save", new { ifRevision = 1, platform = "wy" }), "wy", 2);
            }
            await using var finalOwner = new MusicLibraryOwner(store);
            Settings(await Ok(finalOwner, "music.catalogSource.settings.get"), "wy", 2);
            Equal(2, store.Writes);
        });
        yield return Case("invalid fields platforms and revisions fail without a write", async (owner, store) =>
        {
            foreach (string input in new[]
            {
                """{}""",
                """{"platform":"kg"}""",
                """{"ifRevision":0}""",
                """{"ifRevision":0,"platform":"kg","extra":true}""",
                """{"ifRevision":0,"platform":"kg","schema":1}""",
                """{"ifRevision":0,"platform":"kg","revision":0}""",
                """{"ifRevision":0,"platform":null}""",
                """{"ifRevision":0,"platform":true}""",
                """{"ifRevision":0,"platform":1}""",
                """{"ifRevision":0,"platform":[]}""",
                """{"ifRevision":0,"platform":{}}""",
                """{"ifRevision":0,"platform":""}""",
                """{"ifRevision":0,"platform":"WY"}""",
                """{"ifRevision":0,"platform":"KG"}""",
                """{"ifRevision":0,"platform":" kg"}""",
                """{"ifRevision":0,"platform":"wy "}""",
                """{"ifRevision":0,"platform":"tx"}""",
                """{"ifRevision":0,"platform":"kw"}""",
                """{"ifRevision":0,"platform":"mg"}""",
                """{"ifRevision":0,"platform":"local"}""",
                """{"ifRevision":null,"platform":"kg"}""",
                """{"ifRevision":true,"platform":"kg"}""",
                """{"ifRevision":"0","platform":"kg"}""",
                """{"ifRevision":-1,"platform":"kg"}""",
                """{"ifRevision":0.5,"platform":"kg"}""",
                """{"ifRevision":9007199254740992,"platform":"kg"}""",
                """[]""",
                "\"kg\"",
                """1"""
            })
                await Code(owner, "music.catalogSource.settings.save", JsonNode.Parse(input), "INVALID_REQUEST");
            foreach (string input in new[]
            {
                """{"ifRevision":0,"platform":"wy","platform":"kg"}""",
                """{"ifRevision":0,"ifRevision":0,"platform":"kg"}"""
            })
            {
                using var duplicate = JsonDocument.Parse(input);
                await Code(owner, "music.catalogSource.settings.save", duplicate.RootElement, "INVALID_REQUEST");
            }
            await Code(owner, "music.catalogSource.settings.get", new { platform = "wy" }, "INVALID_REQUEST");
            using var duplicateGet = JsonDocument.Parse("""{"extra":0,"extra":1}""");
            await Code(owner, "music.catalogSource.settings.get", duplicateGet.RootElement, "INVALID_REQUEST");
            await Code(owner, "music.catalogSource.settings.reconcile", null, "CAPABILITY_UNAVAILABLE");
            Settings(await Ok(owner, "music.catalogSource.settings.get"), "wy", 0);
            Equal(0, store.Writes);
            Equal<byte[]?>(null, store.Data);
        });
        yield return Case("stale revision fails even for the already selected platform", async (owner, store) =>
        {
            await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" });
            byte[] saved = store.Data!.ToArray();
            foreach (string platform in new[] { "wy", "kg" })
                await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform }, "REVISION_CONFLICT");
            await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 2, platform = "wy" }, "REVISION_CONFLICT");
            Settings(await Ok(owner, "music.catalogSource.settings.get"), "kg", 1);
            Equal(1, store.Writes);
            Check(saved.SequenceEqual(store.Data!), "revision conflict changed durable bytes");
        });
        yield return Case("malformed durable settings fail closed without repair", async (owner, store) =>
        {
            await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" });
            await owner.AbortAsync();
            byte[] original = store.Data!.ToArray();
            foreach (string value in new[]
            {
                "null", "[]",
                """{"schema":2,"revision":1,"platform":"kg"}""",
                """{"schema":"1","revision":1,"platform":"kg"}""",
                """{"schema":1,"revision":-1,"platform":"kg"}""",
                """{"schema":1,"revision":"1","platform":"kg"}""",
                """{"schema":1,"revision":0.5,"platform":"kg"}""",
                """{"schema":1,"revision":1,"platform":"tx"}""",
                """{"schema":1,"revision":1,"platform":"KG"}""",
                """{"schema":1,"revision":1,"platform":null}""",
                """{"schema":1,"revision":1,"platform":true}""",
                """{"schema":1,"revision":1,"platform":["kg"]}""",
                """{"schema":1,"revision":1}""",
                """{"revision":1,"platform":"kg"}""",
                """{"schema":1,"platform":"kg"}""",
                """{"schema":1,"revision":1,"platform":"kg","extra":true}"""
            })
            {
                var document = JsonNode.Parse(original)!.AsObject();
                document["entries"]!.AsArray().Single()!["value"] = JsonNode.Parse(value);
                store.Data = LJ.Bytes(document);
                byte[] corrupt = store.Data.ToArray();
                await using var reopened = new MusicLibraryOwner(store);
                await Code(reopened, "music.catalogSource.settings.get", null, "CORRUPT_STORAGE");
                await Code(reopened, "music.catalogSource.settings.save",
                    new { ifRevision = 1, platform = "wy" }, "CORRUPT_STORAGE");
                Equal(1, store.Writes);
                Check(corrupt.SequenceEqual(store.Data!), "corrupt settings were rewritten");
            }
        });
        yield return Case("unknown before CAS reads the old durable state without replay", (owner, store) => Unknown(owner, store, false));
        yield return Case("unknown after CAS reads the new durable state without replay", (owner, store) => Unknown(owner, store, true));
        foreach (string method in new[] { "music.queue.mode", "music.search.settings.save", "music.officialSources.settings.save" })
            yield return Case("get cannot settle another owner " + method, async (owner, store) =>
            {
                object parameters = method switch
                {
                    "music.queue.mode" => new { mode = 2 },
                    "music.search.settings.save" => SearchParameters(),
                    _ => new { ifRevision = 0, enabled = new[] { "tx" } }
                };
                string read = method switch
                {
                    "music.queue.mode" => "music.queue.get",
                    "music.search.settings.save" => "music.search.settings.get",
                    _ => "music.officialSources.settings.get"
                };
                store.FailAfterWrite = true;
                await Code(owner, method, parameters, "COMMIT_UNKNOWN");
                int reads = store.Reads;
                await Code(owner, "music.catalogSource.settings.get", null, "PENDING_WRITE");
                await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }, "PENDING_WRITE");
                Equal(reads, store.Reads);
                await Ok(owner, read);
                Equal(reads + 1, store.Reads);
                Settings(await Ok(owner, "music.catalogSource.settings.get"), "wy", 0);
                Equal(1, store.Writes);
            });
        yield return Case("whole document CAS conflict remains fenced on get and save", async (owner, store) =>
        {
            await Ok(owner, "music.catalogSource.settings.get");
            await using var concurrent = new MusicLibraryOwner(store);
            await Ok(concurrent, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" });
            byte[] saved = store.Data!.ToArray();
            await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }, "STORAGE_CONFLICT");
            await Code(owner, "music.catalogSource.settings.get", null, "STORAGE_CONFLICT");
            await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "wy" }, "STORAGE_CONFLICT");
            Equal(1, store.Writes);
            Check(saved.SequenceEqual(store.Data!), "CAS conflict overwrote another owner");
        });
        yield return Case("unknown CAS cannot claim a third durable state as its receipt", async (owner, store) =>
        {
            store.FailAfterWrite = true;
            await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }, "COMMIT_UNKNOWN");
            await using var concurrent = new MusicLibraryOwner(store);
            await Ok(concurrent, "music.catalogSource.settings.save", new { ifRevision = 1, platform = "wy" });
            byte[] saved = store.Data!.ToArray();
            await Code(owner, "music.catalogSource.settings.get", null, "STORAGE_CONFLICT");
            await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 2, platform = "kg" }, "PENDING_WRITE");
            await Code(owner, "music.catalogSource.settings.get", null, "STORAGE_CONFLICT");
            Equal(2, store.Writes);
            Check(saved.SequenceEqual(store.Data!), "unknown CAS changed the third durable state");
        });
        yield return Case("source preferences preserve every existing entry and playback and search state", Independent);
        yield return Case("cancellation before admission cannot write", async (owner, store) =>
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var reply = await owner.ExecuteAsync("music.catalogSource.settings.save",
                new { ifRevision = 0, platform = "kg" }, cancel.Token);
            Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "CANCELLED",
                reply.GetRawText());
            Settings(await Ok(owner, "music.catalogSource.settings.get"), "wy", 0);
            Equal(0, store.Writes);
        });
        yield return new("catalog source local document storage retains KG across store and owner restarts", LocalStoreRestart);
    }

    private static async Task Unknown(MusicLibraryOwner owner, Store store, bool after)
    {
        store.FailBeforeWrite = !after;
        store.FailAfterWrite = after;
        await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }, "COMMIT_UNKNOWN");
        int reads = store.Reads;
        await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "wy" }, "PENDING_WRITE");
        foreach (string method in new[]
        {
            "music.queue.get", "music.search.settings.get", "music.officialSources.settings.get", "music.quality.settings.get"
        })
            await Code(owner, method, null, "PENDING_WRITE");
        Equal(reads, store.Reads);
        Equal(1, store.Writes);
        store.FailRead = true;
        await Code(owner, "music.catalogSource.settings.get", null, "STORAGE_UNAVAILABLE");
        await Code(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }, "PENDING_WRITE");
        store.FailRead = false;
        Settings(await Ok(owner, "music.catalogSource.settings.get"), after ? "kg" : "wy", after ? 1 : 0);
        Settings(await Ok(owner, "music.catalogSource.settings.get"), after ? "kg" : "wy", after ? 1 : 0);
        Equal(1, store.Writes);
        Equal(reads + 2, store.Reads);
        if (!after) Equal<byte[]?>(null, store.Data);
        Settings(await Ok(owner, "music.catalogSource.settings.save",
            new { ifRevision = after ? 1 : 0, platform = after ? "wy" : "kg" }), after ? "wy" : "kg", after ? 2 : 1);
        Equal(2, store.Writes);
    }

    private static async Task Independent(MusicLibraryOwner owner, Store store)
    {
        var metadata = await owner.ReadSourceMetadataAsync();
        var sources = JsonNode.Parse(metadata.State.GetRawText())!.AsObject();
        sources["revision"] = 1;
        sources["installations"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        {
            id = "catalog-source-test", name = "Synthetic source", enabled = true, previous = (object?)null,
            current = new { sourceHash = new string('a', 64), sourceBytes = 1, version = "1",
                permissions = new { storage = new[] { "read", "write" } } }
        }));
        await owner.CommitSourceMetadataAsync(JsonSerializer.SerializeToElement(sources), metadata.Sha256);
        await Ok(owner, "music.settings.save", new { ifRevision = 0, lyrics = false });
        await Ok(owner, "music.officialSources.settings.save", new { ifRevision = 0, enabled = new[] { "tx" } });
        await Ok(owner, "music.search.settings.save", SearchParameters());
        await Ok(owner, "music.search.history.record", new { text = "Synthetic search", kind = "songs" });
        await owner.CommitScriptValueAsync("catalog-source-test", "wy", "synthetic", 1,
            JsonSerializer.SerializeToElement("retained"), false);
        var song = MusicSongFixture.Online("tx", "catalog-source-song", "Synthetic song");
        await Ok(owner, "music.library.create", new { name = "Synthetic playlist", songs = new[] { song } });
        await Ok(owner, "music.queue.replace", new { songs = new[] { song }, index = 0 });
        await Ok(owner, "music.queue.mode", new { mode = 2 });
        string[] methods = ["music.settings.get", "music.officialSources.settings.get", "music.search.settings.get",
            "music.search.history.get", "music.queue.get", "music.library.list"];
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string method in methods) before[method] = (await Ok(owner, method)).GetRawText();
        var document = JsonNode.Parse(store.Data!)!.AsObject();
        byte[] original = store.Data!.ToArray();
        int writes = store.Writes;
        Settings(await Ok(owner, "music.catalogSource.settings.get"), "wy", 0);
        Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "wy" }), "wy", 0);
        Equal(writes, store.Writes);
        Check(original.SequenceEqual(store.Data!), "default settings changed existing document");
        Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }), "kg", 1);
        var after = JsonNode.Parse(store.Data!)!.AsObject();
        Equal(document["entries"]!.AsArray().Count + 1, after["entries"]!.AsArray().Count);
        foreach (var entry in document["entries"]!.AsArray())
        {
            var retained = after["entries"]!.AsArray().Single(value =>
                value!["key"]!.GetValue<string>() == entry!["key"]!.GetValue<string>());
            Check(JsonNode.DeepEquals(entry, retained), "source save changed an existing entry");
        }
        Equal(document["version"]!.GetValue<int>(), after["version"]!.GetValue<int>());
        foreach (string method in methods) Equal(before[method], (await Ok(owner, method)).GetRawText());
        await Ok(owner, "music.settings.save", new { ifRevision = 1, lyrics = true });
        await Ok(owner, "music.officialSources.settings.save", new { ifRevision = 1, enabled = Array.Empty<string>() });
        var search = JsonNode.Parse(JsonSerializer.Serialize(SearchParameters()))!.AsObject();
        search["ifRevision"] = 1;
        search["songs"]!["enabled"] = new JsonArray();
        await Ok(owner, "music.search.settings.save", search);
        await Ok(owner, "music.queue.mode", new { mode = 3 });
        Settings(await Ok(owner, "music.catalogSource.settings.get"), "kg", 1);
        await owner.AbortAsync();
        await using var reopened = new MusicLibraryOwner(store);
        Settings(await Ok(reopened, "music.catalogSource.settings.get"), "kg", 1);
        Equal("tx", (await Ok(reopened, "music.queue.get")).GetProperty("current").GetProperty("song")
            .GetProperty("sources")[0].GetProperty("source").GetString());
        Equal(3, (await Ok(reopened, "music.queue.get")).GetProperty("mode").GetInt32());
    }

    private static async Task LocalStoreRestart()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
            ?? throw new InvalidOperationException("Isolated test root required"), "catalog-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var store = new LocalDocumentStore(root, [MusicLibraryOwner.DocumentNamespace]))
        await using (var owner = new MusicLibraryOwner(store))
            Settings(await Ok(owner, "music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" }), "kg", 1);
        await using var reopenedStore = new LocalDocumentStore(root, [MusicLibraryOwner.DocumentNamespace]);
        await using var reopened = new MusicLibraryOwner(reopenedStore);
        Settings(await Ok(reopened, "music.catalogSource.settings.get"), "kg", 1);
    }

    private static object SearchParameters()
    {
        string[] order = ["mg", "kg", "kw", "tx", "wy"];
        return new
        {
            ifRevision = 0,
            songs = new { order, enabled = new[] { "tx" }, pages = 3, concurrency = 2, cacheMinutes = 5 },
            playlists = new { order, enabled = new[] { "mg" }, pages = 2, concurrency = 3, cacheMinutes = 10 }
        };
    }

    private static TestCase Case(string name, Func<MusicLibraryOwner, Store, Task> run) =>
        new("catalog source " + name, async () =>
        {
            await using var store = new Store();
            var owner = new MusicLibraryOwner(store);
            try { await run(owner, store); }
            finally { await owner.AbortAsync(); }
        });

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");

    private static void Settings(JsonElement reply, string platform, long revision)
    {
        var settings = reply.GetProperty("settings");
        Equal(3, settings.EnumerateObject().Count());
        Equal(1, settings.GetProperty("schema").GetInt32());
        Equal(revision, settings.GetProperty("revision").GetInt64());
        Equal(platform, settings.GetProperty("platform").GetString());
    }

    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? parameters = null)
    {
        var reply = await owner.ExecuteAsync(method, parameters);
        Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
        return reply;
    }

    private static async Task Code(MusicLibraryOwner owner, string method, object? parameters, string expected)
    {
        var reply = await owner.ExecuteAsync(method, parameters);
        Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == expected,
            method + ": expected " + expected + ", got " + reply.GetRawText());
    }

    private sealed class Store : IDocumentStore
    {
        internal byte[]? Data;
        internal int Reads, Writes;
        internal bool FailBeforeWrite, FailAfterWrite, FailRead;

        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equal(MusicLibraryOwner.DocumentNamespace, documentNamespace);
            Reads++;
            if (FailRead) throw new DocumentStoreException(DocumentStoreError.Unavailable);
            byte[]? copy = Data?.ToArray();
            return Task.FromResult(new DocumentSnapshot(copy,
                copy is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(copy)));
        }

        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> value,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equal(MusicLibraryOwner.DocumentNamespace, documentNamespace);
            var actual = Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data);
            if (actual != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            Writes++;
            if (FailBeforeWrite)
            {
                FailBeforeWrite = false;
                throw new IOException("synthetic failure before CAS");
            }
            Data = value.ToArray();
            if (FailAfterWrite)
            {
                FailAfterWrite = false;
                throw new IOException("synthetic receipt loss after CAS");
            }
            return Task.FromResult(DocumentFingerprint.FromBytes(Data));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
