using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Buffers.Binary;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicQqAccountUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native-owner-png-profile-save-readback-logout", Login);
        yield return Case("png-native-decode-bounds-pixels-only", Image);
        yield return Case("large-png-decode-and-explicit-save-no-byte-quota", LargeImageSave);
        yield return Case("visible-user-challenge-only-poll-hide-cancel-no-background", Visibility);
        yield return Case("pending-generation-hidden-before-png-never-publishes", PendingHidden);
        yield return Case("manual-and-visible-query-single-flight", SingleFlight);
        yield return Case("platform-switch-fences-old-authorization-and-pixels", Switch);
        yield return Case("lost-save-receipt-explicit-reconciliation-not-false-login", Unknown);
        yield return Case("refused-terminal-clears-image", Refused);
        yield return Case("display-expiry-does-not-block-canonical-poll", DisplayExpiry);
        yield return Case("unloaded-cancels-and-context-replacement-cannot-reuse-challenge", Unload);
        yield return Case("minimized-cancels-and-restore-does-not-recreate-challenge", Minimized);
        yield return Case("datacontext-replacement-cancels-old-owner-without-polling-new-owner", ContextReplacement);
    }
    private static TestCase Case(string name, Func<Task> action) => new("music.accounts.qq.ui." + name, () => Sta(action));
    private static void Check(bool condition,
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    { if (!condition) throw new InvalidOperationException($"QQ account assertion at {member}:{line}: {expression}"); }
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception e) { done.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Check(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private static async Task Login()
    {
        await using var f = await Fixture.Create();
        Check(f.Module.CanBeginAccountQr);
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.HasAccountQr && f.Module.AccountQrImage is { IsFrozen: true, PixelWidth: 16 } &&
            f.Module.AccountQrAccessibleName == "QQ 音乐登录二维码");
        await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.SelectedAccount!.State == "verified" &&
            f.Module.SelectedAccount.UserId == "42" && f.Module.AccountStatus == "会话验证成功");
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.State == "verified");
        await f.Module.LogoutAccountAsync(f.Module.SelectedAccount!, confirmed: true);
        Check(f.Module.SelectedAccount.State == "none" && f.Runtime.Calls.Count == 0);
    }
    private static Task Image()
    {
        string png = Convert.ToBase64String(Png());
        var image = MusicAccountQrImage.DecodePng(png);
        Check(image.IsFrozen && image.Format == PixelFormats.Bgra32 && image.PixelWidth == 16);
        byte[] pixels = new byte[16 * 16 * 4]; image.CopyPixels(pixels, 64, 0);
        Check(pixels.Contains((byte)0) && pixels.Contains((byte)255));
        foreach (var text in new[] { "https://attacker.invalid/secret", "data:image/svg+xml,<svg/>", "not-base64",
            Convert.ToBase64String(Png(width: 2048)),
            Convert.ToBase64String(Png(inflated: 1024 * 1024)) })
        {
            try { MusicAccountQrImage.DecodePng(text); throw new InvalidOperationException("Expected image rejection"); }
            catch (Exception e) when (e.Message is "INVALID_RESPONSE" or "LIMIT") { }
        }
        return Task.CompletedTask;
    }
    private static async Task LargeImageSave()
    {
        byte[] png = MusicAccountRuntimeFixture.QrPng(width: 1024, height: 1024, noise: true, chunkBytes: 2048);
        string encoded = Convert.ToBase64String(png);
        Check(png.Length > 524288 && encoded.Length > 699052);
        var image = MusicAccountQrImage.DecodePng(encoded);
        Check(image.IsFrozen && image.PixelWidth == 1024 && image.PixelHeight == 1024);
        await using var f = await Fixture.Create();
        f.Transport.Image = png;
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.CanSaveAccountQr);
        await f.Module.SaveAccountQrAsync();
        Check(f.Module.AccountStatus == "二维码已保存" && new FileInfo(f.Dialogs.Path).Length > 524288);
        using var stream = File.OpenRead(f.Dialogs.Path);
        var saved = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        byte[] expected = new byte[1024 * 1024 * 4], actual = new byte[expected.Length];
        image.CopyPixels(expected, 4096, 0);
        new FormatConvertedBitmap(saved.Frames[0], PixelFormats.Bgra32, null, 0).CopyPixels(actual, 4096, 0);
        Check(actual.AsSpan().SequenceEqual(expected));
    }
    private static async Task Visibility()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            await Task.Delay(2200); Check(f.Transport.Calls == 0); // No idle/background timer requests.
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            await Until(() => f.Transport.Polls == 1);
            window.Hide();
            await Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            int calls = f.Transport.Calls;
            await Task.Delay(2300); Check(f.Transport.Calls == calls);
            window.Show(); await Task.Delay(2200);
            Check(!f.Module.HasAccountQr && f.Transport.Calls == calls && f.Runtime.Calls.Count == 0);
        }
        finally { window.Close(); }
    }
    private static async Task PendingHidden()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "qr.create";
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            var begin = f.Module.BeginAccountQrAsync();
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.Hide(); await begin.WaitAsync(TimeSpan.FromSeconds(3));
            f.Transport.Release.TrySetResult();
            await Until(() => !f.Module.AccountBusy);
            Check(!f.Module.HasAccountQr && f.Store.Writes == 0);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "qr.poll";
        await f.Module.BeginAccountQrAsync();
        var first = f.Module.CheckAccountQrAsync();
        await f.Module.CheckAccountQrAsync(); // Admission is synchronous before command dispatch.
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(f.Transport.Polls == 0 && f.Transport.Calls == 2);
        var cancel = f.Module.CancelAccountAsync();
        f.Transport.Release.TrySetResult(); await Task.WhenAll(first, cancel);
        Check(!f.Module.HasAccountQr && f.Store.Writes == 0);
    }
    private static async Task Switch()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "qr.poll";
        await f.Module.BeginAccountQrAsync();
        var first = f.Module.CheckAccountQrAsync();
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Module.SelectedAccount = f.Module.Accounts[0];
        Check(!f.Module.HasAccountQr);
        f.Transport.Release.TrySetResult(); await first;
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.Platform == "wy" && f.Module.Accounts[1].State == "none" && f.Store.Writes == 0);
    }
    private static async Task Unknown()
    {
        await using var f = await Fixture.Create(); f.Store.LoseReceipt = true;
        await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.AccountStatus.Contains("未确认") && f.Module.SelectedAccount!.State != "verified");
        int calls = f.Transport.Calls;
        await f.Module.CheckAccountQrAsync(); Check(f.Transport.Calls == calls);
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.State == "saved" && f.Store.Writes == 1);
    }
    private static async Task Refused()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 68;
        await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.AccountStatus == "已拒绝登录" && f.Store.Writes == 0);
    }
    private static async Task DisplayExpiry()
    {
        await using var f = await Fixture.Create();
        f.Transport.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        f.Transport.Status = 66;
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.HasAccountQr && !f.Module.CanSaveAccountQr && f.Module.CanCheckAccountQr);
        await f.Module.CheckAccountQrAsync();
        Check(f.Transport.Polls == 1 && f.Module.HasAccountQr && f.Module.AccountStatus == "等待扫码");
        f.Transport.Status = 0;
        await f.Module.CheckAccountQrAsync();
        Check(f.Transport.Polls == 2 && !f.Module.HasAccountQr && f.Module.SelectedAccount!.State == "verified" &&
            f.Module.SelectedAccount.UserId == "42" && f.Store.Writes == 1);
    }
    private static async Task Unload()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            window.Content = new Border();
            await Until(() => !f.Module.HasAccountQr);
            int calls = f.Transport.Calls;
            window.Content = view; await Task.Delay(2300);
            Check(f.Transport.Calls == calls && !f.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static async Task Minimized()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            window.WindowState = WindowState.Minimized;
            await Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            int calls = f.Transport.Calls;
            await Task.Delay(2200); window.WindowState = WindowState.Normal; await Task.Delay(2200);
            Check(f.Transport.Calls == calls && !f.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static async Task ContextReplacement()
    {
        await using var first = await Fixture.Create();
        await using var next = await Fixture.Create();
        first.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = first.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            await first.Module.ActivatePlatformSettingsAsync();
            await next.Module.ActivatePlatformSettingsAsync();
            OpenLogin(view);
            await first.Module.BeginAccountQrAsync();
            await next.Module.BeginAccountQrAsync();
            Check(next.Module.HasAccountQr);
            int firstCancels = first.Calls.AccountCalls.Count(method => method == "music.accounts.cancel");
            int firstReads = first.Calls.AccountCalls.Count(method => method == "music.accounts.list");
            int nextAccountCalls = next.Calls.AccountCalls.Count;
            var nextQr = next.Module.AccountQrImage;
            int nextCalls = next.Transport.Calls;
            view.DataContext = next.Module;
            await Until(() => !first.Module.HasAccountQr && !first.Module.AccountBusy);
            int cancelDelta = first.Calls.AccountCalls.Count(method => method == "music.accounts.cancel") - firstCancels;
            int readDelta = first.Calls.AccountCalls.Count(method => method == "music.accounts.list") - firstReads;
            Check(cancelDelta == 1, $"old cancel delta={cancelDelta}; read delta={readDelta}; old busy={first.Module.AccountBusy}; old qr={first.Module.HasAccountQr}");
            Check(readDelta == 1, $"old read delta={readDelta}; cancel delta={cancelDelta}; next account delta={next.Calls.AccountCalls.Count - nextAccountCalls}");
            int calls = first.Transport.Calls;
            await Task.Delay(2300);
            Check(first.Transport.Calls == calls && next.Transport.Calls == nextCalls && next.Module.HasAccountQr);
            Check(next.Calls.AccountCalls.Count == nextAccountCalls && ReferenceEquals(nextQr, next.Module.AccountQrImage));
        }
        finally { window.Close(); }
    }
    private static Window Offscreen(UIElement view) => new()
    {
        Content = view, Left = -32000, Top = -32000, Width = 500, Height = 700,
        ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
    };
    private static void OpenLogin(MusicAccountsView view) =>
        ((Popup)view.FindName("LoginPopup")).IsOpen = true;
    private static async Task Until(Func<bool> predicate,
        [CallerArgumentExpression(nameof(predicate))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { while (!predicate()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { throw new TimeoutException($"QQ account wait at {member}:{line}: {expression}"); }
    }
    private static byte[] Png(int width = 16, int? inflated = null)
    {
        byte[] pixels = new byte[width * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 8) Array.Fill(pixels, (byte)255, i, Math.Min(4, pixels.Length - i));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, 16, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var output = new MemoryStream(); encoder.Save(output);
        if (inflated is null) return output.ToArray();
        // Keep a valid small IHDR but replace IDAT by excessive decompressed data.
        // The native image boundary must reject it before publishing pixels.
        byte[] original = output.ToArray();
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(new byte[inflated.Value]);
        using var bomb = new MemoryStream(); bomb.Write(original, 0, 8);
        bool wrote = false;
        for (int offset = 8; offset < original.Length;)
        {
            int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(offset, 4)));
            string kind = System.Text.Encoding.ASCII.GetString(original, offset + 4, 4);
            if (kind != "IDAT") bomb.Write(original, offset, size + 12);
            else if (!wrote)
            {
                byte[] data = compressed.ToArray(), chunk = new byte[data.Length + 12];
                BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
                "IDAT"u8.CopyTo(chunk.AsSpan(4)); data.CopyTo(chunk, 8);
                uint crc = uint.MaxValue;
                foreach (byte b in chunk.AsSpan(4, data.Length + 4))
                {
                    crc ^= b;
                    for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
                }
                BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
                bomb.Write(chunk); wrote = true;
            }
            offset += size + 12;
        }
        return bomb.ToArray();
    }
    private sealed class QrScenario
    {
        internal int Calls, Polls, Status;
        internal byte[]? Image;
        internal long ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds();
        internal string? HoldAt;
        internal readonly TaskCompletionSource Arrived = MusicAccountRuntimeFixture.Signal(), Release = MusicAccountRuntimeFixture.Signal();
        internal async Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            Calls++;
            if (HoldAt == call.Operation) { Arrived.TrySetResult(); await Release.Task; }
            if (call.Operation == "qr.create") return MusicPlatformRuntimeFixture.Json(new {
                challengeId = call.Parameters.GetProperty("challengeId").GetString(),
                expiresAt = ExpiresAt,
                url = "",
                imageBase64 = Convert.ToBase64String(Image ?? Png()) });
            if (call.Operation == "qr.poll")
            {
                Polls++;
                var account = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("tx").GetRawText())!.AsObject();
                account["cookie"] = "musicid=42; musickey=SYNTHETIC_QQ_KEY";
                return MusicPlatformRuntimeFixture.Json(new { status = Status switch { 66 => "waiting", 68 => "refused", _ => "authenticated" },
                    account = Status == 0 ? account : null });
            }
            throw new InvalidOperationException(call.Operation);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal NativeMusicScriptTestRuntime Runtime = new();
        internal QrScenario Transport = new();
        internal MusicAccountRuntimeFixture.StoreProbe Store;
        internal MusicNativeApplication Application;
        internal MusicAccountFixtureApplication Calls;
        internal MusicCapability Module;
        internal Dialogs Dialogs;
        private Fixture()
        {
            string root = TestWorkspace.DataPath("qq-account-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Store = new(new LocalDocumentStore(root, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            Runtime.Platform.OnRequest = Transport.Read;
            Application = new(Store, MusicTestStores.For(Store), Runtime);
            Calls = new(Application);
            Dialogs = new(System.IO.Path.Combine(root, "explicit-qr.png"));
            Module = new(Calls, Store, new Output(), Dialogs);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try
            {
                await f.Module.InitializeAsync(default);
                // Account fixtures stay on a local page instead of refreshing an unrelated home.
                await f.Module.NavigateAsync("favorites", default);
                f.Module.SelectedAccount = f.Module.Accounts[1]; return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult();
            try { await Module.DisposeAsync(); Check(Runtime.Calls.Count == 0); }
            finally { await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }
    private sealed class Dialogs(string path) : IMusicDialogs, IMusicAccountQrSaveDialog
    {
        internal string Path { get; } = path;
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
        public string? SelectQrPngPath() => Path;
    }
    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId => null;
        public string? Identity => null;
        public bool HasMedia => false;
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public int Volume { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token) => throw new InvalidOperationException();
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(double value) => Task.CompletedTask;
    }
}
