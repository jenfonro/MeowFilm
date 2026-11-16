using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Shared synthetic HTTP, native dispatcher and real Film window fixtures.
internal static class NativeMediaFixtures
{
    internal static object? Field(object obj, string name) => obj.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(obj);
    internal static async Task<IAsyncDisposable> ShowFilmAsync(FilmPlayback player)
    {
        var window = new Window
        {
            Title = "Meowfilm scoped film validation", Width = 640, Height = 400,
            Content = player.VideoView, ShowActivated = false
        };
        var owner = new FilmWindow(player, window);
        try
        {
            window.Show(); window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (!window.IsVisible || !player.VideoView.IsLoaded ||
                player.VideoView.ActualWidth <= 1 || player.VideoView.ActualHeight <= 1)
                throw new InvalidOperationException("Film fixture window did not load at a renderable size");
            return owner;
        }
        catch { await owner.DisposeAsync(); throw; }
    }
    private sealed class FilmWindow(FilmPlayback player, Window window) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await player.DisposeAsync(); }
            finally { window.Content = null; window.Close(); }
        }
    }
    internal static async Task Sta(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await body(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        finally { if (!thread.Join(3000)) throw new InvalidOperationException("Media fixture STA did not exit"); }
    }
    internal sealed record Reply(byte[] Data, string Mime = "application/vnd.apple.mpegurl")
    {
        internal int Status { get; init; } = 200;
        internal string? Location { get; init; }
        internal bool BlockBody { get; init; }
        internal bool IgnoreRange { get; init; }
        internal static Reply Redirect(string location) => new([]) { Status = 302, Location = location };
    }
    internal sealed record Request(string Path, bool Authorized, bool AnyCallerHeader, bool Range);
    internal sealed class Origin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _handlers = [];
        private readonly Task _accept;
        internal string Credential { get; set; } = "Fixture " + Guid.NewGuid().ToString("N");
        internal Dictionary<string, string> Headers => new()
        {
            ["Authorization"] = Credential, ["Cookie"] = "synthetic=" + Credential[8..],
            ["Referer"] = Base.AbsoluteUri, ["X-Fixture"] = "synthetic-only"
        };
        internal Uri Base { get; }
        internal Uri Uri(string path) => new(Base, path);
        internal readonly ConcurrentQueue<Request> Requests = new();
        internal Func<string, Reply> Route = _ => new Reply([]) { Status = 404 };
        internal bool RequireAuthorization = true;
        internal IReadOnlyDictionary<string, string>? RequiredHeaders;
        internal int Blocked;
        internal Origin()
        {
            _listener.Start();
            Base = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _accept = Accept();
        }
        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var task = Handle(client);
                    lock (_handlers) _handlers.Add(task);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    using var registration = _stop.Token.Register(client.Dispose);
                    var bytes = new byte[32768]; int count = 0;
                    while (count < bytes.Length)
                    {
                        int n = await stream.ReadAsync(bytes.AsMemory(count), _stop.Token); if (n == 0) return;
                        count += n; if (bytes.AsSpan(0, count).IndexOf("\r\n\r\n"u8) >= 0) break;
                    }
                    string[] lines = Encoding.ASCII.GetString(bytes, 0, count).Split("\r\n");
                    string path = lines[0].Split(' ')[1];
                    var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
                        .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    bool authorized = headers.GetValueOrDefault("Authorization") == Credential;
                    if (RequiredHeaders is { } required)
                        authorized &= required.All(pair => headers.GetValueOrDefault(pair.Key) == pair.Value);
                    Requests.Enqueue(new(path, authorized,
                        new[] { "Authorization", "Cookie", "Referer", "X-Fixture" }.Any(headers.ContainsKey),
                        headers.ContainsKey("Range")));
                    Reply reply = RequireAuthorization && !authorized ? new Reply([]) { Status = 401 } : Route(path);
                    int status = reply.Status; int start = 0, end = reply.Data.Length - 1;
                    string rangeHeader = "";
                    if (status == 200 && !reply.IgnoreRange && headers.TryGetValue("Range", out var range))
                    {
                        string[] parts = range[6..].Split('-'); start = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        if (parts[1].Length > 0) end = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        end = Math.Min(end, reply.Data.Length - 1); status = 206;
                        rangeHeader = $"Content-Range: bytes {start}-{end}/{reply.Data.Length}\r\n";
                    }
                    int length = Math.Max(0, end - start + 1);
                    string head = $"HTTP/1.1 {status} Fixture\r\nContent-Type: {reply.Mime}\r\nContent-Length: {length}\r\nETag: \"fixture-v1\"\r\n" +
                        rangeHeader + (reply.Location is null ? "" : "Location: " + reply.Location + "\r\n") + "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                    if (reply.BlockBody) { Interlocked.Increment(ref Blocked); await Task.Delay(Timeout.Infinite, _stop.Token); }
                    if (length > 0) await stream.WriteAsync(reply.Data.AsMemory(start, length), _stop.Token);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _accept;
            Task[] handlers; lock (_handlers) handlers = _handlers.ToArray();
            await Task.WhenAll(handlers);
            var released = new TcpListener(IPAddress.Loopback, Base.Port);
            try { released.Start(); } finally { released.Stop(); }
            _stop.Dispose();
        }
    }
}
