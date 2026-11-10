using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;
using MeowFilm.Platform.Runtime;
using Microsoft.Win32.SafeHandles;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MeowFilm.Windows.Tests;

// Real aggregate/Cat Worker, real HTTP/SSE, and the production Film native adapter.
// Only the source and public synthetic media are fixtures. No Film runtime/native probes.
internal sealed class FilmPlaybackNativeFixture : IAsyncDisposable
{
    internal const string Title = "Native Fixture Film";
    internal const string Installation = "film-native-fixture";
    internal static readonly string[] Sites = ["alpha", "beta", "broken"];
    internal static string Flag(string site) => "原始未知线路/" + site;
    internal static string PlayId(string site) => site + "-opaque-file";
    internal static string SourceKey(string site) => $"cat:{Installation}:{site}";
    internal sealed record PageOwner(FilmPlaybackSession Session, FilmPlaybackPage Page, Guid Id, string TargetId);

    internal readonly NodeRuntime Runtime;
    internal readonly NativePlayback Native;
    internal readonly RuntimeTraffic Traffic;
    internal readonly FilmPlaybackView View = new();
    internal readonly Window Window;
    internal readonly string DataRoot;
    private readonly Process process;
    private readonly SafeProcessHandle processHandle;
    private readonly List<FilmPlaybackSession> owners = [];
    private readonly List<string> failures = [];
    private readonly FilmPlaybackClient client;
    private JsonElement[] sources = [];
    private RuntimeConnection connection = null!;
    private Task? disposal;
    internal int Generation { get; private set; }
    internal int NativeEnded { get; private set; }

    private FilmPlaybackNativeFixture(NodeRuntime runtime, NativePlayback native, string dataRoot)
    {
        Runtime = runtime; Native = native; Traffic = new(runtime, native); DataRoot = dataRoot;
        client = new(runtime);
        process = Process.GetProcessById(runtime.ProcessId);
        processHandle = process.SafeHandle; // Pin the actual child, not a potentially reused PID.
        Native.Ended += (_, _) => NativeEnded++;
        Window = new()
        {
            Content = View, Width = 1120, Height = 820, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
    }

    internal static async Task<FilmPlaybackNativeFixture> CreateAsync(bool headers = false,
        bool reports = true, bool longVideo = false)
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
            ?? throw new InvalidOperationException("Run the selected group through scripts/windows/test.ps1.");
        var modules = RuntimeHttpClientTests.Modules;
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules);
        string root = TestWorkspace.DataPath("film-playback-native", Guid.NewGuid().ToString("N"));
        byte[] bytes;
        if (longVideo) bytes = FFmpegVideoFixtures.Progressive();
        else
        {
            bytes = File.ReadAllBytes(Path.Combine(TestWorkspace.SourceRoot, "native", "VideoKernel",
                "Tests", "VideoKernelTests", "Fixtures", "ordinary.mp4"));
            Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) ==
                "19136344f4cd40b26054f0c502d599875931b9da95334d708ea4c9106021860b", "existing public synthetic ordinary.mp4 input");
        }
        var runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules,
            Path.Combine(root, "script-runtime"));
        NativePlayback? native = null;
        FilmPlaybackNativeFixture? fixture = null;
        try
        {
            // The real Cat receipt owns revocation and release; HTTP input has no origin admission.
            native = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            fixture = new(runtime, native, Path.Combine(root, "script-runtime"));
            fixture.connection = await runtime.CaptureConnectionAsync();
            Check(ReferenceEquals(fixture.connection.Client, runtime) &&
                fixture.connection.Revoked.CanBeCanceled && !fixture.connection.Revoked.IsCancellationRequested,
                "capture must belong to this actual connected Node epoch");
            byte[] source = Encoding.UTF8.GetBytes(Worker(bytes, headers, reports));
            Okay(await runtime.UploadSourceAsync("cat", Installation, 1, source), "cat.script.v2");
            Okay(await runtime.CallAsync("cat.script.install",
                new { installationId = Installation, sourceRevision = 1, enabled = true }), "cat.script.v2");
            var status = Okay(await runtime.CallAsync("cat.script.status", new { }), "cat.script.v2");
            var state = FilmJson.Array(status, "instances").Single(row => FilmJson.Text(row, "installationId") == Installation);
            Check(FilmJson.Text(state, "state") == "ready" && FilmJson.Integer(state, "generation") is > 0,
                "the installed Cat Worker must actually be ready");
            fixture.Generation = FilmJson.Integer(state, "generation")!.Value;
            var sites = FilmJson.Array(state, "sites");
            Check(Sites.All(site => sites.Any(row => FilmJson.Text(row, "key") == site)),
                "use sites from the real installed generation, not a fabricated status");
            fixture.sources = Sites.Select(site => FilmJson.Encode(new
            {
                key = SourceKey(site), engine = "cat", installationId = Installation,
                siteId = site, generation = fixture.Generation,
                name = FilmJson.Text(sites.Single(row => FilmJson.Text(row, "key") == site), "name")
            })).ToArray();
            fixture.Window.Show();
            await Until(() => fixture.View.IsLoaded, "loaded real WPF playback consumer");
            Observe("fixture", new
            {
                generation = fixture.Generation, sites = sites.Length, headers, reports,
                asset = longVideo ? "FFmpegVideoFixtures.Progressive/24s" : "VideoKernel/public-ordinary.mp4",
                sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                dummyAudio = true, realVideoSurface = true
            });
            return fixture;
        }
        catch
        {
            if (fixture is not null) await fixture.DisposeAsync();
            else
            {
                if (native is not null) await native.DisposeAsync();
                await runtime.DisposeAsync();
            }
            throw;
        }
    }

    internal async Task<PageOwner> OpenAsync(string platform = "windows", string targetId = "film:native-fixture")
    {
        var session = new FilmPlaybackSession(token =>
        {
            token.ThrowIfCancellationRequested();
            connection.Revoked.ThrowIfCancellationRequested();
            // Same verified origin/lifetime and fixed process. Traffic only forwards and observes.
            return Task.FromResult(connection with { Client = Traffic });
        }, new FilmNativeMedia(Native));
        session.OperationFailed += (_, code) => failures.Add(code);
        owners.Add(session);
        var target = new FilmPlaybackTarget(targetId, "movie", Title);
        var request = new FilmPlaybackRequest(target, sources);
        var page = new FilmPlaybackPage(new(targetId, Title, "", "") { PlaybackContext = request }, request, session, null);
        View.DataContext = page;
        await session.OpenAsync(page, new { platform, target = target.Wire(), sources }, default);
        Guid id = Traffic.Opened(targetId);
        await Until(async () => FilmJson.Text(await SnapshotAsync(id), "stage") != "loading", "real session initialization");
        return new(session, page, id, targetId);
    }

    internal Task<JsonElement> SnapshotAsync(Guid id) =>
        client.CallAsync("session.snapshot", new { sessionId = id.ToString() });
    internal Task<JsonElement> LibraryAsync() => client.CallAsync("library.get", new { });
    internal async Task<JsonElement> WorkerStateAsync() => Okay(await Runtime.CallAsync("cat.script.invoke", new
    {
        installationId = Installation, generation = Generation, siteId = "alpha", operation = "home",
        payload = new { }
    }), "cat.script.v2");
    internal async Task<JsonElement[]> CallsAsync() => FilmJson.Array(await WorkerStateAsync(), "calls");
    internal async Task<JsonElement[]> ReportsAsync() =>
        (await CallsAsync()).Where(row => FilmJson.Text(row, "operation") == "report").ToArray();
    internal async Task<JsonElement[]> HistoryAsync() => FilmJson.Array(await LibraryAsync(), "history");

    internal async Task<string> OutputAsync(PageOwner owner, bool pauseAfterOutput = true)
    {
        await Until(() => owner.Session.HasOutput &&
            Native.ObservedVideoOutput is { DisplayedPictures: > 0, OwnerId: "film" } output &&
            output.Identity == owner.Session.MediaIdentity, "actual matching libVLC video display counter");
        string attempt = owner.Session.MediaIdentity!;
        // Do not race a short ordinary.mp4 to its end while reading history and diagnostics.
        if (pauseAfterOutput) await owner.Session.SetPausedAsync(true);
        await Until(async () => FilmJson.Text(FilmJson.Member(await SnapshotAsync(owner.Id), "current"), "attemptId") == attempt &&
            Traffic.Commands("media.started").Any(call => call.Attempt == attempt && call.Completed),
            "native-driven Node media.started receipt");
        var evidence = Traffic.Commands("media.started").Single(call => call.Attempt == attempt);
        Check(evidence.Pictures > 0, "media.started was sent only after this exact candidate actually displayed pictures");
        Observe("video-output", new
        {
            sessionId = owner.Id, attemptId = attempt, evidence = Native.ObservedVideoOutput!.Evidence,
            displayedPictures = evidence.Pictures, position = Native.PositionSeconds,
            paused = Native.PauseRequested, nativeVersion = Native.NativeVersion
        });
        return attempt;
    }

    internal MediaPlayer CurrentPlayer() =>
        (MediaPlayer)AdaptiveMediaTests.Field(AdaptiveMediaTests.Session(Native)!, "Player")!;
    internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
    internal Button MoreButton() => Visuals(View).OfType<Button>().Single(button => Equals(button.Content, "加载更多"));

    internal sealed class NativeAllocation(object session)
    {
        internal readonly MediaPlayer Player = (MediaPlayer)AdaptiveMediaTests.Field(session, "Player")!;
        private readonly object demux = AdaptiveMediaTests.Field(session, "FFmpeg") ??
            throw new InvalidOperationException("The real private demux must own this input.");
        private readonly object resource = AdaptiveMediaTests.Field(session, "Resource") ??
            throw new InvalidOperationException("The real controlled resource must own this input.");
        internal long Pictures => ((LibVLCSharp.Shared.Media)AdaptiveMediaTests.Field(session, "Media")!)
            .Statistics.DisplayedPictures;
        internal bool PhysicallyReleased =>
            AdaptiveMediaTests.Field(session, "ReleaseTask") is Task<bool> { IsCompletedSuccessfully: true } release &&
            !release.Result && Player.NativeReference == IntPtr.Zero &&
            AdaptiveMediaTests.Field(demux, "_release") is Task { IsCompletedSuccessfully: true } &&
            AdaptiveMediaTests.Field(demux, "_owner") is IntPtr pointer && pointer == IntPtr.Zero &&
            AdaptiveMediaTests.Field(AdaptiveMediaTests.Field(demux, "_http")!, "_readers")
                is System.Collections.IDictionary { Count: 0 } &&
            AdaptiveMediaTests.Field(resource, "release") is Task { IsCompletedSuccessfully: true };
    }

    internal NativeAllocation CaptureNative() => new(AdaptiveMediaTests.Session(Native) ??
        throw new InvalidOperationException("No actual native allocation to observe."));
    internal string[] FailureCodes => failures.ToArray();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle child, uint exitCode);

    internal async Task KillRuntimeAsync()
    {
        Check(!processHandle.IsClosed && !process.HasExited && Runtime.IsConnected,
            "terminate only the live, previously pinned fixture child");
        if (!TerminateProcess(processHandle, 77))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Runtime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Check(process.HasExited && process.ExitCode == 77 && connection.Revoked.IsCancellationRequested &&
            !Runtime.IsConnected, "physical child exit and runtime drain revoke the original epoch");
        Observe("runtime-terminal", new
        {
            processId = Runtime.ProcessId, exactHandle = true, exitCode = process.ExitCode,
            epochRevoked = connection.Revoked.IsCancellationRequested,
            runtimeDrain = Runtime.Completion.IsCompletedSuccessfully
        });
    }

    internal async Task ResourceUnavailableAfterExitAsync(FilmMediaReceipt receipt)
    {
        Check(Runtime.Completion.IsCompletedSuccessfully && connection.Revoked.IsCancellationRequested &&
            receipt.MediaOrigin is not null && receipt.Address.IsLoopback,
            "resource reachability is checked only after the exact runtime physically exits");
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Head, receipt.Address);
        try
        {
            using var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            throw new InvalidOperationException("The dead fixture's media endpoint still returned HTTP " + (int)reply.StatusCode);
        }
        catch (HttpRequestException error) when (error.InnerException is SocketException
            { SocketErrorCode: SocketError.ConnectionRefused })
        {
            Observe("terminal-resource", new { attemptId = receipt.AttemptId, connectionRefused = true, releaseAck = false });
        }
    }

    internal async Task<(JsonElement Library, JsonElement Preferences)> ReadInSecondProcessAsync()
    {
        Check(disposal is { IsCompletedSuccessfully: true } && Runtime.Completion.IsCompletedSuccessfully &&
            connection.Revoked.IsCancellationRequested, "the first fixture and actual process must finish before restart");
        string path = Path.Combine(DataRoot, "film-playback", "library.json");
        byte[] before = File.ReadAllBytes(path);
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")!;
        var modules = RuntimeHttpClientTests.Modules;
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules);
        await using var next = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules, DataRoot);
        using var child = Process.GetProcessById(next.ProcessId);
        _ = child.SafeHandle;
        JsonElement library, preferences;
        try
        {
            var captured = await next.CaptureConnectionAsync();
            Check(!ReferenceEquals(captured.Client, connection.Client) && !captured.Revoked.IsCancellationRequested &&
                connection.Revoked.IsCancellationRequested, "the second real process has a new live epoch, never revives the old one");
            var reader = new FilmPlaybackClient(next);
            preferences = await reader.CallAsync("settings.get", new { });
            library = await reader.CallAsync("library.get", new { });
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
                "second-process formal reads must not rewrite or replace the original durable library");
        }
        finally
        {
            try { await next.DisposeAsync(); }
            finally
            {
                await next.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(child.HasExited, "the second real process must physically exit too");
            }
        }
        Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "read-only restart and close preserve the same durable bytes");
        Observe("same-root-restart", new
        {
            firstProcess = Runtime.ProcessId, secondProcess = next.ProcessId, sameDataRoot = true,
            firstDrain = Runtime.Completion.IsCompletedSuccessfully, secondDrain = next.Completion.IsCompletedSuccessfully,
            libraryUnchanged = true, librarySha256 = Convert.ToHexStringLower(SHA256.HashData(before))
        });
        return (library, preferences);
    }

    internal async Task SessionGoneAsync(Guid id)
    {
        try { await SnapshotAsync(id); }
        catch (FilmPlaybackException error) when (error.Code == "NOT_FOUND")
        {
            Observe("session-release", new { sessionId = id, code = error.Code });
            return;
        }
        throw new InvalidOperationException("Closed Node session is still accessible.");
    }

    internal static async Task ResourceGoneAsync(FilmMediaReceipt receipt)
    {
        // A status check AFTER release, never a surrogate for native output.
        Check(receipt.MediaOrigin is not null && receipt.Address.IsLoopback, "only check the actual controlled test receipt");
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Head, receipt.Address);
        using var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Check(reply.StatusCode == HttpStatusCode.NotFound, "released Node media resource must be 404");
        Observe("resource-release", new { attemptId = receipt.AttemptId, httpStatus = (int)reply.StatusCode });
    }

    internal void NoBackgroundFailures() =>
        Check(failures.Count == 0, "unexpected native Film owner failure codes: " + string.Join(',', failures));

    internal static JsonElement Okay(JsonElement reply, string contract)
    {
        Check(FilmJson.Text(reply, "contract") == contract && FilmJson.Bool(reply, "ok"),
            "real runtime RPC failed: " + FilmJson.Text(reply, "code"));
        return FilmJson.Member(reply, "value").Clone();
    }
    internal static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException("Film native integration: " + reason); }
    internal static void Observe(string phase, object evidence) =>
        Console.WriteLine("OBSERVED film-native " + JsonSerializer.Serialize(new { phase, evidence }));
    internal static async Task Until(Func<bool> predicate, string phase)
    {
        await Until(() => Task.FromResult(predicate()), phase);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    internal static async Task Until(Func<Task<bool>> predicate, string phase)
    {
        var timer = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Film native integration: " + phase);
            await Task.Delay(20); // Poll actual owners; delay is not the success criterion.
        }
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        async Task Drain(Task task)
        {
            try { await task; } catch (Exception error) { failure ??= error; }
        }
        Traffic.ReleaseGates();
        foreach (var owner in owners.AsEnumerable().Reverse()) await Drain(owner.DisposeAsync().AsTask());
        Traffic.Dump();
        View.DataContext = null; Window.Content = null; Window.Close();
        await Drain(Native.DisposeAsync().AsTask());
        await Drain(Runtime.DisposeAsync().AsTask());
        await Drain(Runtime.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await Drain(process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        bool exited = process.HasExited;
        process.Dispose();
        Observe("physical-close", new
        {
            nodeExited = exited, runtimeDrain = Runtime.Completion.IsCompletedSuccessfully,
            nativeHasMedia = Native.HasMedia, failure = failure is null ? null : failure.GetType().Name
        });
        if (failure is not null) throw new InvalidOperationException("Film native integration cleanup failed.", failure);
        Check(exited && Runtime.Completion.IsCompletedSuccessfully && !Native.HasMedia, "actual native and Node process drains");
    }

    // Embedding public fixture bytes only transports test input to the Worker's
    // private volume. The runtime/native media exchange itself is binary HTTP.
    private static string Worker(byte[] bytes, bool headers, bool reports) => $$"""
        const http = require('http');
        const bytes = Buffer.from('{{Convert.ToBase64String(bytes)}}', 'base64');
        const requireHeader = {{(headers ? "true" : "false")}};
        const watchReport = {{(reports ? "true" : "false")}};
        const sites = ['alpha', 'beta', 'broken'];
        const calls = [], reads = [];
        let server;
        const json = (response, value) => {
          response.setHeader('content-type', 'application/json');
          response.end(JSON.stringify(value));
        };
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            const path = request.url.split('?')[0];
            if (path === '/config' || path === '/full-config') {
              json(response, { video: { sites: sites.map(key => ({
                key, name: 'Native ' + key, api: '/api/' + key,
                actions: ['home', 'search', 'detail', 'play', 'report'],
                methods: { home: 'POST', search: 'POST', detail: 'POST', play: 'POST', report: 'POST' }
              })) } }); return;
            }
            if (path.startsWith('/proxy/')) {
              const site = path.split('/')[2];
              const accepted = !requireHeader || request.headers['x-film-native'] === 'fixture';
              const read = { site, method: request.method, range: request.headers.range ?? null,
                headerAccepted: accepted, finished: false };
              reads.push(read);
              response.once('finish', () => { read.finished = true; });
              if (!accepted) { response.writeHead(403); response.end(); return; }
              const body = site === 'broken' ? Buffer.from('not a playable movie') : bytes;
              let start = 0, end = body.length - 1, status = 200;
              if (request.headers.range) {
                const match = /^bytes=(\d*)-(\d*)$/.exec(request.headers.range);
                if (!match || !match[1] && !match[2]) {
                  response.writeHead(416, { 'content-range': 'bytes */' + body.length }); response.end(); return;
                }
                if (!match[1]) start = Math.max(0, body.length - Number(match[2]));
                else { start = Number(match[1]); if (match[2]) end = Math.min(end, Number(match[2])); }
                if (start > end || start >= body.length) {
                  response.writeHead(416, { 'content-range': 'bytes */' + body.length }); response.end(); return;
                }
                status = 206;
              }
              const out = { 'content-type': 'video/mp4', 'content-length': end - start + 1, 'accept-ranges': 'bytes' };
              if (status === 206) out['content-range'] = 'bytes ' + start + '-' + end + '/' + body.length;
              response.writeHead(status, out);
              response.end(request.method === 'HEAD' ? undefined : body.subarray(start, end + 1)); return;
            }
            const parts = path.split('/'), site = parts[2], operation = parts[3];
            if (parts[1] !== 'api' || !sites.includes(site)) { response.writeHead(404); response.end(); return; }
            let body = '';
            request.setEncoding('utf8');
            request.on('data', chunk => { body += chunk; });
            request.on('end', () => {
              let payload;
              try { payload = body ? JSON.parse(body) : {}; }
              catch { response.writeHead(400); response.end(); return; }
              if (operation === 'home') { json(response, { calls, reads }); return; }
              calls.push({ site, operation, payload });
              if (operation === 'search') {
                json(response, { list: [{ vod_id: site === 'alpha' ? 0 : site, vod_name: 'Native Fixture Film' }],
                  page: 1, pagecount: 1 });
              } else if (operation === 'detail') {
                json(response, { list: [{ vod_id: site === 'alpha' ? 0 : site, vod_name: 'Native Fixture Film',
                  vod_play_from: '原始未知线路/' + site,
                  vod_play_url: 'Native Fixture Film.1080p.mp4$' + site + '-opaque-file' }] });
              } else if (operation === 'play') {
                json(response, { parse: 0, url: 'http://127.0.0.1:9987/proxy/' + site + '/ordinary.mp4',
                  format: 'mp4', watchReport,
                  ...(requireHeader ? { headers: { 'X-Film-Native': 'fixture' } } : {}) });
              } else if (operation === 'report') json(response, { ok: true });
              else { response.writeHead(404); response.end(); }
            });
          });
          server.listen(9987, '127.0.0.1', resolve);
        });
        module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
        """;

    // A transparent diagnostic decorator, NOT a fake runtime. All envelopes and
    // SSE values come unchanged from this exact NodeRuntime. An optional gate
    // delays a completed real receipt; stopping observation disposes only its HTTP iterator.
    internal sealed class RuntimeTraffic(NodeRuntime runtime, NativePlayback native) : IRuntimeClient
    {
        internal sealed class Call(string method, string action, string attempt, Guid session, long pictures)
        {
            internal readonly string Method = method, Action = action, Attempt = attempt;
            internal readonly Guid Session = session;
            internal readonly long Pictures = pictures;
            internal volatile bool Completed;
        }
        internal sealed class ReceiptGate
        {
            internal readonly TaskCompletionSource<FilmMediaReceipt> Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Wait => release.Task;
            internal void Release() => release.TrySetResult();
        }
        private readonly object gate = new();
        private readonly List<Call> calls = [];
        private readonly List<(Guid Session, FilmMediaReceipt Receipt)> receipts = [];
        private readonly Dictionary<string, Guid> opened = new(StringComparer.Ordinal);
        private readonly List<ReceiptGate> gates = [];
        private sealed class Observation
        {
            internal readonly CancellationTokenSource Stop = new();
            internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly Dictionary<Guid, Observation> observations = [];
        private ReceiptGate? next;

        internal ReceiptGate HoldNextReceipt()
        {
            lock (gate)
            {
                Check(next is null, "only one unclaimed receipt gate");
                var value = new ReceiptGate(); next = value; gates.Add(value); return value;
            }
        }
        internal Guid Opened(string targetId) { lock (gate) return opened[targetId]; }
        internal Call[] Commands(string action)
        { lock (gate) return calls.Where(call => call.Method == "film.session.command" && call.Action == action).ToArray(); }
        internal int BusinessCalls { get { lock (gate) return calls.Count; } }
        internal FilmMediaReceipt[] Receipts(Guid id)
        { lock (gate) return receipts.Where(row => row.Session == id).Select(row => row.Receipt).ToArray(); }
        internal void ReleaseGates() { lock (gate) foreach (var value in gates) value.Release(); }
        internal async Task StopEventsAsync(Guid id)
        {
            Observation observed;
            lock (gate)
                observed = observations.TryGetValue(id, out var value) ? value :
                    throw new InvalidOperationException("The real session SSE subscription has not started.");
            await observed.Stop.CancelAsync();
            await observed.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Observe("sse-only-stop", new { sessionId = id, actualHttpDrained = true, runtimeConnected = runtime.IsConnected });
        }

        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var input = parameters is null ? default : FilmJson.Encode(parameters);
            string action = FilmJson.Text(input, "action"), attempt = FilmJson.Text(input, "attemptId");
            _ = Guid.TryParse(FilmJson.Text(input, "sessionId"), out Guid id);
            var call = new Call(method, action, attempt, id, action == "media.started" ? Pictures(attempt) : 0);
            lock (gate) calls.Add(call);
            var reply = await runtime.CallAsync(method, parameters, cancellationToken);
            ReceiptGate? held = null;
            if (FilmJson.Bool(reply, "ok"))
            {
                var value = FilmJson.Member(reply, "value");
                lock (gate)
                {
                    call.Completed = true;
                    if (method == "film.session.open")
                        opened[FilmJson.Text(FilmJson.Member(input, "target"), "id")] =
                            Guid.Parse(FilmJson.RequiredText(value, "sessionId"));
                    if (method == "film.session.attempt")
                    {
                        var receipt = FilmMediaReceipt.Read(value);
                        receipts.Add((id, receipt));
                        held = next; next = null;
                        held?.Arrived.TrySetResult(receipt);
                    }
                }
            }
            if (held is not null) await held.Wait; // Intentionally deliver the already-admitted receipt after cancellation.
            return reply;
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            runtime.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
        public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var observed = new Observation();
            lock (gate) observations.Add(sessionId, observed);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, observed.Stop.Token);
            var iterator = runtime.EventsAsync(module, sessionId, after, linked.Token).GetAsyncEnumerator();
            try
            {
                while (true)
                {
                    bool available;
                    try { available = await iterator.MoveNextAsync(); }
                    catch (OperationCanceledException) when (observed.Stop.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested) { break; }
                    if (!available) break;
                    yield return iterator.Current;
                }
            }
            finally
            {
                try
                {
                    await iterator.DisposeAsync();
                    observed.Drained.TrySetResult();
                }
                catch (Exception error)
                {
                    observed.Drained.TrySetException(error);
                    throw;
                }
                finally
                {
                    lock (gate) observations.Remove(sessionId);
                    observed.Stop.Dispose();
                }
            }
        }
        private long Pictures(string attempt)
        {
            if (native.ObservedVideoOutput is { } output && output.Identity == attempt) return output.DisplayedPictures;
            // The commit callback precedes public observation publication. Observe
            // the exact real prepared/current VLC counter, never Playing or a donor's frame.
            object? session = native.Identity == attempt ? AdaptiveMediaTests.Session(native) : null;
            if (session is null && AdaptiveMediaTests.Field(native, "_runtimeHandoffIntent") is RuntimeMediaPlaybackIntent intent &&
                intent.Identity == attempt) session = AdaptiveMediaTests.Field(native, "_handoffSession");
            return session is null ? 0 :
                ((LibVLCSharp.Shared.Media)AdaptiveMediaTests.Field(session, "Media")!).Statistics.DisplayedPictures;
        }
        internal void Dump()
        {
            Call[] copy;
            lock (gate) copy = calls.ToArray();
            foreach (var call in copy.Where(call => call.Action.StartsWith("media.", StringComparison.Ordinal) ||
                         call.Method is "film.session.release" or "film.session.media.release"))
                Observe("consumer-rpc", new
                {
                    method = call.Method, action = call.Action, sessionId = call.Session,
                    attemptId = call.Attempt, actualPicturesBeforeStarted = call.Pictures, accepted = call.Completed
                });
        }
    }
}
