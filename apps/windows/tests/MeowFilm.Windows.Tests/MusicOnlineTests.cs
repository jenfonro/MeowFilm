using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> OnlineCases()
    {
        yield return Case("online metadata keeps precise source duration and derives LX interval at dispatch", async () =>
        {
            await using var f = await Fixture.Create();
            await SetupOnline(f, "info.musicInfo.duration === 180.125 && info.musicInfo.interval === '03:00' ? 'https://media.invalid/precise' : 'https://media.invalid/wrong'");
            var song = (await f.Rpc("music.online.song", new
            {
                platform = "wy", title = "精确时长", artist = "合成艺人", duration = 180.125,
                musicInfo = new { songmid = "42", interval = "09:59" }
            })).GetProperty("song");
            var info = song.GetProperty("sources")[0].GetProperty("musicInfo");
            Equal(180.125, info.GetProperty("duration").GetDouble());
            True(!info.TryGetProperty("interval", out _), "formatted duration is not persisted");
            var reply = await f.Rpc("music.online.resolve", new
            {
                installationId = ScriptRow(f).InstallationId, platform = "wy", song, quality = "320k"
            });
            Equal("https://media.invalid/precise", reply.GetProperty("url").GetString());
            Equal(180.125, song.GetProperty("sources")[0].GetProperty("musicInfo").GetProperty("duration").GetDouble());
            True(!song.GetRawText().Contains("\"interval\""), "dispatch does not mutate durable metadata");
        });
        yield return Case("online enabled network source participates with built-ins explicit quality queue and native Started history", async () =>
        {
            await using var f = await Fixture.Create();
            Equal(0, f.Calls.Count("music.quality.settings.get"));
            await SetupOnline(f);
            await f.Module.ActivateQualitySettingsAsync();
            await Until(() => f.Module.CanEditQuality);
            await f.Module.SetPlaybackQualityRequestAsync("flac");
            await Until(() => f.Module.CanEditQuality && f.Module.PlaybackQualityRequest == "flac");
            Equal("flac", (await f.Rpc("music.quality.settings.get")).GetProperty("settings").GetProperty("request").GetString());
            var source = ScriptRow(f);
            True(source.Enabled, "explicit source enabled");
            await PlayOnlineSong(f.Module);
            Equal(1, f.Playback.Opens.Count);
            Equal("https://media.invalid/42", f.Playback.Opens[0].Uri.AbsoluteUri);
            Equal(0, f.Calls.Count("music.history.recordPlayed"));
            f.Playback.Start(); await Until(() => f.Calls.Count("music.history.recordPlayed") == 1);
            var lyricReply = await f.Application.ExecuteAsync("music.online.lyrics", new {
                installationId = source.InstallationId, platform = "wy",
                song = f.Calls.Last("music.online.resolve").GetProperty("song") });
            Equal(false, lyricReply.GetProperty("ok").GetBoolean());
            var lyricCode = lyricReply.GetProperty("code").GetString()!;
            Equal("CAPABILITY_UNAVAILABLE", lyricCode);
            // Ordinary lookup is official-first. Keep its real denied-upstream
            // path observable before checking the separate capability error arm.
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal("操作失败，请重试", f.Module.LyricStatus); Equal(0, f.Module.Lyrics.Count);
            f.Calls.FailNext("music.lyrics.lookup", lyricCode);
            await f.Module.RefreshLyricsAsync();
            Equal("当前音源不提供歌词", f.Module.LyricStatus); Equal(0, f.Module.Lyrics.Count);
            f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
            Equal(Path.GetFileName(f.Dialogs.ScriptPath), f.Runtime.Uploads.Single().Basename);
            var requests = f.Calls.Log.Where(call => call.Method == "music.online.resolve").ToArray();
            Equal(source.InstallationId, requests.Single().Parameters.GetProperty("installationId").GetString());
            var identity = f.Playback.Identity;
            int opens = f.Playback.Opens.Count;
            await f.Module.SetSourceEnabledAsync(ScriptRow(f), false); await Until(() => f.Module.CanEdit);
            True(!ScriptRow(f).Enabled, "source disable prevents new parsing");
            True(f.Playback.HasMedia && f.Playback.IsPlaying && f.Playback.Identity == identity,
                "source disable preserves already accepted historical media");
            Equal(opens, f.Playback.Opens.Count);
            Equal(requests.Length, f.Calls.Count("music.online.resolve"));
        });
        yield return Case("late online resolver cannot open after its playback retires; no synthetic history", async () =>
        {
            await using var f = await Fixture.Create(); await SetupOnline(f);
            var held = f.Calls.HoldNext("music.online.resolve");
            var play = PlayOnlineSong(f.Module); await held.Arrived.Task;
            var retire = f.Module.PrepareShutdownAsync(); await Drain();
            True(!retire.IsCompleted && !play.IsCompleted, "retirement retains the actual unresolved reply until it settles");
            Equal(0, f.Playback.Opens.Count); Equal(0, f.Calls.Count("music.history.recordPlayed"));
            held.Release(); await Task.WhenAll(retire, play); await Drain();
            Equal(0, f.Playback.Opens.Count); Equal(0, f.Calls.Count("music.history.recordPlayed"));
            await f.Module.CancelShutdownPreparationAsync();
            await f.Module.TogglePlaybackAsync(); Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("lyric settings leave in-flight LX audio intact and the caller's cancellation drains it", async () =>
        {
            await using var f = await Fixture.Create();
            await SetupOnline(f, "new Promise(() => {})");
            var song = (await f.Rpc("music.online.song", new { platform = "wy", musicInfo = new { songmid = "42" },
                title = "合成歌曲", artist = "合成艺人" })).GetProperty("song");
            var settings = (await f.Rpc("music.settings.get")).GetProperty("settings");
            var revision = settings.GetProperty("revision").GetInt64();
            using var request = new CancellationTokenSource();
            var resolution = f.Application.ExecuteAsync("music.online.resolve", new {
                installationId = ScriptRow(f).InstallationId, platform = "wy", song, quality = "320k" }, request.Token);
            await Until(() => f.Runtime.Calls.Contains("music.script.resolve"));
            var rejected = await f.Application.ExecuteAsync("music.settings.save", new {
                ifRevision = revision + 1, lyrics = false });
            True(!rejected.GetProperty("ok").GetBoolean() && !resolution.IsCompleted, "rejected save does not cancel playback");
            await f.Rpc("music.settings.save", new { ifRevision = revision, lyrics = false });
            True(!resolution.IsCompleted, "lyric settings do not choose or cancel an audio provider");
            request.Cancel();
            var cancelled = await resolution.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("CANCELLED", cancelled.GetProperty("code").GetString());
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("JS update failure preserves source, complete source update and explicit rollback", async () =>
        {
            await using var f = await Fixture.Create(); await SetupOnline(f);
            var before = ScriptRow(f);
            File.WriteAllText(f.Dialogs.ScriptPath!, "throw new Error('synthetic failure');", new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            True(f.Module.Status.Length > 0, "trial failure visible");
            await f.Module.RefreshSourceManagementAsync(); await Until(() => f.Module.CanEdit);
            Equal(before.ExecutionHash, ScriptRow(f).ExecutionHash);
            File.WriteAllText(f.Dialogs.ScriptPath!, TestScript("'https://media.invalid/updated'", version: "2") +
                "/*" + new string('x', 40000) + "*/", new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            var uploaded = f.Runtime.Uploads.Last();
            True(uploaded.Bytes.SequenceEqual(File.ReadAllBytes(f.Dialogs.ScriptPath!)), "the update sends the complete selected snapshot once");
            Equal(3, f.Runtime.Uploads.Count);
            var committed = f.Runtime.Requests.Last(request => request.Method == "music.script.sources.update").Parameters;
            Equal(uploaded.InstallationId, committed.GetProperty("installationId").GetString());
            Equal(uploaded.SourceRevision, committed.GetProperty("sourceRevision").GetInt64());
            Equal("2", ScriptRow(f).Version);
            f.Dialogs.Confirmation = false;
            await f.Module.RollbackSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            Equal("2", ScriptRow(f).Version);
            f.Dialogs.Confirmation = true;
            await f.Module.RollbackSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            Equal(before.ExecutionHash, ScriptRow(f).ExecutionHash);
            await f.Module.RemoveSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            Equal(0, ScriptRows(f).Length);
        });
        yield return Case("source enable and update are operational while removal still requires confirmation", async () =>
        {
            await using var f = await Fixture.Create();
            var path = Path.Combine(f.Root, "source.js");
            File.WriteAllText(path, TestScript(), new UTF8Encoding(false));
            f.Dialogs.ScriptPath = path;
            f.Dialogs.Confirmation = false;
            await f.Module.ImportSourceAsync(); await Until(() => f.Module.CanEdit);
            await f.Module.SetSourceEnabledAsync(ScriptRow(f), true); await Until(() => f.Module.CanEdit);
            True(ScriptRow(f).Enabled, "source enable is not a network grant");
            File.WriteAllText(path, TestScript(version: "2"), new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            Equal("2", ScriptRow(f).Version);
            var update = f.Runtime.Requests.Last(request => request.Method == "music.script.sources.update").Parameters;
            True(update.EnumerateObject().Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(["installationId", "sourceRevision"]), "an update selects the exact complete source candidate");
            await f.Module.RemoveSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            Equal(1, ScriptRows(f).Length);
            f.Dialogs.Confirmation = true;
            await f.Module.RemoveSourceAsync(ScriptRow(f)); await Until(() => f.Module.CanEdit);
            Equal(0, ScriptRows(f).Length);
        });
        yield return Case("multi-file source import reads script metadata without executing a script", async () =>
        {
            await using var f = await Fixture.Create();
            var first = Path.Combine(f.Root, "source-a.js");
            var second = Path.Combine(f.Root, "source-b.js");
            File.WriteAllText(first, TestScript(name: "音源 A"), new UTF8Encoding(false));
            File.WriteAllText(second, TestScript(version: "2", name: "音源 B"), new UTF8Encoding(false));
            f.Dialogs.ScriptPaths = [first, second];
            await f.Module.ImportSourceAsync(); await Until(() => f.Module.CanEdit);
            var scripts = ScriptRows(f);
            Equal(2, scripts.Length);
            True(scripts.Select(source => source.Name).SequenceEqual(["音源 A", "音源 B"]),
                "multi-file import preserves selection order and header names");
            Equal("2", scripts[1].Version);
            var detail = await f.Rpc("music.sources.detail", new { installationId = scripts[1].InstallationId });
            True(detail.GetProperty("item").GetProperty("state").GetString() == "disabled" &&
                detail.GetProperty("item").GetProperty("origin").GetProperty("kind").GetString() == "file",
                "an import is a disabled local source");
            True(!f.Runtime.Calls.Contains("music.script.resolve") && !f.Runtime.Calls.Contains("music.script.sources.enable"),
                "import and listing never execute a script");
        });
        yield return Case("two-phase pause replays no source call and explicit resumed play resolves once", async () =>
        {
            await using var f = await Fixture.Create(); await SetupOnline(f);
            await PlayOnlineSong(f.Module); Equal(1, f.Playback.Opens.Count);
            await f.Module.PrepareShutdownAsync();
            var resolves = f.Runtime.Calls.Count(m => m == "music.script.resolve");
            await f.Module.CancelShutdownPreparationAsync();
            Equal(resolves, f.Runtime.Calls.Count(m => m == "music.script.resolve")); Equal(1, f.Playback.Opens.Count);
            await PlayOnlineSong(f.Module);
            Equal(2, f.Playback.Opens.Count); Equal(resolves + 1, f.Runtime.Calls.Count(m => m == "music.script.resolve"));
        });
        yield return Case("playlist loaded-page selection adds through the destination current revision", async () =>
        {
            await using var f = await Fixture.Create();
            var id = await f.Collection("完整歌单", 205);
            await f.Module.NavigateAsync("playlist/" + id, default);
            await f.Module.LoadMoreCollectionAsync();
            f.Module.SelectedSong = f.Module.Songs[100];
            var selected = f.Module.SelectedSong.Id;
            using var menuSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("song-menu-fixture")
                { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                    Width = 900, Height = 700, PositionX = -32000, PositionY = -32000 });
            menuSource.RootVisual = f.Module.View;
            var menu = ((DataGrid)f.Module.View.FindName("SongsGrid")).ContextMenu;
            var grid = (DataGrid)f.Module.View.FindName("SongsGrid");
            f.Module.View.Measure(new Size(900, 700));
            f.Module.View.Arrange(new Rect(0, 0, 900, 700)); f.Module.View.UpdateLayout();
            var request = (ContextMenuEventArgs)typeof(ContextMenuEventArgs)
                .GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    [typeof(object), typeof(bool), typeof(double), typeof(double)], null)!
                .Invoke([grid, true, -1d, -1d]);
            grid.RaiseEvent(request);
            menu.Measure(new Size(420, 600));
            menu.Arrange(new Rect(0, 0, 420, 600)); menu.UpdateLayout();
            var targets = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "添加到歌单");
            True(ReferenceEquals(targets.DataContext, f.Module), "song menu must inherit its actual module context");
            Equal(f.Module.Playlists.Count, targets.Items.Count);
            var favorite = targets.Items.OfType<MenuItem>().Single(item => item.Header as string ==
                f.Module.Playlists.Single(row => row.Id == "favorites").Name);
            var adds = f.Calls.Count("music.library.addSongs");
            favorite.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => f.Calls.Count("music.library.addSongs") == adds + 1 && !f.Module.Busy);
            await f.Module.NavigateAsync("favorites", default);
            Equal(1, f.Module.Songs.Count);
            Equal(selected, f.Module.Songs.Single().Id);
        });
        yield return Case("online native chain uses real libVLC with generated loopback WAV, not URL-success-as-played", async () =>
        {
            await using var server = new MusicWaveServer();
            var nativePath = Path.Combine(AppContext.BaseDirectory, "libvlc",
                "win-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            await using var native = new NativePlayback(dispatcher: null, nativeLibraryPath: nativePath, audioOutput: NativeAudioOutput.Dummy,
                httpOptions: new NativeHttpMediaOptions());
            await using var f = await Fixture.Create(native: native);
            await SetupOnline(f, JsonSerializer.Serialize(server.Url));
            var module = f.Module;
            await PlayOnlineSong(module);
            await Until(() => native.IsPlaying && native.DurationSeconds > 0 && f.Calls.Count("music.history.recordPlayed") > 0);
            Equal("music", native.OwnerId); True(native.DurationSeconds > 0, "native decoded generated audio");
            True(server.Requests > 0, "native requested bytes from controlled HTTP origin");
            Console.WriteLine("Music native evidence: authored LX + generated WAV + libVLC dummy output; no upstream/account/speaker acceptance.");
        });
    }
    private static string TestScript(string result = "'https://media.invalid/' + info.musicInfo.songmid",
        string version = "1", string name = "合成源") => $$$"""
        /*!
         * @name {{{name}}}
         * @version {{{version}}}
         */
        lx.on(lx.EVENT_NAMES.request, async ({info}) => {{{result}}});
        lx.send(lx.EVENT_NAMES.inited, {status:true,sources:{
          wy:{name:'合成源',type:'music',actions:['musicUrl'],qualitys:['320k','flac']}
        }});
        """;
    private static async Task SetupOnline(Fixture f, string? result = null)
    {
        f.Dialogs.ScriptPath = Path.Combine(f.Root, "authored-lx.js");
        File.WriteAllText(f.Dialogs.ScriptPath, TestScript(result ?? "'https://media.invalid/' + info.musicInfo.songmid"), new UTF8Encoding(false));
        await f.Module.ImportSourceAsync(); await Until(() => f.Module.CanEdit);
        Equal(1, ScriptRows(f).Length);
        await f.Module.SetSourceEnabledAsync(ScriptRow(f), true);
        await Until(() => f.Module.CanEdit);
        await f.Module.RefreshSourceManagementAsync(); await Until(() => f.Module.CanEdit);
        var saved = (await f.Rpc("music.settings.get")).GetProperty("settings");
        True(ScriptRow(f).Enabled, "the enabled network source is eligible without a global selection");
        Equal(3, saved.GetProperty("schema").GetInt32());
        True(!saved.TryGetProperty("quality", out _), "online settings must not duplicate native quality policy");
        True(f.Module.Status.Length == 0, "source activation and native settings refresh succeeded");
    }
    private static MusicSourceRow[] ScriptRows(Fixture f) => f.Module.Sources.Where(row => row.IsScript).ToArray();
    private static MusicSourceRow ScriptRow(Fixture f) => ScriptRows(f).Single();
    private static Task PlayOnlineSong(MusicCapability module) =>
        module.PlayOnlineSongAsync("wy", "合成歌曲", "合成艺人", "",
            JsonSerializer.SerializeToElement(new { songmid = "42" }));

    private sealed class MusicWaveServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<Task> clients = [];
        private readonly Task loop;
        private readonly byte[] wave;
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public string Url { get; }
        public MusicWaveServer()
        {
            const int rate = 8000, seconds = 12;
            wave = new byte[44 + rate * seconds * 2];
            using var stream = new MemoryStream(wave); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(wave.Length - 8); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(wave.Length - 44);
            for (var i = 0; i < rate * seconds; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * i / rate)));
            listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/tone.wav";
            loop = Run();
        }
        private async Task Run()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    lock (clients) clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var first = await reader.ReadLineAsync(stop.Token);
                    if (first is null) return;
                    int offset = 0;
                    for (int i = 0; i < 64; i++)
                    {
                        var line = await reader.ReadLineAsync(stop.Token);
                        if (string.IsNullOrEmpty(line)) break;
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line[13..].Split('-')[0], out offset);
                    }
                    offset = Math.Clamp(offset, 0, wave.Length - 1); Interlocked.Increment(ref requests);
                    var range = offset > 0 ? $"Content-Range: bytes {offset}-{wave.Length - 1}/{wave.Length}\r\n" : "";
                    var header = $"HTTP/1.1 {(offset > 0 ? "206 Partial Content" : "200 OK")}\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length - offset}\r\nETag: \"synthetic-wave-v1\"\r\nAccept-Ranges: bytes\r\n{range}Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), stop.Token);
                    await stream.WriteAsync(wave.AsMemory(offset), stop.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop(); await loop;
            Task[] pending; lock (clients) pending = clients.ToArray();
            await Task.WhenAll(pending); stop.Dispose();
        }
    }
}

/// <summary>
/// Executes authored LX sources through the packaged runtime. The App starts its runtime with the
/// App; this fixture process starts at the first script call so tests can observe that native
/// business never needs it. It offers no reverse methods. Authored fixture scripts are the only
/// executed sources.
/// </summary>
internal sealed class NativeMusicScriptTestRuntime : IRuntimeClient, IAsyncDisposable
{
    private readonly AppRuntimeClient runtime;
    private readonly object gate = new();
    private readonly string dataRoot = Path.Combine(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
        ?? Path.GetTempPath(), "script-fixture-" + Guid.NewGuid().ToString("N"));
    public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();
    public int DisposeCalls { get; private set; }
    public bool HasStarted => runtime.HasStarted;
    public NativeMusicScriptTestRuntime() => runtime = new(StartAsync);
    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!method.StartsWith("music.script.", StringComparison.Ordinal))
            throw new InvalidOperationException("Ordinary music business must remain native");
        lock (gate) if (!runtime.HasStarted) runtime.Start();
        Calls.Enqueue(method); Requests.Enqueue((method, JsonSerializer.SerializeToElement(parameters ?? new { })));
        return runtime.CallAsync(method, parameters, cancellationToken);
    }
    public System.Collections.Concurrent.ConcurrentQueue<(string Method, JsonElement Parameters)> Requests { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<(string InstallationId, long SourceRevision, string? Basename, byte[] Bytes)> Uploads { get; } = new();
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
    {
        if (module != "music") throw new InvalidOperationException("Only authored music sources may be uploaded");
        lock (gate) if (!runtime.HasStarted) runtime.Start();
        Uploads.Enqueue((installationId, sourceRevision, basename, source.ToArray()));
        return runtime.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
    }
    private async Task<IRuntimeSession> StartAsync(CancellationToken token)
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
            ?? throw new InvalidOperationException("Packaged runtime root required.");
        string node = Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE")
            ?? throw new InvalidOperationException("Verified Node required.");
        RuntimeModule[] modules = RuntimeHttpClientTests.Modules;
        Directory.CreateDirectory(dataRoot);
        return await NodeRuntime.StartAsync(node,
            RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules),
            modules, dataRoot, token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() { DisposeCalls++; await runtime.DisposeAsync(); }
}
