using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("passive account UI uses explicit selection, validates and saves without echo", async () =>
        {
            var runtime = new Application();
            await using var module = Create(runtime);
            Check(runtime.Calls.Count == 0);
            Check(module.SelectedAccount is null);
            module.SelectedAccount = module.Accounts[1];
            await module.ReadAccountsAsync();
            Check(module.SelectedAccount!.Platform == "tx" && module.SelectedAccount.State == "none");
            await module.ImportAccountAsync("fixture_session=synthetic-only");
            Check(module.SelectedAccount.State == "verified");
            Check(module.SelectedAccount.DisplayName == "合成账号");
            Check(module.AccountStatus == "会话验证成功");
            Check(runtime.Calls.Last().Method == "music.accounts.import");
            Check(runtime.Calls.Last().Parameters.GetProperty("platform").GetString() == "tx");
            Check(!module.AccountStatus.Contains("synthetic", StringComparison.Ordinal));
        });
        yield return Test("logout requires confirmation and leaves local song rows unchanged", async () =>
        {
            var runtime = new Application(); var dialogs = new Dialogs();
            await using var module = Create(runtime, dialogs);
            module.SelectedAccount = module.Accounts[0];
            await module.ImportAccountAsync("fixture=x");
            await module.NavigateAsync("favorites", default);
            var local = module.Songs.Single();
            dialogs.Accept = false; await module.LogoutAccountAsync();
            Check(runtime.Calls.Count(call => call.Method == "music.accounts.logout") == 0);
            dialogs.Accept = true; await module.LogoutAccountAsync();
            Check(module.SelectedAccount!.State == "none");
            Check(module.Songs.Count == 1 && ReferenceEquals(local, module.Songs[0]));
        });
        yield return Test("cancel fences noncooperative validation result and sends explicit platform cancellation", async () =>
        {
            var runtime = new Application { HoldImport = true };
            await using var module = Create(runtime);
            module.SelectedAccount = module.Accounts[0];
            var task = module.ImportAccountAsync("fixture=x");
            await runtime.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(module.AccountBusy);
            await module.CancelAccountAsync(); await task;
            runtime.Reply.TrySetResult(BridgeCodec.Value(new { ok = true, account = Application.Account("wy", "verified") }));
            await Task.Delay(10);
            Check(module.SelectedAccount!.State == "none"); // Explicit cancel reconciles durable result.
            Check(module.AccountStatus == "已取消");
            Check(runtime.Calls.Count(call => call.Method == "music.accounts.cancel") == 1);
            Check(runtime.Calls.Count(call => call.Method == "music.accounts.list") == 1);
        });
        yield return Test("malformed account response preserves rows and sanitizes errors", async () =>
        {
            var runtime = new Application { InvalidList = true };
            await using var module = Create(runtime);
            module.SelectedAccount = module.Accounts[2];
            await module.ReadAccountsAsync();
            Check(module.SelectedAccount!.State == "unknown");
            Check(module.AccountStatus.Length > 0);
            Check(module.Accounts.Count == 3);
            await module.ImportAccountAsync("bad\r\ncookie");
            Check(runtime.Calls.All(call => call.Method != "music.accounts.import"));
        });
        yield return Test("unknown account commit blocks ordinary shutdown until explicit reconciliation", async () =>
        {
            var runtime = new Application { UnknownImport = true };
            var module = Create(runtime);
            module.SelectedAccount = module.Accounts[0];
            await module.ImportAccountAsync("fixture=x");
            Check(module.AccountStatus.Contains("未确认", StringComparison.Ordinal));
            await module.ReadAccountsAsync(); // Explicit read reconciles durable outcome, never replay import.
            Check(runtime.Calls.Count(call => call.Method == "music.accounts.import") == 1);
            await module.PrepareShutdownAsync();
            await module.DisposeAsync();
        });
        yield return Test("unreconciled account commit is not hidden by successful queue flush", async () =>
        {
            var runtime = new Application { UnknownImport = true };
            var module = Create(runtime);
            module.SelectedAccount = module.Accounts[0]; await module.ImportAccountAsync("fixture=x");
            try { await module.PrepareShutdownAsync(); throw new Exception("Expected failed preparation"); }
            catch (Exception error) when (error.Message == "COMMIT_UNKNOWN") { }
            try { await module.DisposeAsync(); } catch (Exception error) when (error.Message == "COMMIT_UNKNOWN") { }
        });
        yield return Test("account view has masked nonpersistent input and standalone resources", async () =>
        {
            var runtime = new Application(); await using var module = Create(runtime);
            var view = new MusicAccountsView { DataContext = module };
            view.Measure(new Size(700, 600)); view.Arrange(new Rect(0, 0, 700, 600)); view.UpdateLayout();
            var platforms = (ItemsControl)view.FindName("PlatformSettingsList");
            var code = (PasswordBox)view.FindName("SmsCodeInput");
            Check(platforms.Items.Count == 3 && code.MaxLength == 8);
            Check(runtime.Calls.Count == 0);
        });
    }
    private static MusicCapability Create(Application runtime, Dialogs? dialogs = null) =>
        new(runtime, new NoStore(), new NoPlayback(), dialogs ?? new Dialogs());
    private static TestCase Test(string name, Func<Task> body) => new("music-account-ui: " + name, () => Sta(body));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Account UI assertion"); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Sta(Func<Task> body)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await body(); completed.TrySetResult(); }
                catch (Exception error) { completed.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)); Check(thread.Join(3000));
    }
    private sealed class Application : MusicApplicationTestDouble
    {
        public bool HoldImport, UnknownImport, InvalidList;
        public readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<JsonElement> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, JsonElement Parameters)> Calls { get; } = [];
        public static object Account(string platform, string state) => new
        {
            platform, state, generation = 1,
            profile = state == "verified" ? new { platform, userId = "42", displayName = "合成账号" } : null
        };
        public override Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var dto = BridgeCodec.Value(parameters ?? new { }); Calls.Add((method, dto));
            if (method == "music.accounts.import" && HoldImport) { Arrived.TrySetResult(); return Reply.Task; }
            if (method == "music.accounts.import" && UnknownImport)
                return Task.FromResult(BridgeCodec.Value(new { ok = false, code = "COMMIT_UNKNOWN" }));
            if (method == "music.accounts.list")
                return Task.FromResult(BridgeCodec.Value(new { ok = true,
                    items = InvalidList ? Array.Empty<object>() : new[] { Account("wy", "none"), Account("tx", "none"), Account("kg", "none") } }));
            if (method is "music.accounts.import" or "music.accounts.validate" or "music.accounts.logout")
                return Task.FromResult(BridgeCodec.Value(new { ok = true,
                    account = Account(dto.GetProperty("platform").GetString()!, method.EndsWith("logout", StringComparison.Ordinal) ? "none" : "verified") }));
            if (method == "music.library.detail")
                return Task.FromResult(BridgeCodec.Value(new { ok = true, offset = 0, limit = 100, total = 1,
                    collection = new { id = "favorites", name = "我的收藏", kind = "favorites", revision = 0, count = 1 },
                    songs = new[] { MusicSongFixture.Local("local-fixture", "合成曲") } }));
            return Task.FromResult(BridgeCodec.Value(new { ok = true }));
        }
    }
    private sealed class NoStore : IProtectedDocumentStore
    {
        public Task<ProtectedDocument> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No document operation expected");
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No document operation expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Accept = true;
        public bool Confirm(string message, string title) => Accept;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class NoPlayback : IMusicPlayback
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
        public Task PauseAsync() => throw new InvalidOperationException();
        public Task ResumeAsync() => throw new InvalidOperationException();
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(double seconds) => throw new InvalidOperationException();
    }
}
