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
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmSettingsUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("draft testing passes all four groups and query without saving", DraftTesting);
        yield return Case("row save add delete and section restore preserve whole panel scope", RuleEditing);
        yield return Case("new-rule inputs reach the Node normalizer without native whitespace or regex interpretation", NativeRuleInputs);
        yield return Case("rule cancellation and errors cannot publish into a hidden view", RuleLifetime);
        yield return Case("local scripts preserve valid UTF8 bytes and reject invalid UTF8", LocalScriptEncoding);
        yield return Case("cat edits toggles restart and bulk deletion reach the source owner", CatManagement);
        yield return Case("centered edits survive resize and offset coercion while confirmations retire without saving", PopupLifetime);
        yield return Case("video options radio selection and order survive a new owner", VideoSettings);
        yield return Case("video detection deletion and explicit reimport use the selected installation", VideoActions);
        yield return Case("failed source operations restore switches and notify with retry", SourceFailure);
        yield return Case("native keyboard scrolling high contrast and screenshots cover both window sizes", Layout);
        yield return Case("empty source pages keep their actions and columns", Empty);
        yield return Case("data sources echo one token and cookie and save server proxy options independently", DataSources);
        yield return Case("site clean lives under matching and playback uses half-width shared order lists", PlaybackLayout);
        yield return Case("cache controls invalidate exact domains and drain both providers on failure", MetadataCache);
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
        int writes = scene.RulesRuntime.Writes;
        scene.RulesRuntime.Result = new()
        {
            Section = FilmRuleSection.Search, Type = "success", Text = "净化后：作品2",
            InputText = "作品2 更新", ProcessedText = "作品2", GroupKey = "作品2",
            TrailingDigits = "2", Previews = ["作品2"]
        };
        Click(Button(scene.View, "测试搜索列表净化"));
        await Until(() => !scene.View.RulesBusy);
        var call = scene.RulesRuntime.Requests.Last(request => request.Method == "film.rules.preview").Input;
        Check(call.GetProperty("request").GetProperty("query").GetString() == "作品2", "query tail context reaches the service");
        Check(call.GetProperty("rules").GetProperty("episodeRules")[0].GetProperty("pattern").GetString() == "draft-episode" &&
            call.GetProperty("rules").GetProperty("movieRules")[0].GetProperty("replace").GetString() == "draft-movie",
            "test consumes the current entire draft");
        Check(scene.RulesRuntime.Writes == writes && scene.Rules.GetSavedSnapshot().EpisodeRules[0].Pattern == "episode",
            "testing neither writes nor changes the saved owner snapshot");
        var result = scene.Element<StackPanel>("SearchResults");
        Check(Texts(result).Contains("聚合 Key：作品2") && Texts(result).Contains("搜索尾号保护：2"),
            "the returned group key and tail are visible business data");
        Check(scene.Editor("SearchEditor").Rows[0].Preview == "净化后：作品2", "returned cumulative previews stay in rule order");
        movie.Rows[0].Pattern = "next-draft";
        Check(result.Children.Count == 0, "changing a draft clears stale results");
        await scene.Select("cats"); await scene.Select("rules");
        Check(movie.Rows[0].Pattern == "next-draft" && scene.RulesRuntime.Writes == writes,
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
        Check(!scene.RulesRuntime.Requests.Any(request => request.Method == "film.rules.normalize"),
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
        await using var scene = await Scene.Create();
        foreach (string name in new[] { "MovieEditor", "EpisodeEditor" })
        {
            var editor = scene.Editor(name);
            var pattern = (TextBox)editor.FindName("NewPattern");
            var replace = (TextBox)editor.FindName("NewReplace");
            pattern.Text = "\u0085"; replace.Text = "X";
            scene.RulesRuntime.Normalize = new FilmMatchRule("\u0085", "X");
            Click((Button)editor.FindName("AddButton")); await Until(() => !scene.View.RulesBusy);
            var call = scene.RulesRuntime.Requests.Last(request => request.Method == "film.rules.normalize");
            var saved = scene.Rules.GetSavedSnapshot();
            Check(call.Input.GetProperty("kind").GetString() == "match" && call.Input.GetProperty("pattern").GetString() == "\u0085" &&
                (name == "MovieEditor" ? saved.MovieRules : saved.EpisodeRules)[^1] == new FilmMatchRule("\u0085", "X"),
                "each editor sends the exact WPF NEL input to Node and saves its receipt without native trimming");
            pattern.Text = " \t\uFEFF"; replace.Text = "invalid"; scene.RulesRuntime.Normalize = null;
            int count = editor.Rows.Count, writes = scene.RulesRuntime.Writes, notices = scene.Notices.Count;
            Click((Button)editor.FindName("AddButton")); await Until(() => !scene.View.RulesBusy);
            Check(scene.RulesRuntime.Requests.Last(request => request.Method == "film.rules.normalize").Input.GetProperty("pattern").GetString() == " \t\uFEFF" &&
                editor.Rows.Count == count && scene.RulesRuntime.Writes == writes && scene.Notices.Count > notices && scene.Notices[^1].Contains("请输入正则", StringComparison.Ordinal),
                "Node's empty normalization receipt prevents save and reports the existing UI error");
        }
        await using var reopened = new FilmRuleService(scene.RulesRuntime);
        await reopened.InitializeAsync();
        Check(reopened.GetSavedSnapshot().MovieRules[^1].Pattern == "\u0085" &&
            reopened.GetSavedSnapshot().EpisodeRules[^1].Pattern == "\u0085",
            "a new settings consumer reads both acknowledged Node rule groups");
    }

    private static async Task RuleLifetime()
    {
        await using var scene = await Scene.Create();
        scene.Box("EpisodeInput").Text = "一集";
        scene.RulesRuntime.HoldProcess = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(Button(scene.View, "测试列表净化与集数匹配"));
        await Until(() => scene.RulesRuntime.ProcessEntered);
        Check(scene.View.RulesBusy && !scene.Element<StackPanel>("RuleSections").IsEnabled, "busy rules remain legible but cannot mutate");
        Capture(scene.View, "film-settings-rules-busy.png");
        await scene.Select("cats");
        scene.RulesRuntime.HoldProcess.SetResult();
        await Until(() => !scene.View.RulesBusy);
        Check(scene.RulesRuntime.ProcessCancelled && scene.Element<StackPanel>("EpisodeResults").Children.Count == 0,
            "a noncooperative late result cannot publish after the page was hidden");
        scene.RulesRuntime.HoldProcess = null;
        await scene.Select("rules");
        scene.RulesRuntime.FailProcess = true;
        Click(Button(scene.View, "测试列表净化与集数匹配")); await Until(() => !scene.View.RulesBusy);
        Check(scene.Notices.Any(message => message.Contains("fixture rule service failure", StringComparison.Ordinal)) &&
            scene.View.CanEditRules, "rule service failure uses the existing notice route and releases busy state");
        scene.RulesRuntime.FailProcess = false;
        scene.RulesRuntime.Result = new() { Section = FilmRuleSection.Episode, Type = "success", Text = "命中第 1 条",
            InputText = "一集", ProcessedText = "一集", Matched = true, MatchedRuleIndex = 1, RewrittenText = "E1" };
        Click(Button(scene.View, "测试列表净化与集数匹配")); await Until(() => !scene.View.RulesBusy);
        Check(Texts(scene.Element<StackPanel>("EpisodeResults")).Contains("改写结果：E1"), "testing can recover after failure");
        scene.Box("EpisodeInput").Text = "离页输入";
        scene.RulesRuntime.ProcessEntered = scene.RulesRuntime.ProcessCancelled = false;
        scene.RulesRuntime.HoldProcess = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(Button(scene.View, "测试列表净化与集数匹配"));
        await Until(() => scene.RulesRuntime.ProcessEntered);
        scene.Window.Content = null; await Drain();
        scene.RulesRuntime.HoldProcess.SetResult(); await Until(() => !scene.View.RulesBusy);
        Check(scene.RulesRuntime.ProcessCancelled && scene.Element<StackPanel>("EpisodeResults").Children.Count == 0,
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
        int restores = scene.RulesRuntime.Requests.Count(request => request.Method == "film.rules.restore");
        Click(restore); await Drain();
        scene.Window.Width = 1050; scene.Window.Height = 650;
        scene.Window.UpdateLayout(); await Drain();
        AssertCentered();
        Click(scene.Element<Button>("PopupCancel")); await Drain();
        Check(!popup.IsOpen && scene.Editor("EpisodeCleanEditor").Rows[0].Pattern == original &&
            scene.RulesRuntime.Requests.Count(request => request.Method == "film.rules.restore") == restores &&
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

    private static async Task DataSources()
    {
        await using var scene = await Scene.Create(dataSettings: true);
        var data = scene.Data!;
        await scene.Select("metadata");
        await Until(() => scene.View.CanEditMetadata);
        Check(scene.Box("MetadataApiToken").Text == "synthetic.v4.token" &&
            scene.Box("DoubanCookie").Text == "synthetic_cookie=initial", "settings echo their actual plain values");
        Check(!Visuals(scene.View).OfType<PasswordBox>().Any(), "no secret-mask controls or second token editor");
        scene.Box("MetadataApiToken").Text = "synthetic-v3-key";
        scene.Box("MetadataApiBase").Text = "https://fixture.invalid/pan/tmdb/3";
        scene.Box("MetadataImageBase").Text = "https://fixture.invalid/pan/tmdb-img";
        scene.Box("MetadataLanguage").Text = "en-US"; scene.Box("MetadataRegion").Text = "US";
        scene.Element<CheckBox>("MetadataIncludeAdult").IsChecked = true;
        Click(Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Tag, "tmdb")));
        await Until(() => !scene.View.MetadataBusy);
        var saved = data.Calls.Last(call => call.Method == "tmdb.data.settings.update").Input;
        Check(saved.GetProperty("apiKey").GetString() == "synthetic-v3-key" && saved.GetProperty("accessToken").GetString() == "" &&
            saved.GetProperty("language").GetString() == "en-US" && saved.GetProperty("region").GetString() == "US" &&
            saved.GetProperty("includeAdult").GetBoolean(), "single token replaces prior auth and all query options reach the runtime");
        Check(scene.Box("MetadataApiToken").Text == "synthetic-v3-key", "successful save stays editable without a configured placeholder");
        scene.Element<ComboBox>("DoubanDataProxy").SelectedValue = "custom";
        scene.Element<ComboBox>("DoubanImageProxy").SelectedValue = "cdn-tx";
        scene.Box("DoubanDataAddress").Text = "https://fixture.invalid/nested/";
        scene.Box("DoubanCookie").Text = "";
        Click(Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Tag, "douban")));
        await Until(() => !scene.View.MetadataBusy);
        saved = data.Calls.Last(call => call.Method == "douban.data.settings.update").Input;
        Check(saved.GetProperty("apiProxy").GetString() == "https://fixture.invalid/nested/" &&
            saved.GetProperty("searchBase").GetString() == "https://search.douban.com" &&
            saved.GetProperty("imageBase").GetString() == "https://img.doubanio.cmliussss.net" &&
            saved.GetProperty("cookie").GetString() == "", "independent proxy choices and empty-cookie clear use the same save");
        data.FailWrite = true;
        scene.Box("MetadataApiToken").Text = "unsaved";
        Click(Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Tag, "tmdb")));
        await Until(() => !scene.View.MetadataBusy);
        Check(scene.Box("MetadataApiToken").Text == "unsaved" &&
            data.Tmdb["apiKey"]?.ToString() == "synthetic-v3-key" && scene.Notices.Contains("数据源设置操作失败"),
            "a failed save keeps the draft and the prior runtime receipt");
        foreach (var width in new[] { 1248, 620 })
        {
            scene.Window.Width = width; await Drain(); scene.View.UpdateLayout();
            Check(scene.Box("MetadataApiToken").ActualWidth > 200, "single-line fields remain usable in both window widths");
            Capture(scene.View, $"film-settings-data-sources-{width}.png");
        }
    }

    private static async Task PlaybackLayout()
    {
        await using var scene = await Scene.Create(dataSettings: true);
        var cleanHost = scene.Element<ContentControl>("SiteCleanHost");
        var playback = (FilmPlaybackSettingsView)cleanHost.Content;
        await Until(() => playback.Idle && ((TextBox)playback.FindName("CleanKeywords")).Text == "直播,预告");
        Check(((Border)playback.FindName("CleanForm")).IsVisible, "matching includes its site-clean editor");
        ((TextBox)playback.FindName("CleanKeywords")).Text = " 广告,，直播, ";
        Click(Button(playback, "保存站点净化")); await Until(() => playback.Idle);
        Check(FilmPreferences.Read(scene.Data!.Preferences).SiteCleanKeywords.SequenceEqual(["广告", "直播"]),
            "the single line preserves order and ignores only empty comma-separated entries");
        await scene.Select("preferences"); await Until(() => playback.Idle && playback.ManualGroups.Count == 1);
        var navigation = scene.Element<StackPanel>("BusinessNavigation").Children.OfType<RadioButton>().ToArray();
        Check(navigation.Length == 1 && Equals(navigation[0].Content, "智能播放设置"), "history and favorites are not settings destinations");
        Check(cleanHost.Content is null && scene.Element<Grid>("BusinessPage").Children.Contains(playback),
            "only one settings consumer is reparented to the current area");
        var order = (SettingsPriorityList)playback.FindName("RuleOrder");
        playback.UpdateLayout();
        Check(!order.ShowToggle && !Visuals(order).OfType<CheckBox>().Any(box => box.IsVisible) &&
            order.ActualWidth > 150 && order.ActualWidth < playback.ActualWidth * .6,
            "film reuses the music order control without an enable flag or a full-width list");
        Check(playback.ManualGroups.Single().Rows.Single().DetailId == "0" &&
            playback.BlockGroups.Single().Rows.Single().Pan == "0", "both management lists retain typed source identity");
        Capture(scene.View, "film-settings-smart-playback.png");
        await scene.Select("rules"); await Until(() => playback.Idle && ((Border)playback.FindName("CleanForm")).IsVisible);
        Check(ReferenceEquals(cleanHost.Content, playback) && scene.Element<Grid>("BusinessPage").Children.Count == 0,
            "returning to matching cannot leave a second copy or a stale management form");
    }

    private static async Task MetadataCache()
    {
        await using var scene = await Scene.Create(dataSettings: true);
        var data = scene.Data!;
        var cache = new FilmMetadataCacheSettingsView(scene.Metadata!);
        scene.Window.Content = cache; await Drain();
        foreach (var (tag, domains) in new[] { ("douban", new[] { "douban" }), ("tmdb", new[] { "tmdb" }), ("both", new[] { "douban", "tmdb" }) })
        {
            int before = data.Calls.Count;
            Click(Visuals(cache).OfType<Button>().Single(button => Equals(button.Tag, tag)));
            await Until(() => ((WrapPanel)cache.FindName("Actions")).IsEnabled);
            var calls = data.Calls.Skip(before).ToArray();
            Check(calls.Select(call => call.Method).All(method => method == "film.data.invalidate") &&
                calls.Select(call => call.Input.GetProperty("domain").GetString()).SequenceEqual(domains),
                "cache buttons issue only the exact provider invalidations");
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        data.HeldInvalidation = gate.Task; data.FailDomain = "douban";
        try
        {
            Click(Visuals(cache).OfType<Button>().Single(button => Equals(button.Tag, "both")));
            var drain = scene.Metadata!.PrepareShutdownAsync();
            Check(!drain.IsCompleted && !((WrapPanel)cache.FindName("Actions")).IsEnabled &&
                !data.InvalidationToken.IsCancellationRequested, "the second admitted invalidation drains even after the first failed");
            gate.SetResult(); await drain; await Drain();
            Check(scene.Notices.Contains("缓存清理失败") && data.Tmdb["apiKey"]?.ToString() == "synthetic-key" &&
                FilmPreferences.Read(data.Preferences).SiteCleanKeywords.SequenceEqual(["直播", "预告"]),
                "partial failure is not success and does not clear settings or preferences");
        }
        finally { gate.TrySetResult(); }
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MemoryStore Store = new();
        internal readonly RecordingRules RulesRuntime = new();
        internal readonly RecordingRuntime Runtime;
        internal readonly FilmRuleService Rules;
        internal readonly FilmSourceService Sources;
        internal readonly FilmSettingsView View;
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        internal readonly DataSettingsRuntime? Data;
        internal readonly FilmMetadataService? Metadata;
        private Scene(int cats, bool dataSettings)
        {
            Runtime = new(cats);
            Rules = new(RulesRuntime); Sources = new(Runtime, Store);
            if (dataSettings)
            {
                Data = new(); Metadata = new(Data);
                View = new(Rules, Sources, new FilmPlaybackSettings(Data), () => null, Metadata);
            }
            else View = new(Rules, Sources);
            Window = new() { Content = View, Width = 1248, Height = 820, Left = -10000, Top = -10000,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            {
                if (args.Message.Length > 0) Notices.Add(args.Message);
            }));
        }
        internal static async Task<Scene> Create(int cats = 2, bool dataSettings = false)
        {
            var scene = new Scene(cats, dataSettings);
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
            var nav = Element<StackPanel>("SettingsNavigation").Children.OfType<RadioButton>()
                .Concat(Element<StackPanel>("BusinessNavigation").Children.OfType<RadioButton>()).Single(button => Equals(button.Tag, page));
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
            RulesRuntime.HoldProcess?.TrySetResult();
            await View.PreparePlaybackSettingsShutdownAsync();
            if (Metadata is not null) await Metadata.DisposeAsync();
            Window.Content = null; Window.Close(); await Drain();
            await Rules.DisposeAsync(); await Sources.DisposeAsync(); await Store.DisposeAsync();
        }
    }

    private sealed class DataSettingsRuntime : IRuntimeClient
    {
        internal readonly List<(string Method, JsonElement Input)> Calls = [];
        internal Dictionary<string, object?> Tmdb = new()
        {
            ["apiBase"] = "https://api.themoviedb.org/3", ["imageBase"] = "https://image.tmdb.org",
            ["apiKey"] = "synthetic-key", ["accessToken"] = "synthetic.v4.token",
            ["language"] = "zh-CN", ["region"] = "CN", ["includeAdult"] = false
        };
        private Dictionary<string, object?> douban = new()
        {
            ["apiBase"] = "https://m.douban.com", ["searchBase"] = "https://search.douban.com",
            ["imageBase"] = "https://img3.doubanio.com", ["apiProxy"] = "", ["imageProxy"] = "", ["cookie"] = "synthetic_cookie=initial"
        };
        internal JsonElement Preferences = FilmJson.Encode(FilmPreferences.Defaults with { SiteCleanKeywords = ["直播", "预告"] });
        internal bool FailWrite;
        internal string? FailDomain;
        internal Task? HeldInvalidation;
        internal CancellationToken InvalidationToken;
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = FilmJson.Encode(parameters ?? new { }); Calls.Add((method, input));
            if (method == "film.data.invalidate")
            {
                InvalidationToken = cancellationToken;
                if (FilmJson.Text(input, "domain") == FailDomain) return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                if (HeldInvalidation is not null) await HeldInvalidation;
                return FilmRuntimeProbe.Ok(FilmJson.Encode(new { }));
            }
            if (method == "film.settings.get") return FilmRuntimeProbe.Ok(Preferences);
            if (method == "film.settings.set") { Preferences = input.Clone(); return FilmRuntimeProbe.Ok(Preferences); }
            if (method == "film.library.get") return FilmRuntimeProbe.Ok(FilmJson.Encode(new
            {
                manual = new[] { new { id = "manual", targetId = "tmdb:tv:7", title = "管理样例", sourceKey = "source", detailId = 0, enabled = true } },
                blocks = new[] { new { targetId = "tmdb:tv:7", sourceKey = "source", detailId = 0, lineId = 0 } }
            }));
            bool tmdb = method.StartsWith("tmdb.", StringComparison.Ordinal);
            var values = tmdb ? Tmdb : douban;
            Check(method.EndsWith(".settings.get", StringComparison.Ordinal) || method.EndsWith(".settings.update", StringComparison.Ordinal),
                "settings must not fetch data or restart a runtime");
            if (method.EndsWith(".update", StringComparison.Ordinal))
            {
                if (FailWrite) return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                foreach (var item in input.EnumerateObject()) values[item.Name] = item.Value.Clone();
            }
            return FilmRuntimeProbe.Ok(FilmJson.Encode(values));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Settings do not upload scripts.");
    }

    // Wire receipts test UI/service wiring. Actual rule semantics belong to the shared runtime suite.
    private sealed class RecordingRules : IRuntimeClient
    {
        internal readonly List<(string Method, JsonElement Input)> Requests = [];
        internal TaskCompletionSource? HoldProcess;
        internal bool ProcessEntered, ProcessCancelled, FailProcess;
        internal int Writes;
        internal FilmMatchRule? Normalize = new("added", "$1", "gi");
        internal FilmRuleResult Result = new() { Section = FilmRuleSection.Episode, Type = "success", Text = "命中第 1 条" };
        private FilmRuleSet saved = new()
        {
            EpisodeCleanRegexRules = ["clean"], EpisodeRules = [new("episode", "E$1", "i")],
            MovieRules = [new("movie", "", "i")], AggregateRegexRules = ["search"]
        };
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var value = FilmJson.Encode(parameters ?? new { }); Requests.Add((method, value));
            JsonElement reply;
            if (method == "film.rules.preview")
            {
                ProcessEntered = true;
                using var registration = cancellationToken.Register(() => ProcessCancelled = true);
                if (HoldProcess is not null) await HoldProcess.Task;
                if (FailProcess) throw new InvalidOperationException("fixture rule service failure");
                reply = JsonSerializer.SerializeToElement(Result, FilmRuleService.Json);
            }
            else if (method == "film.rules.normalize")
                reply = value.GetProperty("kind").GetString() == "match"
                    ? JsonSerializer.SerializeToElement(Normalize, FilmRuleService.Json) : FilmJson.Encode("added-clean");
            else
            {
                if (method == "film.rules.set") { saved = value.GetProperty("rules").Deserialize<FilmRuleSet>(Json)!; Writes++; }
                else if (method == "film.rules.restore")
                {
                    saved = value.GetProperty("rules").Deserialize<FilmRuleSet>(Json)! with
                    { EpisodeCleanRegexRules = ["restored-clean"], EpisodeRules = [new("restored-episode", "E$1", "i")] };
                    Writes++;
                }
                else if (method != "film.rules.get") throw new InvalidOperationException(method);
                reply = FilmRuleConsumerRuntime.Snapshot(saved, "acknowledged-" + Writes);
            }
            return FilmSearchConsumerRuntime.Success(reply);
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Rules do not upload source.");
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
