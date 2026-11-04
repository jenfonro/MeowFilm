using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicOfficialSourcesPolicyTests
{
    private static readonly string[] Platforms = ["wy", "tx", "kg"];

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("official source policy defaults all built-in providers on without a write", async (owner, store) =>
        {
            var settings = Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
            Equal(1, settings.GetProperty("schema").GetInt32());
            Equal(0L, settings.GetProperty("revision").GetInt64());
            Enabled(settings, Platforms);
            Equal(0, store.Writes);
            Equal<byte[]?>(null, store.Data);
        });
        yield return Case("official source switches independently disable and reenable all three built-in providers", async (owner, store) =>
        {
            var enabled = Platforms.ToHashSet(StringComparer.Ordinal);
            long revision = 0;
            foreach (string platform in Platforms)
            {
                enabled.Remove(platform);
                var selected = Platforms.Where(enabled.Contains).ToArray();
                var saved = Ok(await owner.ExecuteAsync("music.officialSources.settings.save",
                    new { ifRevision = revision, enabled = selected })).GetProperty("settings");
                Enabled(saved, selected);
                Equal(++revision, saved.GetProperty("revision").GetInt64());
                Enabled(Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings"), selected);
            }
            foreach (string platform in Platforms)
            {
                enabled.Add(platform);
                var selected = Platforms.Where(enabled.Contains).ToArray();
                var saved = Ok(await owner.ExecuteAsync("music.officialSources.settings.save",
                    new { ifRevision = revision, enabled = selected })).GetProperty("settings");
                Enabled(saved, selected);
                Equal(++revision, saved.GetProperty("revision").GetInt64());
            }
            Equal(6, store.Writes);
        });
        yield return Case("official source switches and an empty enabled set persist through sole-owner restarts", async (owner, store) =>
        {
            Ok(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = new[] { "wy", "kg" } }));
            var entry = JsonNode.Parse(store.Data!)!["entries"]!.AsArray().Single()!;
            Equal("""["meowfilm","music",1,"local","native-official-sources"]""",
                entry["key"]!.GetValue<string>());
            Equal(1, entry["value"]!["schema"]!.GetValue<int>());
            await owner.AbortAsync();
            await using (var reopened = new MusicLibraryOwner(store))
            {
                var settings = Ok(await reopened.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
                Enabled(settings, ["wy", "kg"]);
                Equal(1L, settings.GetProperty("revision").GetInt64());
                var empty = Ok(await reopened.ExecuteAsync("music.officialSources.settings.save",
                    new { ifRevision = 1, enabled = Array.Empty<string>() })).GetProperty("settings");
                Enabled(empty, []);
                Equal(2L, empty.GetProperty("revision").GetInt64());
            }
            await using var finalOwner = new MusicLibraryOwner(store);
            var final = Ok(await finalOwner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
            Enabled(final, []);
            Equal(2L, final.GetProperty("revision").GetInt64());
            Equal(2, store.Writes);
        });
        yield return Case("official source policy rejects duplicates unknown platforms and stale revisions", async (owner, store) =>
        {
            foreach (string input in new[]
            {
                """{"ifRevision":0,"enabled":["wy","wy"]}""",
                """{"ifRevision":0,"enabled":["elsewhere"]}""",
                """{"ifRevision":0,"enabled":["wy","tx","kg","kw"]}""",
                """{"ifRevision":0,"enabled":["WY"]}""",
                """{"ifRevision":0,"enabled":[null]}""",
                """{"ifRevision":0,"enabled":[true]}""",
                """{"ifRevision":0,"enabled":[1]}""",
                """{"ifRevision":0,"enabled":null}""",
                """{"ifRevision":0,"enabled":"wy"}""",
                """{"ifRevision":0,"enabled":{}}""",
                """{"ifRevision":0}""",
                """{"enabled":[]}""",
                """{"ifRevision":"0","enabled":[]}""",
                """{"ifRevision":-1,"enabled":[]}""",
                """{"ifRevision":0.5,"enabled":[]}""",
                """{"ifRevision":0,"enabled":[],"extra":true}"""
            })
                Code(await owner.ExecuteAsync("music.officialSources.settings.save", JsonNode.Parse(input)), "INVALID_REQUEST");
            using var duplicate = JsonDocument.Parse("""{"ifRevision":0,"enabled":["wy"],"enabled":[]}""");
            Code(await owner.ExecuteAsync("music.officialSources.settings.save", duplicate.RootElement), "INVALID_REQUEST");
            Code(await owner.ExecuteAsync("music.officialSources.settings.get", new { extra = true }), "INVALID_REQUEST");
            Equal(0, store.Writes);
            var unchanged = Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
            Enabled(unchanged, Platforms);
            Equal(0L, unchanged.GetProperty("revision").GetInt64());
            Ok(await owner.ExecuteAsync("music.officialSources.settings.save", new { ifRevision = 0, enabled = new[] { "wy" } }));
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Platforms }), "REVISION_CONFLICT");
            Enabled(Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings"), ["wy"]);
            Equal(1, store.Writes);
        });
        yield return Case("malformed durable official source policy is corruption and is not silently reset", async (owner, store) =>
        {
            Ok(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = new[] { "wy" } }));
            await owner.AbortAsync();
            byte[] original = store.Data!.ToArray();
            foreach (string value in new[]
            {
                "null", "[]",
                """{"schema":2,"revision":1,"enabled":["wy"]}""",
                """{"schema":1,"revision":-1,"enabled":["wy"]}""",
                """{"schema":1,"revision":"1","enabled":["wy"]}""",
                """{"schema":1,"revision":1,"enabled":["invalid"]}""",
                """{"schema":1,"revision":1,"enabled":["wy","wy"]}""",
                """{"schema":1,"revision":1,"enabled":[false]}""",
                """{"schema":1,"revision":1,"enabled":null}""",
                """{"schema":1,"revision":1}""",
                """{"schema":1,"revision":1,"enabled":[],"extra":true}"""
            })
            {
                var document = JsonNode.Parse(original)!.AsObject();
                document["entries"]!.AsArray().OfType<JsonObject>().Single()["value"] = JsonNode.Parse(value);
                // Keep the outer document canonical, so the policy itself is what must fail.
                store.Data = LJ.Bytes(document);
                await using var reopened = new MusicLibraryOwner(store);
                Code(await reopened.ExecuteAsync("music.officialSources.settings.get"), "CORRUPT_STORAGE");
                Code(await reopened.ExecuteAsync("music.officialSources.settings.save",
                    new { ifRevision = 1, enabled = Platforms }), "CORRUPT_STORAGE");
                Equal(1, store.Writes);
            }
        });
        yield return Case("unknown official source settings CAS reconciles on its authoritative current read", async (owner, store) =>
        {
            store.FailAfterWrite = true;
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = new[] { "wy", "kg" } }), "COMMIT_UNKNOWN");
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Array.Empty<string>() }), "PENDING_WRITE");
            Code(await owner.ExecuteAsync("music.quality.settings.get"), "PENDING_WRITE");
            store.FailRead = true;
            Code(await owner.ExecuteAsync("music.officialSources.settings.get"), "STORAGE_UNAVAILABLE");
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 1, enabled = Platforms }), "PENDING_WRITE");
            store.FailRead = false;
            var current = Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
            Enabled(current, ["wy", "kg"]);
            Equal(1L, current.GetProperty("revision").GetInt64());
            Ok(await owner.ExecuteAsync("music.quality.settings.get"));
            Equal(1, store.Writes);
        });
        yield return Case("unknown official source CAS before a physical write reads the old state without replay", async (owner, store) =>
        {
            store.FailBeforeWrite = true;
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Array.Empty<string>() }), "COMMIT_UNKNOWN");
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Platforms }), "PENDING_WRITE");
            var current = Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
            Enabled(current, Platforms);
            Equal(0L, current.GetProperty("revision").GetInt64());
            Equal(1, store.Writes);
            Equal<byte[]?>(null, store.Data);
            Ok(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Array.Empty<string>() }));
            Equal(2, store.Writes);
        });
        yield return Case("official source storage conflict never becomes an authoritative-read success", async (owner, store) =>
        {
            Ok(await owner.ExecuteAsync("music.officialSources.settings.get"));
            store.Data = LJ.Bytes(LJ.Obj(("version", 1), ("entries", new JsonArray())));
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Array.Empty<string>() }), "STORAGE_CONFLICT");
            Code(await owner.ExecuteAsync("music.officialSources.settings.get"), "STORAGE_CONFLICT");
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Platforms }), "STORAGE_CONFLICT");
            Equal(0, store.Writes);
        });
        yield return Case("cancelled official source save cannot change settings or admit a write", async (owner, store) =>
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Code(await owner.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Array.Empty<string>() }, cancel.Token), "CANCELLED");
            Enabled(Ok(await owner.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings"), Platforms);
            Equal(0, store.Writes);
        });
    }

    private static TestCase Case(string name, Func<MusicLibraryOwner, PolicyStore, Task> run) =>
        new(name, async () =>
        {
            await using var store = new PolicyStore();
            var owner = new MusicLibraryOwner(store);
            try { await run(owner, store); }
            finally { await owner.AbortAsync(); }
        });

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private static void Enabled(JsonElement settings, string[] expected) =>
        Equal(string.Join(',', expected), string.Join(',', settings.GetProperty("enabled").EnumerateArray()
            .Select(value => value.GetString())));

    private static JsonElement Ok(JsonElement result)
    {
        if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetRawText());
        return result;
    }

    private static void Code(JsonElement result, string expected)
    {
        if (result.GetProperty("ok").GetBoolean() || result.GetProperty("code").GetString() != expected)
            throw new InvalidOperationException($"{expected}: {result.GetRawText()}");
    }

    private sealed class PolicyStore : IDocumentStore
    {
        internal byte[]? Data;
        internal int Writes;
        internal bool FailAfterWrite, FailBeforeWrite, FailRead;

        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Equal(MusicLibraryOwner.DocumentNamespace, documentNamespace);
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
                throw new IOException("synthetic failure before physical write");
            }
            Data = value.ToArray();
            var next = DocumentFingerprint.FromBytes(Data);
            if (FailAfterWrite)
            {
                FailAfterWrite = false;
                throw new IOException("synthetic receipt loss");
            }
            return Task.FromResult(next);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
