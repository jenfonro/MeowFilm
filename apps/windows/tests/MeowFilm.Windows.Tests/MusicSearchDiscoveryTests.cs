// SPDX-License-Identifier: GPL-3.0-only
// Native discovery lifecycle/repository with the shared canonical platform peer.
// HTTP recipes, EAPI, JSON tokens, byte limits and body cleanup belong to Node.
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchDiscoveryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("canonical-operation-and-display-order", Canonical);
        yield return Case("anonymous-and-account-publication-do-not-leak-credentials", Credentials);
        yield return Case("independent-discovery-instances-have-no-shared-quota", Parallel);
        yield return Case("display-lists-preserve-all-canonical-rows", () => Content("many"));
        yield return Case("cache-copy-isolation-rotation-no-network", Cache);
        yield return Case("platform-publication-keeps-completed-native-cache", () => CacheScope("publication"));
        yield return Case("same-credential-keeps-rotation-and-completed-cache", () => CacheScope("same"));
        yield return Case("account-invalidation-fences-rotation-and-completed-cache", () => CacheScope("invalidate"));
        yield return Case("empty-default-and-empty-sections-valid", () => Content("empty"));
        yield return Case("unconsumed-canonical-fields-do-not-drop-sections", () => Content("extra"));
        yield return Case("canonical-keyword-order-and-null-score", () => Content("duplicates"));
        foreach (string stage in new[] { "hot", "toplist", "default", "chart" })
        { string s = stage; yield return Case("partial-" + s, () => Partial(s)); }
        yield return Case("both-primary-failure-is-not-an-empty-success", () => FailureCase("UPSTREAM_ERROR"));
        yield return Case("failed-refresh-retains-completed-cache", FailedRefresh);
        foreach (string kind in new[] { "playlist-id", "duplicate-track", "zero-track", "duplicate-song", "missing-song", "alien-song", "too-many-songs" })
        { string s = kind; yield return Case("ordinary-content-" + s + "-does-not-become-an-identity-gate", () => Content(s)); }
        yield return Case("empty-chart-is-consumable-without-native-detail-request", () => Content("empty-chart"));
        yield return Case("embedded-chart-members-use-existing-canonical-data", () => Content("embedded"));
        yield return Case("partial-chart-members-do-not-trigger-native-proof-read", () => Content("partial-members"));
        yield return Case("duplicate-chart-identities-do-not-fail-discovery", () => Content("duplicate-charts"));
        foreach (string stage in new[] { "hot", "toplist", "default", "chart", "songs" })
        { string s = stage; yield return Case("failed-" + s + "-does-not-publish-or-write", () => FailureCase("INVALID_RESPONSE")); }
        yield return Case("expired-primary-authorization-remains-auth-required", () => FailureCase("AUTH_REQUIRED"));
        yield return Case("partial-authorization-failure-remains-associated", () => Partial("default", "AUTH_REQUIRED"));
        foreach (string kind in new[] { "hot-nonstring", "default-nonstring", "score-negative", "keyword-control", "keyword-limit", "response-limit", "invalid-json" })
        { string s = kind; yield return Case("consumed-field-or-platform-boundary-" + s, () => Boundary(s)); }
        yield return Case("canonical-whole-snapshot-has-no-second-content-budget", () => Content("large"));
        yield return Case("canonical-safe-projection-and-complete-artwork-resource", SafeFields);
        foreach (string code in new[] { "AUTH_REQUIRED", "FORBIDDEN", "RATE_LIMITED", "REDIRECT_DENIED", "UPSTREAM_ERROR", "VERIFICATION_REQUIRED" })
        { string c = code; yield return Case("finite-error-" + c, () => FailureCase(c)); }
        yield return Case("exchange-exception-never-leaks", HandlerFailure);
        yield return Case("coalesced-reader-cancel-does-not-cancel-other", Coalescing);
        foreach (string boundary in new[] { "publication", "result", "metadata" })
        { string s = boundary; yield return Case("last-unsubscribe-owned-" + s, () => Physical(s, "cancel")); }
        yield return Case("single-deadline-25s-awaits-physical", () => Physical("result", "timeout"));
        yield return Case("invalidate-rejects-result-awaits-physical", () => Physical("result", "invalidate"));
        yield return Case("prepare-drains-physical", () => Physical("result", "prepare"));
        yield return Case("dispose-drains-physical-and-borrows-inputs", () => Physical("metadata", "close"));
        yield return Case("invalidation-then-read-drains-old-before-replacement", () => Replacement(false));
        yield return Case("refresh-joins-active-flight", () => Replacement(true));
        yield return Case("cancelled-last-reader-replacement-joins-owned-flight", LastReaderReplacement);
        yield return Case("anonymous-to-account-owner-event-fences-publication", AnonymousChanged);
        yield return Case("credential-replacement-owner-event-fences-publication", CookieChanged);
        yield return Case("account-revoked-during-read-without-event-fenced", Revoked);
        yield return Case("account-acquire-unsubscribe-retains-owned-read", AcquireDrain);
        yield return Case("cached-session-current-check-and-nonwy-invalidation", () => CacheScope("account"));
        yield return Case("acquire-error-redacted-no-platform-request", AcquireError);
        yield return Case("initial-acquire-wrong-platform-and-stale-refused", InvalidSession);
        yield return Case("pre-cancelled-no-acquire-or-platform-request", PreCancelled);
        yield return Case("prepare-retains-cache-close-idempotent", PrepareAndClose);
        yield return Case("rotate-generation-refuses-empty-or-revoked", RotateFence);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.search.discovery." + name, run);
    private static void Check(bool condition, string reason = "Discovery invariant") { if (!condition) throw new InvalidOperationException(reason); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task Failure(string code, Func<Task> action)
    {
        try { await action(); }
        catch (MusicPlatformException error)
        { Check(error.Code == code && error.Message == code && error.InnerException is null && !error.ToString().Contains("synthetic-secret")); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static MusicPlatformSession Account(long generation = 1, string platform = "wy", string cookie = "MUSIC_U=synthetic-secret") =>
        new(platform, "discovery-fixture", generation, "123", cookie);
    private sealed class Sessions : IMusicPlatformSessionSource
    {
        internal MusicPlatformSession? Value;
        internal int Count;
        internal bool RejectCurrent;
        internal Func<int, CancellationToken, ValueTask<MusicPlatformSession?>>? Acquire;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token)
        { Check(platform == "wy"); int n = Interlocked.Increment(ref Count); return Acquire?.Invoke(n, token) ?? ValueTask.FromResult(Value); }
        public bool IsCurrent(MusicPlatformSession session) => !RejectCurrent && ReferenceEquals(Value, session);
    }
    private sealed class Hold
    {
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal();
        internal async Task Block(CancellationToken token)
        { using var registration = token.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult(); await Release.Task; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Sessions Sessions = new();
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly MusicRecordTestStore Store = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly NativeMusicSearchDiscovery Service;
        internal Func<MusicPlatformRuntimeFixture.Call, CancellationToken, Task<JsonElement>>? Reply;
        internal readonly ConcurrentBag<Hold> Holds = [];
        internal Fixture(TimeProvider? clock = null)
        {
            Library = new(Store); Runtime.Client.BindSessions(Sessions);
            Runtime.OnRequest = (call, token) => { Check(call.Operation == "searchDiscovery"); return Reply?.Invoke(call, token) ?? Task.FromResult(Data()); };
            Service = new(Sessions, Runtime.Client, Library, clock);
        }
        internal Hold Block(string boundary)
        {
            var hold = new Hold(); Holds.Add(hold);
            if (boundary == "publication") Runtime.OnSessionUpdate = async (_, token) => { await hold.Block(token); return MusicPlatformRuntimeFixture.Json(new { published = true }); };
            else if (boundary == "metadata") Store.BeforeCommit = (_, token) => hold.Block(token);
            else Reply = async (_, token) => { await hold.Block(token); return Data(); };
            return hold;
        }
        public async ValueTask DisposeAsync()
        { foreach (var hold in Holds) hold.Release.TrySetResult(); await Service.DisposeAsync(); await Library.DisposeAsync(); await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
    }
    private static JsonElement Data(string variation = "")
    {
        int hot = variation == "empty" ? 0 : variation == "many" ? 90 : 20;
        var root = new JsonObject {
            ["hot"] = new JsonArray(Enumerable.Range(1, hot).Select(i => (JsonNode)new JsonObject { ["keyword"] = "热门 " + i, ["score"] = i }).ToArray()),
            ["suggestions"] = new JsonArray(Enumerable.Range(1, variation == "empty" ? 0 : 20).Select(i => (JsonNode)JsonValue.Create("推荐 " + i)!).ToArray()),
            ["charts"] = new JsonArray(), ["failures"] = new JsonArray() };
        if (variation != "empty")
            for (int chart = 1; chart <= 3; chart++)
            {
                int count = variation is "empty-chart" or "zero-track" ? 0 : variation == "partial-members" ? 1 : variation == "too-many-songs" ? 25 : 20;
                var songs = Enumerable.Range(1, count).Select(i => MusicSongFixture.Online("wy", (chart * 100 + i).ToString(),
                    variation == "large" ? new string('长', 4096) + i : "Track " + chart + "/" + i)).ToArray();
                if (variation is "duplicate-track" or "duplicate-song" && songs.Length > 1) songs[1] = songs[0].DeepClone().AsObject();
                if (variation == "missing-song" && songs.Length > 0) songs = songs[..^1];
                if (variation == "alien-song" && songs.Length > 0) songs[0] = MusicSongFixture.Online("wy", "unrelated-native", "Different valid track");
                root["charts"]!.AsArray().Add(new JsonObject { ["id"] = variation == "duplicate-charts" ? "1" : variation == "playlist-id" ? "different-" + chart : chart.ToString(),
                    ["name"] = "榜单 " + chart, ["cover"] = "https://img.invalid/chart.jpg?resource=" + chart, ["count"] = 100,
                    ["creator"] = "Synthetic", ["songs"] = new JsonArray(songs.Cast<JsonNode>().ToArray()) });
            }
        if (variation == "duplicates") { root["hot"]![1]!["keyword"] = "热门 1"; root["hot"]![0]!["score"] = null; }
        if (variation == "extra") { root["Cookie"] = "synthetic-secret"; root["metadata"] = new JsonObject { ["unused"] = 1 }; }
        if (variation == "score-negative") root["hot"]![0]!["score"] = -1;
        if (variation == "keyword-control") root["hot"]![0]!["keyword"] = "word\0other";
        if (variation == "keyword-limit") root["hot"]![0]!["keyword"] = new string('x', 201);
        if (variation == "hot-nonstring") root["hot"]![0]!["keyword"] = new JsonObject();
        if (variation == "default-nonstring") root["suggestions"]![0] = new JsonObject();
        return MusicPlatformRuntimeFixture.Json(root);
    }
    private static async Task Canonical()
    {
        await using var f = new Fixture(); var data = await f.Service.ReadAsync();
        Check(data.Hot.Length == 20 && data.Charts.Length == 3 && data.Charts.All(c => c.Songs.Length == 20));
        Check(data.Hot[0].Keyword == "热门 1" && data.Recommended.SequenceEqual(data.Suggestions.Take(6)));
        Check(data.Charts[0].Songs.All(s => Guid.TryParse(s.GetProperty("songId").GetString(), out _)));
        Check(f.Runtime.Requests.Count == 1 && !f.Runtime.Requests[0].Parameters.GetProperty("refresh").GetBoolean());
    }
    private static async Task Credentials()
    {
        foreach (bool authenticated in new[] { false, true })
        {
            await using var f = new Fixture(); if (authenticated) f.Sessions.Value = Account();
            var snapshot = await f.Service.ReadAsync();
            var publication = f.Runtime.Calls.Single(c => c.Method == "music.platform.session.update").Payload.GetProperty("session");
            Check(authenticated ? publication.GetProperty("cookie").GetString() == "MUSIC_U=synthetic-secret" : publication.ValueKind == JsonValueKind.Null);
            Check(!JsonSerializer.Serialize(snapshot).Contains("synthetic-secret", StringComparison.Ordinal));
        }
    }
    private static async Task Parallel()
    {
        var fixtures = Enumerable.Range(0, 16).Select(_ => new Fixture()).ToArray();
        try
        {
            var holds = fixtures.Select(f => f.Block("result")).ToArray(); var reads = fixtures.Select(f => f.Service.ReadAsync()).ToArray();
            await Wait(Task.WhenAll(holds.Select(h => h.Entered.Task)));
            Check(reads.All(t => !t.IsCompleted)); foreach (var hold in holds) hold.Release.TrySetResult();
            var result = await Task.WhenAll(reads); Check(result.All(r => r.Charts.Length == 3));
        }
        finally { foreach (var fixture in fixtures) await fixture.DisposeAsync(); }
    }
    private static async Task Content(string variation)
    {
        await using var f = new Fixture(); f.Reply = (_, _) => Task.FromResult(Data(variation));
        var data = await f.Service.ReadAsync();
        Check(data.Failures.Length == 0 && data.Charts.Length == (variation == "empty" ? 0 : 3) && f.Runtime.Requests.Count == 1);
        if (variation == "many") Check(data.Hot.Length == 90);
        if (variation is "empty-chart" or "zero-track") Check(data.Charts.All(c => c.Songs.Length == 0));
        if (variation == "too-many-songs") Check(data.Charts.All(c => c.Songs.Length == 25));
        if (variation == "duplicate-charts") Check(data.Charts.All(c => c.Id == "1"));
        if (variation is "duplicate-track" or "duplicate-song") Check(data.Charts[0].Songs[0].GetProperty("songId").GetString() == data.Charts[0].Songs[1].GetProperty("songId").GetString());
        if (variation == "duplicates") Check(data.Hot[0].Score is null && data.Hot[0].Keyword == data.Hot[1].Keyword);
        if (variation == "large") Check(data.Charts[0].Songs[0].GetProperty("title").GetString()!.Length > 4096);
    }
    private static async Task Cache()
    {
        await using var f = new Fixture(); var first = await f.Service.ReadAsync();
        first.Hot[0] = new("external edit", null); first.Suggestions[0] = "external edit"; first.Charts[0].Songs[0] = default;
        var again = await f.Service.ReadAsync(); Check(again.Hot[0].Keyword == "热门 1" && again.Charts[0].Songs[0].ValueKind == JsonValueKind.Object);
        var rotated = f.Service.Rotate(again.Generation); Check(rotated.Recommended.SequenceEqual(again.Suggestions.Skip(6).Take(6)) && f.Runtime.Requests.Count == 1);
    }
    private static async Task CacheScope(string kind)
    {
        await using var f = new Fixture(); f.Sessions.Value = Account(); var first = await f.Service.ReadAsync();
        f.Service.Invalidate("tx"); f.Service.Invalidate("kg");
        Check((await f.Service.ReadAsync()).Generation == first.Generation && f.Runtime.Requests.Count == 1);
        if (kind is "same" or "publication") { f.Runtime.Client.Invalidate("tx"); Check(f.Service.Rotate(first.Generation).Generation == first.Generation); return; }
        if (kind == "account") f.Sessions.Value = Account(2); else f.Service.Invalidate("wy");
        await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(first.Generation)));
        var next = await f.Service.ReadAsync(); Check(next.Generation != first.Generation && f.Runtime.Requests.Count == 2);
    }
    private static async Task Partial(string stage, string code = "UPSTREAM_ERROR")
    {
        await using var f = new Fixture(); var root = JsonNode.Parse(Data().GetRawText())!;
        root["failures"] = new JsonArray(new JsonObject { ["stage"] = stage, ["chartId"] = stage == "chart" ? "2" : null, ["code"] = code });
        f.Reply = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(root));
        var data = await f.Service.ReadAsync(); Check(data.Failures.Single().Stage == stage && data.Failures[0].Code == code && data.Charts.Length == 3);
    }
    private static async Task FailureCase(string code)
    {
        await using var f = new Fixture(); f.Reply = (_, _) => throw new MusicPlatformException(code);
        await Failure(code, () => f.Service.ReadAsync()); Check(f.Store.WriteCalls == 0 && f.Store.Writes == 0 && f.Runtime.Requests.Count == 1);
    }
    private static async Task FailedRefresh()
    {
        await using var f = new Fixture(); var first = await f.Service.ReadAsync(); f.Reply = (_, _) => throw new MusicPlatformException("UPSTREAM_ERROR");
        await Failure("UPSTREAM_ERROR", () => f.Service.ReadAsync(true));
        Check((await f.Service.ReadAsync()).Generation == first.Generation && f.Service.Rotate(first.Generation).Recommended.Length == 6);
    }
    private static Task Boundary(string kind) => kind switch {
        "response-limit" => FailureCase("LIMIT"), "invalid-json" => FailureCase("INVALID_RESPONSE"),
        "hot-nonstring" or "default-nonstring" => Malformed(kind), _ => Content(kind) };
    private static async Task Malformed(string kind)
    {
        await using var f = new Fixture(); f.Reply = (_, _) => Task.FromResult(Data(kind));
        await Failure("INVALID_RESPONSE", () => f.Service.ReadAsync());
    }
    private static async Task SafeFields()
    {
        await using var f = new Fixture(); f.Reply = (_, _) => Task.FromResult(Data("extra"));
        var snapshot = await f.Service.ReadAsync(); var encoded = JsonSerializer.Serialize(snapshot);
        Check(!encoded.Contains("synthetic-secret") && !encoded.Contains("Cookie") && snapshot.Charts[0].Cover.EndsWith("?resource=1"));
        Check(JsonSerializer.Deserialize<MusicSearchDiscoverySnapshot>(encoded)!.Charts[0].Songs.Length == 20);
    }
    private static async Task HandlerFailure()
    {
        await using var f = new Fixture(); f.Reply = (_, _) => throw new IOException("synthetic-secret-path");
        await Failure("INVALID_RESPONSE", () => f.Service.ReadAsync());
    }
    private static async Task Coalescing()
    {
        await using var f = new Fixture(); var hold = f.Block("result"); using var cancel = new CancellationTokenSource();
        var first = f.Service.ReadAsync(cancellationToken: cancel.Token); await Wait(hold.Entered.Task); var second = f.Service.ReadAsync();
        cancel.Cancel(); await Failure("CANCELLED", () => first);
        Check(!hold.Cancelled.Task.IsCompleted && !second.IsCompleted && f.Runtime.Requests.Count == 1);
        hold.Release.TrySetResult(); Check((await second).Charts.Length == 3 && f.Runtime.Requests.Count == 1);
    }
    private static async Task Physical(string boundary, string action)
    {
        var clock = new Clock(); await using var f = new Fixture(clock); var hold = f.Block(boundary); using var cancel = new CancellationTokenSource();
        var read = f.Service.ReadAsync(cancellationToken: cancel.Token); await Wait(hold.Entered.Task); Task? cleanup = null;
        try
        {
            if (boundary == "metadata") Check(f.Store.WriteCalls == 1 && f.Store.Writes == 0 && f.Store.SongIds().Count == 0,
                "Metadata hold must precede the actual SQLite write");
            switch (action)
            {
                case "cancel": cancel.Cancel(); break;
                case "timeout": Check(clock.DueTimes.SequenceEqual([TimeSpan.FromSeconds(25)])); clock.Advance(TimeSpan.FromSeconds(24)); Check(!hold.Cancelled.Task.IsCompleted); clock.Advance(TimeSpan.FromSeconds(1)); break;
                case "invalidate": f.Service.Invalidate("wy"); cleanup = f.Service.CancelAndDrainAsync(); break;
                case "prepare": cleanup = f.Service.CancelAndDrainAsync(); break;
                case "close": cleanup = f.Service.DisposeAsync().AsTask(); break;
            }
            if (action == "cancel") { await Failure("CANCELLED", () => read); Check(!hold.Cancelled.Task.IsCompleted); }
            else
            {
                if (boundary != "metadata") await Wait(hold.Cancelled.Task);
                await Task.Delay(30); Check(!read.IsCompleted && (cleanup is null || !cleanup.IsCompleted));
            }
            if (boundary == "metadata") Check(!hold.Cancelled.Task.IsCompleted && !f.Store.Disposed &&
                f.Store.Writes == 0 && f.Store.SongIds().Count == 0,
                "An admitted metadata commit must remain owned until physical settlement");
        }
        finally { hold.Release.TrySetResult(); }
        await Failure(action switch { "timeout" => "TIMEOUT", "invalidate" => "STALE_SESSION", "close" => "CLOSED", _ => "CANCELLED" }, () => read);
        if (cleanup is not null) await cleanup;
        if (action == "cancel") Check((await f.Service.ReadAsync()).Charts.Length == 3);
        if (boundary == "metadata") Check(f.Store.WriteCalls == 1 && f.Store.Writes == 1 && f.Store.SongIds().Count == 60,
            "Metadata must settle once without cancelling or replaying the admitted SQLite commit");
        Check(f.Runtime.Disposals == 0 && !f.Store.Disposed);
    }
    private static async Task Replacement(bool refresh)
    {
        await using var f = new Fixture(); var hold = f.Block("result"); var first = f.Service.ReadAsync(); await Wait(hold.Entered.Task);
        if (!refresh) f.Service.Invalidate(); var second = f.Service.ReadAsync(refresh);
        if (!refresh) await Wait(hold.Cancelled.Task); else Check(!hold.Cancelled.Task.IsCompleted);
        Check(!second.IsCompleted && f.Runtime.Requests.Count == 1); hold.Release.TrySetResult();
        if (refresh) Check((await first).Charts.Length == 3); else await Failure("STALE_SESSION", () => first);
        Check((await second).Charts.Length == 3 && f.Runtime.Requests.Count == (refresh ? 1 : 2));
    }
    private static async Task LastReaderReplacement()
    {
        await using var f = new Fixture(); var hold = f.Block("result"); using var cancel = new CancellationTokenSource();
        var first = f.Service.ReadAsync(cancellationToken: cancel.Token); await Wait(hold.Entered.Task); cancel.Cancel(); await Failure("CANCELLED", () => first);
        var second = f.Service.ReadAsync(); Check(!hold.Cancelled.Task.IsCompleted && !second.IsCompleted && f.Runtime.Requests.Count == 1);
        hold.Release.TrySetResult(); Check((await second).Charts.Length == 3 && f.Runtime.Requests.Count == 1);
    }
    private static async Task AnonymousChanged()
    {
        await using var f = new Fixture(); var hold = f.Block("result"); var read = f.Service.ReadAsync(); await Wait(hold.Entered.Task);
        f.Sessions.Value = Account(); f.Service.Invalidate("wy"); hold.Release.TrySetResult(); await Failure("STALE_SESSION", () => read);
    }
    private static async Task CookieChanged()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account(); var hold = f.Block("result"); var read = f.Service.ReadAsync(); await Wait(hold.Entered.Task);
        f.Sessions.Value = Account(cookie: "MUSIC_U=synthetic-second"); f.Service.Invalidate("wy"); hold.Release.TrySetResult(); await Failure("STALE_SESSION", () => read);
    }
    private static async Task Revoked()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account(); f.Reply = (_, _) => { f.Sessions.RejectCurrent = true; return Task.FromResult(Data()); };
        await Failure("STALE_SESSION", () => f.Service.ReadAsync());
    }
    private static async Task AcquireDrain()
    {
        await using var f = new Fixture(); var hold = new Hold(); f.Holds.Add(hold);
        f.Sessions.Acquire = async (_, token) => { await hold.Block(token); return null; }; using var cancel = new CancellationTokenSource();
        var read = f.Service.ReadAsync(cancellationToken: cancel.Token); await Wait(hold.Entered.Task); cancel.Cancel();
        await Failure("CANCELLED", () => read); Check(!hold.Cancelled.Task.IsCompleted && f.Runtime.Requests.Count == 0);
        var drain = f.Service.CancelAndDrainAsync(); Check(!drain.IsCompleted); hold.Release.TrySetResult(); await drain;
        f.Sessions.Acquire = null; Check((await f.Service.ReadAsync()).Charts.Length == 3);
    }
    private static async Task AcquireError()
    {
        await using var f = new Fixture(); f.Sessions.Acquire = (_, _) => throw new IOException("synthetic-secret");
        await Failure("CREDENTIALS_UNAVAILABLE", () => f.Service.ReadAsync()); Check(f.Runtime.Requests.Count == 0);
    }
    private static async Task InvalidSession()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account(platform: "tx"); await Failure("STALE_SESSION", () => f.Service.ReadAsync());
        f.Sessions.Value = Account(); f.Sessions.RejectCurrent = true; await Failure("STALE_SESSION", () => f.Service.ReadAsync()); Check(f.Runtime.Requests.Count == 0);
    }
    private static async Task PreCancelled()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Failure("CANCELLED", () => f.Service.ReadAsync(cancellationToken: cancel.Token)); Check(f.Sessions.Count == 0 && f.Runtime.Requests.Count == 0);
    }
    private static async Task PrepareAndClose()
    {
        await using var f = new Fixture(); var saved = await f.Service.ReadAsync(); await f.Service.CancelAndDrainAsync();
        Check((await f.Service.ReadAsync()).Generation == saved.Generation && f.Runtime.Requests.Count == 1);
        var first = f.Service.DisposeAsync().AsTask(); var second = f.Service.DisposeAsync().AsTask(); Check(ReferenceEquals(first, second)); await first;
        await Failure("CLOSED", () => f.Service.ReadAsync()); await Failure("CLOSED", () => Task.FromResult(f.Service.Rotate(saved.Generation)));
        f.Service.Invalidate(); await f.Service.CancelAndDrainAsync(); Check(f.Runtime.Disposals == 0 && !f.Store.Disposed);
    }
    private static async Task RotateFence()
    {
        await using var f = new Fixture(); await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(0)));
        var data = await f.Service.ReadAsync(); await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(data.Generation + 1)));
        f.Service.Invalidate(); await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(data.Generation))); Check(f.Runtime.Requests.Count == 1);
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        internal TimeSpan[] DueTimes { get { lock (gate) return timers.Select(t => t.Initial).ToArray(); } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Check(period == Timeout.InfiniteTimeSpan, "Deadline is not a poller");
            lock (gate) { var timer = new Timer(this, callback, state, dueTime); timers.Add(timer); return timer; }
        }
        internal void Advance(TimeSpan delta)
        {
            Timer[] due;
            lock (gate) { now += delta; due = timers.Where(t => !t.Disposed && t.At <= now).ToArray(); foreach (var timer in due) timer.At = DateTimeOffset.MaxValue; }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal readonly TimeSpan Initial = due;
            internal DateTimeOffset At = owner.now + due;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { if (Disposed) return false; At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; return true; } }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
