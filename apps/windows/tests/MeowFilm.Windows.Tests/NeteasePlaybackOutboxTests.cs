using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class NeteasePlaybackOutboxTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("pre-cancellation and malformed local facts cannot create a pending event", async () =>
        {
            await using var f = await Fixture.Create();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await f.Protocol.StartAsync(f.Accounts.Value!, f.NewStart(), cancelled.Token);
                throw new InvalidOperationException("pre-cancel accepted");
            }
            catch (OperationCanceledException) { }
            var original = f.NewStart();
            foreach (var invalid in new[]
            {
                original with { Duration = double.NaN }, original with { Duration = -1 },
                original with { Position = -1 }, original with { Duration = 10, Position = 11 },
                original with { Rate = double.PositiveInfinity }, original with { Rate = 0 },
                original with { PlaybackId = Guid.Empty }, original with { AdmittedAt = default },
                original with { Recording = new("tx", "MID") }, original with { Algorithm = "\ud800" }
            })
            {
                try { await f.Protocol.StartAsync(f.Accounts.Value!, invalid); }
                catch (MusicFeedbackException) { continue; }
                throw new InvalidOperationException("malformed facts accepted");
            }
            Check(f.Store.OutboxWrites == 0 && f.Remote.Uploads.Count == 0);
        });
        yield return Case("attempt duplicate returns same local session and paired end stays single-shot", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Offline = true;
            var input = f.NewStart();
            var first = f.Protocol.StartAsync(f.Accounts.Value!, input);
            var second = f.Protocol.StartAsync(f.Accounts.Value!, input);
            await using var session = await first;
            Check(ReferenceEquals(session, await second));
            await f.Protocol.DrainAsync();
            Check(f.Saved().Single().Count == 1);
            await Error(() => f.Protocol.StartAsync(f.Accounts.Value!, input with { Algorithm = "changed" }), "INSTANCE_MISMATCH");
            f.Clock.Advance(10);
            var end = new MusicFeedbackEnd(10, 10, 0, 10, 1, "ui")
            { PlaybackId = input.PlaybackId, EndedAt = f.Clock.GetUtcNow() };
            await session.FinishAsync(end); await session.FinishAsync(end);
            await Error(() => session.FinishAsync(end with { Listened = 11 }), "INSTANCE_MISMATCH");
            await f.Protocol.DrainAsync();
            Check(f.Saved().Single().Count == 2 && f.Remote.Uploads.Count == 0);
        });
        yield return Case("WY long unknown-duration high-rate facts use occurrence time not dispatch elapsed", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Offline = true;
            var input = f.NewStart() with { Duration = 90000, Rate = 32,
                StartedAt = f.Clock.GetUtcNow().AddSeconds(-100), AdmittedAt = f.Clock.GetUtcNow().AddSeconds(-100) };
            await using var session = await f.Protocol.StartAsync(f.Accounts.Value!, input);
            await session.FinishAsync(new(100, 3200, 0, 3200, 32, "ui")
            { PlaybackId = input.PlaybackId, EndedAt = f.Clock.GetUtcNow() });
            await f.Protocol.DrainAsync();
            Check(f.Saved().Single().End is { Time: 3200, Realtime: 100 });
        });
        yield return Case("retired platform attempt cannot erase a readmitted pair of the same listening occurrence", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Offline = true;
            using var oldLifetime = new CancellationTokenSource();
            var input = f.NewStart();
            await using var old = await f.Protocol.StartAsync(f.Accounts.Value!, input, oldLifetime.Token);
            await f.Protocol.DrainAsync();
            f.Accounts.Value = MakeAccount(Account() with { Scope = "synthetic-replacement", UserId = "124" });
            f.Controller.AccountChanged("wy");
            await using var next = await f.Protocol.StartAsync(f.Accounts.Value!, input with { Attempt = new() });
            await oldLifetime.CancelAsync();
            await old.DisposeAsync(); await f.Protocol.DrainAsync();
            f.Clock.Advance(3);
            await next.FinishAsync(new(3, 3, 0, 3, 1, "ui")
                { PlaybackId = input.PlaybackId, EndedAt = f.Clock.GetUtcNow() });
            await f.Protocol.DrainAsync();
            var saved = f.Saved().Single();
            Check(saved.Account.UserId == "124" && saved.Start is { Inflight: false } &&
                saved.End is { Time: 3, Realtime: 3 });
        });
        yield return Case("UTF8 event beyond thirty two KiB persists complete unsent facts", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Offline = true;
            var input = f.NewStart() with { Algorithm = new string('界', 12000) };
            await using var session = await f.Protocol.StartAsync(f.Accounts.Value!, input);
            await f.Protocol.DrainAsync();
            Check(f.Store.OutboxWrites > 0 && f.Remote.Uploads.Count == 0 &&
                f.Saved().Single().Algorithm == input.Algorithm && f.Saved().Single().Start is { Inflight: false });
        });
        yield return Case("local admission is not remote ACK and safe unsent start-end recover in order", async () =>
        {
            await using var f = await Fixture.Create();
            f.Remote.Offline = true;
            await using var session = await f.Start();
            await f.Protocol.DrainAsync();
            Check(f.Remote.Uploads.Count == 0 && f.Saved().Single().Start is { Inflight: false });
            f.Clock.Advance(10);
            await session.FinishAsync(f.End());
            await f.Protocol.DrainAsync();
            Check(f.Saved().Single() is { Start: { Inflight: false }, End: { Inflight: false } });
            string saved = Encoding.UTF8.GetString(f.Store.Get(NeteasePlaybackOutboxDocument.Namespace)!);
            Check(!saved.Contains("MUSIC_U", StringComparison.Ordinal) && !saved.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
                !saved.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
            f.Remote.Offline = false;
            await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Check(f.Remote.Uploads.Count == 2 && f.Saved().Length == 0);
        });
        yield return Case("cold startup restores only original account and preserved consent", async () =>
        {
            var store = new Store();
            await using (var first = await Fixture.Create(store))
            {
                first.Remote.Offline = true;
                await using var session = await first.Start();
                first.Clock.Advance(10); await session.FinishAsync(first.End());
                await first.Protocol.DrainAsync();
                Check(first.Saved().Length == 1);
            }
            await using var next = await Fixture.Create(store);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 2 && next.Saved().Length == 0);
        });
        yield return Case("account-ready before preferences load cannot mistake unknown consent for disabled", async () =>
        {
            var store = new Store();
            await using (var first = await Fixture.Create(store))
            {
                first.Remote.Offline = true;
                await using var session = await first.Start(); first.Clock.Advance(10);
                await session.FinishAsync(first.End()); await first.Protocol.DrainAsync();
            }
            await using var next = Fixture.BeforePreferences(store);
            next.Controller.AccountChanged("wy");
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 2 && next.Saved().Length == 0);
        });
        yield return Case("unrelated platform consent update preserves the admitted WY pair", async () =>
        {
            await using var f = await Fixture.Create();
            f.Remote.Offline = true;
            await using var session = await f.Start(); await f.Protocol.DrainAsync();
            var old = await f.Controller.ReadSettingsAsync();
            await f.Controller.SaveSettingsAsync(old.Revision, ["wy", "tx"]);
            await f.Protocol.DrainAsync();
            f.Clock.Advance(10); await session.FinishAsync(f.End());
            await f.Protocol.DrainAsync();
            Check(f.Saved().Single().ConsentRevision == old.Revision + 1);
            f.Remote.Offline = false;
            await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Check(f.Remote.Uploads.Count == 2 && f.Saved().Length == 0);
        });
        yield return Case("real end during unrelated consent CAS is durable before the settings receipt", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Offline = true;
            await using var session = await f.Start(); await f.Protocol.DrainAsync();
            var old = await f.Controller.ReadSettingsAsync();
            f.Store.HoldSettings = true;
            var save = f.Controller.SaveSettingsAsync(old.Revision, ["wy", "tx"]);
            try
            {
                await f.Store.SettingsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Clock.Advance(10); await session.FinishAsync(f.End());
                Check(!save.IsCompleted && f.Saved().Single().End is { Time: 10, Realtime: 10, Inflight: false });
            }
            finally { f.Store.SettingsRelease.TrySetResult(); }
            await save; await f.Protocol.DrainAsync();
            Check(f.Saved().Single().ConsentRevision == old.Revision + 1);
            f.Remote.Offline = false;
            await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Check(f.Remote.Uploads.Count == 2 && f.Saved().Length == 0);
        });
        yield return Case("already qualified local start can settle during unrelated consent CAS", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Offline = true;
            var old = await f.Controller.ReadSettingsAsync();
            f.Store.HoldSettings = true;
            var save = f.Controller.SaveSettingsAsync(old.Revision, ["wy", "tx"]);
            IMusicFeedbackSession? session = null;
            try
            {
                await f.Store.SettingsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                session = await f.Start();
                f.Clock.Advance(10); await session.FinishAsync(f.End());
                Check(!save.IsCompleted && f.Saved().Single().Count == 2 && f.Remote.Uploads.Count == 0);
            }
            finally { f.Store.SettingsRelease.TrySetResult(); }
            await save; await f.Protocol.DrainAsync();
            Check(f.Saved().Single().ConsentRevision == old.Revision + 1);
            if (session is not null) await session.DisposeAsync();
            f.Remote.Offline = false;
            await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Check(f.Remote.Uploads.Count == 2 && f.Saved().Length == 0);
        });
        yield return Case("unready startup account defers safely until an account-ready event", async () =>
        {
            var store = new Store();
            await using (var first = await Fixture.Create(store))
            {
                first.Remote.Offline = true;
                await using var session = await first.Start(); first.Clock.Advance(10); await session.FinishAsync(first.End());
                await first.Protocol.DrainAsync();
            }
            await using var next = await Fixture.Create(store, accountReady: false);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Saved().Length == 1 && next.Remote.Uploads.Count == 0);
            next.Accounts.Value = MakeAccount(Account()); next.Controller.AccountChanged("wy");
            await next.Protocol.DrainAsync();
            Check(next.Saved().Length == 0 && next.Remote.Uploads.Count == 2);
        });
        yield return Case("pairing survives a confirmed start and a later safe-unsent end", async () =>
        {
            var store = new Store();
            await using (var first = await Fixture.Create(store))
            {
                await using var session = await first.Start(); await first.Protocol.DrainAsync();
                Check(first.Remote.Uploads.Count == 1 && first.Saved().Length == 0);
                first.Remote.Offline = true; first.Clock.Advance(10);
                await session.FinishAsync(first.End()); await first.Protocol.DrainAsync();
                Check(first.Saved().Single() is { StartConfirmed: true, Start: null, End: { Inflight: false } });
            }
            await using var next = await Fixture.Create(store);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 1 && next.Saved().Length == 0);
        });
        yield return Case("invalid current outbox fields are not overwritten or silently treated as empty", async () =>
        {
            var store = new Store();
            byte[] invalid = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, playbacks = Array.Empty<object>() });
            store.Set(NeteasePlaybackOutboxDocument.Namespace, invalid);
            await using var f = await Fixture.Create(store);
            f.ExpectedStorageFailure = true;
            await Error(() => f.Protocol.InitializeAsync(), "FEEDBACK_STORAGE_FAILED");
            Check(JsonNode.DeepEquals(JsonNode.Parse(store.Get(NeteasePlaybackOutboxDocument.Namespace)!),JsonNode.Parse(invalid)) && f.Remote.Uploads.Count == 0);
            await Error(async () => { _ = await f.Start(); }, "FEEDBACK_STORAGE_FAILED");
            Check(JsonNode.DeepEquals(JsonNode.Parse(store.Get(NeteasePlaybackOutboxDocument.Namespace)!),JsonNode.Parse(invalid)));
        });
        foreach (string change in new[] { "account", "scope", "generation", "disabled" })
            yield return Case("cold startup discards pending after " + change, async () =>
            {
                var store = new Store();
                await using (var first = await Fixture.Create(store))
                {
                    first.Remote.Offline = true;
                    await using var session = await first.Start();
                    first.Clock.Advance(10); await session.FinishAsync(first.End());
                    await first.Protocol.DrainAsync();
                }
                var account = Account() with { UserId = change == "account" ? "124" : "123",
                    Scope = change == "scope" ? "other-login" : "synthetic-login", Generation = change == "generation" ? 2 : 1 };
                if (change == "disabled")
                {
                    await using var preferences = new MusicFeedbackSettingsOwner(store);
                    var old = await preferences.ReadAsync(); await preferences.SaveAsync(old.Revision, []);
                }
                await using var next = await Fixture.Create(store, account);
                await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
                Check(next.Remote.Uploads.Count == 0 && next.Saved().Length == 0);
            });
        foreach (string change in new[] { "disable", "logout", "account-change" })
            yield return Case("live " + change + " cancels and clears pending without transfer", async () =>
            {
                await using var f = await Fixture.Create();
                f.Remote.Offline = true;
                await using var session = await f.Start();
                await f.Protocol.DrainAsync();
                if (change == "disable")
                {
                    var old = await f.Controller.ReadSettingsAsync();
                    await f.Controller.SaveSettingsAsync(old.Revision, []);
                }
                else
                {
                    f.Accounts.Value = change == "logout" ? null : MakeAccount(Account() with { UserId = "124" });
                    f.Controller.AccountChanged("wy");
                }
                await f.Protocol.DrainAsync();
                Check(f.Saved().Length == 0 && f.Remote.Uploads.Count == 0);
            });
        yield return Case("revocation during noncooperative upload drains physical work and settles late ACK", async () =>
        {
            await using var f = await Fixture.Create();
            f.Remote.Hold = true;
            await using var session = await f.Start();
            await f.Remote.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Saved().Single().Start is { Inflight: true });
            var old = await f.Controller.ReadSettingsAsync();
            await f.Controller.SaveSettingsAsync(old.Revision, []);
            Check(!f.Remote.Cancelled.Task.IsCompleted, "Dispatched upload must settle its real receipt");
            var drain = f.Protocol.DrainAsync();
            Check(!drain.IsCompleted);
            f.Remote.Release.TrySetResult(); await drain;
            Check(f.Saved().Length == 0 && f.Remote.Uploads.Count == 1);
        });
        yield return Case("normal exit freezes local end without revocation and startup can recover safe unsent", async () =>
        {
            var store = new Store();
            await using (var f = await Fixture.Create(store))
            {
                f.Remote.OfflineAfterQualification = true;
                f.Controller.Select(new("entry", MusicSongFixture.Element(MusicSongFixture.Online("wy", "42", "song", "artist", "", 200))));
                f.Controller.Started("entry", 0, 0); await f.Controller.DrainAsync(); await f.Protocol.DrainAsync();
                f.Clock.Advance(9); f.Controller.Observe("entry", 9, 0, true);
                await f.Controller.PrepareShutdownAsync();
                Check(f.Saved().Single().End is { Time: 9, Realtime: 9, Reason: "interrupt", Inflight: false });
            }
            await using var next = await Fixture.Create(store);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 2 && next.Saved().Length == 0);
        });
        yield return Case("controller close persists no-replay outcome without waiting for upload ACK", async () =>
        {
            await using var f = await Fixture.Create(); f.Remote.Hold = true;
            f.Controller.Select(new("entry", MusicSongFixture.Element(
                MusicSongFixture.Online("wy", "42", "song", "artist", "", 200))));
            f.Controller.Started("entry", 0, 0);
            try
            {
                await f.Remote.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Clock.Advance(9); f.Controller.Observe("entry", 9, 0, true);
                await f.Controller.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(!f.Remote.Cancelled.Task.IsCompleted && f.Saved().Length == 0);
                Check(!f.Protocol.DrainAsync().IsCompleted);
            }
            finally { f.Remote.Release.TrySetResult(); }
            await f.Protocol.DrainAsync();
            Check(f.Remote.Uploads.Count == 1 && f.Saved().Length == 0);
        });
        yield return Case("inflight from crash is dropped with paired end and never replayed", async () =>
        {
            var store = new Store();
            await using (var f = await Fixture.Create(store))
            {
                f.Remote.Offline = true;
                await using var session = await f.Start(); f.Clock.Advance(10); await session.FinishAsync(f.End());
                await f.Protocol.DrainAsync();
                var value = f.Saved().Single();
                store.Set(NeteasePlaybackOutboxDocument.Namespace,
                    NeteasePlaybackOutboxDocument.Encode([value with { Start = value.Start! with { Inflight = true } }]));
            }
            await using var next = await Fixture.Create(store);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 0 && next.Saved().Length == 0);
        });
        foreach (string code in new[] { "NETWORK_ERROR", "TIMEOUT", "UPSTREAM_UNCONFIRMED", "UPSTREAM_ERROR", "REDIRECT_DENIED" })
            yield return Case("unknown upload " + code + " is not replayed nor followed by orphan end", async () =>
            {
                var store = new Store();
                await using (var f = await Fixture.Create(store))
                {
                    f.Remote.UploadErrorCode = code;
                    await using var session = await f.Start(); await f.Protocol.DrainAsync();
                    Check(f.Remote.Uploads.Count == 1 && f.Saved().Length == 0);
                    await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
                    Check(f.Remote.Uploads.Count == 1);
                    f.Clock.Advance(10);
                    await Error(() => session.FinishAsync(f.End()), "START_UNCONFIRMED");
                }
                await using var next = await Fixture.Create(store);
                await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
                Check(next.Remote.Uploads.Count == 0);
            });
        foreach (string code in new[] { "NETWORK_ERROR", "TIMEOUT", "UPSTREAM_UNCONFIRMED", "UPSTREAM_ERROR", "REDIRECT_DENIED" })
            yield return Case("start ACK followed by unknown end " + code + " never replays on cold startup", async () =>
            {
                var store = new Store();
                await using (var f = await Fixture.Create(store))
                {
                    await using var session = await f.Start(); await f.Protocol.DrainAsync();
                    Check(f.Remote.Uploads.Count == 1 && f.Saved().Length == 0);
                    f.Remote.UploadErrorCode = code; f.Clock.Advance(10);
                    await session.FinishAsync(f.End()); await f.Protocol.DrainAsync();
                    Check(f.Remote.Uploads.Count == 2 && f.Saved().Length == 0);
                    await session.FinishAsync(f.End());
                    await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
                    Check(f.Remote.Uploads.Count == 2);
                }
                await using var next = await Fixture.Create(store);
                await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
                Check(next.Remote.Uploads.Count == 0 && next.Saved().Length == 0);
            });
        yield return Case("outbox retains more than two hundred fifty six events until time retention expires", async () =>
        {
            await using var f = await Fixture.Create();
            f.Remote.Offline = true;
            for (int i = 0; i < 130; i++)
            {
                await using var session = await f.Start();
                f.Clock.Advance(1); await session.FinishAsync(f.End(1));
                await f.Protocol.DrainAsync();
            }
            var saved = f.Saved();
            Check(saved.Length == 130 && saved.Sum(e => e.Count) == 260 &&
                saved.All(e => e.Start is not null && e.End is not null));
            f.Clock.Advance(8 * 24 * 60 * 60); f.Remote.Offline = false;
            await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Check(f.Saved().Length == 0 && f.Remote.Uploads.Count == 0);
        });
        foreach (int stage in new[] { 1, 2, 3 })
            yield return Case("durable write failure at stage " + stage + " never guesses a receipt or retries", async () =>
            {
                await using var f = await Fixture.Create();
                f.Store.FailAtOutboxWrite = stage;
                f.ExpectedStorageFailure = true;
                if (stage == 1) await Error(async () => { _ = await f.Start(); }, "FEEDBACK_STORAGE_FAILED");
                else
                {
                    await using var session = await f.Start(); await f.Protocol.DrainAsync();
                    Check(f.Remote.Uploads.Count == (stage == 2 ? 0 : 1));
                }
                await Error(() => f.Controller.PrepareShutdownAsync(), "FEEDBACK_STORAGE_FAILED");
                int calls = f.Remote.Uploads.Count;
                await Error(() => f.Protocol.InitializeAsync(), "FEEDBACK_STORAGE_FAILED"); await f.Protocol.DrainAsync();
                Check(f.Remote.Uploads.Count == calls);
            });
        yield return Case("lost inflight write receipt never sends and cold restart does not replay it", async () =>
        {
            var store = new Store();
            await using (var f = await Fixture.Create(store))
            {
                store.LoseAtOutboxWrite = 2; f.ExpectedStorageFailure = true;
                await using var session = await f.Start(); await f.Protocol.DrainAsync();
                Check(f.Remote.Uploads.Count == 0 && f.Saved().Single().Start is { Inflight: true });
            }
            await using var next = await Fixture.Create(store);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 0 && next.Saved().Length == 0);
        });
        yield return Case("disabled then enabled cannot recover old consent after a failed cleanup", async () =>
        {
            var store = new Store();
            await using (var f = await Fixture.Create(store))
            {
                f.Remote.Offline = true;
                await using var session = await f.Start(); await f.Protocol.DrainAsync();
                store.FailAtOutboxWrite = store.OutboxWrites + 1; f.ExpectedStorageFailure = true;
                var old = await f.Controller.ReadSettingsAsync();
                await f.Controller.SaveSettingsAsync(old.Revision, []);
                await f.Protocol.DrainAsync();
            }
            await using (var settings = new MusicFeedbackSettingsOwner(store))
            {
                var old = await settings.ReadAsync(); await settings.SaveAsync(old.Revision, ["wy"]);
            }
            await using var next = await Fixture.Create(store);
            await next.Protocol.InitializeAsync(); await next.Protocol.DrainAsync();
            Check(next.Remote.Uploads.Count == 0 && next.Saved().Length == 0);
        });
    }

    private static TestCase Case(string name, Func<Task> run) => new("netease-outbox: " + name, run);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("outbox assertion"); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Error(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (MusicFeedbackException error) when (error.Code == expected) { return; }
        throw new InvalidOperationException("Expected " + expected);
    }
    private sealed record AccountFacts(string Scope, long Generation, string UserId);
    private static AccountFacts Account() => new("synthetic-login", 1, "123");
    private static MusicPlatformSession MakeAccount(AccountFacts value) =>
        new("wy", value.Scope, value.Generation, value.UserId, "MUSIC_U=SYNTHETIC_SECRET; __csrf=synthetic");

    private sealed class Accounts(MusicPlatformSession? value) : IMusicPlatformSessionSource
    {
        internal MusicPlatformSession? Value = value;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Value); }
        public bool IsCurrent(MusicPlatformSession account) => ReferenceEquals(account, Value);
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1800000000).AddMilliseconds(ticks);
        internal void Advance(double seconds) => ticks += (long)(seconds * 1000);
    }
    private sealed class Store : NativeTestDatabase
    {
        internal int FailAtOutboxWrite,LoseAtOutboxWrite,OutboxWrites;
        internal bool HoldSettings;
        internal readonly TaskCompletionSource SettingsEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SettingsRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static RelationalRecord Record(string owner)=>owner==NeteasePlaybackOutboxDocument.Namespace?FeedbackStorageRecords.Outbox:
            owner==MusicFeedbackSettingsOwner.DocumentNamespace?FeedbackStorageRecords.Settings:throw new InvalidOperationException();
        internal byte[]? Get(string owner){var row=Project(Record(owner),"device");return row is null?null:JsonSerializer.SerializeToUtf8Bytes(row);}
        internal void Set(string owner,byte[] value)=>Seed(Record(owner),JsonNode.Parse(value)!.AsObject(),"device");
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession,T> write,CancellationToken token=default,
            int retainedBytes=0,Action<Action>? commit=null)
        {
            bool outbox=false;
            var result=await base.WriteAsync(db=>{
                var settings=db.Revision(MusicFeedbackSettingsOwner.DocumentNamespace);var before=db.Revision(NeteasePlaybackOutboxDocument.Namespace);
                var value=write(db);outbox=before!=db.Revision(NeteasePlaybackOutboxDocument.Namespace);
                if(outbox&&++OutboxWrites==FailAtOutboxWrite)throw new DocumentStoreException(DocumentStoreError.Storage);
                if(HoldSettings&&settings!=db.Revision(MusicFeedbackSettingsOwner.DocumentNamespace))
                {HoldSettings=false;SettingsEntered.TrySetResult();SettingsRelease.Task.GetAwaiter().GetResult();}
                return value;
            },token,retainedBytes,commit);
            if(outbox&&OutboxWrites==LoseAtOutboxWrite)throw new DocumentStoreException(DocumentStoreError.Storage);
            return result;
        }
    }
    private sealed class Peer
    {
        internal bool Offline, Hold, OfflineAfterQualification;
        internal string? UploadErrorCode;
        internal readonly List<JsonElement> Uploads = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            if (call.Operation == "feedback.qualify")
            {
                if (Offline) throw new MusicPlatformException("NETWORK_ERROR");
                Offline = OfflineAfterQualification;
                return MusicPlatformRuntimeFixture.Json(new { qualified = true });
            }
            Check(call.Operation == "feedback.upload");
            var facts = call.Parameters.GetProperty("events"); Check(facts.GetArrayLength() == 1);
            Uploads.Add(facts[0].Clone()); Entered.TrySetResult();
            using var cancellation = token.Register(() => Cancelled.TrySetResult());
            if (Hold) await Release.Task;
            return MusicPlatformRuntimeFixture.Json(new { status = UploadErrorCode is null ? "confirmed" : "unknown", errorCode = UploadErrorCode });
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store;
        internal readonly Clock Clock = new();
        internal readonly Peer Remote = new();
        internal readonly Accounts Accounts;
        internal readonly MusicFeedbackSettingsOwner Settings;
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly NativeMusicFeedbackProtocol Protocol;
        internal readonly MusicFeedbackController Controller;
        private MusicFeedbackStart last = null!;
        internal bool ExpectedStorageFailure;
        private Fixture(Store store, AccountFacts account)
        {
            Store = store; Accounts = new(MakeAccount(account)); Settings = new(store);
            Runtime.OnRequest = Remote.Respond; Runtime.Client.BindSessions(Accounts);
            Protocol = new(Accounts, Runtime.Client, Clock, store, Settings);
            Controller = new(Settings, Protocol, Accounts, Clock);
        }
        internal static async Task<Fixture> Create(Store? store = null, AccountFacts? account = null, bool accountReady = true)
        {
            var value = new Fixture(store ?? new(), account ?? Account());
            if (!accountReady) value.Accounts.Value = null;
            var settings = await value.Controller.ReadSettingsAsync();
            if (!storeHasSettings(value.Store) && !settings.Enabled.Contains("wy"))
                await value.Controller.SaveSettingsAsync(settings.Revision, ["wy"]);
            await value.Protocol.DrainAsync();
            return value;
            static bool storeHasSettings(Store store) => store.Get(MusicFeedbackSettingsOwner.DocumentNamespace) is not null;
        }
        internal static Fixture BeforePreferences(Store store) => new(store, Account());
        internal Task<IMusicFeedbackSession> Start()
        {
            last = NewStart();
            return Protocol.StartAsync(Accounts.Value!, last);
        }
        internal MusicFeedbackStart NewStart() => new(new(), new("wy", "42"), 0, 0, 1, false)
        { PlaybackId = Guid.NewGuid(), StartedAt = Clock.GetUtcNow(), AdmittedAt = Clock.GetUtcNow() };
        internal MusicFeedbackEnd End(double heard = 10) => new(heard, heard, 0, heard, 1, "ui")
        { PlaybackId = last.PlaybackId, EndedAt = Clock.GetUtcNow() };
        internal NeteasePlaybackPending[] Saved() => NeteasePlaybackOutboxDocument.Decode(
            Store.ReadRecordAsync(NeteasePlaybackOutboxDocument.Namespace, MeowFilm.Capabilities.Feedback.FeedbackStorageRecords.Outbox).GetAwaiter().GetResult());
        public async ValueTask DisposeAsync()
        {
            Remote.Release.TrySetResult();
            Store.SettingsRelease.TrySetResult();
            try { await Controller.DisposeAsync(); }
            catch (MusicFeedbackException error) when (ExpectedStorageFailure && error.Code == "FEEDBACK_STORAGE_FAILED") { }
            finally { await Runtime.DisposeAsync(); }
        }
    }
}
