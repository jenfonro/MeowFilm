using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.AppHost;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Platform.Scripting;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class SettingsNavigationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("settings navigation: category opens owner view and Back restores origin without replay",
            () => MusicQualityConsumerTests.Sta(Navigate));
        yield return new("settings composition: App restoration is independent of the view and failed Node leaves native rules usable",
            () => MusicQualityConsumerTests.Sta(OfflineRules));
    }

    private static async Task OfflineRules()
    {
        string path = TestWorkspace.DataPath("film-settings-composition");
        Directory.CreateDirectory(path);
        await using var store = new LocalDocumentStore(path,
            [FilmRuleService.DocumentNamespace, FilmSourceService.DocumentNamespace]);
        int starts = 0;
        await using var runtime = new AppRuntimeClient(_ =>
        {
            starts++;
            throw new InvalidOperationException("Synthetic Node startup failure");
        });
        var rules = new FilmRuleService(new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), store);
        var sources = new FilmSourceService(runtime, store);
        await using var module = new FilmCapability(rules, sources);
        await module.InitializeAsync(default);
        Check(starts == 0 && !module.SettingsView.IsLoaded);
        runtime.Start();
        await Task.WhenAll(module.StartSourcesAsync(), module.StartSourcesAsync());
        Check(starts == 1 && sources.RuntimeError is not null && !module.SettingsView.IsLoaded);
        await rules.InitializeAsync();
        var saved = await rules.SaveAsync(new FilmRuleSet
        {
            MovieRules = [new("^(.+)$", "$1")]
        });
        var request = new FilmRuleRequest(FilmRuleSection.Movie, "本地规则");
        var result = await rules.ProcessAsync(request);
        Check(result.Matched && result.OutputText == "本地规则");
        Check((await rules.ProcessAsync(request, saved)).OutputText == result.OutputText);
        await module.PrepareShutdownAsync();
        try
        {
            await rules.ProcessAsync(request);
            throw new Exception("Prepared settings accepted work");
        }
        catch (InvalidOperationException) { }
        await module.CancelShutdownPreparationAsync();
        Check((await rules.ProcessAsync(request)).OutputText == "本地规则");
        Check(starts == 1); // Resume does not silently recover/replay a failed runtime.
    }

    private static async Task Navigate()
    {
        var music = new Module("music", "音乐");
        var film = new Module("film", "影视");
        await using var appearance = new AppAppearance(new QualityFixture.DocumentStore());
        await appearance.LoadAsync();
        var shell = new ShellWindow([music, film], [], () => Task.CompletedTask, () => Task.CompletedTask, appearance: appearance)
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
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "通用", "音乐", "影视" }));
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
