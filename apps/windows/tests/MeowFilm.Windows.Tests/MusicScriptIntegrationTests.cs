using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>The native adapter over the real packaged runtime: shared management, module-owned
/// persistence and private storage, native lyric parsing and explicit process recovery.</summary>
public static class MusicScriptIntegrationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native LX real HTTP: two installations resolve directed and keep isolated module storage", TwoInstances);
        yield return new("Native LX real HTTP: a failed update trial keeps the live generation and stored version", Update);
        yield return new("Native LX real HTTP: a restart reads the module catalog and resumes its private data", Restart);
        yield return new("Native LX real HTTP: original local lyric protocol is parsed in native code", Lyrics);
        yield return new("Native LX real HTTP: explicit crashed-process recovery reloads only the requested source", Recover);
        yield return new("Native LX real HTTP: owner transports legal large recordings and source lists", LargeOwner);
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
          wy:{name:'same-name',type:'music',actions:['musicUrl','lyric'],qualitys:['320k']},
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
        internal readonly string Path;
        internal readonly LocalDocumentStore Store;
        internal readonly MusicLibraryOwner Library;
        internal readonly AppRuntimeClient Runtime;
        internal readonly MusicScriptOwner Owner;
        internal NodeRuntime? Child;
        internal Fixture(string path)
        {
            Path = path;
            Store = new(path, ["music.library.v1"], []);
            Library = new(Store);
            Runtime = new(async token =>
            {
                string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
                    ?? throw new InvalidOperationException("Packaged runtime root required.");
                string node = Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE")
                    ?? throw new InvalidOperationException("Verified Node required.");
                RuntimeModule[] modules = [new("music", "0.8.0"), new("cat", "0.2.0"), new("source", "0.2.0")];
                return Child = await NodeRuntime.StartAsync(node,
                    RuntimeBundle.Verify(System.IO.Path.Combine(bundles, "aggregate"), "aggregate", modules),
                    modules,
                    System.IO.Path.Combine(path, "script-runtime"), token);
            });
            Owner = new(Library, Runtime);
            Runtime.Start();
        }
        internal string ModuleData => System.IO.Path.Combine(Path, "script-runtime", "music");
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
    private static async Task<string> InstallSource(Fixture f, string source, bool enable = true)
    {
        var result = await f.Owner.ImportSourceAsync(Encoding.UTF8.GetBytes(source), "fixture.js");
        Check(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        string id = result.GetProperty("item").GetProperty("installationId").GetString()!;
        if (enable) await Ok(f, "music.sources.enable", new { installationId = id });
        return id;
    }
    private static async Task<string> Resolve(Fixture f, string id)
    {
        var result = await Ok(f, "music.online.resolve", new { installationId = id, platform = "wy", quality = "320k",
            song = MusicSongFixture.Online("wy", "11", "Track", "Fixture", duration: 180) });
        Check(!result.GetProperty("playbackVerified").GetBoolean() && !result.GetProperty("qualityVerified").GetBoolean());
        return result.GetProperty("url").GetString()!;
    }
    private static JsonNode StoredData(Fixture f, string id) =>
        JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(f.ModuleData, "data", id + ".json")))!;
    private static async Task TwoInstances()
    {
        await using var f = new Fixture(Data());
        string first = await InstallSource(f, Source("first")), second = await InstallSource(f, Source("second"));
        Check((await Resolve(f, first)).EndsWith("/first/1.mp3", StringComparison.Ordinal));
        Check((await Resolve(f, second)).EndsWith("/second/1.mp3", StringComparison.Ordinal));
        Check((await Resolve(f, first)).EndsWith("/first/2.mp3", StringComparison.Ordinal));
        Check(StoredData(f, second)["sites"]!["wy"]![0]!["value"]!.GetValue<int>() == 1, "module-owned private storage");
        Check(StoredData(f, first)["sites"]!["wy"]![0]!["value"]!.GetValue<int>() == 2);
        var library = await f.Library.ExecuteAsync("music.library.list");
        Check(library.GetProperty("ok").GetBoolean() && !File.ReadAllText(System.IO.Path.Combine(f.ModuleData, "catalog.json"))
            .Contains("media.invalid", StringComparison.Ordinal), "resolved media URLs are never persisted");
        var unavailable = false;
        try { await f.Runtime.CallAsync("music.library.list", new { }); }
        catch (BridgeException e) when (e.Code == "METHOD_UNAVAILABLE") { unavailable = true; }
        Check(unavailable);
    }
    private static async Task Update()
    {
        await using var f = new Fixture(Data()); string id = await InstallSource(f, Source("original"));
        var before = (await Ok(f, "music.sources.detail", new { installationId = id })).GetProperty("item");
        var failed = await f.Owner.ImportSourceAsync(Encoding.UTF8.GetBytes("throw new Error('synthetic failure')"),
            "fixture.js", id);
        Check(!failed.GetProperty("ok").GetBoolean());
        var after = (await Ok(f, "music.sources.detail", new { installationId = id })).GetProperty("item");
        Check(after.GetProperty("contentHash").GetString() == before.GetProperty("contentHash").GetString() &&
            before.GetProperty("generation").GetInt64() == after.GetProperty("generation").GetInt64() &&
            !after.GetProperty("rollbackAvailable").GetBoolean());
        Check((await Resolve(f, id)).EndsWith("/original/1.mp3", StringComparison.Ordinal));
        await Ok(f, "music.sources.disable", new { installationId = id });
        var listed = await Ok(f, "music.sources.list");
        Check(listed.GetProperty("items")[0].GetProperty("state").GetString() == "disabled");
    }
    private static async Task Restart()
    {
        string root = Data(), id;
        await using (var first = new Fixture(root))
        {
            id = await InstallSource(first, Source("restart"));
            Check((await Resolve(first, id)).EndsWith("/restart/1.mp3", StringComparison.Ordinal));
        }
        await using var next = new Fixture(root);
        var listed = await Ok(next, "music.sources.list");
        var item = listed.GetProperty("items")[0];
        Check(item.GetProperty("installationId").GetString() == id && item.GetProperty("enabled").GetBoolean() &&
            item.GetProperty("state").GetString() == "idle", "a restart restores facts, never a ready instance");
        Check((await Resolve(next, id)).EndsWith("/restart/2.mp3", StringComparison.Ordinal));
    }
    private static async Task Lyrics()
    {
        await using var f = new Fixture(Data()); string id = await InstallSource(f, Source("lyrics"));
        var result = await Ok(f, "music.online.lyrics", new { installationId = id, platform = "local",
            song = MusicSongFixture.Local("local-test", "Fixture local", "Fixture", duration: 120) });
        Check(result.GetProperty("state").GetString() == "ready");
        Check(result.GetProperty("lines")[0].GetProperty("time").GetDouble() == 1);
        Check(result.GetProperty("lines")[0].GetProperty("translation").GetString() == "原生时间轴");
    }
    private static async Task Recover()
    {
        await using var f = new Fixture(Data());
        string first = await InstallSource(f, Source("recover")), second = await InstallSource(f, Source("other"));
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
        Check(!failed.GetProperty("ok").GetBoolean() &&
            failed.GetProperty("code").GetString() == "SCRIPT_RUNTIME_UNAVAILABLE" && ReferenceEquals(old, f.Child),
            "no implicit process or business retry");
        await f.Runtime.RetryAsync();
        Check(f.Runtime.Generation == generation + 1 && !ReferenceEquals(old, f.Child));
        var listed = await Ok(f, "music.sources.list");
        Check(listed.GetProperty("items").EnumerateArray().All(item => item.GetProperty("state").GetString() == "idle"),
            "recovery does not load every enabled source");
        Check((await Resolve(f, first)).EndsWith("/recover/2.mp3", StringComparison.Ordinal), "private data survives the process");
        listed = await Ok(f, "music.sources.list");
        Check(listed.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("state").GetString() == "ready") == 1);
        _ = second;
    }
    private static async Task LargeOwner()
    {
        const string source = """
            const fields=['mid','id','songId','qqSongId','songType','type','copyrightId','strMediaMid',
              'hash','albumId','album_id','albumMid','albumAudioId','mixsongid','fileid','fileId','size'];
            lx.on('request',async ({info})=>{
              for(const field of fields)
                if(info.musicInfo[field] !== 'x'.repeat(4096)) throw Error('recording changed');
              return {url:'https://media.invalid/large.mp3',headers:{'X-Checked':String(fields.length)}};
            });
            lx.send('inited',{sources:{wy:{type:'music',actions:['musicUrl'],qualitys:['320k']}}});
            """;
        await using var f = new Fixture(Data());
        string id = await InstallSource(f, source);
        var native = new JsonObject();
        foreach (string field in new[] { "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId", "strMediaMid",
            "hash", "albumId", "album_id", "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId", "size" })
            native[field] = new string('x', 4096);
        var song = MusicSongFixture.Online("wy", "selected", "Track", "Fixture", duration: 180, native: native);
        var recordings = song["sources"]!.AsArray();
        // Distinct same-recording locators are legal; only wy is selected while the
        // whole native request retains all recordings.
        for (int index = 0; index < 125; index++)
            recordings.Add(MusicSongFixture.Online("tx", "other-" + index, "Track", "Fixture",
                duration: 180, native: native)["sources"]![0]!.DeepClone());
        Check(BridgeCodec.EncodeJSONValue(song).Length > 8 * 1024 * 1024);
        var resolved = await Ok(f, "music.online.resolve", new { installationId = id, platform = "wy", quality = "320k", song });
        Check(resolved.GetProperty("url").GetString() == "https://media.invalid/large.mp3" &&
            resolved.GetProperty("headers").GetProperty("X-Checked").GetString() == "17");
        // Long legal header metadata produces a complete response larger than 64 KiB.
        string header = "/**\n * @name " + new string('名', 255) + "\n * @description " + new string('述', 1024) +
            "\n * @author " + new string('作', 1024) + "\n */\n";
        for (int index = 0; index < 10; index++) await InstallSource(f, header + "// " + index, enable: false);
        var listed = await Ok(f, "music.sources.list");
        Check(BridgeCodec.EncodeJSONValue(listed).Length > 65536 && listed.GetProperty("items").GetArrayLength() == 11,
            $"complete list size: {BridgeCodec.EncodeJSONValue(listed).Length} bytes");
        Check(listed.GetProperty("items")[1].GetProperty("description").GetString() == new string('述', 1024));
    }
}
