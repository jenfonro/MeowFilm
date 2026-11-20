using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    private static IEnumerable<TestCase> LyricPersistenceCases()
    {
        yield return new("Native music selected local lyrics preserve damaged siblings and allow overwrite and removal", LocalLyricsIsolation);
        yield return new("Native music lyric display independent cache preserves plain and true words across restart", () => Task.Run(LyricDisplayRestart));
        yield return new("Native music local lyrics current schema preserves plain and true words across restart", LocalLyricsRestart);
        yield return new("Native music local lyrics beyond sixty four songs and two MiB reopen without truncation", LargeLocalLyrics);
        yield return new("Native music current local lyric documents reject damaged schema fields without writes", LyricSnapshotSchemas);
        yield return new("Native music lyric snapshots ignore unused timing fields without rewriting source data", LyricSnapshotFields);
    }

    private static async Task LocalLyricsIsolation()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var (song, _) = await SeedLyricSong(owner);
        await Ok(owner, "music.queue.append", new { songs = new[] { Online("lyric-brother") } });
        var queue = await Ok(owner, "music.queue.get");
        string brother = queue.GetProperty("entries")[1].GetProperty("song").GetProperty("songId").GetString()!;
        await Ok(owner, "music.lyrics.local.save", new { song, text = "[00:01]first" });
        await f.Store.Storage.WriteAsync(db =>
        {
            var record = MusicStorageRecords.Areas["native-local-lyrics"].Relation("items");
            record.Replace(db, LJ.Obj(("songId", brother), ("contentHash", "bad")), ["local", "broken"], 1);
            return true;
        });
        Check((await Ok(owner, "music.lyrics.local.get", new { song })).GetProperty("state").GetString() == "local");
        await f.Store.Storage.WriteAsync(db => db.Execute("DELETE FROM music_windows_local_lyrics_items_lines WHERE scope_key='local' AND items_key<>'broken'"));
        await Error(owner, "music.lyrics.local.get", new { song }, "CORRUPT_STORAGE");
        await Ok(owner, "music.lyrics.local.save", new { song, text = "[00:01]first" });
        Check((await Ok(owner, "music.lyrics.local.get", new { song })).GetProperty("lines")[0].GetProperty("text").GetString() == "first");
        await f.Store.Storage.WriteAsync(db => db.Execute("DELETE FROM music_windows_local_lyrics_items_lines WHERE scope_key='local' AND items_key<>'broken'"));
        await Ok(owner, "music.lyrics.local.remove", new { song });
        Check(await f.Store.Storage.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM music_windows_local_lyrics_items WHERE items_key='broken'")) == 1);
    }

    private const string PersistedLyricText =
        "[offset:-1500]\n[00:01]<00:01.000>A<00:02.000>B<00:03.000>\n[00:04]整句👨‍👩‍👧‍👦\n[6000,20](6000,0,0)Z(6000,20,0)T\n[00:08]<00:08.000>末词";

    private static JsonArray CurrentLyricRows() => new(
        O(new {
            time = 0d, end = 1.5, text = "AB",
            words = new[] {
                new { time = -.5, duration = 1d, text = "A", offset = -.5 },
                new { time = .5, duration = 1d, text = "B", offset = .5 }
            }
        }),
        O(new { time = 2.5, end = 4.5, text = "整句👨‍👩‍👧‍👦", words = Array.Empty<object>() }),
        O(new {
            time = 4.5, end = 4.52, text = "ZT",
            words = new[] {
                new { time = 4.5, duration = 0d, text = "Z", offset = 0d },
                new { time = 4.5, duration = .02, text = "T", offset = 0d }
            }
        }),
        O(new {
            time = 6.5, end = 180d, text = "末词",
            words = new[] { new { time = 6.5, duration = 0d, text = "末词", offset = 0d } }
        }));

    private static async Task<(JsonObject Song, string SourceKey)> SeedLyricSong(MusicLibraryOwner owner)
    {
        await Ok(owner, "music.queue.replace", new { songs = new[] { Online("42") }, index = 0 });
        var queue = await Ok(owner, "music.queue.get");
        var song = Parsed(queue.GetProperty("current").GetProperty("song"));
        return (song, song["sources"]![0]!["sourceKey"]!.GetValue<string>());
    }

    private static async Task LyricDisplayRestart()
    {
        await using var f = new Fixture();
        JsonObject song;
        string sourceKey;
        var lines = CurrentLyricRows();
        lines[0]!["translation"] = "译文";
        lines[0]!["romanization"] = "A B";
        byte[] bytes;
        int writes;
        await using (var owner = new MusicLibraryOwner(f.Store, database: f.Store.Storage))
        {
            var seeded = await SeedLyricSong(owner); song = seeded.Song; sourceKey = seeded.SourceKey;
            await Ok(owner, "music.lyrics.display.save", new { song, sourceKey, lines });
            await owner.PresentationCache.DrainAsync();
            bytes = f.Store.Bytes!.ToArray(); writes = f.Store.Writes;
        }
        Check(await f.Store.Storage.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_lyrics")) == 1);

        await using var reopened = new MusicLibraryOwner(f.Store, database: f.Store.Storage);
        var restored = await Ok(reopened, "music.lyrics.display.get", new { song, sourceKey });
        Check(restored.GetProperty("state").GetString() == "restored" && restored.GetProperty("sourceKey").GetString() == sourceKey);
        Check(JsonNode.DeepEquals(JsonNode.Parse(restored.GetProperty("lines").GetRawText()), lines));
        await Ok(reopened, "music.lyrics.display.save", new { song, sourceKey, lines });
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
    }

    private static async Task LargeLocalLyrics()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        JsonObject song = null!;
        string text = new('x', 2 * 1024 * 1024 + 1);
        for (int i = 0; i < 65; i++)
        {
            await Ok(owner, "music.queue.replace", new { songs = new[] { Online((1000 + i).ToString()) }, index = 0 });
            song = Parsed((await Ok(owner, "music.queue.get")).GetProperty("current").GetProperty("song"));
            await Ok(owner, "music.lyrics.local.save", new { song, text = "[00:01]" + (i == 64 ? text : "line") });
        }
        Check(f.Store.State("native-local-lyrics")["items"]!.AsArray().Count == 65);
        await owner.DisposeAsync();
        var restored = await Ok(f.Owner(), "music.lyrics.local.get", new { song });
        Check(restored.GetProperty("state").GetString() == "local" &&
            restored.GetProperty("lines")[0].GetProperty("text").GetString() == text);
    }

    private static async Task LocalLyricsRestart()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var (song, _) = await SeedLyricSong(owner);
        await Ok(owner, "music.lyrics.local.save", new { song, text = PersistedLyricText });
        var saved = f.Store.State("native-local-lyrics");
        var lines = CurrentLyricRows();
        Check(saved["schema"]!.GetValue<long>() == 1 && JsonNode.DeepEquals(saved["items"]![0]!["lines"], lines));
        byte[] bytes = f.Store.Bytes!.ToArray(); int writes = f.Store.Writes;
        await owner.DisposeAsync();

        var reopened = f.Owner();
        var restored = await Ok(reopened, "music.lyrics.local.get", new { song });
        Check(restored.GetProperty("state").GetString() == "local" && restored.GetProperty("available").GetBoolean());
        Check(JsonNode.DeepEquals(JsonNode.Parse(restored.GetProperty("lines").GetRawText()), lines));
        await Ok(reopened, "music.lyrics.local.save", new { song, text = PersistedLyricText });
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
    }

    private static async Task<JsonObject> CurrentLocalLyricState()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var (song, _) = await SeedLyricSong(owner);
        await Ok(owner, "music.lyrics.local.save", new { song, text = PersistedLyricText });
        return f.Store.State("native-local-lyrics");
    }

    private static async Task LyricSnapshotSchemas()
    {
        var current = await CurrentLocalLyricState();
        foreach (bool missing in new[] { false, true })
        {
            var damaged = current.DeepClone().AsObject();
            if (missing) damaged.Remove("schema"); else damaged["schema"] = "invalid";
            var song = Online("42"); song["songId"] = current["items"]![0]!["songId"]!.DeepClone();
            await RejectDocument(Document(("native-local-lyrics", damaged)), method: "music.lyrics.local.get", parameters: new { song });
        }
    }

    private static async Task LyricSnapshotFields()
    {
        var lines = CurrentLyricRows();
        lines[0]!["timing"] = "word";
        var current = await CurrentLocalLyricState();
        current["items"]![0]!["lines"] = lines.DeepClone();
        await using (var stored = new Fixture())
        {
            stored.Store.Bytes = Document(("native-local-lyrics", current));
            byte[] original = stored.Store.Bytes.ToArray();
            var reference = Online("42"); reference["songId"] = current["items"]![0]!["songId"]!.DeepClone();
            var local = await Ok(stored.Owner(), "music.lyrics.local.get", new { song = reference });
            Check(local.GetProperty("lines")[0].GetProperty("timing").GetString() == "word");
            Check(stored.Store.Writes == 0 && original.SequenceEqual(stored.Store.Bytes));
        }

        await using var f = new Fixture(); var owner = f.Owner();
        var (song, sourceKey) = await SeedLyricSong(owner);
        byte[] bytes = f.Store.Bytes!.ToArray(); int writes = f.Store.Writes;
        await Ok(owner, "music.lyrics.display.save", new { song, sourceKey, lines });
        Check(!(await Ok(owner, "music.lyrics.display.get", new { song })).GetProperty("lines")[0].TryGetProperty("timing", out _));
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
    }
    private sealed class LyricCacheStore : NativeTestDatabase {
    }

}
