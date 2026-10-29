// SPDX-License-Identifier: GPL-3.0-only
// Real sole account owner with a synthetic, deliberately non-cooperative store.
// No authentication, network request, DPAPI/account write, or global-close workaround.
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicDiscoveryAccountDrainTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("internal-management-command-is-not-remotely-callable", Contracts);
        yield return Case("restore-anonymous-one-read-no-login-or-write", Anonymous);
        yield return Case("saved-restore-and-native-session-same-owner-no-verification", Saved);
        yield return Case("initial-observation-waiters-share-and-cancel-independently", InitialObservationWaiters);
        yield return Case("initial-observation-last-cancel-signals-and-settles-physical-read", InitialObservationLastCancel);
        yield return Case("initial-observation-close-drains-before-physical-admission", InitialObservationCloseBeforeAdmission);
        yield return Case("initial-observation-close-cancels-and-drains-physical-read", InitialObservationClose);
        foreach (bool hydration in new[] { true, false })
        {
            bool restore = hydration; string kind = restore ? "restore" : "acquire";
            yield return Case(kind + "-cancel-waits-only-own-physical-read-new-request-queues", () => CancelThenRead(restore));
            yield return Case(kind + "-cancel-late-storage-fault-settles-redacted", () => CancelThenRead(restore, lateFault: true));
            yield return Case(kind + "-queued-cancel-does-not-wait-unrelated-active-read", () => QueuedCancellation(restore));
            yield return Case(kind + "-completion-not-a-global-barrier", () => NotGlobalBarrier(restore));
            yield return Case(kind + "-callback-completes-before-return", () => Notifications(restore, cancel: false));
            yield return Case(kind + "-cancel-during-callback-awaits-and-finishes-notifications", () => Notifications(restore, cancel: true));
            yield return Case(kind + "-subscriber-error-redacted-next-observer-runs", () => ObserverError(restore));
            yield return Case(kind + "-pre-cancelled-no-storage", () => PreCancelled(restore));
            yield return Case(kind + "-corrupt-storage-finite-error", () => ReadError(restore, corrupt: true));
            yield return Case(kind + "-storage-error-finite-error", () => ReadError(restore, corrupt: false));
            yield return Case(kind + "-ordinary-public-early-cancel-contract", () => OrdinaryRead(restore));
            yield return Case(kind + "-same-admission-limit-no-retry-or-extra-read", () => Admission(restore));
        }
        yield return Case("reconcile-anonymous-account-tombstone-invalidates-old-lease", Reconcile);
        yield return Case("acquire-invalid-platform-no-storage", InvalidPlatform);
        yield return Case("acquire-revoked-owner-no-recovery-or-write", Revoked);
        yield return Case("settled-reads-do-not-close-or-dispose-borrowed-store", RemainsOpen);
        yield return Case("cancelled-read-returned-secret-buffer-still-zeroed", BufferOwnership);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.discovery.accounts.drain." + name, run);
    private static void Check(bool condition, string message = "Discovery account drain assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private sealed record Result(JsonElement? List = null, MusicPlatformSession? Session = null);
    private static async Task<Result> Read(Fixture f, bool restore, CancellationToken token = default, bool ordinary = false) =>
        restore ? new(List: ordinary
            ? await f.Owner.CallAsync("music.accounts.list", new { }, token)
            : await f.Owner.RestoreForDiscoveryAsync(token)) :
        new(Session: ordinary ? await f.Owner.AcquireAsync("wy", token) : await f.Owner.AcquireSettledAsync("wy", token));
    private static void Success(Result result, bool restore)
    {
        if (restore)
            Check(result.List is { } list && list.GetProperty("ok").GetBoolean() &&
                list.GetProperty("version").GetInt32() == 1 && list.GetProperty("items").GetArrayLength() == 3);
        else Check(result.Session is { Platform: "wy", UserId: "123" });
    }
    private static async Task Failure(Task<Result> pending, bool restore, string code, CancellationToken token = default)
    {
        try
        {
            var result = await pending;
            Check(restore && result.List is { } list && !list.GetProperty("ok").GetBoolean() &&
                list.GetProperty("code").GetString() == code, "Expected " + code);
            Check(!JsonSerializer.Serialize(result.List).Contains("synthetic-secret", StringComparison.Ordinal), "Leaked error");
        }
        catch (OperationCanceledException error) when (!restore && code == "CANCELLED")
        { Check(error.CancellationToken == token, "Cancellation token identity"); }
        catch (MusicAccountException error) when (!restore)
        {
            Check(error.Code == code && error.InnerException is null && error.Message == code &&
                error.ToString() == "MusicAccountException: " + code, "Wrong or unsafe account failure");
        }
    }
    private static JsonElement Row(JsonElement list) => list.GetProperty("items").EnumerateArray()
        .Single(row => row.GetProperty("platform").GetString() == "wy");
    private static byte[] Document(long generation = 1, bool anonymous = false) => JsonSerializer.SerializeToUtf8Bytes(
        new JsonObject { ["version"] = 1, ["entries"] = new JsonArray(new JsonObject
        {
            ["key"] = "[\"meowfilm\",\"music\",1,\"platform-account\",\"wy\"]",
            ["value"] = new JsonObject
            {
                ["version"] = 1, ["generation"] = generation,
                ["cookie"] = anonymous ? null : "MUSIC_U=synthetic-secret-" + generation,
                ["scope"] = anonymous ? null : "synthetic-scope-" + generation,
                ["validatedAt"] = anonymous ? null : 1000,
                ["profile"] = anonymous ? null : new JsonObject
                    { ["platform"] = "wy", ["userId"] = "123", ["displayName"] = "Saved fixture" }
            }
        }, new JsonObject
        {
            ["key"] = "[\"meowfilm\",\"music\",1,\"platform-account\",\"tx\"]",
            ["value"] = new JsonObject
            {
                ["version"] = 1, ["generation"] = 1, ["cookie"] = null,
                ["profile"] = null, ["scope"] = null, ["validatedAt"] = null
            }
        }) });

    private sealed class Hold
    {
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal(), Settled = Signal();
        internal bool Fault;
    }
    private sealed class HoldingSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        internal int Pending { get { lock (callbacks) return callbacks.Count; } }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (callbacks) callbacks.Enqueue((callback, state));
        }
        internal void RunAll()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (callbacks)
                {
                    if (callbacks.Count == 0) return;
                    next = callbacks.Dequeue();
                }
                next.Callback(next.State);
            }
        }
    }
    private sealed class Store : IProtectedDocumentStore
    {
        internal byte[]? Data = Document();
        internal readonly ConcurrentDictionary<int, Hold> Holds = new();
        internal readonly ConcurrentBag<byte[]> ReturnedBuffers = new();
        internal int Reads, Active, Maximum, Writes, Disposals;
        internal bool Fault;
        internal Hold Block(int number)
        {
            var value = new Hold(); Check(Holds.TryAdd(number, value)); return value;
        }
        public async Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default)
        {
            Check(ns == MusicAccountOwner.DocumentNamespace);
            int count = Interlocked.Increment(ref Reads), active = Interlocked.Increment(ref Active);
            int old; do { old = Maximum; } while (active > old && Interlocked.CompareExchange(ref Maximum, active, old) != old);
            Holds.TryGetValue(count, out var hold);
            var bytes = Data?.ToArray(); // Exact read snapshot; cancellation cannot change its identity.
            try
            {
                if (hold is not null)
                {
                    using var registration = token.Register(() => hold.Cancelled.TrySetResult());
                    hold.Entered.TrySetResult(); await hold.Release.Task; // Deliberately ignores cancellation.
                }
                if (Fault || hold?.Fault == true) throw new IOException("synthetic-secret-storage-path");
                if (bytes is not null) ReturnedBuffers.Add(bytes);
                return new(bytes, bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes));
            }
            finally { Interlocked.Decrement(ref Active); hold?.Settled.TrySetResult(); }
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        { Interlocked.Increment(ref Writes); throw new InvalidOperationException("Read-only fixture forbids writes"); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Protocol : IMusicAccountProtocol
    {
        internal int NetworkCalls, Cancels, Disposals;
        private Task<T> Reject<T>(string operation)
        {
            NetworkCalls++;
            return Task.FromException<T>(new InvalidOperationException(operation + " is outside this read-only fixture"));
        }
        private Task Reject(string operation)
        {
            NetworkCalls++;
            return Task.FromException(new InvalidOperationException(operation + " is outside this read-only fixture"));
        }
        public Task<MusicAccountDetailsObservation> ValidateAsync(string platform, string cookie, CancellationToken cancellationToken = default)
            => Reject<MusicAccountDetailsObservation>("Authentication");
        public Task<MusicAccountChallenge> BeginAsync(string platform, CancellationToken cancellationToken = default)
            => Reject<MusicAccountChallenge>("QR login");
        public Task<MusicAccountPoll> PollAsync(string platform, string challengeId, MusicAccountSessionHandoff handoff, CancellationToken cancellationToken = default)
            => Reject<MusicAccountPoll>("QR polling");
        public Task<MusicAccountSmsSent> SendSmsAsync(MusicAccountSmsInput input, CancellationToken cancellationToken = default)
            => Reject<MusicAccountSmsSent>("SMS send");
        public Task LoginSmsAsync(MusicAccountSmsInput input, string attemptId,
            MusicAccountSessionHandoff acceptSession, CancellationToken cancellationToken = default)
            => Reject("SMS login");
        public Task<MusicAccountDetailsObservation> ReadAccountDetailsAsync(
            MusicPlatformSession account, CancellationToken token = default)
            => Reject<MusicAccountDetailsObservation>("Account details");
        public Task<MusicAccountChallenge> BeginKgAsync(
            Func<CancellationToken, Task<StoredKgDevice>> acquire,
            Func<StoredKgDevice, string, CancellationToken, Task<StoredKgDevice>> save,
            CancellationToken token) => Reject<MusicAccountChallenge>("Kugou login");
        public TimeProvider QqFeedbackClock => TimeProvider.System;
        public Task<MusicAccountProfile> VerifyQqFeedbackAccountAsync(
            MusicPlatformSession account, CancellationToken token)
            => Reject<MusicAccountProfile>("QQ feedback account verification");
        public Task<StoredQqQimei> RequestQqQimeiAsync(StoredQqFeedbackDevice device, CancellationToken token)
            => Reject<StoredQqQimei>("QQ QIMEI");
        public Task<StoredQqDeviceSession> RequestQqDeviceSessionAsync(
            StoredQqFeedbackDevice device, CancellationToken token)
            => Reject<StoredQqDeviceSession>("QQ device session");
        public void Cancel(string platform) { Cancels++; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new();
        internal readonly Protocol Protocol = new();
        internal readonly MusicAccountOwner Owner;
        internal Fixture() => Owner = new(Store, Protocol);
        public async ValueTask DisposeAsync()
        {
            foreach (var hold in Store.Holds.Values) hold.Release.TrySetResult();
            await Owner.DisposeAsync();
            Check(Store.Writes == 0 && Store.Disposals == 0 && Protocol.NetworkCalls == 0, "Borrowed store or no-network boundary");
            await Store.DisposeAsync();
        }
    }
    private static async Task Contracts()
    {
        await using var f = new Fixture();
        var reply = await f.Owner.CallAsync("music.accounts.restoreForDiscovery");
        Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "INVALID_REQUEST" && f.Store.Reads == 0);
    }
    private static async Task Anonymous()
    {
        await using var f = new Fixture(); f.Store.Data = null;
        var result = await Read(f, true); Success(result, true);
        Check(result.List!.Value.GetProperty("items").EnumerateArray().All(r => r.GetProperty("state").GetString() == "none"));
        Check(f.Store.Reads == 1 && f.Protocol.Cancels == 0);
    }
    private static async Task Saved()
    {
        await using var f = new Fixture();
        var restored = await Read(f, true); Success(restored, true);
        Check(Row(restored.List!.Value).GetProperty("state").GetString() == "saved");
        Check(!restored.List.Value.GetRawText().Contains("synthetic-secret", StringComparison.Ordinal));
        var session = (await Read(f, false)).Session!; Check(f.Owner.IsCurrent(session));
        Check(session.Generation == 1 && JsonSerializer.Serialize(session) == "{}" && session.ToString() == "MusicPlatformSession (redacted)");
        var legacy = await f.Owner.AcquireAsync("wy"); Check(legacy is not null && f.Owner.IsCurrent(legacy) && f.Owner.IsCurrent(session));
        Check(f.Store.Reads == 3);
    }
    private static async Task InitialObservationWaiters()
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = f.Owner.EnsureInitialObservationAsync(cancellation.Token);
        await Wait(hold.Entered.Task);
        var survivor = f.Owner.EnsureInitialObservationAsync();
        cancellation.Cancel();
        await Task.Delay(35);
        Check(!cancelled.IsCompleted && !survivor.IsCompleted && !hold.Cancelled.Task.IsCompleted &&
            f.Store.Reads == 1 && f.Store.Active == 1, "One cancelled subscriber retired shared observation");
        hold.Release.TrySetResult();
        var cancelledResult = await cancelled;
        Check(!cancelledResult.GetProperty("ok").GetBoolean() &&
            cancelledResult.GetProperty("code").GetString() == "CANCELLED");
        Success(new(List: await survivor), true);
        Check(hold.Settled.Task.IsCompleted && f.Store.Reads == 1 && f.Store.Active == 0);
    }
    private static async Task InitialObservationLastCancel()
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1);
        using var cancellation = new CancellationTokenSource();
        var pending = f.Owner.EnsureInitialObservationAsync(cancellation.Token);
        await Wait(hold.Entered.Task); cancellation.Cancel();
        await Wait(hold.Cancelled.Task); await Task.Delay(35);
        Check(!pending.IsCompleted && !hold.Settled.Task.IsCompleted,
            "Cancelled last subscriber returned before physical observation settled");
        hold.Release.TrySetResult();
        var result = await pending;
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED");
        Success(new(List: await f.Owner.EnsureInitialObservationAsync()), true);
        Check(f.Store.Reads == 2 && f.Store.Maximum == 1 && f.Store.Active == 0);
    }
    private static async Task InitialObservationCloseBeforeAdmission()
    {
        await using var f = new Fixture();
        var previous = SynchronizationContext.Current;
        var context = new HoldingSynchronizationContext();
        Task<JsonElement> observation;
        Task close;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            observation = f.Owner.EnsureInitialObservationAsync();
            Check(context.Pending == 1 && f.Store.Reads == 0, "Initial observation entered storage before publication");
            close = f.Owner.PrepareCloseAsync();
            Check(!close.IsCompleted && f.Store.Reads == 0,
                "Close returned while the published observation had not settled");
            context.RunAll();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED");
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Store.Reads == 0 && f.Store.Active == 0);
    }
    private static async Task InitialObservationClose()
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1);
        var observation = f.Owner.EnsureInitialObservationAsync();
        await Wait(hold.Entered.Task);
        var close = f.Owner.PrepareCloseAsync();
        await Wait(hold.Cancelled.Task); await Task.Delay(35);
        Check(!observation.IsCompleted && !close.IsCompleted && !hold.Settled.Task.IsCompleted,
            "Close detached the account observation instead of draining it");
        hold.Release.TrySetResult();
        var result = await observation;
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED");
        await close;
        Check(hold.Settled.Task.IsCompleted && f.Store.Reads == 1 && f.Store.Active == 0);
    }
    private static async Task CancelThenRead(bool restore, bool lateFault = false)
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1); hold.Fault = lateFault;
        using var cancel = new CancellationTokenSource();
        var first = Read(f, restore, cancel.Token); await Wait(hold.Entered.Task); cancel.Cancel();
        var next = Read(f, restore);
        try
        {
            await Wait(hold.Cancelled.Task); await Task.Delay(35);
            Check(!first.IsCompleted && !next.IsCompleted && f.Store.Reads == 1 && f.Store.Active == 1, "Detached read or overlapping replacement");
        }
        finally { hold.Release.TrySetResult(); }
        await Failure(first, restore, "CANCELLED", cancel.Token); Success(await next, restore);
        Check(hold.Settled.Task.IsCompleted && f.Store.Reads == 2 && f.Store.Maximum == 1 && f.Store.Active == 0);
    }
    private static async Task QueuedCancellation(bool restore)
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1);
        var unrelated = Read(f, !restore); await Wait(hold.Entered.Task);
        using var cancel = new CancellationTokenSource(); var waiting = Read(f, restore, cancel.Token);
        try
        {
            cancel.Cancel(); await Wait(Failure(waiting, restore, "CANCELLED", cancel.Token));
            Check(!unrelated.IsCompleted && f.Store.Reads == 1 && f.Store.Active == 1, "Queued cancel became a global drain");
        }
        finally { hold.Release.TrySetResult(); }
        Success(await unrelated, !restore); Check(f.Store.Reads == 1);
    }
    private static async Task NotGlobalBarrier(bool restore)
    {
        await using var f = new Fixture(); var one = f.Store.Block(1); var two = f.Store.Block(2);
        var first = Read(f, restore); await Wait(one.Entered.Task); var second = Read(f, !restore);
        try
        {
            one.Release.TrySetResult(); await Wait(two.Entered.Task);
            var result = await first.WaitAsync(TimeSpan.FromSeconds(5)); Success(result, restore);
            Check(!second.IsCompleted && f.Store.Reads == 2 && f.Store.Active == 1, "Completion waited for unrelated operation");
        }
        finally { one.Release.TrySetResult(); two.Release.TrySetResult(); }
        Success(await second, !restore); Check(f.Store.Maximum == 1);
    }
    private static async Task Notifications(bool restore, bool cancel)
    {
        await using var f = new Fixture(); var old = await f.Owner.AcquireSettledAsync("wy"); Check(old is not null);
        f.Store.Data = Document(2);
        var entered = Signal(); var release = Signal(); int notified = 0, later = 0;
        Action<string> observer = p => { Check(p == "wy"); Interlocked.Increment(ref notified); entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        Action<string> after = _ => Interlocked.Increment(ref later);
        f.Owner.SessionChanged += observer; f.Owner.SessionChanged += after;
        using var cancellation = new CancellationTokenSource();
        var pending = Task.Run(() => Read(f, restore, cancellation.Token));
        try
        {
            await Wait(entered.Task);
            Check(!f.Owner.IsCurrent(old!) && !pending.IsCompleted && f.Store.Active == 0, "Notification/lease fence not retained");
            if (cancel) cancellation.Cancel();
            await Task.Delay(35); Check(!pending.IsCompleted && later == 0, "Returned before synchronous callback settled");
        }
        finally { release.TrySetResult(); }
        try
        {
            if (cancel) await Failure(pending, restore, "CANCELLED", cancellation.Token);
            else Success(await pending, restore);
            Check(notified == 1 && later == 1 && f.Store.Reads == 2, "Notification lost/replayed");
        }
        finally { f.Owner.SessionChanged -= observer; f.Owner.SessionChanged -= after; }
    }
    private static async Task ObserverError(bool restore)
    {
        await using var f = new Fixture(); _ = await f.Owner.AcquireSettledAsync("wy"); f.Store.Data = Document(2); int next = 0;
        Action<string> bad = _ => throw new InvalidOperationException("synthetic-secret-observer");
        Action<string> good = _ => next++;
        f.Owner.SessionChanged += bad; f.Owner.SessionChanged += good;
        try { Success(await Read(f, restore), restore); Check(next == 1 && f.Store.Reads == 2); }
        finally { f.Owner.SessionChanged -= bad; f.Owner.SessionChanged -= good; }
    }
    private static async Task PreCancelled(bool restore)
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Failure(Read(f, restore, cancel.Token), restore, "CANCELLED", cancel.Token);
        Check(f.Store.Reads == 0);
    }
    private static async Task ReadError(bool restore, bool corrupt)
    {
        await using var f = new Fixture();
        if (corrupt) f.Store.Data = Encoding.UTF8.GetBytes("synthetic-secret-invalid-json"); else f.Store.Fault = true;
        await Failure(Read(f, restore), restore, corrupt ? "CORRUPT_STORAGE" : "STORAGE");
        Check(f.Store.Reads == 1);
    }
    private static async Task OrdinaryRead(bool restore)
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1); using var cancel = new CancellationTokenSource();
        var ordinary = Read(f, restore, cancel.Token, ordinary: true); await Wait(hold.Entered.Task);
        try
        {
            cancel.Cancel(); await Wait(Failure(ordinary, restore, "CANCELLED", cancel.Token));
            Check(!hold.Settled.Task.IsCompleted && f.Store.Active == 1, "Changed ordinary early-return contract");
        }
        finally { hold.Release.TrySetResult(); }
        // A settled request may queue behind the ordinary operation, not silently bypass its retained gate.
        Success(await Read(f, restore), restore); Check(f.Store.Reads == 2 && f.Store.Maximum == 1);
    }
    private static async Task Admission(bool restore)
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1); using var cancel = new CancellationTokenSource();
        var first = Read(f, restore); await Wait(hold.Entered.Task);
        var queued = Enumerable.Range(0, 3).Select(_ => Read(f, restore, cancel.Token)).ToArray();
        try
        {
            await Failure(Read(f, restore), restore, "RATE_LIMITED");
            cancel.Cancel(); await Task.WhenAll(queued.Select(t => Failure(t, restore, "CANCELLED", cancel.Token)));
            Check(!first.IsCompleted && f.Store.Reads == 1);
        }
        finally { hold.Release.TrySetResult(); }
        Success(await first, restore); Check(f.Store.Reads == 1);
    }
    private static async Task Reconcile()
    {
        await using var f = new Fixture(); f.Store.Data = Document(1, anonymous: true);
        Check(await f.Owner.AcquireSettledAsync("wy") is null); int notifications = 0;
        Action<string> observe = p => { Check(p == "wy"); notifications++; };
        f.Owner.SessionChanged += observe;
        try
        {
            f.Store.Data = Document(2); Success(await Read(f, true), true);
            Check(notifications == 1); var session = await f.Owner.AcquireSettledAsync("wy"); Check(session is not null && f.Owner.IsCurrent(session));
            f.Store.Data = Document(3, anonymous: true);
            var restored = await f.Owner.RestoreForDiscoveryAsync();
            Check(notifications == 2 && !f.Owner.IsCurrent(session!) && Row(restored).GetProperty("state").GetString() == "none");
            Check(await f.Owner.AcquireSettledAsync("wy") is null && f.Store.Reads == 5);
        }
        finally { f.Owner.SessionChanged -= observe; }
    }
    private static async Task InvalidPlatform()
    {
        await using var f = new Fixture();
        try { await f.Owner.AcquireSettledAsync("invalid"); throw new InvalidOperationException("Expected invalid platform"); }
        catch (MusicAccountException error) { Check(error.Code == "INVALID_REQUEST"); }
        Check(f.Store.Reads == 0);
    }
    private static async Task Revoked()
    {
        await using var f = new Fixture(); var session = await f.Owner.AcquireSettledAsync("wy");
        Check(session is not null && f.Owner.Revoke(session));
        Check(await f.Owner.AcquireSettledAsync("wy") is null && f.Store.Reads == 1 && !f.Owner.IsCurrent(session!));
    }
    private static async Task RemainsOpen()
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1); using var cancel = new CancellationTokenSource();
        var first = Read(f, true, cancel.Token); await Wait(hold.Entered.Task); cancel.Cancel(); hold.Release.TrySetResult();
        await Failure(first, true, "CANCELLED");
        Success(await Read(f, false), false); Success(await Read(f, true, ordinary: true), true);
        Check(f.Protocol.Cancels == 0 && f.Protocol.Disposals == 0 && f.Store.Disposals == 0 && f.Store.Reads == 3);
    }
    private static async Task BufferOwnership()
    {
        await using var f = new Fixture(); var hold = f.Store.Block(1); using var cancel = new CancellationTokenSource();
        var pending = Read(f, false, cancel.Token); await Wait(hold.Entered.Task); cancel.Cancel(); hold.Release.TrySetResult();
        await Failure(pending, false, "CANCELLED", cancel.Token);
        Check(f.Store.ReturnedBuffers.Count == 1 && f.Store.ReturnedBuffers.Single().All(b => b == 0));
    }
}
