using System.IO;
using System.Text;
using MeowFilm.AppHost;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicHomeSnapshotTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music home optional cache reopens ordered pages without account or runtime authority", Roundtrip);
        yield return new("Music home cache and permanent songs reopen through one current App database", LocalReopen);
        yield return new("Music home bad cached content misses and fresh pages repair it", Damaged);
        yield return new("Music home foreign root pages miss without replacing accepted snapshots", PlatformMismatch);
        yield return new("Music home song cards and previews resolve current cross-platform repository references", SongReferences);
        yield return new("Music home display cache keeps empty results and has no arbitrary page cap", EmptyAndMany);
        yield return new("Music home cache waits for its queued transaction and actual commit", SlowDisk);
        yield return new("Music home cache failures preserve committed pages and recover without replay", StorageFailures);
        yield return new("Music home cache projects display fields and excludes nested credentials", Sensitive);
        yield return new("Music home cancelled saves and reads keep the accepted snapshot", Cancellation);
        yield return new("Music home cancelled cold reads preserve other readers and owner drain", CancelledRead);
        yield return new("Music home cache enforces the projection byte boundary without replacing accepted content", SizeBoundary);
    }
    private static async Task Roundtrip()
    {
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var store = new NativeTestDatabase(backing);
        var owner = new MusicHomeSnapshotStore(store);
        var first = Page("wy", "first");
        first["entries"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "daily", ["name"] = "每日推荐", ["requiresAccount"] = true,
            ["display"] = true, ["available"] = true, ["presentation"] = "static",
            ["action"] = new JsonObject { ["type"] = "daily", ["branch"] = "default" }
        });
        await owner.SaveAsync(Snapshot("wy", first, Page("wy", "second")));
        await store.DrainAsync();
        await using var reopened = new NativeTestDatabase(backing);
        var restored = (await new MusicHomeSnapshotStore(reopened).ReadAsync(_ => null))!;
        Check(restored.Platform == "wy" && restored.Pages.Select(PageId).SequenceEqual(new[] { "first", "second" }));
        var entry = MusicCatalogDto.Read(restored.Pages[0], "wy", "home", "").First();
        Check(entry.Items.Single() is { RequiresAccount: true, Display: true, Available: true });
        Check(entry.DisplayableCards(includeAccountEntries: false).Items.Count == 0);
        restored.Pages[0] = default;
        Check(PageId((await owner.ReadAsync(_ => null))!.Pages[0]) == "first");
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
            await using var accountReopened = new NativeTestDatabase(backing);
            var accountSnapshot = (await new MusicHomeSnapshotStore(accountReopened).ReadAsync(_ => null))!;
            Check(accountSnapshot.Platform == "wy" && accountSnapshot.Pages.Length == 1 &&
                PageId(accountSnapshot.Pages[0]) == content);
            Check(accountSnapshot.Pages[0].EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { "entries", "platform", "sections" }));
            string text = Encoding.UTF8.GetString(backing.HomeData!);
            Check(!text.Contains(account, StringComparison.Ordinal));
            foreach (string field in new[] { "ok", "scope", "accountStamp", "credentials", "cookie", "cursor",
                "more", "continuation", "snapshot", "expiresAt" })
                Check(!text.Contains("\"" + field + "\"", StringComparison.Ordinal));
            Check(await backing.Inner.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_music_account")) == 0);
        }
        await owner.SaveAsync(Snapshot("kg", Page("kg", "replacement")));
        var replacement = (await owner.ReadAsync(_ => null))!;
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
            await using (var store = new NativeTestDatabase(root))
            {
                await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: store);
                first = JsonNode.Parse((await library.AdoptPlatformResultAsync(
                    JsonSerializer.SerializeToElement(first), default,
                    shape: MusicLibraryOwner.PlatformSongShape.Catalog)).GetRawText())!.AsObject();
                var owner = new MusicHomeSnapshotStore(store);
                Check(await owner.ReadAsync(_ => null) is null);
                await owner.SaveAsync(Snapshot("wy", first, Page("wy", "disk-second")));
                await library.AdoptPlatformResultAsync(JsonSerializer.SerializeToElement(
                    MusicSongFixture.Online("wy", "101", "Current title", "Artist",
                        cover: "https://image.invalid/current.jpg")), default, shape: MusicLibraryOwner.PlatformSongShape.Song);
                await store.DrainAsync();
            }
            await using (var store = new NativeTestDatabase(root))
            {
                await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: store);
                Check((await library.ExecuteAsync("music.library.list")).GetProperty("ok").GetBoolean());
                var saved = (await new MusicHomeSnapshotStore(store).ReadAsync(library.SongSnapshot))!;
                Check(saved.Platform == "wy" && saved.Pages.Select(PageId).SequenceEqual(new[] { "disk-first", "disk-second" }));
                foreach (string field in new[] { "entries", "items" })
                    Check(JsonNode.DeepEquals(first[field], JsonNode.Parse(saved.Pages[0].GetProperty(field).GetRawText())));
                var sections = MusicCatalogDto.Read(saved.Pages[0], "wy", "home", "");
                var daily = sections[0].Items.Single();
                Check(daily.CanOpen && daily.RequiresAccount && daily.ActionId == "daily" &&
                    daily.Action!.Value.GetProperty("branch").GetString() == "style" &&
                    daily.DayOfMonth == 7 && daily.PreviewSongs.Single().Movement == "↑");
                Check(sections[1].Action is { ActionId: "feature", PageCode: "HOME_DISCOVERY_PAGE" });
                var song = sections[1].Items.Single(card => card.Song is not null);
                Check(song.ActionId == "101" && song.Song!.Title == "Current title" &&
                    song.Song.Cover == "https://image.invalid/current.jpg" &&
                    song.PlaybackSource!.Value.GetProperty("id").GetString() == "202");
                Check(sections[2].Items.Single().ActionId == "303");
                var document = await store.ReadRecordAsync(MusicHomeSnapshotStore.Namespace, MeowFilm.Capabilities.HomeStorageRecords.Home);
                using var json = JsonDocument.Parse(document.Data!);
                Check(!Encoding.UTF8.GetString(document.Data!).Contains("\"sources\"") &&
                    !Encoding.UTF8.GetString(document.Data!).Contains("https://image.invalid/song.jpg"));
                Check(json.RootElement.EnumerateObject().Select(property => property.Name)
                    .SequenceEqual(new[] { "platform", "pages" }));
                saved.Pages[0] = default;
                Check(PageId((await new MusicHomeSnapshotStore(store).ReadAsync(library.SongSnapshot))!.Pages[0]) == "disk-first");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root), true);
        }
    }
    private static async Task Damaged()
    {
        foreach (byte[] data in new[] { "{}"u8.ToArray(), "{\"platform\":\"wy\",\"pages\":null}"u8.ToArray(), "{\"platform\":\"wy\",\"pages\":[{}]}"u8.ToArray() })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            backing.HomeData = data;
            await using var store = new NativeTestDatabase(backing);
            var owner = new MusicHomeSnapshotStore(store);
            Check(await owner.ReadAsync(_ => null) is null);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "repaired")));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "repaired");
        }
    }
    private static async Task EmptyAndMany()
    {
        await using var store = new NativeTestDatabase();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", new JsonObject { ["platform"] = "wy", ["entries"] = new JsonArray() }));
        Check(MusicCatalogDto.Read((await owner.ReadAsync(_ => null))!.Pages.Single(), "wy", "home", "").Count == 0);
        var pages = Enumerable.Range(0, 1025).Select(i => Page("wy", i.ToString())).ToArray();
        await owner.SaveAsync(Snapshot("wy", pages));
        Check((await owner.ReadAsync(_ => null))!.Pages.Select(PageId).SequenceEqual(Enumerable.Range(0, 1025).Select(i => i.ToString())));
        await owner.SaveAsync(new("wy", []));
        Check((await owner.ReadAsync(_ => null))!.Pages.Length == 1025);
    }
    private static async Task PlatformMismatch()
    {
        foreach (var invalid in new[]
        {
            Snapshot("wy", Page("kg", "mismatch")),
            Snapshot("wy", Page("wy", "first"), Page("kg", "mixed"))
        })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            await using var store = new NativeTestDatabase(backing);
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
            await store.DrainAsync();
            byte[] safe = backing.HomeData!.ToArray();
            await owner.SaveAsync(invalid);
            var retained = (await owner.ReadAsync(_ => null))!;
            Check(retained.Platform == "wy" && PageId(retained.Pages.Single()) == "safe");
            await store.DrainAsync();
            Check(backing.HomeData!.SequenceEqual(safe));
            await using (var reopened = new NativeTestDatabase(backing))
            {
                var restored = (await new MusicHomeSnapshotStore(reopened).ReadAsync(_ => null))!;
                Check(restored.Platform == "wy" && PageId(restored.Pages.Single()) == "safe");
            }

            var damagedRows = backing.Project(HomeStorageRecords.Home, "device")!;
            damagedRows["pages"]![0]!["platform"] = "kg";
            backing.Seed(HomeStorageRecords.Home, damagedRows, "device");
            await using var damaged = new NativeTestDatabase(backing);
            Check(await new MusicHomeSnapshotStore(damaged).ReadAsync(_ => null) is null);
        }
    }
    private static async Task SongReferences()
    {
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var library = new MusicLibraryOwner(MusicTestStores.For(backing), database: MusicTestStores.For(backing).Storage);
        var song = await library.AdoptSongAsync(MusicSongFixture.Online("kg", "canonical-other-platform",
            "Initial title", "Artist", duration: 180, cover: "https://images.invalid/initial.png"), default);
        var page = Page("wy", "current");
        page["sections"]![0]!["items"] = new JsonArray(song.DeepClone());
        page["entries"] = new JsonArray(new JsonObject
        {
            ["id"] = "chart", ["name"] = "Current chart", ["presentation"] = "preview",
            ["action"] = new JsonObject { ["type"] = "playlist", ["id"] = "chart" },
            ["preview"] = new JsonArray(new JsonObject
            {
                ["title"] = "Stale preview title", ["artist"] = "Stale preview artist",
                ["cover"] = "https://images.invalid/stale-preview.png", ["rank"] = 1,
                ["song"] = song.DeepClone()
            })
        });
        await using (var cache = new NativeTestDatabase(backing))
        {
            await new MusicHomeSnapshotStore(cache).SaveAsync(Snapshot("wy", page));
            await cache.DrainAsync();
        }
        string saved = Encoding.UTF8.GetString(backing.HomeData!);
        Check(saved.Contains(LJ.S(song["songId"]), StringComparison.Ordinal) &&
            !saved.Contains("sources", StringComparison.Ordinal) &&
            !saved.Contains("Initial title", StringComparison.Ordinal) &&
            !saved.Contains("Stale preview", StringComparison.Ordinal) &&
            !saved.Contains("initial.png", StringComparison.Ordinal));
        await library.AdoptPlatformResultAsync(JsonSerializer.SerializeToElement(MusicSongFixture.Online(
            "kg", "canonical-other-platform", "Corrected title", "Artist", duration: 180,
            cover: "https://images.invalid/corrected.png")), default, shape: MusicLibraryOwner.PlatformSongShape.Song);
        await using var cold = new NativeTestDatabase(backing);
        var restored = (await new MusicHomeSnapshotStore(cold).ReadAsync(library.SongSnapshot))!;
        var cards = MusicCatalogDto.Read(restored.Pages.Single(), "wy", "home", "").SelectMany(section => section.Items).ToArray();
        var current = cards.Single(card => card.Song is not null);
        var preview = cards.Single(card => card.Id == "chart").PreviewSongs.Single();
        Check(current.Title == "Corrected title" && current.Platform == "kg" &&
            current.Song!.Id == LJ.S(song["songId"]) && preview.Title == current.Title &&
            preview.Cover == "https://images.invalid/corrected.png" && preview.Song!.Id == current.Song.Id);
    }
    private static async Task SlowDisk()
    {
        await using var backing = new HeldStore();
        await using var store = new NativeTestDatabase(backing);
        var owner = new MusicHomeSnapshotStore(store);
        var reading = owner.ReadAsync(_ => null);
        await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saving = owner.SaveAsync(Snapshot("wy", Page("wy", "saved")));
        var draining = store.DrainAsync();
        try
        {
            Check(!reading.IsCompleted && !saving.IsCompleted && !draining.IsCompleted);
        }
        finally { backing.Release.TrySetResult(); }
        Check(await reading.WaitAsync(TimeSpan.FromSeconds(5)) is null);
        await saving.WaitAsync(TimeSpan.FromSeconds(5));
        await draining.WaitAsync(TimeSpan.FromSeconds(5));
        Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "saved");
    }
    private static async Task StorageFailures()
    {
        foreach (var error in new[] { DocumentStoreError.Io, DocumentStoreError.Storage, DocumentStoreError.UnsafePath })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            await using var store = new NativeTestDatabase(backing);
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
            byte[] committed = backing.HomeData!.ToArray();
            backing.Failure = error;
            Check(await owner.ReadAsync(_ => null) is null);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "not-committed")));
            await store.DrainAsync();
            backing.Failure = null;
            Check(backing.HomeData!.SequenceEqual(committed));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "safe");
            await owner.SaveAsync(Snapshot("wy", Page("wy", "recovered")));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "recovered");
        }
    }
    private static async Task Sensitive()
    {
        foreach (string field in new[] { "credentials", "Cookie", "accessToken", "PASSWORD", "secret", "Authorization",
            "api_key", "headers", "scope", "cursor", "continuation", "snapshot", "expiresAt", "accountStamp" })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            await using var store = new NativeTestDatabase(backing);
            var owner = new MusicHomeSnapshotStore(store);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
            await store.DrainAsync();
            var page = Page("wy", "projected");
            page["sections"]![0]!["items"]![0]!["action"]![field] = "synthetic";
            // Open action JSON remains valid for display, but cannot carry credentials or execution authority into persistence.
            Check(MusicCatalogDto.Read(JsonSerializer.SerializeToElement(page), "wy", "home", "")
                .Single().Items.Single().CanOpen);
            await owner.SaveAsync(Snapshot("wy", page));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "projected");
            await store.DrainAsync();
            Check(!Encoding.UTF8.GetString(backing.HomeData!).Contains("\"" + field + "\"", StringComparison.Ordinal));

        }
    }
    private static async Task SizeBoundary()
    {
        await using var store = new NativeTestDatabase();
        var owner = new MusicHomeSnapshotStore(store);
        var page = Page("wy", "boundary"); page.Remove("ok");
        page["sections"]![0]!["items"]![0]!["subtitle"] = "";
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new { platform = "wy", pages = new[] { page } }).Length;
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('x', (8 * 1024 * 1024) - overhead);
        await owner.SaveAsync(Snapshot("wy", page));
        Check(store.RecordBytes(MeowFilm.Capabilities.HomeStorageRecords.Home)!.Length == (8 * 1024 * 1024));
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('y', (8 * 1024 * 1024) - overhead + 1);
        await owner.SaveAsync(Snapshot("wy", page));
        var restored = (await owner.ReadAsync(_ => null))!.Pages.Single();
        Check(restored.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("subtitle").GetString()![0] == 'x');
    }
    private static async Task Cancellation()
    {
        await using var store = new NativeTestDatabase();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => owner.SaveAsync(Snapshot("kg", Page("kg", "cancelled")), cancelled.Token));
        await Cancelled(() => owner.ReadAsync(_ => null, cancelled.Token));
        Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "safe");
    }
    private static async Task CancelledRead()
    {
        await using var backing = new HeldStore(JsonSerializer.SerializeToUtf8Bytes(
            new { platform = "wy", pages = new[] { Page("wy", "stored") } }));
        await using var store = new NativeTestDatabase(backing);
        var owner = new MusicHomeSnapshotStore(store);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = owner.ReadAsync(_ => null, cancellation.Token);
        var stableRead = owner.ReadAsync(_ => null);
        try
        {
            await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!cancelledRead.IsCompleted && !stableRead.IsCompleted);
            cancellation.Cancel();
            var draining = store.DrainAsync();
            Check(!cancelledRead.IsCompleted && !stableRead.IsCompleted && !draining.IsCompleted);
            backing.Release.TrySetResult();
            await Cancelled(() => cancelledRead.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(PageId((await stableRead.WaitAsync(TimeSpan.FromSeconds(5)))!.Pages.Single()) == "stored");
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.SaveAsync(Snapshot("wy", Page("wy", "after-cancellation")));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "after-cancellation");
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
    private sealed class HeldStore : NativeTestDatabase
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HeldStore(byte[]? data = null)
        {
            if (data is not null)
            {
                var root = JsonNode.Parse(data)!.AsObject();
                new MusicHomeSnapshotStore(Inner).SaveAsync(new MusicHomeSnapshot(root["platform"]!.GetValue<string>(), root["pages"]!.AsArray().Select(page => JsonSerializer.SerializeToElement(page)).ToArray())).GetAwaiter().GetResult();
            }
        }
        public override Task<T> ReadAsync<T>(Func<DatabaseSession,T> read, CancellationToken token = default) =>
            base.ReadAsync(db => { Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return read(db); },token);
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
