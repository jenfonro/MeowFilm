// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using static MeowFilm.Windows.Tests.MusicNativeHttpSeamTests;
namespace MeowFilm.Windows.Tests;
public static class MusicKwMgCatalogTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string platform in new[] { "kw", "mg" })
        {
            string p = platform;
            yield return new("Music " + p + " current complete collection commits every occurrence through the native owner", () => OwnerChain(p, false));
            yield return new("Music " + p + " real queue CAS conflict never commits a partial collection", () => OwnerChain(p, true));
            yield return new("Music " + p + " canonical search reaches the real native script selector and payload", () => SearchToScript(p));
            yield return new("Music " + p + " opaque continuation and short visible page remain consumable", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture(); var continuation = MusicPlatformRuntimeFixture.Json(new { batch = 4, index = 3 });
                f.OnRequest = (call, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { platform = p, items = new object[] { }, more = true, continuation, total = 10001 }));
                var result = await f.Catalog.PlaylistAsync(new(p, "alias", Page: 2, Limit: 99, Continuation: continuation));
                Check(result.GetProperty("more").GetBoolean() && result.GetProperty("items").GetArrayLength() == 0);
                Check(f.Requests.Single().Parameters.GetProperty("continuation").GetRawText() == continuation.GetRawText());
            });
            yield return new("Music " + p + " search preserves every returned recording without page-wide truncation", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture();
                var songs = Enumerable.Range(0, 137).Select(i => MusicSongFixture.Online(p, "recording-" + i)).ToArray();
                f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { items = songs, more = false }));
                var result = await f.Catalog.SearchAsync(new(p, "track", Page: 103, Limit: 30));
                Check(result.GetProperty("items").GetArrayLength() == 137 && f.Requests.Count == 1);
            });
        }
    }
    public static IEnumerable<TestCase> DrainCases()
    {
        foreach (string p in new[] { "kw", "mg" })
        {
            string platform = p;
            yield return new("Music " + platform + " invalidation owns the late canonical read until physical completion", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture(); var entered = Signal(); var release = Signal();
                f.OnRequest = async (_, _) => { entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { items = new object[] { } }); };
                var read = f.Catalog.SearchAsync(new(platform, "x"));
                try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Catalog.Invalidate(platform); Check(!f.Catalog.DrainAsync().IsCompleted); }
                finally { release.TrySetResult(); }
                await Failure("STALE_SESSION", () => read); await f.Catalog.DrainAsync(); Check(f.Requests.Count == 1);
            });
        }
    }
    private static JsonElement Ok(JsonElement result)
    { Check(result.GetProperty("ok").GetBoolean()); return result; }
    private static async Task SearchToScript(string platform)
    {
        await using var runtime = new MusicPlatformRuntimeFixture();
        await using var store = new MusicPlatformRuntimeFixture.MemoryStore();
        await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
        await using var scripts = new MusicScriptOwner(library, runtime);
        const string installation = "5f0c1b9e-2a6d-4e43-8a1f-0b7c2d9e4f31";
        string quality = platform == "kw" ? "128k" : "320k";
        var data = new JsonObject {
            ["types"] = new JsonArray(new JsonObject { ["type"] = quality }),
            ["qualityKnown"] = true
        };
        if (platform == "mg")
        {
            data["copyrightId"] = "copyright";
            data["contentId"] = "content";
            data["audioFormats"] = new JsonArray(new JsonObject {
                ["formatType"] = "HQ", ["asize"] = 1048576, ["aformat"] = "020010", ["contentId"] = "format-content"
            });
        }
        var song = MusicSongFixture.Online(platform, "canonical-resource", native: data);
        runtime.OnRequest = (call, _) => {
            Check(call.Operation == "search");
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new {
                items = platform == "mg" ? new object[] { new { song, rank = 1 } } : new object[] { song }, more = false
            }));
        };
        JsonElement? payload = null;
        runtime.OnOtherCall = (call, _) => {
            Check(call.Method == "music.script.resolve");
            payload = call.Payload.Clone();
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { ok = true, contract = "music.script.v3",
                value = new { installationId = installation, platform, quality, generation = 1,
                    contentHash = new string('a', 64), url = "https://synthetic.invalid/audio.mp3",
                    headers = new { }, expiresAt = (long?)null } }));
        };
        var row = (await runtime.Catalog.SearchAsync(new(platform, "synthetic"))).GetProperty("items")[0];
        var selected = platform == "mg" ? row.GetProperty("song") : row;
        if (platform == "kw")
        {
            var unavailable = await scripts.ExecuteAsync("music.online.resolve", new { installationId = installation,
                platform, song = selected, quality = "320k" });
            Check(!unavailable.GetProperty("ok").GetBoolean() &&
                unavailable.GetProperty("code").GetString() == "RECORDING_QUALITY_UNAVAILABLE" && payload is null);
        }
        var resolved = Ok(await scripts.ExecuteAsync("music.online.resolve", new { installationId = installation,
            platform, song = selected, quality }));
        Check(resolved.GetProperty("quality").GetString() == quality && resolved.GetProperty("installationId").GetString() == installation);
        var sent = payload!.Value;
        var info = sent.GetProperty("musicInfo");
        Check(sent.GetProperty("quality").GetString() == quality && info.GetProperty("source").GetString() == platform &&
            info.GetProperty("songmid").GetString() == "canonical-resource");
        if (platform == "mg")
            Check(info.GetProperty("copyrightId").GetString() == "copyright" && info.GetProperty("contentId").GetString() == "content" &&
                info.GetProperty("audioFormats")[0].GetProperty("asize").GetInt32() == 1048576 &&
                info.GetProperty("audioFormats")[0].GetProperty("contentId").GetString() == "format-content");
        Check(runtime.Requests.Count == 1 && runtime.Calls.Count(call => call.Method == "music.script.resolve") == 1);
    }
    private static async Task OwnerChain(string platform, bool conflict)
    {
        await using var runtime = new MusicPlatformRuntimeFixture();
        await using var store = new MusicPlatformRuntimeFixture.MemoryStore();
        await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
        await using var owner = new MusicNativeCatalogOwner(runtime.Catalog, library);
        var songs = Enumerable.Range(0, 235).Select(i => MusicSongFixture.Online(platform, "recording-" + i / 2, "Track " + i / 2)).ToArray();
        runtime.OnRequest = (call, _) =>
        {
            Check(call.Operation == "playlist.complete");
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { platform, id = "actual-list", name = "Current collection", items = songs, more = false }));
        };
        var stage = Ok(await owner.ExecuteAsync("music.catalog.playlist.prepare", new { platform, id = "requested-list" }));
        Check(stage.GetProperty("count").GetInt32() == 235 && runtime.Requests.Count == 1);
        Check(Ok(await library.ExecuteAsync("music.queue.get")).GetProperty("total").GetInt32() == 0);
        if (conflict) Ok(await library.ExecuteAsync("music.queue.replace", new { songs = new[] { songs[0] }, index = 0 }));
        var commit = await owner.ExecuteAsync("music.catalog.playlist.commit", new { token = stage.GetProperty("token").GetString(), action = "play", index = 101, expectedVersion = 0 });
        if (conflict) Check(!commit.GetProperty("ok").GetBoolean() && commit.GetProperty("code").GetString() == "REVISION_CONFLICT");
        else Check(commit.GetProperty("ok").GetBoolean());
        var queue = Ok(await library.ExecuteAsync("music.queue.get", new { offset = 100, limit = 100 }));
        Check(queue.GetProperty("total").GetInt32() == (conflict ? 1 : 235));
        if (!conflict)
        {
            var items = queue.GetProperty("entries"); Check(items[0].GetProperty("id").GetString() != items[1].GetProperty("id").GetString());
            Check(items[0].GetProperty("song").GetProperty("songId").GetString() == items[1].GetProperty("song").GetProperty("songId").GetString());
            Check(MusicSongFixture.NativeId(queue.GetProperty("current").GetProperty("song")) == "recording-50");
        }
        await using (var cold = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage))
        {
            var reloaded = Ok(await cold.ExecuteAsync("music.queue.get", new { limit = 1 }));
            Check(reloaded.GetProperty("total").GetInt32() == queue.GetProperty("total").GetInt32() &&
                reloaded.GetProperty("current").GetProperty("id").GetString() == queue.GetProperty("current").GetProperty("id").GetString() &&
                reloaded.GetProperty("current").GetProperty("song").GetProperty("songId").GetString() ==
                    queue.GetProperty("current").GetProperty("song").GetProperty("songId").GetString() &&
                MusicSongFixture.NativeId(reloaded.GetProperty("current").GetProperty("song")) ==
                    MusicSongFixture.NativeId(queue.GetProperty("current").GetProperty("song")));
        }
        Check(runtime.Requests.Count == 1);
    }
}
