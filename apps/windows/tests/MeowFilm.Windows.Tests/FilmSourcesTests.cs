using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class FilmSourcesTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("local initialization options order and aggregate choice survive restart", LocalState);
        yield return Case("mixed documents preserve exact raw extensions facts and nested values through save sync and restart", RawSourcePersistence);
        yield return Case("unusable cache and isolated fact records never clear healthy options order or tombstones", IsolatedSourceRecords);
        yield return Case("duplicate source identity members isolate every candidate through save sync and restart", AmbiguousSourceIdentityMembers);
        yield return Case("unknown update display state never retires an otherwise valid source generation", UnknownUpdateState);
        yield return Case("unsupported roots and real source CAS conflicts preserve original storage", SourceDocumentBoundaries);
        yield return Case("native source intent is authoritative and only update restart and detection use Cat RPC", Management);
        yield return Case("tombstones survive updates type changes and duplicate adds for each parent", Tombstones);
        yield return Case("a deletion while runtime status is pending wins over late metadata", DeleteDuringImport);
        yield return Case("starting failed disabled and missing parents reconcile without empty-list loss", Reconciliation);
        yield return Case("dashboard fixture sends home category detail play search parameters", DetectionContract);
        yield return Case("dashboard fallback matrix distinguishes errors empty data and missing actions", DetectionMatrix);
        yield return Case("initial detail script errors preserve missing-playback search fallback", DetailErrorFallback);
        yield return Case("HTTP invocation and navigation failures cannot provide detail playback candidates", DetailFailureBoundaries);
        yield return Case("failed root navigation skips children and preserves search fallback", FailedNavigationRoot);
        yield return Case("live resource failures remain stage errors and stale sessions preserve detection", DetectionFailureOwnership);
        yield return Case("detail navigation preserves custom actions payloads and advancing pages", Navigation);
        yield return Case("deleted disabled and replaced generations fence noncooperative late detections", LateResults);
        yield return Case("shutdown preparation drains and resume admits new work without disposal", PrepareAndResume);
        yield return Case("final shutdown drains late work and leaves borrowed dependencies alive", Disposal);
        yield return Case("selected source hashes and large structured values preserve Unicode", SourceAndValues);
        yield return Case("failed local saves do not publish and lost commit receipts reconcile", StorageFailures);
        yield return Case("post-network-update cache failures retire old generations and preserve user state", ManagementStorageFailures);
        yield return Case("shutdown drains storage-failure observation while preserving the original storage error", StorageFailureShutdown);
        yield return Case("startup failure is observable and local initialization never contacts Node", StartupFailure);
        yield return Case("explicit refresh recovers a failed host epoch without replaying management", ExplicitRuntimeRecovery);
        yield return Case("cancelled management retires cached generations and waits for rollback without replay", CancelledManagement);
        yield return Case("cancelled or timed-out committed online changes reconcile without replay or restoring tombstones", LostOnlineReceipts);
        yield return Case("cancelled structured detection replies preserve the completed result", CancelledDetectionReply);
        yield return Case("failed or shutdown cancellation reconciliation never authorizes a retired generation", CancelledReconciliationFailure);
        yield return Case("timed out management observes delayed rollback independently of the page and preserves its error", TimedOutManagement);
        yield return Case("failed or shutdown timeout reconciliation preserves the original error and drains observation", TimedOutReconciliationFailure);
    }

    private static async Task LocalState()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b");
        var store = new Store(runtime);
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(runtime.Calls.Count == 0 && store.Reads == 2, "initialization only reads the local document");
            await service.RefreshAsync();
            var a = Key("a");
            var b = Key("b");
            await service.SaveVideoOptionsAsync(a, new(false, true, true, false));
            await service.SaveVideoOptionsAsync(a, new(false, false, true, false));
            await service.SaveVideoOptionsAsync(a, new(false, false, false, false));
            await service.SaveVideoOptionsAsync(a, new(false, false, false, true));
            Check(store.Writes == 5, "each changed switch is really saved");
            await service.SaveVideoOptionsAsync(b, new(true, false, true, true), order: 0);
            Check(service.Videos[0].Key == b && service.Videos[1].Key == a &&
                service.Videos.Count(video => video.Options.AggregateImages) == 1,
                "ordering is local and aggregate artwork is globally single-select");
            Check(service.Videos.Single(video => video.Key == a).Options == new FilmVideoOptions(false, false, false, false),
                "choosing another aggregate source preserves all other switches");
            await service.SaveVideoOptionsAsync(a, new(false, false, false, false), order: 500);
            await service.RefreshAsync();
            Check(service.Videos.Single(video => video.Key == a).Order == 500, "sort values are persisted, not constrained by row count or renumbered on sync");
        }
        int calls = runtime.Calls.Count;
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(runtime.Calls.Count == calls && reopened.Videos.Select(video => video.Key).SequenceEqual([Key("b"), Key("a")]),
            "restart restores the local order without runtime access");
        Check(reopened.Videos[1].Order == 500 && reopened.Videos[0].Options == new FilmVideoOptions(true, false, true, true) &&
            reopened.Videos[1].Options == new FilmVideoOptions(false, false, false, false),
            "all four switches survive restart");
        Check(reopened.Cats.All(cat => !cat.CanDetect && cat.Generation == 0), "cached metadata cannot authorize old-generation invocations");
        string json = Encoding.UTF8.GetString(store.Bytes!);
        Check(!json.Contains("\"generation\"", StringComparison.Ordinal) && !json.Contains("\"port\"", StringComparison.Ordinal),
            "runtime generation and port are absent from the durable list");
    }

    private static async Task Management()
    {
        var runtime = new Runtime();
        var store = new Store(runtime);
        await using var service = new FilmSourceService(runtime, store);
        using var file = new SourceFile(FixtureScript);
        var local = await service.ImportLocalAsync("本地猫", file.Path, "local");
        Check(local.Enabled && local.SourceType == "local" && local.LocalPath == file.Path &&
            local.InputRevision == 1 && !local.CanDetect && local.ImportedAt is null,
            "local save exposes committed intent, not an invented runtime receipt");
        var network = await service.ImportNetworkAsync("网络猫", "https://fixture.invalid/cat.js", "net");
        Check(network.SourceType == "network" && network.SourceUrl == "https://fixture.invalid/cat.js" &&
            network.LocalPath is null && runtime.Calls.Count == 0 && runtime.Uploads.Count == 0,
            "adding only saves intent; source retrieval belongs to the runtime");
        await service.EditCatAsync("net", "新名称", "https://fixture.invalid/changed.js");
        await service.EditCatAsync("local", "本地改名");
        Check(service.Cats.Single(cat => cat.InstallationId == "local").InputRevision == 1 &&
            service.Cats.Single(cat => cat.InstallationId == "net").InputRevision == 2,
            "only source changes advance input revision, not name edits");
        using var changed = new SourceFile(FixtureScript + "\n// changed");
        await service.UpdateCatAsync("local", changed.Path);
        Check(service.Cats.Single(cat => cat.InstallationId == "local").InputRevision == 2 &&
            runtime.Calls.Count == 0 && runtime.Uploads.Count == 0, "local update is another native file selection");
        runtime.Apply(store);
        await service.RefreshAsync();
        long? modified = service.Cats.Single(cat => cat.InstallationId == "local").ModifiedAt;
        await service.RestartCatAsync("local");
        Check(service.Cats.Single(cat => cat.InstallationId == "local").ModifiedAt == modified,
            "restart does not synthesize an update time");
        await service.UpdateCatAsync("net");
        Fields(runtime.Calls.Last(call => call.Method == "update").Input, "installationId");
        Check(service.Cats.Single(cat => cat.InstallationId == "net").UpdatedAt == 200,
            "successful network update uses its actual reported timestamp");
        runtime.NextUpdateState = "unchanged";
        await service.UpdateCatAsync("net");
        Check(service.Cats.Single(cat => cat.InstallationId == "net").ModifiedAt == 200,
            "unchanged content retains modification time");
        runtime.FailNext = "update";
        await Code(() => service.UpdateCatAsync("net"), "SCRIPT_EXCEPTION");
        var failed = service.Cats.Single(cat => cat.InstallationId == "net");
        Check(failed.UpdateState == "failed" && failed.UpdateError == "SCRIPT_EXCEPTION" && failed.CanDetect &&
            failed.ModifiedAt == 200 && service.Videos.Count == 2, "failed online update preserves actual working facts");
        int count = runtime.Calls.Count;
        await service.EnableCatAsync("net", false);
        await Code(() => service.RestartCatAsync("net"), "DISABLED");
        await Code(() => service.DetectAsync(Key("net")), "DISABLED");
        await service.DeleteCatAsync("local");
        Check(runtime.Calls.Count == count && service.Cats.Single().InstallationId == "net" &&
            service.Videos.Single().Key == Key("net"), "native disable/delete never RPC and affect only their parent");
        Check(runtime.Calls.All(call => call.Method is "status" or "restart" or "update"),
            "native management never sends legacy install/import/edit/enable/uninstall or source upload");
    }

    private static async Task CancelledManagement()
    {
        var runtime = new Runtime(); runtime.Add("a", "video", "deleted");
        await using var service = new FilmSourceService(runtime, new Store(runtime));
        await service.RefreshAsync();
        await service.DeleteVideoAsync(new("a", "deleted"));
        var options = new FilmVideoOptions(false, false, false, true);
        await service.SaveVideoOptionsAsync(Key("a"), options);
        var entered = Signal(); var reconciling = Signal(); var release = Signal();
        using var cancel = new CancellationTokenSource();
        bool updating = false;
        int statuses = 0;
        runtime.OnCall = (operation, _, token) => operation switch
        {
            "update" => Update(token),
            "status" when updating => Status(token),
            _ => null
        };
        async Task<JsonElement> Update(CancellationToken token)
        {
            updating = true;
            runtime.Nodes["a"].State = "starting";
            runtime.Nodes["a"].Generation = 2;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Expected cancellation");
        }
        async Task<JsonElement> Status(CancellationToken token)
        {
            Check(!token.IsCancellationRequested, "cancelled page token is not used to coordinate Node");
            if (++statuses == 1)
                return runtime.Reply(new { instances = runtime.Nodes.Values.Select(node => node.Wire()).ToArray() });
            reconciling.TrySetResult();
            await release.Task.WaitAsync(token);
            var cat = runtime.Nodes["a"];
            cat.Generation = 3; cat.State = "ready"; cat.UpdateState = "failed"; cat.UpdateError = "CANCELLED";
            return runtime.Reply(new { instances = runtime.Nodes.Values.Select(node => node.Wire()).ToArray() });
        }
        var update = service.UpdateCatAsync("a", null, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await reconciling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!update.IsCompleted && !service.Cats.Single().CanDetect,
                "cancellation cannot finish or expose an old ready generation before rollback settles");
            release.TrySetResult();
            await Cancelled(update);
            Check(service.Cats.Single().CanDetect && service.Cats.Single().Generation == 3 &&
                service.Cats.Single().UpdateError == "CANCELLED", "cancellation publishes the actual recovered generation");
            Check((await service.DetectAsync(Key("a"))).Result == "valid" &&
                runtime.Invocations.All(call => call.Generation == 3), "next detection works without manual refresh");
            Check(service.Videos.Count == 1 && service.Videos.Single().Options == options,
                "rollback synchronization preserves options and the deleted video");
            Check(runtime.Uploads.Count == 0 && runtime.Calls.Count(call => call.Method == "update") == 1 &&
                !runtime.Calls.Any(call => call.Method is "install" or "import" or "restart" or "refresh"),
                "cancellation reads status only; no management replay");
        }
        finally { release.TrySetResult(); cancel.Cancel(); }
    }

    private static async Task CancelledReconciliationFailure()
    {
        foreach (bool shutdown in new[] { false, true })
        {
            var runtime = new Runtime(); runtime.Add("a");
            await using var service = new FilmSourceService(runtime, new Store(runtime));
            await service.RefreshAsync();
            var entered = Signal(); var reconciling = Signal();
            using var cancel = new CancellationTokenSource();
            runtime.OnCall = (operation, _, token) => operation switch
            {
                "update" => Update(token),
                "status" => Status(token),
                _ => null
            };
            async Task<JsonElement> Update(CancellationToken token)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Expected cancellation");
            }
            async Task<JsonElement> Status(CancellationToken token)
            {
                reconciling.TrySetResult();
                if (shutdown) await Task.Delay(Timeout.Infinite, token);
                throw new BridgeException("CLOSED");
            }
            var update = service.UpdateCatAsync("a", null, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await reconciling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (shutdown) await service.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Cancelled(update);
            Check(!service.Cats.Single().CanDetect && service.Cats.Single().Generation == 0 &&
                service.RuntimeError is not null, "failed coordination invalidates execution but preserves the list");
            if (shutdown) service.Resume();
            await Code(() => service.DetectAsync(Key("a")), "NOT_READY");
            runtime.OnCall = null;
            await service.RefreshAsync();
            Check(service.Cats.Single().CanDetect, "explicit refresh can recover after failed coordination");
        }
    }

    private static async Task LostOnlineReceipts()
    {
        foreach (string operation in new[] { "update", "restart" })
        foreach (bool cancelled in new[] { false, true })
        {
            var runtime = new Runtime();
            runtime.Add("a", "video", "deleted"); runtime.Add("b", "video", "deleted");
            var store = new Store(runtime);
            await using var service = new FilmSourceService(runtime, store);
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "deleted"));
            await service.DeleteVideoAsync(new("b", "deleted"));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(Key("a"), options, order: 17);
            using var page = new CancellationTokenSource();
            var timeout = new BridgeException("TIMEOUT");
            runtime.OnCall = (method, _, _) =>
            {
                if (method != operation) return null;
                runtime.Nodes["a"].Generation++;
                if (operation == "update") runtime.Nodes["a"].SourceHash = "committed";
                if (!cancelled) return Task.FromException<JsonElement>(timeout);
                page.Cancel();
                return Task.FromException<JsonElement>(new OperationCanceledException(page.Token));
            };
            int before = runtime.Calls.Count;
            Task change = operation == "update" ? service.UpdateCatAsync("a", cancellationToken: page.Token) :
                service.RestartCatAsync("a", page.Token);
            if (cancelled) await Cancelled(change); else await SameError(change, timeout);
            var current = service.Cats.Single(cat => cat.InstallationId == "a");
            Check(current is { CanDetect: true, Generation: 2 } &&
                current.SourceHash == (operation == "update" ? "committed" : "hash") &&
                service.Videos.Select(video => video.Key).ToHashSet().SetEquals([Key("a"), Key("b")]) &&
                service.Videos.Single(video => video.Key == Key("a")) is { Order: 17 } video && video.Options == options,
                "lost online receipts publish only actual runtime facts and preserve committed options and tombstones");
            Check(runtime.Calls.Skip(before).Select(call => call.Method).SequenceEqual([operation, "status"]) &&
                runtime.Uploads.Count == 0, "uncertain online operations are observed once, never replayed");
            Check((await service.DetectAsync(Key("a"))).Result == "valid" &&
                runtime.Invocations.All(call => call.Generation == 2), "the next probe never uses the retired generation");
            await using var reopened = new FilmSourceService(runtime, store);
            await reopened.InitializeAsync();
            Check(reopened.Videos.Select(video => video.Key).ToHashSet().SetEquals([Key("a"), Key("b")]),
                "reconciled facts and suppressed video rows survive a new native owner");
        }
    }

    private static async Task CancelledDetectionReply()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store(runtime);
        await using var service = new FilmSourceService(runtime, store);
        await service.RefreshAsync();
        var baseline = await service.DetectAsync(Key("a"));
        byte[] before = store.Bytes!.ToArray();
        using var page = new CancellationTokenSource();
        runtime.AfterReply = page.Cancel;
        try { await Cancelled(service.DetectAsync(Key("a"), page.Token)); }
        finally { runtime.AfterReply = null; }
        Check(JsonElement.DeepEquals(Element(service.Videos.Single().Detection!), Element(baseline)) &&
            store.Bytes!.SequenceEqual(before),
            "page cancellation after a structured response cannot publish a partial detection or clear the previous result");
    }

    private static async Task TimedOutManagement()
    {
        foreach (bool bridgeFailure in new[] { true, false })
        {
            var runtime = new Runtime(); runtime.Add("a", "video", "deleted");
            await using var service = new FilmSourceService(runtime, new Store(runtime));
            await service.RefreshAsync();
            var original = service.Cats.Single();
            await service.DeleteVideoAsync(new("a", "deleted"));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(Key("a"), options);
            var reconciling = Signal(); var release = Signal();
            using var page = new CancellationTokenSource();
            var timeout = new BridgeException("TIMEOUT");
            int statuses = 0;
            runtime.OnCall = (operation, _, token) => operation switch
            {
                "update" => Update(),
                "status" => Status(token),
                _ => null
            };
            Task<JsonElement> Update()
            {
                runtime.Nodes["a"].State = "starting";
                runtime.Nodes["a"].Generation = 2;
                return bridgeFailure ? Task.FromException<JsonElement>(timeout) :
                    Result(new { ok = false, contract = "cat.script.v2", code = "TIMEOUT" });
            }
            async Task<JsonElement> Status(CancellationToken token)
            {
                if (++statuses == 1)
                {
                    Check(service.Cats.Single().Generation == 0 && !service.Cats.Single().CanDetect,
                        "the cached generation is retired before the first status reply");
                    return runtime.Reply(new { instances = runtime.Nodes.Values.Select(node => node.Wire()).ToArray() });
                }
                reconciling.TrySetResult();
                await release.Task.WaitAsync(token);
                Check(!token.IsCancellationRequested, "page cancellation cannot cancel owner observation after TIMEOUT");
                var cat = runtime.Nodes["a"];
                cat.Generation = 3; cat.State = "ready"; cat.UpdateState = "failed"; cat.UpdateError = "CANCELLED";
                return runtime.Reply(new { instances = runtime.Nodes.Values.Select(node => node.Wire()).ToArray() });
            }
            var update = service.UpdateCatAsync("a", null, page.Token);
            try
            {
                await reconciling.Task.WaitAsync(TimeSpan.FromSeconds(5));
                page.Cancel();
                Check(!update.IsCompleted && !service.Cats.Single().CanDetect && service.Cats.Single().Generation == 2,
                    "TIMEOUT waits for the observed starting generation to settle");
                await Code(() => service.DetectAsync(Key("a")), "BUSY");
                release.TrySetResult();
                if (bridgeFailure) await SameError(update, timeout);
                else await Code(() => update.WaitAsync(TimeSpan.FromSeconds(5)), "TIMEOUT");
                var recovered = service.Cats.Single();
                Check(recovered.CanDetect && recovered.Generation == 3 && recovered.UpdateError == "CANCELLED" &&
                    recovered.SourceHash == original.SourceHash && recovered.ModifiedAt == original.ModifiedAt &&
                    service.RuntimeError is null, "timeout publishes the recovered generation and original metadata");
                Check((await service.DetectAsync(Key("a"))).Result == "valid" &&
                    runtime.Invocations.All(call => call.Generation == 3), "next detection needs no refresh");
                Check(service.Videos.Count == 1 && service.Videos.Single().Options == options,
                    "owner observation preserves the tombstone and all four switches");
                Check(runtime.Uploads.Count == 0 && runtime.Calls.Count(call => call.Method == "update") == 1 &&
                    runtime.Calls.SkipWhile(call => call.Method != "update").Skip(1)
                        .All(call => call.Method is "status" or "invoke"),
                    "timeout only observes status; it never replays management");
            }
            finally { release.TrySetResult(); page.Cancel(); }
        }
    }

    private static async Task TimedOutReconciliationFailure()
    {
        foreach (string operation in new[] { "update", "restart" })
        foreach (string outcome in new[] { "failure", "prepare", "dispose" })
        {
            var runtime = new Runtime(); runtime.Add("a", "video", "deleted");
            var store = new Store(runtime);
            await using var service = new FilmSourceService(runtime, store);
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "deleted"));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(Key("a"), options);
            var reconciling = Signal(); var interrupted = Signal(); var release = Signal(); var settled = Signal();
            var timeout = new BridgeException("TIMEOUT");
            runtime.OnCall = (method, _, token) => method == operation ? Task.FromException<JsonElement>(timeout) :
                method == "status" ? Status(token) : null;
            async Task<JsonElement> Status(CancellationToken token)
            {
                try
                {
                    Check(!service.Cats.Single().CanDetect && service.Cats.Single().Generation == 0,
                        "even a failed status request starts with the old generation retired");
                    reconciling.TrySetResult();
                    if (outcome != "failure")
                    {
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException)
                        {
                            interrupted.TrySetResult();
                            await release.Task; // Model real cleanup that must settle after cancellation.
                            throw;
                        }
                    }
                    throw new BridgeException("CLOSED");
                }
                finally { settled.TrySetResult(); }
            }
            Task management = operation == "update" ? service.UpdateCatAsync("a") : service.RestartCatAsync("a");
            try
            {
                await reconciling.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (outcome != "failure")
                {
                    var shutdown = outcome == "prepare" ? service.PrepareShutdownAsync() : service.DisposeAsync().AsTask();
                    await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!shutdown.IsCompleted && !management.IsCompleted,
                        "shutdown cancels the owner but waits for actual observation cleanup");
                    release.TrySetResult();
                    await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                }
                await SameError(management, timeout);
                Check(settled.Task.IsCompleted && !service.Cats.Single().CanDetect &&
                    service.Cats.Single().Generation == 0 && service.RuntimeError is not null &&
                    service.Videos.Count == 1 && service.Videos.Single().Options == options,
                    "coordination failure keeps the list and options without authorizing the old generation");
                Check(!runtime.Disposed && !store.Disposed && runtime.Calls.Count(call => call.Method == operation) == 1,
                    "shutdown owns its observation, not borrowed dependencies or a management replay");
                if (outcome == "dispose") continue;
                if (outcome == "prepare") service.Resume();
                await Code(() => service.DetectAsync(Key("a")), "NOT_READY");
                runtime.OnCall = null;
                await service.RefreshAsync();
                Check(service.Cats.Single().CanDetect && service.Videos.Count == 1,
                    "explicit recovery after failed coordination remains usable and preserves the tombstone");
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task Tombstones()
    {
        var runtime = new Runtime();
        runtime.Add("a", "shared", "other"); runtime.Add("b", "shared");
        var store = new Store(runtime);
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "shared"));
            await service.DeleteVideoAsync(new("a", "other"));
            await service.DeleteVideoAsync(new("b", "shared"));
            await service.RefreshAsync();
            await service.RestartCatAsync("a");
            await service.UpdateCatAsync("a");
            using var local = new SourceFile(FixtureScript);
            await service.EditCatAsync("a", "本地替换", "local", null, local.Path);
            runtime.Apply(store);
            await service.RefreshAsync();
            await Code(() => service.ImportLocalAsync("重复本地", local.Path, "a"), "STORAGE_CONFLICT");
            await Code(() => service.ImportNetworkAsync("重复网络", "https://fixture.invalid/new.js", "a"), "STORAGE_CONFLICT");
            Check(service.Videos.Count == 0, "update restart type changes and rejected duplicates never restore deletions");
        }
        await using var restored = new FilmSourceService(runtime, store);
        await restored.InitializeAsync();
        await restored.StartAsync();
        Check(restored.Videos.Count == 0 && restored.Cats.Count == 2,
            "cold initialization and runtime startup retain tombstones and configured identities");
        await restored.DeleteCatAsync("a");
        runtime.Apply(store);
        await restored.RefreshAsync();
        Check(restored.Cats.Single().InstallationId == "b" && restored.Videos.Count == 0,
            "native deletion prunes only the selected parent and leaves the other tombstone");
    }

    private static async Task Reconciliation()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b");
        await using var service = new FilmSourceService(runtime, new Store(runtime));
        await service.RefreshAsync();
        var site = runtime.Nodes["a"].Sites.Single();
        runtime.Nodes["a"].State = "starting"; runtime.Nodes["a"].Sites.Clear();
        await service.RefreshAsync();
        Check(service.Videos.Count == 2 && service.Cats.Single(cat => cat.InstallationId == "a").Sites.Count == 1,
            "starting does not empty the cached descriptors");
        runtime.Nodes["a"].State = "failed";
        await service.RefreshAsync();
        Check(service.Videos.Count == 2, "failed instances keep the previous list");
        runtime.Nodes["a"].Enabled = false; runtime.Nodes["a"].State = "disabled";
        await service.RefreshAsync();
        Check(service.Videos.Count == 2, "disabled keeps the previous list");
        runtime.Nodes["a"].Enabled = true; runtime.Nodes["a"].State = "ready";
        runtime.Nodes["a"].Sites.Add(site with { Name = "renamed" });
        await service.RefreshAsync();
        Check(service.Videos.Single(video => video.Key == Key("a")).Name == "renamed", "same identity updates its descriptor");
        runtime.Nodes["a"].Sites.Clear();
        await service.RefreshAsync();
        Check(service.Videos.Single().Key == Key("b"), "ready empty list reconciles only its own parent");
        runtime.Nodes.Remove("b");
        await service.RefreshAsync();
        Check(service.Videos.Single().Key == Key("b") && service.Cats.Count == 2 &&
            !service.Cats.Single(cat => cat.InstallationId == "b").CanDetect,
            "missing runtime parent never deletes configured native intent or its cached video");
    }

    private static async Task DeleteDuringImport()
    {
        var runtime = new Runtime(); runtime.Add("a", "previous", "later");
        var entered = Signal(); var release = Signal();
        await using var service = new FilmSourceService(runtime, new Store(runtime));
        await service.RefreshAsync();
        await service.DeleteVideoAsync(new("a", "previous"));
        runtime.OnCall = (method, _, _) => method == "status" ? Status() : null;
        async Task<JsonElement> Status()
        {
            var reply = runtime.Reply(new { instances = runtime.Nodes.Values.Select(node => node.Wire()).ToArray() });
            entered.TrySetResult();
            await release.Task;
            return reply;
        }
        var refreshing = service.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DeleteVideoAsync(new("a", "later"));
            release.TrySetResult();
            await refreshing;
            Check(service.Videos.Count == 0, "late authoritative site discovery never revives old or newly deleted rows");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task DetectionContract()
    {
        var runtime = new Runtime();
        runtime.Add("a");
        runtime.Invoke = (call, _) =>
        {
            Check(call.InstallationId == "a" && call.Generation == 1 && call.SiteId == "video", "invocation uses parent plus site plus current generation");
            switch (call.Operation)
            {
                case "home": Fields(call.Payload); return Result(new { data = new { types = new[] { new { type_id = "电影" } } } });
                case "category":
                    Fields(call.Payload, "id", "page", "filter", "filters");
                    Check(call.Payload.GetProperty("id").GetString() == "电影" && call.Payload.GetProperty("page").GetInt32() == 1 &&
                        call.Payload.GetProperty("filter").GetBoolean() && !call.Payload.GetProperty("filters").EnumerateObject().Any(),
                        "category follows the dashboard's full parameter contract");
                    return Result(new { data = new { items = new[] { new { vod_id = "电影/一" } } } });
                case "detail":
                    Fields(call.Payload, "id", "vod_id");
                    Check(call.Payload.GetProperty("id").GetString() == "电影/一" && call.Payload.GetProperty("vod_id").GetString() == "电影/一",
                        "detail sends both keys");
                    return Result(new { list = new[] { new { vod_play_from = "线路一|||线路二$$$备用", vod_play_url = "首集$bad#二集$unused|||第一集$good#第二集$unused$$$首集$last" } } });
                case "play":
                    Fields(call.Payload, "flag", "id", "siteApi");
                    Check(call.Payload.GetProperty("siteApi").GetString() == "/spider/fixture", "play carries the declared site API without port identity");
                    string id = call.Payload.GetProperty("id").GetString()!;
                    if (id == "bad") return Result(new { ok = false, error = "line failed" });
                    Check(id == "good" && call.Payload.GetProperty("flag").GetString() == "线路二", "second line's first episode is tried");
                    return Result(new { url = new[] { "清晰", "https://media.invalid/fixture.m3u8" } });
                case "search":
                    Fields(call.Payload, "wd", "page");
                    Check(call.Payload.GetProperty("wd").GetString() == "斗破" && call.Payload.GetProperty("page").GetInt32() == 1,
                        "search uses the reference keyword and page");
                    return Result(new { list = Array.Empty<object>() });
                default: throw new InvalidOperationException("Unexpected fixture operation");
            }
        };
        var store = new Store(runtime);
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.RefreshAsync();
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(Key("a"), options);
            var result = await service.DetectAsync(Key("a"));
            Check(result.Result == "valid" && result.PlaybackResolved && !result.MediaPlaybackVerified,
                "an empty successful search and parsed address satisfy detection, not media playback");
            Check(runtime.Invocations.Select(call => call.Operation).SequenceEqual(["home", "category", "detail", "play", "play", "search"]),
                "real declared operation sequence");
            Check(result.Steps.Any(step => step.Operation == "play" && step.State == "failed") &&
                service.Videos.Single().Options == options, "failed lines are observable and detection never changes options");
        }
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        Check(reopened.Videos.Single().Detection is { Result: "valid", PlaybackResolved: true, MediaPlaybackVerified: false },
            "detection persists locally");
    }

    private static async Task DetectionMatrix()
    {
        foreach (string scenario in new[] { "category-fails", "category-empty", "missing-metadata", "search-fails", "play-fails", "unsupported", "empty", "pan-resource", "invalid-json" })
        {
            var runtime = new Runtime(); runtime.Add("a");
            if (scenario == "unsupported") runtime.Nodes["a"].Sites[0] = Site("video", actions: []);
            runtime.Invoke = (call, _) => call.Operation switch
            {
                "home" => Result(new { @class = Array.Empty<object>() }),
                "category" => scenario switch
                {
                    "category-fails" => Result(new { success = false, message = "category failure" }),
                    "category-empty" or "empty" => Result(new { list = Array.Empty<object>() }),
                    "missing-metadata" => Result(new { list = new[] { new { vod_id = "missing" } } }),
                    _ => Result(new { list = new[] { Playable() } })
                },
                "detail" => Result(new { list = new[] { new { vod_id = "missing" } } }),
                "search" => scenario switch
                {
                    "search-fails" => Result(new { statusCode = 403, message = "search failure" }),
                    "empty" => Result(new { list = Array.Empty<object>() }),
                    _ => Result(new { list = new[] { Playable() } })
                },
                "play" => scenario switch
                {
                    "play-fails" => Result(new { url = "" }),
                    "pan-resource" => Result(new { url = "meow.pan.v1.fixture" }),
                    "invalid-json" => Result("<html>not a Cat result</html>"),
                    _ => Result(new { url = "https://media.invalid/video" })
                },
                _ => throw new InvalidOperationException("Unexpected operation")
            };
            await using var service = new FilmSourceService(runtime, new Store(runtime));
            await service.RefreshAsync();
            var result = await service.DetectAsync(Key("a"));
            string expected = scenario switch
            {
                "category-fails" or "category-empty" => "category_error",
                "missing-metadata" => "valid",
                "search-fails" => "search_error",
                _ => "invalid"
            };
            Check(result.Result == expected, $"reference fallback result: {scenario}");
            if (scenario == "play-fails") Check(runtime.Invocations.All(call => call.Operation != "search"), "known metadata with failed playback does not trigger search fallback");
            if (scenario == "unsupported") Check(runtime.Invocations.Count == 0 && result.Steps.All(step => step.State == "unsupported"),
                "missing declared actions are unsupported without fake invocations");
            if (scenario == "empty") Check(result.Steps.Count(step => step.State == "empty") == 2, "empty category/search are distinct from call failures");
            if (scenario == "pan-resource") Check(!result.PlaybackResolved && result.Steps.Any(step => step.State == "unsupported"),
                "an unresolved pan resource is not reported as a parsed media address");
            if (scenario == "invalid-json") Check(result.Steps.Any(step => step.Operation == "play" && step.State == "failed"),
                "HTTP-like content without valid Cat JSON is a failure");
            foreach (var call in runtime.Invocations.Where(call => call.Operation == "category"))
                Check(call.Payload.GetProperty("id").GetString() == "0", "missing class ID falls back to zero");
        }
    }

    private static async Task DetailErrorFallback()
    {
        foreach (bool withError in new[] { true, false })
        {
            var runtime = new Runtime(); runtime.Add("a");
            runtime.Invoke = (call, _) => call.Operation switch
            {
                "home" => Result(new { @class = new[] { new { type_id = "movies" } } }),
                "category" => Result(new { list = new[] { new { vod_id = "first" } } }),
                "detail" when call.Payload.GetProperty("id").GetString() == "first" =>
                    withError ? Result(new { list = Array.Empty<object>(), error = "分类详情原始错误" }) :
                        Result(new { list = Array.Empty<object>() }),
                "search" => Result(new { list = new[] { new { vod_id = "searched" } } }),
                "detail" when call.Payload.GetProperty("id").GetString() == "searched" =>
                    Result(new { list = new[] { Playable() } }),
                "play" => Result(new { url = "https://media.invalid/video.mp4" }),
                _ => throw new InvalidOperationException("Unexpected fallback operation")
            };
            var store = new Store(runtime);
            FilmSourceDetection detection;
            var options = new FilmVideoOptions(false, false, false, true);
            await using (var service = new FilmSourceService(runtime, store))
            {
                await service.RefreshAsync();
                await service.SaveVideoOptionsAsync(Key("a"), options, order: 500);
                detection = await service.DetectAsync(Key("a"));
                Check(runtime.Invocations.Select(call => call.Operation)
                    .SequenceEqual(["home", "category", "detail", "search", "detail", "play"]),
                    "both empty-detail responses follow the reference search fallback");
                Check(detection is { Result: "valid", PlaybackResolved: true, MediaPlaybackVerified: false },
                    "search playback resolves without claiming media playback");
                Check(detection.Steps.Any(step => step.Operation == "detail" && step.State == "failed" &&
                    step.Error == "分类详情原始错误") == withError,
                    "a script error remains an observable failed detail stage");
                Check(JsonElement.DeepEquals(Element(service.Videos.Single().Detection!), Element(detection)) &&
                    service.Videos.Single().Options == options &&
                    service.Videos.Single().Order == 500, "fallback changes detection, not saved user choices");
            }
            await using var reopened = new FilmSourceService(runtime, store);
            await reopened.InitializeAsync();
            Check(JsonElement.DeepEquals(Element(reopened.Videos.Single().Detection!), Element(detection)) &&
                reopened.Videos.Single().Options == options && reopened.Videos.Single().Order == 500,
                "the complete fallback result and unchanged options survive local reopening");
        }
    }

    private static async Task DetailFailureBoundaries()
    {
        foreach (string failure in new[] { "http", "nested-http", "invoke", "navigation-error", "navigation-http" })
        {
            var runtime = new Runtime(); runtime.Add("a");
            bool navigation = failure.StartsWith("navigation-", StringComparison.Ordinal);
            runtime.Invoke = (call, _) => call.Operation switch
            {
                "home" => Result(new { }),
                "category" => Result(new { list = new[] { new { vod_id = "first" } } }),
                "detail" when navigation && call.Payload.GetProperty("id").GetString() == "first" =>
                    Result(new { list = new[] { new { vod_id = "child", vod_navigation = new
                        { action = "detail", payload = new { id = "child" } } } } }),
                "detail" => failure switch
                {
                    "http" or "navigation-http" => Result(new { statusCode = 503, list = new[] { Playable() } }),
                    "nested-http" => Result(new { error = "script error", data = new { statusCode = 503, list = new[] { Playable() } } }),
                    "invoke" => Task.FromException<JsonElement>(new FilmSourceException("UPSTREAM_HTTP_ERROR")),
                    _ => Result(new { error = "navigation failed", list = new[] { Playable() } })
                },
                "search" => Result(new { list = Array.Empty<object>() }),
                "play" => Result(new { url = "https://media.invalid/must-not-play.mp4" }),
                _ => throw new InvalidOperationException("Unexpected failed detail operation")
            };
            await using var service = new FilmSourceService(runtime, new Store(runtime));
            await service.RefreshAsync();
            var result = await service.DetectAsync(Key("a"));
            Check(result.Result == "invalid" && !result.PlaybackResolved &&
                result.Steps.Any(step => step.Operation == "detail" && step.State == "failed"),
                "failed detail transport or navigation is not successful playback metadata: " + failure);
            Check(runtime.Invocations.Select(call => call.Operation).SequenceEqual(navigation
                    ? ["home", "category", "detail", "detail", "search"] : ["home", "category", "detail"]),
                "HTTP/invoke failure stops initial detail, while an empty navigation tree may only search: " + failure);
        }
    }

    private static async Task FailedNavigationRoot()
    {
        foreach (bool failed in new[] { true, false })
        {
            var runtime = new Runtime(); runtime.Add("a");
            var root = new Dictionary<string, object>
            {
                ["msg"] = "根导航失败",
                ["list"] = new[] { new { vod_id = "child", vod_navigation = new
                    { action = "detail", payload = new { id = "child" } } } }
            };
            if (failed) root["ok"] = false;
            runtime.Invoke = (call, _) => call.Operation switch
            {
                "home" => Result(new { @class = new[] { new { type_id = "movies" } } }),
                "category" => Result(new { list = new[] { new { vod_id = "root" } } }),
                "detail" when call.Payload.GetProperty("id").GetString() == "root" => Result(root),
                "detail" => Result(new { list = new[] { Playable() } }),
                "play" => Result(new { url = "https://media.invalid/video.mp4" }),
                "search" => Result(new { list = Array.Empty<object>() }),
                _ => throw new InvalidOperationException("Unexpected root navigation operation")
            };
            var store = new Store(runtime);
            FilmSourceDetection detection;
            var options = new FilmVideoOptions(false, false, false, true);
            await using (var service = new FilmSourceService(runtime, store))
            {
                await service.RefreshAsync();
                await service.SaveVideoOptionsAsync(Key("a"), options, order: 500);
                detection = await service.DetectAsync(Key("a"));
                Check(runtime.Invocations.Select(call => call.Operation).SequenceEqual(failed
                    ? ["home", "category", "detail", "search"]
                    : ["home", "category", "detail", "detail", "play", "search"]),
                    "failed root navigation cannot dispatch children or play, while the control can");
                Check(detection.Result == (failed ? "invalid" : "valid") &&
                    detection.PlaybackResolved == !failed && !detection.MediaPlaybackVerified,
                    "the root failure is not made valid by child playback metadata");
                Check(detection.Steps.Any(step => step.Operation == "detail" && step.State == "failed" &&
                    step.Error == "根导航失败") == failed, "the failed root remains observable");
                Check(service.Videos.Single().Options == options && service.Videos.Single().Order == 500,
                    "root response classification never changes video options");
            }
            await using var reopened = new FilmSourceService(runtime, store);
            await reopened.InitializeAsync();
            Check(JsonElement.DeepEquals(Element(reopened.Videos.Single().Detection!), Element(detection)) &&
                reopened.Videos.Single().Options == options && reopened.Videos.Single().Order == 500,
                "the classified root result and user options survive reopening");
        }
    }

    private static async Task DetectionFailureOwnership()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store(runtime);
        await using var service = new FilmSourceService(runtime, store);
        await service.RefreshAsync();
        var baseline = await service.DetectAsync(Key("a"));
        var saved = store.Bytes!.ToArray();
        int writes = store.Writes;
        runtime.Invoke = (call, _) => call.Operation == "search"
            ? Task.FromException<JsonElement>(new FilmSourceException("STALE_SESSION")) : DefaultInvoke(call);
        using var caller = new CancellationTokenSource();
        await Cancelled(service.DetectAsync(Key("a"), caller.Token));
        Check(!caller.IsCancellationRequested &&
            JsonElement.DeepEquals(Element(service.Videos.Single().Detection!), Element(baseline)) &&
            store.Writes == writes && store.Bytes!.SequenceEqual(saved),
            "session failure cancels detection without caller cancellation or replacing durable baseline");
        runtime.Invoke = (call, _) => call.Operation == "search"
            ? Task.FromException<JsonElement>(new FilmSourceException("RESOURCE_LIMIT")) : DefaultInvoke(call);
        var liveFailure = await service.DetectAsync(Key("a"), caller.Token);
        Check(liveFailure.Result == "search_error" && liveFailure.PlaybackResolved &&
            liveFailure.Steps.Any(step => step.Operation == "search" && step.State == "failed" && step.Error == "RESOURCE_LIMIT") &&
            JsonElement.DeepEquals(Element(service.Videos.Single().Detection!), Element(liveFailure)),
            "RESOURCE_LIMIT without lifecycle failure remains an ordinary failed stage, not universal cancellation");
    }

    private static async Task Navigation()
    {
        var runtime = new Runtime(); runtime.Add("a");
        runtime.Nodes["a"].Sites[0] = Site("video", actions: ["home", "category", "detail", "play", "search", "folder"]);
        runtime.Invoke = (call, _) => call.Operation switch
        {
            "home" => Result(new { }),
            "category" => Result(new { list = new[] { new { vod_id = "root" } } }),
            "detail" => Result(new { list = new[] { new { vod_id = "folder", vod_navigation = new
                { action = "folder", payload = new { id = "opaque/目录", pg = 1 } } } } }),
            "folder" => NavigationPage(call.Payload),
            "play" => Result(new { data = new { url = "https://media.invalid/navigation" } }),
            "search" => Result(new { list = Array.Empty<object>() }),
            _ => throw new InvalidOperationException("Unexpected operation")
        };
        await using var service = new FilmSourceService(runtime, new Store(runtime));
        await service.RefreshAsync();
        var result = await service.DetectAsync(Key("a"));
        Check(result.Result == "valid" && runtime.Invocations.Count(call => call.Operation == "folder") == 2,
            "navigation follows custom action and page progression");
        Task<JsonElement> NavigationPage(JsonElement payload)
        {
            Fields(payload, "id", "pg");
            Check(payload.GetProperty("id").GetString() == "opaque/目录", "navigation does not decode script-private identifiers");
            int pg = payload.GetProperty("pg").GetInt32();
            Check(pg is 1 or 2, "bounded by the fixture's own pagecount");
            return pg == 1 ? Result(new { page = 1, pagecount = 2, list = new[] { new { vod_id = "unplayable" } } }) :
                Result(new { page = 2, pagecount = 2, list = new[] { Playable() } });
        }
        runtime.Invocations.Clear();
        runtime.Invoke = (call, _) => call.Operation switch
        {
            "home" => Result(new { }),
            "category" => Result(new { list = new[] { new { vod_id = "root" } } }),
            "detail" or "folder" => Result(new { list = new[] { new { vod_id = "folder", vod_navigation = new
                { action = "folder", payload = new { id = "cycle", pg = 1 } } } } }),
            "search" => Result(new { list = Array.Empty<object>() }),
            _ => throw new InvalidOperationException("Unexpected cycle operation")
        };
        result = await service.DetectAsync(Key("a"));
        Check(result.Result == "invalid" && result.Steps.Any(step => step.Error == "NAVIGATION_CYCLE") &&
            runtime.Invocations.Count(call => call.Operation == "folder") == 1,
            "navigation cycles terminate on identity without an arbitrary depth or page cap");
    }

    private static async Task LateResults()
    {
        foreach (string change in new[] { "delete", "disable", "generation", "restart", "newer-detection" })
        {
            var runtime = new Runtime(); runtime.Add("a");
            var entered = Signal(); var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Invoke = async (call, _) =>
            {
                if (call.Operation == "home") { entered.TrySetResult(); return await release.Task; }
                return await DefaultInvoke(call);
            };
            await using var service = new FilmSourceService(runtime, new Store(runtime));
            await service.RefreshAsync();
            var detection = service.DetectAsync(Key("a"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (change)
            {
                case "delete": await service.DeleteVideoAsync(Key("a")); break;
                case "disable": await service.EnableCatAsync("a", false); break;
                case "restart": await service.RestartCatAsync("a"); break;
                case "generation": runtime.Nodes["a"].Generation++; await service.RefreshAsync(); break;
                case "newer-detection":
                    runtime.Invoke = (call, _) => DefaultInvoke(call);
                    await service.DetectAsync(Key("a"));
                    break;
            }
            release.SetResult(Element(new { }));
            await Cancelled(detection);
            Check(change == "newer-detection" ? service.Videos.Single().Detection?.Result == "valid" :
                service.Videos.All(video => video.Detection is null), $"late {change} result cannot publish");
            if (change == "delete") Check(service.Videos.Count == 0, "late detection cannot recreate a deleted video");
        }
    }

    private static async Task PrepareAndResume()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store(runtime);
        await using var service = new FilmSourceService(runtime, store);
        await service.RefreshAsync();
        var entered = Signal(); var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Invoke = async (_, _) => { entered.TrySetResult(); return await release.Task; };
        var detection = service.DetectAsync(Key("a"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var prepare = service.PrepareShutdownAsync();
        Check(!prepare.IsCompleted, "preparation waits for the actual in-flight operation");
        bool refused = false;
        try { service.Resume(); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "resume cannot reopen admission or dispose cancellation ownership before drain");
        await Code(() => service.RefreshAsync(), "SHUTTING_DOWN");
        release.SetResult(Element(new { }));
        await Cancelled(detection);
        await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!runtime.Disposed && !store.Disposed, "preparation does not dispose borrowed dependencies");
        service.Resume();
        runtime.Invoke = (call, _) => DefaultInvoke(call);
        var result = await service.DetectAsync(Key("a"));
        Check(result.Result == "valid", "cancelling app exit restores a usable owner");
    }

    private static async Task Disposal()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store(runtime);
        var service = new FilmSourceService(runtime, store);
        await service.RefreshAsync();
        var entered = Signal(); var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Invoke = async (_, _) => { entered.TrySetResult(); return await release.Task; };
        var detection = service.DetectAsync(Key("a"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = service.DisposeAsync().AsTask();
        Check(!closing.IsCompleted, "dispose drains execution, not only the consumer wait");
        release.SetResult(Element(new { }));
        await Cancelled(detection);
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        Check(!runtime.Disposed && !store.Disposed && service.Videos.Single().Detection is null,
            "late result cannot publish, and the shared runtime/store belong to the app");
    }

    private static async Task SourceAndValues()
    {
        var runtime = new Runtime();
        var store = new Store(runtime);
        await using var service = new FilmSourceService(runtime, store);
        string source = FixtureScript + "\n//" + new string('中', 70000);
        string name = "猫🎬" + new string('文', 70000);
        using var file = new SourceFile(source);
        await service.ImportLocalAsync(name, file.Path, "large");
        using (var configured = JsonDocument.Parse(store.CatBytes!))
        {
            var input = configured.RootElement.GetProperty("scripts")[0];
            Check(input.GetProperty("localHash").GetString() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Path))) &&
                input.GetProperty("name").GetString() == name && !Encoding.UTF8.GetString(store.CatBytes!).Contains(source, StringComparison.Ordinal),
                "the complete selected file is hashed; native intent stores path and Unicode name but never script text");
        }
        runtime.Apply(store);
        await service.RefreshAsync();
        using (var cached = JsonDocument.Parse(store.Bytes!))
        {
            Fields(cached.RootElement, "version", "cats", "videos", "deleted");
            Fields(cached.RootElement.GetProperty("cats")[0], "installationId", "name", "sourceType", "sourceUrl",
                "enabled", "inputRevision", "importedAt", "updatedAt", "updateState", "updateError", "sourceHash", "sites");
        }
        runtime.OnCall = (operation, _, _) => operation == "status"
            ? Task.FromResult(runtime.Reply(new { instances = "invalid" })) : null;
        await Code(() => service.RefreshAsync(), "INVALID_RESPONSE");
        Check(service.Videos.Count == 1 && service.Cats.Single().Name == name && !service.Cats.Single().CanDetect,
            "invalid structured status preserves the local list without authorizing the cached generation");
        runtime.OnCall = null;
        runtime.Nodes["large"].Sites[0] = Site("video", ["home", "category", "detail", "folder", "play", "search"]);
        await service.RefreshAsync();
        string opaque = new('界', 70000);
        runtime.Invoke = (call, _) => call.Operation switch
        {
            "home" => Result(new { }),
            "category" => Result(new { list = new[] { new { vod_id = "root" } } }),
            "detail" => Result(new { list = new[] { new { vod_id = "folder", vod_navigation = new
                { action = "folder", payload = new { id = opaque } } } } }),
            "folder" => LargeNavigation(call.Payload),
            _ => DefaultInvoke(call)
        };
        var detected = await service.DetectAsync(Key("large"));
        Check(detected.PlaybackResolved && runtime.Calls.Any(call => call.Method == "invoke" &&
            call.Input.GetProperty("operation").GetString() == "folder" &&
            call.Input.GetProperty("payload").GetProperty("id").GetString() == opaque) && runtime.Uploads.Count == 0,
            "large arbitrary navigation payload remains a structured object throughout invocation");
        Task<JsonElement> LargeNavigation(JsonElement payload)
        {
            Check(payload.GetProperty("id").GetString() == opaque, "structured payload preserves script-private Unicode");
            return Result(new { list = new[] { Playable() } });
        }
    }

    private static async Task StorageFailures()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store(runtime);
        await using var service = new FilmSourceService(runtime, store);
        await service.RefreshAsync();
        var before = service.Videos.Single().Options;
        store.FailBefore = true;
        try { await service.SaveVideoOptionsAsync(Key("a"), new(false, false, false, true)); throw new InvalidOperationException("Expected storage failure"); }
        catch (DocumentStoreException) { }
        Check(service.Videos.Single().Options == before, "failed write leaves published switches unchanged");
        store.FailAfter = true;
        await service.SaveVideoOptionsAsync(Key("a"), new(false, false, false, true));
        Check(service.Videos.Single().Options == new FilmVideoOptions(false, false, false, true),
            "lost receipt is reconciled against the actual committed document");
        var corrupt = new Store { Bytes = Encoding.UTF8.GetBytes("{invalid") };
        await using var broken = new FilmSourceService(runtime, corrupt);
        int calls = runtime.Calls.Count;
        await Code(() => broken.InitializeAsync(), "INVALID_DOCUMENT");
        Check(runtime.Calls.Count == calls && corrupt.Writes == 0, "bad local document is reported without replacing it or starting Node");
    }

    private static async Task ManagementStorageFailures()
    {
        foreach (bool persistent in new[] { false, true })
        {
            const string operation = "update";
            var runtime = new Runtime();
            runtime.Add("a", "video", "deleted"); runtime.Add("b", "video", "deleted");
            var store = new Store(runtime);
            await using var service = new FilmSourceService(runtime, store);
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "deleted"));
            await service.DeleteVideoAsync(new("b", "deleted"));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(Key("a"), options, order: 500);
            byte[] beforeBytes = store.Bytes!.ToArray();
            var failure = new DocumentStoreException(DocumentStoreError.Storage);
            store.WriteError = failure;
            store.FailBefore = true;
            store.FailAlways = persistent;
            int calls = runtime.Calls.Count, observations = 0;
            runtime.OnCall = (method, _, _) =>
            {
                if (method == "status" && store.WriteFailures > 0)
                {
                    observations++;
                    Check(service.Cats.All(cat => !cat.CanDetect && cat.Generation == 0),
                        "local write failure retires cached generations before reading authoritative status");
                }
                return null;
            };
            Task change = service.UpdateCatAsync("a");
            await SameError(change, failure);
            Check(observations > 0 && runtime.Uploads.Count == 0 &&
                runtime.Calls.Skip(calls).Count(call => call.Method == operation) == 1 &&
                runtime.Calls.SkipWhile(call => call.Method != operation).Skip(1)
                    .All(call => call.Method == "status"),
                "storage recovery observes status without replaying the committed management command");
            if (persistent)
            {
                Check(service.Cats.All(cat => !cat.CanDetect && cat.Generation == 0) &&
                    service.RuntimeError is not null && store.Bytes!.SequenceEqual(beforeBytes),
                    "persistent write failure publishes no new document and never authorizes a retired generation");
                await Code(() => service.DetectAsync(Key("a")), "NOT_READY");
            }
            else
            {
                var cat = service.Cats.Single(cat => cat.InstallationId == "a");
                Check(cat.Generation == runtime.Nodes["a"].Generation &&
                    cat.SourceHash == runtime.Nodes["a"].SourceHash &&
                    cat.CanDetect && service.RuntimeError is null,
                    "a settled local write publishes only the actual runtime generation");
                Check((await service.DetectAsync(Key("a"))).Result == "valid" &&
                    runtime.Invocations.All(call => call.Generation == cat.Generation),
                    "the next probe uses the new generation, never the retired one");
            }
            FilmSourceKey[] expected = [Key("a"), Key("b")];
            Check(service.Videos.Select(video => video.Key).ToHashSet().SetEquals(expected),
                "failed online management cannot clear either parent's video tombstones");
            if (expected.Contains(Key("a")))
                Check(service.Videos.Single(video => video.Key == Key("a")) is { Order: 500 } video &&
                    video.Options == options, "all persisted user switches and sort order survive the failure");
            await using var reopened = new FilmSourceService(runtime, store);
            await reopened.InitializeAsync();
            Check(reopened.Videos.Select(video => video.Key).ToHashSet().SetEquals(expected) &&
                reopened.Cats.All(cat => !cat.CanDetect), "the last confirmed local state survives a new owner");
        }
    }

    private static async Task StorageFailureShutdown()
    {
        foreach (bool dispose in new[] { false, true })
        {
            var runtime = new Runtime(); runtime.Add("a", "video", "deleted");
            var store = new Store(runtime);
            await using var service = new FilmSourceService(runtime, store);
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "deleted"));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(Key("a"), options);
            var failure = new DocumentStoreException(DocumentStoreError.Storage);
            store.WriteError = failure; store.FailBefore = true;
            var observing = Signal(); var interrupted = Signal(); var release = Signal();
            runtime.OnCall = (method, _, token) => method == "status" && store.WriteFailures > 0
                ? Observe(token) : null;
            async Task<JsonElement> Observe(CancellationToken token)
            {
                Check(service.Cats.All(cat => !cat.CanDetect), "storage failure observation revokes detection first");
                observing.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { interrupted.TrySetResult(); await release.Task; throw; }
                throw new InvalidOperationException("Expected owner cancellation");
            }
            var change = service.UpdateCatAsync("a");
            try
            {
                await observing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var close = dispose ? service.DisposeAsync().AsTask() : service.PrepareShutdownAsync();
                await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!close.IsCompleted && !change.IsCompleted, "shutdown waits for real status-request cleanup");
                release.TrySetResult();
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                await SameError(change, failure);
                Check(service.Cats.All(cat => !cat.CanDetect) && service.Videos.Single().Options == options &&
                    runtime.Calls.Count(call => call.Method == "update") == 1 && !runtime.Disposed && !store.Disposed,
                    "shutdown preserves the original storage error, user state and borrowed dependencies");
                if (!dispose)
                {
                    service.Resume();
                    runtime.OnCall = null;
                    await service.RefreshAsync();
                    Check(service.Cats.Single().CanDetect && service.Videos.Count == 1,
                        "resumed refresh reconciles the new generation without reviving the deleted site");
                }
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task StartupFailure()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store(runtime);
        await using (var first = new FilmSourceService(runtime, store)) await first.RefreshAsync();
        runtime.FailNext = "start";
        await using var service = new FilmSourceService(runtime, store);
        int notifications = 0, calls = runtime.Calls.Count;
        service.Changed += (_, _) => notifications++;
        await service.InitializeAsync();
        Check(runtime.Calls.Count == calls && service.Videos.Count == 1, "cached list is available before Node");
        await Code(() => service.StartAsync(), "SERVICE_ERROR");
        Check(service.RuntimeError == "SERVICE_ERROR" && notifications >= 2 && service.Videos.Count == 1 &&
            !service.Cats.Single().CanDetect, "startup failure is observable without destroying local state");
        await service.SaveVideoOptionsAsync(Key("a"), new(false, true, false, false));
        await service.StartAsync();
        Check(service.RuntimeError is null && service.Cats.Single().CanDetect, "startup can be retried after failure");
    }

    private static async Task ExplicitRuntimeRecovery()
    {
        var session = new Runtime { StartingStatuses = 1 }; session.Add("restored");
        var store = new Store(session);
        int factories = 0;
        await using var runtime = new AppRuntimeClient(_ =>
        {
            if (++factories == 1) return Task.FromException<IRuntimeSession>(new BridgeException("INTERNAL"));
            session.Apply(store); // A new runtime epoch reads the final native input, not a queued operation list.
            return Task.FromResult<IRuntimeSession>(session);
        });
        await using var service = new FilmSourceService(runtime, store);
        await service.InitializeAsync();
        Check(factories == 0, "local initialization never enters the runtime factory");
        runtime.Start();
        try { await service.StartAsync(); throw new InvalidOperationException("Expected factory failure"); }
        catch (BridgeException error) { Check(error.Code == "INTERNAL", "failed factory is observable"); }
        Check(service.RuntimeError is not null && runtime.CanRetry && factories == 1, "first failed epoch is retained");
        await service.ImportNetworkAsync("离线保存", "https://fixture.invalid/new.js", "new");
        Check(factories == 1 && session.Calls.Count == 0 && service.Cats.Any(cat => cat.InstallationId == "new"),
            "ordinary saving remains available in a failed host epoch without retrying it");
        await service.RefreshAsync();
        Check(factories == 2 && !runtime.CanRetry && service.RuntimeError is null &&
            service.Cats.Single(cat => cat.InstallationId == "restored").CanDetect &&
            service.Cats.Any(cat => cat.InstallationId == "new"),
            "explicit refresh creates a fresh epoch then synchronizes status");
        Check(session.Calls.All(call => call.Method == "status"), "recovery does not replay install/update/start business calls");
        await service.RefreshAsync();
        Check(factories == 2, "healthy refresh does not recreate the runtime");
    }

    // Authored CatNode direct-Spider fixture contract. Runtime below validates its
    // RPC/payload surface; it does not execute JS and is not native-script evidence.
    private const string FixtureScript = """
        module.exports = {
          home: async () => ({class:[{type_id:'电影'}]}),
          category: async (id,page,filter,filters) => ({list:[{vod_id:'电影/一'}]}),
          detail: async id => ({list:[{vod_play_from:'线路一',vod_play_url:'首集$fixture'}]}),
          play: async (flag,id) => ({url:'https://media.invalid/fixture.m3u8'}),
          search: async (wd,quick,page) => ({list:[]})
        };
        """;

    private static TestCase Case(string name, Func<Task> run) => new("film-sources: " + name, run);
    private static FilmSourceKey Key(string parent) => new(parent, "video");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static Task<JsonElement> Result(object value) => Task.FromResult(Element(value));
    private static object Playable() => new { vod_id = "fixture", vod_play_from = "线路", vod_play_url = "首集$fixture#第二集$unused" };
    private static Task<JsonElement> DefaultInvoke(Invocation call) => call.Operation switch
    {
        "home" => Result(new { @class = new[] { new { type_id = "1" } } }),
        "category" => Result(new { list = new[] { Playable() } }),
        "play" => Result(new { url = "https://media.invalid/fixture.m3u8" }),
        "search" => Result(new { list = Array.Empty<object>() }),
        _ => throw new InvalidOperationException("Unexpected default fixture operation")
    };
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Fields(JsonElement value, params string[] fields) =>
        Check(value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal)), "Unexpected fixture payload fields: " + value.GetRawText());
    private static async Task Code(Func<Task> action, string code)
    {
        try { await action(); }
        catch (FilmSourceException error) { Check(error.Code == code, $"Expected {code}, received {error.Code}"); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }
    private static async Task SameError(Task task, Exception expected)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error)
        {
            Check(ReferenceEquals(error, expected), "the original management exception must survive reconciliation");
            return;
        }
        throw new InvalidOperationException("Expected " + expected.Message);
    }
    private sealed record RpcCall(string Method, JsonElement Input);
    private sealed record Invocation(string InstallationId, long Generation, string SiteId, string Operation, JsonElement Payload);
    private sealed record SiteFixture(string Key, string Name, string Api, string[] Actions)
    {
        internal object Wire() => new { key = Key, name = Name, api = Api, actions = Actions,
            methods = Actions.ToDictionary(action => action, action => action == "home" ? "GET" : "POST"), discovery = "declared" };
    }
    private static SiteFixture Site(string key, string[]? actions = null) =>
        new(key, "视频 " + key, "/spider/fixture", actions ?? ["home", "category", "detail", "play", "search"]);
    private sealed class CatFixture(string id)
    {
        internal string Name = "猫 " + id, SourceType = "network", State = "ready", UpdateState = "idle", SourceHash = "hash";
        internal string? SourceUrl = "https://fixture.invalid/cat.js", UpdateError;
        internal long InputRevision = 1;
        internal long Generation = 1, ImportedAt = 100;
        internal long? UpdatedAt;
        internal bool Enabled = true;
        internal List<SiteFixture> Sites = [Site("video")];
        internal object Wire() => new { installationId = id, name = Name, sourceType = SourceType, sourceUrl = SourceUrl,
            enabled = Enabled, importedAt = ImportedAt, updatedAt = UpdatedAt, updateState = UpdateState, updateError = UpdateError,
            inputRevision = InputRevision, generation = Generation, state = State, sourceHash = SourceHash, error = State == "failed" ? "SCRIPT_EXCEPTION" : null,
            sites = Sites.Select(site => site.Wire()).ToArray() };
    }
    private sealed class Runtime : IRuntimeSession
    {
        public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Image requests are not part of this fixture.");

        internal readonly Dictionary<string, CatFixture> Nodes = new(StringComparer.Ordinal);
        internal readonly List<RpcCall> Calls = [];
        internal readonly List<Invocation> Invocations = [];
        internal readonly List<string> Uploads = [];
        internal Func<Invocation, CancellationToken, Task<JsonElement>> Invoke = (call, _) => DefaultInvoke(call);
        internal string? FailNext;
        internal string NextUpdateState = "updated";
        internal Action? AfterReply;
        internal Func<string, JsonElement, CancellationToken, Task<JsonElement>?>? OnCall;
        internal int StartingStatuses;
        internal bool Disposed;
        internal void Add(string id, params string[] keys)
        {
            Nodes[id] = new(id);
            if (keys.Length > 0) Nodes[id].Sites = keys.Select(key => Site(key)).ToList();
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            Uploads.Add(installationId);
            throw new InvalidOperationException("Native Cat settings never upload script bytes.");
        }
        internal void Apply(Store store)
        {
            var inputs = JsonSerializer.Deserialize<JsonElement>(store.CatBytes!).GetProperty("scripts");
            var ids = inputs.EnumerateArray().Select(input => input.GetProperty("installationId").GetString()!).ToHashSet();
            foreach (string id in Nodes.Keys.Where(id => !ids.Contains(id)).ToArray()) Nodes.Remove(id);
            foreach (var input in inputs.EnumerateArray())
            {
                string id = input.GetProperty("installationId").GetString()!;
                if (!Nodes.ContainsKey(id)) Add(id);
                var node = Nodes[id];
                node.Name = input.GetProperty("name").GetString()!;
                node.SourceType = input.GetProperty("sourceType").GetString()!;
                node.SourceUrl = input.GetProperty("sourceUrl").GetString();
                node.InputRevision = input.GetProperty("inputRevision").GetInt64();
                node.Enabled = input.GetProperty("enabled").GetBoolean();
                node.State = node.Enabled ? "ready" : "disabled";
            }
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(method.StartsWith("cat.script.", StringComparison.Ordinal), "Cat protocol only");
            string operation = method["cat.script.".Length..];
            var input = Element(parameters ?? new { });
            Calls.Add(new(operation, input));
            if (OnCall?.Invoke(operation, input, cancellationToken) is { } intercepted) return await intercepted;
            if (FailNext == operation)
            {
                FailNext = null;
                if (operation == "update")
                {
                    var failed = Nodes[input.GetProperty("installationId").GetString()!];
                    failed.UpdateState = "failed"; failed.UpdateError = "SCRIPT_EXCEPTION";
                }
                return Failure(operation == "start" ? "SERVICE_ERROR" : "SCRIPT_EXCEPTION");
            }
            if (operation is "status" or "start")
            {
                Fields(input);
                if (operation == "status" && StartingStatuses > 0)
                {
                    StartingStatuses--;
                    var states = Nodes.Values.ToDictionary(entry => entry, entry => entry.State);
                    foreach (var entry in Nodes.Values.Where(entry => entry.Enabled)) entry.State = "starting";
                    var reply = Reply(new { instances = Nodes.Values.Select(entry => entry.Wire()).ToArray() });
                    foreach (var entry in states) entry.Key.State = entry.Value;
                    return reply;
                }
                return Reply(new { instances = Nodes.Values.Select(entry => entry.Wire()).ToArray() });
            }
            string id = input.GetProperty("installationId").GetString()!;
            if (operation == "invoke")
            {
                Fields(input, "installationId", "generation", "siteId", "operation", "payload");
                var payload = input.GetProperty("payload");
                Check(payload.ValueKind == JsonValueKind.Object, "Cat invocation carries a structured object");
                var call = new Invocation(id, input.GetProperty("generation").GetInt64(), input.GetProperty("siteId").GetString()!,
                    input.GetProperty("operation").GetString()!, payload.Clone());
                Check(Nodes[id].Sites.Any(site => site.Key == call.SiteId && site.Actions.Contains(call.Operation)), "invocation must use a declared site action");
                Invocations.Add(call);
                return Reply(await Invoke(call, cancellationToken)); // May deliberately ignore cancellation to test fencing.
            }
            var cat = Nodes[id];
            switch (operation)
            {
                case "update":
                    Fields(input, "installationId");
                    Check(cat.SourceType == "network", "local updates do not call the runtime");
                    cat.UpdateState = NextUpdateState; cat.UpdateError = null;
                    if (NextUpdateState == "updated") { cat.UpdatedAt = 200; cat.Generation++; cat.SourceHash = "updated"; }
                    break;
                case "restart": Fields(input, "installationId"); cat.Generation++; break;
                default: throw new InvalidOperationException("Unexpected RPC " + operation);
            }
            return Reply(cat.Wire());
        }
        internal JsonElement Reply(object value)
        {
            var reply = Element(new { ok = true, contract = "cat.script.v2", value });
            AfterReply?.Invoke();
            return reply;
        }
        private static JsonElement Failure(string code) => Element(new { ok = false, contract = "cat.script.v2", code });
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class SourceFile : IDisposable
    {
        internal string Path { get; } = TestWorkspace.DataPath("film-source-files", Guid.NewGuid().ToString("N") + ".js");
        internal SourceFile(string source)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, source, new UTF8Encoding(false));
        }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class Store : IDocumentStore
    {
        internal const string CatNamespace = "film.cat-sources.v1";
        internal byte[]? Bytes, CatBytes;
        internal Store(Runtime? runtime = null)
        {
            if (runtime is not null) CatBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, port = 0, providers = new { }, mappings = Array.Empty<object>(),
                scripts = runtime.Nodes.Select(pair => new
                {
                    installationId = pair.Key, name = pair.Value.Name, sourceType = pair.Value.SourceType,
                    sourceUrl = pair.Value.SourceUrl, localPath = (string?)null,
                    localHash = pair.Value.SourceType == "local" ? new string('0', 64) : null,
                    enabled = pair.Value.Enabled, inputRevision = pair.Value.InputRevision
                }).ToArray()
            });
        }
        internal int Reads, Writes, WriteFailures;
        internal bool FailBefore, FailAfter, FailAlways, Disposed;
        internal DocumentStoreException? WriteError;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            Check(documentNamespace is FilmSourceService.DocumentNamespace or CatNamespace, "known native document namespace");
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            var bytes = documentNamespace == CatNamespace ? CatBytes : Bytes;
            return Task.FromResult(new DocumentSnapshot(bytes?.ToArray(),
                bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
        }
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Check(documentNamespace is FilmSourceService.DocumentNamespace or CatNamespace, "known native document namespace");
            cancellationToken.ThrowIfCancellationRequested();
            if (FailBefore || FailAlways)
            {
                FailBefore = false; WriteFailures++;
                throw WriteError ?? new DocumentStoreException(DocumentStoreError.Storage);
            }
            var bytes = documentNamespace == CatNamespace ? CatBytes : Bytes;
            var previous = bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes);
            if (previous != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            bytes = data.ToArray();
            if (documentNamespace == CatNamespace) CatBytes = bytes; else Bytes = bytes;
            Writes++;
            if (FailAfter) { FailAfter = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
            return Task.FromResult(DocumentFingerprint.FromBytes(bytes));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
