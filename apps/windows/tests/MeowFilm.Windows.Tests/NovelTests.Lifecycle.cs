using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;
using Microsoft.Web.WebView2.Core;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private sealed record DialogSurface(PlayerDialogHost Host, FrameworkElement Panel)
    {
        internal bool IsAttached => Host.Children.Contains(Panel);
    }
    private static DialogSurface ObserveDialog(DependencyObject child)
    {
        var host = PlayerDialogHost.Find(child) ?? throw new InvalidOperationException("Dialog has no application owner.");
        var panel = host.Children.OfType<FrameworkElement>().Single(element => DesktopVisual.Contains(element, child));
        return new(host, panel);
    }
    private static Task DialogClosed(DialogSurface surface)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long observation = 0;
        observation = surface.Host.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => CheckClosed());
        CheckClosed(); return closed.Task;
        void CheckClosed()
        {
            if (surface.IsAttached) return;
            surface.Host.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, observation);
            closed.TrySetResult();
        }
    }
    private static void ClickDialog(DialogSurface surface, string label) =>
        DesktopTestUi.Invoke(Find<Button>(surface.Panel).Single(b => b.Content as string == label && DesktopVisual.IsVisible(b)));

    private static async Task DialogClosure()
    {
        var owner = new StackPanel(); var host = NovelTestUi.Window(owner); NovelUi.Theme(DesktopTestUi.WindowRoot(host)); host.Show(); await NovelTestUi.LoadedAsync(owner);
        try
        {
            foreach (string mode in new[] { "manual", "automatic", "automatic-failure" })
            {
                using var cancellation = new CancellationTokenSource();
                var body = new TextBlock { Text = "合成确认内容" };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var dialog = NovelUi.DialogAsync(owner, "生命周期确认", body, "确认", _ =>
                {
                    entered.TrySetResult();
                    return mode == "manual" ? Task.FromResult(true) : release.Task;
                }, cancellation.Token, autoSubmit: mode != "manual");
                await NovelTestUi.LoadedAsync(body);
                var popup = ObserveDialog(body); var closed = DialogClosed(popup);
                try
                {
                    if (mode == "manual")
                    {
                        await Until(() => Find<Button>(popup.Panel).Any(b => b.Content as string == "确认" && DesktopVisual.IsVisible(b)));
                        ClickDialog(popup, "确认");
                    }
                    else
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        if (mode == "automatic-failure") release.SetException(new IOException("Synthetic automatic submission failure."));
                        else release.SetResult(true);
                    }
                    Check(closed.IsCompleted || !dialog.IsCompleted, mode + ": dialog returned before actual Closed.");
                    if (mode == "automatic-failure")
                    {
                        try { await dialog; throw new InvalidOperationException("Automatic failure became success."); }
                        catch (IOException e) { Check(e.Message == "Synthetic automatic submission failure."); }
                    }
                    else Check(await dialog, mode + ": successful dialog was not accepted.");
                    Check(closed.IsCompleted && !popup.IsAttached && popup.Panel.Parent is null, mode + ": incomplete popup retirement.");
                    await DesktopTestUi.DrainAsync();
                }
                finally { cancellation.Cancel(); release.TrySetResult(true); }
            }
        }
        finally { host.Close(); }
    }

    private static async Task DialogCancellation()
    {
        foreach (string mode in new[] { "cancel", "escape", "token", "owner-hidden", "owner-closed" })
        {
            var owner = new StackPanel(); var host = NovelTestUi.Window(owner); NovelUi.Theme(DesktopTestUi.WindowRoot(host)); host.Show(); await NovelTestUi.LoadedAsync(owner);
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var body = new TextBlock { Text = "等待合成提交释放" };
            var dialog = NovelUi.DialogAsync(owner, "取消提交", body, "确认", Save, cancellation.Token);
            await NovelTestUi.LoadedAsync(body);
            var popup = ObserveDialog(body); var closed = DialogClosed(popup);
            try
            {
                await Until(() => Find<Button>(popup.Panel).Any(b => b.Content as string == "确认" && DesktopVisual.IsVisible(b)));
                ClickDialog(popup, "确认"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                switch (mode)
                {
                    case "cancel": ClickDialog(popup, "取消"); break;
                    case "escape":
                        Check(popup.Host.HandleEscape(VirtualKey.Escape), "real dialog owner accepts Escape"); break;
                    case "token": cancellation.Cancel(); break;
                    case "owner-hidden": owner.Visibility = Visibility.Collapsed; break;
                    case "owner-closed": host.Close(); break;
                }
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await closed.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!dialog.IsCompleted, mode + ": dialog acknowledged cancellation before save physically ended.");
                physical.SetResult();
                Check(!await dialog, mode + ": cancelled save's late success was accepted.");
                Check(popup.Panel.Parent is null && !popup.IsAttached, mode + ": popup remained attached.");
                await DesktopTestUi.DrainAsync();
            }
            finally { physical.TrySetResult(); cancellation.Cancel(); host.Close(); }
            async Task<bool> Save(CancellationToken token)
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await physical.Task; return true;
            }
        }
    }

    private static async Task DialogIsolation()
    {
        var owner = new StackPanel(); var host = NovelTestUi.Window(owner); NovelUi.Theme(DesktopTestUi.WindowRoot(host)); host.Show(); await NovelTestUi.LoadedAsync(owner);
        using var oldCancellation = new CancellationTokenSource();
        using var currentCancellation = new CancellationTokenSource();
        try
        {
            int saves = 0;
            var firstBody = new TextBlock { Text = "旧确认" };
            var first = NovelUi.DialogAsync(owner, "旧确认", firstBody, "确认", _ =>
            { saves++; return Task.FromResult(true); }, oldCancellation.Token);
            await NovelTestUi.LoadedAsync(firstBody);
            var oldPopup = ObserveDialog(firstBody); var oldClosed = DialogClosed(oldPopup);
            await Until(() => Find<Button>(oldPopup.Panel).Any(b => b.Content as string == "取消" && DesktopVisual.IsVisible(b)));
            ClickDialog(oldPopup, "取消"); Check(!await first && saves == 0 && oldClosed.IsCompleted);
            var nextBody = new TextBlock { Text = "当前确认" };
            var next = NovelUi.DialogAsync(owner, "当前确认", nextBody, "确认", _ =>
            { saves++; return Task.FromResult(true); }, currentCancellation.Token);
            await NovelTestUi.LoadedAsync(nextBody);
            var popup = ObserveDialog(nextBody); var closed = DialogClosed(popup);
            oldCancellation.Cancel(); await DesktopTestUi.DrainAsync();
            Check(popup.IsAttached && !next.IsCompleted && saves == 0, "Retired dialog cancelled or submitted the next confirmation.");
            await Until(() => Find<Button>(popup.Panel).Any(b => b.Content as string == "确认" && DesktopVisual.IsVisible(b)));
            ClickDialog(popup, "确认"); Check(await next && closed.IsCompleted && saves == 1);
            await DesktopTestUi.DrainAsync();
        }
        finally { currentCancellation.Cancel(); host.Close(); }
    }

    private static async Task DialogSaveFailures()
    {
        foreach (string mode in new[] { "io", "document", "cancel" })
        {
            await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
            var owner = new StackPanel(); var host = NovelTestUi.Window(owner); NovelUi.Theme(DesktopTestUi.WindowRoot(host)); host.Show(); await NovelTestUi.LoadedAsync(owner);
            store.Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var physical = store.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var body = new TextBlock { Text = "已接纳的本地保存" };
            var dialog = NovelUi.DialogAsync(owner, "保存排版", body, "保存", async ct =>
            { await library.SaveAppearanceAsync(new() { FontSize = 28 }, ct); return true; }, cancellation.Token);
            await NovelTestUi.LoadedAsync(body);
            var popup = ObserveDialog(body); var closed = DialogClosed(popup);
            try
            {
                await Until(() => Find<Button>(popup.Panel).Any(b => b.Content as string == "保存" && DesktopVisual.IsVisible(b)));
                ClickDialog(popup, "保存"); await store.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ClickDialog(popup, "取消"); await closed.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!dialog.IsCompleted && library.Appearance.FontSize == 22 && store.Writes == 1,
                    mode + ": closed dialog did not wait for the accepted write.");
                store.WriteFailure = mode switch
                {
                    "io" => new IOException("Synthetic accepted write failure"),
                    "document" => new DocumentStoreException(DocumentStoreError.Storage),
                    _ => new OperationCanceledException()
                };
                physical.TrySetResult();
                if (mode == "cancel") Check(!await dialog, "Cancellation was reported as a save failure.");
                else
                {
                    Exception? failure = null;
                    try { await dialog; } catch (Exception error) { failure = error; }
                    Check(ReferenceEquals(failure, store.WriteFailure), mode + ": closed dialog lost the real write failure.");
                }
                Check(library.Appearance.FontSize == 22 && store.Bytes is null && popup.Panel.Parent is null && !popup.IsAttached);
            }
            finally { physical.TrySetResult(); cancellation.Cancel(); host.Close(); await library.PrepareShutdownAsync(); }
        }
        await using var retryStore = new MemoryStore(); var retryLibrary = new NovelLibrary(retryStore); await retryLibrary.InitializeAsync();
        var retryOwner = new StackPanel(); var retryHost = NovelTestUi.Window(retryOwner); NovelUi.Theme(DesktopTestUi.WindowRoot(retryHost)); retryHost.Show(); await NovelTestUi.LoadedAsync(retryOwner);
        using var retryCancellation = new CancellationTokenSource();
        retryStore.FailWrite = true;
        var retryBody = new TextBlock { Text = "保留输入与原位重试" };
        var retry = NovelUi.DialogAsync(retryOwner, "保存排版", retryBody, "保存", async ct =>
        { await retryLibrary.SaveAppearanceAsync(new() { FontSize = 28 }, ct); return true; }, retryCancellation.Token);
        await NovelTestUi.LoadedAsync(retryBody);
        var retryPopup = ObserveDialog(retryBody); var retryClosed = DialogClosed(retryPopup);
        try
        {
            await Until(() => Find<Button>(retryPopup.Panel).Any(b => b.Content as string == "保存" && DesktopVisual.IsVisible(b)));
            var content = retryPopup.Panel; ClickDialog(retryPopup, "保存");
            await Until(() => Find<TextBlock>(retryPopup.Panel).Any(t => t.Text == "操作失败，请检查输入或重试" && DesktopVisual.IsVisible(t)) &&
                Find<Button>(retryPopup.Panel).Single(b => b.Content as string == "保存").IsEnabled);
            Check(!retry.IsCompleted && !retryClosed.IsCompleted && retryPopup.IsAttached &&
                ReferenceEquals(content, retryPopup.Panel) && ReferenceEquals(ObserveDialog(retryBody).Panel, retryPopup.Panel) &&
                retryLibrary.Appearance.FontSize == 22 && retryStore.Bytes is null);
            retryStore.FailWrite = false; ClickDialog(retryPopup, "保存");
            Check(await retry && retryClosed.IsCompleted && retryPopup.Panel.Parent is null &&
                retryLibrary.Appearance.FontSize == 28 && retryStore.Writes == 2);
            var reopened = new NovelLibrary(retryStore); await reopened.InitializeAsync();
            Check(reopened.Appearance.FontSize == 28);
        }
        finally { retryCancellation.Cancel(); retryHost.Close(); await retryLibrary.PrepareShutdownAsync(); }
    }

    private static async Task DialogFailureOwners()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        foreach (string owner in new[] { "view", "settings" })
        foreach (bool cancel in new[] { false, true })
        {
            await using var store = new MemoryStore(); var runtime = new Runtime();
            await using var module = new NovelCapability(NovelTestSources.Create(runtime), new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
            await module.InitializeAsync(default);
            FrameworkElement view = owner == "view" ? module.View : module.SettingsView;
            var root = new Grid();
            root.Children.Add(view);
            var dialogHost = new PlayerDialogHost();
            root.Children.Add(dialogHost);
            var host = NovelTestUi.Window(root);
            dialogHost.Attach(host);
            host.Show(); await NovelTestUi.LoadedAsync(view);
            var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (owner == "settings")
                    await Until(() => Find<TextBlock>(view).Any(text => text.Text == runtime.Records.Single().Name));
                store.Hold = physical; store.Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (owner == "settings")
                    DesktopTestUi.Invoke(Find<Button>(view).Single(b => b.Content as string == "阅读排版"));
                else module.RunUi(async ct =>
                {
                    await NovelUi.DialogAsync(view, "本地写入", new TextBlock(), "保存", async token =>
                    { await module.Library.SaveAppearanceAsync(new() { FontSize = 28 }, token); return true; }, ct);
                });
                DialogSurface? popup = null;
                if (owner == "settings")
                {
                    await Until(() => Find<Button>(dialogHost).Any(button => button.Content as string == "保存" && button.IsLoaded));
                    DesktopTestUi.Invoke(Find<Button>(dialogHost).Single(b => b.Content as string == "保存"));
                    await store.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    DesktopTestUi.Invoke(Find<Button>(dialogHost).Single(b => b.Content as string == "取消"));
                    await Until(() => !dialogHost.IsOpen);
                }
                else
                {
                    await Until(() => DialogItems<Button>(view).Any(b => b.Content as string == "保存" && DesktopVisual.IsVisible(b)));
                    popup = ObserveDialog(DialogItems<Button>(view).Single(b => b.Content as string == "保存" && DesktopVisual.IsVisible(b)));
                    var closed = DialogClosed(popup); ClickDialog(popup, "保存");
                    await store.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5)); ClickDialog(popup, "取消");
                    await closed.WaitAsync(TimeSpan.FromSeconds(5));
                }
                Check(module.Notice.Length == 0 && store.Writes == 1 && !physical.Task.IsCompleted && module.Library.Appearance.FontSize == 22);
                store.WriteFailure = cancel ? new OperationCanceledException() : new DocumentStoreException(DocumentStoreError.Storage);
                physical.TrySetResult();
                if (!cancel)
                    await Until(() => module.Notice == (owner == "settings" ? "小说设置操作失败，请重试" : "小说数据保存失败，原记录已保留"));
                else
                {
                    if (owner == "settings") await ((NovelSettingsView)module.SettingsView).DrainAsync();
                    else await module.PrepareShutdownAsync();
                    await DesktopTestUi.DrainAsync();
                    Check(module.Notice.Length == 0, owner + ": cancellation became a visible write failure.");
                }
                Check(module.Library.Appearance.FontSize == 22 && store.Bytes is null &&
                    (owner == "settings" ? !dialogHost.IsOpen : popup is null || !popup.IsAttached));
            }
            finally { physical.TrySetResult(); host.Close(); await module.PrepareShutdownAsync(); }
        }
    }

    private static async Task WebLifecycle()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mode in new[] { "success", "failure", "token", "owner-closed" })
        {
            var owner = new StackPanel(); var host = NovelTestUi.Window(owner); NovelUi.Theme(DesktopTestUi.WindowRoot(host)); host.Show(); await NovelTestUi.LoadedAsync(owner);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var operation = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "rule", request = new
            {
                url = "https://lifecycle.invalid/fixture",
                html = "<html><head><link rel='icon' href='data:,'></head><body>合成动态页面</body></html>",
                script = "new Promise((resolve,reject) => { window.finishFixture = () => " +
                    (mode == "failure" ? "reject(new Error('synthetic failure'))" : "resolve('completed')") + "; })"
            } }), cancellation.Token);
            CoreWebView2Environment? environment = null; bool exited = false, returnedAtExit = false;
            try
            {
                await Until(() => DialogItems<WebView2>(owner).Any(w => w.CoreWebView2 is not null));
                var web = DialogItems<WebView2>(owner).Single(); var popup = ObserveDialog(web); var closed = DialogClosed(popup);
                environment = web.CoreWebView2.Environment; string profile = environment.UserDataFolder;
                environment.BrowserProcessExited += Exited;
                Check(Directory.Exists(profile) && profiles.Add(profile), "Web interaction reused or lost its private profile.");
                while (await web.CoreWebView2.ExecuteScriptAsync("typeof window.finishFixture").AsTask().WaitAsync(cancellation.Token) != "\"function\"")
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await DesktopTestUi.DrainAsync();
                }
                if (mode == "success")
                {
                    await Preview((FrameworkElement)popup.Panel, "novel-web-lifecycle", 640, 520);
                    string path = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT") ?? TestWorkspace.DataPath("novel-previews");
                    TestWorkspace.ValidatePath(TestWorkspace.DataRoot, path); Directory.CreateDirectory(path);
                    using var image = File.Create(Path.Combine(path, "novel-web-lifecycle-browser.png"));
                    await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image.AsRandomAccessStream());
                }
                if (mode == "token") cancellation.Cancel();
                else if (mode == "owner-closed") host.Close();
                else await web.CoreWebView2.ExecuteScriptAsync("window.finishFixture()");
                if (mode is "token" or "owner-closed")
                {
                    try { await operation; throw new InvalidOperationException(mode + ": cancelled Web returned a result."); }
                    catch (OperationCanceledException) { }
                }
                else if (mode == "failure")
                {
                    try { await operation; throw new InvalidOperationException("Failed Web script returned a result."); }
                    catch (InvalidDataException e) { Check(e.Message == "WEB_SCRIPT_FAILED"); }
                }
                else Check(Json((await operation)!).GetProperty("value").GetString() == "completed");
                Check(closed.IsCompleted && exited && !returnedAtExit && !Directory.Exists(profile),
                    mode + ": Web returned before actual Closed, whole browser-group exit, or profile deletion.");
                Check(!DialogItems<WebView2>(owner).Any(), mode + ": WebView remained attached.");
                await DesktopTestUi.DrainAsync();
            }
            finally
            {
                if (environment is not null) environment.BrowserProcessExited -= Exited;
                cancellation.Cancel(); host.Close();
                if (!operation.IsCompleted)
                {
                    try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { }
                }
            }
            void Exited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
            { exited = true; returnedAtExit = operation.IsCompleted; }
        }
    }
}
