using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicHomeSnapshotTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music home snapshot reopens through new local store and owner with ordered card content", Reopen);
        yield return new("Music home snapshot replaces all pages and platform in one document", ReplacePlatform);
        yield return new("Music home snapshot has no account buckets or runtime cursor authority", AccountsAndRuntime);
        yield return new("Music home snapshot retains successful empty pages but never an absent result", EmptyResult);
        yield return new("Music home snapshot has no page count cap below the document boundary", ManyPages);
        yield return new("Music home snapshot honors the exact shared eight MiB document boundary", SizeBoundary);
        yield return new("Music home snapshot rejects invalid caller pages without replacing content", InvalidPages);
        yield return new("Music home snapshot rejects malformed and incompatible stored documents without overwriting", InvalidDocuments);
        yield return new("Music home snapshot rejects nested credentials and execution authority retained by the DTO", SensitiveContent);
        yield return new("Music home snapshot validates absence and content fingerprints", Fingerprints);
        yield return new("Music home snapshot propagates read and write failures without a fallback or retry", StorageFailures);
        yield return new("Music home snapshot rejects unknown receipts without replay or false rollback", Receipts);
        yield return new("Music home snapshot cancellation before write admission keeps the old document", CancelBeforeAdmission);
        yield return new("Music home snapshot serializes saves and reads until admitted writes settle", SerialWrites);
        yield return new("Music home snapshot queued saves each CAS against the completed predecessor", QueuedSaves);
        yield return new("Music home snapshot cross owner CAS conflict preserves the competing winner", CompetingOwners);
    }

    private static async Task Reopen()
    {
        string root = TestWorkspace.DataPath("music-home-snapshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Page("wy", "first");
            first["entries"] = new JsonArray(new JsonObject
            {
                ["id"] = "daily-entry", ["key"] = "daily-key", ["name"] = "每日推荐",
                ["presentation"] = "preview", ["dayOfMonth"] = 7, ["decoration"] = "calendar",
                ["requiresAccount"] = true, ["display"] = true, ["available"] = true,
                ["action"] = new JsonObject { ["type"] = "daily", ["branch"] = "style" },
                ["preview"] = new JsonArray(new JsonObject
                {
                    ["title"] = "Preview", ["artist"] = "Artist", ["cover"] = "https://image.invalid/preview.jpg",
                    ["rank"] = 2, ["lastRank"] = 3
                })
            });
            var section = first["sections"]![0]!;
            section["action"] = new JsonObject
            {
                ["type"] = "channel",
                ["channel"] = new JsonObject { ["id"] = "feature", ["pageCode"] = "HOME_DISCOVERY_PAGE" }
            };
            section["items"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "song-card", ["key"] = "song-key",
                ["song"] = MusicSongFixture.Online("wy", "101", "Song", "Artist", cover: "https://image.invalid/song.jpg"),
                ["playbackSource"] = new JsonObject { ["type"] = "playlist", ["id"] = "202" }
            });
            first["items"] = new JsonArray(MusicSongFixture.Online("wy", "303", "Root song"));
            var second = Page("wy", "second");
            first["expiresAt"] = 1; // Past service lifetime is not a display-cache TTL.
            await using (var store = new LocalDocumentStore(root, AppIdentity.NativeNamespaces,
                protectedNamespaces: AppIdentity.ProtectedNamespaces))
            {
                var owner = new MusicHomeSnapshotStore(store);
                Check(await owner.ReadAsync() is null, "missing document must be a miss");
                await owner.SaveAsync(Snapshot("wy", first, second));
            }
            await using (var store = new LocalDocumentStore(root, AppIdentity.NativeNamespaces,
                protectedNamespaces: AppIdentity.ProtectedNamespaces))
            {
                var restored = await new MusicHomeSnapshotStore(store).ReadAsync();
                Check(restored is { Platform: "wy", Pages.Length: 2 }, "new owners must restore all pages in order");
                Check(PageId(restored!.Pages[0]) == "first" && PageId(restored.Pages[1]) == "second");
                foreach (string field in new[] { "entries", "sections", "items" })
                    Check(JsonNode.DeepEquals(first[field], JsonNode.Parse(restored.Pages[0].GetProperty(field).GetRawText())),
                        "display and action content must roundtrip intact: " + field);
                var sections = MusicCatalogDto.Read(restored.Pages[0], "wy", "home", "");
                var daily = sections[0].Items.Single();
                Check(daily.CanOpen && daily.RequiresAccount && daily.ActionId == "daily" &&
                    daily.Action!.Value.GetProperty("branch").GetString() == "style" &&
                    daily.DayOfMonth == 7 && daily.PreviewSongs.Single().Movement == "↑");
                Check(sections[1].Action is { ActionId: "feature", PageCode: "HOME_DISCOVERY_PAGE" });
                var song = sections[1].Items.Single(card => card.Song is not null);
                Check(song.ActionId == "101" && song.Song!.Title == "Song" &&
                    song.PlaybackSource!.Value.GetProperty("id").GetString() == "202");
                Check(sections[2].Items.Single().ActionId == "303");
                var saved = await store.ReadAsync(MusicHomeSnapshotStore.Namespace);
                using var json = JsonDocument.Parse(saved.Data!);
                Check(json.RootElement.EnumerateObject().Select(property => property.Name)
                    .SequenceEqual(new[] { "schema", "platform", "pages" }));
                Check(json.RootElement.GetProperty("schema").GetInt32() == 1);
                restored.Pages[0] = JsonSerializer.SerializeToElement(new { platform = "kg" });
                Check(PageId((await new MusicHomeSnapshotStore(store).ReadAsync())!.Pages[0]) == "first",
                    "returned pages must not be a mutable owner cache");
            }
        }
        finally { Directory.Delete(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root), recursive: true); }
    }

    private static async Task ReplacePlatform()
    {
        await using var store = new Store();
        await new MusicHomeSnapshotStore(store).SaveAsync(Snapshot("wy", Page("wy", "old-1"), Page("wy", "old-2")));
        byte[] original = (await store.Bytes())!;
        await new MusicHomeSnapshotStore(store).SaveAsync(Snapshot("kg", Page("kg", "new")));
        var restored = await new MusicHomeSnapshotStore(store).ReadAsync();
        Check(restored is { Platform: "kg", Pages.Length: 1 } && PageId(restored.Pages[0]) == "new",
            "replacement must not merge pages or return a different platform bucket");
        Check(store.Writes.Count == 2 && store.Writes[0].Expected == DocumentFingerprint.Missing &&
            store.Writes[1].Expected == DocumentFingerprint.FromBytes(original));
        OnlyHomeNamespace(store);
    }

    private static async Task AccountsAndRuntime()
    {
        await using var store = new Store();
        foreach (var (account, content) in new[]
        {
            ("synthetic-account-A", "first-content"), ("synthetic-account-B", "second-content")
        })
        {
            var page = Page("wy", content);
            page["scope"] = account;
            page["accountStamp"] = account;
            page["credentials"] = new JsonObject { ["cookie"] = "synthetic" };
            page["cursor"] = "synthetic-cursor";
            page["more"] = true;
            page["continuation"] = new JsonObject
            {
                ["scope"] = account, ["cursor"] = "synthetic-cursor", ["target"] = "wy:home"
            };
            page["snapshot"] = "synthetic-execution-snapshot";
            page["expiresAt"] = 1;
            await new MusicHomeSnapshotStore(store).SaveAsync(Snapshot("wy", page));
            var restored = (await new MusicHomeSnapshotStore(store).ReadAsync())!;
            Check(restored.Pages.Length == 1 && PageId(restored.Pages[0]) == content,
                "another account must replace the same complete document");
            Check(restored.Pages[0].EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { "entries", "platform", "sections" }));
            string text = Encoding.UTF8.GetString((await store.Bytes())!);
            Check(!text.Contains(account, StringComparison.Ordinal), "account identity must not enter display persistence");
            foreach (string field in new[] { "ok", "scope", "accountStamp", "credentials", "cookie", "cursor",
                "more", "continuation", "snapshot", "expiresAt" })
                Check(!text.Contains("\"" + field + "\"", StringComparison.Ordinal), "runtime field retained: " + field);
        }
        Check(store.Writes.Count == 2);
        OnlyHomeNamespace(store);
    }

    private static async Task EmptyResult()
    {
        await using var store = new Store();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
        byte[] old = (await store.Bytes())!;
        await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(new("wy", [])));
        await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(Snapshot("wy",
            new JsonObject { ["ok"] = false, ["platform"] = "wy", ["entries"] = new JsonArray() })));
        Check(store.Writes.Count == 1 && (await store.Bytes())!.SequenceEqual(old));
        await owner.SaveAsync(Snapshot("wy", new JsonObject
        {
            ["ok"] = true, ["platform"] = "wy", ["entries"] = new JsonArray(), ["sections"] = new JsonArray()
        }));
        var restored = (await owner.ReadAsync())!;
        Check(restored.Pages.Length == 1 && MusicCatalogDto.Read(restored.Pages[0], "wy", "home", "").Count == 0,
            "an actual successful empty home is still a display snapshot");
    }

    private static async Task ManyPages()
    {
        await using var store = new Store();
        var pages = Enumerable.Range(0, 1025).Select(index => Page("wy", "page-" + index)).ToArray();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", pages));
        var restored = (await new MusicHomeSnapshotStore(store).ReadAsync())!;
        Check(restored.Pages.Length == pages.Length && restored.Pages.Select(PageId)
            .SequenceEqual(Enumerable.Range(0, pages.Length).Select(index => "page-" + index)),
            "the shared byte boundary must not become a page-count limit");
    }

    private static async Task SizeBoundary()
    {
        await using var store = new Store();
        var page = Page("wy", "boundary");
        page.Remove("ok");
        page["sections"]![0]!["items"]![0]!["subtitle"] = "";
        int remaining = LocalDocumentStore.MaximumDocumentBytes - Document("wy", page).Length;
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('x', remaining);
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", page));
        byte[] old = (await store.Bytes())!;
        Check(old.Length == LocalDocumentStore.MaximumDocumentBytes);
        Check((await owner.ReadAsync())!.Pages[0].GetProperty("sections")[0].GetProperty("items")[0]
            .GetProperty("subtitle").GetString()!.Length == remaining, "the exact document boundary must be readable");
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('x', remaining + 1);
        await Error(DocumentStoreError.Limit, () => owner.SaveAsync(Snapshot("wy", page)));
        Check(store.Writes.Count == 1 && (await store.Bytes())!.SequenceEqual(old),
            "oversize save must not evict or truncate successful content");
        await store.Seed(Document("wy", page));
        int writes = store.Writes.Count;
        await Error(DocumentStoreError.Limit, async () => _ = await owner.ReadAsync());
        await Error(DocumentStoreError.Limit, () => owner.SaveAsync(Snapshot("wy", Page("wy", "small"))));
        Check(store.Writes.Count == writes && (await store.Bytes())!.Length == LocalDocumentStore.MaximumDocumentBytes + 1);
    }

    private static async Task InvalidPages()
    {
        await using var store = new Store();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
        byte[] old = (await store.Bytes())!;
        var badSong = Page("wy", "foreign-song");
        badSong["items"] = new JsonArray(MusicSongFixture.Online("kg", "101"));
        var badPresentation = Page("wy", "bad-entry");
        badPresentation["entries"] = new JsonArray(new JsonObject { ["id"] = "entry", ["presentation"] = "unknown" });
        var badBoolean = Page("wy", "bad-boolean");
        badBoolean["sections"]![0]!["items"]![0]!["requiresAccount"] = "true";
        var badSection = Page("wy", "bad-section");
        badSection["sections"]![0]!["items"] = null;
        foreach (var invalid in new[]
        {
            Snapshot("tx", Page("tx", "unsupported")),
            Snapshot("wy", Page("kg", "mismatch")),
            Snapshot("wy", Page("wy", "first"), Page("kg", "mixed")),
            Snapshot("wy", new JsonObject { ["platform"] = "wy", ["ok"] = true }),
            Snapshot("wy", new JsonObject { ["entries"] = new JsonArray() }),
            Snapshot("wy", badSong), Snapshot("wy", badPresentation), Snapshot("wy", badBoolean), Snapshot("wy", badSection),
            new MusicHomeSnapshot("wy", [JsonSerializer.SerializeToElement(new[] { "invalid" })])
        })
            await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(invalid));
        Check(store.Writes.Count == 1 && (await store.Bytes())!.SequenceEqual(old));
    }

    private static async Task InvalidDocuments()
    {
        foreach (var invalid in new (string Json, DocumentStoreError Error)[]
        {
            ("", DocumentStoreError.Corrupt),
            ("{", DocumentStoreError.Corrupt),
            ("null", DocumentStoreError.Corrupt),
            ("""{"schema":2,"platform":"wy","pages":[]}""", DocumentStoreError.UnsupportedVersion),
            ("""{"schema":1.0,"platform":"wy","pages":[]}""", DocumentStoreError.UnsupportedVersion),
            ("""{"schema":"1","platform":"wy","pages":[]}""", DocumentStoreError.UnsupportedVersion),
            ("""{"schema":1,"schema":1,"platform":"wy","pages":[]}""", DocumentStoreError.Corrupt),
            ("""{"platform":"wy","pages":[]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"tx","pages":[{"platform":"tx","entries":[]}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":{}}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"kg","entries":[]}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy","items":{}}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy"}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy","entries":[],"scope":"synthetic"}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy","entries":[],"ok":true}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy","entries":[],"entries":[]}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy","sections":[{"id":"x","items":[{"id":"a","id":"b"}]}]}]}""", DocumentStoreError.Corrupt),
            ("""{"schema":1,"platform":"wy","pages":[{"platform":"wy","entries":[]}],"credentials":{}}""", DocumentStoreError.Corrupt)
        })
        {
            await using var store = new Store();
            byte[] bytes = Encoding.UTF8.GetBytes(invalid.Json);
            await store.Seed(bytes);
            var owner = new MusicHomeSnapshotStore(store);
            await Error(invalid.Error, async () => _ = await owner.ReadAsync());
            await Error(invalid.Error, () => owner.SaveAsync(Snapshot("kg", Page("kg", "new"))));
            Check(store.Writes.Count == 0 && (await store.Bytes())!.SequenceEqual(bytes),
                "format failures must neither become misses nor overwrite stored data");
            OnlyHomeNamespace(store);
        }
    }

    private static async Task SensitiveContent()
    {
        foreach (string field in new[] { "credentials", "Cookie", "accessToken", "PASSWORD", "secret", "Authorization",
            "api_key", "headers", "scope", "cursor", "continuation", "snapshot", "expiresAt", "accountStamp" })
        {
            await using var store = new Store();
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
            byte[] old = (await store.Bytes())!;
            var page = Page("wy", "unsafe");
            page["sections"]![0]!["items"]![0]!["action"]![field] = "synthetic";
            // Action JSON is intentionally open in the display DTO. Persistence
            // must enforce its own boundary without dropping the valid action ID.
            Check(MusicCatalogDto.Read(JsonSerializer.SerializeToElement(page), "wy", "home", "")
                .Single().Items.Single().CanOpen);
            await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(Snapshot("wy", page)));
            Check(store.Writes.Count == 1 && (await store.Bytes())!.SequenceEqual(old));
            page.Remove("ok");
            await store.Seed(Document("wy", page));
            await Error(DocumentStoreError.Corrupt, async () => _ = await owner.ReadAsync());
            await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(Snapshot("wy", Page("wy", "new"))));
            Check(store.Writes.Count == 1, "untrusted durable fields must not be silently cleaned and overwritten");
        }
    }

    private static async Task Fingerprints()
    {
        await using var store = new Store();
        var owner = new MusicHomeSnapshotStore(store);
        Check(await owner.ReadAsync() is null && store.Writes.Count == 0);
        store.ReadFingerprint = DocumentFingerprint.FromBytes("synthetic"u8);
        await Error(DocumentStoreError.Corrupt, async () => _ = await owner.ReadAsync());
        await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(Snapshot("wy", Page("wy", "new"))));
        var page = Page("wy", "old"); page.Remove("ok");
        await store.Seed(Document("wy", page));
        foreach (var fingerprint in new[] { DocumentFingerprint.Missing, DocumentFingerprint.FromBytes("other"u8) })
        {
            store.ReadFingerprint = fingerprint;
            await Error(DocumentStoreError.Corrupt, async () => _ = await owner.ReadAsync());
            await Error(DocumentStoreError.Corrupt, () => owner.SaveAsync(Snapshot("wy", Page("wy", "new"))));
        }
        Check(store.Writes.Count == 0);
    }

    private static async Task StorageFailures()
    {
        foreach (var failure in new[] { DocumentStoreError.Unavailable, DocumentStoreError.UnsafePath,
            DocumentStoreError.Protection, DocumentStoreError.Storage, DocumentStoreError.Closed,
            DocumentStoreError.Conflict, DocumentStoreError.Corrupt, DocumentStoreError.UnsupportedVersion })
        {
            await using var store = new Store();
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
            byte[] old = (await store.Bytes())!;
            int reads = store.Reads.Count;
            store.ReadError = failure;
            await Error(failure, async () => _ = await owner.ReadAsync());
            await Error(failure, () => owner.SaveAsync(Snapshot("kg", Page("kg", "new"))));
            Check(store.Reads.Count == reads + 2 && store.Writes.Count == 1);
            store.ReadError = null;
            store.WriteError = failure;
            await Error(failure, () => owner.SaveAsync(Snapshot("kg", Page("kg", "new"))));
            Check(store.Writes.Count == 2 && store.Reads.Count == reads + 3 &&
                (await store.Bytes())!.SequenceEqual(old), "failed write must preserve the old document without retry");
            Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "old");
            OnlyHomeNamespace(store);
        }
    }

    private static async Task Receipts()
    {
        foreach (bool commit in new[] { false, true })
        {
            await using var store = new Store();
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
            store.SkipCommit = !commit;
            store.WrongReceipt = true;
            int reads = store.Reads.Count;
            await Error(DocumentStoreError.Storage, () => owner.SaveAsync(Snapshot("kg", Page("kg", "new"))));
            Check(store.Writes.Count == 2 && store.Reads.Count == reads + 1,
                "a mismatched receipt must fail without rereading another version or retrying");
            var actual = (await new MusicHomeSnapshotStore(store).ReadAsync())!;
            Check(actual.Platform == (commit ? "kg" : "wy") && PageId(actual.Pages.Single()) == (commit ? "new" : "old"),
                "a failed receipt does not imply rollback of an already committed write");
        }
        await using var lostReceipt = new Store();
        var lostOwner = new MusicHomeSnapshotStore(lostReceipt);
        await lostOwner.SaveAsync(Snapshot("wy", Page("wy", "old")));
        lostReceipt.WriteError = DocumentStoreError.Storage;
        lostReceipt.CommitBeforeFailure = true;
        int before = lostReceipt.Reads.Count;
        await Error(DocumentStoreError.Storage, () => lostOwner.SaveAsync(Snapshot("kg", Page("kg", "committed"))));
        Check(lostReceipt.Writes.Count == 2 && lostReceipt.Reads.Count == before + 1);
        Check(PageId((await new MusicHomeSnapshotStore(lostReceipt).ReadAsync())!.Pages.Single()) == "committed");
    }

    private static async Task CancelBeforeAdmission()
    {
        await using var store = new Store();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
        byte[] old = (await store.Bytes())!;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            int reads = store.Reads.Count;
            await Cancelled(() => owner.SaveAsync(Snapshot("kg", Page("kg", "new")), cancelled.Token));
            await Cancelled(async () => _ = await owner.ReadAsync(cancelled.Token));
            Check(store.Reads.Count == reads && store.Writes.Count == 1);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var hold = store.NextRead = new Hold();
            var saving = owner.SaveAsync(Snapshot("kg", Page("kg", "new")), cancellation.Token);
            try
            {
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Cancelled(() => saving);
            }
            finally { hold.Release.TrySetResult(); }
        }
        Check(store.Writes.Count == 1 && (await store.Bytes())!.SequenceEqual(old));
        Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "old", "cancelled save must release the owner gate");
    }

    private static async Task SerialWrites()
    {
        await using var store = new Store();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "old")));
        byte[] old = (await store.Bytes())!;
        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var hold = store.NextWrite = new Hold();
        var first = owner.SaveAsync(Snapshot("wy", Page("wy", "first")), firstCancellation.Token);
        Task<MusicHomeSnapshot?>? reading = null;
        try
        {
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int reads = store.Reads.Count;
            var queued = owner.SaveAsync(Snapshot("kg", Page("kg", "cancelled")), queuedCancellation.Token);
            reading = owner.ReadAsync();
            Check(store.Reads.Count == reads && !first.IsCompleted && !queued.IsCompleted && !reading.IsCompleted,
                "queued operations must not read a fingerprint before the admitted write settles");
            queuedCancellation.Cancel();
            await Cancelled(() => queued);
            firstCancellation.Cancel();
            Check(!first.IsCompleted && !reading.IsCompleted && (await store.Bytes())!.SequenceEqual(old),
                "caller cancellation must not fabricate a receipt for an admitted write");
        }
        finally { hold.Release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Check(reading is not null && PageId((await reading.WaitAsync(TimeSpan.FromSeconds(5)))!.Pages.Single()) == "first");
        byte[] firstBytes = (await store.Bytes())!;
        await owner.SaveAsync(Snapshot("kg", Page("kg", "last")));
        Check(store.Writes.Count == 3 && store.Writes[2].Expected == DocumentFingerprint.FromBytes(firstBytes),
            "the next save must use the completed write's actual fingerprint");
        Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "last");
    }

    private static async Task QueuedSaves()
    {
        await using var store = new Store();
        var owner = new MusicHomeSnapshotStore(store);
        var firstPage = Page("wy", "first");
        firstPage.Remove("ok");
        var hold = store.NextWrite = new Hold();
        var first = owner.SaveAsync(Snapshot("wy", firstPage));
        Task? second = null;
        try
        {
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = owner.SaveAsync(Snapshot("kg", Page("kg", "second")));
            Check(store.Reads.Count == 1 && store.Writes.Count == 1 && !second.IsCompleted,
                "queued saves must not race the predecessor's fingerprint");
        }
        finally { hold.Release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Check(second is not null);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Check(store.Writes.Count == 2 && store.Writes[0].Expected == DocumentFingerprint.Missing &&
            store.Writes[1].Expected == DocumentFingerprint.FromBytes(Document("wy", firstPage)));
        var restored = (await owner.ReadAsync())!;
        Check(restored is { Platform: "kg", Pages.Length: 1 } && PageId(restored.Pages[0]) == "second");
    }

    private static async Task CompetingOwners()
    {
        await using var store = new Store();
        var first = new MusicHomeSnapshotStore(store);
        await first.SaveAsync(Snapshot("wy", Page("wy", "old")));
        byte[] old = (await store.Bytes())!;
        var hold = store.NextWrite = new Hold();
        var losing = first.SaveAsync(Snapshot("wy", Page("wy", "loser")));
        try
        {
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await new MusicHomeSnapshotStore(store).SaveAsync(Snapshot("kg", Page("kg", "winner")));
        }
        finally { hold.Release.TrySetResult(); }
        await Error(DocumentStoreError.Conflict, () => losing.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(store.Writes.Count == 3 && store.Reads.Count == 3 &&
            store.Writes[1].Expected == DocumentFingerprint.FromBytes(old) &&
            store.Writes[2].Expected == DocumentFingerprint.FromBytes(old), "a stale owner must issue one CAS without replay");
        var restored = (await first.ReadAsync())!;
        Check(restored is { Platform: "kg", Pages.Length: 1 } && PageId(restored.Pages[0]) == "winner");
    }

    private static JsonObject Page(string platform, string id) => new()
    {
        ["ok"] = true, ["platform"] = platform, ["entries"] = new JsonArray(),
        ["sections"] = new JsonArray(new JsonObject
        {
            ["id"] = id, ["title"] = "Home " + id, ["type"] = "playlists", ["layout"] = "rail",
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = id, ["name"] = "Playlist " + id, ["cover"] = "https://image.invalid/playlist.jpg",
                ["action"] = new JsonObject { ["type"] = "playlist", ["id"] = id }
            })
        })
    };

    private static MusicHomeSnapshot Snapshot(string platform, params JsonObject[] pages) =>
        new(platform, pages.Select(page => JsonSerializer.SerializeToElement(page)).ToArray());
    private static byte[] Document(string platform, params JsonObject[] pages) =>
        JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, platform, pages });
    private static string PageId(JsonElement page) => page.GetProperty("sections")[0].GetProperty("id").GetString()!;
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,
        string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void OnlyHomeNamespace(Store store) =>
        Check(store.Reads.Concat(store.Writes.Select(write => write.Namespace)).All(value => value == "music.home.v1"),
            "all owners must access exactly one native namespace");
    private static async Task Error(DocumentStoreError expected, Func<Task> action)
    {
        try { await action(); }
        catch (DocumentStoreException error)
        {
            Check(error.Error == expected && error.Message == expected.ToString() && error.InnerException is null,
                "expected fixed document error " + expected);
            return;
        }
        throw new InvalidOperationException("Expected " + expected);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }

    private sealed class Hold
    {
        internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The existing in-memory store owns the real CAS comparison. This wrapper
    // only controls admission/receipt timing and injects explicit storage faults.
    private sealed class Store : IDocumentStore
    {
        private readonly MusicClientContextFixture.MemoryStore inner = new();
        internal readonly List<string> Reads = [];
        internal readonly List<(string Namespace, DocumentFingerprint Expected)> Writes = [];
        internal DocumentStoreError? ReadError, WriteError;
        internal DocumentFingerprint? ReadFingerprint;
        internal Hold? NextRead, NextWrite;
        internal bool WrongReceipt, SkipCommit, CommitBeforeFailure;

        internal async Task Seed(byte[] bytes)
        {
            var before = await inner.ReadAsync(MusicHomeSnapshotStore.Namespace);
            await inner.WriteAsync(MusicHomeSnapshotStore.Namespace, bytes, before.Fingerprint);
        }
        internal async Task<byte[]?> Bytes() => (await inner.ReadAsync(MusicHomeSnapshotStore.Namespace)).Data;

        public async Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(documentNamespace);
            if (ReadError is { } error) throw new DocumentStoreException(error);
            var saved = await inner.ReadAsync(documentNamespace, cancellationToken);
            if (NextRead is { } hold)
            {
                NextRead = null;
                hold.Arrived.TrySetResult();
                await hold.Release.Task.WaitAsync(cancellationToken);
            }
            return saved with { Fingerprint = ReadFingerprint ?? saved.Fingerprint };
        }

        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] owned = data.ToArray();
            Writes.Add((documentNamespace, expected));
            if (NextWrite is { } hold)
            {
                NextWrite = null;
                hold.Arrived.TrySetResult();
                await hold.Release.Task; // Accepted writes settle independently of caller cancellation.
            }
            if (WriteError is { } before && !CommitBeforeFailure) throw new DocumentStoreException(before);
            var receipt = SkipCommit ? DocumentFingerprint.Missing :
                await inner.WriteAsync(documentNamespace, owned, expected);
            if (WriteError is { } after) throw new DocumentStoreException(after);
            return WrongReceipt ? DocumentFingerprint.Missing : receipt;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
