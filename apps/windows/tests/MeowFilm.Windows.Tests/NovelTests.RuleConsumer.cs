using System.Text.Json;
using System.Windows.Controls;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    // Explicit RPC receipts only; execution and budgets use the real runtime fixture.
    private sealed class RuleReceiptRuntime : IRuntimeClient, IAsyncDisposable
    {
        internal int Disposals;
        internal readonly List<(string Method, object? Parameters, CancellationToken Token)> Calls = [];
        internal Func<object?, CancellationToken, Task<JsonElement>> Respond = (_, _) => Task.FromResult(
            Json(new { ok = true, contract = "source.runtime.v2", value = new { key = "accepted" } }));
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Check(method == "source.runtime.rules.process");
            Calls.Add((method, parameters, cancellationToken));
            return Respond(parameters, cancellationToken);
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure rules never upload source.");
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private static async Task RuleConsumer()
    {
        await using var runtime = new RuleReceiptRuntime();
        await using var rules = new NovelRuleProgram(runtime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originals = new[] { new NovelBlock("text", "广告漢語") };
        var selected = new[]
        {
            new NovelRule { Id = "second", Order = 7, Pattern = "广告", Replacement = "", TimeoutMillisecond = 42 },
            new NovelRule { Id = "disabled", Order = -5, Enabled = false, Pattern = "漢", Replacement = "汉" },
            new NovelRule { Id = "first", Order = 7, Pattern = "語", Replacement = "语" }
        };
        var expected = NovelJson.Value(new
        {
            operation = "transform", title = "原文", blocks = originals, book = Book().Json(),
            source = new { installationId = "a", sourceUrl = Source().SourceUrl, bookSourceUrl = Source().SourceUrl, name = Source().Name },
            rules = selected, conversion = "traditional"
        });
        runtime.Respond = async (parameters, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            var payload = NovelJson.Value(parameters!);
            Check(payload.EnumerateObject().Count() == 1 && JsonElement.DeepEquals(expected, payload.GetProperty("request")),
                "one detached request preserves all rules, disabled entries, original order, budgets and final conversion");
            return Json(new { ok = true, contract = "source.runtime.v2", value = new { title = "处理结果", blocks = new[] { new NovelBlock("text", "净化") } } });
        };
        var processing = rules.TransformAsync("原文", originals, Book(), Source(), selected, "traditional");
        await entered.Task;
        selected[0] = selected[0] with { Pattern = "改写" }; originals[0] = new("text", "调用后改写");
        release.TrySetResult();
        Check((await processing).GetProperty("title").GetString() == "处理结果" && runtime.Calls.Count == 1);

        foreach (string code in new[] { "TIMEOUT", "SOURCE_FAILURE", "CLOSED", "CANCELLED", "CAPABILITY_UNAVAILABLE" })
        {
            runtime.Respond = (_, _) => Task.FromResult(Json(new { ok = false, contract = "source.runtime.v2", code }));
            try { await rules.IdentityAsync(Book()); throw new InvalidOperationException("Rule RPC failure became success"); }
            catch (BridgeException error) { Check(error.Code == code); }
        }
        runtime.Respond = (_, _) => Task.FromResult(Json(new { ok = true, contract = "source.runtime.v2", value = new { valid = false, errors = new[] { "invalid" } } }));
        try { await rules.ValidateAsync(new() { Pattern = "[" }); throw new InvalidOperationException("Invalid rule accepted"); }
        catch (System.IO.InvalidDataException error) { Check(error.Message == "INVALID_RULE"); }

        var physical = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken accepted = default;
        runtime.Respond = async (_, ct) =>
        {
            accepted = ct;
            using var registration = ct.Register(() => cancelled.TrySetResult());
            return await physical.Task;
        };
        var pending = rules.IdentityAsync(Book());
        var closed = rules.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(accepted.IsCancellationRequested && !pending.IsCompleted && !closed.IsCompleted && runtime.Disposals == 0,
                "closing owns the cancellation and actual completion, never the shared runtime");
            physical.SetResult(Json(new { ok = true, contract = "source.runtime.v2", value = new { key = "late" } }));
            try { await pending; throw new InvalidOperationException("Late cancelled rule result published"); }
            catch (OperationCanceledException) { }
            await closed;
        }
        finally
        {
            physical.TrySetResult(Json(new { ok = true, contract = "source.runtime.v2", value = new { key = "late" } }));
        }
        Check(runtime.Disposals == 0);
        try { await rules.IdentityAsync(Book()); throw new InvalidOperationException("Closed rules admitted work"); }
        catch (ObjectDisposedException) { }
        await using var replacement = new NovelRuleProgram(runtime);
        runtime.Respond = (_, _) => Task.FromResult(Json(new { ok = true, contract = "source.runtime.v2", value = true }));
        Check(await replacement.MatchUrlAsync("pattern", "url") && runtime.Disposals == 0);
        var match = NovelJson.Value(runtime.Calls.Last().Parameters!).GetProperty("request");
        Check(match.EnumerateObject().Count() == 3 && NovelJson.Text(match, "operation") == "matchUrl" &&
            NovelJson.Text(match, "pattern") == "pattern" && NovelJson.Text(match, "url") == "url");
    }

    private static async Task WebRuleRetirement()
    {
        await using var remote = new RuleReceiptRuntime();
        await using var rules = new NovelRuleProgram(remote);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.Respond = async (parameters, ct) =>
        {
            Check(NovelJson.Text(NovelJson.Value(parameters!).GetProperty("request"), "operation") == "matchUrl");
            using var registration = ct.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); return await physical.Task;
        };
        var owner = new StackPanel(); var host = GeneralAppearanceTests.Window(owner); NovelUi.Theme(host); host.Show();
        var runtime = new Runtime(); var sources = new NovelSourceService(runtime);
        await sources.RefreshAsync();
        await using var server = new WebFixture();
        bool closed = false;
        sources.InteractAsync = async (pending, ct) =>
        {
            try { return await NovelWebInteraction.ShowAsync(owner, rules, pending, ct); }
            finally { closed = true; }
        };
        runtime.Read = async (_, _, _, ct) =>
        {
            runtime.PendingWeb = [Json(new { id = "remote-match", installationId = "a", mode = "webview", request = new
                { url = server.BaseUrl + "/dynamic", sourceRegex = ".*", script = "'completed'" } })];
            await Task.Delay(Timeout.Infinite, ct); return Json(new { });
        };
        var invocation = sources.InvokeAsync(sources.Sources.Single(), "content", new { });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sources.UpdateAsync(sources.Sources.Single(), new { enabled = false });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!invocation.IsCompleted && !closed, "source cancellation must drain the accepted URL RPC and browser");
            physical.SetResult(Json(new { ok = true, contract = "source.runtime.v2", value = true }));
            try { await invocation; throw new InvalidOperationException("Retired remote match committed"); }
            catch (OperationCanceledException) { }
            Check(closed && runtime.Calls.Where(call => call.Method == "source.runtime.web.respond").All(call =>
                !call.Payload.TryGetProperty("result", out _) && call.Payload.GetProperty("cancelled").GetBoolean()));
        }
        finally
        {
            physical.TrySetResult(Json(new { ok = true, contract = "source.runtime.v2", value = true }));
            await sources.PrepareShutdownAsync();
            host.Close();
        }
    }

    private static async Task ResetOwners()
    {
        await using var store = new MemoryStore();
        var sources = new Runtime { Records = [] };
        await using var remote = new RuleReceiptRuntime();
        var physical = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new NovelCapability(new NovelSourceService(sources), new NovelLibrary(store), new NovelRuleProgram(remote));
        await old.InitializeAsync(default);
        await old.Library.SaveEntryAsync(new("old-book", "old-key", Book(), OnShelf: true));
        await old.Library.SaveRulesAsync([new() { Pattern = "旧规则" }]);
        await old.Library.SaveAppearanceAsync(new() { FontSize = 29 });
        var ruleAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ruleCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.Respond = async (_, ct) =>
        {
            using var registration = ct.Register(() => ruleCancelled.TrySetResult());
            ruleAccepted.TrySetResult();
            return await physical.Task;
        };
        var opening = old.ReceiveBookAsync(Book(), default);
        await ruleAccepted.Task;
        var heldWrite = store.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeAccepted = store.Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedWrite = old.Library.SaveAppearanceAsync(new() { FontSize = 30 });
        await writeAccepted.Task;
        var shutdown = new AppShutdown([old], []);
        NovelCapability? fresh = null;
        var reset = new AppUserDataReset(() => { }, shutdown.ForceReleaseAsync,
            () => { Check(shutdown.IsReleased); store.Bytes = null; return Task.CompletedTask; },
            async () =>
            {
                fresh = new(new NovelSourceService(sources), new NovelLibrary(store), new NovelRuleProgram(remote));
                await fresh.InitializeAsync(default);
                await fresh.NavigateAsync(NovelCapability.HomeRoute, default);
            });
        try
        {
            var resetting = reset.ResetAsync();
            await ruleCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!resetting.IsCompleted && fresh is null && remote.Calls.Single().Token.IsCancellationRequested,
                "reset must wait for accepted novel RPC and native persistence");
            physical.SetResult(Json(new { ok = true, contract = "source.runtime.v2", value = new { key = "retired" } }));
            try { await opening; throw new InvalidOperationException("Old novel opened after reset"); }
            catch (OperationCanceledException) { }
            Check(!resetting.IsCompleted && fresh is null);
            heldWrite.TrySetResult(); await acceptedWrite; await resetting;
            Check(fresh is not null && fresh.Library.Entries.Count == 0 && fresh.Library.Rules.Count == 0 &&
                fresh.Library.Appearance.FontSize == 22 && fresh.Session is null &&
                fresh.CaptureNavigationLocation() == NovelCapability.HomeRoute && old.Session is null &&
                remote.Disposals == 0, "fresh native owner and navigation must not retain a retired book or its data");
            try { await old.Rules.IdentityAsync(Book()); throw new InvalidOperationException("Old owner remained usable"); }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            physical.TrySetResult(Json(new { ok = true, contract = "source.runtime.v2", value = new { key = "retired" } }));
            heldWrite.TrySetResult();
            await old.DisposeAsync();
            if (fresh is not null) await fresh.DisposeAsync();
        }
    }
}
