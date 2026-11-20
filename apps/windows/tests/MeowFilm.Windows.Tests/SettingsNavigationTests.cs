using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using MeowFilm.Capabilities;
using MeowFilm.AppHost;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class SettingsNavigationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("settings navigation: category opens owner view and Back restores origin without replay",
            () => TestUiHost.InvokeAsync(Navigate));
        yield return new("settings composition: App restoration is independent of the view and offline native settings stay editable while a real execution request fails",
            () => TestUiHost.InvokeAsync(OfflineRules));
        yield return new("settings layout: real novel managers stay compact below shell categories with shared white search at supported widths",
            () => TestUiHost.InvokeAsync(NovelShellLayout));
    }

    private static async Task OfflineRules()
    {
        string path = TestWorkspace.DataPath("film-settings-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        await using var store = new NativeDatabase(path);
        int starts = 0;
        await using var runtime = new AppRuntimeConnectionHost(_ =>
        {
            starts++;
            throw new InvalidOperationException("Synthetic Node startup failure");
        });
        var rules = new FilmRuleService(runtime, store);
        var sources = new FilmSourceService(runtime, store);
        await using var module = new FilmCapability(rules, sources, runtime);
        await module.InitializeAsync(default);
        Check(starts == 0 && !module.SettingsView.IsLoaded);
        await rules.InitializeAsync();
        var draft = rules.GetSavedSnapshot() with { MovieRules = [new("离线规则")] };
        await rules.SaveAsync(draft);
        Check(starts == 0 && rules.GetSavedSnapshot().MovieRules[0].Pattern == "离线规则");
        runtime.Start();
        await Task.WhenAll(module.StartSourcesAsync(), module.StartSourcesAsync());
        Check(starts == 1 && sources.RuntimeError is not null && !module.SettingsView.IsLoaded);
        await rules.InitializeAsync();
        Check(starts == 1 && rules.GetSavedSnapshot().MovieRules[0].Pattern == "离线规则");
        try { await rules.ProcessAsync(new(FilmRuleSection.Movie, "规则")); throw new Exception("Failed Node executed rules"); }
        catch (BridgeException error) { Check(error.Code == "INTERNAL" && starts == 1); }
        await module.PrepareShutdownAsync();
        try { await rules.ProcessAsync(new(FilmRuleSection.Movie, "规则")); throw new Exception("Prepared settings accepted work"); }
        catch (InvalidOperationException) { }
        await module.DisposeAsync();
        Check(starts == 1, "retirement cannot restart the shared runtime");
    }

    private static async Task Navigate()
    {
        var music = new Module("music", "音乐");
        var film = new Module("film", "影视");
        var novel = new Module("novel", "小说");
        await using var appearance = new AppAppearance(new QualityFixture.Database());
        await appearance.LoadAsync();
        var shell = new ShellWindow([music, film, novel], [], () => Task.CompletedTask, appearance: appearance)
        {
        };
        try
        {
            shell.Show();
            await Drain();
            DesktopTestUi.Invoke((Button)DesktopTestUi.WindowRoot(shell).FindName("SettingsButton"));
            await Drain();
            var header = (DesktopPageHeader)DesktopTestUi.WindowRoot(shell).FindName("PageHeaderHost");
            var slot = (ContentControl)header.FindName("LeadingSlot");
            var tabs = ((Panel)slot.Content).Children.OfType<RadioButton>().ToArray();
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "通用", "音乐", "影视", "小说" }), "the shell exposes each category in order");
            Check(tabs[0].IsChecked == true && !DesktopVisual.IsVisible(music.SettingsView) && !DesktopVisual.IsVisible(film.SettingsView), "opening general settings leaves module settings hidden");
            Check(film.Initializations == 0, "opening general settings does not initialize an unrelated module");
            tabs[1].IsChecked = true;
            await Ready(shell, () => DesktopVisual.IsVisible(music.SettingsView) && !DesktopVisual.IsVisible(film.SettingsView), "music settings become visible");
            ((TextBox)music.SettingsView).Text = "未保存草稿";
            int navigations = music.Navigations;
            tabs[2].IsChecked = true;
            await Ready(shell, () => DesktopVisual.IsVisible(film.SettingsView) && !DesktopVisual.IsVisible(music.SettingsView) && film.Initializations == 1, "film settings replace music settings and initialize once");
            ((TextBox)film.SettingsView).Text = "影视草稿";
            tabs[1].IsChecked = true;
            await Ready(shell, () => DesktopVisual.IsVisible(music.SettingsView) && ((TextBox)music.SettingsView).Text == "未保存草稿", "music settings return with their draft");
            tabs[2].IsChecked = true;
            await Ready(shell, () => DesktopVisual.IsVisible(film.SettingsView) && ((TextBox)film.SettingsView).Text == "影视草稿" && film.Initializations == 1, "film settings return with their draft and existing initialization");
            tabs[3].IsChecked = true;
            await Ready(shell, () => DesktopVisual.IsVisible(novel.SettingsView) && !DesktopVisual.IsVisible(film.SettingsView) && novel.Initializations == 1, "novel settings become the only visible module settings");
            ((TextBox)novel.SettingsView).Text = "小说排版草稿";
            tabs[2].IsChecked = true;
            await Ready(shell, () => DesktopVisual.IsVisible(film.SettingsView) && ((TextBox)film.SettingsView).Text == "影视草稿" && !DesktopVisual.IsVisible(novel.SettingsView), "film settings return from novel settings");
            tabs[0].IsChecked = true;
            await Ready(shell, () => !DesktopVisual.IsVisible(film.SettingsView) && !DesktopVisual.IsVisible(music.SettingsView), "general settings hide every module settings view");
            var before = ((Grid)DesktopTestUi.WindowRoot(shell)).Background;
            await appearance.SaveAsync("#A0D0C0");
            Check(!ReferenceEquals(((Grid)DesktopTestUi.WindowRoot(shell)).Background, before));
            await GeneralAppearanceTests.SavePreview((FrameworkElement)shell.Content, "general-shell", 1110, 820);
            DesktopTestUi.Invoke((Button)DesktopTestUi.WindowRoot(shell).FindName("BackButton"));
            await Ready(shell, () => DesktopVisual.IsVisible(music.View) && !DesktopVisual.IsVisible(film.SettingsView) && !DesktopVisual.IsVisible(music.SettingsView), "Back restores the original music page");
            Check(music.Navigations == navigations, "Back does not replay module navigation");
        }
        finally
        {
            shell.Close();
            await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task NovelShellLayout()
    {
        var runtime = new NovelTests.Runtime();
        var sources = NovelTestSources.Create(runtime);
        await using var store = new NovelTests.MemoryStore();
        var library = new NovelLibrary(store);
        await library.InitializeAsync();
        await library.SaveRulesAsync([new NovelRule { Id = "shell-rule", Name = "净化广告", Group = "正文", Pattern = "广告" }]);
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var novel = new NovelCapability(sources, library, new NovelRuleProgram(ruleRuntime));
        await using var appearance = new AppAppearance(new QualityFixture.Database());
        await appearance.LoadAsync();
        var shell = new ShellWindow([new Module("music", "音乐"), new Module("film", "影视"), novel], [],
            () => novel.DisposeAsync().AsTask(), appearance: appearance)
        {
        };
        var root = (FrameworkElement)shell.Content;
        var settings = (NovelSettingsView)novel.SettingsView;
        string? previous = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT",
                previous ?? TestWorkspace.DataPath("novel-management-previews"));
            DesktopTheme.SetIsHighContrast(DesktopTestUi.WindowRoot(shell), false);
            shell.Show();
            await Ready(() => DesktopTestUi.WindowRoot(shell).IsLoaded && ((Button)DesktopTestUi.WindowRoot(shell).FindName("SettingsButton")).IsEnabled);
            DesktopTestUi.Invoke((Button)DesktopTestUi.WindowRoot(shell).FindName("SettingsButton"));
            var header = (DesktopPageHeader)DesktopTestUi.WindowRoot(shell).FindName("PageHeaderHost");
            var slot = (ContentControl)header.FindName("LeadingSlot");
            await Ready(() => DesktopVisual.IsVisible(slot) && slot.Content is Panel);
            var categories = (Panel)slot.Content;
            var areas = categories.Children.OfType<RadioButton>().ToArray();
            Check(areas.Select(area => area.Content).SequenceEqual(new[] { "通用", "音乐", "影视", "小说" }));
            areas.Single(area => Equals(area.Content, "小说")).IsChecked = true;
            await Ready(() => settings.IsLoaded && DesktopVisual.IsVisible(settings) && sources.Sources.Count == 1);
            foreach (int width in new[] { 860, 1110 })
            {
                // Match the existing shell fixture's fixed viewport, without changing the desktop.
                shell.Resize(width, 820);
                foreach (var (title, marker, name) in new[]
                {
                    ("书源管理", runtime.Records.Single().Name, "sources"),
                    ("替换净化", "净化广告", "rules")
                })
                {
                    NovelTests.Find<RadioButton>(settings).Single(area => Equals(area.Content, title)).IsChecked = true;
                    await Ready(() => root.ActualWidth > 0 && root.ActualHeight > 0 && NovelTests.Find<TextBlock>(settings).Any(text =>
                            text.IsLoaded && DesktopVisual.IsVisible(text) && text.Text == marker));
                    var subpages = NovelTests.Find<RadioButton>(settings).ToArray();
                    var manager = NovelTests.Find<ManagementList>(settings).Single();
                    var items = NovelTests.Find<ListView>(manager).Single();
                    var reading = NovelTests.Find<Button>(settings).Single(button => Equals(button.Content, "阅读排版"));
                    var categoryBounds = Bounds(categories);
                    var pageBounds = Bounds(settings);
                    var subpageBounds = Bounds(subpages[0]);
                    var searchBounds = Bounds(manager.Search);
                    var listBounds = Bounds(items);
                    await GeneralAppearanceTests.SavePreview(root, $"novel-settings-shell-{name}-{width}", width, 820);
                    Verify(areas[^1].IsChecked == true && subpages.Select(area => area.Content)
                        .SequenceEqual(new[] { "书源管理", "替换净化" }) &&
                        Equals(subpages.Single(area => area.IsChecked == true).Content, title),
                        "the shell and real novel subpage own the visible manager");
                    Verify(Math.Abs(pageBounds.Top - Bounds(header).Bottom) <= 1 &&
                        Math.Abs(subpageBounds.Top - pageBounds.Top) <= 1 &&
                        subpageBounds.Top - categoryBounds.Bottom is >= -1 and <= 20,
                        "global categories and novel subpages must meet without a blank band or overlap");
                    Verify(searchBounds.Top - subpageBounds.Bottom is >= -1 and <= 10 &&
                        listBounds.Top - searchBounds.Bottom is >= -1 and <= 20,
                        "subpages, search and the actual list must retain compact nonoverlapping gaps");
                    Verify(searchBounds.Right <= Bounds(manager.Tools).Left + 1 &&
                        Bounds(subpages[^1]).Right <= Bounds(reading).Left + 1 &&
                        categoryBounds.Right <= Bounds(header).Right + 1,
                        $"search tools, reading settings and shell caption must not overlap navigation " +
                        $"(root={root.ActualWidth}x{root.ActualHeight}, categories={categoryBounds}, subpageEnd={Bounds(subpages[^1])}, " +
                        $"reading={Bounds(reading)}, search={searchBounds}, tools={Bounds(manager.Tools)}, header={Bounds(header)}).");
                    foreach (var group in new[] { areas, subpages })
                    {
                        Verify(group.All(area => ReferenceEquals(area.Style, DesktopVisual.Resource(area, "MeowFilm.SettingsArea")) &&
                            area.FontWeight == Microsoft.UI.Text.FontWeights.Normal), "both navigation levels use the original regular shared style");
                        for (int i = 1; i < group.Length; i++)
                            Verify(Bounds(group[i - 1]).Right <= Bounds(group[i]).Left + 1,
                                "adjacent navigation labels must not overlap");
                    }
                    foreach (var control in areas.Cast<FrameworkElement>().Concat(subpages)
                        .Append(manager.Search).Append(manager.Tools).Append(reading).Append(items))
                    {
                        var bounds = Bounds(control);
                        Verify(DesktopVisual.IsVisible(control) && bounds.Width > 0 && bounds.Height > 0 &&
                            bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 &&
                            bounds.Bottom <= root.ActualHeight + 1, "navigation and manager controls must fit the real shell viewport");
                    }
                    Verify(ReferenceEquals(manager.Search.Style, DesktopVisual.Resource(manager.Search, "MeowFilm.ManagementSearch")) &&
                        ReferenceEquals(manager.Search.Background, DesktopVisual.Resource(manager.Search, "MeowFilm.Brush.Surface")) &&
                        manager.Search.Background is SolidColorBrush background && background.Color == Colors.White &&
                        DesktopTestUi.Visuals(manager.Search).OfType<Border>()
                            .Any(border => ReferenceEquals(border.Background, manager.Search.Background)),
                        "the real manager must render the original shared white search chrome");

                    void Verify(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException($"Novel shell {width}/{title}: {message}");
                    }
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", previous);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed += (_, _) => closed.TrySetResult();
            shell.Close();
            await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(shell.CleanupCompleted && !shell.IsVisible);
        }
        Rect Bounds(FrameworkElement element) => DesktopTestUi.Bounds(element, root);
        async Task Ready(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                DesktopTestUi.WindowRoot(shell).UpdateLayout();
                await Drain();
            }
        }
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 5; i++) await DesktopTestUi.DrainAsync();
    }
    private static async Task Ready(ShellWindow shell, Func<bool> condition, string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            DesktopTestUi.WindowRoot(shell).UpdateLayout();
            await Drain();
            if (timeout.IsCancellationRequested) throw new TimeoutException($"Settings navigation did not complete: {operation}");
        }
    }
    private static void Check(bool condition, string message = "Settings navigation assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class Module(string id, string title) : IDesktopModule
    {
        public string Id => id;
        public string Title => title;
        public string Version => "1";
        public ObservableCollection<NavigationItem> Navigation { get; } = [new("home", title, "\uE80F")];
        public FrameworkElement View { get; } = new TextBlock { Text = title };
        public FrameworkElement SettingsView { get; } = new TextBox();
        public FrameworkElement? PlayerView => null;
        public int Navigations { get; private set; }
        public int Initializations { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken token) { Initializations++; return Task.CompletedTask; }
        public Task NavigateAsync(string route, CancellationToken token) { Navigations++; return Task.CompletedTask; }
        public Task SearchAsync(string query, CancellationToken token) => Task.CompletedTask;
        public string CaptureNavigationLocation() => "home";
        public Task RestoreNavigationAsync(string location, CancellationToken token) => Task.CompletedTask;
        public Task PrepareShutdownAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
