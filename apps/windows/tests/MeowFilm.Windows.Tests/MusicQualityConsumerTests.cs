using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

public static class MusicQualityConsumerTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native settings persist each quality change immediately and reject all-off policy", Settings);
        yield return Case("native module immediate auto policy reaches exact LX request", Playback);
        yield return Case("native settings resolve unknown CAS internally without a reload action", Unknown);
        yield return Case("native admitted immediate save physically drains before close", Close);
        yield return Case("authored original LX request receives master and 192k without official spatial mapping", RealLx);
        yield return Case("long lived source choices and refresh send current repository material to authored LX", CurrentRecording);
    }
    private static TestCase Case(string name, Func<Task> run) => new("quality consumer " + name, () => Sta(run));
    private static void Check(bool value, string message = "quality consumer assertion") => QualityFixture.Check(value, message);
    private static async Task Settings()
    {
        await using var f = new Scene();
        await f.Module.InitializeAsync(default); await f.Module.ActivateQualitySettingsAsync();
        await Editable(f.Module);
        Check(f.Module.CanEditQuality && f.Module.QualityCategories.Count == 7 && f.Module.SpatialQualities.Count == 7);
        Check(f.Module.SpatialQualities.Single(row => row.Key == "sony360").Label == "360全景音频");
        Check(f.Module.SpatialChoices().All(choice =>
            choice.Label == f.Module.SpatialQualities.Single(row => row.Key == choice.Key).Label),
            "spatial submenu uses the same full names as the settings list");
        string first = f.Module.QualityCategories[0].Key;
        await f.Module.MoveQualityAsync(f.Module.QualityCategories[0], f.Module.QualityCategories[1], false);
        await Editable(f.Module);
        Check(f.Module.QualityCategories[1].Key == first);
        string spatial = f.Module.SpatialQualities[0].Key;
        await f.Module.SetQualityEnabledAsync(f.Module.SpatialQualities[0], false, true);
        await Editable(f.Module);
        Check(!f.Module.SpatialQualities.Single(row => row.Key == spatial).Enabled);
        await f.Module.SetPlaybackQualityRequestAsync("auto"); await Editable(f.Module);
        Check(f.Module.PlaybackQualityRequest == "auto" && f.Module.QualityStatus.Length == 0);
        var reply = await f.Fixture.Ok("music.quality.settings.get");
        Check(reply.GetProperty("settings").GetProperty("request").GetString() == "auto" &&
            reply.GetProperty("settings").GetProperty("qualityOrder")[1].GetString() == first);
        foreach (string key in f.Module.QualityCategories.Where(row => row.Enabled).Select(row => row.Key).ToArray())
        {
            await f.Module.SetQualityEnabledAsync(f.Module.QualityCategories.Single(row => row.Key == key), false, false);
            await Editable(f.Module);
        }
        Check(f.Module.QualityStatus == "请至少保留一种可用音质");
        Check(f.Module.QualityCategories.Count(row => row.Enabled) == 1);
    }
    private static async Task Playback()
    {
        await using var f = new Scene(); await f.Module.InitializeAsync(default);
        await f.Configure();
        await f.Module.ActivateQualitySettingsAsync(); await Editable(f.Module);
        await f.Module.SetPlaybackQualityRequestAsync("auto"); await Editable(f.Module);
        await f.Play("11");
        Check(f.Output.Opens == 1 && f.Fixture.Runtime.Qualities.SequenceEqual(["master"]));
        Check(f.Module.PlaybackQualityRequest == "auto");
        var before = await f.Fixture.Ok("music.quality.settings.get");
        await f.Fixture.Ok("music.settings.save", new {
            ifRevision = before.GetProperty("onlineSettings").GetProperty("revision").GetInt64(), lyrics = true });
        await f.Module.RefreshSourceManagementAsync(); await Editable(f.Module);
        var after = await f.Fixture.Ok("music.quality.settings.get");
        Check(after.GetProperty("settings").GetRawText() == before.GetProperty("settings").GetRawText() &&
            after.GetProperty("signature").GetString() == before.GetProperty("signature").GetString(),
            "saving lyrics leaves the complete audio policy and signature unchanged");
        Check(f.Module.LyricsEnabled && f.Module.PlaybackQualityRequest == "auto");
        Check(f.Output.Opens == 1 && f.Output.HasMedia && f.Output.Revocations == 0 &&
            f.Fixture.Runtime.Qualities.SequenceEqual(["master"]),
            "saving lyrics neither revokes open audio nor resolves or reopens it");
        await f.Module.SetPlaybackQualityRequestAsync("128k"); await Editable(f.Module);
        Check(f.Module.PlaybackQualityRequest == "128k");
        Check(f.Output.Opens == 1 && f.Output.HasMedia && f.Output.Revocations == 0,
            "policy changes retain the already open media while retiring only future recovery");
    }
    private static async Task Unknown()
    {
        await using var f = new Scene(); await f.Module.InitializeAsync(default); await f.Module.ActivateQualitySettingsAsync();
        await Editable(f.Module);
        MusicTestStores.For(f.Fixture.Store).Fault = true; MusicTestStores.For(f.Fixture.Store).FaultAfter = true;
        await f.Module.SetPlaybackQualityRequestAsync("master"); await Editable(f.Module);
        Check(f.Module.CanEditQuality && f.Module.PlaybackQualityRequest == "master" &&
            f.Module.QualityStatus.Length == 0);
        int writes = MusicTestStores.For(f.Fixture.Store).Writes;
        await f.Module.SetPlaybackQualityRequestAsync("master");
        Check(MusicTestStores.For(f.Fixture.Store).Writes == writes);
        MusicTestStores.For(f.Fixture.Store).Fault = true; MusicTestStores.For(f.Fixture.Store).FaultAfter = false;
        await f.Module.SetPlaybackQualityRequestAsync("128k"); await Editable(f.Module);
        Check(f.Module.PlaybackQualityRequest == "master" &&
            f.Module.QualityStatus == "音质设置保存失败" &&
            MusicTestStores.For(f.Fixture.Store).Writes == writes + 1);
    }
    private static async Task Close()
    {
        await using var f = new Scene(); await f.Module.InitializeAsync(default); await f.Module.ActivateQualitySettingsAsync();
        await Editable(f.Module);
        string before = f.Module.PlaybackQualityRequest;
        int writes = MusicTestStores.For(f.Fixture.Store).Writes;
        MusicTestStores.For(f.Fixture.Store).HoldCommit();
        var save = f.Module.SetPlaybackQualityRequestAsync("auto");
        await MusicTestStores.For(f.Fixture.Store).Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = f.Module.PrepareShutdownAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!save.IsCompleted && !close.IsCompleted, "physical durable write must still own input");
        MusicTestStores.For(f.Fixture.Store).Release?.TrySetResult(); await Task.WhenAll(save, close);
        Check(f.Module.QualityStatus.Length == 0 && f.Module.PlaybackQualityRequest == before &&
            MusicTestStores.For(f.Fixture.Store).Writes == writes + 1,
            "the admitted save commits while its retired page keeps its prior projection");
        await f.Module.CancelShutdownPreparationAsync();
        await f.Module.ActivateQualitySettingsAsync();
        Check(f.Module.PlaybackQualityRequest == "auto" && MusicTestStores.For(f.Fixture.Store).Writes == writes + 1,
            "a fresh read adopts the committed policy without replaying the save");
    }
    private static async Task RealLx()
    {
        var store = new QualityFixture.DocumentStore();
        await using var runtime = new NativeMusicScriptTestRuntime();
        await using var app = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
        async Task<JsonElement> Call(string method, object? p = null)
        {
            var r = await app.ExecuteAsync(method, p); Check(r.GetProperty("ok").GetBoolean(), method + r.GetRawText()); return r;
        }
        const string source = """
            lx.on(lx.EVENT_NAMES.request,async ({info})=>{
              if(!info.musicInfo.types.some(t=>t.type===info.type)) throw new Error('type mismatch');
              return 'https://media.invalid/'+info.musicInfo.songmid+'/'+info.type;
            });
            lx.send(lx.EVENT_NAMES.inited,{status:true,sources:{
              wy:{name:'quality fixture',type:'music',actions:['musicUrl'],qualitys:['128k','192k','320k','flac','flac24bit','master']}
            }});
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        var installed = await app.ImportSourceAsync(bytes, "quality-lx.js");
        Check(installed.GetProperty("ok").GetBoolean(), installed.GetRawText());
        string id = installed.GetProperty("item").GetProperty("installationId").GetString()!;
        await Call("music.sources.enable", new { installationId = id });
        foreach (string quality in new[] { "master", "192k" })
        {
            var r = await Call("music.online.resolve", new { installationId = id, platform = "wy", quality, song = QualityFixture.Song() });
            Check(r.GetProperty("url").GetString() == "https://media.invalid/11/" + quality);
        }
        Check(runtime.Calls.All(m => m.StartsWith("music.script.", StringComparison.Ordinal)));
    }
    private static async Task Editable(MusicCapability module)
    {
        // The real Native app can enqueue its initial LibrarySync status event
        // after Initialize completes. A user cannot click the disabled Save
        // control during that command. Wait for that real lane, not a sleep.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (module.CanEditQuality) return;
            await Task.Delay(1, timeout.Token);
        } while (true);
    }
    private static async Task CurrentRecording()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var runtime = new NativeMusicScriptTestRuntime();
        await using var app = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
        var output = new SwitchingOutput();
        await using var module = new MusicCapability(app, store, output, new Dialogs());
        async Task<JsonElement> Call(string method, object? p = null)
        {
            var result = await app.ExecuteAsync(method, p);
            Check(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText());
            return result;
        }
        JsonObject Track(string title, double duration, long size, params string[] types) =>
            MusicSongFixture.Online("wy", "current-recording", title, "Current artist", duration: duration,
                native: new JsonObject { ["size"] = size,
                    ["types"] = new JsonArray(types.Select(type => (JsonNode)new JsonObject { ["type"] = type }).ToArray()) });
        var observation = Track("Original recording", 120, 1024, "320k");
        runtime.Platform.OnRequest = (call, _) =>
        {
            Check(call.Platform == "wy", "only the selected WY recording may read the platform");
            if (call.Operation == "chorus")
            {
                Check(call.Parameters.GetProperty("id").GetString() == "current-recording",
                    "initial chorus belongs to the selected recording");
                return Task.FromResult(JsonSerializer.SerializeToElement(new { platform = "wy", id = "current-recording",
                    point = new { start = 30, end = 45 } }));
            }
            Check(call.Operation == "search" &&
                call.Parameters.GetProperty("keyword").GetString() is "original" or "corrected" or "refreshed",
                "only explicit catalog observations and the selected recording's chorus may read the platform");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { items = new[] { observation }, more = false }));
        };
        var original = (await Call("music.catalog.search", new { platform = "wy", keyword = "original", limit = 1 })).GetProperty("items")[0];
        string songId = original.GetProperty("songId").GetString()!;
        string key = original.GetProperty("sources")[0].GetProperty("sourceKey").GetString()!;
        const string source = """
            lx.on(lx.EVENT_NAMES.request,async ({info})=>{
              if(!info.musicInfo.types.some(t=>t.type===info.type)) throw new Error('type mismatch');
              return {url:'https://media.invalid/'+info.musicInfo.songmid+'/'+info.type,expiresAt:Date.now()+29000};
            });
            lx.send(lx.EVENT_NAMES.inited,{status:true,sources:{
              wy:{name:'current recording',type:'music',actions:['musicUrl'],qualitys:['320k','flac']}
            }});
            """;
        var installed = await app.ImportSourceAsync(Encoding.UTF8.GetBytes(source), "current-recording.js");
        Check(installed.GetProperty("ok").GetBoolean(), installed.GetRawText());
        string installation = installed.GetProperty("item").GetProperty("installationId").GetString()!;
        await Call("music.sources.enable", new { installationId = installation });
        await Call("music.officialSources.settings.save", new { ifRevision = 0, enabled = Array.Empty<string>() });
        await Call("music.settings.save", new { ifRevision = 0, lyrics = false });
        await Call("music.queue.replace", new { songs = new[] { original, original }, index = 0 });
        await module.InitializeAsync(default);
        await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
        await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.First());
        Check(output.HasMedia && output.Opens == 1 && module.CanSwitchSource, "initial 320k playback:" + module.Status);
        var chorusReady = QualityFixture.Gate();
        void ChorusReady(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MusicCapability.HasChorus) && module.HasChorus) chorusReady.TrySetResult();
        }
        module.PropertyChanged += ChorusReady;
        try
        {
            output.Start();
            if (module.HasChorus) chorusReady.TrySetResult();
            await chorusReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { module.PropertyChanged -= ChorusReady; }
        Check(module.HasChorus && module.ChorusPositionSeconds == 30,
            "the initial native playback must adopt its real official chorus before measuring metadata corrections");
        await output.SeekAsync(29);
        var initialQueue = await Call("music.queue.get");
        var entries = initialQueue.GetProperty("entries");
        Check(entries.GetArrayLength() == 2 && entries[0].GetProperty("id").GetString() != entries[1].GetProperty("id").GetString() &&
            entries.EnumerateArray().All(entry => entry.GetProperty("song").GetProperty("songId").GetString() == songId));
        Check((await Call("music.history.get", new { kind = "days" })).GetProperty("items").GetArrayLength() == 1,
            "the initial real Started event must already own the one playback record");
        async Task<string> Control()
        {
            var queue = JsonNode.Parse((await Call("music.queue.get")).GetRawText())!.AsObject();
            foreach (var entry in queue["entries"]!.AsArray().OfType<JsonObject>()
                .Append(queue["current"]!.AsObject()))
                entry["song"] = new JsonObject { ["songId"] = entry["song"]!["songId"]!.DeepClone() };
            var history = MusicTestStores.For(store).State("history");
            return queue.ToJsonString() + history.ToJsonString();
        }
        string control = await Control();
        var initialPlatformRequests = runtime.Platform.Requests.ToArray();
        Check(initialPlatformRequests.Select(request => request.Platform + "/" + request.Operation)
            .SequenceEqual(["wy/search", "wy/chorus"]),
            "initial playback may only read its explicit catalog observation and selected recording's chorus");
        module.OpenSourceChoices();
        foreach (var _ in MusicQualityPolicy.SourceTiers) await module.LoadMoreSourcesAsync();
        Check(!module.CanLoadMoreSources, "the old recording's browser has exhausted every declared group");
        observation = Track("Corrected recording", 124, 4096, "320k", "flac");
        var corrected = (await Call("music.catalog.search", new { platform = "wy", keyword = "corrected", limit = 1 })).GetProperty("items")[0];
        Check(corrected.GetProperty("songId").GetString() == songId && corrected.GetProperty("baselineDuration").GetDouble() == 120 &&
            module.CanLoadMoreSources, "new recording types become available to the same long lived session");
        int resolves = runtime.Requests.Count(request => request.Method == "music.script.resolve");
        await module.SwitchQualityAsync("lossless");
        var dispatched = runtime.Requests.Where(request => request.Method == "music.script.resolve").ToArray();
        Check(dispatched.Length == resolves + 1 && output.Commits == 1 && output.PositionSeconds == 29,
            "a newly available flac quality hands off once without moving playback:" + module.Status);
        void Material(JsonElement parameters, string name, double duration, long size)
        {
            var info = parameters.GetProperty("musicInfo");
            Check(parameters.GetProperty("installationId").GetString() == installation &&
                parameters.GetProperty("platform").GetString() == "wy" && parameters.GetProperty("quality").GetString() == "flac" &&
                info.GetProperty("songmid").GetString() == "current-recording" &&
                info.GetProperty("name").GetString() == name && info.GetProperty("duration").GetDouble() == duration &&
                info.GetProperty("size").GetInt64() == size &&
                info.GetProperty("types").EnumerateArray().Select(type => type.GetProperty("type").GetString()).SequenceEqual(["320k", "flac"]),
                "the actual authored script request must use the current selected recording");
        }
        Material(dispatched.Last().Parameters, "Corrected recording", 124, 4096);
        module.OpenSourceChoices();
        var choice = module.SourceChoices.Single(row => row.Level == "flac" && row.Selectable);
        observation = Track("Refreshed recording", 123, 8192, "320k", "flac");
        var refreshed = (await Call("music.catalog.search", new { platform = "wy", keyword = "refreshed", limit = 1 })).GetProperty("items")[0];
        resolves = dispatched.Length;
        await module.SelectSourceChoiceAsync(choice);
        dispatched = runtime.Requests.Where(request => request.Method == "music.script.resolve").ToArray();
        Check(dispatched.Length == resolves + 1, "refresh asks only the selected installation and quality once");
        Material(dispatched.Last().Parameters, "Refreshed recording", 123, 8192);
        Check(module.Status == module.OperationErrorText(new MusicOperationException("QUALITY_CACHE_CHANGED")) &&
            output.Commits == 1 && output.HasMedia && output.PositionSeconds == 29,
            "changed recording material must not relax the accepted cache signature or replace the current output");
        bool sameSongId = refreshed.GetProperty("songId").GetString() == songId;
        bool sameBasis = refreshed.GetProperty("baselineDuration").GetDouble() == 120;
        bool sameSourceKey = refreshed.GetProperty("sources")[0].GetProperty("sourceKey").GetString() == key;
        bool sameControl = await Control() == control;
        var platformRequests = runtime.Platform.Requests.ToArray();
        var routes = platformRequests.Select(request => request.Platform + "/" + request.Operation).ToArray();
        var searches = platformRequests.Where(request => request.Operation == "search")
            .Select(request => request.Parameters.GetProperty("keyword").GetString()).ToArray();
        string unchanged = $"songId={sameSongId}; baseline={sameBasis}; sourceKey={sameSourceKey}; " +
            $"control={sameControl}; platformRequests={platformRequests.Length}; initialRequests={initialPlatformRequests.Length}; " +
            $"routes={string.Join(",", routes)}";
        Check(sameSongId, "metadata refresh must retain its permanent songId; " + unchanged);
        Check(sameBasis, "metadata refresh must retain its first valid 120-second basis; " + unchanged);
        Check(sameSourceKey, "metadata refresh must retain its selected sourceKey; " + unchanged);
        Check(sameControl, "queue and complete persisted history must remain unchanged; " + unchanged);
        Check(searches.SequenceEqual(["original", "corrected", "refreshed"]),
            "all catalog reads must be the three explicit metadata observations, without any rediscovery; " + unchanged);
        Check(routes.Skip(initialPlatformRequests.Length).SequenceEqual(["wy/search", "wy/search"]),
            "after initial playback settles, metadata corrections and source selection must add only the two explicit catalog reads; " + unchanged);
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly QualityFixture Fixture = new();
        internal readonly PlaybackOutput Output = new();
        internal readonly MusicCapability Module;
        internal Scene() => Module = new(Fixture.App, Fixture.Store, Output, new Dialogs());
        internal async Task Configure()
        {
            string id = await Fixture.Install();
            var source = (await Fixture.Ok("music.sources.list")).GetProperty("items").EnumerateArray().Single();
            Check(source.GetProperty("installationId").GetString() == id && source.GetProperty("enabled").GetBoolean(),
                "enabled script participates in native quality selection");
            await Fixture.Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
        }
        internal async Task Play(string id)
        {
            await Module.PlayOnlineSongAsync("wy", "合成歌曲", "合成艺人", "",
                MusicSongFixture.Info(QualityFixture.Song(id)));
        }
        public async ValueTask DisposeAsync()
        {
            MusicTestStores.For(Fixture.Store).Release?.TrySetResult(); Fixture.Runtime.Release.TrySetResult();
            await Module.DisposeAsync(); await Fixture.DisposeAsync();
        }
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private class PlaybackOutput : IMusicPlayback
    {
        private CancellationTokenRegistration access;
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => false;
        public double PositionSeconds { get; private set; }
        public double DurationSeconds => 120;
        public int Volume { get; set; } = 50;
        internal int Opens, Revocations;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started;
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OwnerId = owner; Identity = identity; Opens++;
            Adopt(token);
            return Task.CompletedTask;
        }
        protected void Adopt(CancellationToken token)
        {
            access.Dispose(); HasMedia = true;
            access = token.Register(() => { HasMedia = false; Revocations++; });
        }
        internal void Start()
        {
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            Started?.Invoke(this, EventArgs.Empty);
        }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { access.Dispose(); HasMedia = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) { PositionSeconds = seconds; return Task.CompletedTask; }
    }
    private sealed class SwitchingOutput : PlaybackOutput, INativePlaybackHandoff
    {
        private CancellationToken? prepared;
        internal int Commits;
        public double PreparedHandoffDurationSeconds => 120;
        public Task StopOutputForHandoffAsync() => StopAsync();
        public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token)
        { token.ThrowIfCancellationRequested(); prepared = token; return Task.CompletedTask; }
        public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
            throw new InvalidOperationException("This current-recording case has no media file cache");
        public Task CommitHandoffAsync(Action committed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            (prepared ?? throw new InvalidOperationException("No prepared input")).ThrowIfCancellationRequested();
            Adopt(prepared.Value); prepared = null; Commits++; committed();
            return Task.CompletedTask;
        }
        public Task DiscardHandoffAsync() { prepared = null; return Task.CompletedTask; }
    }
    internal static async Task Sta(Func<Task> body)
    { await TestUiHost.InvokeAsync(body); }
}
