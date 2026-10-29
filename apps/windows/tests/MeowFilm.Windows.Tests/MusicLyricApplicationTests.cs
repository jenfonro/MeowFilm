using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicLyricApplicationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("official exact lyrics work without LX target or ordinary Node business", async f =>
        {
            var result = await f.Lookup(Song());
            Ready(result); Equal("wy", f.Provider.Requests.Single().Platform);
            Equal("42", f.Provider.Requests.Single().RecordingId);
            Equal(0, f.ScriptCalls); Equal(0, f.Store.Writes);
            Equal("translation", result.GetProperty("lines")[0].GetProperty("translation").GetString());
            Equal("romanized", result.GetProperty("lines")[0].GetProperty("romanization").GetString());
        });
        yield return Case("QQ uses exact songmid never numeric music id or media id", async f =>
        {
            var song = Song("tx", "004exact");
            SetNative(song, "qqSongId", "987"); SetNative(song, "mediaMid", "wrong");
            Ready(await f.Lookup(song)); Equal("004exact", f.Provider.Requests.Single().RecordingId);
        });
        yield return Case("Kugou hash remains the exact uppercase recording with seconds and album identity", async f =>
        {
            var song = Song("kg", "aabbccddaabbccddaabbccddaabbccdd");
            SetNative(song, "albumAudioId", "123");
            Ready(await f.Lookup(song)); var request = f.Provider.Requests.Single();
            Equal("AABBCCDDAABBCCDDAABBCCDDAABBCCDD", request.RecordingId);
            Equal(120d, request.DurationSeconds); Equal("123", request.AlbumAudioId);
        });
        yield return Case("unknown explicitly chosen edition is rejected before any provider", async f =>
        {
            var result = await f.Owner.ExecuteAsync(new { song = Song(), sourceKey = "[\"wy\",\"different\"]" });
            Code(result, "TARGET_CHANGED"); Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
        });
        yield return Case("quarantined edition cannot reappear as the canonical source", async f =>
        {
            Ready(await f.Lookup(Song()));
            var song = Song();
            var quarantined = Song("wy", "43")["sources"]![0]!.DeepClone();
            song["quarantinedSources"] = new JsonArray(quarantined);
            var key = quarantined!["sourceKey"]!.GetValue<string>();
            Code(await f.Owner.ExecuteAsync(new { song, sourceKey = key }), "TARGET_CHANGED");
            Equal(1, f.Provider.Requests.Count);
        });
        yield return Case("metadata request does not inherit the chosen audio delivery platform", async f =>
        {
            var result = await f.Owner.ExecuteAsync(new { song = Song(),
                scriptTarget = new { installationId = "selected-audio-script", platform = "kg" } });
            Ready(result); Equal("wy", f.Provider.Requests.Single().Platform); Equal(0, f.ScriptCalls);
        });
        yield return Case("WY missing official lyric permits only explicit same-platform LX fallback", async f =>
        {
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            var result = await f.Owner.ExecuteAsync(new { song = Song(),
                scriptTarget = new { installationId = "chosen-script", platform = "wy" } });
            Ready(result); Equal(1, f.ScriptCalls);
            Equal("chosen-script", f.ScriptInput.GetProperty("installationId").GetString());
            Equal("wy", f.ScriptInput.GetProperty("platform").GetString());
            True(f.ScriptInput.GetProperty("sourceKey").GetString()!.Length > 0);
        });
        foreach (var platform in new[] { "tx", "kg" })
            yield return Case(platform + " never falls back to LX even if the script advertises lyrics", async f =>
            {
                f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
                var result = await f.Owner.ExecuteAsync(new { song = Song(platform, platform == "tx" ? "mid42" : new string('A', 32)),
                    scriptTarget = new { installationId = "not-used", platform } });
                Equal("none", result.GetProperty("state").GetString()); Equal(0, f.ScriptCalls);
            });
        yield return Case("no implicit script selection or cross-platform fallback on empty official result", async f =>
        {
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            Equal("none", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal("none", (await f.Owner.ExecuteAsync(new { song = Song(),
                scriptTarget = new { installationId = "not-used", platform = "kg" } })).GetProperty("state").GetString());
            Equal(0, f.ScriptCalls); Equal(2, f.Provider.Requests.Count); // Empty results are not cached.
        });
        yield return Case("disabled native lyric preference causes zero provider and script calls", async f =>
        {
            var saved = await f.Library.ExecuteAsync("music.settings.save", new {
                ifRevision = 0, lyrics = false, target = (object?)null
            });
            True(saved.GetProperty("ok").GetBoolean());
            Equal("disabled", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
        });
        yield return Case("metadata order consumes only exact enabled editions rather than the playback target", async f =>
        {
            var song = Song();
            var qq = await f.Library.ExecuteAsync("music.online.song", new { platform = "tx",
                musicInfo = Song("tx", "004exact")["source_data"], title = "Fixture song",
                artist = "Fixture artist", album = "Fixture album", duration = 120 });
            True(qq.GetProperty("ok").GetBoolean());
            // The public contract admits exact normalized source rows, not
            // arbitrary raw songs masquerading as established mappings.
            song["sources"]!.AsArray().Add(JsonNode.Parse(qq.GetProperty("song").GetRawText())!["sources"]![0]!.DeepClone());
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "tx", "wy", "kg", "kw", "mg" }, enabled = new[] { "wy", "tx" } })).GetProperty("ok").GetBoolean());
            Ready(await f.Lookup(song)); Equal("tx", f.Provider.Requests.Single().Platform);
        });
        yield return Case("empty metadata enabled subset blocks both official and explicit LX fallback", async f =>
        {
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = Array.Empty<string>() })).GetProperty("ok").GetBoolean());
            var result = await f.Owner.ExecuteAsync(new { song = Song(),
                scriptTarget = new { installationId = "explicit-script", platform = "wy" } });
            Equal("disabled", result.GetProperty("state").GetString());
            Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
        });
        yield return Case("explicit disabled source never bypasses the persisted policy through a warm cache", async f =>
        {
            var ready = await f.Lookup(Song());
            var key = ready.GetProperty("sourceKey").GetString();
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = new[] { "tx" } })).GetProperty("ok").GetBoolean());
            Equal("disabled", (await f.Owner.ExecuteAsync(new { song = Song(), sourceKey = key })).GetProperty("state").GetString());
            Equal(1, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
        });
        yield return Case("automatic lookup reports disabled when none of the song editions is enabled", async f =>
        {
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = new[] { "tx" } })).GetProperty("ok").GetBoolean());
            Equal("disabled", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
        });
        yield return Case("saved local timed text is returned without any provider and is not a fabricated source key", async f =>
        {
            True((await f.Library.ExecuteAsync("music.queue.replace", new { songs = new[] { Song() }, index = 0 })).GetProperty("ok").GetBoolean());
            True((await f.Library.ExecuteAsync("music.lyrics.local.save", new { song = Song(), text = "[00:01]chosen\n[00:20]second" })).GetProperty("ok").GetBoolean());
            var result = await f.Lookup(Song());
            Equal("local", result.GetProperty("state").GetString());
            Equal("chosen", result.GetProperty("lines")[0].GetProperty("text").GetString());
            True(!result.TryGetProperty("sourceKey", out _)); Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
        });
        yield return Case("a late explicit script result cannot publish after native metadata policy changes", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate();
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            f.ScriptHandler = async () => { arrived.TrySetResult(); await release.Task; };
            var read = f.Owner.ExecuteAsync(new { song = Song(),
                scriptTarget = new { installationId = "chosen-script", platform = "wy" } });
            await arrived.Task;
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = Array.Empty<string>() })).GetProperty("ok").GetBoolean());
            f.Owner.Invalidate(); release.TrySetResult();
            Code(await read, "STALE_SESSION");
            Equal("disabled", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal(1, f.ScriptCalls);
        });
        yield return Case("coalesced subscribers cancel independently until the last one leaves", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate(); bool physicalCancelled = false;
            f.Provider.Handler = async (request, token) => {
                using var registration = token.Register(() => physicalCancelled = true);
                arrived.TrySetResult(); await release.Task.WaitAsync(token); return Payload(request);
            };
            using var a = new CancellationTokenSource(); using var b = new CancellationTokenSource();
            var first = f.Lookup(Song(), a.Token); await arrived.Task;
            var second = f.Lookup(Song(), b.Token);
            a.Cancel(); Code(await first, "CANCELLED"); True(!physicalCancelled);
            release.TrySetResult(); Ready(await second); Equal(1, f.Provider.Requests.Count);
        });
        yield return Case("last cancelled subscriber drains before the same recording can restart", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate(); var cancelled = Signal();
            f.Provider.Handler = async (request, token) => {
                using var registration = token.Register(() => cancelled.TrySetResult());
                arrived.TrySetResult(); await release.Task; return Payload(request);
            };
            using var cancellation = new CancellationTokenSource();
            var first = f.Lookup(Song(), cancellation.Token); await arrived.Task;
            cancellation.Cancel(); await cancelled.Task;
            var joined = f.Lookup(Song());
            await Task.Delay(30);
            True(!first.IsCompleted && !joined.IsCompleted);
            Equal(1, f.Provider.Requests.Count);
            release.TrySetResult();
            Code(await first, "CANCELLED");
            Code(await joined, "CANCELLED");
            f.Provider.Handler = (request, _) => Task.FromResult<NativeLyricPayload?>(Payload(request));
            Ready(await f.Lookup(Song()));
            Equal(2, f.Provider.Requests.Count);
        });
        yield return Case("account invalidation cancels pending old metadata and cannot publish into new epoch", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate();
            f.Provider.Handler = async (request, _) => {
                arrived.TrySetResult(); await release.Task; return Payload(request);
            };
            var old = f.Lookup(Song()); await arrived.Task;
            f.Owner.Invalidate("wy");
            f.Provider.Handler = (request, _) => Task.FromResult<NativeLyricPayload?>(Payload(request) with { Text = "[00:01]new" });
            var fresh = await f.Lookup(Song()); release.TrySetResult();
            Code(await old, "CANCELLED"); Ready(fresh);
            Equal("new", (await f.Lookup(Song())).GetProperty("lines")[0].GetProperty("text").GetString());
            Equal(2, f.Provider.Requests.Count); Equal(1, f.Provider.Invalidations);
        });
        yield return Case("successful cache expires after ten minutes without extending expiry on hit", async f =>
        {
            Ready(await f.Lookup(Song())); f.Clock.Advance(TimeSpan.FromMinutes(9));
            Ready(await f.Lookup(Song())); Equal(1, f.Provider.Requests.Count);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            Ready(await f.Lookup(Song())); Equal(2, f.Provider.Requests.Count);
        });
        yield return Case("cache keeps at most one hundred exact recording entries", async f =>
        {
            for (int i = 1; i <= 101; i++) Ready(await f.Lookup(Song(id: i.ToString())));
            Equal(101, f.Provider.Requests.Count);
            Ready(await f.Lookup(Song(id: "101"))); Equal(101, f.Provider.Requests.Count);
            Ready(await f.Lookup(Song(id: "1"))); Equal(102, f.Provider.Requests.Count);
        });
        yield return Case("provider identity mismatch is a fixed error and is never cached", async f =>
        {
            f.Provider.Handler = (request, _) => Task.FromResult<NativeLyricPayload?>(Payload(request) with { RecordingId = "wrong" });
            Code(await f.Lookup(Song()), "LYRIC_IDENTITY_MISMATCH");
            Code(await f.Lookup(Song()), "LYRIC_IDENTITY_MISMATCH");
            Equal(2, f.Provider.Requests.Count);
        });
        yield return Case("invalid lyric size rejects without storing a truncated success", async f =>
        {
            f.Provider.Handler = (request, _) => Task.FromResult<NativeLyricPayload?>(Payload(request) with {
                Text = "[00:01]" + new string('x', 65536)
            });
            Code(await f.Lookup(Song()), "INVALID_LYRIC_RESPONSE");
            f.Provider.Handler = (request, _) => Task.FromResult<NativeLyricPayload?>(Payload(request));
            Ready(await f.Lookup(Song())); Equal(2, f.Provider.Requests.Count);
        });
        yield return Case("native lyric owner close physically drains a noncooperative request", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate(); var cancelled = Signal();
            f.Provider.Handler = async (request, token) => {
                using var registration = token.Register(() => cancelled.TrySetResult());
                arrived.TrySetResult(); await release.Task; return Payload(request);
            };
            var read = f.Lookup(Song()); await arrived.Task;
            var close = f.Owner.DisposeAsync().AsTask(); await cancelled.Task;
            True(!close.IsCompleted);
            release.TrySetResult(); await close; Code(await read, "CANCELLED");
            Code(await f.Lookup(Song()), "CLOSED");
        });
        yield return Case("cancelled but physically running requests retain the sixteen-work admission budget", async f =>
        {
            var release = f.ReleaseGate();
            var arrived = Enumerable.Range(0, 16).Select(_ => Signal()).ToArray();
            int calls = -1;
            f.Provider.Handler = async (request, _) => {
                arrived[Interlocked.Increment(ref calls)].TrySetResult();
                await release.Task; return Payload(request);
            };
            var cancellations = new List<CancellationTokenSource>();
            var work = new List<Task<JsonElement>>();
            for (int i = 0; i < 16; i++)
            {
                var cancel = new CancellationTokenSource();
                cancellations.Add(cancel);
                var pending = f.Lookup(Song(id: (i + 1).ToString()), cancel.Token);
                work.Add(pending);
                await arrived[i].Task;
                cancel.Cancel();
            }
            Code(await f.Lookup(Song(id: "99")), "LIMIT");
            Equal(16, f.Provider.Requests.Count);
            release.TrySetResult();
            foreach (var pending in work) Code(await pending, "CANCELLED");
            foreach (var cancellation in cancellations) cancellation.Dispose();
        });
    }
    private static TestCase Case(string name, Func<Fixture, Task> body) => new("Native lyric application " + name,
        async () => { await using var fixture = new Fixture(); await body(fixture).WaitAsync(TimeSpan.FromSeconds(10)); });
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static NativeLyricPayload Payload(NativeLyricRequest request) =>
        new(request.Platform, request.RecordingId, "[00:01]original", "[00:01]translation", "[00:01]romanized");
    private static JsonObject Song(string platform = "wy", string id = "42")
    {
        var native = new JsonObject { ["duration"] = 120 };
        if (platform == "kg") native["hash"] = id;
        return MusicSongFixture.Online(platform, id, "Fixture song", "Fixture artist",
            "Fixture album", 120, native: native);
    }
    private static void SetNative(JsonObject song, string name, string value)
    {
        song["source_data"]![name] = value;
        song["sources"]![0]!["source_data"]![name] = value;
    }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Lyric assertion failed"); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}"); }
    private static void Ready(JsonElement result)
    { True(result.GetProperty("ok").GetBoolean()); Equal("ready", result.GetProperty("state").GetString()); True(result.GetProperty("lines").GetArrayLength() > 0); }
    private static void Code(JsonElement result, string expected)
    { True(!result.GetProperty("ok").GetBoolean()); Equal(expected, result.GetProperty("code").GetString()); }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new();
        internal readonly Provider Provider = new();
        internal readonly Clock Clock = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicLyricApplication Owner;
        internal int ScriptCalls;
        internal JsonElement ScriptInput;
        internal Func<Task>? ScriptHandler;
        private readonly List<TaskCompletionSource> releaseOnClose = [];
        internal TaskCompletionSource ReleaseGate()
        { var value = Signal(); releaseOnClose.Add(value); return value; }
        internal Fixture()
        {
            Library = new(Store); Owner = new(Library, Provider, Script, Clock);
        }
        private async Task<JsonElement> Script(string method, object? input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Equal("music.online.lyrics", method);
            ScriptCalls++; ScriptInput = JsonSerializer.SerializeToElement(input);
            if (ScriptHandler is { } handler) await handler();
            return JsonSerializer.SerializeToElement(new { ok = true, state = "ready",
                lines = new[] { new { time = 1, text = "script" } } });
        }
        internal Task<JsonElement> Lookup(JsonObject song, CancellationToken ct = default) =>
            Owner.ExecuteAsync(new { song }, ct);
        public async ValueTask DisposeAsync()
        { foreach (var value in releaseOnClose) value.TrySetResult(); await Owner.DisposeAsync(); await Library.AbortAsync(); }
    }
    private sealed class Provider : INativeMusicLyrics
    {
        internal readonly ConcurrentQueue<NativeLyricRequest> Requests = new();
        internal Func<NativeLyricRequest, CancellationToken, Task<NativeLyricPayload?>> Handler =
            (request, _) => Task.FromResult<NativeLyricPayload?>(Payload(request));
        internal int Invalidations;
        public Task<NativeLyricPayload?> ReadAsync(NativeLyricRequest request, CancellationToken ct)
        { Requests.Enqueue(request); return Handler(request, ct); }
        public void Invalidate(string? platform = null) => Invalidations++;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan time) => now += time;
    }
    private sealed class Store : IProtectedDocumentStore
    {
        private byte[]? data;
        internal int Writes;
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, ns);
            return Task.FromResult(new ProtectedDocument(data?.ToArray(),
                data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data)));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, ns);
            var before = data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data);
            if (expected != before) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
            Writes++; data = bytes.ToArray(); return Task.FromResult(DocumentFingerprint.FromBytes(data));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
