using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        yield return new("native-film-runtime: real Cat probe and deletion survive restart and update until explicit reimport",
            ProbeAndReimport);
        yield return new("native-film-runtime: cold start restores enabled cats and four options without executing disabled scripts",
            ColdStartAndOptions);
    }

    private static async Task ProbeAndReimport()
    {
        string root = NewData();
        var key = new FilmSourceKey("probe-cat", SiteKey);
        string original = Script("original"), updated = Script("updated");
        await WithServiceAsync(root, async (service, token) =>
        {
            await service.StartAsync(token);
            Check(service.Cats.Count == 0 && service.Videos.Count == 0, "dedicated data root starts empty");
            var imported = await service.ImportLocalAsync("检测猫", original, key.InstallationId, token);
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
            var replaced = await service.UpdateCatAsync(key.InstallationId, updated, token);
            Check(replaced.CanDetect && replaced.UpdateState == "updated" &&
                replaced.Sites.Single().Name == "Fixture updated",
                "ordinary update really installs and discovers the replacement script");
            Check(service.Videos.Count == 0, "successful ordinary update preserves the deletion");
            await service.RefreshAsync(token);
            Check(service.Videos.Count == 0, "status synchronization preserves the deletion");
        });

        await WithServiceAsync(root, async (service, token) =>
        {
            await service.InitializeAsync(token);
            Check(service.Cats.Single().InstallationId == key.InstallationId && service.Videos.Count == 0,
                "reopening the persistent service retains the deletion");
            await service.StartAsync(token);
            Check(service.Cats.Single().CanDetect && service.Cats.Single().Sites.Single().Name == "Fixture updated" &&
                service.Videos.Count == 0, "cold Node startup restores the cat without restoring its deleted video");
            await service.RefreshAsync(token);
            Check(service.Videos.Count == 0, "cold-start synchronization keeps the tombstone");

            await ExpectCode("SCRIPT_EXCEPTION", () => service.ImportLocalAsync("失败导入",
                "module.exports = { start( };", key.InstallationId, token));
            Check(service.Videos.Count == 0 && service.Cats.Single().Name == "检测猫" && service.Cats.Single().CanDetect,
                "failed explicit reimport preserves the deletion and the working installation");
            var reimported = await service.ImportLocalAsync("重新导入猫", updated, key.InstallationId, token);
            Check(reimported.InstallationId == key.InstallationId && reimported.Name == "重新导入猫" &&
                reimported.Enabled && reimported.CanDetect &&
                service.Cats.Count == 1 && service.Videos.Count == 1,
                "successful explicit reimport restores the video under the same installation identity");
            AssertSite(Video(service, key), "updated");
            AssertDetection(await service.DetectAsync(key, token));
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
        await WithServiceAsync(root, async (service, token) =>
        {
            await service.StartAsync(token);
            await service.ImportLocalAsync("启用猫", source, enabled.InstallationId, token);
            await service.ImportLocalAsync("待停用猫", source, disabled.InstallationId, token);
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
            Check(!stopped.Enabled && !stopped.CanDetect && !stopped.CanRestart && stopped.State == "disabled",
                "disabling a cat stops its execution and removes detection/restart availability");
            Check(service.Videos.Count == 2 && Video(service, disabled).Detection?.CheckedAt == disabledCheckedAt,
                "disabling keeps the video list and previous detection");
            await ExpectCode("DISABLED", () => service.DetectAsync(disabled, token));
            await ExpectCode("DISABLED", () => service.RestartCatAsync(disabled.InstallationId, token));
        });

        await WithServiceAsync(root, async (service, token) =>
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
            var resumed = await service.EnableCatAsync(disabled.InstallationId, true, token);
            Check(resumed.Enabled && resumed.CanDetect, "explicit enable resumes the saved installation");
            // This probe throws inside the guest if the disabled script ran during cold startup.
            AssertDetection(await service.DetectAsync(disabled, token));
            AssertOptions(service, enabled, enabledOptions, disabled, disabledOptions);
        });
    }

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

    private static async Task WithServiceAsync(string root, Func<FilmSourceService, CancellationToken, Task> run)
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
            ?? throw new InvalidOperationException("Run scripts/windows/test.ps1; prepare-test-bundles aggregate inputs are required.");
        RuntimeModule[] modules = [new("music", "0.8.0"), new("cat", "0.1.0"), new("source", "0.1.0")];
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules,
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(),
            Path.Combine(root, "script-runtime"), deadline.Token);
        using var process = Process.GetProcessById(runtime.ProcessId);
        _ = process.SafeHandle; // Pin this exact child before shutdown so PID reuse cannot satisfy the assertion.
        try
        {
            Check(runtime.IsConnected && !process.HasExited, "the aggregate Node process is connected");
            await using var store = new LocalDocumentStore(Path.Combine(root, "documents"), [FilmSourceService.DocumentNamespace]);
            await using var service = new FilmSourceService(runtime, store);
            await run(service, deadline.Token);
        }
        finally
        {
            await runtime.DisposeAsync();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(process.HasExited && !runtime.IsConnected, "shutdown closes the bridge and actually exits the owned Node process");
        }
    }

    // A CatNode HTTP script: returned IDs drive subsequent requests, and invalid wire inputs throw in the guest.
    // GET search checks encoded Unicode query values; POST category/detail/play check typed JSON payloads.
    private static string Script(string revision, bool auditStarts = false) => $$"""
        const http = require('http');
        const fs = require('fs');
        const revision = {{JsonSerializer.Serialize(revision)}};
        const auditStarts = {{(auditStarts ? "true" : "false")}};
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
