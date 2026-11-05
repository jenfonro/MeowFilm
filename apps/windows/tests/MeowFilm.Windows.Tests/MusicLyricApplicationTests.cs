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
        yield return Case("official exact lyrics work without script discovery or ordinary Node business", async f =>
        {
            var result = await f.Lookup(Song());
            Ready(result); Equal("wy", f.Provider.Requests.Single().Platform);
            Equal("42", f.Provider.Requests.Single().RecordingId);
            Equal(0, f.ScriptCalls); Equal(0, f.SourceReads); Equal(0, f.Store.Writes);
            Equal("translation", result.GetProperty("lines")[0].GetProperty("translation").GetString());
            Equal("romanized", result.GetProperty("lines")[0].GetProperty("romanization").GetString());
        });
        yield return Case("QQ uses exact songmid never numeric music id or media id", async f =>
        {
            var song = Song("tx", "004exact");
            SetNative(song, "qqSongId", "987"); SetNative(song, "strMediaMid", "wrong");
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

        yield return Case("enabled script platforms do not replace exact official lyric identity", async f =>
        {
            f.AddSource("kugou-script", "kg");
            f.AddSource("netease-script", "wy");
            var result = await f.Lookup(Song());
            Ready(result); Equal("wy", f.Provider.Requests.Single().Platform); Equal(0, f.ScriptCalls);
            Equal(0, f.SourceReads);
        });
        foreach (var platform in new[] { "wy", "kw", "mg" })
            yield return Case(platform + " uses enabled same-platform scripts for missing native lyrics", async f =>
            {
                f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
                f.AddSource("disabled-script", platform, enabled: false);
                f.AddSource("other-platform", "kg");
                f.AddSource("enabled-script", platform);
                var result = await f.Lookup(Song(platform));
                Ready(result); Equal(1, f.ScriptCalls);
                Equal("enabled-script", f.ScriptInput.GetProperty("installationId").GetString());
                Equal(platform, f.ScriptInput.GetProperty("platform").GetString());
                Equal(1L, f.ScriptInput.GetProperty("generation").GetInt64());
                True(f.ScriptInput.GetProperty("sourceKey").GetString()!.Length > 0);
                Equal(platform == "wy" ? 1 : 0, f.Provider.Requests.Count);
            });
        yield return Case("idle enabled scripts activate sequentially only until exact lyrics are found", async f =>
        {
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            f.AddSource("disabled", "wy", enabled: false, idle: true);
            f.AddSource("no-lyrics", "wy", idle: true, lyrics: false);
            f.AddSource("empty", "wy", idle: true).Text = "";
            f.AddSource("match", "wy", idle: true).Text = "accepted";
            f.AddSource("unused", "wy", idle: true);
            Equal(0, f.ScriptCalls); Equal(0, f.Activated.Count);
            var result = await f.Lookup(Song());
            Ready(result); Equal("accepted", result.GetProperty("lines")[0].GetProperty("text").GetString());
            True(f.Activated.SequenceEqual(["no-lyrics", "empty", "match"]));
            True(f.ScriptRequests.Select(input => input.GetProperty("installationId").GetString())
                .SequenceEqual(["no-lyrics", "empty", "match"]));
            True(f.ScriptRequests.All(input => input.GetProperty("generation").GetInt64() == 0));
            Equal(1, f.Provider.Requests.Count);
        });
        foreach (var platform in new[] { "tx", "kg" })
            yield return Case(platform + " never falls back to LX even if the script advertises lyrics", async f =>
            {
                f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
                f.AddSource("not-used", platform);
                var result = await f.Lookup(Song(platform, platform == "tx" ? "mid42" : new string('A', 32)));
                Equal("none", result.GetProperty("state").GetString()); Equal(0, f.ScriptCalls);
                Equal(0, f.SourceReads);
            });
        yield return Case("disabled or mismatched scripts cannot supply missing official lyrics", async f =>
        {
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            f.AddSource("disabled", "wy", enabled: false);
            f.AddSource("other-platform", "kg");
            Equal("none", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal("none", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal(0, f.ScriptCalls); Equal(2, f.Provider.Requests.Count); // Empty results are not cached.
        });
        foreach (var code in new[] { "CANCELLED", "STALE_SESSION", "CORRUPT_STORAGE", "PENDING_WRITE",
            "COMMIT_UNKNOWN", "PERMISSION_DENIED", "SCRIPT_EXCEPTION" })
            yield return Case("script " + code + " cannot silently continue to another installation", async f =>
            {
                f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
                f.AddSource("failed", "wy").Error = code;
                f.AddSource("unused", "wy", idle: true);
                Code(await f.Lookup(Song()), code);
                Equal(1, f.ScriptCalls); Equal(0, f.Activated.Count);
            });
        yield return Case("disabled native lyric preference causes zero provider and script calls", async f =>
        {
            f.AddSource("enabled-script", "wy");
            var saved = await f.Library.ExecuteAsync("music.settings.save", new {
                ifRevision = 0, lyrics = false
            });
            True(saved.GetProperty("ok").GetBoolean());
            Equal("disabled", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls); Equal(0, f.SourceReads);
        });
        yield return Case("metadata order consumes only exact enabled editions independently of scripts", async f =>
        {
            f.AddSource("netease-script", "wy");
            var song = Song();
            var qq = await f.Library.ExecuteAsync("music.online.song", new { platform = "tx",
                musicInfo = MusicSongFixture.Info(Song("tx", "004exact")), title = "Fixture song",
                artist = "Fixture artist", album = "Fixture album", duration = 120 });
            True(qq.GetProperty("ok").GetBoolean());
            // The public contract admits exact normalized source rows, not
            // arbitrary raw songs masquerading as established mappings.
            song["sources"]!.AsArray().Add(JsonNode.Parse(qq.GetProperty("song").GetRawText())!["sources"]![0]!.DeepClone());
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "tx", "wy", "kg", "kw", "mg" }, enabled = new[] { "wy", "tx" } })).GetProperty("ok").GetBoolean());
            Ready(await f.Lookup(song)); Equal("tx", f.Provider.Requests.Single().Platform);
        });
        yield return Case("empty metadata enabled subset blocks both official and enabled LX lookup", async f =>
        {
            f.AddSource("enabled-script", "wy");
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = Array.Empty<string>() })).GetProperty("ok").GetBoolean());
            var result = await f.Lookup(Song());
            Equal("disabled", result.GetProperty("state").GetString());
            Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls); Equal(0, f.SourceReads);
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
            f.AddSource("enabled-script", "wy", idle: true);
            True((await f.Library.ExecuteAsync("music.queue.replace", new { songs = new[] { Song() }, index = 0 })).GetProperty("ok").GetBoolean());
            True((await f.Library.ExecuteAsync("music.lyrics.local.save", new { song = Song(), text = "[00:01]chosen\n[00:20]second" })).GetProperty("ok").GetBoolean());
            var result = await f.Lookup(Song());
            Equal("local", result.GetProperty("state").GetString());
            Equal("chosen", result.GetProperty("lines")[0].GetProperty("text").GetString());
            True(!result.TryGetProperty("sourceKey", out _)); Equal(0, f.Provider.Requests.Count); Equal(0, f.ScriptCalls);
            Equal(0, f.SourceReads); Equal(0, f.Activated.Count);
        });
        yield return Case("a late enabled script result cannot publish after native metadata policy changes", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate();
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            f.AddSource("enabled-script", "wy");
            f.ScriptHandler = async _ => { arrived.TrySetResult(); await release.Task; };
            var read = f.Lookup(Song());
            await arrived.Task;
            True((await f.Library.ExecuteAsync("music.metadata.settings.save", new { ifRevision = 0,
                platforms = new[] { "wy", "tx", "kg", "kw", "mg" }, enabled = Array.Empty<string>() })).GetProperty("ok").GetBoolean());
            f.Owner.Invalidate(); release.TrySetResult();
            Code(await read, "STALE_SESSION");
            Equal("disabled", (await f.Lookup(Song())).GetProperty("state").GetString());
            Equal(1, f.ScriptCalls);
        });
        foreach (var mutation in new[] { "disable", "update" })
            yield return Case("source " + mutation + " rejects late script lyrics and uses current grants on retry", async f =>
            {
                var arrived = Signal(); var release = f.ReleaseGate();
                f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
                var first = f.AddSource("changed", "wy"); first.Text = "old";
                f.AddSource("remaining", "wy").Text = "remaining";
                f.ScriptHandler = async _ => { arrived.TrySetResult(); await release.Task; };
                var read = f.Lookup(Song()); await arrived.Task;
                if (mutation == "disable") first.Enabled = false;
                else { first.Generation++; first.Text = "updated"; }
                f.Owner.Invalidate();
                release.TrySetResult(); Code(await read, "STALE_SESSION");
                f.ScriptHandler = null;
                var fresh = await f.Lookup(Song());
                Ready(fresh);
                Equal(mutation == "disable" ? "remaining" : "updated",
                    fresh.GetProperty("lines")[0].GetProperty("text").GetString());
                Equal(mutation == "disable" ? "remaining" : "changed",
                    f.ScriptInput.GetProperty("installationId").GetString());
                Equal(mutation == "disable" ? 1L : 2L, f.ScriptInput.GetProperty("generation").GetInt64());
                Equal(2, f.ScriptCalls);
            });
        yield return Case("cancelled script lookup keeps owned read and never starts the next installation", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate(); var cancelled = Signal();
            f.Provider.Handler = (_, _) => Task.FromResult<NativeLyricPayload?>(null);
            f.AddSource("first", "wy"); f.AddSource("unused", "wy", idle: true);
            f.ScriptHandler = async token => {
                using var registration = token.Register(() => cancelled.TrySetResult());
                arrived.TrySetResult(); await release.Task;
            };
            using var cancellation = new CancellationTokenSource();
            var read = f.Lookup(Song(), cancellation.Token); await arrived.Task;
            cancellation.Cancel(); Code(await read.WaitAsync(TimeSpan.FromSeconds(2)), "CANCELLED");
            True(!cancelled.Task.IsCompleted); Equal(1, f.ScriptCalls);
            var drain = f.Owner.DrainAsync(); True(!drain.IsCompleted);
            release.TrySetResult(); await drain;
            Equal(1, f.ScriptCalls); Equal(0, f.Activated.Count);
        });
        yield return Case("coalesced subscribers cancel independently of owned work", async f =>
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
        yield return Case("last cancelled subscriber leaves a reusable in-flight recording", async f =>
        {
            var arrived = Signal(); var release = f.ReleaseGate(); var cancelled = Signal();
            f.Provider.Handler = async (request, token) => {
                using var registration = token.Register(() => cancelled.TrySetResult());
                arrived.TrySetResult(); await release.Task; return Payload(request);
            };
            using var cancellation = new CancellationTokenSource();
            var first = f.Lookup(Song(), cancellation.Token); await arrived.Task;
            cancellation.Cancel(); Code(await first.WaitAsync(TimeSpan.FromSeconds(2)), "CANCELLED");
            True(!cancelled.Task.IsCompleted);
            var joined = f.Lookup(Song());
            await Task.Delay(30);
            True(first.IsCompleted && !joined.IsCompleted);
            Equal(1, f.Provider.Requests.Count);
            release.TrySetResult();
            Code(await first, "CANCELLED");
            Ready(await joined);
            Ready(await f.Lookup(Song()));
            Equal(1, f.Provider.Requests.Count);
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
        yield return Case("cancelled physical work stays tracked without refusing current lyrics", async f =>
        {
            var release = f.ReleaseGate();
            var arrived = Enumerable.Range(0, 24).Select(_ => Signal()).ToArray();
            int calls = -1;
            f.Provider.Handler = async (request, _) => {
                arrived[Interlocked.Increment(ref calls)].TrySetResult();
                await release.Task; return Payload(request);
            };
            var cancellations = new List<CancellationTokenSource>();
            var work = new List<Task<JsonElement>>();
            for (int i = 0; i < 24; i++)
            {
                var cancel = new CancellationTokenSource();
                cancellations.Add(cancel);
                var pending = f.Lookup(Song(id: (i + 1).ToString()), cancel.Token);
                work.Add(pending);
                await arrived[i].Task;
                cancel.Cancel();
            }
            Equal(24, f.Provider.Requests.Count);
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
        MusicSongFixture.Info(song)[name] = value;
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
        internal int ScriptCalls, SourceReads;
        internal JsonElement ScriptInput;
        internal Func<CancellationToken, Task>? ScriptHandler;
        internal readonly List<JsonElement> ScriptRequests = [];
        internal readonly List<string> Activated = [];
        private readonly List<ScriptSource> sources = [];
        private readonly List<TaskCompletionSource> releaseOnClose = [];
        internal TaskCompletionSource ReleaseGate()
        { var value = Signal(); releaseOnClose.Add(value); return value; }
        internal Fixture()
        {
            Library = new(Store); Owner = new(Library, Provider, Script, Clock);
        }
        internal ScriptSource AddSource(string id, string platform, bool enabled = true, bool idle = false, bool lyrics = true)
        {
            var source = new ScriptSource(id, platform, enabled, idle, lyrics);
            sources.Add(source); return source;
        }
        private async Task<JsonElement> Script(string method, object? input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (method == "music.sources.list")
            {
                SourceReads++;
                return JsonSerializer.SerializeToElement(new { ok = true, revision = 1,
                    items = sources.Select(value => value.View()).ToArray() });
            }
            var parameters = JsonSerializer.SerializeToElement(input);
            string id = parameters.GetProperty("installationId").GetString()!;
            var source = sources.Single(value => value.Id == id);
            if (method == "music.sources.detail")
            {
                SourceReads++;
                return JsonSerializer.SerializeToElement(new { ok = true, item = source.View() });
            }
            Equal("music.online.lyrics", method);
            ScriptCalls++; ScriptInput = JsonSerializer.SerializeToElement(input);
            ScriptRequests.Add(ScriptInput);
            True(source.Enabled); Equal(source.Generation, parameters.GetProperty("generation").GetInt64());
            if (source.Generation == 0) { source.Generation = 1; Activated.Add(id); }
            if (parameters.GetProperty("platform").GetString() != source.Platform || !source.Lyrics)
                return JsonSerializer.SerializeToElement(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
            string text = source.Text;
            if (ScriptHandler is { } handler) await handler(ct);
            if (source.Error is { } code) return JsonSerializer.SerializeToElement(new { ok = false, code });
            return JsonSerializer.SerializeToElement(new { ok = true, state = text.Length > 0 ? "ready" : "none",
                lines = text.Length > 0 ? new[] { new { time = 1, text } } : [] });
        }
        internal Task<JsonElement> Lookup(JsonObject song, CancellationToken ct = default) =>
            Owner.ExecuteAsync(new { song }, ct);
        public async ValueTask DisposeAsync()
        { foreach (var value in releaseOnClose) value.TrySetResult(); await Owner.DisposeAsync(); await Library.AbortAsync(); }
    }
    private sealed class ScriptSource(string id, string platform, bool enabled, bool idle, bool lyrics)
    {
        internal readonly string Id = id, Platform = platform;
        internal readonly bool Lyrics = lyrics;
        internal bool Enabled = enabled;
        internal long Generation = idle ? 0 : 1;
        internal string Text = "script";
        internal string? Error;
        internal object View() => new {
            installationId = Id, enabled = Enabled, generation = Generation,
            state = !Enabled ? "disabled" : Generation == 0 ? "idle" : "ready",
            capabilities = Generation == 0 ? [] : new[] {
                new { platform = Platform, actions = Lyrics ? new[] { "lyric" } : new[] { "musicUrl" } }
            }
        };
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
    private sealed class Store : IDocumentStore
    {
        private byte[]? data;
        internal int Writes;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, ns);
            return Task.FromResult(new DocumentSnapshot(data?.ToArray(),
                data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data)));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, ns);
            var before = data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data);
            if (expected != before) throw new DocumentStoreException(DocumentStoreError.Conflict);
            Writes++; data = bytes.ToArray(); return Task.FromResult(DocumentFingerprint.FromBytes(data));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
