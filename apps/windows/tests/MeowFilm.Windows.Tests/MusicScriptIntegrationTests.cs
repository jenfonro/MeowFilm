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
        yield return new("Native LX real pipe: concurrent request contexts preserve site and installation storage across awaits", ConcurrentStorage);
        yield return new("Native LX real pipe: startup global and completed request contexts cannot borrow storage authority", ScopedStorage);
        yield return new("Native LX real pipe: response envelope preserves original raw value", RawValue);
    }
    private const string SourceTemplate = """
        const label = __LABEL__;
        lx.on(lx.EVENT_NAMES.request, async ({source,action,info}, {storage}) => {
          if(action==='lyric') return {lyric:'[00:01.00]native timing',tlyric:'[00:01.00]原生时间轴'};
          const count=(await storage.get('count')||0)+1;
          await storage.set('count',count);
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
        internal Func<string, JsonElement, CancellationToken, Task>? BeforeStorage;
        internal NodeRuntime? Child;
        internal Fixture(string path)
        {
            Store = new(path, ["music.library.v1"], [MusicLibraryOwner.ScriptContentCollection]);
            Library = new(Store);
            MusicScriptOwner? owner = null;
            Runtime = new(async token =>
            {
                string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
                    ?? throw new InvalidOperationException("Packaged runtime root required.");
                string node = Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE")
                    ?? throw new InvalidOperationException("Verified Node required.");
                RuntimeModule[] modules = [new("music", "0.6.0"), new("cat", "0.1.0")];
                var capabilities = owner!.ScriptCapabilities.ToDictionary(pair => pair.Key,
                    pair => new Func<JsonElement, CancellationToken, Task<object?>>(async (input, cancellationToken) =>
                    {
                        if (BeforeStorage is { } before) await before(pair.Key, input, cancellationToken);
                        return await pair.Value(input, cancellationToken);
                    }), StringComparer.Ordinal);
                return Child = await NodeRuntime.StartAsync(node, RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules),
                    modules, capabilities, Path.Combine(path, "script-runtime"), token);
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
        => await InstallSource(f, Source(label));
    private static async Task<string> InstallSource(Fixture f, string source)
    {
        string token = await Upload(f, source);
        var result = await Ok(f, "music.sources.install", new { token, name = "Same Name", version = "1",
            permissions = new { storage = new[] { "read", "write" } } });
        return result.GetProperty("item").GetProperty("installationId").GetString()!;
    }
    private static async Task<string> Resolve(Fixture f, string id)
    {
        var result = await Ok(f, "music.online.resolve", new { installationId = id, platform = "wy", quality = "320k",
            song = MusicSongFixture.Online("wy", "11", "Track", "Fixture", duration: 180) });
        Check(!result.GetProperty("playbackVerified").GetBoolean());
        return result.GetProperty("url").GetString()!;
    }
    private static async Task<JsonElement> InvokeRaw(Fixture f, string id, string site, string operation, object payload)
    {
        var status = await f.Runtime.CallAsync("music.script.status", new { });
        Check(status.GetProperty("contract").GetString() == "music.script.v2");
        var instance = status.GetProperty("instances").EnumerateArray().Single(item => item.GetProperty("installationId").GetString() == id);
        var reply = await f.Runtime.CallAsync("music.script.invoke", new
        {
            installationId = id, revision = instance.GetProperty("revision").GetInt64(),
            generation = instance.GetProperty("generation").GetInt64(), siteId = site, operation, payload
        });
        Check(reply.GetProperty("ok").GetBoolean(), "raw script invocation: " +
            (reply.TryGetProperty("code", out var code) ? code.GetString() : "missing"));
        Check(reply.GetProperty("contract").GetString() == "music.script.v2");
        return reply.GetProperty("value").Clone();
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
            permissions = new { storage = new[] { "read", "write" } } });
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
    private static async Task ConcurrentStorage()
    {
        const string source = """
            lx.on('request', async ({source,info}, {storage}) => {
              await Promise.resolve();
              await storage.set('same',info.value);
              await new Promise(resolve=>setTimeout(resolve,1));
              return {source,value:await storage.get('same'),nil:null,flags:[false,0,'qs']};
            });
            lx.send('inited',{sources:{
              wy:{type:'music',actions:['probe'],qualitys:[]},
              tx:{type:'music',actions:['probe'],qualitys:[]}
            }});
            """;
        await using var f = new Fixture(Data());
        string first = await InstallSource(f, source), second = await InstallSource(f, source);
        await Ok(f, "music.sources.enable", new { installationId = first });
        await Ok(f, "music.sources.enable", new { installationId = second });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(string Id, string Site, long Generation, string Value)>();
        f.BeforeStorage = async (method, input, token) =>
        {
            if (method != "platform.musicScript.storage.set") return;
            calls.Enqueue((input.GetProperty("installationId").GetString()!, input.GetProperty("siteId").GetString()!,
                input.GetProperty("generation").GetInt64(), input.GetProperty("value").GetString()!));
            if (calls.Count == 4) entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var targets = new[] { (first, "wy", "first-wy"), (first, "tx", "first-tx"), (second, "wy", "second-wy"), (second, "tx", "second-tx") };
        var requests = targets.Select(target => InvokeRaw(f, target.Item1, target.Item2, "probe", new { value = target.Item3 })).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(requests.All(request => !request.IsCompleted), "both instances and sites must be concurrently awaiting storage");
            release.TrySetResult();
            var values = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
            for (int index = 0; index < targets.Length; index++)
            {
                Check(values[index].GetProperty("source").GetString() == targets[index].Item2);
                Check(values[index].GetProperty("value").GetString() == targets[index].Item3);
                Check(values[index].GetProperty("nil").ValueKind == JsonValueKind.Null &&
                    !values[index].GetProperty("flags")[0].GetBoolean() && values[index].GetProperty("flags")[1].GetInt32() == 0);
            }
            Check(calls.Select(call => (call.Id, call.Site, call.Value)).ToHashSet().SetEquals(targets));
            Check(calls.All(call => call.Generation > 0));
            var metadata = await f.Library.ReadSourceMetadataAsync();
            foreach (var target in targets)
                Check((await f.Library.ReadScriptValueAsync(target.Item1, target.Item2, "same",
                    metadata.State.GetProperty("revision").GetInt64())).GetString() == target.Item3);
        }
        finally { release.TrySetResult(); }
    }
    private static async Task ScopedStorage()
    {
        const string source = """
            const startup=meow.storage.set('startup','must not persist').then(()=> 'allowed',e=>e.code);
            let retired;
            lx.on('request',async ({info},{storage})=>{
              const global=await meow.storage.set('global','must not persist').then(()=> 'allowed',e=>e.code);
              const old=retired ? await retired.set('retired','must not persist').then(()=> 'allowed',e=>e.code) : null;
              await Promise.resolve();
              await storage.set('active',info.value);
              retired=storage;
              return {startup:await startup,global,old,active:await storage.get('active')};
            });
            lx.send('inited',{sources:{wy:{type:'music',actions:['probe'],qualitys:[]}}});
            """;
        await using var f = new Fixture(Data()); string id = await InstallSource(f, source);
        await Ok(f, "music.sources.enable", new { installationId = id });
        var first = await InvokeRaw(f, id, "wy", "probe", new { value = "first" });
        var second = await InvokeRaw(f, id, "wy", "probe", new { value = "second" });
        Check(first.GetProperty("startup").GetString() == "PERMISSION_DENIED" && first.GetProperty("global").GetString() == "PERMISSION_DENIED");
        Check(second.GetProperty("old").GetString() == "PERMISSION_DENIED" && second.GetProperty("active").GetString() == "second");
        var metadata = await f.Library.ReadSourceMetadataAsync();
        long revision = metadata.State.GetProperty("revision").GetInt64();
        foreach (string key in new[] { "startup", "global", "retired" })
            Check((await f.Library.ReadScriptValueAsync(id, "wy", key, revision)).ValueKind == JsonValueKind.Null);
    }
    private static async Task RawValue()
    {
        await using var f = new Fixture(Data()); string id = await Install(f, "raw");
        await Ok(f, "music.sources.enable", new { installationId = id });
        var value = await InvokeRaw(f, id, "wy", "musicUrl", new { quality = "320k", musicInfo = new { } });
        Check(value.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["url", "headers"]));
        Check(value.GetProperty("url").GetString() == "https://media.invalid/raw/1.mp3");
        Check(value.GetProperty("headers").GetProperty("Accept").GetString() == "audio/*");
        const string extension = """
            lx.on('request',async ({action,info})=>{
              if(action!==info.expectedAction||info.type!=='qs'||info.quality!=='qsvip'||info.query!=='fixture')
                throw new Error('extension payload changed');
              return info.value;
            });
            lx.send('inited',{sources:{wy:{
              type:'music',actions:['search','musicSearch','pic'],qualitys:['qs','qsvip']
            }}});
            """;
        string extensionId = await InstallSource(f, extension);
        await Ok(f, "music.sources.enable", new { installationId = extensionId });
        foreach (string operation in new[] { "search", "musicSearch", "pic" })
            foreach (object? expected in new object?[] { null, false, 7, "unchanged", new object?[] { null, "qs", false }, new { found = true, rows = new[] { 1, 2 } } })
            {
                var raw = await InvokeRaw(f, extensionId, "wy", operation, new { value = expected, expectedAction = operation, type = "qs", quality = "qsvip", query = "fixture" });
                Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(raw.GetRawText()),
                    JsonSerializer.SerializeToNode(expected)), "raw extension value must retain its original JSON shape");
            }
    }
}
