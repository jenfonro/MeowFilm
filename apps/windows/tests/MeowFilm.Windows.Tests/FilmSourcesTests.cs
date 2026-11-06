using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmSourcesTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("local initialization options order and aggregate choice survive restart", LocalState);
        yield return Case("management uses the Cat wire and status metadata without owning the runtime", Management);
        yield return Case("uninstall reports target cleanup failures and reconciles committed error receipts", UninstallFailures);
        yield return Case("tombstones isolate parents and only successful explicit reimport restores included sites", Tombstones);
        yield return Case("explicit network reimport enables and restores only its successful current list", NetworkReimport);
        yield return Case("a new deletion while reimport is running wins over the late receipt", DeleteDuringImport);
        yield return Case("starting failed disabled and missing parents reconcile without empty-list loss", Reconciliation);
        yield return Case("dashboard fixture sends home category detail play search parameters", DetectionContract);
        yield return Case("dashboard fallback matrix distinguishes errors empty data and missing actions", DetectionMatrix);
        yield return Case("detail navigation preserves custom actions payloads and advancing pages", Navigation);
        yield return Case("deleted disabled and replaced generations fence noncooperative late detections", LateResults);
        yield return Case("shutdown preparation drains and resume admits new work without disposal", PrepareAndResume);
        yield return Case("final shutdown drains late work and leaves borrowed dependencies alive", Disposal);
        yield return Case("source and raw JSON blob transfers verify chunks hashes and cleanup receipts", Transfers);
        yield return Case("cancelled stage and blob receipts are released without publishing results", CancelledTransfers);
        yield return Case("failed local saves do not publish and lost commit receipts reconcile", StorageFailures);
        yield return Case("startup failure is observable and local initialization never contacts Node", StartupFailure);
        yield return Case("explicit refresh recovers a failed host epoch without replaying management", ExplicitRuntimeRecovery);
    }

    private static async Task LocalState()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b");
        var store = new Store();
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.InitializeAsync();
            Check(runtime.Calls.Count == 0 && store.Reads == 1, "initialization only reads the local document");
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
        var store = new Store();
        await using var service = new FilmSourceService(runtime, store);
        var local = await service.ImportLocalAsync("本地猫", FixtureScript, "local");
        Check(local.Enabled && local.SourceType == "local" && local.ImportedAt == 100 && local.ModifiedAt == 100,
            "local install defaults enabled and uses Node timestamps");
        Check(runtime.UploadedSource == FixtureScript && runtime.Stages.Count == 0, "source upload is consumed and cleaned");
        var install = runtime.Calls.Single(call => call.Method == "install").Input;
        Fields(install, "installationId", "token", "name", "sourceType", "sourceUrl", "enabled");
        Check(install.GetProperty("sourceType").GetString() == "local", "local install metadata");
        Check(install.GetProperty("sourceUrl").ValueKind == JsonValueKind.Null, "local reimport explicitly clears a previous network URL");
        var network = await service.ImportNetworkAsync("网络猫", "https://fixture.invalid/cat.js", "net");
        Check(network.SourceType == "network" && network.SourceUrl == "https://fixture.invalid/cat.js", "URL stays in public Node metadata");
        await service.EditCatAsync("net", "新名称", "https://fixture.invalid/changed.js");
        Check(service.Cats.Single(cat => cat.InstallationId == "net").Name == "新名称", "edit synchronizes authoritative metadata");
        await service.EditCatAsync("local", "本地改名");
        Fields(runtime.Calls.Last(call => call.Method == "edit").Input, "installationId", "name");
        await service.UpdateCatAsync("local", FixtureScript + "\n// changed");
        Check(runtime.Calls.Last(call => call.Method == "update").Input.TryGetProperty("token", out _), "local update stages selected contents");
        long? modified = service.Cats.Single(cat => cat.InstallationId == "local").ModifiedAt;
        await service.RestartCatAsync("local");
        Check(service.Cats.Single(cat => cat.InstallationId == "local").ModifiedAt == modified, "restart does not synthesize an update time");
        await service.UpdateCatAsync("net");
        Fields(runtime.Calls.Last(call => call.Method == "update").Input, "installationId");
        Check(service.Cats.Single(cat => cat.InstallationId == "net").UpdatedAt == 200, "successful network update uses its reported time");
        runtime.NextUpdateState = "unchanged";
        await service.UpdateCatAsync("net");
        Check(service.Cats.Single(cat => cat.InstallationId == "net").ModifiedAt == 200, "unchanged content retains modification time");
        runtime.FailNext = "update";
        await Code(() => service.UpdateCatAsync("net"), "UPDATE_FAILED");
        var failed = service.Cats.Single(cat => cat.InstallationId == "net");
        Check(failed.UpdateState == "failed" && failed.UpdateError == "UPDATE_FAILED" && failed.CanDetect &&
            failed.ModifiedAt == 200 && service.Videos.Count == 2, "failed update resynchronizes metadata and keeps old descriptors");
        await service.EnableCatAsync("net", false);
        int count = runtime.Calls.Count;
        await Code(() => service.RestartCatAsync("net"), "DISABLED");
        await Code(() => service.DetectAsync(Key("net")), "DISABLED");
        Check(!runtime.Calls.Skip(count).Any(call => call.Method is "restart" or "invoke") && service.Videos.Count == 2,
            "disabled cats retain the list and cannot execute restart or detection");
        await service.DeleteCatAsync("local");
        Check(service.Cats.Single().InstallationId == "net" && service.Videos.Single().Key == Key("net"),
            "uninstall removes only that parent's children");
        Check(runtime.Calls.Where(call => call.Method is "install" or "import" or "edit" or "update" or "restart" or "enable" or "uninstall")
            .All(call => runtime.Calls.Skip(runtime.Calls.IndexOf(call) + 1).Any(next => next.Method == "status")),
            "management receipts are followed by authoritative status");
    }

    private static async Task UninstallFailures()
    {
        foreach (string scenario in new[] { "cleanup", "error-receipt", "other-installation" })
        {
            var runtime = new Runtime();
            runtime.Add("a", "video", "deleted"); runtime.Add("b", "video", "deleted");
            await using var service = new FilmSourceService(runtime, new Store());
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "deleted"));
            await service.DeleteVideoAsync(new("b", "deleted"));
            runtime.Uninstall = id =>
            {
                Check(id == "a", "uninstall targets one installation");
                runtime.Nodes.Remove(id);
                if (scenario == "error-receipt")
                    return Element(new { ok = false, contract = "cat.script.v1", code = "COMMIT_UNKNOWN" });
                var failures = scenario == "cleanup"
                    ? new[] { new { installationId = "b", code = "SERVICE_ERROR" }, new { installationId = "a", code = "STORAGE_ERROR" } }
                    : new[] { new { installationId = "b", code = "SERVICE_ERROR" } };
                return Element(new { ok = true, contract = "cat.script.v1",
                    data = JsonSerializer.Serialize(new { applied = false, failures }) });
            };
            int before = runtime.Calls.Count;
            if (scenario == "other-installation") await service.DeleteCatAsync("a");
            else await Code(() => service.DeleteCatAsync("a"), scenario == "cleanup" ? "STORAGE_ERROR" : "COMMIT_UNKNOWN");
            Check(runtime.Calls.Skip(before).Select(call => call.Method).SequenceEqual(["uninstall", "status"]),
                "both payload cleanup failures and error receipts coordinate with authoritative status");
            Check(service.Cats.Single().InstallationId == "b" && service.Videos.Single().Key == Key("b"),
                "committed uninstall stays removed locally even when its cleanup or receipt reports failure");
            runtime.Add("a", "video", "deleted");
            await service.RefreshAsync();
            Check(service.Videos.Where(video => video.Key.InstallationId == "a").Select(video => video.Key.SiteKey)
                    .ToHashSet().SetEquals(["video", "deleted"]) &&
                service.Videos.Where(video => video.Key.InstallationId == "b").Select(video => video.Key.SiteKey)
                    .SequenceEqual(["video"]),
                "committed uninstall clears only the removed parent's tombstones");
        }
    }

    private static async Task Tombstones()
    {
        var runtime = new Runtime();
        runtime.Add("a", "shared", "other"); runtime.Add("b", "shared");
        var store = new Store();
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "shared"));
            await service.DeleteVideoAsync(new("a", "other"));
            await service.DeleteVideoAsync(new("b", "shared"));
            await service.RefreshAsync();
            await service.RestartCatAsync("a");
            await service.UpdateCatAsync("a", FixtureScript);
            Check(service.Videos.Count == 0, "refresh restart and ordinary update do not revive deleted videos");
        }
        await using var restored = new FilmSourceService(runtime, store);
        await restored.InitializeAsync();
        await restored.StartAsync();
        Check(restored.Videos.Count == 0, "application restart and startup keep tombstones");
        runtime.FailNext = "install";
        await Code(() => restored.ImportLocalAsync("failed", FixtureScript, "a"), "IMPORT_FAILED");
        Check(restored.Videos.Count == 0, "failed explicit import restores nothing");
        runtime.ImportState = "failed";
        await Code(() => restored.ImportLocalAsync("not-ready", FixtureScript, "a"), "IMPORT_FAILED");
        Check(restored.Videos.Count == 0, "successful transport with failed instance is not successful import");
        runtime.ImportState = "ready";
        runtime.NextSites = ["shared"];
        await restored.ImportLocalAsync("reimport", FixtureScript, "a");
        Check(restored.Videos.Count == 1 && restored.Videos[0].Key == new FilmSourceKey("a", "shared"),
            "successful reimport only restores the included keys for that parent");
        runtime.Nodes["a"].Sites.Add(Site("other"));
        await restored.RefreshAsync();
        Check(restored.Videos.Count == 1, "a deleted site absent from reimport remains suppressed when it later reappears");
        await restored.DeleteCatAsync("a");
        runtime.NextSites = ["shared", "other"];
        await restored.ImportLocalAsync("fresh", FixtureScript, "a");
        Check(restored.Videos.Count == 2 && restored.Videos.All(video => video.Key.InstallationId == "a"),
            "parent removal clears its tombstones, preserving the other parent's tombstones");
    }

    private static async Task Reconciliation()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b");
        await using var service = new FilmSourceService(runtime, new Store());
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
        Check(service.Videos.Count == 0 && service.Cats.Single().InstallationId == "a", "missing parent is authoritatively removed");
    }

    private static async Task NetworkReimport()
    {
        var runtime = new Runtime(); runtime.Add("a", "old", "other", "later");
        runtime.Nodes["a"].SourceType = "network"; runtime.Nodes["a"].SourceUrl = "https://fixture.invalid/source.js";
        var store = new Store();
        await using (var service = new FilmSourceService(runtime, store))
        {
            await service.RefreshAsync();
            await service.DeleteVideoAsync(new("a", "old"));
            await service.DeleteVideoAsync(new("a", "other"));
            await service.EnableCatAsync("a", false);
            runtime.NextSites = ["old", "later"];
            var imported = await service.ImportNetworkAsync("replacement", "https://fixture.invalid/new.js", "a");
            Check(imported.Enabled && imported.State == "ready" && service.Videos.Select(video => video.Key.SiteKey)
                .ToHashSet().SetEquals(["old", "later"]), "explicit reimport enables and discovers the actual replacement list");
            Check(runtime.Calls.Last(call => call.Method == "import").Input.GetProperty("enabled").GetBoolean(),
                "network import explicitly enables just like local import");
            await service.DeleteVideoAsync(new("a", "later"));
        }
        await using var reopened = new FilmSourceService(runtime, store);
        await reopened.InitializeAsync();
        await reopened.StartAsync();
        Check(reopened.Videos.Count == 1 && reopened.Videos.Single().Key.SiteKey == "old",
            "later deletion remains effective across app restart");
        runtime.Nodes["a"].Sites.Add(Site("other"));
        await reopened.RefreshAsync();
        Check(reopened.Videos.Count == 1, "a deleted site absent from the successful reimport stays suppressed on later sync");
    }

    private static async Task DeleteDuringImport()
    {
        var runtime = new Runtime(); runtime.Add("a", "previous", "later");
        var entered = Signal(); var release = Signal();
        await using var service = new FilmSourceService(runtime, new Store());
        await service.RefreshAsync();
        await service.DeleteVideoAsync(new("a", "previous"));
        runtime.BeforeInstall = async () => { entered.TrySetResult(); await release.Task; };
        var importing = service.ImportLocalAsync("replacement", FixtureScript, "a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DeleteVideoAsync(new("a", "later"));
        release.SetResult();
        await importing;
        Check(service.Videos.Single().Key.SiteKey == "previous",
            "explicit import restores prior tombstones while respecting later user deletion");
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
        var store = new Store();
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
            await using var service = new FilmSourceService(runtime, new Store());
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
        await using var service = new FilmSourceService(runtime, new Store());
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
            await using var service = new FilmSourceService(runtime, new Store());
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
        var store = new Store();
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
        var store = new Store();
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

    private static async Task Transfers()
    {
        var runtime = new Runtime { BlobReplies = true };
        var store = new Store();
        await using var service = new FilmSourceService(runtime, store);
        string source = FixtureScript + "\n//" + new string('中', 70000);
        string name = "猫🎬" + new string('文', 70000);
        await service.ImportLocalAsync(name, source, "large");
        Check(runtime.UploadedSource == source && service.Cats.Single().Name == name &&
            runtime.Calls.Count(call => call.Method == "stage.chunk") > 1 &&
            runtime.Calls.Count(call => call.Method == "blob.read") > 1 &&
            runtime.Stages.Count == 0 && runtime.Blobs.Count == 0, "chunked source and raw JSON blobs preserve Unicode with cleanup");
        runtime.CorruptBlobHash = true;
        await Code(() => service.RefreshAsync(), "INVALID_RESPONSE");
        Check(runtime.Blobs.Count == 0 && service.Videos.Count == 1, "corrupt blob is released without clearing the local list");
        runtime.CorruptBlobHash = false;
        runtime.BadChunkAck = true;
        int installs = runtime.Calls.Count(call => call.Method == "install");
        await Code(() => service.ImportLocalAsync("bad transfer", source, "bad"), "INVALID_RESPONSE");
        Check(runtime.Stages.Count == 0 && runtime.Calls.Count(call => call.Method == "install") == installs,
            "invalid stage acknowledgement prevents install and aborts staged bytes");
        runtime.BadChunkAck = false;
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
            call.Input.TryGetProperty("payloadToken", out _)) && runtime.Stages.Count == 0 && runtime.Blobs.Count == 0,
            "large arbitrary navigation payload uses JSON staging and cleans all transfers");
        Task<JsonElement> LargeNavigation(JsonElement payload)
        {
            Check(payload.GetProperty("id").GetString() == opaque, "JSON staging preserves script-private Unicode payload");
            return Result(new { list = new[] { Playable() } });
        }
    }

    private static async Task StorageFailures()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store();
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

    private static async Task CancelledTransfers()
    {
        var runtime = new Runtime(); runtime.Add("a");
        await using var service = new FilmSourceService(runtime, new Store());
        await service.RefreshAsync();
        using (var cancel = new CancellationTokenSource())
        {
            runtime.AfterStageBegin = cancel.Cancel;
            await Cancelled(service.ImportLocalAsync("cancelled", FixtureScript, "cancelled", cancel.Token));
            Check(runtime.Stages.Count == 0 && runtime.Calls.All(call => call.Method != "install"),
                "a token received at cancellation is aborted before any install");
            runtime.AfterStageBegin = null;
        }
        using (var cancel = new CancellationTokenSource())
        {
            runtime.BlobReplies = true;
            runtime.AfterBlobCreated = cancel.Cancel;
            await Cancelled(service.DetectAsync(Key("a"), cancel.Token));
            Check(runtime.Blobs.Count == 0 && service.Videos.Single().Detection is null,
                "a blob received at cancellation is released without publishing the detection");
            runtime.AfterBlobCreated = null;
        }
    }

    private static async Task StartupFailure()
    {
        var runtime = new Runtime(); runtime.Add("a");
        var store = new Store();
        await using (var first = new FilmSourceService(runtime, store)) await first.RefreshAsync();
        runtime.FailNext = "start";
        await using var service = new FilmSourceService(runtime, store);
        int notifications = 0, calls = runtime.Calls.Count;
        service.Changed += (_, _) => notifications++;
        await service.InitializeAsync();
        Check(runtime.Calls.Count == calls && service.Videos.Count == 1, "cached list is available before Node");
        await Code(() => service.StartAsync(), "START_FAILED");
        Check(service.RuntimeError == "START_FAILED" && notifications >= 2 && service.Videos.Count == 1 &&
            !service.Cats.Single().CanDetect, "startup failure is observable without destroying local state");
        await service.SaveVideoOptionsAsync(Key("a"), new(false, true, false, false));
        await service.StartAsync();
        Check(service.RuntimeError is null && service.Cats.Single().CanDetect, "startup can be retried after failure");
    }

    private static async Task ExplicitRuntimeRecovery()
    {
        var session = new Runtime { StartingStatuses = 1 }; session.Add("restored");
        int factories = 0;
        await using var runtime = new AppRuntimeClient(_ => ++factories == 1
            ? Task.FromException<IRuntimeSession>(new BridgeException("INTERNAL"))
            : Task.FromResult<IRuntimeSession>(session));
        await using var service = new FilmSourceService(runtime, new Store());
        await service.InitializeAsync();
        Check(factories == 0, "local initialization never enters the runtime factory");
        runtime.Start();
        try { await service.StartAsync(); throw new InvalidOperationException("Expected factory failure"); }
        catch (BridgeException error) { Check(error.Code == "INTERNAL", "failed factory is observable"); }
        Check(service.RuntimeError is not null && runtime.CanRetry && factories == 1, "first failed epoch is retained");
        try { await service.ImportLocalAsync("uncertain", FixtureScript, "new"); throw new InvalidOperationException("Expected failed epoch"); }
        catch (BridgeException) { }
        Check(factories == 1 && session.Calls.Count == 0, "management never automatically retries the host");
        await service.RefreshAsync();
        Check(factories == 2 && !runtime.CanRetry && service.RuntimeError is null &&
            service.Cats.Single().InstallationId == "restored" && service.Cats.Single().CanDetect,
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
        internal string Name = "猫 " + id, SourceType = "local", State = "ready", UpdateState = "idle", SourceHash = "hash";
        internal string? SourceUrl, UpdateError;
        internal long Generation = 1, ImportedAt = 100;
        internal long? UpdatedAt;
        internal bool Enabled = true;
        internal List<SiteFixture> Sites = [Site("video")];
        internal object Wire() => new { installationId = id, name = Name, sourceType = SourceType, sourceUrl = SourceUrl,
            enabled = Enabled, importedAt = ImportedAt, updatedAt = UpdatedAt, updateState = UpdateState, updateError = UpdateError,
            generation = Generation, state = State, sourceHash = SourceHash, error = State == "failed" ? "SCRIPT_EXCEPTION" : null,
            sites = Sites.Select(site => site.Wire()).ToArray() };
    }
    private sealed record Upload(int Length, string Sha256, MemoryStream Bytes);

    private sealed class Runtime : IRuntimeSession
    {
        internal readonly Dictionary<string, CatFixture> Nodes = new(StringComparer.Ordinal);
        internal readonly List<RpcCall> Calls = [];
        internal readonly List<Invocation> Invocations = [];
        internal readonly Dictionary<string, Upload> Stages = [];
        internal readonly Dictionary<string, byte[]> Blobs = [];
        internal Func<Invocation, CancellationToken, Task<JsonElement>> Invoke = (call, _) => DefaultInvoke(call);
        internal string? FailNext, UploadedSource;
        internal string NextUpdateState = "updated", ImportState = "ready";
        internal string[]? NextSites;
        internal Func<Task>? BeforeInstall;
        internal Func<string, JsonElement>? Uninstall;
        internal Action? AfterStageBegin, AfterBlobCreated;
        internal int StartingStatuses;
        internal bool BlobReplies, CorruptBlobHash, BadChunkAck, Disposed;
        internal void Add(string id, params string[] keys)
        {
            Nodes[id] = new(id);
            if (keys.Length > 0) Nodes[id].Sites = keys.Select(key => Site(key)).ToList();
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(method.StartsWith("cat.script.", StringComparison.Ordinal), "Cat protocol only");
            string operation = method["cat.script.".Length..];
            var input = Element(parameters ?? new { });
            Calls.Add(new(operation, input));
            if (FailNext == operation)
            {
                FailNext = null;
                if (operation == "update")
                {
                    var failed = Nodes[input.GetProperty("installationId").GetString()!];
                    failed.UpdateState = "failed"; failed.UpdateError = "UPDATE_FAILED";
                }
                return Failure(operation == "update" ? "UPDATE_FAILED" : operation == "start" ? "START_FAILED" : "IMPORT_FAILED");
            }
            if (operation == "stage.begin")
            {
                Fields(input, "length", "sha256", "kind");
                string token = Guid.NewGuid().ToString("N");
                Stages[token] = new(input.GetProperty("length").GetInt32(), input.GetProperty("sha256").GetString()!, new());
                AfterStageBegin?.Invoke();
                return Envelope(new { token, chunkBytes = 65536 });
            }
            if (operation == "stage.chunk")
            {
                string token = input.GetProperty("token").GetString()!;
                var upload = Stages[token];
                Check(input.GetProperty("offset").GetInt32() == upload.Bytes.Length, "contiguous upload");
                byte[] bytes = Convert.FromBase64String(input.GetProperty("data").GetString()!);
                Check(bytes.Length is > 0 and <= 65536, "protocol chunk size");
                upload.Bytes.Write(bytes);
                return Envelope(new { next = upload.Bytes.Length + (BadChunkAck ? 1 : 0) });
            }
            if (operation == "stage.abort")
            {
                if (Stages.Remove(input.GetProperty("token").GetString()!, out var upload)) upload.Bytes.Dispose();
                return Envelope(new { released = true });
            }
            if (operation == "blob.read")
            {
                var bytes = Blobs[input.GetProperty("token").GetString()!];
                int offset = input.GetProperty("offset").GetInt32(), count = Math.Min(65536, bytes.Length - offset);
                return Envelope(new { data = Convert.ToBase64String(bytes, offset, count), next = offset + count });
            }
            if (operation == "blob.release")
            {
                Blobs.Remove(input.GetProperty("token").GetString()!);
                return Envelope(new { released = true });
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
                Fields(input, "installationId", "generation", "siteId", "operation",
                    input.TryGetProperty("payloadToken", out _) ? "payloadToken" : "payload");
                string json = input.TryGetProperty("payloadToken", out var upload) ? Encoding.UTF8.GetString(Take(upload.GetString()!)) :
                    input.GetProperty("payload").GetString()!;
                using var payload = JsonDocument.Parse(json);
                var call = new Invocation(id, input.GetProperty("generation").GetInt64(), input.GetProperty("siteId").GetString()!,
                    input.GetProperty("operation").GetString()!, payload.RootElement.Clone());
                Check(Nodes[id].Sites.Any(site => site.Key == call.SiteId && site.Actions.Contains(call.Operation)), "invocation must use a declared site action");
                Invocations.Add(call);
                return Reply(await Invoke(call, cancellationToken)); // May deliberately ignore cancellation to test fencing.
            }
            if (operation is "install" or "import")
            {
                if (BeforeInstall is not null) await BeforeInstall();
                if (operation == "install") UploadedSource = Encoding.UTF8.GetString(Take(input.GetProperty("token").GetString()!));
                if (!Nodes.ContainsKey(id)) Add(id);
                var imported = Nodes[id];
                imported.Name = input.GetProperty("name").GetString()!;
                imported.SourceType = operation == "install" ? "local" : "network";
                imported.SourceUrl = operation == "import" ? input.GetProperty("sourceUrl").GetString() : null;
                imported.Enabled = input.GetProperty("enabled").GetBoolean();
                imported.Generation++; imported.State = imported.Enabled ? ImportState : "disabled";
                imported.SourceHash = "imported"; imported.UpdateState = "idle"; imported.UpdateError = null;
                if (NextSites is { } sites) { imported.Sites = sites.Select(key => Site(key)).ToList(); NextSites = null; }
                return Reply(imported.Wire());
            }
            var cat = Nodes[id];
            switch (operation)
            {
                case "edit":
                    cat.Name = input.GetProperty("name").GetString()!;
                    if (input.TryGetProperty("sourceUrl", out var url)) cat.SourceUrl = url.GetString();
                    break;
                case "update":
                    if (input.TryGetProperty("token", out var upload)) UploadedSource = Encoding.UTF8.GetString(Take(upload.GetString()!));
                    cat.UpdateState = NextUpdateState; cat.UpdateError = null;
                    if (NextUpdateState == "updated") { cat.UpdatedAt = 200; cat.Generation++; cat.SourceHash = "updated"; }
                    break;
                case "restart": Fields(input, "installationId"); cat.Generation++; break;
                case "enable":
                    Fields(input, "installationId", "enabled");
                    cat.Enabled = input.GetProperty("enabled").GetBoolean();
                    cat.State = cat.Enabled ? "ready" : "disabled"; cat.Generation++;
                    break;
                case "uninstall":
                    Fields(input, "installationId");
                    if (Uninstall is not null) return Uninstall(id);
                    Nodes.Remove(id);
                    return Reply(new { removed = true });
                default: throw new InvalidOperationException("Unexpected RPC " + operation);
            }
            return Reply(cat.Wire());
        }
        private byte[] Take(string token)
        {
            var upload = Stages[token]; byte[] bytes = upload.Bytes.ToArray();
            Check(bytes.Length == upload.Length && Convert.ToHexStringLower(SHA256.HashData(bytes)) == upload.Sha256, "upload length/hash");
            Stages.Remove(token); upload.Bytes.Dispose(); return bytes;
        }
        private JsonElement Reply(object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (!BlobReplies) return Envelope(new { data = Encoding.UTF8.GetString(bytes) });
            string token = Guid.NewGuid().ToString("N");
            Blobs[token] = bytes;
            AfterBlobCreated?.Invoke();
            return Envelope(new { blob = new { token, length = bytes.Length, chunkBytes = 65536,
                sha256 = CorruptBlobHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)) } });
        }
        private static JsonElement Envelope(object fields)
        {
            var result = new Dictionary<string, object?> { ["ok"] = true, ["contract"] = "cat.script.v1" };
            foreach (var field in Element(fields).EnumerateObject()) result[field.Name] = field.Value.Clone();
            return Element(result);
        }
        private static JsonElement Failure(string code) => Element(new { ok = false, contract = "cat.script.v1", code });
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Store : IDocumentStore
    {
        internal byte[]? Bytes;
        internal int Reads, Writes;
        internal bool FailBefore, FailAfter, Disposed;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            Check(documentNamespace == FilmSourceService.DocumentNamespace, "local document namespace");
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return Task.FromResult(new DocumentSnapshot(Bytes?.ToArray(),
                Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)));
        }
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Check(documentNamespace == FilmSourceService.DocumentNamespace, "local document namespace");
            cancellationToken.ThrowIfCancellationRequested();
            if (FailBefore) { FailBefore = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
            var previous = Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes);
            if (previous != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            Bytes = data.ToArray(); Writes++;
            if (FailAfter) { FailAfter = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
            return Task.FromResult(DocumentFingerprint.FromBytes(Bytes));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
