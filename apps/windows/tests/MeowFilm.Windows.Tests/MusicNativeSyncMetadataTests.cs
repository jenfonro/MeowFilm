using System.Text.Json;
using System.Text.Json.Nodes;

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
        yield return new("Music sync WY metadata wrong detail identity blocks merge", () => MissingRejected(false));
        yield return new("Music sync WY metadata auth failure cannot become membership fallback", () => MissingRejected(true));
        yield return new("Music sync WY metadata conflicting duplicate details block every write", DuplicateMetadataConflict);
    }

    private static JsonObject SongStore(JsonElement library) => JsonNode.Parse(library.GetRawText())!["songs"]!.AsObject();
    private static void SameMetadata(JsonObject before, JsonObject after) =>
        Check(JsonNode.DeepEquals(before, after), "KNOWN_METADATA_DOWNGRADED");
    private static async Task MissingMetadata()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); var data = JsonNode.Parse(snapshot.Library.GetRawText())!.AsObject();
        var song = data["songs"]!.AsObject().First().Value!.AsObject();
        song["album"] = "Preserved Album"; song["sources"]![0]!["musicInfo"]!["albumName"] = "Preserved Album";
        await f.Owner.CommitLibrarySyncAsync(snapshot, E(data), snapshot.State);
        var before = await f.Owner.ReadLibrarySyncAsync();
        f.Server.WyMissingTracks.Add(1); Ok(await f.Run()); var after = await f.Owner.ReadLibrarySyncAsync();
        SameMetadata(SongStore(before.Library), SongStore(after.Library));
        Check(after.Library.GetProperty("collections")[0].GetProperty("bindings")[0].GetProperty("baseline")[0].GetString() == "1001");
        Check(f.Server.WyDetailReads > 0 && f.Server.Writes == 0 && after.State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }
    private static async Task MissingSecondarySource()
    {
        await using var f = new Fixture("wy");
        var c = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
        Ok(await f.Owner.ExecuteAsync("music.library.addSongs", new { id = "favorites", ifRevision = c.GetProperty("revision").GetInt64(),
            songs = new[] { Song("wy", "1001"), Song("tx", "MID1") } }));
        var initial = await f.Owner.ReadLibrarySyncAsync(); var songs = SongStore(initial.Library);
        Check(songs.Count == 1 && MusicSongFixture.Info(songs.First().Value!)["source"]!.GetValue<string>() == "tx" &&
            songs.First().Value!["sources"]!.AsArray().Count == 2, "SYNTHETIC_CANONICAL_SETUP");
        await f.Enable();
        // Enabling already passes through the public owner's existing source ordering normalization.
        // Assert that this preserves content, then take the actual pre-pull baseline, not a pre-enable draft.
        var firstSong = songs.First().Value!.AsObject();
        firstSong["sources"] = new JsonArray(firstSong["sources"]!.AsArray().OrderBy(s => s!["sourceKey"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(s => s!.DeepClone()).ToArray());
        var before = await f.Owner.ReadLibrarySyncAsync(); SameMetadata(songs, SongStore(before.Library));
        f.Server.Favorite.Add(1); f.Server.WyMissingTracks.Add(1); Ok(await f.Run());
        var after = await f.Owner.ReadLibrarySyncAsync(); SameMetadata(SongStore(before.Library), SongStore(after.Library));
        Check(f.Server.Writes == 0 && after.Library.GetProperty("collections")[0].GetProperty("order").GetArrayLength() == 1);
    }
    private static async Task MissingThenAvailable()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.WyMissingTracks.Add(1); await f.Enable(); Ok(await f.Run());
        var first = await f.Owner.ReadLibrarySyncAsync(); var initial = SongStore(first.Library); var item = initial.Single();
        Check(item.Value!["title"]!.GetValue<string>() == "未知歌曲" && MusicSongFixture.Info(item.Value)["songmid"]!.GetValue<string>() == "1001");
        f.Server.WyMissingTracks.Clear(); await f.Restart(); Ok(await f.Run());
        var after = await f.Owner.ReadLibrarySyncAsync(); var actual = SongStore(after.Library);
        Check(actual.Count == 1 && actual.ContainsKey(item.Key) && actual[item.Key]!["title"]!.GetValue<string>() == "Track 1");
        Check(actual[item.Key]!["artist"]!.GetValue<string>() == "Synthetic Artist" && actual[item.Key]!["duration"]!.GetValue<double>() == 200);
        Check(after.Library.GetProperty("collections")[0].GetProperty("order")[0].GetString() == item.Key && f.Server.Writes == 0);
    }
    private static async Task MissingOccurrences()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); f.Server.Favorite.Add(1);
        await f.Enable(); Ok(await f.Run()); var before = await f.Owner.ReadLibrarySyncAsync();
        f.Server.WyMissingTracks.Add(1); f.Server.WyDetailNetworkFailure = true; Ok(await f.Run());
        var after = await f.Owner.ReadLibrarySyncAsync(); SameMetadata(SongStore(before.Library), SongStore(after.Library));
        var collection = after.Library.GetProperty("collections")[0]; var binding = collection.GetProperty("bindings")[0];
        Check(collection.GetProperty("order").GetArrayLength() == 2 &&
            binding.GetProperty("baseline").EnumerateArray().Select(n => n.GetString()).SequenceEqual(["1001", "1002", "1001#1"]));
        Check(binding.GetProperty("map").GetProperty("1001").GetString() == binding.GetProperty("map").GetProperty("1001#1").GetString());
        Check(f.Server.Writes == 0 && f.Server.WyDetailReads > 0);
    }
    private static async Task MissingReadback(bool reopen)
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        await f.AddFavorite(2); var before = await f.Owner.ReadLibrarySyncAsync();
        f.Server.FavoriteTarget = 2;
        f.Server.AfterWrite = () =>
        {
            f.Server.WyMissingTracks.UnionWith([1, 2]);
            if (reopen) f.Server.FailReads = true;
        };
        if (reopen)
        {
            Code(await f.Run(), "UPSTREAM_ERROR");
            Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Object);
            await f.Restart(); f.Server.FailReads = false;
        }
        Ok(await f.Run()); var after = await f.Owner.ReadLibrarySyncAsync();
        SameMetadata(SongStore(before.Library), SongStore(after.Library));
        Check(f.Server.Writes == 1 && f.Server.Favorite.Songs.SequenceEqual([1, 2]) && f.Server.WyDetailReads > 0);
        Check(after.State.GetProperty("pending").ValueKind == JsonValueKind.Null);
        Check(after.Library.GetProperty("collections")[0].GetProperty("intents").EnumerateObject().All(p => !p.Value.GetProperty("pending").GetBoolean()));
    }
    private static async Task MissingFetched()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.WyMissingTracks.Add(1); f.Server.WyDetailsAvailable = true;
        await f.Enable(); Ok(await f.Run()); var actual = SongStore((await f.Owner.ReadLibrarySyncAsync()).Library);
        Check(actual.Count == 1 && actual.First().Value!["title"]!.GetValue<string>() == "Track 1" && f.Server.WyDetailReads > 0 && f.Server.Writes == 0);
    }
    private static async Task MissingRejected(bool auth)
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        var before = await f.Owner.ReadLibrarySyncAsync(); f.Server.WyMissingTracks.Add(1);
        f.Server.WyWrongDetail = !auth; f.Server.WyDetailCode = auth ? 301 : 200;
        Code(await f.Run(), auth ? "AUTH_REQUIRED" : "IDENTITY_MISMATCH");
        var after = await f.Owner.ReadLibrarySyncAsync();
        Check(JsonNode.DeepEquals(JsonNode.Parse(before.Library.GetRawText()), JsonNode.Parse(after.Library.GetRawText())));
        Check(f.Server.Writes == 0 && f.Server.WyDetailReads > 0);
    }
    private static async Task DuplicateMetadataConflict()
    {
        await using var f = new Fixture("wy"); f.Server.Favorite.Add(1); f.Server.Favorite.Add(1);
        f.Server.WyConflictingDuplicate = true; await f.Enable(); var before = await f.Owner.ReadLibrarySyncAsync();
        Code(await f.Run(), "IDENTITY_MISMATCH"); var after = await f.Owner.ReadLibrarySyncAsync();
        Check(JsonNode.DeepEquals(JsonNode.Parse(before.Library.GetRawText()), JsonNode.Parse(after.Library.GetRawText())) && f.Server.Writes == 0);
    }
}
