using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicSongMatchStoreTests
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music song matches roundtrip canonical recordings all platforms and exact observation states", Roundtrip);
        yield return new("Music song matches decode consumed facts and ignore unused fields", UnusedFields);
        yield return new("Music song matches empty snapshot removes all observations without changing library", EmptySnapshot);
        yield return new("Music song matches damaged content misses and fresh snapshots replace it", Damaged);
        yield return new("Music song matches storage errors are optional and writes retain memory", StorageFailures);
        yield return new("Music song matches slow initial reads cannot block accepted snapshots", SlowRead);
        yield return new("Music song matches never persist credentials playback URLs or candidate pages", Secrets);
        yield return new("Music song matches capacity evicts oldest whole entries and retains input order", Capacity);
        yield return new("Music song matches oversized individual entry is evicted whole", OversizedEntry);
        yield return new("Music song matches cancelled saves do not change accepted facts", Cancellation);
        yield return new("Music song matches cancelled cold reads preserve other readers and owner drain", CancelledRead);
        yield return new("Music matching eligibility includes only deduplicated active library members", Eligibility);
        yield return new("Music matching eligibility gate preparation cancellation and closure preserve stable snapshots", EligibilityLifecycle);
        yield return new("Music matching eligibility fences unknown library commits until reconciliation", EligibilityPending);
        yield return new("Music matching eligibility fences library CAS conflicts and read failures", EligibilityFailures);
    }

    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }

    private static JsonObject Entry(string id, JsonObject? song = null) => new()
    {
        ["key"] = NativeMusicSong.Hash("matching:" + id),
        ["song"] = song?.DeepClone() ?? MusicSongFixture.Online("wy", id, "Match " + id, "Artist"),
        ["platforms"] = new JsonObject { ["wy"] = Status("matched") },
        ["excluded"] = new JsonArray()
    };

    private static JsonObject Status(string state, long at = 0, string context = "") => new()
    {
        ["state"] = state,
        ["checkedAt"] = at,
        ["context"] = context,
        ["query"] = "歌曲 Artist 🎵",
        ["code"] = "",
        ["page"] = 1, ["pages"] = 1, ["limit"] = 30,
        ["matchedAt"] = state == "matched" ? JsonValue.Create(at) : null
    };

    private static byte[] Document(params JsonObject[] entries) => JsonSerializer.SerializeToUtf8Bytes(
        new JsonObject { ["entries"] = LJ.Arr(entries) }, LJ.Json);
    private static JsonArray Excluded(params string[] sources) => new(new JsonObject
    {
        ["key"] = NativeMusicSong.Hash("original-recording"), ["sources"] = LJ.Arr(sources)
    });
    private static string Key(JsonObject entry) => entry["key"]!.GetValue<string>();
    private static JsonObject Observation(JsonObject entry) => entry["platforms"]!["wy"]!.AsObject();

    private static async Task LibraryError(string expected, Func<Task> action)
    {
        try { await action(); }
        catch (MusicLibraryException error) { Check(error.Code == expected, error.Code); return; }
        throw new InvalidOperationException("Expected " + expected);
    }

    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }

    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? parameters = null)
    {
        var result = await owner.ExecuteAsync(method, parameters);
        Check(result.GetProperty("ok").GetBoolean(), method + ": " + result);
        return result;
    }

    private static async Task Roundtrip()
    {
        await using var store = new Store();
        await using var cacheStore = new CacheDocumentStore(store);
        var cache = new MusicSongMatchStore(cacheStore);
        Check((await cache.ReadAsync(default)).Count == 0 && store.WriteCalls == 0);
        var song = MusicSongFixture.Online("wy", "roundtrip", "Recording", "Artist");
        foreach (string platform in new[] { "tx", "kw", "kg", "mg" })
        {
            var native = platform switch
            {
                "tx" => new JsonObject { ["strMediaMid"] = "media-mid", ["qualityKnown"] = true,
                    ["types"] = new JsonArray(new JsonObject { ["type"] = "flac", ["hash"] = "format-hash", ["size"] = "1024" }) },
                "kg" => new JsonObject { ["hash"] = "abcdef0123456789", ["albumAudioId"] = "audio-id" },
                "mg" => new JsonObject { ["contentId"] = "content-id", ["audioFormats"] = new JsonArray(
                    new JsonObject { ["formatType"] = "flac", ["fileId"] = "file-id", ["size"] = 1024 }) },
                _ => new JsonObject()
            };
            var other = MusicSongFixture.Online(platform, "roundtrip-" + platform, "Recording", "Artist", native: native);
            song["sources"]!.AsArray().Add(other["sources"]![0]!.DeepClone());
        }
        song = NativeMusicSong.Normalize(song);
        var entry = Entry("roundtrip", song);
        entry["platforms"] = new JsonObject
        {
            ["wy"] = Status("matched"),
            ["tx"] = Status("notFound", 1700000000123, NativeMusicSong.Hash("tx-context")),
            ["kw"] = Status("failed", 1700000000124, NativeMusicSong.Hash("kw-context")),
            ["kg"] = Status("matched", 1700000000125),
            ["mg"] = Status("notFound", 1700000000126)
        };
        entry["platforms"]!["kw"]!["code"] = "NETWORK_FAILURE";
        entry["platforms"]!["tx"]!["query"] = new string('q', 256);
        entry["platforms"]!["tx"]!["code"] = new string('c', 80);
        entry["excluded"] = Excluded(NativeMusicSong.SourceKey(
            MusicSongFixture.Online("tx", "rejected-recording", "Other recording", "Artist")));
        var expected = entry.DeepClone();
        await cache.WriteAsync([entry], default);
        await cacheStore.DrainAsync();
        Check(store.WriteCalls == 1 && store.WrittenNamespaces.SequenceEqual(new[] { "music.song-matches.v1" }));
        var root = JsonNode.Parse(store.Bytes(MusicSongMatchStore.Namespace)!)!.AsObject();
        Check(root.Select(field => field.Key).SequenceEqual(new[] { "entries" }) && root["entries"]!.AsArray().Count == 1);
        entry["song"]!["title"] = "Changed caller";
        await using var restarted = new CacheDocumentStore(store);
        var first = (await new MusicSongMatchStore(restarted).ReadAsync(default)).Single();
        Check(JsonNode.DeepEquals(first, expected) && Key(first).Length == 16);
        first["song"]!["sources"]!.AsArray().Clear();
        Check(JsonNode.DeepEquals((await cache.ReadAsync(default)).Single(), expected));
        Check(store.ReadNamespaces.All(value => value == MusicSongMatchStore.Namespace));
    }

    private static async Task UnusedFields()
    {
        await using var backing = new Store();
        var entry = Entry("unused");
        entry["excluded"] = Excluded(NativeMusicSong.SourceKey(
            MusicSongFixture.Online("tx", "rejected", "Other")));
        var expected = entry.DeepClone();
        foreach (var owner in new[]
        {
            entry, entry["song"]!.AsObject(), entry["song"]!["sources"]![0]!.AsObject(),
            entry["song"]!["sources"]![0]!["musicInfo"]!.AsObject(),
            entry["platforms"]!["wy"]!.AsObject(), entry["excluded"]![0]!.AsObject()
        })
            owner["unused"] = new JsonObject { ["nested"] = "https://unused.invalid/not-consumed" };
        backing.Seed(MusicSongMatchStore.Namespace, Document(entry));
        await using var store = new CacheDocumentStore(backing);
        var decoded = (await new MusicSongMatchStore(store).ReadAsync(default)).Single();
        Check(JsonNode.DeepEquals(decoded, expected),
            "only consumed normalized recording facts and observations are returned");
    }

    private static async Task EmptySnapshot()
    {
        await using var store = new Store();
        byte[] library = "unrelated-library-bytes"u8.ToArray();
        store.Seed(MusicLibraryOwner.DocumentNamespace, library);
        await using var cacheStore = new CacheDocumentStore(store);
        var cache = new MusicSongMatchStore(cacheStore);
        await cache.WriteAsync([], default);
        Check((await cache.ReadAsync(default)).Count == 0);
        var unsearched = Entry("unsearched", MusicSongFixture.Local("library:unsearched"));
        unsearched["platforms"] = new JsonObject();
        await cache.WriteAsync([Entry("one"), unsearched], default);
        Check(JsonNode.DeepEquals((await cache.ReadAsync(default))[1], unsearched));
        await cache.WriteAsync([], default);
        await cacheStore.DrainAsync();
        Check((await cache.ReadAsync(default)).Count == 0);
        Check(store.Bytes(MusicLibraryOwner.DocumentNamespace)!.SequenceEqual(library) &&
            store.ReadNamespaces.All(value => value == MusicSongMatchStore.Namespace));
        Check(JsonNode.Parse(store.Bytes(MusicSongMatchStore.Namespace)!)!["entries"]!.AsArray().Count == 0);
    }

    private static async Task Secrets()
    {
        await using var store = new CacheDocumentStore();
        var cache = new MusicSongMatchStore(store);
        var entry = Entry("safe");
        entry["cookie"] = "secret"; entry["results"] = new JsonArray(Entry("candidate"));
        entry["platforms"]!["wy"]!["token"] = "secret";
        await cache.WriteAsync([entry], default);
        string output = Encoding.UTF8.GetString(store.ReadMemory(MusicSongMatchStore.Namespace).Data!);
        Check(!output.Contains("secret") && !output.Contains("results") && !output.Contains("candidate"));
        foreach (string field in new[] { "query", "code" })
        {
            var unsafeEntry = Entry("unsafe"); unsafeEntry["platforms"]!["wy"]![field] = "https://media.invalid/audio?token=secret";
            await cache.WriteAsync([unsafeEntry], default);
            Check(Key((await cache.ReadAsync(default)).Single()) == Key(entry));
        }
        var credential = Entry("unsafe-song");
        MusicSongFixture.Info(credential["song"]!)["headers"] = new JsonObject { ["Authorization"] = "secret" };
        await cache.WriteAsync([credential], default);
        Check(Key((await cache.ReadAsync(default)).Single()) == Key(entry));
    }

    private static async Task Damaged()
    {
        foreach (byte[] bytes in new[] { "{"u8.ToArray(), "{\"entries\":null}"u8.ToArray(),
            "{\"entries\":[{}]}"u8.ToArray() })
        {
            await using var backing = new Store();
            backing.Seed(MusicSongMatchStore.Namespace, bytes);
            await using var store = new CacheDocumentStore(backing);
            var cache = new MusicSongMatchStore(store);
            Check((await cache.ReadAsync(default)).Count == 0);
            await cache.WriteAsync([Entry("new")], default);
            Check(Key((await cache.ReadAsync(default)).Single()) == Key(Entry("new")));
        }
    }
    private static async Task StorageFailures()
    {
        foreach (var error in new[] { DocumentStoreError.Protection, DocumentStoreError.Storage, DocumentStoreError.Unavailable })
        {
            await using var backing = new Store { ReadError = error, WriteError = error };
            await using var store = new CacheDocumentStore(backing);
            var cache = new MusicSongMatchStore(store);
            Check((await cache.ReadAsync(default)).Count == 0);
            await cache.WriteAsync([Entry("new")], default);
            Check(Key((await cache.ReadAsync(default)).Single()) == Key(Entry("new")));
            await store.DrainAsync();
        }
    }
    private static async Task SlowRead()
    {
        var backing = new SlowStore();
        await using var store = new CacheDocumentStore(backing);
        var cache = new MusicSongMatchStore(store);
        var reading = cache.ReadAsync(default);
        await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await cache.WriteAsync([Entry("new")], default).WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { backing.Release.TrySetResult(); }
        Check(Key((await reading).Single()) == Key(Entry("new")));
        await store.DrainAsync();
    }
    private sealed class SlowStore(byte[]? data = null) : IDocumentStore
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(data, data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default) =>
            Task.FromResult(DocumentFingerprint.FromBytes(data.Span));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Capacity()
    {
        var entries = Enumerable.Range(0, 22).Select(i => Entry("capacity-" + i,
            MusicSongFixture.Local("library:capacity:" + i, "Sized " + i, "Artist", new string('x', 256000)))).ToArray();
        Check(Document(entries.TakeLast(16).ToArray()).Length <= MaximumBytes &&
            Document(entries.TakeLast(17).ToArray()).Length > MaximumBytes);
        await using var store = new Store();
        await using var cacheStore = new CacheDocumentStore(store);
        var cache = new MusicSongMatchStore(cacheStore);
        await cache.WriteAsync(entries, default);
        await cacheStore.DrainAsync();
        var restored = await cache.ReadAsync(default);
        Check(store.Bytes(MusicSongMatchStore.Namespace)!.Length <= MaximumBytes &&
            restored.Select(Key).SequenceEqual(entries.TakeLast(16).Select(Key)));
        for (int i = 0; i < restored.Count; i++) Check(JsonNode.DeepEquals(restored[i], entries[6 + i]));
    }

    private static async Task OversizedEntry()
    {
        var huge = Entry("oversized", MusicSongFixture.Local("library:oversized", "Large", album: new string('x', MaximumBytes)));
        await using var store = new Store();
        await using var cacheStore = new CacheDocumentStore(store);
        var cache = new MusicSongMatchStore(cacheStore);
        await cache.WriteAsync([Entry("old"), huge, Entry("new")], default);
        await cacheStore.DrainAsync();
        Check(Key((await cache.ReadAsync(default)).Single()) == Key(Entry("new")) &&
            store.Bytes(MusicSongMatchStore.Namespace)!.Length <= MaximumBytes);
        await cache.WriteAsync([huge], default);
        Check((await cache.ReadAsync(default)).Count == 0);
    }

    private static async Task Cancellation()
    {
        await using var store = new CacheDocumentStore();
        var cache = new MusicSongMatchStore(store);
        await cache.WriteAsync([Entry("accepted")], default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => cache.WriteAsync([Entry("cancelled")], cancelled.Token));
        await Cancelled(async () => _ = await cache.ReadAsync(cancelled.Token));
        Check(Key((await cache.ReadAsync(default)).Single()) == Key(Entry("accepted")));
    }

    private static async Task CancelledRead()
    {
        var backing = new SlowStore(Document(Entry("stored")));
        await using var store = new CacheDocumentStore(backing);
        var cache = new MusicSongMatchStore(store);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = cache.ReadAsync(cancellation.Token);
        var stableRead = cache.ReadAsync(default);
        try
        {
            await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!cancelledRead.IsCompleted && !stableRead.IsCompleted);
            cancellation.Cancel();
            await Cancelled(() => cancelledRead.WaitAsync(TimeSpan.FromSeconds(5)));
            var draining = store.DrainAsync();
            Check(!stableRead.IsCompleted && !draining.IsCompleted);
            backing.Release.TrySetResult();
            Check(Key((await stableRead.WaitAsync(TimeSpan.FromSeconds(5))).Single()) == Key(Entry("stored")));
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.WriteAsync([Entry("after-cancellation")], default);
            Check(Key((await cache.ReadAsync(default)).Single()) == Key(Entry("after-cancellation")));
            await store.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { backing.Release.TrySetResult(); }
    }

    private static async Task Eligibility()
    {
        await using var store = new Store();
        await using var owner = new MusicLibraryOwner(store, "matching-scope");
        Check((await owner.ReadMatchingSongsAsync(default)).Count == 0 && store.WriteCalls == 0);
        var shared = MusicSongFixture.Online("wy", "shared", "Shared", "Artist");
        var removed = MusicSongFixture.Local("library:removed", "Removed");
        var member = MusicSongFixture.Local("library:member", "Member");
        var deleted = MusicSongFixture.Local("library:deleted", "Deleted");
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 0, songs = new[] { shared, removed } });
        await Ok(owner, "music.library.removeSong", new { id = "favorites", ifRevision = 1, songId = "library:removed" });
        await Ok(owner, "music.library.create", new { name = "Active", songs = new[] { shared, member } });
        await Ok(owner, "music.library.create", new { name = "Deleted", songs = new[] { deleted } });
        await Ok(owner, "music.library.remove", new { id = "playlist:2", ifRevision = 1 });
        await Ok(owner, "music.queue.replace", new { songs = new[] { MusicSongFixture.Local("library:history-only") }, index = 0 });
        string occurrence = (await Ok(owner, "music.queue.get")).GetProperty("current").GetProperty("id").GetString()!;
        await Ok(owner, "music.history.recordPlayed", new { entryId = occurrence, at = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        await Ok(owner, "music.queue.replace", new { songs = new[] { MusicSongFixture.Local("library:queue-only") }, index = 0 });
        int reads = store.ReadCalls, writes = store.WriteCalls;
        byte[] before = store.Bytes(MusicLibraryOwner.DocumentNamespace)!;
        var eligible = await owner.ReadMatchingSongsAsync(default);
        Check(eligible.Count == 2 && eligible.Select(song => LJ.S(song["songId"]))
            .SequenceEqual(new[] { LJ.S(shared["songId"]), "library:member" }));
        Check(JsonNode.DeepEquals(eligible[0], shared) && JsonNode.DeepEquals(eligible[1], member));
        eligible[0]["title"] = "Changed returned snapshot"; eligible[0]["sources"]!.AsArray().Clear();
        Check(JsonNode.DeepEquals((await owner.ReadMatchingSongsAsync(default))[0], shared));
        Check(reads == store.ReadCalls && writes == store.WriteCalls &&
            store.Bytes(MusicLibraryOwner.DocumentNamespace)!.SequenceEqual(before));
        await using (var restarted = new MusicLibraryOwner(store, "matching-scope"))
            Check((await restarted.ReadMatchingSongsAsync(default)).Count == 2);
        await Ok(owner, "music.library.removeSong", new { id = "playlist:1", ifRevision = 1, songId = "library:member" });
        Check((await owner.ReadMatchingSongsAsync(default)).Count == 1);
        await Ok(owner, "music.library.removeSong", new { id = "favorites", ifRevision = 2, songId = LJ.S(shared["songId"]) });
        Check((await owner.ReadMatchingSongsAsync(default)).Count == 1);
        await Ok(owner, "music.library.remove", new { id = "playlist:1", ifRevision = 2 });
        Check((await owner.ReadMatchingSongsAsync(default)).Count == 0);
        Check(store.ReadNamespaces.All(value => value == MusicLibraryOwner.DocumentNamespace) &&
            store.WrittenNamespaces.All(value => value == MusicLibraryOwner.DocumentNamespace));
    }

    private static async Task EligibilityLifecycle()
    {
        await using var store = new Store();
        await using var owner = new MusicLibraryOwner(store);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Cancelled(async () => await owner.ReadMatchingSongsAsync(cancelled.Token));
            Check(store.ReadCalls == 0);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeWrite = async _ => { entered.TrySetResult(); await release.Task; };
        var saving = owner.ExecuteAsync("music.library.addSongs",
            new { id = "favorites", ifRevision = 0, songs = new[] { MusicSongFixture.Local("library:admitted") } });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = owner.ReadMatchingSongsAsync(cancellation.Token);
        var stableRead = owner.ReadMatchingSongsAsync(default);
        bool blocked = !cancelledRead.IsCompleted && !stableRead.IsCompleted;
        cancellation.Cancel(); release.TrySetResult();
        Check((await saving).GetProperty("ok").GetBoolean());
        await Cancelled(async () => await cancelledRead);
        Check(blocked && (await stableRead).Single()["songId"]!.GetValue<string>() == "library:admitted");
        await owner.PrepareShutdownAsync();
        await LibraryError("CLOSED", async () => await owner.ReadMatchingSongsAsync(default));
        await owner.CancelShutdownPreparationAsync();
        Check((await owner.ReadMatchingSongsAsync(default)).Count == 1);
        await owner.AbortAsync();
        await LibraryError("CLOSED", async () => await owner.ReadMatchingSongsAsync(default));
    }

    private static async Task EligibilityPending()
    {
        foreach (bool committed in new[] { false, true })
        {
            await using var store = new Store { WriteError = DocumentStoreError.Storage, CommitBeforeFailure = committed };
            await using var owner = new MusicLibraryOwner(store);
            var result = await owner.ExecuteAsync("music.library.addSongs",
                new { id = "favorites", ifRevision = 0, songs = new[] { MusicSongFixture.Local("library:pending") } });
            Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
            int reads = store.ReadCalls;
            await LibraryError("COMMIT_UNKNOWN", async () => await owner.ReadMatchingSongsAsync(default));
            Check(store.ReadCalls == reads && store.WriteCalls == 1);
            store.WriteError = null;
            await owner.ReconcileAsync();
            Check((await owner.ReadMatchingSongsAsync(default)).Count == (committed ? 1 : 0) && store.WriteCalls == 1);
        }
    }

    private static async Task EligibilityFailures()
    {
        await using (var store = new Store())
        {
            await using var first = new MusicLibraryOwner(store);
            await using var second = new MusicLibraryOwner(store);
            Check((await first.ReadMatchingSongsAsync(default)).Count == 0);
            await Ok(second, "music.library.addSongs",
                new { id = "favorites", ifRevision = 0, songs = new[] { MusicSongFixture.Local("library:winner") } });
            var conflict = await first.ExecuteAsync("music.library.addSongs",
                new { id = "favorites", ifRevision = 0, songs = new[] { MusicSongFixture.Local("library:loser") } });
            Check(conflict.GetProperty("code").GetString() == "STORAGE_CONFLICT");
            int writes = store.WriteCalls;
            await LibraryError("STORAGE_CONFLICT", async () => await first.ReadMatchingSongsAsync(default));
            Check(store.WriteCalls == writes);
            await first.AbortAsync();
        }
        await using (var damaged = new Store())
        {
            damaged.Seed(MusicLibraryOwner.DocumentNamespace, "{\"version\":2,\"entries\":[]}"u8.ToArray());
            await using var owner = new MusicLibraryOwner(damaged);
            await LibraryError("CORRUPT_STORAGE", async () => await owner.ReadMatchingSongsAsync(default));
            Check(damaged.WriteCalls == 0);
        }
        await using var unavailable = new Store { ReadError = DocumentStoreError.Unavailable };
        await using var unavailableOwner = new MusicLibraryOwner(unavailable);
        bool surfaced = false;
        try { await unavailableOwner.ReadMatchingSongsAsync(default); }
        catch (DocumentStoreException error) { surfaced = error.Error == DocumentStoreError.Unavailable; }
        Check(surfaced && unavailable.WriteCalls == 0);
    }

    private sealed class Store : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> documents = new(StringComparer.Ordinal);
        internal readonly List<string> ReadNamespaces = [], WrittenNamespaces = [];
        internal int ReadCalls, WriteCalls;
        internal DocumentStoreError? ReadError, WriteError;
        internal bool CommitBeforeFailure;
        internal Func<CancellationToken, Task>? BeforeWrite;
        internal void Seed(string documentNamespace, byte[] bytes) => documents[documentNamespace] = bytes.ToArray();
        internal byte[]? Bytes(string documentNamespace) =>
            documents.TryGetValue(documentNamespace, out var bytes) ? bytes.ToArray() : null;

        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++; ReadNamespaces.Add(documentNamespace);
            if (ReadError is { } failure)
                throw new DocumentStoreException(failure);
            byte[]? bytes = Bytes(documentNamespace);
            return Task.FromResult(new DocumentSnapshot(bytes,
                bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
        }

        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++; WrittenNamespaces.Add(documentNamespace);
            if (BeforeWrite is { } before) await before(cancellationToken);
            byte[]? previous = Bytes(documentNamespace);
            var actual = previous is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(previous);
            if (actual != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            if (WriteError is { } beforeFailure && !CommitBeforeFailure) throw new DocumentStoreException(beforeFailure);
            documents[documentNamespace] = data.ToArray();
            if (WriteError is { } afterFailure) throw new DocumentStoreException(afterFailure);
            return DocumentFingerprint.FromBytes(data.Span);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
