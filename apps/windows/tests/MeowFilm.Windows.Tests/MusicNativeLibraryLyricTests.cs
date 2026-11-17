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
        yield return new("Native music lyric display independent cache preserves plain and true words across restart", LyricDisplayRestart);
        yield return new("Native music local lyrics current schema preserves plain and true words across restart", LocalLyricsRestart);
        yield return new("Native music current local lyric documents reject damaged schema fields without writes", LyricSnapshotSchemas);
        yield return new("Native music lyric snapshots ignore unused timing fields without rewriting source data", LyricSnapshotFields);
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
        string directory = TestWorkspace.DataPath("native-lyric-display-" + Guid.NewGuid().ToString("N"), MusicPresentationCache.DirectoryName);
        Directory.CreateDirectory(directory);
        JsonObject song;
        string sourceKey;
        var lines = CurrentLyricRows();
        lines[0]!["translation"] = "译文";
        lines[0]!["romanization"] = "A B";
        byte[] bytes;
        int writes;
        await using (var owner = new MusicLibraryOwner(f.Store, presentationCacheRoot: directory))
        {
            var seeded = await SeedLyricSong(owner); song = seeded.Song; sourceKey = seeded.SourceKey;
            await Ok(owner, "music.lyrics.display.save", new { song, sourceKey, lines });
            await owner.PresentationCache.DrainAsync();
            bytes = f.Store.Bytes!.ToArray(); writes = f.Store.Writes;
        }
        Check(Directory.EnumerateFiles(directory, "*.resource").Count() == 1);

        await using var reopened = new MusicLibraryOwner(f.Store, presentationCacheRoot: directory);
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
        Check(saved["schema"]!.GetValue<int>() == 1 && JsonNode.DeepEquals(saved["items"]![0]!["lines"], lines));
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
    private sealed class LyricCacheStore : IDocumentStore
    {
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unexpected document cache read in native library fixture: " + ns);
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unexpected document cache write in native library fixture: " + ns);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
