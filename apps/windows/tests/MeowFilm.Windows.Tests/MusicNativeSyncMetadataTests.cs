using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    // Included by the existing RecoveryCases registration; no new runner or product wiring is required.
    private static IEnumerable<TestCase> MetadataCases()
    {
        yield return new("Music sync WY metadata missing detail preserves newer local fields", MissingMetadata);
        yield return new("Music sync WY metadata secondary source preserves canonical recording", MissingSecondarySource);
        yield return new("Music sync WY metadata new placeholder later enriches same identity", MissingThenAvailable);
        yield return new("Music sync WY metadata network failure preserves duplicate occurrences", MissingOccurrences);
        yield return new("Music sync WY metadata write readback cannot downgrade songs", () => MissingReadback(false));
        yield return new("Music sync WY metadata pending reopen cannot downgrade or replay", () => MissingReadback(true));
        yield return new("Music sync WY metadata successful detail fetch is not a placeholder", MissingFetched);
        yield return new("Music sync WY canonical metadata read failure preserves all local songs", () => MissingRejected(false));
        yield return new("Music sync WY metadata auth failure cannot become membership fallback", () => MissingRejected(true));
        yield return new("Music sync WY differing metadata for duplicate members keeps one permanent recording", DuplicateMetadataConflict);
        yield return new("Music sync in-flight pull preserves current detail sources and newly queued permanent references", CurrentMetadataDuringPull);
        yield return new("Music sync KG in-flight members keep distinct registered UUIDs after metadata completion", RegisteredKgMembersDuringPull);
        yield return new("Music sync long canonical account collection and member IDs persist through an unknown write and cold read", LongCanonicalIds);
    }

    private sealed record SyncDiagnostic(JsonElement Library, JsonElement State);
    // Explicit test-only projection, not an optimistic token or a persistence protocol.
    private static async Task<SyncDiagnostic> ReadSyncDiagnosticAsync(Fixture fixture)
    {
        var current = await fixture.Owner.ReadLibrarySyncAsync();
        var library = JsonNode.Parse(current.Library.GetRawText())!.AsObject();
        library["songs"] = MusicTestStores.For(fixture.Store).Root["songs"]!.DeepClone();
        return new(E(library), current.State);
    }
    private static JsonObject SongStore(JsonElement library) => JsonNode.Parse(library.GetRawText())!["songs"]!.AsObject();
    private static void SameMetadata(JsonObject before, JsonObject after) =>
        Check(JsonNode.DeepEquals(before, after), "KNOWN_METADATA_DOWNGRADED");
    private static async Task LongCanonicalIds()
    {
        await using var f = new Fixture();
        string account = "account-" + new string('a', 96);
        string remote = "playlist-" + new string('p', 2400);
        string first = "member-" + new string('m', 2400);
        string second = "member-" + new string('n', 2400);
        string memberKey = JsonSerializer.Serialize(new object[] { first, 0 });
        f.Sessions.Values["tx"] = Sessions.Make("tx", 1, account);
        f.Server.AccountId = account;
        f.Server.Favorite.Id = remote;
        f.Server.Favorite.Add(1);
        f.Server.ChangeRemote = row => row with
        {
            Members = row.Members.Select(member => new MeowFilm.Capabilities.Sync.SyncMember(
                first, memberKey, Song("tx", first), member.Sort, member.HasMetadata)).ToArray()
        };
        await f.Enable();
        Ok(await f.Run());
        var before = await ReadSyncDiagnosticAsync(f);
        var binding = before.Library.GetProperty("collections")[0].GetProperty("bindings")[0];
        string songId = binding.GetProperty("map").GetProperty(memberKey).GetString()!;
        Check(Guid.TryParse(songId, out _) && S(binding, "accountId") == account &&
            S(binding, "id") == remote && binding.GetProperty("baseline")[0].GetString() == memberKey &&
            before.Library.GetProperty("accounts").GetProperty("tx").GetString() == account);
        var favorites = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
        Ok(await f.Owner.ExecuteAsync("music.library.addSongs", new
        {
            id = "favorites", ifRevision = favorites.GetProperty("revision").GetInt64(),
            songs = new[] { Song("tx", second, 2) }
        }));
        f.Server.OnWrite = (call, _) =>
        {
            Check(call.Parameters.GetProperty("ids")[0].GetString() == second &&
                call.Parameters.GetProperty("remote").GetProperty("id").GetString() == remote);
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = "NETWORK_ERROR" }));
        };
        Code(await f.Run(), "WRITE_UNCONFIRMED");
        var pending = (await ReadSyncDiagnosticAsync(f)).State.GetProperty("pending");
        Check(S(pending, "accountId") == account && S(pending, "remoteId") == remote &&
            pending.GetProperty("before")[0].GetString() == first &&
            pending.GetProperty("desired").EnumerateArray().Select(value => value.GetString()).SequenceEqual([first, second]) &&
            pending.GetProperty("ids")[0].GetString() == second && f.Server.Writes == 1);
        string savedPending = pending.GetRawText();
        await f.Restart();
        var reopened = await ReadSyncDiagnosticAsync(f);
        Check(reopened.State.GetProperty("pending").GetRawText() == savedPending &&
            reopened.Library.GetProperty("collections")[0].GetProperty("bindings")[0].GetProperty("map").GetProperty(memberKey).GetString() == songId &&
            reopened.Library.GetProperty("songs").GetProperty(songId).GetProperty("sources")[0].GetProperty("musicInfo").GetProperty("songmid").GetString() == first);
        Code(await f.Run(), "WRITE_UNCONFIRMED");
        Check(f.Server.Writes == 1, "UNKNOWN_WRITE_REPLAYED");
    }
    private static async Task MissingMetadata()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        var snapshot = await ReadSyncDiagnosticAsync(f); var data = JsonNode.Parse(snapshot.Library.GetRawText())!.AsObject();
        var song = data["songs"]!.AsObject().First().Value!.AsObject();
        song["album"] = "Preserved Album"; song["sources"]![0]!["musicInfo"]!["albumName"] = "Preserved Album";
        await f.Owner.AdoptLibrarySyncSongsAsync(await f.Owner.ReadLibrarySyncAsync(), [(song, true)], default);
        var before = await ReadSyncDiagnosticAsync(f);
        f.Server.WyMissingTracks.Add(1); Ok(await f.Run()); var after = await ReadSyncDiagnosticAsync(f);
        SameMetadata(SongStore(before.Library), SongStore(after.Library));
        Check(after.Library.GetProperty("collections")[0].GetProperty("bindings")[0].GetProperty("baseline")[0].GetString() == """["1001",0]""");
        Check(f.Server.WyDetailReads > 0 && f.Server.Writes == 0 && after.State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }
    private static async Task MissingSecondarySource()
    {
        await using var f = new Fixture("wy");
        var c = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
        Ok(await f.Owner.ExecuteAsync("music.library.addSongs", new { id = "favorites", ifRevision = c.GetProperty("revision").GetInt64(),
            songs = new[] { Song("wy", "1001"), Song("tx", "MID1") } }));
        var initial = await ReadSyncDiagnosticAsync(f); var songs = SongStore(initial.Library);
        Check(songs.Count == 1 && Guid.TryParse(songs.Single().Key, out _) &&
            songs.Single().Value!["sources"]!.AsArray().Select(s => s!["source"]!.GetValue<string>())
                .Order(StringComparer.Ordinal).SequenceEqual(["tx", "wy"]), "CANONICAL_SETUP");
        await f.Enable();
        var before = await ReadSyncDiagnosticAsync(f); SameMetadata(songs, SongStore(before.Library));
        f.Server.Favorite.Add(1); f.Server.WyMissingTracks.Add(1); Ok(await f.Run());
        var after = await ReadSyncDiagnosticAsync(f); SameMetadata(SongStore(before.Library), SongStore(after.Library));
        Check(f.Server.Writes == 0 && after.Library.GetProperty("collections")[0].GetProperty("order").GetArrayLength() == 1);
    }
    private static async Task MissingThenAvailable()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.WyMissingTracks.Add(1); await f.Enable(); Ok(await f.Run());
        var first = await ReadSyncDiagnosticAsync(f); var initial = SongStore(first.Library); var item = initial.Single();
        Check(Guid.TryParse(item.Key, out _) && item.Value!["title"]!.GetValue<string>() == "" &&
            item.Value!["duration"] is null && item.Value!["baselineDuration"] is null &&
            MusicSongFixture.Info(item.Value)["songmid"]!.GetValue<string>() == "1001");
        f.Server.WyMissingTracks.Clear(); await f.Restart(); Ok(await f.Run());
        var after = await ReadSyncDiagnosticAsync(f); var actual = SongStore(after.Library);
        Check(actual.Count == 1 && actual.ContainsKey(item.Key) && actual[item.Key]!["title"]!.GetValue<string>() == "Track 1");
        Check(actual[item.Key]!["artist"]!.GetValue<string>() == "Synthetic Artist" && actual[item.Key]!["duration"]!.GetValue<double>() == 200);
        Check(MusicSongFixture.Info(actual[item.Key]!)["name"]!.GetValue<string>() == "Track 1" &&
            MusicSongFixture.Info(actual[item.Key]!)["duration"]!.GetValue<double>() == 200 &&
            actual[item.Key]!["baselineDuration"]!.GetValue<double>() == 200);
        Check(after.Library.GetProperty("collections")[0].GetProperty("order")[0].GetString() == item.Key && f.Server.Writes == 0);
    }
    private static async Task MissingOccurrences()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); f.Server.Favorite.Add(1);
        await f.Enable(); Ok(await f.Run()); var before = await ReadSyncDiagnosticAsync(f);
        f.Server.WyMissingTracks.Add(1); f.Server.WyDetailNetworkFailure = true; Ok(await f.Run());
        var after = await ReadSyncDiagnosticAsync(f); SameMetadata(SongStore(before.Library), SongStore(after.Library));
        var collection = after.Library.GetProperty("collections")[0]; var binding = collection.GetProperty("bindings")[0];
        Check(collection.GetProperty("order").GetArrayLength() == 2 &&
            binding.GetProperty("baseline").EnumerateArray().Select(n => n.GetString())
                .SequenceEqual(["""["1001",0]""", """["1002",0]""", """["1001",1]"""]));
        Check(binding.GetProperty("map").GetProperty("""["1001",0]""").GetString() ==
            binding.GetProperty("map").GetProperty("""["1001",1]""").GetString());
        Check(f.Server.Writes == 0 && f.Server.WyDetailReads > 0);
    }
    private static async Task MissingReadback(bool reopen)
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        await f.AddFavorite(2); var before = await ReadSyncDiagnosticAsync(f);
        f.Server.FavoriteTarget = 2;
        f.Server.AfterWrite = () =>
        {
            f.Server.WyMissingTracks.UnionWith([1, 2]);
            if (reopen) f.Server.FailReads = true;
        };
        if (reopen)
        {
            Code(await f.Run(), "UPSTREAM_ERROR");
            Check((await ReadSyncDiagnosticAsync(f)).State.GetProperty("pending").ValueKind == JsonValueKind.Object);
            await f.Restart(); f.Server.FailReads = false;
        }
        Ok(await f.Run()); var after = await ReadSyncDiagnosticAsync(f);
        SameMetadata(SongStore(before.Library), SongStore(after.Library));
        Check(f.Server.Writes == 1 && f.Server.Favorite.Songs.SequenceEqual([1, 2]) && f.Server.WyDetailReads > 0);
        Check(after.State.GetProperty("pending").ValueKind == JsonValueKind.Null);
        Check(after.Library.GetProperty("collections")[0].GetProperty("intents").EnumerateObject().All(p => !p.Value.GetProperty("pending").GetBoolean()));
    }
    private static async Task MissingFetched()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.WyMissingTracks.Add(1); f.Server.WyDetailsAvailable = true;
        await f.Enable(); Ok(await f.Run()); var actual = SongStore((await ReadSyncDiagnosticAsync(f)).Library);
        Check(actual.Count == 1 && actual.First().Value!["title"]!.GetValue<string>() == "Track 1" && f.Server.WyDetailReads > 0 && f.Server.Writes == 0);
    }
    private static async Task MissingRejected(bool auth)
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        var before = await ReadSyncDiagnosticAsync(f); f.Server.WyMissingTracks.Add(1);
        if (auth) f.Server.WyDetailCode = 301;
        else f.Server.ChangeRemote = _ => throw new MusicPlatformException("NETWORK_ERROR");
        Code(await f.Run(), auth ? "AUTH_REQUIRED" : "NETWORK_ERROR");
        var after = await ReadSyncDiagnosticAsync(f);
        Check(JsonNode.DeepEquals(JsonNode.Parse(before.Library.GetRawText()), JsonNode.Parse(after.Library.GetRawText())));
        Check(f.Server.Writes == 0 && f.Server.WyDetailReads > 0);
    }
    private static async Task DuplicateMetadataConflict()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.Favorite.Add(1);
        f.Server.ChangeRemote = remote => remote with {
            Members = remote.Members.Select((member, index) => index == 0 ? member :
                member with { Song = MusicSongFixture.Online("wy", member.Id, "Updated metadata",
                    "Synthetic Artist", "Updated album", 201) }).ToArray()
        };
        await f.Enable(); Ok(await f.Run()); var after = await ReadSyncDiagnosticAsync(f);
        var songs = SongStore(after.Library); var binding = after.Library.GetProperty("collections")[0].GetProperty("bindings")[0];
        Check(songs.Count == 1 && Guid.TryParse(songs.Single().Key, out _) && f.Server.Writes == 0 &&
            songs.Single().Value!["title"]!.GetValue<string>() == "Updated metadata" &&
            MusicSongFixture.Info(songs.Single().Value!)["duration"]!.GetValue<double>() == 201 &&
            songs.Single().Value!["baselineDuration"]!.GetValue<double>() == 200 &&
            binding.GetProperty("baseline").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(["""["1001",0]""", """["1001",1]"""]) &&
            binding.GetProperty("map").EnumerateObject().All(pair => pair.Value.GetString() == songs.Single().Key));
    }
    private static async Task CurrentMetadataDuringPull()
    {
        await using var f = new Fixture();
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); await f.Enable();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Server.Entered = entered; f.Server.Release = new();
        var run = f.Run();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var empty = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Ok(await f.Owner.ReplaceQueueAsync([E(Song("tx", "MID1")), E(Song("tx", "MID500", 500))],
                0, null, empty.GetProperty("version").GetInt64()));
            var queued = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            string songId = S(queued.GetProperty("current").GetProperty("song"), "songId");
            string queueOnlyId = S(queued.GetProperty("entries")[1].GetProperty("song"), "songId");
            var newer = MusicSongFixture.Online("tx", "MID1", "Current corrected title", "Current corrected artist",
                "Current album", 201, cover: "https://images.example.test/current.png");
            MusicSongFixture.Info(newer)["size"] = "4096";
            var secondary = MusicSongFixture.Online("wy", "1001", "Current corrected title", "Current corrected artist",
                "Current album", 201, cover: "https://images.example.test/secondary.png");
            f.Runtime.OnRequest = (call, token) => call.Operation == "song"
                ? Task.FromResult(E(call.Platform == "tx" ? newer : secondary))
                : f.Server.Respond(call, token);
            await using (var catalog = new MusicNativeCatalogOwner(f.Runtime.Catalog, f.Owner))
            {
                var detail = Ok(await catalog.ExecuteAsync("music.catalog.song", new { platform = "tx", id = "MID1" }));
                var added = Ok(await catalog.ExecuteAsync("music.catalog.song", new { platform = "wy", id = "1001" }));
                Check(S(detail.GetProperty("song"), "songId") == songId && S(added.GetProperty("song"), "songId") == songId,
                    "REAL_DETAIL_READS_MUST_REUSE_THE_NEW_QUEUE_RECORDING");
            }
            var current = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Check(current.GetProperty("version").GetInt64() == queued.GetProperty("version").GetInt64() &&
                S(current, "currentId") == S(queued, "currentId") &&
                current.GetProperty("entries").EnumerateArray().Select(row => S(row, "id"))
                    .SequenceEqual(queued.GetProperty("entries").EnumerateArray().Select(row => S(row, "id"))));
            f.Server.Release.TrySetResult(); Ok(await run);
            var after = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Check(JsonElement.DeepEquals(after, current), "OLD_SYNC_METADATA_CANNOT_ROLL_BACK_CURRENT_DETAILS_OR_QUEUE_FACTS");
            var snapshot = await ReadSyncDiagnosticAsync(f);
            var songs = snapshot.Library.GetProperty("songs");
            var song = songs.GetProperty(songId);
            var collection = snapshot.Library.GetProperty("collections")[0];
            Check(songs.EnumerateObject().Count() == 3 && songs.TryGetProperty(queueOnlyId, out _) &&
                song.GetProperty("baselineDuration").GetDouble() == 200 && S(song, "title") == "Current corrected title" &&
                song.GetProperty("duration").GetDouble() == 201 && S(song, "cover_url") == "https://images.example.test/current.png" &&
                song.GetProperty("sources").EnumerateArray().Select(source => S(source, "source")).Order(StringComparer.Ordinal)
                    .SequenceEqual(["tx", "wy"]) && S(MusicSongFixture.Info(song), "size") == "4096",
                "SYNC_MUST_KEEP_CURRENT_SOURCES_UUID_AND_FIXED_BASELINE_IN_THE_SINGLE_REPOSITORY");
            Check(collection.GetProperty("order").GetArrayLength() == 2 &&
                collection.GetProperty("order")[0].GetString() == songId &&
                collection.GetProperty("bindings")[0].GetProperty("map").GetProperty("""["MID1",0]""").GetString() == songId &&
                collection.GetProperty("bindings")[0].GetProperty("map").GetProperty("""["MID2",0]""").GetString() ==
                    collection.GetProperty("order")[1].GetString() &&
                f.Server.Writes == 0 && f.Server.SearchReads == 0 && f.Server.Reads == 1 &&
                snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null,
                "REAL_SYNC_MEMBERSHIP_MUST_REFERENCE_REGISTERED_UUIDS_WITHOUT_EXTRA_READS_OR_WRITES");
            await f.Restart();
            var cold = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            var restored = await ReadSyncDiagnosticAsync(f);
            Check(S(cold, "currentId") == S(current, "currentId") &&
                JsonElement.DeepEquals(cold.GetProperty("entries"), current.GetProperty("entries")) &&
                JsonElement.DeepEquals(restored.Library.GetProperty("songs"), songs) &&
                JsonElement.DeepEquals(restored.Library.GetProperty("collections"), snapshot.Library.GetProperty("collections")),
                "CURRENT_FORMAT_REOPEN_MUST_RESOLVE_THE_SAME_LATEST_REPOSITORY_AND_MEMBERSHIP");
        }
        finally { f.Server.Release.TrySetResult(); await run; }
    }
    private static async Task RegisteredKgMembersDuringPull()
    {
        await using var f = new Fixture("kg");
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); await f.Enable();
        string firstHash = Native("kg", 1), secondHash = Native("kg", 2);
        string firstKey = f.Server.Favorite.Files[0].ToString(), secondKey = f.Server.Favorite.Files[1].ToString();
        JsonObject Missing(string hash) => MusicSongFixture.Online("kg", hash, "", "", "", 0, native: O(new { hash }));
        JsonObject Complete(string hash) => MusicSongFixture.Online("kg", hash, "Shared complete metadata",
            "Synthetic Artist", "Synthetic Album", 200, native: O(new { hash }));
        // File occurrence keys are separate from the hash in the canonical song.
        f.Server.ChangeRemote = remote => remote with {
            Members = remote.Members.Select(member => member with { Song = Complete(member.Id) }).ToArray()
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Server.Entered = entered; f.Server.Release = new();
        var run = f.Run();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var empty = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Ok(await f.Owner.ReplaceQueueAsync([E(Missing(secondHash)), E(Missing(firstHash))],
                0, null, empty.GetProperty("version").GetInt64()));
            var queued = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            string secondId = S(queued.GetProperty("entries")[0].GetProperty("song"), "songId");
            string firstId = S(queued.GetProperty("entries")[1].GetProperty("song"), "songId");
            Check(firstId != secondId && Guid.TryParseExact(firstId, "D", out _) && Guid.TryParseExact(secondId, "D", out _),
                "INCOMPLETE_DIFFERENT_SOURCES_MUST_REGISTER_INDEPENDENT_PERMANENT_UUIDS");
            f.Runtime.OnRequest = (call, token) => call.Operation == "song"
                ? Task.FromResult(E(Complete(call.Parameters.GetProperty("id").GetString()!)))
                : f.Server.Respond(call, token);
            await using (var catalog = new MusicNativeCatalogOwner(f.Runtime.Catalog, f.Owner))
            {
                var first = Ok(await catalog.ExecuteAsync("music.catalog.song", new { platform = "kg", id = firstHash }));
                var second = Ok(await catalog.ExecuteAsync("music.catalog.song", new { platform = "kg", id = secondHash }));
                Check(S(first.GetProperty("song"), "songId") == firstId && S(second.GetProperty("song"), "songId") == secondId,
                    "LATER_REAL_DETAILS_CANNOT_MERGE_TWO_REGISTERED_SOURCES");
            }
            var current = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            var before = await ReadSyncDiagnosticAsync(f);
            var currentSongs = before.Library.GetProperty("songs");
            Check(currentSongs.EnumerateObject().Count() == 2 &&
                currentSongs.EnumerateObject().All(pair => pair.Value.GetProperty("baselineDuration").GetDouble() == 200 &&
                    pair.Value.GetProperty("sources").GetArrayLength() == 1) &&
                MusicSongFixture.NativeId(currentSongs.GetProperty(firstId)) == firstHash &&
                MusicSongFixture.NativeId(currentSongs.GetProperty(secondId)) == secondHash &&
                current.GetProperty("version").GetInt64() == queued.GetProperty("version").GetInt64() &&
                S(current, "currentId") == S(queued, "currentId"),
                "DETAILS_MUST_KEEP_SOURCE_OWNERSHIP_AND_QUEUE_FACTS");
            f.Server.Release.TrySetResult(); Ok(await run);
            var after = await ReadSyncDiagnosticAsync(f);
            var collection = after.Library.GetProperty("collections")[0];
            var binding = collection.GetProperty("bindings")[0];
            Check(JsonElement.DeepEquals(after.Library.GetProperty("songs"), currentSongs) &&
                JsonElement.DeepEquals(Ok(await f.Owner.ExecuteAsync("music.queue.get")), current),
                "OLD_GROUPED_SYNC_DETAILS_CANNOT_REBIND_OR_DOWNGRADE_CURRENT_RECORDINGS");
            Check(collection.GetProperty("order").EnumerateArray().Select(value => value.GetString()).SequenceEqual([firstId, secondId]) &&
                binding.GetProperty("baseline").EnumerateArray().Select(value => value.GetString()).SequenceEqual([firstKey, secondKey]) &&
                binding.GetProperty("map").GetProperty(firstKey).GetString() == firstId &&
                binding.GetProperty("map").GetProperty(secondKey).GetString() == secondId &&
                f.Server.Reads == 1 && f.Server.SearchReads == 0 && f.Server.Writes == 0 &&
                after.State.GetProperty("pending").ValueKind == JsonValueKind.Null,
                "EACH_REAL_KG_FILE_MEMBER_MUST_RETAIN_ITS_REGISTERED_HASH_OWNER");
            await f.Restart();
            var cold = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            var restored = await ReadSyncDiagnosticAsync(f);
            Check(S(cold, "currentId") == S(current, "currentId") &&
                JsonElement.DeepEquals(cold.GetProperty("entries"), current.GetProperty("entries")) &&
                JsonElement.DeepEquals(restored.Library.GetProperty("songs"), currentSongs) &&
                JsonElement.DeepEquals(restored.Library.GetProperty("collections"), after.Library.GetProperty("collections")),
                "CURRENT_FORMAT_REOPEN_MUST_KEEP_BOTH_MEMBERS_AND_QUEUE_REFERENCES");
        }
        finally { f.Server.Release.TrySetResult(); await run; }
    }
}
