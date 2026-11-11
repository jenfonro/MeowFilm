using System.IO;
using System.Text;
using MeowFilm.AppHost;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicHomeSnapshotTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music home optional cache reopens ordered pages without account or runtime authority", Roundtrip);
        yield return new("Music home cache reopens through its independent local cache namespace and root", LocalReopen);
        yield return new("Music home bad cached content misses and fresh pages repair it", Damaged);
        yield return new("Music home foreign platform pages and songs miss without replacing accepted snapshots", PlatformMismatch);
        yield return new("Music home display cache keeps empty results and has no arbitrary page cap", EmptyAndMany);
        yield return new("Music home cache write does not wait for initial read or persistent receipt", SlowDisk);
        yield return new("Music home cache storage failures cannot negate successful pages", StorageFailures);
        yield return new("Music home cache refuses nested credentials without replacing safe content", Sensitive);
        yield return new("Music home cancelled saves and reads keep the accepted snapshot", Cancellation);
        yield return new("Music home cancelled cold reads preserve other readers and owner drain", CancelledRead);
        yield return new("Music home cache enforces the document byte boundary without replacing accepted content", SizeBoundary);
    }
    private static async Task Roundtrip()
    {
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "first"), Page("wy", "second")));
        await store.DrainAsync();
        await using var reopened = new CacheDocumentStore(backing);
        var restored = (await new MusicHomeSnapshotStore(reopened).ReadAsync())!;
        Check(restored.Platform == "wy" && restored.Pages.Select(PageId).SequenceEqual(new[] { "first", "second" }));
        restored.Pages[0] = default;
        Check(PageId((await owner.ReadAsync())!.Pages[0]) == "first");
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
            await owner.SaveAsync(Snapshot("wy", page));
            await store.DrainAsync();
            await using var accountReopened = new CacheDocumentStore(backing);
            var accountSnapshot = (await new MusicHomeSnapshotStore(accountReopened).ReadAsync())!;
            Check(accountSnapshot.Platform == "wy" && accountSnapshot.Pages.Length == 1 &&
                PageId(accountSnapshot.Pages[0]) == content);
            Check(accountSnapshot.Pages[0].EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { "entries", "platform", "sections" }));
            string text = Encoding.UTF8.GetString(backing.Data[MusicHomeSnapshotStore.Namespace]);
            Check(!text.Contains(account, StringComparison.Ordinal));
            foreach (string field in new[] { "ok", "scope", "accountStamp", "credentials", "cookie", "cursor",
                "more", "continuation", "snapshot", "expiresAt" })
                Check(!text.Contains("\"" + field + "\"", StringComparison.Ordinal));
            Check(backing.Data.Keys.SequenceEqual(new[] { MusicHomeSnapshotStore.Namespace }));
        }
        await owner.SaveAsync(Snapshot("kg", Page("kg", "replacement")));
        var replacement = (await owner.ReadAsync())!;
        Check(replacement is { Platform: "kg", Pages.Length: 1 } && PageId(replacement.Pages[0]) == "replacement");
    }
    private static async Task LocalReopen()
    {
        string root = TestWorkspace.DataPath("music-home-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Page("wy", "disk-first");
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
            first["expiresAt"] = 1; // Past service lifetime is not a display-cache TTL.
            await using (var backing = new LocalDocumentStore(root, AppIdentity.CacheNamespaces,
                protectedNamespaces: AppIdentity.CacheProtectedNamespaces))
            await using (var store = new CacheDocumentStore(backing))
            {
                var owner = new MusicHomeSnapshotStore(store);
                Check(await owner.ReadAsync() is null);
                await owner.SaveAsync(Snapshot("wy", first, Page("wy", "disk-second")));
                await store.DrainAsync();
            }
            await using (var backing = new LocalDocumentStore(root, AppIdentity.CacheNamespaces,
                protectedNamespaces: AppIdentity.CacheProtectedNamespaces))
            await using (var store = new CacheDocumentStore(backing))
            {
                var saved = (await new MusicHomeSnapshotStore(store).ReadAsync())!;
                Check(saved.Platform == "wy" && saved.Pages.Select(PageId).SequenceEqual(new[] { "disk-first", "disk-second" }));
                foreach (string field in new[] { "entries", "sections", "items" })
                    Check(JsonNode.DeepEquals(first[field], JsonNode.Parse(saved.Pages[0].GetProperty(field).GetRawText())));
                var sections = MusicCatalogDto.Read(saved.Pages[0], "wy", "home", "");
                var daily = sections[0].Items.Single();
                Check(daily.CanOpen && daily.RequiresAccount && daily.ActionId == "daily" &&
                    daily.Action!.Value.GetProperty("branch").GetString() == "style" &&
                    daily.DayOfMonth == 7 && daily.PreviewSongs.Single().Movement == "↑");
                Check(sections[1].Action is { ActionId: "feature", PageCode: "HOME_DISCOVERY_PAGE" });
                var song = sections[1].Items.Single(card => card.Song is not null);
                Check(song.ActionId == "101" && song.Song!.Title == "Song" &&
                    song.PlaybackSource!.Value.GetProperty("id").GetString() == "202");
                Check(sections[2].Items.Single().ActionId == "303");
                var document = await store.ReadAsync(MusicHomeSnapshotStore.Namespace);
                using var json = JsonDocument.Parse(document.Data!);
                Check(json.RootElement.EnumerateObject().Select(property => property.Name)
                    .SequenceEqual(new[] { "platform", "pages" }));
                saved.Pages[0] = default;
                Check(PageId((await new MusicHomeSnapshotStore(store).ReadAsync())!.Pages[0]) == "disk-first");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root), true);
        }
    }
    private static async Task Damaged()
    {
        foreach (byte[] data in new[] { "{"u8.ToArray(), "[]"u8.ToArray(), "{\"pages\":null}"u8.ToArray(),
            "{\"platform\":\"wy\",\"pages\":[{}]}"u8.ToArray() })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            backing.Data[MusicHomeSnapshotStore.Namespace] = data;
            await using var store = new CacheDocumentStore(backing);
            var owner = new MusicHomeSnapshotStore(store);
            Check(await owner.ReadAsync() is null);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "repaired")));
            Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "repaired");
        }
    }
    private static async Task EmptyAndMany()
    {
        await using var store = new CacheDocumentStore();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", new JsonObject { ["platform"] = "wy", ["entries"] = new JsonArray() }));
        Check(MusicCatalogDto.Read((await owner.ReadAsync())!.Pages.Single(), "wy", "home", "").Count == 0);
        var pages = Enumerable.Range(0, 1025).Select(i => Page("wy", i.ToString())).ToArray();
        await owner.SaveAsync(Snapshot("wy", pages));
        Check((await owner.ReadAsync())!.Pages.Select(PageId).SequenceEqual(Enumerable.Range(0, 1025).Select(i => i.ToString())));
        await owner.SaveAsync(new("wy", []));
        Check((await owner.ReadAsync())!.Pages.Length == 1025);
    }
    private static async Task PlatformMismatch()
    {
        var foreignSong = Page("wy", "foreign-song");
        foreignSong["items"] = new JsonArray(MusicSongFixture.Online("kg", "101"));
        foreach (var invalid in new[]
        {
            Snapshot("wy", Page("kg", "mismatch")),
            Snapshot("wy", Page("wy", "first"), Page("kg", "mixed")),
            Snapshot("wy", foreignSong)
        })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            await using var store = new CacheDocumentStore(backing);
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
            await store.DrainAsync();
            byte[] safe = backing.Data[MusicHomeSnapshotStore.Namespace].ToArray();
            await owner.SaveAsync(invalid);
            var retained = (await owner.ReadAsync())!;
            Check(retained.Platform == "wy" && PageId(retained.Pages.Single()) == "safe");
            await store.DrainAsync();
            Check(backing.Data[MusicHomeSnapshotStore.Namespace].SequenceEqual(safe));
            await using (var reopened = new CacheDocumentStore(backing))
            {
                var restored = (await new MusicHomeSnapshotStore(reopened).ReadAsync())!;
                Check(restored.Platform == "wy" && PageId(restored.Pages.Single()) == "safe");
            }

            backing.Data[MusicHomeSnapshotStore.Namespace] =
                JsonSerializer.SerializeToUtf8Bytes(new { platform = invalid.Platform, pages = invalid.Pages });
            await using var damaged = new CacheDocumentStore(backing);
            Check(await new MusicHomeSnapshotStore(damaged).ReadAsync() is null);
        }
    }
    private static async Task SlowDisk()
    {
        var backing = new HeldStore();
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        var reading = owner.ReadAsync();
        await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await owner.SaveAsync(Snapshot("wy", Page("wy", "saved"))).WaitAsync(TimeSpan.FromSeconds(2));
            Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "saved");
        }
        finally { backing.Release.TrySetResult(); }
        Check(PageId((await reading)!.Pages.Single()) == "saved");
        await store.DrainAsync();
    }
    private static async Task StorageFailures()
    {
        foreach (var error in new[] { DocumentStoreError.Protection, DocumentStoreError.Storage, DocumentStoreError.UnsafePath })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore { Failure = error };
            await using var store = new CacheDocumentStore(backing);
            var owner = new MusicHomeSnapshotStore(store);
            Check(await owner.ReadAsync() is null);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "saved")));
            Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "saved");
            await store.DrainAsync();
        }
    }
    private static async Task Sensitive()
    {
        foreach (string field in new[] { "credentials", "Cookie", "accessToken", "PASSWORD", "secret", "Authorization",
            "api_key", "headers", "scope", "cursor", "continuation", "snapshot", "expiresAt", "accountStamp" })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            await using var store = new CacheDocumentStore(backing);
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
            await store.DrainAsync();
            byte[] safe = backing.Data[MusicHomeSnapshotStore.Namespace].ToArray();
            var page = Page("wy", "unsafe");
            page["sections"]![0]!["items"]![0]!["action"]![field] = "synthetic";
            // Open action JSON remains valid for display, but cannot carry credentials or execution authority into persistence.
            Check(MusicCatalogDto.Read(JsonSerializer.SerializeToElement(page), "wy", "home", "")
                .Single().Items.Single().CanOpen);
            await owner.SaveAsync(Snapshot("wy", page));
            Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "safe");
            await store.DrainAsync();
            Check(backing.Data[MusicHomeSnapshotStore.Namespace].SequenceEqual(safe));

            page.Remove("ok");
            backing.Data[MusicHomeSnapshotStore.Namespace] =
                JsonSerializer.SerializeToUtf8Bytes(new { platform = "wy", pages = new[] { page } });
            await using var maliciousCache = new CacheDocumentStore(backing);
            var restoredOwner = new MusicHomeSnapshotStore(maliciousCache);
            Check(await restoredOwner.ReadAsync() is null);
            await restoredOwner.SaveAsync(Snapshot("wy", Page("wy", "repaired")));
            Check(PageId((await restoredOwner.ReadAsync())!.Pages.Single()) == "repaired");
            await maliciousCache.DrainAsync();
            await using var repairedCache = new CacheDocumentStore(backing);
            Check(PageId((await new MusicHomeSnapshotStore(repairedCache).ReadAsync())!.Pages.Single()) == "repaired");
        }
    }
    private static async Task SizeBoundary()
    {
        await using var store = new CacheDocumentStore();
        var owner = new MusicHomeSnapshotStore(store);
        var page = Page("wy", "boundary"); page.Remove("ok");
        page["sections"]![0]!["items"]![0]!["subtitle"] = "";
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new { platform = "wy", pages = new[] { page } }).Length;
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('x', LocalDocumentStore.MaximumDocumentBytes - overhead);
        await owner.SaveAsync(Snapshot("wy", page));
        Check(store.ReadMemory(MusicHomeSnapshotStore.Namespace).Data!.Length == LocalDocumentStore.MaximumDocumentBytes);
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('y', LocalDocumentStore.MaximumDocumentBytes - overhead + 1);
        await owner.SaveAsync(Snapshot("wy", page));
        var restored = (await owner.ReadAsync())!.Pages.Single();
        Check(restored.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("subtitle").GetString()![0] == 'x');
    }
    private static async Task Cancellation()
    {
        await using var store = new CacheDocumentStore();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => owner.SaveAsync(Snapshot("kg", Page("kg", "cancelled")), cancelled.Token));
        await Cancelled(() => owner.ReadAsync(cancelled.Token));
        Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "safe");
    }
    private static async Task CancelledRead()
    {
        var backing = new HeldStore(JsonSerializer.SerializeToUtf8Bytes(
            new { platform = "wy", pages = new[] { Page("wy", "stored") } }));
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = owner.ReadAsync(cancellation.Token);
        var stableRead = owner.ReadAsync();
        try
        {
            await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!cancelledRead.IsCompleted && !stableRead.IsCompleted);
            cancellation.Cancel();
            await Cancelled(() => cancelledRead.WaitAsync(TimeSpan.FromSeconds(5)));
            var draining = store.DrainAsync();
            Check(!stableRead.IsCompleted && !draining.IsCompleted);
            backing.Release.TrySetResult();
            Check(PageId((await stableRead.WaitAsync(TimeSpan.FromSeconds(5)))!.Pages.Single()) == "stored");
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.SaveAsync(Snapshot("wy", Page("wy", "after-cancellation")));
            Check(PageId((await owner.ReadAsync())!.Pages.Single()) == "after-cancellation");
            await store.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { backing.Release.TrySetResult(); }
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }
    private sealed class HeldStore(byte[]? data = null) : IDocumentStore
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
    private static string PageId(JsonElement page) => page.GetProperty("sections")[0].GetProperty("id").GetString()!;
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Home cache assertion"); }
}
