using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class RuntimeTests
{
    private static string Bundles => Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
        ?? throw new InvalidOperationException("Run scripts/windows/test.ps1; real runtime inputs are required.");
    private static string Node => Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE")
        ?? throw new InvalidOperationException("A verified Node executable is required.");
    private static string NewData()
    {
        var parent = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
            ?? throw new InvalidOperationException("A dedicated test data directory is required.");
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private const string Identity = "aggregate";
    private static RuntimeModule[] Modules => RuntimeHttpClientTests.Modules;
    private static string[] Namespaces => ["music.library.v1", "music.accounts.v1"];
    private static string ScriptData() => Path.Combine(NewData(), "script-runtime");

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("native-runtime: App real Node/ACL/PID handshake preserves aggregate identity", async () =>
        {
            var modules = Modules;
            var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, modules);
            await using var store = new LocalDocumentStore(NewData(), Namespaces, protectedNamespaces: ["music.accounts.v1"]);
            var scriptData = ScriptData();
            int pid;
            await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, scriptData))
            {
                pid = runtime.ProcessId;
                BridgeTests.Equal(true, runtime.IsConnected);
                var status = await runtime.CallAsync("music.script.sources.list", new { });
                BridgeTests.Equal("music.script.v3", status.GetProperty("contract").GetString());
                BridgeTests.Equal(true, status.GetProperty("ok").GetBoolean());
                BridgeTests.Equal(0, status.GetProperty("value").GetProperty("items").GetArrayLength());
                var cat = await runtime.CallAsync("cat.script.status", new { });
                BridgeTests.Equal(true, cat.GetProperty("ok").GetBoolean());
                var catStatus = cat.GetProperty("value");
                BridgeTests.Equal(Path.GetFullPath(Path.Combine(scriptData, "settings.json")),
                    catStatus.GetProperty("settingsPath").GetString());
                var sourceStatus = await runtime.CallAsync("source.runtime.status", new { });
                BridgeTests.Equal(true, sourceStatus.GetProperty("ok").GetBoolean());
                BridgeTests.Equal("source.runtime.v2", sourceStatus.GetProperty("contract").GetString());
                BridgeTests.Equal(0, sourceStatus.GetProperty("value").GetProperty("instances").GetArrayLength());
                BridgeTests.Equal(true, runtime.IsConnected);
                foreach (var method in new[] { "music.library.list", "music.queue.get", "music.accounts.list",
                    "music.catalog.search", "film.script.status", "film.sources.list", "reader.rules.reset",
                    "reader.sources.list", "reader.content.chapter", "missing.operation" })
                    await BridgeTests.Expect("METHOD_UNAVAILABLE", () => runtime.CallAsync(method, new { }));
            }
            AssertExited(pid);
        });
        yield return new("native-runtime: native music queue survives script process restart through DPAPI", async () =>
        {
            var root = NewData();
            var modules = Modules;
            var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, modules);
            string collectionId;
            await using (var store = new LocalDocumentStore(root, Namespaces, protectedNamespaces: ["music.accounts.v1"]))
            await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, ScriptData()))
            await using (var library = new MusicLibraryOwner(store))
            {
                var created = await library.ExecuteAsync("music.library.create", new { name = "本机契约测试" });
                BridgeTests.Equal(true, created.GetProperty("ok").GetBoolean());
                collectionId = created.GetProperty("collection").GetProperty("id").GetString()!;
                var added = await library.ExecuteAsync("music.library.addSongs", new
                {
                    id = collectionId, ifRevision = 1,
                    songs = new[] { MusicSongFixture.Local("test:local", "确定性测试") }
                });
                BridgeTests.Equal(true, added.GetProperty("ok").GetBoolean());
                var queue = await library.ExecuteAsync("music.queue.playCollection", new { id = collectionId, ifRevision = 2, index = 0 });
                BridgeTests.Equal(true, queue.GetProperty("ok").GetBoolean());
                await BridgeTests.Expect("METHOD_UNAVAILABLE", () => runtime.CallAsync("music.library.list", new { }));
            }
            await using (var store = new LocalDocumentStore(root, Namespaces, protectedNamespaces: ["music.accounts.v1"]))
            await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, ScriptData()))
            await using (var library = new MusicLibraryOwner(store))
            {
                var detail = await library.ExecuteAsync("music.library.detail", new { id = collectionId, offset = 0, limit = 10 });
                BridgeTests.Equal("确定性测试", detail.GetProperty("songs")[0].GetProperty("title").GetString());
                var queue = await library.ExecuteAsync("music.queue.get", new { offset = 0, limit = 1 });
                BridgeTests.Equal("test:local", queue.GetProperty("current").GetProperty("song").GetProperty("songId").GetString());
            }
        });
        yield return new("native-runtime: child crash closes pending generation and reclaims process", async () =>
        {
            var modules = Modules;
            var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, modules);
            await using var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, ScriptData());
            using (var owned = Process.GetProcessById(runtime.ProcessId)) owned.Kill();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await BridgeTests.Expect("CLOSED", () => runtime.CallAsync("music.script.sources.list", new { }));
            AssertExited(runtime.ProcessId);
        });
        foreach (var mode in new[] { "exit-after-start", "disconnect-after-start" })
            yield return new("native-runtime: " + mode + " proves release without a close ACK and permits explicit recovery", async () =>
            {
                var firstFixture = CreateFixture(mode);
                var nextFixture = CreateFixture("normal");
                var firstReady = new TaskCompletionSource<NodeRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
                NodeRuntime? next = null;
                int launches = 0;
                var port = new AppRuntimeClient(async token =>
                {
                    var launch = ++launches;
                    var fixture = launch == 1 ? firstFixture : nextFixture;
                    var session = await NodeRuntime.StartAsync(Node, fixture.Bundle, Modules, ScriptData(), token);
                    if (launch == 1) firstReady.TrySetResult(session);
                    else next = session;
                    return session;
                });
                try
                {
                    port.Start();
                    var first = await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var pending = port.CallAsync("music.script.invoke");
                    await WaitForFixtureFileAsync(Path.Combine(firstFixture.Root, "business-entered"));
                    await File.WriteAllTextAsync(Path.Combine(firstFixture.Root, "retire"), "retire");
                    await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    await BridgeTests.Expect("CLOSED", () => pending);
                    BridgeTests.Equal(false, first.IsConnected);
                    BridgeTests.Equal(true, port.CanRetry);
                    AssertExited(first.ProcessId);
                    await first.DisposeAsync();
                    await first.DisposeAsync();
                    BridgeTests.Equal(false, File.Exists(Path.Combine(firstFixture.Root, "close-entered")));
                    await port.RetryAsync();
                    BridgeTests.Equal(2, launches);
                    BridgeTests.Equal(RuntimeConnectionState.Ready, port.State);
                    var successor = next ?? throw new InvalidOperationException("Missing recovery session");
                    BridgeTests.Equal(true, successor.IsConnected);
                    BridgeTests.Equal(false, File.Exists(Path.Combine(nextFixture.Root, "business-entered")));
                    BridgeTests.Equal(false, File.Exists(Path.Combine(nextFixture.Root, "close-entered")));
                    await port.DisposeAsync();
                    BridgeTests.Equal(true, File.Exists(Path.Combine(nextFixture.Root, "close-entered")));
                    AssertExited(successor.ProcessId);
                }
                finally { await port.DisposeAsync(); }
            });
        foreach (var protocol in new[] { "cat", "qysg" })
            yield return new("native-runtime: source " + protocol + " complete upload load invoke and unload use current public contract", async () =>
            {
                var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, Modules);
                var pid = 0;
                await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, Modules, ScriptData()))
                {
                    pid = runtime.ProcessId;
                    var revision = RuntimeSourceRevision.Next();
                    var source = protocol == "cat" ? "export default { searchContent(keyword) { return " +
                        "{list:[{vod_id:keyword,vod_name:keyword}]}; } };" :
                        "<html><script>function search(keyword) { return " +
                        "[{name:keyword,bookUrl:'https://books.invalid/book/1'}]; }</script></html>";
                    var bytes = Encoding.UTF8.GetBytes(source);
                    var upload = await runtime.UploadSourceAsync("source", "native-source", revision, bytes,
                        protocol == "cat" ? "fixture.js" : "fixture.html");
                    BridgeTests.Equal(true, upload.GetProperty("ok").GetBoolean());
                    BridgeTests.Equal("source.runtime.v2", upload.GetProperty("contract").GetString());
                    var receipt = upload.GetProperty("value");
                    BridgeTests.Equal("native-source", receipt.GetProperty("installationId").GetString());
                    BridgeTests.Equal(revision, receipt.GetProperty("sourceRevision").GetInt64());
                    BridgeTests.Equal(bytes.Length, receipt.GetProperty("sourceBytes").GetInt32());
                    BridgeTests.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), receipt.GetProperty("contentHash").GetString());
                    var loaded = await runtime.CallAsync("source.runtime.load", new
                    {
                        installationId = "native-source", sourceRevision = revision, revision = 1, expectedRevision = 0,
                        protocol, version = "1.0.0", metadata = new { }
                    });
                    BridgeTests.Equal(true, loaded.GetProperty("ok").GetBoolean());
                    BridgeTests.Equal("source.runtime.v2", loaded.GetProperty("contract").GetString());
                    var instance = loaded.GetProperty("value").GetProperty("instance");
                    BridgeTests.Equal("native-source", instance.GetProperty("installationId").GetString());
                    BridgeTests.Equal(1L, instance.GetProperty("revision").GetInt64());
                    BridgeTests.Equal(protocol, instance.GetProperty("protocol").GetString());
                    BridgeTests.Equal(true, instance.GetProperty("generation").GetInt64() > 0);
                    var input = new { installationId = "native-source", revision = 1,
                        generation = instance.GetProperty("generation").GetInt64(), action = "search", payload = new { keyword = "本机运行器" } };
                    var result = await runtime.CallAsync("source.runtime.invoke", input);
                    BridgeTests.Equal(true, result.GetProperty("ok").GetBoolean());
                    BridgeTests.Equal("source.runtime.v2", result.GetProperty("contract").GetString());
                    var value = result.GetProperty("value");
                    BridgeTests.Equal("本机运行器", protocol == "cat" ? value.GetProperty("list")[0].GetProperty("vod_name").GetString() :
                        value.GetProperty("books")[0].GetProperty("name").GetString());
                    if (protocol == "qysg")
                        BridgeTests.Equal("https://books.invalid/book/1", value.GetProperty("books")[0].GetProperty("bookUrl").GetString());
                    var unloaded = await runtime.CallAsync("source.runtime.unload", new { installationId = "native-source",
                        revision = 1, generation = instance.GetProperty("generation").GetInt64() });
                    BridgeTests.Equal(true, unloaded.GetProperty("ok").GetBoolean());
                    BridgeTests.Equal("source.runtime.v2", unloaded.GetProperty("contract").GetString());
                    BridgeTests.Equal(true, unloaded.GetProperty("value").GetProperty("unloaded").GetBoolean());
                    BridgeTests.Equal(true, unloaded.GetProperty("value").GetProperty("cleanupComplete").GetBoolean());
                    var status = await runtime.CallAsync("source.runtime.status");
                    BridgeTests.Equal(true, status.GetProperty("ok").GetBoolean());
                    BridgeTests.Equal(0, status.GetProperty("value").GetProperty("instances").GetArrayLength());
                }
                AssertExited(pid);
            });
        foreach (var (mode, code) in new[]
        {
            ("wrong-protocol", "INVALID_RESPONSE"), ("wrong-endpoint", "INVALID_RESPONSE"),
            ("extra-ready-field", "INVALID_RESPONSE"), ("extra-ready-bytes", "INVALID_RESPONSE"),
            ("invalid-json", "INVALID_RESPONSE"), ("large-ready", "INVALID_RESPONSE"),
            ("exit", "CLOSED"), ("timeout", "TIMEOUT")
        })
            yield return new("native-runtime: startup " + mode + " reclaims the real child", async () =>
            {
                var fixture = CreateFixture(mode);
                await BridgeTests.Expect(code, () => NodeRuntime.StartAsync(Node, fixture.Bundle, Modules, ScriptData()));
                var pid = await ReadFixturePidAsync(fixture.Root);
                AssertExited(pid);
            });
        yield return new("native-runtime: startup cancellation after process launch waits for actual exit", async () =>
        {
            var fixture = CreateFixture("timeout");
            using var caller = new CancellationTokenSource();
            var startup = NodeRuntime.StartAsync(Node, fixture.Bundle, Modules, ScriptData(), caller.Token);
            var pid = await ReadFixturePidAsync(fixture.Root);
            caller.Cancel();
            await BridgeTests.Expect("CANCELLED", () => startup);
            AssertExited(pid);
        });
        yield return new("native-runtime: close waits for remote cleanup and the actual process exit", async () =>
        {
            var fixture = CreateFixture("drain");
            var runtime = await NodeRuntime.StartAsync(Node, fixture.Bundle, Modules, ScriptData());
            var pid = runtime.ProcessId;
            try
            {
                var closing = runtime.DisposeAsync().AsTask();
                await WaitForFixtureFileAsync(Path.Combine(fixture.Root, "close-entered"));
                BridgeTests.Equal(false, closing.IsCompleted);
                BridgeTests.Equal(false, runtime.Completion.IsCompleted);
                using (var child = Process.GetProcessById(pid)) BridgeTests.Equal(false, child.HasExited);
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "release"), "release");
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                await runtime.Completion;
                AssertExited(pid);
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "release"), "release");
                await runtime.DisposeAsync();
            }
        });
        foreach (var (mode, code) in new[]
        {
            ("incomplete-close", "INVALID_RESPONSE"), ("disconnect-close", "CLOSED"), ("stubborn-close", "CLOSED"),
            ("extra-during-close", "INVALID_RESPONSE")
        })
            yield return new("native-runtime: " + mode + " remains a failure after process reclamation", async () =>
            {
                var fixture = CreateFixture(mode);
                var runtime = await NodeRuntime.StartAsync(Node, fixture.Bundle, Modules, ScriptData());
                var pid = runtime.ProcessId;
                await BridgeTests.Expect(code, () => runtime.DisposeAsync().AsTask());
                await BridgeTests.Expect(code, () => runtime.DisposeAsync().AsTask());
                await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                AssertExited(pid);
            });
        yield return new("native-runtime: post-ready pipe bytes close the session and reclaim the child", async () =>
        {
            var fixture = CreateFixture("extra-after-start");
            var runtime = await NodeRuntime.StartAsync(Node, fixture.Bundle, Modules, ScriptData());
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "extra"), "extra");
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            BridgeTests.Equal(false, runtime.IsConnected);
            await BridgeTests.Expect("INVALID_RESPONSE", () => runtime.DisposeAsync().AsTask());
            AssertExited(runtime.ProcessId);
        });
        yield return new("native-runtime: verified bundle rejects changed and unlisted payloads", () =>
        {
            var changed = CreateFixture("normal");
            File.AppendAllText(Path.Combine(changed.Root, "entry.mjs"), "\n// changed");
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.Verify(changed.Root, Identity, Modules));
            var extra = CreateFixture("normal");
            File.WriteAllText(Path.Combine(extra.Root, "extra.node"), "unlisted");
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.Verify(extra.Root, Identity, Modules));
            return Task.CompletedTask;
        });
        yield return new("native-runtime: wrong identity, module, version and remote data roots fail closed", async () =>
        {
            var description = BridgeCodec.Value(new { protocol = "runtime.http.v1", product = Identity,
                modules = new[] { new { id = "music", version = "0.2.0", methods = new[] { "music.script.sources.list" } } } });
            BridgeTests.Throws("INVALID_RESPONSE", () => NodeRuntime.ValidateDescription(description, Identity, Modules));
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.Verify(Path.Combine(Bundles, Identity), "music", Modules));
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity,
                [new("reader", "0.4.0")]));
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.SafePath(NewData(), "../escape"));
            var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, Modules);
            await BridgeTests.Expect("INVALID_MESSAGE", () => NodeRuntime.StartAsync(Node, bundle, Modules, "//server/share"));
        });
        yield return new("native-runtime: closing job reclaims owned Node descendants", async () =>
        {
            var start = new ProcessStartInfo(Node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("setTimeout(()=>{const c=require('node:child_process').spawn(process.execPath,['-e','setInterval(()=>{},60000)'],{stdio:'ignore'});console.log(c.pid)},250);setInterval(()=>{},60000)");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed");
            OwnedProcessJob? job = null;
            try
            {
                job = OwnedProcessJob.Attach(process);
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var childPid = int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture);
                using var child = Process.GetProcessById(childPid);
                _ = child.SafeHandle; // Pin the exact owned process before the job is closed.
                job.Dispose();
                await Task.WhenAll(process.WaitForExitAsync(), child.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5));
                BridgeTests.Equal(true, process.HasExited);
                BridgeTests.Equal(true, child.HasExited);
            }
            finally
            {
                job?.Dispose();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        });
        yield return new("native-files: explicit file grants persist separately from script namespace", async () =>
        {
            var root = NewData();
            var media = Path.Combine(root, "测试.wav");
            await File.WriteAllBytesAsync(media, Encoding.ASCII.GetBytes("test-only-grant-not-playback"));
            await using var store = new LocalDocumentStore(root, ["music.files.v1"]);
            var files = new GrantedMediaFiles(store, "music.files.v1", [".wav"]);
            var grants = await files.GrantAsync([media, media]);
            BridgeTests.Equal(1, grants.Count);
            var restored = new GrantedMediaFiles(store, "music.files.v1", [".wav"]);
            BridgeTests.Equal(media, (await restored.ResolveAsync(grants[0].Id)).LocalPath);
        });
    }
    private static (string Root, RuntimeBundle Bundle) CreateFixture(string mode)
    {
        var root = NewData();
        var script = """
            import net from 'node:net';
            import http from 'node:http';
            import fs from 'node:fs';
            const mode = MODE;
            const description = DESCRIPTION;
            fs.writeFileSync(new URL('./pid', import.meta.url), String(process.pid));
            if (mode === 'exit') process.exit(0);
            const argv = process.argv.slice(2);
            if (argv.length !== 4 || argv[0] !== '--meow-pipe' || argv[2] !== '--meow-session') process.exit(2);
            let config, server, initialized = false, retired = false, text = '';
            const stream = net.createConnection({path: '\\\\.\\pipe\\' + argv[1]});
            const exists = name => fs.existsSync(new URL('./' + name, import.meta.url));
            const ticker = setInterval(() => {
              if (mode === 'extra-after-start' && exists('extra') && !stream.destroyed) stream.write('x');
              if (!retired && exists('retire')) {
                retired = true;
                if (mode === 'exit-after-start') process.exit(0);
                if (mode === 'disconnect-after-start') stream.end();
              }
            }, 20);
            const finish = () => {
              if (mode === 'stubborn-close') return;
              clearInterval(ticker); server?.closeAllConnections(); server?.close(); stream.destroy();
            };
            stream.on('error', finish);
            stream.on('end', finish);
            stream.on('close', finish);
            stream.on('data', bytes => {
              if (initialized) { process.exitCode = 3; finish(); return; }
              text += bytes.toString('utf8');
              if (!text.endsWith('\n')) return;
              initialized = true;
              try { config = JSON.parse(text); } catch { process.exitCode = 4; finish(); return; }
              if (Object.keys(config).sort().join(',') !== 'dataRoot,sessionId' ||
                  config.sessionId !== argv[3] ||
                  typeof config.dataRoot !== 'string' ||
                  process.env.MEOWFILM_SCRIPT_DATA_ROOT !== undefined) {
                process.exitCode = 5; finish(); return;
              }
              if (mode === 'timeout') return;
              if (mode === 'invalid-json') { stream.write('{\n'); return; }
              if (mode === 'large-ready') { stream.write(' '.repeat(65537) + '\n'); return; }
              server = http.createServer(async (request, response) => {
                request.resume();
                if (request.headers.authorization !== undefined || request.headers['x-meow-session'] !== undefined) {
                  response.destroy(); return;
                }
                let value;
                if (request.url === '/v1/describe' && request.method === 'GET') value = description;
                else if (request.url === '/v1/music/invoke' && request.method === 'POST') {
                  fs.writeFileSync(new URL('./business-entered', import.meta.url), 'entered');
                  return;
                }
                else if (request.url === '/v1/close' && request.method === 'POST') {
                  fs.writeFileSync(new URL('./close-entered', import.meta.url), 'entered');
                  if (mode === 'disconnect-close') { response.destroy(); return; }
                  if (mode === 'extra-during-close') stream.write('x');
                  if (mode === 'drain') {
                    await new Promise(resolve => {
                      const waiting = setInterval(() => { if (exists('release')) { clearInterval(waiting); resolve(); } }, 20);
                    });
                  }
                  value = {closed:true, cleanupComplete:mode !== 'incomplete-close'};
                } else { response.destroy(); return; }
                response.writeHead(200, {'content-type':'application/json', 'x-meow-request-id':request.headers['x-meow-request-id']});
                response.end(JSON.stringify({ok:true, contract:'runtime.http.v1', value}));
              });
              server.listen(0, '127.0.0.1', () => {
                const ready = {protocol:mode === 'wrong-protocol' ? 'wrong' : 'runtime.http.v1',
                  baseUrl:mode === 'wrong-endpoint' ? 'file:///invalid-runtime' : `http://127.0.0.1:${server.address().port}`};
                if (mode === 'extra-ready-field') ready.extra = true;
                stream.write(JSON.stringify(ready) + '\n' + (mode === 'extra-ready-bytes' ? 'x' : ''));
              });
            });
            """.Replace("MODE", JsonSerializer.Serialize(mode), StringComparison.Ordinal)
            .Replace("DESCRIPTION", JsonSerializer.Serialize(RuntimeHttpClientTests.Description()), StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(script);
        File.WriteAllBytes(Path.Combine(root, "entry.mjs"), bytes);
        File.WriteAllBytes(Path.Combine(root, "runtime-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, status = "complete", product = Identity, transport = "windows-http-v1",
            assemblyProfile = "common-native", platform = "win32", entry = "entry.mjs",
            modules = Modules.Select(module => module.Id).ToArray(),
            files = new[] { new { path = "entry.mjs", sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes = bytes.Length } }
        }));
        return (root, RuntimeBundle.Verify(root, Identity, Modules));
    }
    private static async Task<int> ReadFixturePidAsync(string root)
    {
        var path = Path.Combine(root, "pid");
        await WaitForFixtureFileAsync(path);
        return int.Parse(await File.ReadAllTextAsync(path), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static async Task WaitForFixtureFileAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path)) await Task.Delay(20, deadline.Token);
    }
    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new InvalidOperationException("Owned child leaked"); }
        catch (ArgumentException) { }
    }
}
