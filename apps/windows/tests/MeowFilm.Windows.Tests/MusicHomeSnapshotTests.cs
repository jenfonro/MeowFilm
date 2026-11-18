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
        yield return new("Music home cache reopens through its independent local cache namespace and root", LocalReopen);
        yield return new("Music home cold cache retains recommendation action parameters without credentials", ActionParameters);
        yield return new("Music home bad cached content misses and fresh pages repair it", Damaged);
        yield return new("Music home cache uses its native platform scope without rejecting partial page fields", PlatformMismatch);
        yield return new("Music home song cards and previews resolve current cross-platform repository references", SongReferences);
        yield return new("Music home display cache keeps empty results and has no arbitrary page cap", EmptyAndMany);
        yield return new("Music home cache write does not wait for initial read or persistent receipt", SlowDisk);
        yield return new("Music home cache storage failures cannot negate successful pages", StorageFailures);
        yield return new("Music home cache projects display fields and excludes nested credentials", Sensitive);
        yield return new("Music home cancelled saves and reads keep the accepted snapshot", Cancellation);
        yield return new("Music home cancelled cold reads preserve other readers and owner drain", CancelledRead);
        yield return new("Music home cache enforces the document byte boundary without replacing accepted content", SizeBoundary);
    }
    private static async Task Roundtrip()
    {
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        var first = Page("wy", "first");
        first["sections"]!.AsArray().Insert(0, Feature(new JsonObject
        {
            ["id"] = "daily", ["kind"] = "daily", ["label"] = "每日推荐", ["title"] = "每日推荐", ["requiresAccount"] = true,
            ["display"] = true, ["available"] = true, ["presentation"] = "static",
            ["action"] = new JsonObject { ["type"] = "daily", ["branch"] = "default" }
        }));
        await owner.SaveAsync(Snapshot("wy", first, Page("wy", "second")));
        await store.DrainAsync();
        await using var reopened = new CacheDocumentStore(backing);
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
            await using var accountReopened = new CacheDocumentStore(backing);
            var accountSnapshot = (await new MusicHomeSnapshotStore(accountReopened).ReadAsync(_ => null))!;
            Check(accountSnapshot.Platform == "wy" && accountSnapshot.Pages.Length == 1 &&
                PageId(accountSnapshot.Pages[0]) == content);
            Check(accountSnapshot.Pages[0].EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(new[] { "platform", "sections" }));
            string text = Encoding.UTF8.GetString(backing.Data[MusicHomeSnapshotStore.Namespace]);
            Check(!text.Contains(account, StringComparison.Ordinal));
            foreach (string field in new[] { "ok", "scope", "accountStamp", "credentials", "cookie", "cursor",
                "more", "continuation", "snapshot", "expiresAt" })
                Check(!text.Contains("\"" + field + "\"", StringComparison.Ordinal));
            Check(backing.Data.Keys.SequenceEqual(new[] { MusicHomeSnapshotStore.Namespace }));
        }
        await owner.SaveAsync(Snapshot("kg", Page("kg", "replacement")));
        var replacement = (await owner.ReadAsync(_ => null))!;
        Check(replacement is { Platform: "kg", Pages.Length: 1 } && PageId(replacement.Pages[0]) == "replacement");
    }
    private static async Task LocalReopen()
    {
        string root = TestWorkspace.DataPath("music-home-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "songs"));
        try
        {
            await using var songs = new MusicRecordTestStore(Path.Combine(root, "songs"));
            var first = Page("wy", "disk-first");
            first["sections"]!.AsArray().Insert(0, Feature(new JsonObject
            {
                ["id"] = "daily-entry", ["key"] = "daily-key", ["kind"] = "daily", ["title"] = "每日推荐", ["label"] = "每日推荐",
                ["contextText"] = "每日推荐 | 从「今日歌曲」听起", ["playCount"] = 3031000, ["coverLabel"] = "推荐",
                ["presentation"] = "preview", ["dayOfMonth"] = 7, ["decoration"] = "calendar",
                ["requiresAccount"] = true, ["display"] = true, ["available"] = true,
                ["action"] = new JsonObject { ["type"] = "daily", ["branch"] = "style" },
                ["preview"] = new JsonArray(new JsonObject
                {
                    ["title"] = "Preview", ["artist"] = "Artist", ["cover"] = "https://image.invalid/preview.jpg",
                    ["rank"] = 2, ["lastRank"] = 3
                })
            }));
            var section = first["sections"]![1]!;
            section["action"] = new JsonObject
            {
                ["type"] = "channel",
                ["channel"] = new JsonObject { ["id"] = "feature", ["pageCode"] = "HOME_DISCOVERY_PAGE" }
            };
            section["items"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "song-card", ["key"] = "song-key", ["kind"] = "song", ["title"] = "Card title",
                ["action"] = new JsonObject { ["type"] = "song", ["id"] = "101" },
                ["song"] = MusicSongFixture.Online("wy", "101", "Song", "Artist", cover: "https://image.invalid/song.jpg"),
                ["playbackSource"] = new JsonObject { ["type"] = "playlist", ["id"] = "202" }
            });
            first["sections"]!.AsArray().Add(new JsonObject
            {
                ["layout"] = "song-list", ["items"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "song", ["title"] = "Root song",
                    ["action"] = new JsonObject { ["type"] = "song", ["id"] = "303" },
                    ["song"] = MusicSongFixture.Online("wy", "303", "Root song")
                })
            });
            first["expiresAt"] = 1; // Past service lifetime is not a display-cache TTL.
            await using (var backing = new LocalDocumentStore(root, AppIdentity.CacheNamespaces,
                protectedNamespaces: AppIdentity.CacheProtectedNamespaces))
            await using (var store = new CacheDocumentStore(backing))
            {
                await using var library = new MusicLibraryOwner(MusicTestStores.For(songs));
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
            await using (var backing = new LocalDocumentStore(root, AppIdentity.CacheNamespaces,
                protectedNamespaces: AppIdentity.CacheProtectedNamespaces))
            await using (var store = new CacheDocumentStore(backing))
            {
                await using var library = new MusicLibraryOwner(MusicTestStores.For(songs));
                Check((await library.ExecuteAsync("music.library.list")).GetProperty("ok").GetBoolean());
                var saved = (await new MusicHomeSnapshotStore(store).ReadAsync(library.SongSnapshot))!;
                Check(saved.Platform == "wy" && saved.Pages.Select(PageId).SequenceEqual(new[] { "disk-first", "disk-second" }));
                var sections = MusicCatalogDto.Read(saved.Pages[0], "wy", "home", "");
                var daily = sections[0].Items.Single();
                Check(daily.CanOpen && daily.RequiresAccount && daily.ActionId == "daily" &&
                    daily.Action!.Value.GetProperty("branch").GetString() == "style" &&
                    daily.DayOfMonth == 7 && daily.PreviewSongs.Single().Movement == "↑");
                Check(daily.ContextText == "每日推荐 | 从「今日歌曲」听起" && daily.Caption == daily.ContextText &&
                    daily.PlayCount == 3031000 && daily.CoverLabel == "推荐");
                Check(sections[1].Action is { ActionId: "feature", PageCode: "HOME_DISCOVERY_PAGE" });
                var song = sections[1].Items.Single(card => card.Song is not null);
                Check(song.ActionId == "101" && song.Song!.Title == "Current title" &&
                    song.Song.Cover == "https://image.invalid/current.jpg" &&
                    song.PlaybackSource!.Value.GetProperty("id").GetString() == "202");
                Check(sections[2].Items.Single().ActionId == "303");
                var document = await store.ReadAsync(MusicHomeSnapshotStore.Namespace);
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
    private static async Task ActionParameters()
    {
        var actions = JsonNode.Parse("""
            [
              {"type":"similar","sourceIds":["9007199254740993","42"],"sourceType":"song"},
              {"type":"fm","provider":"rule","ruleId":"rule-1","tabTagId":"tag-2","sourceName":"Rule FM"},
              {"type":"fm","mode":"scene","submode":"work"},
              {"type":"heartbeat","entry":"favorites","songId":"9007199254740993"}
            ]
            """)!.AsArray();
        var page = Page("wy", "actions");
        page["sections"]![0]!["items"] = new JsonArray(actions.Select((action, index) =>
        {
            var saved = action!.DeepClone();
            saved["credentials"] = new JsonObject { ["cookie"] = "synthetic" };
            saved["scope"] = "synthetic-account";
            return (JsonNode?)new JsonObject
            {
                ["id"] = index.ToString(), ["kind"] = action["type"]!.GetValue<string>(),
                ["title"] = "Recommendation", ["action"] = saved
            };
        }).ToArray());
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using (var store = new CacheDocumentStore(backing))
        {
            await new MusicHomeSnapshotStore(store).SaveAsync(Snapshot("wy", page));
            await store.DrainAsync();
        }
        await using var reopened = new CacheDocumentStore(backing);
        var snapshot = (await new MusicHomeSnapshotStore(reopened).ReadAsync(_ =>
            throw new InvalidOperationException("An action seed is not a repository song reference")))!;
        var cards = MusicCatalogDto.Read(snapshot.Pages.Single(), "wy", "home", "").Single().Items;
        Check(cards.Count == actions.Count);
        for (int index = 0; index < actions.Count; index++)
        {
            Check(cards[index].CanOpen && cards[index].Action is not null);
            Check(JsonNode.DeepEquals(JsonNode.Parse(cards[index].Action!.Value.GetRawText()), actions[index]));
        }
    }
    private static async Task Damaged()
    {
        foreach (byte[] data in new[] { "{"u8.ToArray(), "[]"u8.ToArray(), "{\"pages\":null}"u8.ToArray(),
            "{\"platform\":\"unknown\",\"pages\":[{}]}"u8.ToArray() })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            backing.Data[MusicHomeSnapshotStore.Namespace] = data;
            await using var store = new CacheDocumentStore(backing);
            var owner = new MusicHomeSnapshotStore(store);
            Check(await owner.ReadAsync(_ => null) is null);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "repaired")));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "repaired");
        }
    }
    private static async Task EmptyAndMany()
    {
        await using var store = new CacheDocumentStore();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", new JsonObject { ["platform"] = "wy", ["sections"] = new JsonArray() }));
        Check(MusicCatalogDto.Read((await owner.ReadAsync(_ => null))!.Pages.Single(), "wy", "home", "").Count == 0);
        var pages = Enumerable.Range(0, 1025).Select(i => Page("wy", i.ToString())).ToArray();
        await owner.SaveAsync(Snapshot("wy", pages));
        Check((await owner.ReadAsync(_ => null))!.Pages.Select(PageId).SequenceEqual(Enumerable.Range(0, 1025).Select(i => i.ToString())));
        await owner.SaveAsync(new("wy", []));
        Check((await owner.ReadAsync(_ => null))!.Pages.Length == 1025);
    }
    private static async Task PlatformMismatch()
    {
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        var partial = Page("kg", "partial");
        partial["sections"]!.AsArray().Add(new JsonObject { ["title"] = "Empty section" });
        await owner.SaveAsync(Snapshot("wy", partial, new JsonObject()));
        var restored = (await owner.ReadAsync(_ => null))!;
        Check(restored.Platform == "wy" && restored.Pages.Length == 2 && PageId(restored.Pages[0]) == "partial");
        Check(restored.Pages.All(page => page.GetProperty("platform").GetString() == "wy"));
        Check(MusicCatalogDto.Read(restored.Pages[0], "wy", "home", "").Count == 2);
        await owner.SaveAsync(Snapshot("unknown", Page("wy", "invalid-scope")));
        Check(PageId((await owner.ReadAsync(_ => null))!.Pages[0]) == "partial");
    }
    private static async Task SongReferences()
    {
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var library = new MusicLibraryOwner(MusicTestStores.For(backing));
        var song = await library.AdoptSongAsync(MusicSongFixture.Online("kg", "canonical-other-platform",
            "Initial title", "Artist", duration: 180, cover: "https://images.invalid/initial.png"), default);
        var page = Page("wy", "current");
        page["sections"]![0]!["items"] = new JsonArray(new JsonObject
        {
            ["kind"] = "song", ["title"] = "Independent card", ["cover"] = "https://images.invalid/card.png",
            ["song"] = song.DeepClone()
        });
        page["sections"]!.AsArray().Add(Feature(new JsonObject
        {
            ["id"] = "chart", ["kind"] = "playlist", ["title"] = "Current chart", ["presentation"] = "preview",
            ["action"] = new JsonObject { ["type"] = "playlist", ["id"] = "chart" },
            ["preview"] = new JsonArray(new JsonObject
            {
                ["title"] = "Stale preview title", ["artist"] = "Stale preview artist",
                ["cover"] = "https://images.invalid/stale-preview.png", ["rank"] = 1,
                ["song"] = song.DeepClone()
            })
        }));
        await using (var cache = new CacheDocumentStore(backing))
        {
            await new MusicHomeSnapshotStore(cache).SaveAsync(Snapshot("wy", page));
            await cache.DrainAsync();
        }
        string saved = Encoding.UTF8.GetString(backing.Data[MusicHomeSnapshotStore.Namespace]);
        Check(saved.Contains(LJ.S(song["songId"]), StringComparison.Ordinal) &&
            !saved.Contains("sources", StringComparison.Ordinal) &&
            !saved.Contains("Initial title", StringComparison.Ordinal) &&
            saved.Contains("Stale preview", StringComparison.Ordinal) &&
            !saved.Contains("initial.png", StringComparison.Ordinal));
        await library.AdoptPlatformResultAsync(JsonSerializer.SerializeToElement(MusicSongFixture.Online(
            "kg", "canonical-other-platform", "Corrected title", "Artist", duration: 180,
            cover: "https://images.invalid/corrected.png")), default, shape: MusicLibraryOwner.PlatformSongShape.Song);
        await using var cold = new CacheDocumentStore(backing);
        var restored = (await new MusicHomeSnapshotStore(cold).ReadAsync(library.SongSnapshot))!;
        var cards = MusicCatalogDto.Read(restored.Pages.Single(), "wy", "home", "").SelectMany(section => section.Items).ToArray();
        var current = cards.Single(card => card.Song is not null);
        var preview = cards.Single(card => card.Id == "chart").PreviewSongs.Single();
        Check(current.Title == "Independent card" && current.Cover == "https://images.invalid/card.png" &&
            current.Song!.Title == "Corrected title" && current.Song.Id == LJ.S(song["songId"]) && preview.Title == current.Song.Title &&
            preview.Cover == "https://images.invalid/corrected.png" && preview.Song!.Id == current.Song.Id);
    }
    private static async Task SlowDisk()
    {
        var backing = new HeldStore();
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        var reading = owner.ReadAsync(_ => null);
        await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await owner.SaveAsync(Snapshot("wy", Page("wy", "saved"))).WaitAsync(TimeSpan.FromSeconds(2));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "saved");
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
            Check(await owner.ReadAsync(_ => null) is null);
            await owner.SaveAsync(Snapshot("wy", Page("wy", "saved")));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "saved");
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
            var page = Page("wy", "projected");
            page["sections"]![0]!["items"]![0]!["action"]![field] = "synthetic";
            // Open action JSON remains valid for display, but cannot carry credentials or execution authority into persistence.
            Check(MusicCatalogDto.Read(JsonSerializer.SerializeToElement(page), "wy", "home", "")
                .Single().Items.Single().CanOpen);
            await owner.SaveAsync(Snapshot("wy", page));
            Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "projected");
            await store.DrainAsync();
            Check(!Encoding.UTF8.GetString(backing.Data[MusicHomeSnapshotStore.Namespace]).Contains("\"" + field + "\"", StringComparison.Ordinal));

            page.Remove("ok");
            backing.Data[MusicHomeSnapshotStore.Namespace] =
                JsonSerializer.SerializeToUtf8Bytes(new { platform = "wy", pages = new[] { page } });
            await using var maliciousCache = new CacheDocumentStore(backing);
            var restoredOwner = new MusicHomeSnapshotStore(maliciousCache);
            var projected = (await restoredOwner.ReadAsync(_ => null))!;
            Check(PageId(projected.Pages.Single()) == "projected" &&
                !projected.Pages.Single().GetRawText().Contains("\"" + field + "\"", StringComparison.Ordinal));
            await restoredOwner.SaveAsync(Snapshot("wy", Page("wy", "repaired")));
            Check(PageId((await restoredOwner.ReadAsync(_ => null))!.Pages.Single()) == "repaired");
            await maliciousCache.DrainAsync();
            await using var repairedCache = new CacheDocumentStore(backing);
            Check(PageId((await new MusicHomeSnapshotStore(repairedCache).ReadAsync(_ => null))!.Pages.Single()) == "repaired");
        }
    }
    private static async Task SizeBoundary()
    {
        await using var store = new CacheDocumentStore();
        var owner = new MusicHomeSnapshotStore(store);
        var page = Page("wy", "boundary"); page.Remove("ok");
        page["sections"]![0]!["items"]![0]!["subtitle"] = "";
        await owner.SaveAsync(Snapshot("wy", page));
        int overhead = store.ReadMemory(MusicHomeSnapshotStore.Namespace).Data!.Length;
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('x', CacheDocumentStore.MaximumDocumentBytes - overhead);
        await owner.SaveAsync(Snapshot("wy", page));
        Check(store.ReadMemory(MusicHomeSnapshotStore.Namespace).Data!.Length == CacheDocumentStore.MaximumDocumentBytes);
        page["sections"]![0]!["items"]![0]!["subtitle"] = new string('y', CacheDocumentStore.MaximumDocumentBytes - overhead + 1);
        await owner.SaveAsync(Snapshot("wy", page));
        var restored = (await owner.ReadAsync(_ => null))!.Pages.Single();
        Check(restored.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("subtitle").GetString()![0] == 'x');
    }
    private static async Task Cancellation()
    {
        await using var store = new CacheDocumentStore();
        var owner = new MusicHomeSnapshotStore(store);
        await owner.SaveAsync(Snapshot("wy", Page("wy", "safe")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => owner.SaveAsync(Snapshot("kg", Page("kg", "cancelled")), cancelled.Token));
        await Cancelled(() => owner.ReadAsync(_ => null, cancelled.Token));
        Check(PageId((await owner.ReadAsync(_ => null))!.Pages.Single()) == "safe");
    }
    private static async Task CancelledRead()
    {
        var backing = new HeldStore(JsonSerializer.SerializeToUtf8Bytes(
            new { platform = "wy", pages = new[] { Page("wy", "stored") } }));
        await using var store = new CacheDocumentStore(backing);
        var owner = new MusicHomeSnapshotStore(store);
        using var cancellation = new CancellationTokenSource();
        var cancelledRead = owner.ReadAsync(_ => null, cancellation.Token);
        var stableRead = owner.ReadAsync(_ => null);
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
        ["ok"] = true, ["platform"] = platform,
        ["sections"] = new JsonArray(new JsonObject
        {
            ["id"] = id, ["title"] = "Home " + id, ["layout"] = "playlist-rail",
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = id, ["kind"] = "playlist", ["title"] = "Playlist " + id, ["cover"] = "https://image.invalid/playlist.jpg",
                ["action"] = new JsonObject { ["type"] = "playlist", ["id"] = id }
            })
        })
    };

    private static JsonObject Feature(params JsonObject[] cards) => new()
    {
        ["id"] = "features", ["layout"] = "feature-rail",
        ["items"] = new JsonArray(cards.Cast<JsonNode?>().ToArray())
    };
    private static MusicHomeSnapshot Snapshot(string platform, params JsonObject[] pages) =>
        new(platform, pages.Select(page => JsonSerializer.SerializeToElement(page)).ToArray());
    private static string PageId(JsonElement page) => page.GetProperty("sections").EnumerateArray().First(section =>
        MusicCatalogDto.Text(section, "layout") == "playlist-rail").GetProperty("id").GetString()!;
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Home cache assertion"); }
}
