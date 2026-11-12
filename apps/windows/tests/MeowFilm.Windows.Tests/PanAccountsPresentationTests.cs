using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.PanAccountFixture;

namespace MeowFilm.Windows.Tests;

public static class PanAccountsPresentationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("three-column table binds exact states and actions at wide and narrow widths", Table);
        yield return Case("real login button uses shared chrome, masked fields and replacement save binding", Manual);
        yield return Case("real QR controls switch methods retry and close while cancelling challenges", Qr);
        yield return Case("hide minimize unload and owner replacement cancel login without leaking a new popup", Lifetime);
        yield return Case("logout confirmation binds revision, cancellation and exact clear action", Logout);
        yield return Case("film settings exposes the real pan owner and retires it on section exit", Entry);
    }
    private static TestCase Case(string name, Func<Task> run) =>
        new("pan-accounts-ui: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static async Task Table()
    {
        await using var scene = await Scene.Create(runtime =>
        {
            runtime.Account("baidu", 1, "signedIn", "cookie");
            runtime.Account("quark", 2, "expired", "cookie");
            runtime.Overrides["pan.refresh"] = (parameters, _) =>
            {
                string id = parameters.GetProperty("provider").GetString()!;
                return Task.FromResult(id == "quark" ? Failure("AUTH_FAILED") :
                    Ok(new { refreshed = true, account = runtime.Accounts[id] }));
            };
        });
        var header = (Grid)scene.View.FindName("AccountTableHeader");
        Check(header.ColumnDefinitions.Count == 3 &&
            header.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(["平台", "状态", "操作"]),
            "table has exactly platform/status/action columns");
        var cells = Visuals(scene.View).OfType<Grid>().Where(grid => grid.Name == "AccountCells").ToArray();
        Check(cells.Length == 7 && cells.All(grid => grid.ColumnDefinitions.Count == 3), "all seven rows share the three-column contract");
        Check(VisibleActions(scene.View, scene.Model.Rows[0]).SequenceEqual(["刷新", "退出登录"]), "signed-in row actions bind");
        Check(VisibleActions(scene.View, scene.Model.Rows[1]).SequenceEqual(["重新登录", "退出登录"]), "expired row actions bind");
        Check(VisibleActions(scene.View, scene.Model.Rows[2]).SequenceEqual(["登录"]), "signed-out row action binds");
        Check(Visuals(scene.View).OfType<Image>().Count() == 0, "music icons and account profile are not reused");
        Check(scene.Notices.Contains("网盘凭据已失效") &&
            !Visuals(scene.View).OfType<TextBlock>().Any(text => text.Text == "网盘凭据已失效"), "errors route through NoticeOutlet rather than table descriptions");
        foreach (bool contrast in new[] { false, true, false })
        {
            DesktopTheme.SetIsHighContrast(scene.View, contrast); await Drain();
            var surface = Visuals(scene.View).OfType<Border>().First(border =>
                ReferenceEquals(border.Style, scene.View.FindResource("Settings.ListSurface")));
            if (contrast) Check(((SolidColorBrush)surface.Background).Color == SystemColors.WindowColor, "high contrast reaches the reused table surface");
        }
        GeneralAppearanceTests.SavePreview(scene.View, "pan-accounts-wide", 900, 620);
        scene.Window.Width = 390; scene.Window.Height = 440; await Drain();
        var scroll = (ScrollViewer)scene.View.FindName("TableScroll");
        Check(scroll.ViewportWidth < ((Grid)scene.View.FindName("AccountTable")).ActualWidth &&
            scroll.ScrollableWidth > 0 && scene.View.ActualWidth <= 390, "narrow width scrolls inside the table without dropping columns");
        GeneralAppearanceTests.SavePreview(scene.View, "pan-accounts-narrow", 374, 400);
        Check(scene.BindingErrors.ToString().Length == 0, "table paths and template bindings resolve without WPF errors");
    }
    private static async Task Manual()
    {
        await using var scene = await Scene.Create();
        var row = scene.Model.Rows.Single(row => row.Provider == "tianyi");
        Click(Action(scene.View, row, "登录")); await Drain();
        var popup = (Popup)scene.View.FindName("LoginPopup");
        var frame = (AccountLoginSurface)scene.View.FindName("LoginSurface");
        var fields = (StackPanel)scene.View.FindName("CredentialInputs");
        Check(popup.IsOpen && !scene.Model.QrMode && frame.Title == row.Name &&
            ReferenceEquals(frame.Style, scene.View.FindResource("Account.LoginSurface")), "manual button reaches the real shared login surface");
        Check(fields.Children.OfType<PasswordBox>().Select(box => ((PanCredentialField)box.Tag).Id).SequenceEqual(["cookie", "password"]) &&
            fields.Children.OfType<TextBox>().Single().Tag is PanCredentialField { Id: "username" }, "secrets use PasswordBox; username is a normal input");
        foreach (var input in fields.Children.OfType<PasswordBox>()) Check(input.Password.Length == 0 && input.MaxLength == 0, "old secrets are never read back and no credential length limit is added");
        var password = fields.Children.OfType<PasswordBox>().Single(box => ((PanCredentialField)box.Tag).Id == "password");
        password.Password = "  synthetic password  ";
        fields.Children.OfType<TextBox>().Single().Text = "synthetic-user";
        bool exact = false;
        scene.Runtime.Overrides["pan.settings.set"] = (parameters, _) =>
        {
            var value = parameters.GetProperty("credentials");
            exact = value.EnumerateObject().Select(field => field.Name).SequenceEqual(["username", "password"]) &&
                value.GetProperty("password").GetString() == "  synthetic password  ";
            scene.Runtime.Account("tianyi", 1, "signedOut", "username", "password");
            return Task.FromResult(Ok(scene.Runtime.Accounts["tianyi"]));
        };
        Click((Button)scene.View.FindName("SaveCredentials"));
        await Until(() => !row.IsBusy && !popup.IsOpen);
        Check(exact && row.Status == "signedIn" && fields.Children.OfType<PasswordBox>().All(box => box.Password.Length == 0),
            "save replaces exactly supplied values, preserves spaces, verifies, closes and clears draft");
        Check(scene.BindingErrors.ToString().Length == 0, "manual controls resolve owner bindings");
    }
    private static async Task Qr()
    {
        await using var scene = await Scene.Create(runtime =>
        {
            runtime.ChallengeType = "image"; runtime.Challenge = PanAccountsTests.PixelDataUri;
        });
        Click(Action(scene.View, scene.Model.Rows[0], "登录"));
        await Until(() => scene.Model.QrImage is not null);
        var popup = (Popup)scene.View.FindName("LoginPopup");
        var frame = (AccountLoginSurface)scene.View.FindName("LoginSurface");
        Check(popup.IsOpen && Visuals(frame).OfType<Image>().Single().Source == scene.Model.QrImage, "image challenge binds actual detached pixels");
        Click((Button)scene.View.FindName("ManualModeButton"));
        await Until(() => !scene.Model.QrMode && scene.Runtime.Cancelled.Count == 1);
        var fields = (StackPanel)scene.View.FindName("CredentialInputs");
        Check(fields.Children.OfType<PasswordBox>().Single().Tag is PanCredentialField { Id: "cookie" }, "QR manual method exposes a hidden Cookie input");
        fields.Children.OfType<PasswordBox>().Single().Password = "synthetic-cookie";
        Click((Button)scene.View.FindName("QrModeButton"));
        await Until(() => scene.Model.QrImage is not null);
        Check(fields.Children.Count == 0, "method switch removes and clears the old draft");
        Click(Visuals(frame).OfType<Button>().Single(button => Equals(button.Content, "刷新二维码")));
        await Until(() => scene.Runtime.Starts == 3 && scene.Model.QrImage is not null);
        Check(scene.Runtime.Cancelled.Count == 2, "real retry retires the previous challenge");
        DesktopTheme.SetIsHighContrast(scene.View, true); await Drain();
        var close = Visuals(frame).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "关闭登录");
        Check(close.IsEnabled, "shared close remains usable in high contrast");
        Click(close);
        await Until(() => !popup.IsOpen && scene.Runtime.Cancelled.Count == 3);
        Check(!scene.Model.LoginOpen && scene.Model.QrImage is null, "shared chrome close cleans up login");
        Check(scene.BindingErrors.ToString().Length == 0, "QR image mode and shared popup bindings resolve");
    }
    private static async Task Lifetime()
    {
        await using var scene = await Scene.Create();
        for (int action = 0; action < 4; action++)
        {
            Click(Action(scene.View, scene.Model.Rows[0], "登录"));
            await Until(() => scene.Model.QrImage is not null);
            int before = scene.Runtime.Cancelled.Count;
            if (action == 0) scene.View.Visibility = Visibility.Collapsed;
            else if (action == 1) scene.Window.WindowState = WindowState.Minimized;
            else if (action == 2) scene.Window.Content = null;
            else scene.View.DataContext = new PanAccounts(new PanAccountFixture());
            await Until(() => !scene.Model.LoginOpen && scene.Runtime.Cancelled.Count == before + 1);
            Check(!((Popup)scene.View.FindName("LoginPopup")).IsOpen, "surface retirement closes the actual popup");
            if (action == 0) scene.View.Visibility = Visibility.Visible;
            else if (action == 1) scene.Window.WindowState = WindowState.Normal;
            else if (action == 2) scene.Window.Content = scene.View;
            else
            {
                var replacement = (PanAccounts)scene.View.DataContext;
                scene.View.DataContext = scene.Model;
                await replacement.DisposeAsync();
            }
            await Until(() => scene.Model.Rows[0].CanLogin);
        }
        Check(scene.BindingErrors.ToString().Length == 0, "owner replacement never leaves stale popup bindings");
    }
    private static async Task Logout()
    {
        await using var scene = await Scene.Create(runtime => runtime.Account("baidu", 1, "signedIn", "cookie"));
        var row = scene.Model.Rows[0];
        Click(Action(scene.View, row, "退出登录")); await Drain();
        var popup = (Popup)scene.View.FindName("LogoutPopup");
        Check(popup.IsOpen, "logout requires the same confirmation interaction as music accounts");
        Click((Button)scene.View.FindName("CancelLogout"));
        Check(!popup.IsOpen && row.Revision == 1, "cancel does not clear credentials");
        Click(Action(scene.View, row, "退出登录")); await Drain();
        Click(Visuals(popup.Child).OfType<Button>().Single(button => Equals(button.Content, "确定")));
        await Until(() => !row.IsBusy && row.Revision == 2);
        Check(row.Status == "signedOut" && !row.Configured &&
            scene.Runtime.Calls.Count(call => call.Method == "pan.settings.clear") == 1, "confirm binds exactly one revision-advancing clear");
        scene.Runtime.Account("baidu", 3, "expired", "cookie");
        scene.Runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure("AUTH_FAILED"));
        await scene.Model.ReloadAsync(); await Drain();
        Click(Action(scene.View, row, "退出登录")); await Drain();
        scene.Runtime.Account("baidu", 4, "expired", "cookie");
        await scene.Model.ReloadAsync(); await Drain();
        Check(!popup.IsOpen && popup.PlacementTarget is null, "confirmation for an older revision is retired");
    }
    private static async Task Entry()
    {
        var runtime = new PanAccountFixture();
        await using var store = new QualityFixture.DocumentStore();
        runtime.Overrides["film.rules.get"] = (_, _) => Task.FromResult(FilmRuntimeProbe.Ok(
            JsonSerializer.SerializeToElement(new { rules = new FilmRuleSet(), revision = "fixture-rules" }, Json)));
        await using var rules = new FilmRuleService(runtime);
        await using var sources = new FilmSourceService(runtime, store);
        var view = new FilmSettingsView(rules, sources);
        var navigation = (StackPanel)view.FindName("SettingsNavigation");
        var entry = navigation.Children.OfType<RadioButton>().Single(button => Equals(button.Content, "网盘账号"));
        entry.IsChecked = true;
        var window = TestWindow(view);
        try
        {
            window.Show(); await Drain();
            var pan = (PanAccountsView)view.FindName("PanPage");
            var model = (PanAccounts)pan.DataContext;
            await Until(() => model.Rows.Count == 7 && !model.IsLoading);
            Check(pan.IsVisible && !((Grid)view.FindName("BusinessPage")).IsVisible &&
                runtime.Calls.Any(call => call.Method == "pan.providers"), "film settings instantiates the pan owner using its existing runtime");
            Click(Action(pan, model.Rows[0], "登录"));
            await Until(() => model.QrImage is not null);
            navigation.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "rules")).IsChecked = true;
            await Until(() => runtime.Cancelled.Count == 1 && !model.LoginOpen);
            Check(!((Popup)pan.FindName("LoginPopup")).IsOpen && store.Writes == 0, "leaving the settings category cancels QR and creates no second credential store");
        }
        finally
        {
            window.Content = null; window.Close(); await Drain();
            await view.PreparePlaybackSettingsShutdownAsync();
        }
    }
    private static string[] VisibleActions(DependencyObject view, PanAccountRow row) =>
        Visuals(view).OfType<Button>().Where(button => ReferenceEquals(button.DataContext, row) && button.IsVisible)
            .Select(button => (string)button.Content).ToArray();
    private static Button Action(DependencyObject view, PanAccountRow row, string content) =>
        Visuals(view).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, row) &&
            button.IsVisible && Equals(button.Content, content));
    private static void Click(Button button)
    {
        Check(button.IsEnabled, "action must be enabled");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Drain()
    {
        for (int i = 0; i < 5; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Pan UI did not settle");
            await Task.Delay(10); await Drain();
        }
        await Drain();
    }
    private static Window TestWindow(FrameworkElement content) => new()
    {
        Content = content, Width = 900, Height = 680, Left = -10000, Top = -10000,
        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
    };
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly PanAccountFixture Runtime = new();
        internal readonly PanAccounts Model;
        internal readonly PanAccountsView View;
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        internal readonly StringWriter BindingErrors = new();
        private readonly TextWriterTraceListener listener;
        private readonly SourceLevels previous;
        private Scene(Action<PanAccountFixture>? configure)
        {
            configure?.Invoke(Runtime);
            Model = new(Runtime);
            View = new() { DataContext = Model };
            Window = TestWindow(View);
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            {
                if (args.Message.Length > 0) Notices.Add(args.Message);
            }));
            listener = new(BindingErrors);
            previous = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        }
        internal static async Task<Scene> Create(Action<PanAccountFixture>? configure = null)
        {
            var scene = new Scene(configure);
            try
            {
                scene.Window.Show();
                await Until(() => scene.Model.Rows.Count == 7 && !scene.Model.IsLoading);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Window.Content = null; Window.Close(); await Drain();
            await Model.DisposeAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previous;
            listener.Dispose(); BindingErrors.Dispose();
        }
    }
}
