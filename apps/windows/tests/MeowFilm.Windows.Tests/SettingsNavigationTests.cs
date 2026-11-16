using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
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
            () => MusicQualityConsumerTests.Sta(Navigate));
        yield return new("settings composition: App restoration is independent of the view and offline native settings stay editable while a real execution request fails",
            () => MusicQualityConsumerTests.Sta(OfflineRules));
        yield return new("settings layout: real novel managers stay compact below shell categories with shared white search at supported widths",
            () => MusicQualityConsumerTests.Sta(NovelShellLayout));
    }

    private static async Task OfflineRules()
    {
        string path = TestWorkspace.DataPath("film-settings-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        await using var store = new LocalDocumentStore(path, AppIdentity.NativeNamespaces,
            AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
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
        await module.CancelShutdownPreparationAsync();
        await rules.InitializeAsync();
        Check(starts == 1 && rules.GetSavedSnapshot().MovieRules[0].Pattern == "离线规则");
    }

    private static async Task Navigate()
    {
        var music = new Module("music", "音乐");
        var film = new Module("film", "影视");
        var novel = new Module("novel", "小说");
        await using var appearance = new AppAppearance(new QualityFixture.DocumentStore());
        await appearance.LoadAsync();
        var shell = new ShellWindow([music, film, novel], [], () => Task.CompletedTask, () => Task.CompletedTask, appearance: appearance)
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            shell.Show();
            await Drain();
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Drain();
            var header = (DesktopPageHeader)shell.FindName("PageHeaderHost");
            var slot = (ContentControl)header.FindName("LeadingSlot");
            var tabs = ((Panel)slot.Content).Children.OfType<RadioButton>().ToArray();
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "通用", "音乐", "影视", "小说" }));
            Check(tabs[0].IsChecked == true && !music.SettingsView.IsVisible && !film.SettingsView.IsVisible);
            Check(film.Initializations == 0);
            tabs[1].IsChecked = true;
            await Drain();
            Check(music.SettingsView.IsVisible && !film.SettingsView.IsVisible);
            ((TextBox)music.SettingsView).Text = "未保存草稿";
            int navigations = music.Navigations;
            tabs[2].IsChecked = true;
            await Drain();
            Check(film.SettingsView.IsVisible && !music.SettingsView.IsVisible && film.Initializations == 1);
            ((TextBox)film.SettingsView).Text = "影视草稿";
            tabs[1].IsChecked = true;
            await Drain();
            Check(music.SettingsView.IsVisible && ((TextBox)music.SettingsView).Text == "未保存草稿");
            tabs[2].IsChecked = true;
            await Drain();
            Check(((TextBox)film.SettingsView).Text == "影视草稿" && film.Initializations == 1);
            tabs[3].IsChecked = true;
            await Drain();
            Check(novel.SettingsView.IsVisible && !film.SettingsView.IsVisible && novel.Initializations == 1);
            ((TextBox)novel.SettingsView).Text = "小说排版草稿";
            tabs[2].IsChecked = true;
            await Drain();
            Check(((TextBox)film.SettingsView).Text == "影视草稿" && !novel.SettingsView.IsVisible);
            tabs[0].IsChecked = true;
            await Drain();
            Check(!film.SettingsView.IsVisible && !music.SettingsView.IsVisible);
            var before = shell.Background;
            await appearance.SaveAsync("#A0D0C0");
            Check(!ReferenceEquals(shell.Background, before));
            GeneralAppearanceTests.SavePreview((FrameworkElement)shell.Content, "general-shell", 1110, 820);
            ((Button)shell.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Drain();
            Check(music.View.IsVisible && !film.SettingsView.IsVisible && !music.SettingsView.IsVisible);
            Check(music.Navigations == navigations);
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
        await using var appearance = new AppAppearance(new QualityFixture.DocumentStore());
        await appearance.LoadAsync();
        var shell = new ShellWindow([new Module("music", "音乐"), new Module("film", "影视"), novel], [],
            () => novel.DisposeAsync().AsTask(), () => novel.DisposeAsync().AsTask(), appearance: appearance)
        {
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        var root = (FrameworkElement)shell.Content;
        var settings = (NovelSettingsView)novel.SettingsView;
        string? previous = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT",
                previous ?? TestWorkspace.DataPath("novel-management-previews"));
            DesktopTheme.SetIsHighContrast(shell, false);
            shell.Show();
            await Ready(() => shell.IsLoaded && ((Button)shell.FindName("SettingsButton")).IsEnabled);
            ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var header = (DesktopPageHeader)shell.FindName("PageHeaderHost");
            var slot = (ContentControl)header.FindName("LeadingSlot");
            await Ready(() => slot.IsVisible && slot.Content is Panel);
            var categories = (Panel)slot.Content;
            var areas = categories.Children.OfType<RadioButton>().ToArray();
            Check(areas.Select(area => area.Content).SequenceEqual(new[] { "通用", "音乐", "影视", "小说" }));
            areas.Single(area => Equals(area.Content, "小说")).IsChecked = true;
            await Ready(() => settings.IsLoaded && settings.IsVisible && sources.Sources.Count == 1);
            foreach (int width in new[] { 860, 1110 })
            {
                // Match the existing shell fixture's fixed viewport, without changing the desktop.
                shell.MinWidth = shell.MaxWidth = width;
                shell.MinHeight = shell.MaxHeight = 820;
                shell.Width = width; shell.Height = 820;
                foreach (var (title, marker, name) in new[]
                {
                    ("书源管理", runtime.Records.Single().Name, "sources"),
                    ("替换净化", "净化广告", "rules")
                })
                {
                    NovelTests.Find<RadioButton>(settings).Single(area => Equals(area.Content, title)).IsChecked = true;
                    await Ready(() => settings.IsArrangeValid && Math.Abs(root.ActualWidth - width) < 1 &&
                        Math.Abs(root.ActualHeight - 820) < 1 && NovelTests.Find<TextBlock>(settings).Any(text =>
                            text.IsLoaded && text.IsVisible && text.IsArrangeValid && text.Text == marker));
                    var subpages = NovelTests.Find<RadioButton>(settings).ToArray();
                    var manager = NovelTests.Find<ManagementList>(settings).Single();
                    var items = NovelTests.Find<ListBox>(manager).Single();
                    var reading = NovelTests.Find<Button>(settings).Single(button => Equals(button.Content, "阅读排版"));
                    var categoryBounds = Bounds(categories);
                    var pageBounds = Bounds(settings);
                    var subpageBounds = Bounds(subpages[0]);
                    var searchBounds = Bounds(manager.Search);
                    var listBounds = Bounds(items);
                    GeneralAppearanceTests.SavePreview(root, $"novel-settings-shell-{name}-{width}", width, 820);
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
                        "search tools, reading settings and shell caption must not overlap navigation");
                    foreach (var group in new[] { areas, subpages })
                    {
                        Verify(group.All(area => ReferenceEquals(area.Style, area.FindResource("MeowFilm.SettingsArea")) &&
                            area.FontWeight == FontWeights.Normal), "both navigation levels use the original regular shared style");
                        for (int i = 1; i < group.Length; i++)
                            Verify(Bounds(group[i - 1]).Right <= Bounds(group[i]).Left + 1,
                                "adjacent navigation labels must not overlap");
                    }
                    foreach (var control in areas.Cast<FrameworkElement>().Concat(subpages)
                        .Append(manager.Search).Append(manager.Tools).Append(reading).Append(items))
                    {
                        var bounds = Bounds(control);
                        Verify(control.IsVisible && bounds.Width > 0 && bounds.Height > 0 &&
                            bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 &&
                            bounds.Bottom <= root.ActualHeight + 1, "navigation and manager controls must fit the real shell viewport");
                    }
                    var searchChrome = (Border)VisualTreeHelper.GetChild(manager.Search, 0);
                    Verify(ReferenceEquals(manager.Search.Style, manager.Search.FindResource("MeowFilm.ManagementSearch")) &&
                        ReferenceEquals(manager.Search.Background, manager.Search.FindResource("MeowFilm.Brush.Surface")) &&
                        manager.Search.Background is SolidColorBrush background && background.Color == Colors.White &&
                        ReferenceEquals(searchChrome.Background, manager.Search.Background),
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
            shell.Close();
            await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            Check(shell.CleanupCompleted && !shell.IsVisible);
        }
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        async Task Ready(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                shell.UpdateLayout();
                await Drain();
            } while (!condition());
        }
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 5; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Settings navigation assertion failed");
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
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
