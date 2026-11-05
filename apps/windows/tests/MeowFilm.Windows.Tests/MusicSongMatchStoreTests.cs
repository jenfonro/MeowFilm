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
        yield return new("Music song matches empty snapshot removes all observations without changing library", EmptySnapshot);
        yield return new("Music song matches reject secrets URLs unknown fields and whole candidate pages", RejectTransient);
        yield return new("Music song matches reject malformed observation records without replacing saved data", InvalidRecords);
        yield return new("Music song matches unknown damaged duplicate and oversized documents are never overwritten", IncompatibleDocuments);
        yield return new("Music song matches validate fingerprints and preserve protected format errors", StorageFormats);
        yield return new("Music song matches unavailable cache reads miss and writes remain best effort", Unavailable);
        yield return new("Music song matches CAS conflict rereads and preserves the winning document", Conflicts);
        yield return new("Music song matches ambiguous writes reread receipts without replaying snapshots", AmbiguousWrites);
        yield return new("Music song matches capacity evicts oldest whole entries and retains input order", Capacity);
        yield return new("Music song matches oversized individual entry is evicted whole", OversizedEntry);
        yield return new("Music song matches gate and cancellation settle an admitted write before reading", Cancellation);
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
        ["code"] = ""
    };

    private static byte[] Document(params JsonObject[] entries) => JsonSerializer.SerializeToUtf8Bytes(
        new JsonObject { ["version"] = 1, ["entries"] = LJ.Arr(entries) }, LJ.Json);
    private static string Key(JsonObject entry) => entry["key"]!.GetValue<string>();
    private static JsonObject Observation(JsonObject entry) => entry["platforms"]!["wy"]!.AsObject();

    private static async Task DataError(DocumentStoreError expected, Func<Task> action)
    {
        try { await action(); }
        catch (MusicPlaybackCacheDataException error)
        {
            Check(error.Error == expected, $"Expected {expected}, actual {error.Error}");
            return;
        }
        throw new InvalidOperationException("Expected incompatible cache data");
    }

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
        var cache = new MusicSongMatchStore(store);
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
        entry["excluded"] = new JsonArray(NativeMusicSong.SourceKey(
            MusicSongFixture.Online("tx", "rejected-recording", "Other recording", "Artist")));
        var expected = entry.DeepClone();
        await cache.WriteAsync([entry], default);
        Check(store.WriteCalls == 1 && store.WrittenNamespaces.SequenceEqual(new[] { "music.song-matches.v1" }));
        var root = JsonNode.Parse(store.Bytes(MusicSongMatchStore.Namespace)!)!.AsObject();
        Check(root.Select(field => field.Key).SequenceEqual(new[] { "version", "entries" }) &&
            root["version"]!.GetValue<int>() == 1 && root["entries"]!.AsArray().Count == 1);
        entry["song"]!["title"] = "Changed caller";
        var first = (await new MusicSongMatchStore(store).ReadAsync(default)).Single();
        Check(JsonNode.DeepEquals(first, expected) && Key(first).Length == 16);
        first["song"]!["sources"]!.AsArray().Clear();
        Check(JsonNode.DeepEquals((await cache.ReadAsync(default)).Single(), expected));
        Check(store.ReadNamespaces.All(value => value == MusicSongMatchStore.Namespace));
    }

    private static async Task EmptySnapshot()
    {
        await using var store = new Store();
        byte[] library = "unrelated-library-bytes"u8.ToArray();
        store.Seed(MusicLibraryOwner.DocumentNamespace, library);
        var cache = new MusicSongMatchStore(store);
        await cache.WriteAsync([], default);
        Check(store.WriteCalls == 0 && store.Bytes(MusicSongMatchStore.Namespace) is null);
        var unsearched = Entry("unsearched", MusicSongFixture.Local("library:unsearched"));
        unsearched["platforms"] = new JsonObject();
        await cache.WriteAsync([Entry("one"), unsearched], default);
        Check(JsonNode.DeepEquals((await cache.ReadAsync(default))[1], unsearched));
        await cache.WriteAsync([], default);
        Check((await cache.ReadAsync(default)).Count == 0 && store.WriteCalls == 2);
        Check(store.Bytes(MusicLibraryOwner.DocumentNamespace)!.SequenceEqual(library) &&
            store.ReadNamespaces.All(value => value == MusicSongMatchStore.Namespace));
        Check(JsonNode.Parse(store.Bytes(MusicSongMatchStore.Namespace)!)!["entries"]!.AsArray().Count == 0);
    }

    private static async Task RejectTransient()
    {
        Action<JsonObject>[] defects =
        [
            entry => entry["cookie"] = "synthetic-cookie",
            entry => entry["song"]!["token"] = "synthetic-token",
            entry => entry["song"]!["sources"]![0]!["credential"] = "synthetic-credential",
            entry => MusicSongFixture.Info(entry["song"]!)["headers"] = new JsonObject { ["Authorization"] = "synthetic" },
            entry => MusicSongFixture.Info(entry["song"]!)["url"] = "https://media.invalid/audio",
            entry => MusicSongFixture.Info(entry["song"]!)["password"] = "synthetic",
            entry => MusicSongFixture.Info(entry["song"]!)["_types"] =
                new JsonObject { ["flac"] = new JsonObject { ["secret"] = "synthetic" } },
            entry => Observation(entry)["query"] = "https://media.invalid/audio?token=synthetic",
            entry => Observation(entry)["code"] = "https://media.invalid/audio",
            entry => entry["results"] = new JsonArray(MusicSongFixture.Online("tx", "candidate")),
            entry => entry["excluded"] = new JsonArray("[\"tx\",\"blob:synthetic\",\"\"]"),
            entry =>
            {
                var source = entry["song"]!["sources"]![0]!.AsObject();
                var info = source["musicInfo"]!.AsObject();
                info["songmid"] = "https://media.invalid/native-id";
                source["sourceKey"] = NativeMusicSong.InfoKey(info);
            }
        ];
        foreach (var defect in defects)
        {
            await using var store = new Store();
            byte[] before = Document(Entry("durable"));
            store.Seed(MusicSongMatchStore.Namespace, before);
            var invalid = Entry("invalid"); defect(invalid);
            var cache = new MusicSongMatchStore(store);
            await DataError(DocumentStoreError.Corrupt, () => cache.WriteAsync([invalid], default));
            Check(store.WriteCalls == 0 && store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(before));
            store.Seed(MusicSongMatchStore.Namespace, Document(invalid));
            await DataError(DocumentStoreError.Corrupt, async () => await cache.ReadAsync(default));
        }
    }

    private static IEnumerable<JsonObject> InvalidEntries()
    {
        Action<JsonObject>[] defects =
        [
            entry => entry.Remove("key"),
            entry => entry["key"] = new string('a', 32),
            entry => entry["key"] = new string('A', 16),
            entry => entry["key"] = 123,
            entry => entry["song"]!["sources"]!.AsArray().Add(entry["song"]!["sources"]![0]!.DeepClone()),
            entry => entry["song"]!["unused"] = true,
            entry => entry["platforms"]!["local"] = Status("matched"),
            entry => entry["platforms"]!["wy"] = null,
            entry => Observation(entry).Remove("query"),
            entry => Observation(entry)["complete"] = true,
            entry => Observation(entry)["state"] = "not_found",
            entry => Observation(entry)["checkedAt"] = -1,
            entry => Observation(entry)["checkedAt"] = 0.5,
            entry => Observation(entry)["checkedAt"] = "1",
            entry => Observation(entry)["context"] = new string('a', 32),
            entry => Observation(entry)["context"] = new string('A', 16),
            entry => Observation(entry)["query"] = new string('q', 257),
            entry => Observation(entry)["code"] = new string('c', 81),
            entry => entry["excluded"] = new JsonArray(123),
            entry => entry["excluded"] = new JsonArray("not-a-source-key"),
            entry => entry["excluded"] = new JsonArray("[\"tx\",\"id\",\"unexpected\"]")
        ];
        foreach (var defect in defects) { var entry = Entry("invalid"); defect(entry); yield return entry; }
    }

    private static async Task InvalidRecords()
    {
        foreach (var invalid in InvalidEntries())
        {
            await using var store = new Store();
            byte[] before = Document(Entry("durable"));
            store.Seed(MusicSongMatchStore.Namespace, before);
            await DataError(DocumentStoreError.Corrupt, () => new MusicSongMatchStore(store).WriteAsync([invalid], default));
            Check(store.WriteCalls == 0 && store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(before));
            byte[] damaged = Document(invalid); store.Seed(MusicSongMatchStore.Namespace, damaged);
            await DataError(DocumentStoreError.Corrupt, async () => await new MusicSongMatchStore(store).ReadAsync(default));
            await DataError(DocumentStoreError.Corrupt, () => new MusicSongMatchStore(store).WriteAsync([], default));
            Check(store.WriteCalls == 0 && store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(damaged));
        }
        await using var duplicates = new Store();
        var repeated = Entry("duplicate");
        await DataError(DocumentStoreError.Corrupt,
            () => new MusicSongMatchStore(duplicates).WriteAsync([repeated, repeated], default));
        Check(duplicates.WriteCalls == 0);
    }

    private static async Task IncompatibleDocuments()
    {
        (byte[] Bytes, DocumentStoreError Error)[] documents =
        [
            ("{\"version\":2,\"entries\":[]}"u8.ToArray(), DocumentStoreError.UnsupportedVersion),
            ("{\"version\":\"1\",\"entries\":[]}"u8.ToArray(), DocumentStoreError.UnsupportedVersion),
            ("{\"version\":1,\"entries\":[],\"unknown\":true}"u8.ToArray(), DocumentStoreError.Corrupt),
            ("{\"version\":1,\"version\":1,\"entries\":[]}"u8.ToArray(), DocumentStoreError.Corrupt),
            ("{\"version\":1,\"entries\":null}"u8.ToArray(), DocumentStoreError.Corrupt),
            ("{\"version\":1,\"entries\":{}}"u8.ToArray(), DocumentStoreError.Corrupt),
            ("{\"version\":1,\"entries\":[null]}"u8.ToArray(), DocumentStoreError.Corrupt),
            ("[]"u8.ToArray(), DocumentStoreError.Corrupt),
            ("{"u8.ToArray(), DocumentStoreError.Corrupt),
            ([], DocumentStoreError.Corrupt),
            (Document(Entry("duplicate"), Entry("duplicate")), DocumentStoreError.Corrupt),
            (Encoding.UTF8.GetBytes("{\"version\":1,\"entries\":[]}" + new string(' ', MaximumBytes)), DocumentStoreError.Limit)
        ];
        foreach (var (bytes, expected) in documents)
        {
            await using var store = new Store();
            store.Seed(MusicSongMatchStore.Namespace, bytes);
            var cache = new MusicSongMatchStore(store);
            await DataError(expected, async () => await cache.ReadAsync(default));
            await DataError(expected, () => cache.WriteAsync([Entry("replacement")], default));
            await DataError(expected, () => cache.WriteAsync([], default));
            Check(store.WriteCalls == 0 && store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(bytes));
        }
        await using var duplicateFields = new Store();
        string valid = Encoding.UTF8.GetString(Document(Entry("nested")));
        byte[] damaged = Encoding.UTF8.GetBytes(valid.Replace("\"checkedAt\":0", "\"checkedAt\":0,\"checkedAt\":0", StringComparison.Ordinal));
        Check(!damaged.SequenceEqual(Encoding.UTF8.GetBytes(valid)));
        duplicateFields.Seed(MusicSongMatchStore.Namespace, damaged);
        await DataError(DocumentStoreError.Corrupt, () => new MusicSongMatchStore(duplicateFields).WriteAsync([], default));
        Check(duplicateFields.WriteCalls == 0 && duplicateFields.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(damaged));
    }

    private static async Task StorageFormats()
    {
        await using (var store = new Store())
        {
            store.ReadFingerprint = DocumentFingerprint.FromBytes("absent-data"u8);
            var cache = new MusicSongMatchStore(store);
            await DataError(DocumentStoreError.Corrupt, async () => await cache.ReadAsync(default));
            await DataError(DocumentStoreError.Corrupt, () => cache.WriteAsync([], default));
            Check(store.WriteCalls == 0);
            byte[] before = Document(Entry("stored")); store.Seed(MusicSongMatchStore.Namespace, before);
            await DataError(DocumentStoreError.Corrupt, () => cache.WriteAsync([Entry("new")], default));
            Check(store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(before));
        }
        foreach (var expected in new[] { DocumentStoreError.Corrupt, DocumentStoreError.UnsupportedVersion,
                     DocumentStoreError.Limit, DocumentStoreError.Protection })
        {
            await using var store = new Store { ReadError = expected };
            var cache = new MusicSongMatchStore(store);
            await DataError(expected, async () => await cache.ReadAsync(default));
            await DataError(expected, () => cache.WriteAsync([], default));
            Check(store.WriteCalls == 0);
        }
    }

    private static async Task Unavailable()
    {
        foreach (var failure in new[] { DocumentStoreError.Unavailable, DocumentStoreError.Storage,
                     DocumentStoreError.Closed, DocumentStoreError.Conflict })
        {
            await using var store = new Store { ReadError = failure };
            byte[] before = Document(Entry("old")); store.Seed(MusicSongMatchStore.Namespace, before);
            var cache = new MusicSongMatchStore(store);
            Check((await cache.ReadAsync(default)).Count == 0);
            await cache.WriteAsync([Entry("new")], default);
            Check(store.WriteCalls == 0 && store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(before));
            store.ReadError = null; store.WriteError = failure;
            await cache.WriteAsync([Entry("new")], default);
            Check(store.WriteCalls == 1 && store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(before));
        }
        await using var unavailableAfterAdmission = new Store
        {
            WriteError = DocumentStoreError.Storage, ReadErrorAfterWrite = DocumentStoreError.Unavailable
        };
        await new MusicSongMatchStore(unavailableAfterAdmission).WriteAsync([Entry("new")], default);
        Check(unavailableAfterAdmission.WriteCalls == 1 && unavailableAfterAdmission.ReadCalls == 2 &&
            unavailableAfterAdmission.Bytes(MusicSongMatchStore.Namespace) is null);
    }

    private static async Task Conflicts()
    {
        (byte[] Bytes, DocumentStoreError? Error)[] outcomes =
        [
            (Document(Entry("winner")), null),
            ("{\"version\":2,\"entries\":[]}"u8.ToArray(), DocumentStoreError.UnsupportedVersion),
            ("{"u8.ToArray(), DocumentStoreError.Corrupt)
        ];
        foreach (var (winner, error) in outcomes)
        {
            await using var store = new Store();
            store.Seed(MusicSongMatchStore.Namespace, Document(Entry("old")));
            store.CompetingWrite = winner;
            var cache = new MusicSongMatchStore(store);
            if (error is { } incompatible)
                await DataError(incompatible, () => cache.WriteAsync([Entry("loser")], default));
            else await cache.WriteAsync([Entry("loser")], default);
            Check(store.WriteCalls == 1 && store.ReadCalls == 2 &&
                store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(winner));
        }
    }

    private static async Task AmbiguousWrites()
    {
        foreach (bool wrongReceipt in new[] { false, true })
        foreach (bool committed in new[] { false, true })
        {
            await using var store = new Store
            {
                WriteError = wrongReceipt ? null : DocumentStoreError.Storage,
                CommitBeforeFailure = committed, WrongReceipt = wrongReceipt, SkipCommit = !committed
            };
            byte[] before = Document(Entry("old")); store.Seed(MusicSongMatchStore.Namespace, before);
            var incoming = Entry("new"); var cache = new MusicSongMatchStore(store);
            bool commitError = false;
            try { await cache.WriteAsync([incoming], default); }
            catch (MusicPlaybackCacheCommitException) { commitError = true; }
            Check(commitError == (wrongReceipt && !committed));
            Check(store.WriteCalls == 1 && store.ReadCalls == 2 &&
                store.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(committed ? Document(incoming) : before));
            store.WriteError = null; store.WrongReceipt = false;
            Check(Key((await cache.ReadAsync(default)).Single()) == Key(committed ? incoming : Entry("old")));
        }
        await using var replaced = new Store
        {
            WrongReceipt = true, ReplacementAfterCommit = Document(Entry("other-writer"))
        };
        bool rejected = false;
        try { await new MusicSongMatchStore(replaced).WriteAsync([Entry("candidate")], default); }
        catch (MusicPlaybackCacheCommitException) { rejected = true; }
        Check(rejected && replaced.WriteCalls == 1 && replaced.ReadCalls == 2 &&
            replaced.Bytes(MusicSongMatchStore.Namespace)!.SequenceEqual(Document(Entry("other-writer"))));
    }

    private static async Task Capacity()
    {
        var entries = Enumerable.Range(0, 22).Select(i => Entry("capacity-" + i,
            MusicSongFixture.Local("library:capacity:" + i, "Sized " + i, "Artist", new string('x', 256000)))).ToArray();
        Check(Document(entries.TakeLast(16).ToArray()).Length <= MaximumBytes &&
            Document(entries.TakeLast(17).ToArray()).Length > MaximumBytes);
        await using var store = new Store();
        await new MusicSongMatchStore(store).WriteAsync(entries, default);
        var restored = await new MusicSongMatchStore(store).ReadAsync(default);
        Check(store.Bytes(MusicSongMatchStore.Namespace)!.Length <= MaximumBytes &&
            restored.Select(Key).SequenceEqual(entries.TakeLast(16).Select(Key)));
        for (int i = 0; i < restored.Count; i++) Check(JsonNode.DeepEquals(restored[i], entries[6 + i]));
    }

    private static async Task OversizedEntry()
    {
        var huge = Entry("oversized", MusicSongFixture.Local("library:oversized", "Large", album: new string('x', MaximumBytes)));
        await using var store = new Store();
        var cache = new MusicSongMatchStore(store);
        await cache.WriteAsync([Entry("old"), huge, Entry("new")], default);
        Check(Key((await cache.ReadAsync(default)).Single()) == Key(Entry("new")) &&
            store.Bytes(MusicSongMatchStore.Namespace)!.Length <= MaximumBytes);
        await cache.WriteAsync([huge], default);
        Check((await cache.ReadAsync(default)).Count == 0);
    }

    private static async Task Cancellation()
    {
        await using var store = new Store();
        var cache = new MusicSongMatchStore(store);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Cancelled(async () => await cache.ReadAsync(cancelled.Token));
            await Cancelled(() => cache.WriteAsync([Entry("cancelled")], cancelled.Token));
            Check(store.ReadCalls == 0 && store.WriteCalls == 0);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool admittedWithoutCancellation = false;
        store.BeforeWrite = async token =>
        {
            admittedWithoutCancellation = !token.CanBeCanceled;
            entered.TrySetResult(); await release.Task;
        };
        using var writerCancellation = new CancellationTokenSource();
        var writing = cache.WriteAsync([Entry("admitted")], writerCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var readCancellation = new CancellationTokenSource();
        var cancelledRead = cache.ReadAsync(readCancellation.Token);
        var stableRead = cache.ReadAsync(default);
        bool blocked = !cancelledRead.IsCompleted && !stableRead.IsCompleted;
        readCancellation.Cancel(); writerCancellation.Cancel();
        release.TrySetResult();
        await writing;
        await Cancelled(async () => await cancelledRead);
        Check(blocked && admittedWithoutCancellation &&
            Key((await stableRead).Single()) == Key(Entry("admitted")) && store.WriteCalls == 1);
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
        catch (ProtectedDocumentException error) { surfaced = error.Error == DocumentStoreError.Unavailable; }
        Check(surfaced && unavailable.WriteCalls == 0);
    }

    private sealed class Store : IProtectedDocumentStore
    {
        private readonly Dictionary<string, byte[]> documents = new(StringComparer.Ordinal);
        internal readonly List<string> ReadNamespaces = [], WrittenNamespaces = [];
        internal int ReadCalls, WriteCalls;
        internal DocumentStoreError? ReadError, WriteError, ReadErrorAfterWrite;
        internal DocumentFingerprint? ReadFingerprint;
        internal bool CommitBeforeFailure, WrongReceipt, SkipCommit;
        internal byte[]? CompetingWrite, ReplacementAfterCommit;
        internal Func<CancellationToken, Task>? BeforeWrite;
        internal void Seed(string documentNamespace, byte[] bytes) => documents[documentNamespace] = bytes.ToArray();
        internal byte[]? Bytes(string documentNamespace) =>
            documents.TryGetValue(documentNamespace, out var bytes) ? bytes.ToArray() : null;

        public Task<ProtectedDocument> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++; ReadNamespaces.Add(documentNamespace);
            if ((WriteCalls > 0 ? ReadErrorAfterWrite ?? ReadError : ReadError) is { } failure)
                throw new ProtectedDocumentException(failure);
            byte[]? bytes = Bytes(documentNamespace);
            return Task.FromResult(new ProtectedDocument(bytes, ReadFingerprint ??
                (bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes))));
        }

        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++; WrittenNamespaces.Add(documentNamespace);
            if (BeforeWrite is { } before) await before(cancellationToken);
            if (CompetingWrite is { } competing) { Seed(documentNamespace, competing); CompetingWrite = null; }
            byte[]? previous = Bytes(documentNamespace);
            var actual = previous is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(previous);
            if (actual != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
            if (WriteError is { } beforeFailure && !CommitBeforeFailure) throw new ProtectedDocumentException(beforeFailure);
            if (!SkipCommit) documents[documentNamespace] = data.ToArray();
            if (ReplacementAfterCommit is { } replacement) Seed(documentNamespace, replacement);
            if (WriteError is { } afterFailure) throw new ProtectedDocumentException(afterFailure);
            return WrongReceipt ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data.Span);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
