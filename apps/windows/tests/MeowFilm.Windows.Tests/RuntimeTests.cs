using System.Diagnostics;
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
    private static RuntimeModule[] Modules => [new("music", "0.7.0"), new("cat", "0.1.0")];
    private static string[] Namespaces => ["music.library.v1", "music.accounts.v1"];
    private static string ScriptData() => Path.Combine(NewData(), "script-runtime");

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("native-runtime: App real Node/ACL/PID handshake preserves aggregate identity", async () =>
        {
            var modules = Modules;
            var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, modules);
            await using var store = new LocalDocumentStore(NewData(), Namespaces, protectedNamespaces: ["music.accounts.v1"]);
            await using var documents = new ProtectedDocumentBridge(store, []);
            var scriptData = ScriptData();
            int pid;
            await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, documents.Methods, scriptData))
            {
                pid = runtime.ProcessId;
                BridgeTests.Equal(true, runtime.IsConnected);
                var status = await runtime.CallAsync("music.script.status", new { });
                BridgeTests.Equal("music.script.v2", status.GetProperty("contract").GetString());
                BridgeTests.Equal(false, status.GetProperty("documentAccess").GetBoolean());
                BridgeTests.Equal(0, status.GetProperty("instances").GetArrayLength());
                var cat = await runtime.CallAsync("cat.script.status", new { });
                BridgeTests.Equal(true, cat.GetProperty("ok").GetBoolean());
                using var catStatus = JsonDocument.Parse(cat.GetProperty("data").GetString()!);
                BridgeTests.Equal(Path.GetFullPath(Path.Combine(scriptData, "settings.json")),
                    catStatus.RootElement.GetProperty("settingsPath").GetString());
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
            await using (var documents = new ProtectedDocumentBridge(store, []))
            await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, documents.Methods, ScriptData()))
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
            await using (var documents = new ProtectedDocumentBridge(store, []))
            await using (var runtime = await NodeRuntime.StartAsync(Node, bundle, modules, documents.Methods, ScriptData()))
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
            await using var runtime = await NodeRuntime.StartAsync(Node, bundle, modules,
                new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(), ScriptData());
            using (var owned = Process.GetProcessById(runtime.ProcessId)) owned.Kill();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await BridgeTests.Expect("CLOSED", () => runtime.CallAsync("music.script.status", new { }));
        });
        yield return new("native-runtime: wrong identity, module, version and remote data roots fail closed", async () =>
        {
            var description = BridgeCodec.Value(new { protocol = 1, product = Identity,
                modules = new[] { new { id = "music", version = "0.2.0", methods = new[] { "music.script.status" } } } });
            BridgeTests.Throws("INVALID_MESSAGE", () => NodeRuntime.ValidateDescription(description, Identity, Modules));
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.Verify(Path.Combine(Bundles, Identity), "music", Modules));
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity,
                [new("reader", "0.4.0")]));
            BridgeTests.Throws("INVALID_MESSAGE", () => RuntimeBundle.SafePath(NewData(), "../escape"));
            var bundle = RuntimeBundle.Verify(Path.Combine(Bundles, Identity), Identity, Modules);
            await BridgeTests.Expect("INVALID_MESSAGE", () => NodeRuntime.StartAsync(Node, bundle, Modules,
                new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(), "//server/share"));
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
            await using var bridge = new ProtectedDocumentBridge(store, []);
            var reply = BridgeCodec.Value(await bridge.Methods["platform.documents.read.begin"](
                BridgeCodec.Value(new { version = 1, @namespace = "music.files.v1", token = Guid.NewGuid().ToString("D") }), default));
            BridgeTests.Equal(false, reply.GetProperty("ok").GetBoolean());
        });
    }
    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new InvalidOperationException("Owned child leaked"); }
        catch (ArgumentException) { }
    }
}
