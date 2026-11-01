using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicArtworkPolicyTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native music artwork saved provider priority is independent of primary playback platform", () =>
        {
            var song = Song("wy", "42", "https://wy.example.test/a");
            song["sources"]!.AsArray().Add(Row("tx", "other", "https://tx.example.test/a"));
            var current = MusicSongFixture.Element(song);
            Equal("https://tx.example.test/a", Pick(current, ["tx", "wy"], ["tx", "wy"]));
            Equal("https://wy.example.test/a", Pick(current, ["tx", "wy"], ["wy"]));
            Equal("", Pick(current, ["wy", "tx"], [])); return Task.CompletedTask;
        });
        yield return new("Native music artwork rejects a noncurrent root and reads recording metadata", () =>
        {
            var invalid = Song("wy", "42");
            invalid["sources"]![0]!["musicInfo"]!["img"] = "https://unrelated.example.test/a?token=x";
            Equal("", Pick(MusicSongFixture.Element(invalid), ["wy"], ["wy"]));
            var song = Song("wy", "42", "https://native.example.test/a");
            song["cover_url"] = "https://display.example.test/a";
            Equal("https://native.example.test/a", Pick(MusicSongFixture.Element(song), ["wy"], ["wy"]));
            return Task.CompletedTask;
        });
        yield return new("Native music artwork ignores credentialed URLs and limits each provider to two candidates", () =>
        {
            foreach (string uri in new[] { "https://u:secret@example.test/a", "https://example.test/a?token=private",
                "file:///C:/private.png", "https://example.test/a#x" })
            {
                var song = Song("wy", "42");
                song["sources"]![0]!["musicInfo"]!["img"] = uri;
                Equal("", Pick(MusicSongFixture.Element(song), ["wy"], ["wy"]));
            }
            var many = Song("wy", "1");
            many["sources"]!.AsArray().Add(Row("wy", "2", ""));
            many["sources"]!.AsArray().Add(Row("wy", "3", "https://third.example.test/a"));
            many["sources"]!.AsArray().Add(Row("tx", "other", "https://tx.example.test/a"));
            Equal("https://tx.example.test/a", Pick(MusicSongFixture.Element(many), ["wy", "tx"], ["wy", "tx"]));
            return Task.CompletedTask;
        });
    }
    private static JsonObject Song(string platform, string id, string cover = "") =>
        MusicSongFixture.Online(platform, id, "Artwork fixture", "Synthetic artist", "Synthetic album", 180, cover);
    private static JsonObject Row(string platform, string id, string cover) =>
        Song(platform, id, cover)["sources"]![0]!.DeepClone().AsObject();
    private static string Pick(JsonElement song, string[] order, string[] enabled) =>
        MusicArtworkPolicy.Select(song, order, enabled.ToHashSet(StringComparer.Ordinal));
    private static void Equal(string expected, string actual)
    { if (expected != actual) throw new InvalidOperationException($"Artwork policy expected {expected}, got {actual}"); }
}
