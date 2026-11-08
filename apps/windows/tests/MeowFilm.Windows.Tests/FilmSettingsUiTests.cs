using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Core.Scripting;
using MeowFilm.Desktop;
using MeowFilm.Platform.Scripting;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmSettingsUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("draft testing passes all four groups and query without saving", DraftTesting);
        yield return Case("row save add delete and section restore preserve whole panel scope", RuleEditing);
        yield return Case("native new-rule inputs share JavaScript whitespace semantics through the loaded view", NativeRuleInputs);
        yield return Case("rule cancellation and errors cannot publish into a hidden view", RuleLifetime);
        yield return Case("local scripts preserve valid UTF8 bytes and reject invalid UTF8", LocalScriptEncoding);
        yield return Case("cat edits toggles restart and bulk deletion reach the source owner", CatManagement);
        yield return Case("centered edits survive resize and offset coercion while confirmations retire without saving", PopupLifetime);
        yield return Case("video options radio selection and order survive a new owner", VideoSettings);
        yield return Case("video detection deletion and explicit reimport use the selected installation", VideoActions);
        yield return Case("failed source operations restore switches and notify with retry", SourceFailure);
        yield return Case("native keyboard scrolling high contrast and screenshots cover both window sizes", Layout);
        yield return Case("empty source pages keep their actions and columns", Empty);
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("film-settings-ui: " + name, () => MusicQualityConsumerTests.Sta(body));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task DraftTesting()
    {
        await using var scene = await Scene.Create();
        var ruleEditor = scene.Editor("EpisodeEditor");
        Check(ruleEditor.Rows.Select(row => row.Number).SequenceEqual(Enumerable.Range(1, ruleEditor.Rows.Count)),
            "saved rules expose their current sequence numbers");
        var addRule = (FrameworkElement)ruleEditor.FindName("NewPattern");
        var rulesList = (FrameworkElement)ruleEditor.FindName("RuleList");
        Check(addRule.TranslatePoint(new Point(), ruleEditor).Y < rulesList.TranslatePoint(new Point(), ruleEditor).Y,
            "new rule input precedes the saved rows");
        Check(scene.View.Header is { Title: "设置", SearchVisible: false, SearchEnabled: false, Slot: null },
            "film settings contributes content without category navigation");
        var episode = scene.Editor("EpisodeEditor");
        var movie = scene.Editor("MovieEditor");
        episode.Rows[0].Pattern = "draft-episode";
        movie.Rows[0].Replace = "draft-movie";
        scene.Box("SearchInput").Text = "作品2 更新";
        scene.Box("SearchQuery").Text = "作品2";
        int writes = scene.Store.Writes;
        scene.Engine.Result = new()
        {
            Section = FilmRuleSection.Search, Type = "success", Text = "净化后：作品2",
            InputText = "作品2 更新", ProcessedText = "作品2", GroupKey = "作品2",
            TrailingDigits = "2", Previews = ["作品2"]
        };
        Click(Button(scene.View, "测试搜索列表净化"));
        await Until(() => !scene.View.RulesBusy);
        var call = scene.Engine.Requests.Last(request => request.GetProperty("operation").GetString() == "process");
        Check(call.GetProperty("request").GetProperty("query").GetString() == "作品2", "query tail context reaches the service");
        Check(call.GetProperty("rules").GetProperty("episodeRules")[0].GetProperty("pattern").GetString() == "draft-episode" &&
            call.GetProperty("rules").GetProperty("movieRules")[0].GetProperty("replace").GetString() == "draft-movie",
            "test consumes the current entire draft");
        Check(scene.Store.Writes == writes && scene.Rules.GetSavedSnapshot().EpisodeRules[0].Pattern == "episode",
            "testing neither writes nor changes the saved owner snapshot");
        var result = scene.Element<StackPanel>("SearchResults");
        Check(Texts(result).Contains("聚合 Key：作品2") && Texts(result).Contains("搜索尾号保护：2"),
            "the returned group key and tail are visible business data");
        Check(scene.Editor("SearchEditor").Rows[0].Preview == "净化后：作品2", "returned cumulative previews stay in rule order");
        movie.Rows[0].Pattern = "next-draft";
        Check(result.Children.Count == 0, "changing a draft clears stale results");
        await scene.Select("cats"); await scene.Select("rules");
        Check(movie.Rows[0].Pattern == "next-draft" && scene.Store.Writes == writes,
            "switching settings pages preserves unsaved draft without saving it");
    }

    private static async Task RuleEditing()
    {
        await using var scene = await Scene.Create();
        scene.Editor("EpisodeCleanEditor").Rows[0].Pattern = "clean-draft";
        scene.Editor("MovieEditor").Rows[0].Replace = "movie-draft";
        Click(Visuals(scene.Editor("EpisodeEditor")).OfType<Button>().First(button => Equals(button.Content, "保存")));
        await Until(() => !scene.View.RulesBusy);
        Check(scene.Rules.GetSavedSnapshot().EpisodeCleanRegexRules.SequenceEqual(["clean-draft"]) &&
            scene.Rules.GetSavedSnapshot().MovieRules[0].Replace == "movie-draft", "a row save persists every group's draft");
        Check(!scene.Engine.Requests.Any(request => request.GetProperty("operation").GetString() is "newClean" or "newMatch"),
            "existing-row edits do not use new-rule parsing");
        var editor = scene.Editor("MovieEditor");
        ((TextBox)editor.FindName("NewPattern")).Text = "/added/gi";
        ((TextBox)editor.FindName("NewReplace")).Text = "$1";
        Click((Button)editor.FindName("AddButton"));
        await Until(() => !scene.View.RulesBusy);
        Check(scene.Rules.GetSavedSnapshot().MovieRules[^1] == new FilmMatchRule("added", "$1", "gi"),
            "new matching rows retain the normalizer's flags and replacement");
        int count = editor.Rows.Count;
        var rowDelete = Visuals(editor).OfType<Button>().First(button => Equals(button.Content, "删除"));
        Click(rowDelete); await Drain();
        Click(scene.Element<Button>("PopupCancel"));
        Check(editor.Rows.Count == count, "cancelled delete keeps the rule");
        Click(rowDelete); await scene.AcceptPopup();
        Check(editor.Rows.Count == count - 1, "confirmed deletion saves the new full snapshot");
        scene.Editor("SearchEditor").Rows[0].Pattern = "search-unsaved";
        var restore = Button(scene.View, "恢复列表净化默认规则");
        DesktopTheme.SetIsHighContrast(scene.View, true); scene.View.UpdateLayout();
        var restoreText = Visuals(restore).OfType<TextBlock>().Single();
        Check(((SolidColorBrush)restore.Foreground).Color == SystemColors.WindowTextColor &&
            ((SolidColorBrush)restoreText.Foreground).Color == SystemColors.WindowTextColor, "restore normal text uses system window foreground");
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        restore.SetValue(hoverKey, true); scene.View.UpdateLayout();
        Check(((SolidColorBrush)restore.Background).Color == SystemColors.HighlightColor &&
            ((SolidColorBrush)restoreText.Foreground).Color == SystemColors.HighlightTextColor, "restore hovered text matches its system highlight background");
        restore.SetValue(hoverKey, false);
        restore.IsEnabled = false; scene.View.UpdateLayout();
        Check(((SolidColorBrush)restore.Foreground).Color == SystemColors.GrayTextColor &&
            ((SolidColorBrush)restoreText.Foreground).Color == SystemColors.GrayTextColor, "disabled restore text uses system gray foreground");
        restore.ClearValue(UIElement.IsEnabledProperty);
        Click(restore); await Drain();
        Check(scene.Element<Popup>("ActionPopup").IsOpen, "restore still requires confirmation");
        Click(scene.Element<Button>("PopupCancel")); await Drain();
        Check(scene.Editor("SearchEditor").Rows[0].Pattern == "search-unsaved" &&
            scene.Rules.GetSavedSnapshot().EpisodeCleanRegexRules.SequenceEqual(["clean-draft"]), "cancelled restore preserves drafts and saved rules");
        Click(restore); await scene.AcceptPopup();
        var restored = scene.Rules.GetSavedSnapshot();
        Check(restored.EpisodeCleanRegexRules.SequenceEqual(["restored-clean"]) &&
            restored.EpisodeRules[0].Pattern == "restored-episode" &&
            restored.AggregateRegexRules.SequenceEqual(["search-unsaved"]),
            "episode restore replaces its two groups and saves other groups' drafts");
    }

    private static async Task NativeRuleInputs()
    {
        await using var store = new MemoryStore();
        await using var rules = new FilmRuleService(new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), store);
        await using var sources = new FilmSourceService(new RecordingRuntime(0), store);
        await rules.SaveAsync(new FilmRuleSet
        {
            EpisodeCleanRegexRules = ["^never$"],
            EpisodeRules = [new("^never$", "unused")],
            MovieRules = [new("^never$", "unused")],
            AggregateRegexRules = ["^never$"]
        });
        var view = new FilmSettingsView(rules, sources);
        var window = new Window { Content = view, Width = 1248, Height = 820, Left = -10000, Top = -10000,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        var notices = new List<string>();
        window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
        {
            if (args.Message.Length > 0) notices.Add(args.Message);
        }));
        try
        {
            window.Show();
            await Until(() => view.RulesLoaded && !view.RulesBusy);
            foreach (var (name, section) in new[]
            {
                ("MovieEditor", FilmRuleSection.Movie), ("EpisodeEditor", FilmRuleSection.Episode)
            })
            {
                var editor = (FilmSettingsRuleEditor)view.FindName(name);
                var pattern = (TextBox)editor.FindName("NewPattern");
                var replace = (TextBox)editor.FindName("NewReplace");
                var add = (Button)editor.FindName("AddButton");
                async Task InvokeAdd()
                {
                    add.BringIntoView(); view.UpdateLayout(); await Drain();
                    Check(add.IsEnabled && add.IsLoaded && add.IsVisible, "real new-rule button accepts input");
                    ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                pattern.Text = "\u0085"; replace.Text = "X";
                Check(pattern.Text == "\u0085", "WPF preserves the NEL input");
                int writes = store.Writes;
                await InvokeAdd();
                await Until(() => store.Writes > writes && !view.RulesBusy);
                var saved = rules.GetSavedSnapshot();
                var matches = section == FilmRuleSection.Movie ? saved.MovieRules : saved.EpisodeRules;
                Check(matches[^1].Pattern == "\u0085" && matches[^1].Replace == "X",
                    "the native normalizer accepts NEL and saves the same rule");
                var request = new FilmRuleRequest(section, "a\u0085b");
                Check((await rules.ProcessAsync(request)).OutputText == "aXb" &&
                    (await rules.ProcessAsync(request, saved)).OutputText == "aXb",
                    "saved and draft consumers share the native rule result");

                pattern.Text = "b"; replace.Text = "Y";
                writes = store.Writes;
                await InvokeAdd();
                await Until(() => store.Writes > writes && !view.RulesBusy);
                Check((await rules.ProcessAsync(new(section, "ab"))).OutputText == "aY",
                    "ordinary rule input remains functional");

                pattern.Text = " \t\uFEFF"; replace.Text = "invalid";
                writes = store.Writes;
                int count = editor.Rows.Count, noticeCount = notices.Count;
                await InvokeAdd();
                await Until(() => notices.Count > noticeCount && !view.RulesBusy);
                Check(notices[^1].Contains("请输入正则", StringComparison.Ordinal) &&
                    store.Writes == writes && editor.Rows.Count == count,
                    "JavaScript whitespace reaches the shared normalizer and cannot be saved");
            }
            await using var reopened = new FilmRuleService(
                new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), store);
            await reopened.InitializeAsync();
            foreach (var section in new[] { FilmRuleSection.Movie, FilmRuleSection.Episode })
                Check((await reopened.ProcessAsync(new(section, "a\u0085b"))).OutputText == "aXb",
                    "both NEL rules survive reopening the saved document");
        }
        finally
        {
            window.Content = null; window.Close(); await Drain();
        }
    }

    private static async Task RuleLifetime()
    {
        await using var scene = await Scene.Create();
        scene.Box("EpisodeInput").Text = "一集";
        scene.Engine.HoldProcess = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(Button(scene.View, "测试列表净化与集数匹配"));
        await Until(() => scene.Engine.ProcessEntered);
        Check(scene.View.RulesBusy && !scene.Element<StackPanel>("RuleSections").IsEnabled, "busy rules remain legible but cannot mutate");
        Capture(scene.View, "film-settings-rules-busy.png");
        await scene.Select("cats");
        scene.Engine.HoldProcess.SetResult();
        await Until(() => !scene.View.RulesBusy);
        Check(scene.Engine.ProcessCancelled && scene.Element<StackPanel>("EpisodeResults").Children.Count == 0,
            "a noncooperative late result cannot publish after the page was hidden");
        scene.Engine.HoldProcess = null;
        await scene.Select("rules");
        scene.Engine.FailProcess = true;
        Click(Button(scene.View, "测试列表净化与集数匹配")); await Until(() => !scene.View.RulesBusy);
        Check(scene.Notices.Any(message => message.Contains("fixture engine failure", StringComparison.Ordinal)) &&
            scene.View.CanEditRules, "engine failure uses the existing notice route and releases busy state");
        scene.Engine.FailProcess = false;
        scene.Engine.Result = new() { Section = FilmRuleSection.Episode, Type = "success", Text = "命中第 1 条",
            InputText = "一集", ProcessedText = "一集", Matched = true, MatchedRuleIndex = 1, RewrittenText = "E1" };
        Click(Button(scene.View, "测试列表净化与集数匹配")); await Until(() => !scene.View.RulesBusy);
        Check(Texts(scene.Element<StackPanel>("EpisodeResults")).Contains("改写结果：E1"), "testing can recover after failure");
        scene.Box("EpisodeInput").Text = "离页输入";
        scene.Engine.ProcessEntered = scene.Engine.ProcessCancelled = false;
        scene.Engine.HoldProcess = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(Button(scene.View, "测试列表净化与集数匹配"));
        await Until(() => scene.Engine.ProcessEntered);
        scene.Window.Content = null; await Drain();
        scene.Engine.HoldProcess.SetResult(); await Until(() => !scene.View.RulesBusy);
        Check(scene.Engine.ProcessCancelled && scene.Element<StackPanel>("EpisodeResults").Children.Count == 0,
            "unloading cancels the request and rejects a noncooperative completion");
    }

    private static async Task LocalScriptEncoding()
    {
        string path = TestWorkspace.DataPath("film-settings-script-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            const string expected = "\uFEFFconst 名称 = '猫源🐾';\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(expected);
            await File.WriteAllBytesAsync(path, bytes);
            string actual = await FilmSettingsView.ReadLocalScriptAsync(path);
            Check(actual == expected && Encoding.UTF8.GetBytes(actual).SequenceEqual(bytes),
                "valid UTF8 script text preserves original bytes including BOM and line endings");
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
            bool rejected = false;
            try { await FilmSettingsView.ReadLocalScriptAsync(path); }
            catch (DecoderFallbackException) { rejected = true; }
            Check(rejected, "invalid UTF8 must fail explicitly rather than substitute replacement characters");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task PopupLifetime()
    {
        await using var scene = await Scene.Create(19);
        scene.Window.Left = 80; scene.Window.Top = 60;
        scene.Window.Width = 1100; scene.Window.Height = 430;
        await scene.Select("cats");
        var grid = scene.Element<DataGrid>("CatsGrid");
        var scroll = Visuals(grid).OfType<ScrollViewer>().First();
        var row = scene.View.CatRows.Last();
        string id = row.Source.InstallationId;
        var popup = scene.Element<Popup>("ActionPopup");
        int edits = scene.Runtime.Calls.Count(call => call.Method == "cat.script.edit");
        int writes = scene.Store.Writes;
        await OpenEdit();
        scene.Box("CatNameBox").Text = "保留的猫源草稿";
        scene.Box("CatUrlBox").Text = "https://fixture.invalid/retained.js";
        double offset = scroll.VerticalOffset;
        Check(offset > 0, "the editor opens from the visible bottom row of an overflowing table");
        scene.Window.Width = 1000;
        scene.Window.UpdateLayout(); await Drain();
        AssertDraft();
        scene.Window.Height = 820;
        scene.Window.UpdateLayout(); await Drain();
        Check(scroll.VerticalOffset < offset, "height growth actually coerces the bottom table offset");
        AssertDraft();
        Check(scene.Runtime.Calls.Count(call => call.Method == "cat.script.edit") == edits &&
            scene.Store.Writes == writes, "resizing does not save the editor");
        await scene.AcceptPopup();
        var saved = scene.Runtime.Calls.Where(call => call.Method == "cat.script.edit").Skip(edits).ToArray();
        Check(saved.Length == 1 && saved[0].Parameters.GetProperty("installationId").GetString() == id &&
            saved[0].Parameters.GetProperty("name").GetString() == "保留的猫源草稿" &&
            saved[0].Parameters.GetProperty("sourceUrl").GetString() == "https://fixture.invalid/retained.js" &&
            scene.Sources.Cats.Single(cat => cat.InstallationId == id).Name == "保留的猫源草稿",
            "the retained action saves exactly once to the original installation");
        Check(!popup.IsOpen, "saving closes the retained editor");
        edits++;
        writes = scene.Store.Writes;
        await OpenEdit();
        scene.Box("CatNameBox").Text = "切页放弃";
        await scene.Select("rules");
        Check(!popup.IsOpen, "switching categories retires the edit");

        var restore = Button(scene.View, "恢复列表净化默认规则");
        string original = scene.Editor("EpisodeCleanEditor").Rows[0].Pattern;
        int restores = scene.Engine.Requests.Count(request => request.GetProperty("operation").GetString() == "restore");
        Click(restore); await Drain();
        scene.Window.Width = 1050; scene.Window.Height = 650;
        scene.Window.UpdateLayout(); await Drain();
        AssertCentered();
        Click(scene.Element<Button>("PopupCancel")); await Drain();
        Check(!popup.IsOpen && scene.Editor("EpisodeCleanEditor").Rows[0].Pattern == original &&
            scene.Engine.Requests.Count(request => request.GetProperty("operation").GetString() == "restore") == restores &&
            scene.Store.Writes == writes, "resized restore confirmation can be cancelled without changing rules or writing");

        await scene.Select("cats");
        await OpenEdit();
        scene.Box("CatNameBox").Text = "隐藏放弃";
        scene.View.Visibility = Visibility.Collapsed; await Drain();
        Check(!popup.IsOpen, "hiding the view retires its editor");
        scene.View.Visibility = Visibility.Visible;
        await Until(() => !scene.View.SourcesBusy); await Drain();
        await OpenEdit();
        scene.Box("CatNameBox").Text = "卸载放弃";
        scene.Window.Content = null; await Drain();
        Check(!popup.IsOpen && scene.Runtime.Calls.Count(call => call.Method == "cat.script.edit") == edits &&
            scene.Store.Writes == writes &&
            scene.Sources.Cats.Single(cat => cat.InstallationId == id).Name == "保留的猫源草稿",
            "category changes hiding and unloading do not save abandoned drafts");

        async Task OpenEdit()
        {
            var current = scene.View.CatRows.Single(value => value.Source.InstallationId == id);
            Click(await Cell<Button>(grid, current, 7, button => Equals(button.Content, "编辑")));
            await Drain();
            Check(popup.IsOpen, "the actual cat row opens its editor");
        }
        void AssertDraft()
        {
            AssertCentered();
            Check(scene.Box("CatNameBox").Text == "保留的猫源草稿" &&
                scene.Box("CatUrlBox").Text == "https://fixture.invalid/retained.js",
                "width and height changes preserve both edit fields");
        }
        void AssertCentered()
        {
            Check(popup.IsOpen, "a viewport change does not cancel the centered popup");
            var surface = scene.Element<Border>("PopupSurface");
            var actual = surface.PointToScreen(new(surface.ActualWidth / 2, surface.ActualHeight / 2));
            var expected = scene.View.PointToScreen(new(scene.View.ActualWidth / 2, scene.View.ActualHeight / 2));
            Check((actual - expected).Length < 3, "the actual popup remains centered on window content");
        }
    }

    private static async Task CatManagement()
    {
        await using var scene = await Scene.Create();
        await scene.Select("cats");
        var grid = scene.Element<DataGrid>("CatsGrid");
        Check(grid.Columns.Skip(1).Select(column => column.Header?.ToString()).SequenceEqual(
            ["启用", "名称", "类型", "状态", "修改时间", "更新状态", "操作"]), "cat columns follow the agreed order");
        var first = scene.View.CatRows[0];
        Click(await Cell<Button>(grid, first, 7, button => Equals(button.Content, "编辑"))); await Drain();
        scene.Box("CatNameBox").Text = "已改名的猫源";
        scene.Box("CatUrlBox").Text = "https://fixture.invalid/revised.js";
        await scene.AcceptPopup();
        Check(scene.Sources.Cats[0].Name == "已改名的猫源" &&
            scene.Runtime.Calls.Any(call => call.Method == "cat.script.edit"), "edit persists via the management owner");
        var toggle = await Cell<CheckBox>(grid, first, 1);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Click(toggle); await Until(() => !scene.View.SourcesBusy);
        Check(!first.Enabled && !first.CanRestart &&
            !(await Cell<Button>(grid, first, 7, button => Equals(button.Content, "重启"))).IsEnabled,
            "disabled cats cannot restart");
        await scene.Select("videos");
        Check(scene.View.VideoRows.Where(row => row.Source.Key.InstallationId == first.Source.InstallationId).All(row => !row.CanDetect),
            "disabled cats also make their video detection unavailable");
        await scene.Select("cats");
        toggle = await Cell<CheckBox>(grid, first, 1);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Click(toggle); await Until(() => !scene.View.SourcesBusy);
        Click(await Cell<Button>(grid, first, 7, button => Equals(button.Content, "重启"))); await Until(() => !scene.View.SourcesBusy);
        Check(scene.Runtime.Calls.Count(call => call.Method == "cat.script.restart") == 1, "restart targets one installation");
        Click(await Cell<Button>(grid, first, 7, button => Equals(button.Content, "更新"))); await Until(() => !scene.View.SourcesBusy);
        Check(scene.Runtime.Calls.Count(call => call.Method == "cat.script.update") == 1 && first.Source.UpdateState == "updated",
            "network update uses the installed URL through the owner");
        first.Selected = true;
        Click(Button(scene.View, "删除选中猫源")); await Drain();
        Check(scene.Sources.Cats.Count == 2, "opening confirmation does not delete");
        Capture(scene.Element<Border>("PopupSurface"), "film-settings-delete-confirmation.png");
        await scene.AcceptPopup();
        Check(scene.Sources.Cats.Count == 1 && scene.Sources.Videos.All(video => video.Key.InstallationId != first.Source.InstallationId),
            "bulk selection performs actual owner deletion and child cleanup");
    }

    private static async Task VideoSettings()
    {
        await using var scene = await Scene.Create();
        await scene.Select("videos");
        var grid = scene.Element<DataGrid>("VideosGrid");
        var first = scene.View.VideoRows[0];
        foreach (int column in new[] { 4, 5, 6 })
        {
            var box = await Cell<CheckBox>(grid, first, column);
            box.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Click(box);
            await Until(() => !scene.View.SourcesBusy);
        }
        var radio = await Cell<RadioButton>(grid, first, 7);
        radio.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Click(radio); await Until(() => !scene.View.SourcesBusy);
        var second = scene.View.VideoRows[1];
        radio = await Cell<RadioButton>(grid, second, 7);
        radio.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Click(radio); await Until(() => !scene.View.SourcesBusy);
        Check(scene.Sources.Videos.Count(video => video.Options.AggregateImages) == 1 &&
            scene.Sources.Videos.Single(video => video.Options.AggregateImages).Key == second.Source.Key,
            "aggregate images uses owner-wide single selection");
        var order = await Cell<TextBox>(grid, second, 8);
        order.Text = "0"; order.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        RaiseKey(order, Key.Enter); await Until(() => !scene.View.SourcesBusy);
        await using var reopened = new FilmSourceService(scene.Runtime, scene.Store);
        await reopened.InitializeAsync();
        var saved = reopened.Videos.Single(video => video.Key == first.Source.Key);
        Check(saved.Options is { Enabled: false, HomeVisible: false, SearchEnabled: false, AggregateImages: false } &&
            reopened.Videos[0].Key == second.Source.Key && reopened.Videos[0].Options.AggregateImages,
            "all four settings and ordering reload through a new service");
    }

    private static async Task VideoActions()
    {
        await using var scene = await Scene.Create();
        await scene.Select("videos");
        var grid = scene.Element<DataGrid>("VideosGrid");
        var first = scene.View.VideoRows[0];
        var original = first.Source.Options;
        Click(await Cell<Button>(grid, first, 10, button => Equals(button.Content, "检测")));
        await Until(() => !scene.View.SourcesBusy);
        Check(scene.Runtime.Calls.Where(call => call.Method == "cat.script.invoke").Select(call => call.Parameters.GetProperty("operation").GetString())
            .SequenceEqual(["home", "category", "detail", "play", "search"]), "detection calls the real owner probe through its runtime port");
        Check(first.Source.Detection?.Result == "valid" && first.Source.Options == original,
            "detection updates its result without changing options");
        Click(await Cell<Button>(grid, first, 10, button => Equals(button.Content, "删除"))); await scene.AcceptPopup();
        Check(scene.Sources.Videos.All(video => video.Key != first.Source.Key), "video deletion reaches persistent mapping owner");
        await scene.Select("cats");
        Click(Button(scene.View, "网络导入")); await Drain();
        var target = scene.Element<ComboBox>("ImportTarget");
        target.SelectedIndex = 1;
        await scene.AcceptPopup();
        Check(scene.Runtime.Calls.Last(call => call.Method == "cat.script.import").Parameters.GetProperty("installationId").GetString() ==
            first.Source.Key.InstallationId, "explicit reimport preserves the selected installation identity");
        Check(scene.Sources.Videos.Any(video => video.Key == first.Source.Key), "successful explicit reimport restores its deleted video");
    }

    private static async Task SourceFailure()
    {
        await using var scene = await Scene.Create();
        await scene.Select("videos");
        var grid = scene.Element<DataGrid>("VideosGrid");
        var row = scene.View.VideoRows[0];
        scene.Store.FailWrite = true;
        var box = await Cell<CheckBox>(grid, row, 5);
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Click(box);
        await Until(() => !scene.View.SourcesBusy);
        Check(row.HomeVisible && box.IsChecked == true && scene.View.SourcesFailed,
            "failed save restores the actual owner state and exposes retry");
        Check(scene.Notices.Any(message => message.Contains("来源操作失败", StringComparison.Ordinal)), "source failure is a window notice");
        Capture(scene.View, "film-settings-videos-failure.png");
        scene.Store.FailWrite = false;
        Click(Button(scene.View, "重试视频源读取")); await Until(() => !scene.View.SourcesBusy);
        Check(!scene.View.SourcesFailed && scene.View.CanEditSources, "source retry recovers the page");
        await scene.Select("rules");
        Check(scene.View.CanEditRules, "source failure does not block the pure client rule editor");
    }

    private static async Task Layout()
    {
        await using var scene = await Scene.Create(18);
        foreach (var size in new[] { (Width: 1248, Height: 820), (Width: 620, Height: 430) })
        {
            scene.Window.Width = size.Width; scene.Window.Height = size.Height; await Drain(); scene.View.UpdateLayout();
            foreach (string page in new[] { "rules", "cats", "videos" })
            {
                await scene.Select(page); scene.View.UpdateLayout();
                if (page == "rules")
                {
                    var scroll = scene.Element<ScrollViewer>("RulesScroller");
                    Check(scroll.ViewportHeight > 100 && scroll.ScrollableHeight > 0 && scroll.ScrollableWidth == 0,
                        "rule sections scroll inside the available height at either window size");
                    var sections = scene.Element<StackPanel>("RuleSections").Children.OfType<Border>().ToArray();
                    Check(sections.Length == 3 && sections.All(section => section.BorderThickness.Bottom == 1), "all settings sections use a shared divider");
                    foreach (var section in sections)
                    {
                        var content = (StackPanel)section.Child;
                        var header = (DockPanel)content.Children[0];
                        var title = header.Children.OfType<TextBlock>().Single();
                        var text = new FormattedText(title.Text, CultureInfo.CurrentCulture, title.FlowDirection,
                            new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch),
                            title.FontSize, title.Foreground, VisualTreeHelper.GetDpi(title).PixelsPerDip);
                        Check(title.TextWrapping == TextWrapping.NoWrap && title.ActualWidth + .5 >= text.WidthIncludingTrailingWhitespace,
                            "section titles are complete on one line: " + title.Text);
                        var fields = (FrameworkElement)content.Children[1];
                        var headingBounds = new Rect(header.TranslatePoint(new Point(), content), header.RenderSize);
                        var contentBounds = new Rect(fields.TranslatePoint(new Point(), content), fields.RenderSize);
                        Check(headingBounds.Bottom <= contentBounds.Top && contentBounds.Top - headingBounds.Bottom <= 9 &&
                            Math.Abs(headingBounds.Left - contentBounds.Left) < 1 && Math.Abs(contentBounds.Width - content.ActualWidth) < 1,
                            "every rule section has a compact top heading and full-width fields");
                    }
                    var savedRow = Visuals(scene.Editor("EpisodeCleanEditor")).OfType<Border>()
                        .First(border => border.Style == scene.Editor("EpisodeCleanEditor").FindResource("Settings.RuleSurface"));
                    Check(savedRow.CornerRadius == new CornerRadius(10) && savedRow.BorderThickness == new Thickness(1) &&
                        ((SolidColorBrush)savedRow.BorderBrush).Color == Color.FromArgb(217, 203, 213, 225), "saved rules use the server row outline");
                    Check(Button(scene.View, "恢复搜索列表净化默认规则").Style == scene.View.FindResource("Settings.DangerButton"),
                        "restore is styled as a destructive action and keeps its confirmation");
                }
                else
                {
                    var grid = scene.Element<DataGrid>(page == "cats" ? "CatsGrid" : "VideosGrid");
                    Check(grid.Parent is Border card && card.Style == scene.View.FindResource("Settings.ListSurface") &&
                        card.CornerRadius == new CornerRadius(10) && grid.BorderThickness == new Thickness(0),
                        "source lists reuse the shared rounded card rather than a second square frame");
                    var scroll = Visuals(grid).OfType<ScrollViewer>().First();
                    // DataGrid's virtualizing panel reports ViewportHeight in rows,
                    // not DIP. Measure the actual viewport against the 56-DIP row.
                    var viewport = Visuals(scroll).OfType<ScrollContentPresenter>().First();
                    Check(viewport.ActualHeight > grid.RowHeight && scroll.ScrollableHeight > 0,
                        "source tables retain rows and vertical scrolling");
                    if (size.Width == 620) Check(scroll.ScrollableWidth > 0, "minimum window keeps all table columns via internal horizontal scrolling");
                }
                Capture(scene.View, $"film-settings-{page}-{size.Width}.png");
            }
        }
        var nav = scene.Element<StackPanel>("SettingsNavigation").Children.OfType<RadioButton>().ToArray();
        nav[2].Focus(); RaiseKey(nav[2], Key.Home); await Drain();
        Check(nav[0].IsChecked == true && nav[0].IsKeyboardFocused, "Home navigates internal tabs and keeps real focus");
        RaiseKey(nav[0], Key.Right); await Drain();
        Check(nav[1].IsChecked == true && nav[1].IsKeyboardFocused, "arrow navigation works");
        DesktopTheme.SetIsHighContrast(scene.View, true); scene.View.UpdateLayout();
        Check(scene.View.Background is SolidColorBrush brush && brush.Color == SystemColors.WindowColor, "high contrast uses system ground");
        foreach (var control in Visuals(scene.View).OfType<Control>().Where(control => control.IsVisible &&
                     control is TextBox or CheckBox or RadioButton))
            Check(AutomationProperties.GetName(control).Length > 0, "visible inputs have accessible names");
    }

    private static async Task Empty()
    {
        await using var scene = await Scene.Create(0);
        foreach (string page in new[] { "cats", "videos" })
        {
            await scene.Select(page);
            Check(Texts(scene.View).Contains(page == "cats" ? "暂无猫源" : "暂无视频源"), "real empty state is visible");
            Check(scene.View.CanEditSources && !scene.View.CanDeleteCats && !scene.View.CanDeleteVideos,
                "empty pages allow import and disable selection actions");
            Capture(scene.View, $"film-settings-{page}-empty.png");
        }
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MemoryStore Store = new();
        internal readonly RecordingEngine Engine = new();
        internal readonly RecordingRuntime Runtime;
        internal readonly FilmRuleService Rules;
        internal readonly FilmSourceService Sources;
        internal readonly FilmSettingsView View;
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        private Scene(int cats)
        {
            Runtime = new(cats);
            Rules = new(Engine, Store); Sources = new(Runtime, Store);
            View = new(Rules, Sources);
            Window = new() { Content = View, Width = 1248, Height = 820, Left = -10000, Top = -10000,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            {
                if (args.Message.Length > 0) Notices.Add(args.Message);
            }));
        }
        internal static async Task<Scene> Create(int cats = 2)
        {
            var scene = new Scene(cats);
            try
            {
                await scene.Sources.RefreshAsync();
                scene.Window.Show();
                await Until(() => scene.View.RulesLoaded && !scene.View.RulesBusy);
                scene.View.UpdateLayout(); await Drain();
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
        internal FilmSettingsRuleEditor Editor(string name) => Element<FilmSettingsRuleEditor>(name);
        internal TextBox Box(string name) => Element<TextBox>(name);
        internal async Task Select(string page)
        {
            var nav = Element<StackPanel>("SettingsNavigation").Children.OfType<RadioButton>().Single(button => Equals(button.Tag, page));
            nav.IsChecked = true;
            await Until(() => !View.SourcesBusy && (page != "rules" || !View.RulesBusy));
            View.UpdateLayout(); await Drain();
        }
        internal async Task AcceptPopup()
        {
            await Drain();
            Check(Element<Popup>("ActionPopup").IsOpen, "confirmation or edit popup is open");
            Click(Element<Button>("PopupAccept"));
            await Until(() => !View.RulesBusy && !View.SourcesBusy);
            View.UpdateLayout(); await Drain();
        }
        public async ValueTask DisposeAsync()
        {
            Engine.HoldProcess?.TrySetResult();
            Window.Content = null; Window.Close(); await Drain();
            await Rules.DisposeAsync(); await Sources.DisposeAsync(); await Store.DisposeAsync();
        }
    }

    // These ports test UI/service wiring. Rule engine semantics are covered by FilmRulesTests.
    private sealed class RecordingEngine : IJavaScriptJsonEngine
    {
        internal readonly List<JsonElement> Requests = [];
        internal TaskCompletionSource? HoldProcess;
        internal bool ProcessEntered, ProcessCancelled, FailProcess;
        internal FilmRuleResult Result = new() { Section = FilmRuleSection.Episode, Type = "success", Text = "命中第 1 条" };
        private static FilmRuleSet Seed => new()
        {
            EpisodeCleanRegexRules = ["clean"], EpisodeRules = [new("episode", "E$1", "i")],
            MovieRules = [new("movie", "", "i")], AggregateRegexRules = ["search"]
        };
        public async Task<JsonElement> InvokeAsync(object request, CancellationToken cancellationToken = default)
        {
            var value = JsonSerializer.SerializeToElement(request, Json); Requests.Add(value);
            string operation = value.GetProperty("operation").GetString()!;
            if (operation == "process")
            {
                ProcessEntered = true;
                using var registration = cancellationToken.Register(() => ProcessCancelled = true);
                if (HoldProcess is not null) await HoldProcess.Task; // Deliberately late, to exercise the view fence.
                if (FailProcess) throw new InvalidOperationException("fixture engine failure");
                return JsonSerializer.SerializeToElement(Result, Json);
            }
            if (operation == "newMatch") return JsonSerializer.SerializeToElement(new FilmMatchRule("added", "$1", "gi"), Json);
            if (operation == "newClean") return JsonSerializer.SerializeToElement("added-clean", Json);
            var rules = operation == "initialize" ? Seed : value.GetProperty("rules").Deserialize<FilmRuleSet>(Json)!;
            if (operation == "restore")
                rules = rules with { EpisodeCleanRegexRules = ["restored-clean"], EpisodeRules = [new("restored-episode", "E$1", "i")] };
            return JsonSerializer.SerializeToElement(new
            {
                changed = false, rules,
                document = new
                {
                    rules.EpisodeCleanRegexRules,
                    episodeRules = rules.EpisodeRules.Select(rule => JsonSerializer.Serialize(rule, Json)).ToArray(),
                    movieRules = rules.MovieRules.Select(rule => JsonSerializer.Serialize(rule, Json)).ToArray(),
                    rules.AggregateRegexRules
                }
            }, Json);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryStore : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> documents = [];
        internal int Writes;
        internal bool FailWrite;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? bytes = documents.TryGetValue(documentNamespace, out var stored) ? stored.ToArray() : null;
            return Task.FromResult(new DocumentSnapshot(bytes, bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
        }
        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new DocumentStoreException(DocumentStoreError.Storage);
            var before = await ReadAsync(documentNamespace, cancellationToken);
            if (before.Fingerprint != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            documents[documentNamespace] = data.ToArray(); Writes++;
            return DocumentFingerprint.FromBytes(data.Span);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRuntime : IRuntimeClient
    {
        internal readonly List<(string Method, JsonElement Parameters)> Calls = [];
        private readonly List<FilmCatSource> cats;
        internal RecordingRuntime(int count) => cats = Enumerable.Range(0, count).Select(index => new FilmCatSource(
            $"cat-{index}", $"测试猫源 {index + 1}", index % 2 == 0 ? "network" : "local",
            index % 2 == 0 ? $"https://fixture.invalid/{index}.js" : null, true, 1730000000000, null,
            "idle", null, 1, "ready", "fixture-hash", null,
            [new("site", $"测试视频源 {index + 1}", $"/fixture/{index}",
                ["home", "category", "detail", "play", "search"], new Dictionary<string, string>())])).ToList();
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The film settings fixture does not upload local sources.");
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = JsonSerializer.SerializeToElement(parameters, Json); Calls.Add((method, value));
            string operation = method["cat.script.".Length..];
            if (operation == "status") return Reply(new { instances = cats });
            if (operation == "invoke")
                return Reply(value.GetProperty("operation").GetString() switch
                {
                    "home" => new { @class = new[] { new { type_id = "tv", type_name = "剧集" } } },
                    "category" => new { list = new[] { new { vod_id = "film-1" } } },
                    "detail" => new { list = new[] { new { vod_id = "film-1", vod_play_from = "线路", vod_play_url = "第一集$episode-1" } } },
                    "play" => new { url = "https://fixture.invalid/media.m3u8" },
                    "search" => (object)new { list = Array.Empty<object>() },
                    _ => throw new InvalidOperationException("Unexpected fixture probe operation")
                });
            string id = value.GetProperty("installationId").GetString()!;
            int index = cats.FindIndex(cat => cat.InstallationId == id);
            if (operation == "uninstall") { cats.RemoveAt(index); return Reply(new { }); }
            var current = cats[index];
            cats[index] = operation switch
            {
                "enable" => current with { Enabled = value.GetProperty("enabled").GetBoolean(),
                    State = value.GetProperty("enabled").GetBoolean() ? "ready" : "disabled" },
                "edit" => current with { Name = value.GetProperty("name").GetString()!,
                    SourceUrl = value.TryGetProperty("sourceUrl", out var url) ? url.GetString() : current.SourceUrl },
                "restart" => current with { Generation = current.Generation + 1, State = "ready" },
                "update" => current with { UpdatedAt = current.ImportedAt + 1000, UpdateState = "updated" },
                "import" => current with { Enabled = true, Generation = current.Generation + 1, State = "ready" },
                _ => throw new InvalidOperationException("Unexpected fixture management operation: " + operation)
            };
            return Reply(cats[index]);
        }
        private static Task<JsonElement> Reply(object value) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            ok = true, contract = "cat.script.v2", value
        }, Json));
    }

    private static Button Button(DependencyObject root, string name) => Visuals(root).OfType<Button>().First(button =>
        AutomationProperties.GetName(button) == name || Equals(button.Content, name));
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<string> Texts(DependencyObject root) => Visuals(root).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task<T> Cell<T>(DataGrid grid, object row, int column, Func<T, bool>? predicate = null) where T : FrameworkElement
    {
        grid.ScrollIntoView(row, grid.Columns[column]); grid.UpdateLayout(); await Drain();
        var content = grid.Columns[column].GetCellContent(row) ?? throw new InvalidOperationException("Unrealized table cell");
        return Visuals(content).OfType<T>().First(value => predicate?.Invoke(value) ?? true);
    }
    private static void RaiseKey(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(args);
        Check(args.Handled, "native keyboard action handled");
    }
    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("film settings UI did not settle");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!condition()) await Task.Delay(1);
        }
        await Drain();
    }
    private static void Capture(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT") is not { Length: > 0 } root) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root); Directory.CreateDirectory(root);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(root, name)); encoder.Save(output);
    }
}
