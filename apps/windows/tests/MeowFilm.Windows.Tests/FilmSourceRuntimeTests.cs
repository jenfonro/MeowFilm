using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmSourceRuntimeTests
{
    private const string SiteKey = "fixture";
    private const string SiteApi = "/spider/film-fixture/3";

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("native-film-runtime: real Cat probe and deletion survive native replacement duplicate adds and cold restart",
            ProbeAndTombstones);
        yield return new("native-film-runtime: complete Cat source and large structured payloads round trip without losing types",
            LargeStructuredPayload);
        yield return new("native-film-runtime: cold start restores enabled cats and four options without executing disabled scripts",
            ColdStartAndOptions);
        yield return new("native-film-runtime: cancelled update observes rollback and the next probe needs no refresh",
            CancelledUpdate);
        yield return new("native-film-runtime: default RPC timeout observes rollback and the next probe needs no refresh",
            TimedOutUpdate);
        yield return new("native-film-runtime: a committed network update followed by local cache storage failure never exposes a retired generation",
            UpdateStorageFailure);
        yield return new("native-film-runtime: detail script errors retain real search fallback and durable results",
            DetailErrorFallback);
        yield return new("native-film-runtime: an in-flight Worker exit preserves detection until explicit restart without replay",
            DetectionWorkerExit);
        yield return new("native-film-runtime: failed navigation roots cannot dispatch children or publish valid detection",
            FailedNavigationRoot);
        yield return new("native-film-runtime: offline repeated saves cold load only final intent and never execute disabled scripts", OfflineColdStart);
        yield return new("native-film-runtime: native local network local switching downloads missing cache and preserves identity", TypeSwitching);
        yield return new("native-film-runtime: a late download cannot load or overwrite a newer native local intention", LateDownload);
        yield return new("native-film-runtime: edited names do not replace the live worker or discard its private state", NameOnlyEdit);
        yield return new("native-film-runtime: changed selected bytes fail their saved hash without rolling back native intent", SelectedFileChanged);
        yield return new("native-film-runtime: failed network update keeps actual source while native type intent remains saved", NetworkFailure);
        yield return new("native-film-runtime: failed saved type replacement retains native intent until another save", FailedTypeSelection);
        yield return new("native-film-runtime: native deletion releases the target worker and volume while keeping its sibling alive", DeletionCleanup);
    }

    private static async Task OfflineColdStart()
    {
        string root = NewData();
        await using var server = new ScriptServer(Script("final"));
        var offline = new OfflineRuntime();
        await using (var documents = new LocalDocumentStore(Path.Combine(root, "documents"), AppIdentity.NativeNamespaces,
            AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces))
        await using (var service = new FilmSourceService(offline, documents))
        {
            await service.ImportLocalAsync("先本地", WriteSource(Script("unused")), "enabled");
            await service.EditCatAsync("enabled", "网络一", "network", server.Url + "?old", null);
            await service.EditCatAsync("enabled", "最终网络", "network", server.Url, null);
            string disabled = """
                require('fs').writeFileSync('/disabled-ran', 'unexpected');
                throw new Error('disabled script must never execute');
                """;
            await service.ImportLocalAsync("保持停用", WriteSource(disabled), "disabled");
            await service.EnableCatAsync("disabled", false);
            Check(service.Cats.Single(cat => cat.InstallationId == "enabled").InputRevision == 3 &&
                offline.Calls == 0 && server.Requests == 0, "offline repeated native saves never contact or start a runtime");
        }
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.InitializeAsync(token);
            Check(service.Cats.Single(cat => cat.InstallationId == "enabled") is
                { Name: "最终网络", SourceType: "network", InputRevision: 3, CanDetect: false },
                "cold native projection contains final intent, not old intermediate saves");
            await service.StartAsync(token);
            var enabled = await AwaitReadyAsync(service, runtime, "enabled", 3, token);
            await WaitRuntimeAsync(runtime, "disabled", cat => cat.GetProperty("state").GetString() == "disabled", token);
            Check(enabled.Name == "最终网络" && server.Requests == 1 && service.Cats.Single(cat =>
                cat.InstallationId == "disabled") is { Enabled: false, CanDetect: false } &&
                !File.Exists(Path.Combine(root, "script-runtime", "volumes", "disabled", "disabled-ran")),
                "real cold start downloads only final URL once and never executes the disabled selected script");
            AssertSite(Video(service, new("enabled", SiteKey)), "final");
            AssertDetection(await service.DetectAsync(new("enabled", SiteKey), token));
        });
    }

    private static async Task TypeSwitching()
    {
        string root = NewData();
        await using var server = new ScriptServer(Script("same"));
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            const string id = "switching";
            var key = new FilmSourceKey(id, SiteKey);
            await service.StartAsync(token);
            var original = await ImportAndAwaitReadyAsync(service, runtime, "身份不变", Script("same"), id, token);
            AssertDetection(await service.DetectAsync(key, token));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(key, options, order: 17, cancellationToken: token);
            await service.DeleteVideoAsync(key, token);
            var networkIntent = await service.EditCatAsync(id, "网络", "network", server.Url, null, token);
            var network = await AwaitReadyAsync(service, runtime, id, networkIntent.InputRevision, token);
            Check(server.Requests == 1 && network.SourceHash == original.SourceHash &&
                network.InputRevision == 2 && network.Generation > original.Generation && service.Videos.Count == 0,
                "even matching local cached bytes cannot stand in for a new network identity: a real download occurs");
            string local = WriteSource(Script("selected"));
            var localIntent = await service.EditCatAsync(id, "再本地", "local", null, local, token);
            var restored = await AwaitReadyAsync(service, runtime, id, localIntent.InputRevision, token);
            Check(restored.InstallationId == id && restored.SourceType == "local" && restored.LocalPath == local &&
                restored.SourceUrl is null && restored.InputRevision == 3 && restored.SourceHash == Hash(local) &&
                restored.Generation > network.Generation && service.Videos.Count == 0 && server.Requests == 1,
                "network-to-local loads the selected file while preserving identity and video tombstones");
            var starts = SourceStarts(root, id);
            Check(starts.SequenceEqual(["same", "same", "selected"]), "each intended source runs once and shares the original private volume");
            await service.EnableCatAsync(id, false, token);
            await WaitRuntimeAsync(runtime, id, cat => cat.GetProperty("state").GetString() == "disabled", token);
            var disabledIntent = await service.EditCatAsync(id, "停用网络", "network", server.Url + "?disabled", null, token);
            await WaitRuntimeAsync(runtime, id, cat => cat.GetProperty("inputRevision").GetInt64() == disabledIntent.InputRevision &&
                cat.GetProperty("state").GetString() == "disabled", token);
            Check(!service.Cats.Single().Enabled && service.Videos.Count == 0 && server.Requests == 1 &&
                SourceStarts(root, id).SequenceEqual(starts), "saving a new type neither enables a disabled cat nor restores deleted videos");
        });
    }

    private static async Task LateDownload()
    {
        string root = NewData();
        await using var server = new ScriptServer(Script("late-network"));
        var entered = Signal(); var release = Signal();
        server.ExpectClientCancellation = true;
        server.BeforeReply = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            const string id = "late-source";
            await service.StartAsync(token);
            await ImportAndAwaitReadyAsync(service, runtime, "原本地", Script("original"), id, token);
            var network = await service.EditCatAsync(id, "迟到网络", "network", server.Url, null, token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                string local = WriteSource(Script("latest-local"));
                var latest = await service.EditCatAsync(id, "最新本地", "local", null, local, token);
                Check(latest.InputRevision == network.InputRevision + 1 && latest.Name == "最新本地" && !latest.CanDetect,
                    "a new native commit is available while the previous download is genuinely still pending");
                release.TrySetResult();
                var ready = await AwaitReadyAsync(service, runtime, id, latest.InputRevision, token);
                Check(ready.Name == "最新本地" && ready.SourceType == "local" && ready.SourceHash == Hash(local) &&
                    SourceStarts(root, id).SequenceEqual(["original", "latest-local"]),
                    "the obsolete network response cannot execute, replace the newer local input, or alter its stored identity");
                AssertSite(Video(service, new(id, SiteKey)), "latest-local");
                AssertDetection(await service.DetectAsync(new(id, SiteKey), token));
            }
            finally { release.TrySetResult(); }
        });
    }

    private static async Task NameOnlyEdit()
    {
        string root = NewData();
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            const string id = "name-only";
            await service.StartAsync(token);
            var original = await ImportAndAwaitReadyAsync(service, runtime, "原名称", Script("name"), id, token);
            var detection = await service.DetectAsync(new(id, SiteKey), token);
            var edited = await service.EditCatAsync(id, "新名称", cancellationToken: token);
            await WaitRuntimeAsync(runtime, id, cat => cat.GetProperty("name").GetString() == "新名称" &&
                cat.GetProperty("state").GetString() == "ready", token);
            await service.RefreshAsync(token);
            Check(edited.InputRevision == original.InputRevision && service.Cats.Single().Generation == original.Generation &&
                service.Cats.Single().SourceHash == original.SourceHash && SourceStarts(root, id).SequenceEqual(["name"]) &&
                SameDetection(Video(service, new(id, SiteKey)).Detection, detection),
                "name-only edits update metadata without replacing the Worker, its generation, source, private state or detection");
            AssertDetection(await service.DetectAsync(new(id, SiteKey), token));
        });
    }

    private static async Task SelectedFileChanged()
    {
        string root = NewData();
        var offline = new OfflineRuntime();
        string path = WriteSource(Script("selected-before"));
        string selectedHash = Hash(path);
        await using (var store = new LocalDocumentStore(Path.Combine(root, "documents"), AppIdentity.NativeNamespaces,
            AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces))
        await using (var service = new FilmSourceService(offline, store))
            await service.ImportLocalAsync("选中文件", path, "changed");
        File.WriteAllText(path, Script("unselected-after"), new UTF8Encoding(false));
        IDocumentStore? documents = null;
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.StartAsync(token);
            await WaitRuntimeAsync(runtime, "changed", cat => cat.GetProperty("state").GetString() == "failed", token);
            await service.RefreshAsync(token);
            Check(service.Cats.Single() is { InputRevision: 1, LocalPath: not null, CanDetect: false, UpdateError: "STORAGE_CONFLICT" } &&
                SourceStarts(root, "changed").Length == 0, "changed file bytes are never executed under a stale selected hash");
            var stored = await documents!.ReadAsync("film.cat-sources.v1", token);
            using var parsed = JsonDocument.Parse(stored.Data!);
            Check(parsed.RootElement.GetProperty("scripts")[0].GetProperty("localHash").GetString() == selectedHash &&
                parsed.RootElement.GetProperty("scripts")[0].GetProperty("localPath").GetString() == path,
                "failed loading does not rewrite the committed selection or pretend unselected bytes were accepted");
        }, store => { documents = store; return store; });
    }

    private static async Task NetworkFailure()
    {
        await using var server = new ScriptServer(Script("accepted"));
        await WithServiceAsync(NewData(), async (service, runtime, token) =>
        {
            const string id = "network-failure";
            await service.StartAsync(token);
            var original = await ImportNetworkAndAwaitReadyAsync(service, runtime, "网络", server.Url, id, token);
            server.Source = "module.exports = { start( };";
            await ExpectCode("SCRIPT_EXCEPTION", () => service.UpdateCatAsync(id, cancellationToken: token));
            var recovered = service.Cats.Single();
            Check(recovered.CanDetect && recovered.SourceHash == original.SourceHash && recovered.SourceUrl == server.Url &&
                recovered.InputRevision == original.InputRevision && recovered.UpdateState == "failed",
                "an actual network update failure keeps the accepted file and its native URL without undoing intent");
            AssertDetection(await service.DetectAsync(new(id, SiteKey), token));
        });
    }

    private static async Task FailedTypeSelection()
    {
        string root = NewData();
        IDocumentStore? documents = null;
        await using var server = new ScriptServer("module.exports = { start( };");
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            const string id = "failed-type";
            await service.StartAsync(token);
            string selected = WriteSource(Script("working"));
            var initial = await service.ImportLocalAsync("本地", selected, id, token);
            var original = await AwaitReadyAsync(service, runtime, id, initial.InputRevision, token);
            var intent = await service.EditCatAsync(id, "已保存网络", "network", server.Url, null, token);
            await WaitRuntimeAsync(runtime, id, cat => cat.GetProperty("inputRevision").GetInt64() == intent.InputRevision &&
                cat.GetProperty("state").GetString() == "failed", token);
            await service.RefreshAsync(token);
            var failed = service.Cats.Single();
            var saved = await documents!.ReadAsync("film.cat-sources.v1", token);
            using var parsed = JsonDocument.Parse(saved.Data!);
            var input = parsed.RootElement.GetProperty("scripts")[0];
            Check(failed is { SourceType: "network", LocalPath: null, CanDetect: false } &&
                failed.SourceUrl == server.Url && failed.InputRevision == 2 &&
                input.GetProperty("sourceType").GetString() == "network" && input.GetProperty("sourceUrl").GetString() == server.Url &&
                input.GetProperty("localHash").ValueKind == JsonValueKind.Null && failed.SourceHash == original.SourceHash,
                "loading failure never rolls back native type/URL or marks the old local installation ready under the new intent");
            var restoredIntent = await service.EditCatAsync(id, "重新保存本地", "local", null, selected, token);
            var restored = await AwaitReadyAsync(service, runtime, id, restoredIntent.InputRevision, token);
            Check(restored.InputRevision == 3 && restored.SourceHash == original.SourceHash &&
                SourceStarts(root, id).SequenceEqual(["working", "working"]),
                "a later native save, not a shadow configuration rollback, restores the explicitly selected local input");
            AssertDetection(await service.DetectAsync(new(id, SiteKey), token));
        }, store => { documents = store; return store; });
    }

    private static async Task DeletionCleanup()
    {
        string root = NewData();
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.StartAsync(token);
            await ImportAndAwaitReadyAsync(service, runtime, "待删", Script("deleted"), "deleted", token);
            var sibling = await ImportAndAwaitReadyAsync(service, runtime, "保留", Script("sibling"), "sibling", token);
            string volume = Path.Combine(root, "script-runtime", "volumes", "deleted");
            Check(Directory.Exists(volume), "the target owns an actual running Worker volume before deletion");
            await service.DeleteCatAsync("deleted", token);
            Check(service.Cats.Single().InstallationId == "sibling", "native deletion returns with only the target removed");
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
            observation.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var reply = await runtime.CallAsync("cat.script.status", new { }, observation.Token);
                Check(reply.GetProperty("ok").GetBoolean(), "read-only deletion observation succeeds");
                if (!reply.GetProperty("value").GetProperty("instances").EnumerateArray()
                    .Any(cat => cat.GetProperty("installationId").GetString() == "deleted") && !Directory.Exists(volume)) break;
                await Task.Delay(20, observation.Token);
            }
            var actualSibling = await WaitRuntimeAsync(runtime, "sibling", cat =>
                cat.GetProperty("state").GetString() == "ready", token);
            Check(actualSibling.GetProperty("generation").GetInt64() == sibling.Generation &&
                service.Videos.Single().Key == new FilmSourceKey("sibling", SiteKey),
                "real target cleanup does not stop, replace or delete its sibling");
            AssertDetection(await service.DetectAsync(new("sibling", SiteKey), token));
        });
    }

    private static string WriteSource(string source)
    {
        string path = TestWorkspace.DataPath("film-runtime-selected", Guid.NewGuid().ToString("N") + ".js");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source, new UTF8Encoding(false));
        return path;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string[] SourceStarts(string root, string id)
    {
        string path = Path.Combine(root, "script-runtime", "volumes", id, "source-starts.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<string>(line)!).ToArray() : [];
    }

    private static async Task<FilmCatSource> ImportAndAwaitReadyAsync(FilmSourceService service, NodeRuntime runtime,
        string name, string source, string id, CancellationToken token)
    {
        string path = WriteSource(source);
        var saved = await service.ImportLocalAsync(name, path, id, token);
        Check(saved.LocalPath == path && saved.InputRevision == 1, "native local import commits the actual selected file reference");
        return await AwaitReadyAsync(service, runtime, id, saved.InputRevision, token);
    }

    private static async Task<FilmCatSource> ImportNetworkAndAwaitReadyAsync(FilmSourceService service, NodeRuntime runtime,
        string name, string url, string id, CancellationToken token)
    {
        var saved = await service.ImportNetworkAsync(name, url, id, token);
        return await AwaitReadyAsync(service, runtime, id, saved.InputRevision, token);
    }

    private static async Task<FilmCatSource> UpdateLocalAndAwaitReadyAsync(FilmSourceService service, NodeRuntime runtime,
        string id, string source, CancellationToken token)
    {
        var saved = await service.UpdateCatAsync(id, WriteSource(source), token);
        return await AwaitReadyAsync(service, runtime, id, saved.InputRevision, token);
    }

    private static async Task<FilmCatSource> AwaitReadyAsync(FilmSourceService service, NodeRuntime runtime,
        string id, long revision, CancellationToken token)
    {
        var actual = await WaitRuntimeAsync(runtime, id, cat =>
            cat.GetProperty("inputRevision").GetInt64() == revision && cat.GetProperty("state").GetString() == "ready", token);
        await service.RefreshAsync(token);
        var observed = service.Cats.Single(cat => cat.InstallationId == id);
        Check(observed.CanDetect && observed.InputRevision == revision &&
            observed.Generation == actual.GetProperty("generation").GetInt64() &&
            observed.SourceHash == actual.GetProperty("sourceHash").GetString(),
            "public readiness agrees with a real matching runtime revision, generation and installed source hash");
        return observed;
    }

    private static async Task<JsonElement> WaitRuntimeAsync(NodeRuntime runtime, string id,
        Func<JsonElement, bool> accepts, CancellationToken token)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
        observation.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var reply = await runtime.CallAsync("cat.script.status", new { }, observation.Token);
            Check(reply.GetProperty("ok").GetBoolean(), "real runtime status returns its Cat envelope");
            var actual = reply.GetProperty("value").GetProperty("instances").EnumerateArray()
                .Where(cat => cat.GetProperty("installationId").GetString() == id).Select(cat => cat.Clone()).ToArray();
            if (actual.Length == 1 && accepts(actual[0])) return actual[0];
            // Only pace observation; elapsed time never stands in for readiness.
            await Task.Delay(20, observation.Token);
        }
    }

    private static async Task LargeStructuredPayload()
    {
        await WithServiceAsync(NewData(), async (service, runtime, token) =>
        {
            const string id = "structured-cat";
            string name = "完整猫🎬" + new string('文', 70000);
            string source = """
                const http = require('http');
                let server;
                module.exports.start = () => new Promise(resolve => {
                  server = http.createServer((request, response) => {
                    response.setHeader('content-type', 'application/json; charset=utf-8');
                    const path = request.url.split('?')[0];
                    if (path === '/config' || path === '/full-config') {
                      response.end(JSON.stringify({ video: { sites: [{
                        key: 'echo', name: 'Echo', api: '/api/echo', actions: ['echo'],
                        methods: { echo: 'POST' }
                      }] } }));
                      return;
                    }
                    if (request.method !== 'POST' || path !== '/api/echo/echo') {
                      response.statusCode = 404;
                      response.end(JSON.stringify({ error: 'unexpected route' }));
                      return;
                    }
                    const chunks = [];
                    request.on('data', chunk => chunks.push(Buffer.from(chunk)));
                    request.once('end', () => {
                      const input = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                      response.end(JSON.stringify(input));
                    });
                  });
                  server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
                });
                module.exports.stop = () => { server.closeAllConnections(); server.close(); };
                """ + "\n//" + new string('中', 70000);
            await service.StartAsync(token);
            var installed = await ImportAndAwaitReadyAsync(service, runtime, name, source, id, token);
            Check(installed.CanDetect && installed.Name == name &&
                installed.Sites.Single().Actions.SequenceEqual(["echo"]),
                "complete selected Unicode file loads and returns a large structured management value");
            var payload = JsonSerializer.SerializeToElement(new
            {
                id = "片🎬" + new string('界', 70000),
                page = 2,
                filter = true,
                nested = new { values = new object?[] { null, false, 3, "中文" } }
            });
            var reply = await runtime.CallAsync("cat.script.invoke", new
            {
                installationId = id, generation = installed.Generation,
                siteId = "echo", operation = "echo", payload
            }, token);
            Check(reply.GetProperty("ok").GetBoolean() && reply.GetProperty("contract").GetString() == "cat.script.v2" &&
                JsonElement.DeepEquals(reply.GetProperty("value"), payload),
                "real Cat HTTP invocation returns the complete envelope and preserves a large structured payload and its types");
        });
    }

    private static async Task ProbeAndTombstones()
    {
        string root = NewData();
        var key = new FilmSourceKey("probe-cat", SiteKey);
        string original = Script("original"), updated = Script("updated");
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.StartAsync(token);
            Check(service.Cats.Count == 0 && service.Videos.Count == 0, "dedicated data root starts empty");
            var imported = await ImportAndAwaitReadyAsync(service, runtime, "检测猫", original, key.InstallationId, token);
            Check(imported.InstallationId == key.InstallationId && imported.Enabled && imported.CanDetect &&
                imported.SourceType == "local" && imported.SourceUrl is null && imported.ImportedAt is > 0,
                "local import enables the real installation and discovers its sites");
            Check(service.Cats.Count == 1 && service.Videos.Count == 1, "one imported cat exposes one video source");
            AssertSite(Video(service, key), "original");
            AssertDetection(await service.DetectAsync(key, token));
            AssertDetection(Video(service, key).Detection);

            await service.DeleteVideoAsync(key, token);
            Check(service.Videos.Count == 0, "explicit video deletion removes the local row");
            var restarted = await service.RestartCatAsync(key.InstallationId, token);
            Check(restarted.CanDetect && restarted.Sites.Count == 1 && restarted.ModifiedAt == imported.ModifiedAt,
                "cat restart succeeds without changing its modification time");
            Check(service.Videos.Count == 0, "cat restart does not restore the deleted video");
            var replaced = await UpdateLocalAndAwaitReadyAsync(service, runtime, key.InstallationId, updated, token);
            Check(replaced.CanDetect && replaced.UpdateState == "updated" &&
                replaced.Sites.Single().Name == "Fixture updated",
                "ordinary update really installs and discovers the replacement script");
            Check(service.Videos.Count == 0, "successful ordinary update preserves the deletion");
            await service.RefreshAsync(token);
            Check(service.Videos.Count == 0, "status synchronization preserves the deletion");
        });

        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.InitializeAsync(token);
            Check(service.Cats.Single().InstallationId == key.InstallationId && service.Videos.Count == 0,
                "reopening the persistent service retains the deletion");
            await service.StartAsync(token);
            Check(service.Cats.Single().CanDetect && service.Cats.Single().Sites.Single().Name == "Fixture updated" &&
                service.Videos.Count == 0, "cold Node startup restores the cat without restoring its deleted video");
            await service.RefreshAsync(token);
            Check(service.Videos.Count == 0, "cold-start synchronization keeps the tombstone");

            string duplicate = WriteSource("module.exports = { start( };");
            await ExpectCode("STORAGE_CONFLICT", () => service.ImportLocalAsync("重复本地", duplicate, key.InstallationId, token));
            await ExpectCode("STORAGE_CONFLICT", () => service.ImportNetworkAsync("重复网络",
                "https://fixture.invalid/duplicate.js", key.InstallationId, token));
            Check(service.Videos.Count == 0 && service.Cats.Single().Name == "检测猫" && service.Cats.Single().CanDetect,
                "duplicate adds never overwrite the configured identity or restore its deleted video");
        });
    }

    private static async Task ColdStartAndOptions()
    {
        string root = NewData();
        var enabled = new FilmSourceKey("enabled-cat", SiteKey);
        var disabled = new FilmSourceKey("disabled-cat", SiteKey);
        var enabledOptions = new FilmVideoOptions(false, false, false, false);
        var disabledOptions = new FilmVideoOptions(true, false, true, true);
        string source = Script("lifecycle", auditStarts: true);
        long disabledCheckedAt = 0;
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.StartAsync(token);
            await ImportAndAwaitReadyAsync(service, runtime, "启用猫", source, enabled.InstallationId, token);
            await ImportAndAwaitReadyAsync(service, runtime, "待停用猫", source, disabled.InstallationId, token);
            Check(service.Cats.Count == 2 && service.Cats.All(cat => cat.Enabled && cat.CanDetect) &&
                service.Videos.Count == 2, "same site key in separate installations produces two independent rows");
            await service.SaveVideoOptionsAsync(enabled, enabledOptions with { AggregateImages = true },
                cancellationToken: token);
            Check(Video(service, enabled).Options == (enabledOptions with { AggregateImages = true }),
                "all four video options are saved");
            await service.SaveVideoOptionsAsync(disabled, disabledOptions, cancellationToken: token);
            AssertOptions(service, enabled, enabledOptions, disabled, disabledOptions);

            // One successful probe per intentional activation. The guest counts both in its private volume.
            AssertDetection(await service.DetectAsync(enabled, token));
            var detection = await service.DetectAsync(disabled, token);
            AssertDetection(detection);
            disabledCheckedAt = detection.CheckedAt;
            AssertOptions(service, enabled, enabledOptions, disabled, disabledOptions);

            var stopped = await service.EnableCatAsync(disabled.InstallationId, false, token);
            await WaitRuntimeAsync(runtime, disabled.InstallationId, cat => cat.GetProperty("state").GetString() == "disabled", token);
            Check(!stopped.Enabled && !stopped.CanDetect && !stopped.CanRestart && stopped.State == "disabled",
                "disabling a cat stops its execution and removes detection/restart availability");
            Check(service.Videos.Count == 2 && Video(service, disabled).Detection?.CheckedAt == disabledCheckedAt,
                "disabling keeps the video list and previous detection");
            await ExpectCode("DISABLED", () => service.DetectAsync(disabled, token));
            await ExpectCode("DISABLED", () => service.RestartCatAsync(disabled.InstallationId, token));
        });

        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            await service.InitializeAsync(token);
            AssertOptions(service, enabled, enabledOptions, disabled, disabledOptions);
            AssertDetection(Video(service, enabled).Detection);
            AssertDetection(Video(service, disabled).Detection);
            Check(Video(service, disabled).Detection?.CheckedAt == disabledCheckedAt,
                "a new persistent service restores the saved detection without probing");

            await service.StartAsync(token);
            var activeCat = service.Cats.Single(cat => cat.InstallationId == enabled.InstallationId);
            var inactiveCat = service.Cats.Single(cat => cat.InstallationId == disabled.InstallationId);
            Check(service.Cats.Count == 2 && activeCat.Enabled && activeCat.CanDetect &&
                !inactiveCat.Enabled && !inactiveCat.CanDetect && !inactiveCat.CanRestart && inactiveCat.State == "disabled",
                "cold startup restores the enabled installation and keeps the disabled one inactive");
            AssertOptions(service, enabled, enabledOptions, disabled, disabledOptions);
            await ExpectCode("DISABLED", () => service.DetectAsync(disabled, token));
            await ExpectCode("DISABLED", () => service.RestartCatAsync(disabled.InstallationId, token));
            Check(Video(service, disabled).Detection?.CheckedAt == disabledCheckedAt,
                "rejected detection does not replace the saved result");

            AssertDetection(await service.DetectAsync(enabled, token));
            var enabledIntent = await service.EnableCatAsync(disabled.InstallationId, true, token);
            var resumed = await AwaitReadyAsync(service, runtime, disabled.InstallationId, enabledIntent.InputRevision, token);
            Check(resumed.Enabled && resumed.CanDetect, "explicit enable resumes the saved installation");
            // This probe throws inside the guest if the disabled script ran during cold startup.
            AssertDetection(await service.DetectAsync(disabled, token));
            AssertOptions(service, enabled, enabledOptions, disabled, disabledOptions);
        });
    }

    private static async Task CancelledUpdate()
    {
        await using var server = new ScriptServer(Script("accepted"));
        await WithServiceAsync(NewData(), async (service, runtime, token) =>
        {
            const string id = "cancelled-update";
            var key = new FilmSourceKey(id, SiteKey);
            await service.StartAsync(token);
            var original = await ImportNetworkAndAwaitReadyAsync(service, runtime, "可恢复猫源", server.Url, id, token);
            AssertDetection(await service.DetectAsync(key, token));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(key, options, cancellationToken: token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            server.Source = "module.exports.start = () => new Promise(() => {});";
            var update = service.UpdateCatAsync(id, null, cancel.Token);
            try
            {
                // Observe the actual candidate generation, not a guessed delay.
                while (true)
                {
                    var status = await runtime.CallAsync("cat.script.status", new { }, token);
                    var cat = status.GetProperty("value").GetProperty("instances")[0];
                    if (cat.GetProperty("state").GetString() == "starting" &&
                        cat.GetProperty("generation").GetInt64() > original.Generation) break;
                    Check(!update.IsCompleted, "candidate must still be pending before cancellation");
                    await Task.Delay(20, token);
                }
                cancel.Cancel();
                try { await update; throw new InvalidOperationException("Expected cancelled update"); }
                catch (OperationCanceledException) { }
                var recovered = service.Cats.Single();
                Check(recovered.CanDetect && recovered.Generation > original.Generation &&
                    recovered.SourceHash == original.SourceHash && recovered.ModifiedAt == original.ModifiedAt &&
                    recovered.UpdateState == "failed" && recovered.UpdateError == "CANCELLED",
                    "service observes actual rollback before returning cancellation");
                Check(Video(service, key).Options == options, "cancellation preserves all local switches");
                AssertDetection(await service.DetectAsync(key, token));
            }
            finally { cancel.Cancel(); }
        });
    }

    private static async Task TimedOutUpdate()
    {
        await using var server = new ScriptServer(Script("accepted"));
        await WithServiceAsync(NewData(), async (service, runtime, token) =>
        {
            const string id = "timed-out-update";
            var key = new FilmSourceKey(id, SiteKey);
            await service.StartAsync(token);
            var original = await ImportNetworkAndAwaitReadyAsync(service, runtime, "超时恢复猫源", server.Url, id, token);
            AssertDetection(await service.DetectAsync(key, token));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(key, options, cancellationToken: token);
            var elapsed = Stopwatch.StartNew();
            // Use NodeRuntime's real default RPC deadline; no synthetic timeout or manual cancellation.
            server.Source = "module.exports.start = () => new Promise(() => {});";
            var update = service.UpdateCatAsync(id, null, token);
            while (true)
            {
                var status = await runtime.CallAsync("cat.script.status", new { }, token);
                var cat = status.GetProperty("value").GetProperty("instances")[0];
                if (cat.GetProperty("state").GetString() == "starting" &&
                    cat.GetProperty("generation").GetInt64() > original.Generation) break;
                Check(!update.IsCompleted, "candidate must be pending before its default RPC timeout");
                await Task.Delay(20, token);
            }
            try { await update; throw new InvalidOperationException("Expected RPC timeout"); }
            catch (BridgeException error) { Check(error.Code == "TIMEOUT", "the caller retains BridgeException(TIMEOUT)"); }
            Check(!token.IsCancellationRequested, "the test deadline did not cause cancellation");
            var recovered = service.Cats.Single();
            Check(recovered.CanDetect && recovered.Generation > original.Generation &&
                recovered.SourceHash == original.SourceHash && recovered.ModifiedAt == original.ModifiedAt &&
                recovered.UpdateState == "failed" && recovered.UpdateError == "CANCELLED" &&
                service.RuntimeError is null, "owner observes Node rollback before returning the original timeout");
            Check(Video(service, key).Options == options, "RPC timeout preserves all local switches");
            AssertSite(Video(service, key), "accepted");
            AssertDetection(await service.DetectAsync(key, token));
            Console.WriteLine($"RPC_TIMEOUT_ROLLBACK elapsedMs={elapsed.ElapsedMilliseconds}; " +
                $"generation={original.Generation}->{recovered.Generation}; stages=home,category,detail,play,search; refresh=false");
        });
    }

    private static async Task UpdateStorageFailure()
    {
        FailNextWriteStore? faults = null;
        await using var server = new ScriptServer(Script("accepted"));
        await WithServiceAsync(NewData(), async (service, runtime, token) =>
        {
            const string id = "storage-update";
            var key = new FilmSourceKey(id, SiteKey);
            await service.StartAsync(token);
            var original = await ImportNetworkAndAwaitReadyAsync(service, runtime, "存储恢复猫源", server.Url, id, token);
            AssertDetection(await service.DetectAsync(key, token));
            var options = new FilmVideoOptions(false, false, false, true);
            await service.SaveVideoOptionsAsync(key, options, order: 500, cancellationToken: token);
            faults!.FailNext = true;
            try
            {
                server.Source = Script("updated");
                await service.UpdateCatAsync(id, cancellationToken: token);
                throw new InvalidOperationException("Expected storage failure");
            }
            catch (DocumentStoreException error)
            {
                Check(ReferenceEquals(error, faults.Failure), "the original local storage error survives status coordination");
            }
            var current = service.Cats.Single();
            var reply = await runtime.CallAsync("cat.script.status", new { }, token);
            Check(reply.GetProperty("ok").GetBoolean(), "real Node status succeeds after the committed update");
            var actual = reply.GetProperty("value").GetProperty("instances").EnumerateArray()
                .Single(cat => cat.GetProperty("installationId").GetString() == id);
            Check(actual.GetProperty("state").GetString() == "ready" &&
                actual.GetProperty("generation").GetInt64() > original.Generation &&
                actual.GetProperty("sourceHash").GetString() != original.SourceHash,
                "the write fault happened after Node committed a working new version");
            Check(current.CanDetect && current.Generation == actual.GetProperty("generation").GetInt64() &&
                current.SourceHash == actual.GetProperty("sourceHash").GetString() && service.RuntimeError is null,
                "the public snapshot never advertises a retired generation after the storage error");
            Check(Video(service, key).Options == options && Video(service, key).Order == 500,
                "status coordination preserves the four committed switches and ordering");
            AssertSite(Video(service, key), "updated");
            AssertDetection(await service.DetectAsync(key, token));
            await service.RefreshAsync(token);
            AssertDetection(await service.DetectAsync(key, token));
            Check(faults.Failures == 1, "exactly one write-before-forward failure was injected");
            Console.WriteLine($"LOCAL_STORAGE_UPDATE generation={original.Generation}->{current.Generation}; " +
                "injected=1; error=Storage; staleReady=false; stages=home,category,detail,play,search; nextProbeRefresh=false");
        }, inner => faults = new FailNextWriteStore(inner));
    }

    private static async Task DetailErrorFallback()
    {
        foreach (bool withError in new[] { true, false })
        {
            string root = NewData();
            IDocumentStore? documents = null;
            await WithServiceAsync(root, async (service, runtime, token) =>
            {
                const string id = "detail-fallback";
                var key = new FilmSourceKey(id, "fallback");
                await service.StartAsync(token);
                await ImportAndAwaitReadyAsync(service, runtime, "详情回退猫源", DetailFallbackScript(withError), id, token);
                Check(ProbeEvents(root, id).Length == 0, "discovery does not probe business actions");
                var options = new FilmVideoOptions(false, false, false, true);
                await service.SaveVideoOptionsAsync(key, options, order: 500, cancellationToken: token);
                var result = await service.DetectAsync(key, token);
                var events = ProbeEvents(root, id);
                Check(ProbeOperations(events).SequenceEqual(["home", "category", "detail", "search", "detail", "play"]),
                    "HTTP200 empty detail with or without a script error follows real search/detail/play");
                var firstDetail = events.First(value => value.GetProperty("kind").GetString() == "response" &&
                    value.GetProperty("operation").GetString() == "detail");
                Check(firstDetail.GetProperty("statusCode").GetInt32() == 200 &&
                    firstDetail.GetProperty("value").GetProperty("list").GetArrayLength() == 0 &&
                    firstDetail.GetProperty("value").TryGetProperty("error", out var message) == withError &&
                    (!withError || message.GetString() == "分类详情原始错误"),
                    "the guest returns the fixed empty-detail response and the control only removes error");
                Check(result is { Result: "valid", PlaybackResolved: true, MediaPlaybackVerified: false } &&
                    result.Steps.Any(step => step.Operation == "detail" && step.State == "failed" &&
                        step.Error == "分类详情原始错误") == withError,
                    "fallback is valid while retaining the original failed detail stage");
                var stored = await documents!.ReadAsync(FilmSourceService.DocumentNamespace, token);
                using var parsed = JsonDocument.Parse(stored.Data!);
                var durable = parsed.RootElement.GetProperty("videos").EnumerateArray().Single()
                    .GetProperty("detection").Deserialize<FilmSourceDetection>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Check(SameDetection(result, durable) && SameDetection(result, Video(service, key).Detection) &&
                    Video(service, key).Options == options && Video(service, key).Order == 500,
                    "the real local document stores the same detection without changing saved user options");
                Console.WriteLine($"DETAIL_ERROR_FALLBACK error={withError}; result={result.Result}; " +
                    "stages=home,category,detail,search,detail,play; mediaPlaybackVerified=false; durable=true");
            }, store => { documents = store; return store; });
        }
    }

    private static async Task FailedNavigationRoot()
    {
        foreach (bool failed in new[] { true, false })
        {
            string root = NewData();
            IDocumentStore? documents = null;
            await WithServiceAsync(root, async (service, runtime, token) =>
            {
                const string id = "navigation-root";
                var key = new FilmSourceKey(id, "navigation");
                await service.StartAsync(token);
                await ImportAndAwaitReadyAsync(service, runtime, "导航根错误猫源", NavigationRootScript(failed), id, token);
                Check(ProbeEvents(root, id).Length == 0, "discovery does not dispatch navigation or play");
                var options = new FilmVideoOptions(false, false, false, true);
                await service.SaveVideoOptionsAsync(key, options, order: 500, cancellationToken: token);
                var result = await service.DetectAsync(key, token);
                string[] expected = failed ? ["home", "category", "detail", "search"]
                    : ["home", "category", "detail", "detail", "play", "search"];
                var events = ProbeEvents(root, id);
                Check(ProbeOperations(events).SequenceEqual(expected),
                    "the failed navigation root skips child detail and play but still reaches search");
                var rootResponse = events.First(value => value.GetProperty("kind").GetString() == "response" &&
                    value.GetProperty("operation").GetString() == "detail");
                Check(rootResponse.GetProperty("statusCode").GetInt32() == 200 &&
                    rootResponse.GetProperty("value").TryGetProperty("ok", out var ok) == failed &&
                    (!failed || ok.ValueKind == JsonValueKind.False),
                    "the control removes only the root ok:false from a successful HTTP response");
                Check(result.Result == (failed ? "invalid" : "valid") && result.PlaybackResolved == !failed &&
                    !result.MediaPlaybackVerified && result.Steps.Any(step => step.Operation == "detail" &&
                        step.State == "failed" && step.Error == "根导航失败") == failed,
                    "failed root navigation cannot be made valid by a reachable child");
                var stored = await documents!.ReadAsync(FilmSourceService.DocumentNamespace, token);
                using var parsed = JsonDocument.Parse(stored.Data!);
                var durable = parsed.RootElement.GetProperty("videos").EnumerateArray().Single()
                    .GetProperty("detection").Deserialize<FilmSourceDetection>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Check(SameDetection(result, durable) && SameDetection(result, Video(service, key).Detection) &&
                    Video(service, key).Options == options && Video(service, key).Order == 500,
                    "root classification is durable and leaves all user settings unchanged");
                Console.WriteLine($"NAVIGATION_ROOT_FAILURE failed={failed}; result={result.Result}; " +
                    $"stages={string.Join(',', expected)}; durable=true; optionsUnchanged=true");
            }, store => { documents = store; return store; });
        }
    }

    private static async Task DetectionWorkerExit()
    {
        string root = NewData();
        IDocumentStore? documents = null;
        await WithServiceAsync(root, async (service, runtime, token) =>
        {
            const string id = "detection-worker-exit";
            var key = new FilmSourceKey(id, "worker-exit");
            string marker = Path.Combine(root, "script-runtime", "volumes", id, "search-count");
            await service.StartAsync(token);
            var installed = await ImportAndAwaitReadyAsync(service, runtime, "Worker 退出猫源", WorkerExitScript, id, token);
            var baseline = await service.DetectAsync(key, token);
            var saved = await documents!.ReadAsync(FilmSourceService.DocumentNamespace, token);
            var initialEvents = ProbeEvents(root, id);
            var initialWorker = initialEvents.Single(value => value.GetProperty("kind").GetString() == "worker.start");
            int initialThreadId = initialWorker.GetProperty("threadId").GetInt32();
            Check(baseline is { Result: "valid", PlaybackResolved: true, MediaPlaybackVerified: false } &&
                !initialWorker.GetProperty("isMainThread").GetBoolean() && initialThreadId > 0 &&
                ProbeOperations(initialEvents).SequenceEqual(["home", "category", "detail", "play", "search"]) &&
                File.ReadAllText(marker) == "1", "one real Worker produces the complete valid baseline on the original generation");

            FilmSourceDetection? returned = null;
            OperationCanceledException? failure = null;
            try { returned = await service.DetectAsync(key, token); }
            catch (OperationCanceledException error) { failure = error; }
            // Do not refresh or issue management while observing the failed call.
            var immediate = Video(service, key).Detection;
            var after = await documents.ReadAsync(FilmSourceService.DocumentNamespace, token);
            var failedEvents = ProbeEvents(root, id).Skip(initialEvents.Length).ToArray();
            var exitAttempt = failedEvents.Single(value => value.GetProperty("kind").GetString() == "worker.exit-attempt");
            Check(!token.IsCancellationRequested &&
                exitAttempt.GetProperty("count").GetInt32() == 2 &&
                exitAttempt.GetProperty("threadId").GetInt32() == initialThreadId &&
                !failedEvents.Any(value => value.GetProperty("kind").GetString() == "response" &&
                    value.GetProperty("operation").GetString() == "search") &&
                File.ReadAllText(marker) == "2" &&
                ProbeOperations(failedEvents).SequenceEqual(["home", "category", "detail", "play", "search"]),
                "the uncancelled second probe reaches one detached rejection after play and receives no search response");

            // Observe terminal failure with a deadline; the delay only paces status polling.
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
            observation.CancelAfter(TimeSpan.FromSeconds(30));
            JsonElement actual;
            do
            {
                var reply = await runtime.CallAsync("cat.script.status", new { }, observation.Token);
                Check(reply.GetProperty("ok").GetBoolean(), "direct read-only Cat status succeeds");
                actual = reply.GetProperty("value").GetProperty("instances").EnumerateArray()
                    .Single(cat => cat.GetProperty("installationId").GetString() == id).Clone();
                Check(actual.GetProperty("generation").GetInt64() == installed.Generation,
                    "read-only failure observation must never see an automatically restarted generation");
                if (actual.GetProperty("state").GetString() == "failed") break;
                await Task.Delay(20, observation.Token);
            } while (true);
            var observedEvents = ProbeEvents(root, id);
            Check(runtime.IsConnected && actual.GetProperty("sourceHash").GetString() == installed.SourceHash &&
                service.Cats.Single().Generation == installed.Generation &&
                observedEvents.Length == initialEvents.Length + failedEvents.Length &&
                observedEvents.Count(value => value.GetProperty("kind").GetString() == "worker.start") == 1 &&
                File.ReadAllText(marker) == "2" &&
                SameDetection(immediate, Video(service, key).Detection),
                "the failed Worker neither restarts nor replays while the same Node owner and client snapshot remain intact");
            Check(returned is null && failure?.InnerException is FilmSourceException { Code: "STALE_SESSION" } &&
                !token.IsCancellationRequested, "retired Worker invocation fails as lifecycle cancellation, not a new search_error");
            Check(SameDetection(baseline, immediate) && after.Fingerprint == saved.Fingerprint &&
                after.Data!.SequenceEqual(saved.Data!), "the retired generation cannot replace the in-memory or durable baseline");

            int recoveryStart = observedEvents.Length;
            var restarted = await service.RestartCatAsync(id, token);
            Check(restarted.CanDetect && restarted.Generation > installed.Generation &&
                restarted.SourceHash == installed.SourceHash && service.Cats.Single().Generation == restarted.Generation &&
                SameDetection(baseline, Video(service, key).Detection) && File.ReadAllText(marker) == "2" &&
                !ProbeOperations(ProbeEvents(root, id).Skip(recoveryStart)).Any(),
                "explicit restart alone restores a new generation without replaying detection or replacing its baseline");
            var recovered = await service.DetectAsync(key, token);
            var recoveryDocument = await documents.ReadAsync(FilmSourceService.DocumentNamespace, token);
            using var recoveryJson = JsonDocument.Parse(recoveryDocument.Data!);
            var recoveryDetection = recoveryJson.RootElement.GetProperty("videos").EnumerateArray().Single()
                .GetProperty("detection").Deserialize<FilmSourceDetection>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var recoveryEvents = ProbeEvents(root, id).Skip(recoveryStart).ToArray();
            var recoveryWorker = recoveryEvents.Single(value => value.GetProperty("kind").GetString() == "worker.start");
            int recoveryThreadId = recoveryWorker.GetProperty("threadId").GetInt32();
            Check(service.Cats.Single().Generation == restarted.Generation &&
                recovered is { Result: "valid", PlaybackResolved: true, MediaPlaybackVerified: false } &&
                !recoveryWorker.GetProperty("isMainThread").GetBoolean() && recoveryThreadId != initialThreadId &&
                File.ReadAllText(marker) == "3" &&
                ProbeOperations(recoveryEvents).SequenceEqual(["home", "category", "detail", "play", "search"]) &&
                SameDetection(recovered, recoveryDetection) && SameDetection(recovered, Video(service, key).Detection),
                "explicit restart and a new five-stage probe succeed on a new Worker and persist the same detection");
            Console.WriteLine($"DETECTION_WORKER_EXIT worker={initialThreadId}->{recoveryThreadId}; " +
                $"generation={installed.Generation}->{restarted.Generation}; failedGeneration={actual.GetProperty("generation").GetInt64()}; " +
                "callerCancelled=false; failure=STALE_SESSION; autoRestart=false; replay=false; baselinePreserved=true; restartRecovery=valid");
        }, store => { documents = store; return store; });
    }

    private static JsonElement[] ProbeEvents(string root, string id)
    {
        string file = Path.Combine(root, "script-runtime", "volumes", id, "fixture-audit.jsonl");
        return File.Exists(file) ? File.ReadAllLines(file).Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray() : [];
    }

    private static IEnumerable<string?> ProbeOperations(IEnumerable<JsonElement> events) =>
        events.Where(value => value.GetProperty("kind").GetString() == "request")
            .Select(value => value.GetProperty("operation").GetString());

    private static bool SameDetection(FilmSourceDetection? expected, FilmSourceDetection? actual) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));

    private static FilmVideoSource Video(FilmSourceService service, FilmSourceKey key) =>
        service.Videos.Single(video => video.Key == key);

    private static void AssertSite(FilmVideoSource video, string revision)
    {
        Check(video.Name == "Fixture " + revision && video.Api == SiteApi, "site discovery preserves the fixture name and API");
        Check(video.Actions.Order(StringComparer.Ordinal).SequenceEqual(["category", "detail", "home", "play", "search"]),
            "discovery exposes the five real HTTP actions");
        foreach (string operation in new[] { "home", "category", "detail", "play", "search" })
            Check(video.Methods.TryGetValue(operation, out string? method) &&
                method == (operation is "home" or "search" ? "GET" : "POST"), "declared HTTP method for " + operation);
    }

    private static void AssertDetection(FilmSourceDetection? result)
    {
        if (result is null) throw new InvalidOperationException("The detection result was not persisted.");
        Check(result.Result == "valid" && result.PlaybackResolved && !result.MediaPlaybackVerified &&
            result.CheckedAt > 0 && result.Error is null,
            "fixture returns resolve a playback address without playing media: " + result.Error);
        Check(result.Steps.Select(step => step.Operation).SequenceEqual(["home", "category", "detail", "play", "search"]) &&
            result.Steps.All(step => step.State == "success" && step.Error is null),
            "all five HTTP calls complete successfully in the CatPawRunner probe order");
    }

    private static void AssertOptions(FilmSourceService service, FilmSourceKey first, FilmVideoOptions firstOptions,
        FilmSourceKey second, FilmVideoOptions secondOptions)
    {
        Check(service.Videos.Count == 2 && Video(service, first).Options == firstOptions &&
            Video(service, second).Options == secondOptions &&
            service.Videos.Count(video => video.Options.AggregateImages) == 1,
            "four persisted options remain independent and aggregate images stays single-select");
    }

    private static async Task ExpectCode(string code, Func<Task> operation)
    {
        try { await operation(); }
        catch (FilmSourceException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected FilmSourceException: " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string NewData()
    {
        string root = TestWorkspace.DataPath("film-source-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        return root;
    }

    private static async Task WithServiceAsync(string root, Func<FilmSourceService, NodeRuntime, CancellationToken, Task> run,
        Func<IDocumentStore, IDocumentStore>? decorateStore = null)
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
            ?? throw new InvalidOperationException("Run scripts/windows/test.ps1; prepare-test-bundles aggregate inputs are required.");
        RuntimeModule[] modules = RuntimeHttpClientTests.Modules;
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var store = new LocalDocumentStore(Path.Combine(root, "documents"), AppIdentity.NativeNamespaces,
            AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
        IDocumentStore documents = decorateStore?.Invoke(store) ?? store;
        await using var runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules,
            Path.Combine(root, "script-runtime"), deadline.Token, settingsStore: documents,
            settingsNamespaces: AppIdentity.RuntimeSettingsNamespaces);
        using var process = Process.GetProcessById(runtime.ProcessId);
        _ = process.SafeHandle; // Pin this exact child before shutdown so PID reuse cannot satisfy the assertion.
        try
        {
            Check(runtime.IsConnected && !process.HasExited, "the aggregate Node process is connected");
            await using var service = new FilmSourceService(runtime, documents);
            await run(service, runtime, deadline.Token);
        }
        finally
        {
            await runtime.DisposeAsync();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.HasExited && !runtime.IsConnected, "shutdown closes the runtime and actually exits the owned Node process");
        }
    }

    private sealed class FailNextWriteStore(IDocumentStore inner) : IDocumentStore
    {
        internal readonly DocumentStoreException Failure = new(DocumentStoreError.Storage);
        internal bool FailNext;
        internal int Failures;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(documentNamespace, cancellationToken);
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            if (FailNext && documentNamespace == FilmSourceService.DocumentNamespace)
            { FailNext = false; Failures++; throw Failure; }
            return inner.WriteAsync(documentNamespace, data, expected, cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The fixture owns the real store.
    }

    private sealed class OfflineRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Offline settings must not contact Node."); }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Offline settings must not upload source."); }
    }

    private sealed class ScriptServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> requests = [];
        private readonly Task accepting;
        private int count;
        internal string Source;
        internal Func<CancellationToken, Task>? BeforeReply;
        internal bool ExpectClientCancellation;
        internal int Requests => Volatile.Read(ref count);
        internal string Url { get; }
        internal ScriptServer(string source)
        {
            Source = source;
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/cat.js";
            accepting = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    requests.Add(ReplyAsync(client));
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        }
        private async Task ReplyAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? request = await reader.ReadLineAsync(lifetime.Token);
                    Check(request?.StartsWith("GET /cat.js", StringComparison.Ordinal) == true,
                        "network source loading uses its real saved HTTP URL");
                    while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 }) { }
                    Interlocked.Increment(ref count);
                    byte[] body = Encoding.UTF8.GetBytes(Source);
                    if (BeforeReply is { } before) await before(lifetime.Token);
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/javascript\r\n" +
                        $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, lifetime.Token);
                    await stream.WriteAsync(body, lifetime.Token);
                    await stream.FlushAsync(lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (IOException) when (lifetime.IsCancellationRequested || ExpectClientCancellation) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Stop();
            await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Dispose();
        }
    }

    // The only control difference is removal of error from the first HTTP200 detail.
    private static string DetailFallbackScript(bool withError) => $$"""
        const http = require('http');
        const fs = require('fs');
        const API = '/api/fallback';
        const firstDetail = {{(withError ? """{"list":[],"error":"分类详情原始错误"}""" : """{"list":[]}""")}};
        let server;
        function audit(value) {
          fs.appendFileSync('/fixture-audit.jsonl', JSON.stringify(value) + '\n');
        }
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            const url = new URL(request.url, 'http://127.0.0.1');
            const reply = (operation, value) => {
              audit({ kind: 'response', operation, statusCode: response.statusCode, value });
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify(value));
            };
            if (url.pathname === '/full-config' || url.pathname === '/config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'fallback', name: 'Fallback', api: API,
                actions: ['home', 'category', 'detail', 'play', 'search']
              }] } }));
              return;
            }
            if (!url.pathname.startsWith(API + '/')) {
              response.statusCode = 404;
              response.end();
              return;
            }
            const operation = url.pathname.slice(API.length + 1);
            const chunks = [];
            request.on('data', chunk => chunks.push(Buffer.from(chunk)));
            request.once('end', () => {
              const text = Buffer.concat(chunks).toString('utf8');
              const input = request.method === 'GET'
                ? Object.fromEntries(url.searchParams) : text ? JSON.parse(text) : {};
              audit({ kind: 'request', operation, method: request.method, input });
              if (operation === 'home')
                return reply(operation, { class: [{ type_id: 'movies', type_name: 'Movies' }] });
              if (operation === 'category')
                return reply(operation, { list: [{ vod_id: 'first' }] });
              if (operation === 'detail' && input.id === 'first')
                return reply(operation, firstDetail);
              if (operation === 'search')
                return reply(operation, { list: [{ vod_id: 'searched' }] });
              if (operation === 'detail' && input.id === 'searched')
                return reply(operation, { list: [{
                  vod_id: 'searched', vod_play_from: 'direct', vod_play_url: '1$working-id'
                }] });
              if (operation === 'play')
                return reply(operation, { parse: 0, url: 'https://media.invalid/video.mp4' });
              response.statusCode = 400;
              return reply(operation, { error: 'unexpected controlled input' });
            });
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
        """;

    private static string NavigationRootScript(bool failed) => $$"""
        const http = require('http'), fs = require('fs');
        const root = {
          {{(failed ? "ok: false," : "")}} msg: '根导航失败',
          list: [{ vod_id: 'child', vod_navigation: { action: 'detail', payload: { id: 'child' } } }]
        };
        let server;
        const audit = value => fs.appendFileSync('/fixture-audit.jsonl', JSON.stringify(value) + '\n');
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            const path = request.url.split('?')[0];
            response.setHeader('content-type', 'application/json');
            if (path === '/config' || path === '/full-config')
              return response.end(JSON.stringify({ video: { sites: [{
                key: 'navigation', api: '/api/navigation', actions: ['home', 'category', 'detail', 'play', 'search']
              }] } }));
            const operation = path.slice('/api/navigation/'.length), chunks = [];
            request.on('data', chunk => chunks.push(Buffer.from(chunk)));
            request.once('end', () => {
              const text = Buffer.concat(chunks).toString('utf8'), input = text ? JSON.parse(text) : {};
              audit({ kind: 'request', operation, input });
              const value = operation === 'home' ? { class: [{ type_id: 'movies' }] } :
                operation === 'category' ? { list: [{ vod_id: 'root' }] } :
                operation === 'detail' && input.id === 'root' ? root :
                operation === 'detail' && input.id === 'child' ? { list: [{
                  vod_id: 'leaf', vod_play_from: 'direct', vod_play_url: '1$working-id'
                }] } :
                operation === 'play' ? { url: 'https://media.invalid/video.mp4' } :
                operation === 'search' ? { list: [] } : { error: 'unexpected controlled input' };
              audit({ kind: 'response', operation, statusCode: response.statusCode, value });
              response.end(JSON.stringify(value));
            });
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
        """;

    // The second search rejects outside the callback return chain to fail this Worker.
    // The private-volume count permits recovery only after an explicit restart.
    private const string WorkerExitScript = """
        const http = require('http');
        const fs = require('fs');
        const { isMainThread, threadId } = require('worker_threads');
        const marker = '/search-count';
        let server;
        function audit(value) {
          fs.appendFileSync('/fixture-audit.jsonl', JSON.stringify(value) + '\n');
        }
        module.exports.start = () => {
          audit({ kind: 'worker.start', isMainThread, threadId });
          server = http.createServer((request, response) => {
            const path = request.url.split('?')[0];
            const reply = (operation, value) => {
              audit({ kind: 'response', operation, statusCode: response.statusCode, value });
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify(value));
            };
            if (path === '/full-config' || path === '/config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'worker-exit', name: 'Worker exit', api: '/api/worker-exit',
                actions: ['home', 'category', 'detail', 'play', 'search']
              }] } }));
              return;
            }
            if (!path.startsWith('/api/worker-exit/')) {
              response.statusCode = 404;
              response.end();
              return;
            }
            const operation = path.slice('/api/worker-exit/'.length);
            audit({ kind: 'request', operation, method: request.method });
            if (operation === 'home')
              return reply(operation, { class: [{ type_id: 'movies', type_name: 'Movies' }] });
            if (operation === 'category')
              return reply(operation, { list: [{ vod_id: 'v' }] });
            if (operation === 'detail')
              return reply(operation, { list: [{
                vod_id: 'v', vod_play_from: 'direct', vod_play_url: '1$working-id'
              }] });
            if (operation === 'play')
              return reply(operation, { parse: 0, url: 'https://media.invalid/video.mp4' });
            if (operation === 'search') {
              const count = Number(fs.existsSync(marker) ? fs.readFileSync(marker, 'utf8') : '0') + 1;
              fs.writeFileSync(marker, String(count));
              if (count === 2) {
                audit({ kind: 'worker.exit-attempt', count, threadId });
                void Promise.reject(new Error('fixture-worker-exit'));
                return;
              }
              return reply(operation, { list: [], searchCount: count });
            }
            response.statusCode = 400;
            return reply(operation, { error: 'unexpected controlled input' });
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
        """;

    // A CatNode HTTP script: returned IDs drive subsequent requests, and invalid wire inputs throw in the guest.
    // GET search checks encoded Unicode query values; POST category/detail/play check typed JSON payloads.
    private static string Script(string revision, bool auditStarts = false) => $$"""
        const http = require('http');
        const fs = require('fs');
        const revision = {{JsonSerializer.Serialize(revision)}};
        const auditStarts = {{(auditStarts ? "true" : "false")}};
        fs.appendFileSync('/source-starts.jsonl', JSON.stringify(revision) + '\n');
        const API = '/spider/film-fixture/3';
        const TYPE = '电影/中文 & 1';
        const VIDEO = '片/中文?键=一&二';
        const FLAG = '线路 甲';
        const EPISODE = 'episode/中文?token=一&part=2';
        const order = ['home', 'category', 'detail', 'play', 'search'];
        let next = 0, server;
        function requireValue(value, message) {
          if (!value) throw new Error('film fixture: ' + message);
        }
        function number(path) {
          return fs.existsSync(path) ? Number(fs.readFileSync(path, 'utf8')) : 0;
        }
        const starts = auditStarts ? number('/film-starts.txt') + 1 : 0;
        if (auditStarts) fs.writeFileSync('/film-starts.txt', String(starts));
        function expect(operation, actual, expected) {
          requireValue(order[next] === operation, 'unexpected action ' + operation + ', expected ' + order[next]);
          requireValue(actual && typeof actual === 'object' && !Array.isArray(actual), operation + ' input object');
          requireValue(JSON.stringify(Object.keys(actual).sort()) === JSON.stringify(Object.keys(expected).sort()),
            operation + ' input fields');
          for (const key of Object.keys(expected))
            requireValue(JSON.stringify(actual[key]) === JSON.stringify(expected[key]), operation + ' argument ' + key);
          next = (next + 1) % order.length;
        }
        const routes = new Map([
          ['GET ' + API + '/home', input => {
            expect('home', input, {});
            if (auditStarts) {
              const checks = number('/film-checks.txt') + 1;
              requireValue(starts === checks, 'unexpected script execution: starts=' + starts + ', probes=' + checks);
              fs.writeFileSync('/film-checks.txt', String(checks));
            }
            return { data: { types: [{ type_id: TYPE, type_name: '电影' }] } };
          }],
          ['POST ' + API + '/category', input => {
            expect('category', input, { id: TYPE, page: 1, filter: true, filters: {} });
            return { data: { items: [{ vod_id: VIDEO, vod_name: '受控影片' }] } };
          }],
          ['POST ' + API + '/detail', input => {
            expect('detail', input, { id: VIDEO, vod_id: VIDEO });
            return { list: [{ vod_id: VIDEO, vod_play_from: FLAG, vod_play_url: '第一集$' + EPISODE }] };
          }],
          ['POST ' + API + '/play', input => {
            expect('play', input, { flag: FLAG, id: EPISODE, siteApi: API });
            return { url: ['测试清晰度', 'https://media.invalid/film-fixture.m3u8'] };
          }],
          ['GET ' + API + '/search', input => {
            expect('search', input, { wd: '斗破', page: '1' });
            return { list: [{ vod_id: VIDEO, vod_name: '受控搜索结果' }] };
          }],
        ]);
        function json(response, value) {
          response.setHeader('content-type', 'application/json; charset=utf-8');
          response.end(JSON.stringify(value));
        }
        async function route(request, response) {
          const url = new URL(request.url, 'http://127.0.0.1');
          if (url.pathname === '/full-config' || url.pathname === '/config') {
            requireValue(request.method === 'GET', 'config method');
            return json(response, { video: { sites: [{ key: 'fixture', name: 'Fixture ' + revision, api: API }] } });
          }
          if (!url.pathname.startsWith(API + '/')) {
            response.statusCode = 404;
            return json(response, { error: 'fixture route not found' });
          }
          const handler = routes.get(request.method + ' ' + url.pathname);
          requireValue(typeof handler === 'function', 'wrong method or route: ' + request.method + ' ' + url.pathname);
          const chunks = [];
          for await (const chunk of request) chunks.push(Buffer.from(chunk));
          const body = Buffer.concat(chunks).toString('utf8');
          let input;
          if (request.method === 'GET') {
            requireValue(body.length === 0, 'GET body must be empty');
            input = Object.fromEntries(url.searchParams);
          } else {
            requireValue(url.search === '' && body.length > 0, 'POST requires a JSON body without query parameters');
            input = JSON.parse(body);
          }
          json(response, handler(input));
        }
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            route(request, response).catch(error => {
              response.statusCode = 500;
              json(response, { error: error.message });
            });
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => {
          server?.closeAllConnections();
          server?.close();
        };
        """;
}
