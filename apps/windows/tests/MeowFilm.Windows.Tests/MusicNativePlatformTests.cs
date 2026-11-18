// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

// Node owns provider recipes; these cases exercise the real native business adapter.
public static partial class MusicNativePlatformTests
{
    private const string Huge = "9007199254740993123";
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in DailyStyleCases()) yield return test;
        foreach (var test in ChartCases()) yield return test;
        foreach (var test in ChannelDirectoryCases()) yield return test;
        foreach (var test in HomeHeartbeatCases()) yield return test;
        yield return Case("manifest is passive and describes the shared Node contract", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            var d = f.Catalog.Describe();
            Equal("node", d.GetProperty("execution").GetString());
            Equal("music.platform.v1", d.GetProperty("contract").GetString());
            foreach (var platform in new[] { "wy", "tx", "kg", "kw", "mg" })
                True(d.GetProperty("platforms").GetProperty(platform).GetProperty("song").GetBoolean());
            True(!d.GetProperty("platforms").GetProperty("kw").GetProperty("accounts").GetBoolean());
            True(!d.GetProperty("platforms").GetProperty("tx").GetProperty("home").GetBoolean());
            Equal(0, f.Calls.Count);
        });
        foreach (var platform in new[] { "wy", "tx", "kg", "kw", "mg" })
        {
            string p = platform;
            yield return Case(p + " canonical aliases, optional artwork and long IDs are consumed unchanged", async () =>
            {
                await using var f = Peer(call =>
                {
                    Equal("song", call.Operation); Equal(Huge, call.Parameters.GetProperty("id").GetString());
                    return Json(MusicSongFixture.Online(p, "canonical/alias", cover: ""));
                });
                var value = await f.Catalog.SongAsync(new(p, Huge));
                Equal("canonical/alias", MusicSongFixture.NativeId(value));
                Equal(1, f.Requests.Count);
            });
            yield return Case(p + " complete collection retains every occurrence without a verification reread", async () =>
            {
                var rows = Enumerable.Range(0, 205).Select(i => MusicSongFixture.Online(p, (i % 2).ToString())).ToArray();
                await using var f = Peer(call =>
                {
                    Equal("playlist.complete", call.Operation);
                    return Json(new { id = "actual-list", items = rows, more = false, total = 999 });
                });
                var result = await f.Catalog.CompletePlaylistAsync(new(p, "requested-list"));
                Equal(205, result.GetProperty("items").GetArrayLength());
                Equal("0", MusicSongFixture.NativeId(result.GetProperty("items")[204]));
                Equal("actual-list", result.GetProperty("id").GetString()); Equal(1, f.Requests.Count);
            });
        }
        foreach (string code in new[] { "NETWORK_ERROR", "UPSTREAM_ERROR", "INVALID_RESPONSE", "LIMIT", "AUTH_REQUIRED", "FORBIDDEN", "TIMEOUT" })
        {
            string c = code;
            yield return Case("canonical failure retains " + c + " without retries or content reclassification", async () =>
            {
                await using var f = Peer(_ => throw new MusicPlatformException(c));
                await Error(c, () => f.Catalog.SongAsync(new("wy", "1"))); Equal(1, f.Requests.Count);
            });
        }
        yield return Case("session credentials cannot stringify or serialize", () =>
        {
            var session = Session("wy"); Equal("{}", JsonSerializer.Serialize(session));
            Equal("MusicPlatformSession (redacted)", session.ToString()); return Task.CompletedTask;
        });
        yield return Case("extra ordinary parameters reach the shared operation without a native schema gate", async () =>
        {
            await using var f = Peer(call => Json(new { id = call.Parameters.GetProperty("id").GetString() }));
            foreach (object input in new object[] { new { id = "a/b" }, new { id = "a/b", extra = 1 },
                new { id = "a/b", extra = new { url = "https://unused.invalid", headers = new { cookie = "unused" } } } })
                Equal("a/b", (await f.Client.ReadAsync("wy", "song", input, default)).GetProperty("id").GetString());
            Equal(3, f.Requests.Count);
        });
        yield return Case("catalog maps all existing public operations to their canonical names", async () =>
        {
            await using var f = Peer(call => Json(new { operation = call.Operation }));
            var reads = new (string Operation, Func<Task<JsonElement>> Read)[] {
                ("search", () => f.Catalog.SearchAsync(new("wy", "关键词"))),
                ("playlist", () => f.Catalog.PlaylistAsync(new("wy", "list"))),
                ("playlists", () => f.Catalog.PlaylistsAsync(new("wy", ["list-a", "list-b"]))),
                ("album", () => f.Catalog.AlbumAsync(new("wy", "album"))),
                ("artist", () => f.Catalog.ArtistAsync(new("wy", "artist"))),
                ("daily", () => f.Catalog.DailyAsync(new())),
                ("fm", () => f.Catalog.FmAsync(new())),
                ("similar", () => f.Catalog.SimilarAsync(new("wy", ["seed"]))),
                ("queueRecommendations", () => f.Catalog.QueueRecommendationsAsync(new("wy", "seed", 40))),
                ("personalPlaylists", () => f.Catalog.PersonalPlaylistsAsync(new("wy"))),
                ("chorus", () => f.Catalog.ChorusAsync("wy", "seed"))
            };
            foreach (var read in reads) Equal(read.Operation, (await read.Read()).GetProperty("operation").GetString());
            Equal(reads.Length, f.Requests.Count);
        });
    }
    private static MusicPlatformRuntimeFixture Peer(Func<MusicPlatformRuntimeFixture.Call, JsonElement> respond, MusicPlatformSession? session = null)
    {
        var f = new MusicPlatformRuntimeFixture(); f.Sessions.Current = session;
        f.OnRequest = (call, _) => Task.FromResult(respond(call)); return f;
    }
    private static MusicPlatformSession Session(string platform) => new(platform, "synthetic-scope", 1, Huge, "SYNTHETIC_COOKIE=value");
    private static JsonElement Json(object value) => MusicPlatformRuntimeFixture.Json(value);
    private static TestCase Case(string name, Func<Task> run) => new("MusicNativePlatformTests." + name, run);
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Platform assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (MusicPlatformException error) { Equal(code, error.Code); True(error.InnerException is null); return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
