using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicScriptIntegrationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native LX real pipe: two instances use explicit source and native protected storage", TwoInstances);
        yield return new("Native LX real pipe: failed trial retains prior generation and durable source state", Update);
        yield return new("Native LX real pipe: restart reads native state without Node then resumes isolated source data", Restart);
        yield return new("Native LX real pipe: original local lyric protocol is parsed in native code", Lyrics);
        yield return new("Native LX real pipe: explicit crashed-process recovery preserves native data and rehydrates only selected source", Recover);
    }
    private const string SourceTemplate = """
        const label = __LABEL__;
        lx.on(lx.EVENT_NAMES.request, async ({source,action,info}) => {
          if(action==='lyric') return {lyric:'[00:01.00]native timing',tlyric:'[00:01.00]原生时间轴'};
          const count=(await meow.storage.get('count')||0)+1;
          await meow.storage.set('count',count);
          return {url:'https://media.invalid/'+label+'/'+count+'.mp3',headers:{Accept:'audio/*'}};
        });
        lx.send(lx.EVENT_NAMES.inited,{sources:{
          wy:{name:'same-name',type:'music',actions:['musicUrl'],qualitys:['320k']},
          local:{name:'local',type:'music',actions:['musicUrl','lyric'],qualitys:[]}
        }});
        """;
    private static string Source(string label) =>
        SourceTemplate.Replace("__LABEL__", JsonSerializer.Serialize(label), StringComparison.Ordinal);
    private static void Check(bool value, string message = "assertion") { if (!value) throw new InvalidOperationException(message); }
    private static string Data()
    {
        var root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
            ?? throw new InvalidOperationException("Dedicated test data root required.");
        string path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly DpapiDocumentStore Store;
        internal readonly MusicLibraryOwner Library;
        internal readonly OnDemandRuntimeClient Runtime;
        internal readonly MusicScriptOwner Owner;
        internal NodeRuntime? Child;
        internal Fixture(string path)
        {
            Store = new(path, ["music.library.v1"]);
            Library = new(Store);
            MusicScriptOwner? owner = null;
            Runtime = new(async token =>
            {
                string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
                    ?? throw new InvalidOperationException("Packaged runtime root required.");
                string node = Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE")
                    ?? throw new InvalidOperationException("Verified Node required.");
                RuntimeModule[] modules = [new("music", "0.4.0")];
                return Child = await NodeRuntime.StartAsync(node, RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules),
                    modules, owner!.ScriptCapabilities, token);
            });
            Owner = owner = new(Library, Runtime);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Owner.DisposeAsync(); }
            finally
            {
                await Runtime.DisposeAsync();
                await Library.DisposeAsync();
                await Store.DisposeAsync();
            }
        }
    }
    private static async Task<JsonElement> Ok(Fixture f, string method, object? p = null)
    {
        var result = await f.Owner.ExecuteAsync(method, p);
        Check(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText());
        return result;
    }
    private static async Task<string> Upload(Fixture f, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var begin = await Ok(f, "music.transfer.begin", new { kind = "script", length = bytes.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), basename = "fixture.js" });
        string token = begin.GetProperty("token").GetString()!;
        await Ok(f, "music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bytes) }); return token;
    }
    private static async Task<string> Install(Fixture f, string label)
    {
        string token = await Upload(f, Source(label));
        var result = await Ok(f, "music.sources.install", new { token, name = "Same Name", version = "1",
            permissions = new { networkOrigins = Array.Empty<string>(), storage = new[] { "read", "write" } } });
        return result.GetProperty("item").GetProperty("installationId").GetString()!;
    }
    private static async Task<string> Resolve(Fixture f, string id)
    {
        var result = await Ok(f, "music.online.resolve", new { installationId = id, platform = "wy", quality = "320k",
            song = MusicSongFixture.Online("wy", "11", "Track", "Fixture", duration: 180) });
        Check(!result.GetProperty("playbackVerified").GetBoolean());
        return result.GetProperty("url").GetString()!;
    }
    private static async Task TwoInstances()
    {
        await using var f = new Fixture(Data());
        string first = await Install(f, "first"), second = await Install(f, "second");
        Check(!f.Runtime.HasStarted);
        await Ok(f, "music.sources.enable", new { installationId = first });
        await Ok(f, "music.sources.enable", new { installationId = second });
        Check((await Resolve(f, first)).EndsWith("/first/1.mp3", StringComparison.Ordinal));
        Check((await Resolve(f, second)).EndsWith("/second/1.mp3", StringComparison.Ordinal));
        Check((await Resolve(f, first)).EndsWith("/first/2.mp3", StringComparison.Ordinal));
        var snapshot = await f.Library.ReadSourceMetadataAsync();
        long revision = snapshot.State.GetProperty("revision").GetInt64();
        var stored = await f.Library.ReadScriptValueAsync(second, "wy", "count", revision);
        Check(stored.GetInt32() == 1);
        var unavailable = false;
        try { await f.Runtime.CallAsync("music.library.list", new { }); }
        catch (BridgeException e) when (e.Code == "METHOD_UNAVAILABLE") { unavailable = true; }
        Check(unavailable);
    }
    private static async Task Update()
    {
        await using var f = new Fixture(Data()); string id = await Install(f, "original");
        await Ok(f, "music.sources.enable", new { installationId = id });
        var before = (await Ok(f, "music.sources.detail", new { installationId = id })).GetProperty("item");
        string token = await Upload(f, "throw new Error('synthetic failure')");
        var failed = await f.Owner.ExecuteAsync("music.sources.update", new { installationId = id, token, version = "2",
            permissions = new { networkOrigins = Array.Empty<string>(), storage = new[] { "read", "write" } } });
        Check(!failed.GetProperty("ok").GetBoolean());
        var after = (await Ok(f, "music.sources.detail", new { installationId = id })).GetProperty("item");
        Check(after.GetProperty("version").GetString() == "1" &&
            before.GetProperty("generation").GetInt64() == after.GetProperty("generation").GetInt64());
        Check((await Resolve(f, id)).EndsWith("/original/1.mp3", StringComparison.Ordinal));
        await Ok(f, "music.sources.disable", new { installationId = id });
        var status = await f.Runtime.CallAsync("music.script.status", new { });
        Check(status.GetProperty("instances").GetArrayLength() == 0);
    }
    private static async Task Restart()
    {
        string root = Data(), id;
        await using (var first = new Fixture(root))
        {
            id = await Install(first, "restart");
            await Ok(first, "music.sources.enable", new { installationId = id });
            Check((await Resolve(first, id)).EndsWith("/restart/1.mp3", StringComparison.Ordinal));
        }
        await using var next = new Fixture(root);
        await Ok(next, "music.sources.list");
        Check(!next.Runtime.HasStarted);
        Check((await Resolve(next, id)).EndsWith("/restart/2.mp3", StringComparison.Ordinal));
    }
    private static async Task Lyrics()
    {
        await using var f = new Fixture(Data()); string id = await Install(f, "lyrics");
        await Ok(f, "music.sources.enable", new { installationId = id });
        var result = await Ok(f, "music.online.lyrics", new { installationId = id, platform = "local",
            song = MusicSongFixture.Local("local-test", "Fixture local", "Fixture", duration: 120) });
        Check(result.GetProperty("state").GetString() == "ready");
        Check(result.GetProperty("lines")[0].GetProperty("time").GetDouble() == 1);
        Check(result.GetProperty("lines")[0].GetProperty("translation").GetString() == "原生时间轴");
    }
    private static async Task Recover()
    {
        await using var f = new Fixture(Data());
        string first = await Install(f, "recover"), second = await Install(f, "other");
        await Ok(f, "music.sources.enable", new { installationId = first });
        await Ok(f, "music.sources.enable", new { installationId = second });
        Check((await Resolve(f, first)).EndsWith("/recover/1.mp3", StringComparison.Ordinal));
        long generation = f.Runtime.Generation;
        var old = f.Child!;
        using (var child = Process.GetProcessById(old.ProcessId)) child.Kill();
        await old.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Runtime.CanRetry);
        var local = await f.Library.ExecuteAsync("music.library.create", new { name = "离线原生操作" });
        Check(local.GetProperty("ok").GetBoolean(), "native library must remain usable after process death");
        var failed = await f.Owner.ExecuteAsync("music.online.resolve", new { installationId = first, platform = "wy", quality = "320k",
            song = MusicSongFixture.Online("wy", "11", "Track") });
        Check(!failed.GetProperty("ok").GetBoolean() && ReferenceEquals(old, f.Child), "no implicit process or business retry");
        await f.Runtime.RetryAsync();
        Check(f.Runtime.Generation == generation + 1 && !ReferenceEquals(old, f.Child));
        var status = await f.Runtime.CallAsync("music.script.status", new { });
        Check(status.GetProperty("instances").GetArrayLength() == 0, "recovery does not activate all enabled scripts");
        Check((await Resolve(f, first)).EndsWith("/recover/2.mp3", StringComparison.Ordinal));
        status = await f.Runtime.CallAsync("music.script.status", new { });
        Check(status.GetProperty("instances").GetArrayLength() == 1 &&
            status.GetProperty("instances")[0].GetProperty("installationId").GetString() == first);
    }
}
