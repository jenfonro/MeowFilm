using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;

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
    }
    private static TestCase Case(string name, Func<Task> run) => new("quality consumer " + name, () => Sta(run));
    private static void Check(bool value, string message = "quality consumer assertion") => QualityFixture.Check(value, message);
    private static async Task Settings()
    {
        await using var f = new Scene();
        await f.Module.InitializeAsync(default); await f.Module.ActivateQualitySettingsAsync();
        await Editable(f.Module);
        Check(f.Module.CanEditQuality && f.Module.QualityCategories.Count == 7 && f.Module.SpatialQualities.Count == 7);
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
        f.Fixture.Store.FaultNamespace = "music.library.v1"; f.Fixture.Store.Fault = true; f.Fixture.Store.FaultAfter = true;
        await f.Module.SetPlaybackQualityRequestAsync("master"); await Editable(f.Module);
        Check(f.Module.CanEditQuality && f.Module.PlaybackQualityRequest == "master" &&
            f.Module.QualityStatus.Length == 0);
        int writes = f.Fixture.Store.Writes;
        await f.Module.SetPlaybackQualityRequestAsync("master");
        Check(f.Fixture.Store.Writes == writes);
        f.Fixture.Store.Fault = true; f.Fixture.Store.FaultAfter = false;
        await f.Module.SetPlaybackQualityRequestAsync("128k"); await Editable(f.Module);
        Check(f.Module.PlaybackQualityRequest == "master" &&
            f.Module.QualityStatus == "音质设置保存失败" &&
            f.Fixture.Store.Writes == writes + 1);
    }
    private static async Task Close()
    {
        await using var f = new Scene(); await f.Module.InitializeAsync(default); await f.Module.ActivateQualitySettingsAsync();
        await Editable(f.Module);
        f.Fixture.Store.HoldNamespace = "music.library.v1";
        var save = f.Module.SetPlaybackQualityRequestAsync("auto");
        await f.Fixture.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = f.Module.PrepareShutdownAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!save.IsCompleted && !close.IsCompleted, "physical durable write must still own input");
        f.Fixture.Store.Release.TrySetResult(); await Task.WhenAll(save, close);
        Check(f.Module.QualityStatus.Length == 0 && f.Module.PlaybackQualityRequest == "auto",
            "known committed receipt remains authoritative");
        await f.Module.CancelShutdownPreparationAsync();
        Check(f.Module.PlaybackQualityRequest == "auto");
    }
    private static async Task RealLx()
    {
        var store = new QualityFixture.DocumentStore();
        await using var runtime = new NativeMusicScriptTestRuntime();
        await using var app = new MusicNativeApplication(store, runtime, new QualityFixture.RejectHttp());
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
        string token = (await Call("music.transfer.begin", new { kind = "script", basename = "quality-lx.js",
            length = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) })).GetProperty("token").GetString()!;
        await Call("music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bytes) });
        string id = (await Call("music.sources.add", new { token }))
            .GetProperty("item").GetProperty("installationId").GetString()!;
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
            Fixture.Store.Release.TrySetResult(); Fixture.Runtime.Release.TrySetResult();
            await Module.DisposeAsync(); await Fixture.DisposeAsync();
        }
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class PlaybackOutput : IMusicPlayback
    {
        private CancellationTokenRegistration access;
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 120;
        public int Volume { get; set; } = 50;
        internal int Opens, Revocations;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            access.Dispose();
            OwnerId = owner; Identity = identity; HasMedia = true; Opens++;
            access = token.Register(() => { HasMedia = false; Revocations++; });
            return Task.CompletedTask;
        }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { access.Dispose(); HasMedia = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
    internal static async Task Sta(Func<Task> body)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = QualityFixture.Gate();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? error = null;
                try { await body(); }
                catch (Exception e) { error = e; }
                finally { done.TrySetResult(error); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "quality-consumer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var failure = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
