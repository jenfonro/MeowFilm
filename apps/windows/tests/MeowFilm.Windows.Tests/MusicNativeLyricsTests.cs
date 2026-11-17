// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Windows.Tests.MusicNativeHttpSeamTests;
namespace MeowFilm.Windows.Tests;
public static class MusicNativeLyricsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
        {
            string p = platform;
            yield return Case(p + " canonical timed text is consumed without a second decoder or identity gate", async () => {
                await using var f = new MusicPlatformRuntimeFixture();
                var service = new NativeMusicLyrics(f.Client);
                f.OnRequest = (call, _) => {
                    Check(call.Operation == "lyrics" && call.Parameters.GetProperty("id").GetString() == "canonical/alias");
                    Check(call.Parameters.GetProperty("musicInfo").GetProperty("albumAudioId").GetString() == "9007199254740997");
                    return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { id = "different", platform = "unused", text = "[00:01.000]原\n[00:02.000]文", translation = "[00:01.000]译", romanization = "[00:01.000]yuan" }));
                };
                var payload = await service.ReadAsync(new(p, "canonical/alias", 180, "9007199254740997"), default);
                Check(payload is not null && payload.Text == "[00:01.000]原\n[00:02.000]文" && payload.Translation == "[00:01.000]译" && payload.Romanization == "[00:01.000]yuan");
                var rendered = MusicLyricTiming.Parse(payload!.Text, payload.Translation, 180);
                Check(rendered.GetArrayLength() == 2 && f.Requests.Count == 1);
            });
            yield return Case(p + " canonical absence remains null and does not create a cache", async () => {
                await using var f = new MusicPlatformRuntimeFixture(); var service = new NativeMusicLyrics(f.Client);
                f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { text = "", translation = "", romanization = "" }));
                Check(await service.ReadAsync(new(p, "id"), default) is null);
                Check(await service.ReadAsync(new(p, "id"), default) is null && f.Requests.Count == 2);
            });
        }
        foreach (string code in new[] { "INVALID_RESPONSE", "AUTH_REQUIRED", "FORBIDDEN", "VERIFICATION_REQUIRED", "RATE_LIMITED", "NETWORK_ERROR", "LIMIT", "TIMEOUT" })
        {
            string c = code;
            yield return Case("canonical " + c + " never becomes confirmed absence or retry", async () => {
                await using var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => throw new MusicPlatformException(c);
                await Failure(c, () => new NativeMusicLyrics(f.Client).ReadAsync(new("wy", "id"), default)); Check(f.Requests.Count == 1);
            });
        }
        yield return Case("main timed lyrics remain consumable without optional translation or romanization", async () => {
            await using var f = new MusicPlatformRuntimeFixture(); var service = new NativeMusicLyrics(f.Client);
            f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { text = "[00:01.000]原文", translation = "", romanization = "" }));
            var payload = await service.ReadAsync(new("wy", "id"), default);
            Check(payload is not null && payload.Text == "[00:01.000]原文" && payload.Translation.Length == 0 && payload.Romanization.Length == 0);
            Check(MusicLyricTiming.Parse(payload!.Text, payload.Translation, 180).GetArrayLength() == 1 && f.Requests.Count == 1);
        });
        yield return Case("local lyric settings retire their own work without revoking unrelated platform reads", async () => {
            await using var store = new MusicPlatformRuntimeFixture.MemoryStore();
            await using var runtime = new MusicPlatformRuntimeFixture();
            await using var app = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            var catalogEntered = Signal(); var lyricEntered = Signal(); var release = Signal();
            runtime.OnRequest = async (call, token) => {
                if (call.Operation == "search") catalogEntered.TrySetResult();
                else { Check(call.Operation == "lyrics"); lyricEntered.TrySetResult(); }
                await release.Task;
                token.ThrowIfCancellationRequested();
                return call.Operation == "search"
                    ? MusicPlatformRuntimeFixture.Json(new { items = new[] { MusicSongFixture.Online("wy", "42") }, more = false })
                    : MusicPlatformRuntimeFixture.Json(new { text = "[00:01]current", translation = "", romanization = "" });
            };
            var song = MusicSongFixture.Online("wy", "42");
            var catalog = app.ExecuteAsync("music.catalog.search", new { platform = "wy", keyword = "Synthetic", limit = 1 });
            var lyric = app.ExecuteAsync("music.lyrics.lookup", new { song,
                sourceKey = song["sources"]![0]!["sourceKey"]!.GetValue<string>() });
            int publications;
            try {
                await Task.WhenAll(catalogEntered.Task, lyricEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
                publications = runtime.Calls.Count(call => call.Method == "music.platform.session.update" &&
                    call.Payload.GetProperty("session").ValueKind == JsonValueKind.Null);
                var saved = await app.ExecuteAsync("music.metadata.settings.save", new {
                    ifRevision = 0, platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = new[] { "wy" } });
                Check(saved.GetProperty("ok").GetBoolean() && !catalog.IsCompleted);
            } finally { release.TrySetResult(); }
            var read = await catalog;
            Check(read.GetProperty("ok").GetBoolean() && read.GetProperty("items").GetArrayLength() == 1);
            var retired = await lyric;
            Check(!retired.GetProperty("ok").GetBoolean() && retired.GetProperty("code").GetString() == "CANCELLED");
            var fresh = await app.ExecuteAsync("music.lyrics.lookup", new { song,
                sourceKey = song["sources"]![0]!["sourceKey"]!.GetValue<string>() });
            Check(fresh.GetProperty("ok").GetBoolean() && fresh.GetProperty("lines")[0].GetProperty("text").GetString() == "current");
            Check(runtime.Calls.Count(call => call.Method == "music.platform.session.update" &&
                call.Payload.GetProperty("session").ValueKind == JsonValueKind.Null) == publications);
        });
        yield return Case("caller cancellation owns held physical runtime work without blocking an independent lyric read", async () => {
            await using var f = new MusicPlatformRuntimeFixture(); var service = new NativeMusicLyrics(f.Client);
            var entered = Signal(); var release = Signal();
            f.OnRequest = async (call, token) => {
                if (call.Parameters.GetProperty("id").GetString() == "held") { entered.TrySetResult(); await release.Task; }
                token.ThrowIfCancellationRequested();
                return MusicPlatformRuntimeFixture.Json(new { text = "[00:01]词", translation = "", romanization = "" });
            };
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Cancelled(() => service.ReadAsync(new("wy", "before"), cancel.Token)); Check(f.Requests.Count == 0);
            using var during = new CancellationTokenSource(); var held = service.ReadAsync(new("wy", "held"), during.Token);
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); during.Cancel();
                Check(!held.IsCompleted && !f.Client.DrainAsync().IsCompleted);
                Check(await service.ReadAsync(new("wy", "other"), default) is not null);
            } finally { release.TrySetResult(); }
            await Cancelled(() => held); await f.Client.DrainAsync();
        });
        yield return Case("shared platform revocation rejects late lyrics but does not cancel another platform", async () => {
            await using var f = new MusicPlatformRuntimeFixture(); var service = new NativeMusicLyrics(f.Client);
            var entered = Signal(); var release = Signal();
            f.OnRequest = async (call, _) => { if (call.Platform == "wy") { entered.TrySetResult(); await release.Task; } return MusicPlatformRuntimeFixture.Json(new { text = "[00:01]词", translation = "", romanization = "" }); };
            var held = service.ReadAsync(new("wy", "id"), default);
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Client.Invalidate("kg"); Check(!held.IsCompleted); f.Client.Invalidate("wy");
                Check(await service.ReadAsync(new("kg", "id"), default) is not null);
            } finally { release.TrySetResult(); }
            await Failure("STALE_SESSION", () => held); await f.Client.DrainAsync();
        });
        yield return Case("replacement of an exact account lease retires its late lyric result", async () => {
            await using var f = new MusicPlatformRuntimeFixture(); var entered = Signal(); var release = Signal();
            f.Sessions.Current = new("wy", "old", 1, "42", "MUSIC_U=SYNTHETIC_OLD");
            f.OnRequest = async (_, _) => { entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { text = "[00:01]late", translation = "", romanization = "" }); };
            var held = new NativeMusicLyrics(f.Client).ReadAsync(new("wy", "id"), default);
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Sessions.Current = new("wy", "new", 2, "42", "MUSIC_U=SYNTHETIC_NEW");
            } finally { release.TrySetResult(); }
            await Failure("STALE_SESSION", () => held);
        });
    }
    private static TestCase Case(string name, Func<Task> run) => new("native-lyrics: " + name, run);
    private static async Task Cancelled(Func<Task> run)
    {
        try { await run(); }
        catch (OperationCanceledException) { return; }
        catch (MusicPlatformException e) when (e.Code == "CANCELLED") { return; }
        throw new InvalidOperationException("Expected lyric cancellation");
    }
}
