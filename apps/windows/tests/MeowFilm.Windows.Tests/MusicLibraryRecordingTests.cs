using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    private static IEnumerable<TestCase> RecordingConsumerCases()
    {
        yield return new("Native music library subgroup membership and incoming metadata share recording identity", LibrarySubgroups);
        yield return new("Native music library separates non-transitive merged snapshots", LibraryRecordingPairs);
        yield return new("Native music library rejects conflicting secondary native binding without writing", SecondaryNativeConflict);
        yield return new("Native music history preserves newest context and all conflicting recording groups", HistoryRecordingGroups);
    }
    private static JsonObject RecordingSong(string platform, string id, double duration) =>
        MusicSongFixture.Online(platform, id, "Recording", "Artist", duration: duration);
    private static JsonObject RecordingSet(JsonObject primary, params JsonObject[] other)
    {
        var result = primary.DeepClone().AsObject();
        foreach (var song in other) result["sources"]!.AsArray().Add(song["sources"]![0]!.DeepClone());
        return NativeMusicSong.Normalize(result);
    }
    private static async Task LibrarySubgroups()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 0,
            songs = new[] { RecordingSong("wy", "short", 100), RecordingSong("wy", "long", 200) } });
        var before = (await Ok(owner, "music.library.detail", new { id = "favorites" })).GetProperty("songs");
        string longId = before.EnumerateArray().Single(song => song.GetProperty("duration").GetDouble() == 200)
            .GetProperty("songId").GetString()!;
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 1, songs = new[] { RecordingSong("tx", "long", 200) } });
        var membership = await Ok(owner, "music.library.membership", new { id = "favorites", song = RecordingSong("kw", "other", 200) });
        Check(membership.GetProperty("present").GetBoolean() && membership.GetProperty("songId").GetString() == longId);
        var updated = RecordingSong("tx", "long", 201);
        updated["album"] = "Corrected album"; MusicSongFixture.Info(updated)["albumName"] = "Corrected album";
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 2, songs = new[] { updated } });
        var rows = (await Ok(owner, "music.library.detail", new { id = "favorites" })).GetProperty("songs");
        Check(rows.GetArrayLength() == 2);
        var longer = rows.EnumerateArray().Single(song => song.GetProperty("songId").GetString() == longId);
        Check(longer.GetProperty("album").GetString() == "Corrected album" && longer.GetProperty("sources").GetArrayLength() == 2);
        Check(MusicSongFixture.Info(longer).GetProperty("source").GetString() == "tx" &&
            MusicSongFixture.Info(longer).GetProperty("duration").GetDouble() == 201);
        foreach (var row in rows.EnumerateArray()) _ = SongRow.Read(row);
        await owner.DisposeAsync();
        Check((await Ok(f.Owner(), "music.library.detail", new { id = "favorites" })).GetProperty("songs").GetArrayLength() == 2);
    }
    private static async Task LibraryRecordingPairs()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var first = RecordingSet(RecordingSong("wy", "first", 103), RecordingSong("tx", "short", 100));
        var later = RecordingSet(RecordingSong("wy", "later", 103), RecordingSong("tx", "long", 106));
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 0, songs = new[] { first } });
        var membership = await Ok(owner, "music.library.membership", new { id = "favorites", song = later });
        Check(!membership.GetProperty("present").GetBoolean());
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 1, songs = new[] { later } });
        var rows = (await Ok(owner, "music.library.detail", new { id = "favorites" })).GetProperty("songs");
        Check(rows.GetArrayLength() == 2);
        foreach (var row in rows.EnumerateArray()) _ = SongRow.Read(row);
    }
    private static async Task SecondaryNativeConflict()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 0,
            songs = new[] { RecordingSong("tx", "bound", 100) } });
        var conflicting = RecordingSet(RecordingSong("wy", "new", 106), RecordingSong("tx", "bound", 106));
        int writes = f.Store.Writes;
        await Error(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 1, songs = new[] { conflicting } }, "NATIVE_ID_CONFLICT");
        Check(f.Store.Writes == writes && (await Ok(owner, "music.library.detail", new { id = "favorites" }))
            .GetProperty("songs").GetArrayLength() == 1);
    }
    private static async Task HistoryRecordingGroups()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var common = RecordingSong("wy", "common", 103);
        JsonObject[] snapshots =
        [
            RecordingSet(common, RecordingSong("tx", "short", 100)),
            RecordingSet(common, RecordingSong("tx", "long", 106)),
            RecordingSet(common, RecordingSong("kw", "middle-low", 100.5), RecordingSong("mg", "middle-high", 103.5))
        ];
        long start = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12)).ToUnixTimeMilliseconds();
        async Task Record(JsonObject song, string context, long at)
        {
            await Ok(owner, "music.queue.replace", new { songs = new[] { song }, index = 0, context = new { sourceTitle = context } });
            string entryId = (await Ok(owner, "music.queue.get")).GetProperty("current").GetProperty("id").GetString()!;
            await Ok(owner, "music.history.recordPlayed", new { entryId, at });
        }
        for (int i = 0; i < snapshots.Length; i++) await Record(snapshots[i], "context-" + i, start + i);
        await Record(snapshots[2], "older duplicate", start - 1);
        var days = await Ok(owner, "music.history.get", new { kind = "days" });
        string day = days.GetProperty("items")[0].GetProperty("id").GetString()!;
        var history = (await Ok(owner, "music.history.detail", new { kind = "days", id = day })).GetProperty("items");
        Check(history.GetArrayLength() == 3 && history.EnumerateArray().Select(row => row.GetProperty("at").GetInt64())
            .SequenceEqual(new[] { start + 2, start + 1, start }));
        Check(history[0].GetProperty("context").GetProperty("sourceTitle").GetString() == "context-2");
        var songs = history.EnumerateArray().Select(row => row.GetProperty("song")).ToArray();
        Check(songs.Select(song => song.GetProperty("songId").GetString()).Distinct().Count() == 3);
        foreach (var song in songs) _ = SongRow.Read(song);
        await owner.DisposeAsync();
        Check((await Ok(f.Owner(), "music.history.detail", new { kind = "days", id = day })).GetProperty("items").GetArrayLength() == 3);
    }
}
