using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    private static IEnumerable<TestCase> LyricPersistenceCases()
    {
        yield return new("Native music lyric display current schema preserves plain and true words across restart", LyricDisplayRestart);
        yield return new("Native music local lyrics current schema preserves plain and true words across restart", LocalLyricsRestart);
        yield return new("Native music lyric snapshots reject non-current schemas without writes", LyricSnapshotSchemas);
        yield return new("Native music lyric snapshots reject timing fields without writes", LyricSnapshotFields);
    }

    private const string PersistedLyricText =
        "[offset:-1500]\n[00:01]<00:01.000>A<00:02.000>B<00:03.000>\n[00:04]整句👨‍👩‍👧‍👦\n[6000,20](6000,0,0)Z(6000,20,0)T";

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
        await using var f = new Fixture(); var owner = f.Owner();
        var (song, sourceKey) = await SeedLyricSong(owner);
        var lines = CurrentLyricRows();
        lines[0]!["translation"] = "译文";
        lines[0]!["romanization"] = "A B";
        await Ok(owner, "music.lyrics.display.save", new { song, sourceKey, lines });
        var saved = f.Store.State("native-lyric-display");
        Check(saved["schema"]!.GetValue<int>() == 2 && JsonNode.DeepEquals(saved["lines"], lines));
        byte[] bytes = f.Store.Bytes!.ToArray(); int writes = f.Store.Writes;
        await owner.DisposeAsync();

        var reopened = f.Owner();
        var restored = await Ok(reopened, "music.lyrics.display.get", new { song, sourceKey });
        Check(restored.GetProperty("state").GetString() == "restored" && restored.GetProperty("sourceKey").GetString() == sourceKey);
        Check(JsonNode.DeepEquals(JsonNode.Parse(restored.GetProperty("lines").GetRawText()), lines));
        await Ok(reopened, "music.lyrics.display.save", new { song, sourceKey, lines });
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
    }

    private static async Task LocalLyricsRestart()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var (song, _) = await SeedLyricSong(owner);
        await Ok(owner, "music.lyrics.local.save", new { song, text = PersistedLyricText });
        var saved = f.Store.State("native-local-lyrics");
        var lines = CurrentLyricRows();
        Check(saved["schema"]!.GetValue<int>() == 2 && JsonNode.DeepEquals(saved["items"]![0]!["lines"], lines));
        byte[] bytes = f.Store.Bytes!.ToArray(); int writes = f.Store.Writes;
        await owner.DisposeAsync();

        var reopened = f.Owner();
        var restored = await Ok(reopened, "music.lyrics.local.get", new { song });
        Check(restored.GetProperty("state").GetString() == "local" && restored.GetProperty("available").GetBoolean());
        Check(JsonNode.DeepEquals(JsonNode.Parse(restored.GetProperty("lines").GetRawText()), lines));
        await Ok(reopened, "music.lyrics.local.save", new { song, text = PersistedLyricText });
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
    }

    private static JsonObject LyricSnapshot(string area, int schema, JsonArray lines) =>
        area == "native-lyric-display" ?
            O(new { schema, songKey = new string('a', 64), songId = "library:local:a", sourceKey = "synthetic-source", lines }) :
            O(new { schema, items = new[] {
                new { songKey = new string('a', 64), songId = "library:local:a", contentHash = new string('b', 64), lines }
            } });

    private static async Task LyricSnapshotSchemas()
    {
        foreach (string legacy in new[] {
            """
            {
              "version": 1,
              "entries": [{
                "key": "[\"meowfilm\",\"music\",1,\"local\",\"native-lyric-display\"]",
                "value": {
                  "schema": 1,
                  "songKey": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "songId": "library:local:a",
                  "sourceKey": "synthetic-source",
                  "lines": [{
                    "time": 1, "end": 6, "text": "AB", "timing": "estimated",
                    "words": [
                      { "time": 1, "duration": 2.5, "text": "A", "offset": 0 },
                      { "time": 3.5, "duration": 2.5, "text": "B", "offset": 2.5 }
                    ]
                  }]
                }
              }]
            }
            """,
            """
            {
              "version": 1,
              "entries": [{
                "key": "[\"meowfilm\",\"music\",1,\"local\",\"native-local-lyrics\"]",
                "value": {
                  "schema": 1,
                  "items": [{
                    "songKey": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "songId": "library:local:a",
                    "contentHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "lines": [{
                      "time": 1, "end": 6, "text": "AB", "timing": "estimated",
                      "words": [
                        { "time": 1, "duration": 2.5, "text": "A", "offset": 0 },
                        { "time": 3.5, "duration": 2.5, "text": "B", "offset": 2.5 }
                      ]
                    }]
                  }]
                }
              }]
            }
            """
        })
            await RejectDocument(Encoding.UTF8.GetBytes(legacy));
        foreach (string area in new[] { "native-lyric-display", "native-local-lyrics" })
            await RejectDocument(Document((area, LyricSnapshot(area, 3, CurrentLyricRows()))));
    }

    private static async Task LyricSnapshotFields()
    {
        var lines = CurrentLyricRows();
        lines[0]!["timing"] = "word";
        foreach (string area in new[] { "native-lyric-display", "native-local-lyrics" })
            await RejectDocument(Document((area, LyricSnapshot(area, 2, lines))));

        await using var f = new Fixture(); var owner = f.Owner();
        var (song, sourceKey) = await SeedLyricSong(owner);
        byte[] bytes = f.Store.Bytes!.ToArray(); int writes = f.Store.Writes;
        await Error(owner, "music.lyrics.display.save", new { song, sourceKey, lines }, "INVALID_REQUEST");
        Check(f.Store.Writes == writes && bytes.SequenceEqual(f.Store.Bytes!));
    }
}
