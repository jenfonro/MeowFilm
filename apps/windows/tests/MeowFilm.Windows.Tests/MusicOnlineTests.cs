using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

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
                installationId = f.Module.Sources.Single().InstallationId, platform = "wy", song, quality = "320k"
            });
            Equal("https://media.invalid/precise", reply.GetProperty("url").GetString());
            Equal(180.125, song.GetProperty("sources")[0].GetProperty("musicInfo").GetProperty("duration").GetDouble());
            True(!song.GetRawText().Contains("\"interval\""), "dispatch does not mutate durable metadata");
        });
        yield return Case("online safe JS selection, explicit quality and target, queue and native Started history", async () =>
        {
            await using var f = await Fixture.Create();
            Equal(0, f.Calls.Count("music.quality.settings.get"));
            await SetupOnline(f);
            await f.Module.ActivateQualitySettingsAsync();
            await Until(() => f.Module.CanEditQuality);
            await f.Module.SetPlaybackQualityRequestAsync("flac");
            await Until(() => f.Module.CanEditQuality && f.Module.PlaybackQualityRequest == "flac");
            Equal("flac", (await f.Rpc("music.quality.settings.get")).GetProperty("settings").GetProperty("request").GetString());
            var source = f.Module.Sources.Single();
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
            True(f.Calls.Log.Where(call => call.Method.StartsWith("music.transfer.", StringComparison.Ordinal))
                .All(call => !call.Parameters.GetRawText().Contains(JsonSerializer.Serialize(f.Dialogs.ScriptPath).Trim('"'),
                    StringComparison.Ordinal)), "no script pathname RPC");
            var requests = f.Calls.Log.Where(call => call.Method == "music.online.resolve").ToArray();
            Equal(source.InstallationId, requests.Single().Parameters.GetProperty("installationId").GetString());
            await f.Module.SourceActionAsync("disable"); True(!f.Playback.HasMedia, "source disable stops owned media");
        });
        yield return Case("late online resolver cannot open after cancellation; no synthetic history", async () =>
        {
            await using var f = await Fixture.Create(); await SetupOnline(f);
            var held = f.Calls.HoldNext("music.online.resolve");
            var play = PlayOnlineSong(f.Module); await held.Arrived.Task;
            var cancel = f.Module.CancelOnlineAsync(); await Drain();
            True(!cancel.IsCompleted && !play.IsCompleted, "cancel retains the actual unresolved reply until it settles");
            Equal(0, f.Playback.Opens.Count); Equal(0, f.Calls.Count("music.history.recordPlayed"));
            held.Release(); await Task.WhenAll(cancel, play); await Drain();
            Equal(0, f.Playback.Opens.Count); Equal(0, f.Calls.Count("music.history.recordPlayed"));
            await f.Module.TogglePlaybackAsync(); Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("only successful Native settings save cancels in-flight actual LX playback resolution", async () =>
        {
            await using var f = await Fixture.Create();
            await SetupOnline(f, "new Promise(() => {})");
            var song = (await f.Rpc("music.online.song", new { platform = "wy", musicInfo = new { songmid = "42" },
                title = "合成歌曲", artist = "合成艺人" })).GetProperty("song");
            var settings = (await f.Rpc("music.settings.get")).GetProperty("settings");
            var revision = settings.GetProperty("revision").GetInt64();
            var resolution = f.Application.ExecuteAsync("music.online.resolve", new {
                installationId = f.Module.Sources.Single().InstallationId, platform = "wy", song, quality = "320k" });
            await Until(() => f.Runtime.Calls.Contains("music.script.invoke"));
            var rejected = await f.Application.ExecuteAsync("music.settings.save", new {
                ifRevision = revision + 1, lyrics = true, target = settings.GetProperty("target") });
            True(!rejected.GetProperty("ok").GetBoolean() && !resolution.IsCompleted, "rejected save does not cancel playback");
            await f.Rpc("music.settings.save", new { ifRevision = revision, lyrics = true, target = settings.GetProperty("target") });
            var cancelled = await resolution.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("CANCELLED", cancelled.GetProperty("code").GetString());
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("JS update failure preserves source, safe multi-chunk update and explicit rollback", async () =>
        {
            await using var f = await Fixture.Create(); await SetupOnline(f);
            var before = f.Module.Sources.Single();
            f.Dialogs.Confirmation = false;
            File.WriteAllText(f.Dialogs.ScriptPath!, "throw new Error('synthetic failure');", new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync();
            True(f.Module.Status.Length > 0, "trial failure visible");
            await f.Module.RefreshOnlineAsync(); Equal(before.Hash, f.Module.Sources.Single().Hash);
            File.WriteAllText(f.Dialogs.ScriptPath!, TestScript("'https://media.invalid/updated'", version: "2") +
                "/*" + new string('x', 40000) + "*/", new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync();
            True(f.Calls.Count("music.transfer.chunk") >= 5, "multiple owned byte chunks");
            Equal("2", f.Module.Sources.Single().Version);
            f.Dialogs.Confirmation = true;
            await f.Module.SourceActionAsync("rollback"); Equal(before.Hash, f.Module.Sources.Single().Hash);
            await f.Module.SourceActionAsync("remove"); Equal(0, f.Module.Sources.Count);
        });
        yield return Case("source enable and update are operational while removal still requires confirmation", async () =>
        {
            await using var f = await Fixture.Create();
            var path = Path.Combine(f.Root, "source.js");
            File.WriteAllText(path, TestScript(), new UTF8Encoding(false));
            f.Dialogs.ScriptPath = path;
            f.Dialogs.Confirmation = false;
            await f.Module.ImportSourceAsync();
            await f.Module.SourceActionAsync("enable");
            True(f.Module.Sources.Single().Enabled, "source enable is not a network grant");
            File.WriteAllText(path, TestScript(version: "2"), new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync();
            Equal("2", f.Module.Sources.Single().Version);
            var permissions = f.Calls.Last("music.sources.update").GetProperty("permissions");
            True(permissions.EnumerateObject().Select(field => field.Name).SequenceEqual(["storage"]),
                "the native update contract owns only storage permissions");
            await f.Module.SourceActionAsync("remove"); Equal(1, f.Module.Sources.Count);
            f.Dialogs.Confirmation = true;
            await f.Module.SourceActionAsync("remove"); Equal(0, f.Module.Sources.Count);
        });
        yield return Case("multi-file source import reads script metadata and remains Node-free", async () =>
        {
            await using var f = await Fixture.Create();
            var first = Path.Combine(f.Root, "source-a.js");
            var second = Path.Combine(f.Root, "source-b.js");
            File.WriteAllText(first, TestScript(name: "音源 A"), new UTF8Encoding(false));
            File.WriteAllText(second, TestScript(version: "2", name: "音源 B"), new UTF8Encoding(false));
            f.Dialogs.ScriptPaths = [first, second];
            await f.Module.ImportSourceAsync();
            Equal(2, f.Module.Sources.Count);
            True(f.Module.Sources.Select(source => source.Name).SequenceEqual(["音源 A", "音源 B"]),
                "multi-file import preserves selection order and header names");
            Equal("2", f.Module.Sources[1].Version);
            Equal("音源 B", f.Module.SelectedSource?.Name);
            var detail = await f.Rpc("music.sources.detail", new { installationId = f.Module.SelectedSource!.InstallationId });
            True(detail.GetProperty("permissions").EnumerateObject().Select(field => field.Name).SequenceEqual(["storage"]),
                "import persists only storage permissions");
            True(!f.Runtime.HasStarted, "import and listing never start Node");
        });
        yield return Case("two-phase source pause unloads LX and explicit resumed play reactivates without replay", async () =>
        {
            await using var f = await Fixture.Create(); await SetupOnline(f);
            await PlayOnlineSong(f.Module); Equal(1, f.Playback.Opens.Count);
            await f.Module.PrepareShutdownAsync();
            True(f.Runtime.Calls.Contains("music.script.unload"), "script resources retired during prepare");
            var invokes = f.Runtime.Calls.Count(m => m == "music.script.invoke");
            await f.Module.CancelShutdownPreparationAsync();
            Equal(invokes, f.Runtime.Calls.Count(m => m == "music.script.invoke")); Equal(1, f.Playback.Opens.Count);
            await PlayOnlineSong(f.Module);
            Equal(2, f.Playback.Opens.Count); Equal(invokes + 1, f.Runtime.Calls.Count(m => m == "music.script.invoke"));
        });
        yield return Case("playlist loaded-page selection adds through the destination current revision", async () =>
        {
            await using var f = await Fixture.Create();
            var id = await f.Collection("完整歌单", 205);
            await f.Module.NavigateAsync("playlist/" + id, default);
            await f.Module.LoadMoreCollectionAsync();
            f.Module.SelectedSong = f.Module.Songs[100];
            var selected = f.Module.SelectedSong.Id;
            var menu = ((DataGrid)f.Module.View.FindName("SongsGrid")).ContextMenu;
            menu.DataContext = f.Module;
            menu.Measure(new Size(420, 600));
            menu.Arrange(new Rect(0, 0, 420, 600)); menu.UpdateLayout();
            var targets = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "添加到歌单");
            True(ReferenceEquals(targets.DataContext, f.Module), "song menu must inherit its actual module context");
            Equal(f.Module.Playlists.Count, targets.Items.Count);
            var favorite = new MenuItem
            {
                DataContext = targets.Items.Cast<PlaylistRow>().Single(row => row.Id == "favorites"),
                Style = targets.ItemContainerStyle
            };
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
                httpOptions: NativeHttpMediaOptions.ForLoopbackTests([new Uri(new Uri(server.Url), "/")]));
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
        await f.Module.ImportSourceAsync(); Equal(1, f.Module.Sources.Count);
        f.Module.SelectedSource = f.Module.Sources.Single();
        await f.Module.LoadSourceAsync(f.Module.SelectedSource);
        await f.Module.SourceActionAsync("enable");
        await Until(() => f.Module.CanEdit);
        await f.Module.UseSourcePlatformAsync("wy");
        var saved = (await f.Rpc("music.settings.get")).GetProperty("settings");
        Equal(f.Module.Sources.Single().InstallationId, saved.GetProperty("target").GetProperty("installationId").GetString());
        Equal("wy", saved.GetProperty("target").GetProperty("platform").GetString());
        True(!saved.TryGetProperty("quality", out _), "online settings must not duplicate native quality policy");
        True(f.Module.Status.Length == 0, "configured exact playback target");
    }
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
/// Executes only the checked-in LX script-only module, on first explicit script
/// call. Native business and document RPC are not installed in this test process.
/// Authored fixture scripts are the only executed sources.
/// </summary>
internal sealed class NativeMusicScriptTestRuntime : IRuntimeClient, IAsyncDisposable
{
    private readonly OnDemandRuntimeClient runtime;
    private IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> capabilities
        = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>();
    public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();
    public int DisposeCalls { get; private set; }
    public bool HasStarted => runtime.HasStarted;
    public NativeMusicScriptTestRuntime() => runtime = new(StartAsync);
    public void Register(IScriptPlatformCapabilities application)
    {
        if (HasStarted) throw new InvalidOperationException("Capabilities must be frozen before scripts start");
        capabilities = application.ScriptCapabilities.ToDictionary(p => p.Key, p => p.Value);
        if (capabilities.Keys.Any(k => !k.StartsWith("platform.musicScript.", StringComparison.Ordinal)))
            throw new InvalidOperationException("No generic document authority in script fixture");
    }
    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!method.StartsWith("music.script.", StringComparison.Ordinal))
            throw new InvalidOperationException("Ordinary music business must remain native");
        Calls.Enqueue(method); return runtime.CallAsync(method, parameters, cancellationToken);
    }
    private async Task<IRuntimeSession> StartAsync(CancellationToken token)
    {
        var root = new DirectoryInfo(TestWorkspace.SourceRoot);
        var node = Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE") ?? throw new InvalidOperationException("Pinned Node required for authored LX execution");
        var pipeName = "meowfilm-" + Guid.NewGuid().ToString("D");
        var session = Guid.NewGuid().ToString("D");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var bootstrap = new Uri(Path.Combine(root.FullName, "apps/windows/runtime/bootstrap.mjs")).AbsoluteUri;
        var module = new Uri(Path.Combine(root.FullName, "apps/runtime/modules/music-script.mjs")).AbsoluteUri;
        var code = $"import {{startWindowsAppRuntime}} from {JsonSerializer.Serialize(bootstrap)};\n" +
            $"import {{createMusicScriptModule}} from {JsonSerializer.Serialize(module)};\n" +
            "await startWindowsAppRuntime({product:'music',argv:process.argv.slice(1),assemble:({call})=>[createMusicScriptModule({call})]});";
        var info = new ProcessStartInfo(node) { WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--input-type=module", "-e", code, "--", "--meow-pipe", pipeName, "--meow-session", session })
            info.ArgumentList.Add(arg);
        Process? child = null;
        try
        {
            child = Process.Start(info) ?? throw new InvalidOperationException("Authored script test runtime failed");
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            return new ScriptSession(child, new BridgePeer(pipe, session, capabilities), output, errors);
        }
        catch
        {
            pipe.Dispose();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().ConfigureAwait(false); child.Dispose();
            }
            throw;
        }
    }
    public async ValueTask DisposeAsync() { DisposeCalls++; await runtime.DisposeAsync(); }
    private sealed class ScriptSession(Process child, BridgePeer peer, Task<string> output, Task<string> errors) : IRuntimeSession
    {
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
            => peer.CallAsync(method, parameters ?? new { }, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await peer.DisposeAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            var code = child.ExitCode; child.Dispose();
            if ((await output).Length > 0 || (await errors).Length > 0 || code != 0)
                throw new InvalidOperationException("Script-only fixture exited with diagnostics");
        }
    }
}
