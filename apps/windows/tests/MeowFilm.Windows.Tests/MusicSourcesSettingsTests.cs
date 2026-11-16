using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicSourcesSettingsTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SourcesSettingsCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SourcesSettingsCases()
    {
        yield return Case("wrong-source form reads every queue page and persists an exact manual choice", async () =>
        {
            await using var f = await Fixture.Create();
            var songs = Enumerable.Range(0, 205).Select(index => MusicSongFixture.Online("wy", "Excluded" + index)).ToArray();
            await f.Rpc("music.queue.replace", new { songs, index = 0 });
            int reads = f.Calls.Count("music.queue.get");
            await f.Module.PrepareSourceExclusionFormAsync();
            Equal(205, f.Module.ExclusionSongs.Count);
            Equal(3, f.Calls.Count("music.queue.get") - reads);
            var song = f.Module.ExclusionSongs.Last();
            await f.Module.AddSourceExclusionAsync(song,
                f.Module.ExclusionProviders.Single(provider => provider.Key == "official"),
                f.Module.ExclusionPlatforms.Single(platform => platform.Key == "wy"));
            var lastPage = await f.Rpc("music.queue.get", new { offset = 200, limit = 100 });
            string songId = lastPage.GetProperty("entries").EnumerateArray().Last().GetProperty("song").GetProperty("songId").GetString()!;
            True(Guid.TryParse(songId, out _) && f.Module.SourceExclusions.Single().SongId == songId,
                "a song beyond the first two pages is selectable by its real identity");
            True(!f.Module.HasCurrent, "manual exclusion never begins playback");
        });
        yield return Case("source inputs and platform labels retain text and system foregrounds across enabled and high contrast states", async () =>
        {
            await using var f = await Fixture.Create();
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Calls.Count("music.officialSources.settings.get") > 0 && f.Module.CanEditScripts);
            await Drain();
            var sources = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            Click(FindButton(sources, "添加网络源")); await Drain();
            var input = (TextBox)sources.FindName("NetworkAddress");
            var popup = (Popup)sources.FindName("NetworkPopup");
            var form = (FrameworkElement)popup.Child;
            var enabledBinding = input.GetBindingExpression(UIElement.IsEnabledProperty);
            True(enabledBinding is not null, "the real input retains its business availability binding");
            const string address = "https://example.invalid/source.js";
            input.Text = address;
            var row = f.Module.Sources.Single(item => item.Platform == "tx");
            var states = row.Platforms.Select(item => item.State).ToArray();
            True(row.Platforms.Any(item => item.State == "unsupported") && row.Platforms.Any(item => item.State != "unsupported"),
                "the built-in row supplies both supported and unsupported real platform cells");
            foreach (bool highContrast in new[] { false, true, false })
            {
                DesktopTheme.SetIsHighContrast(settings, highContrast);
                foreach (bool enabled in new[] { true, false, true })
                {
                    // The input now inherits enabled state from its popup form, not the page's visual tree.
                    sources.IsEnabled = true;
                    form.IsEnabled = enabled;
                    await Until(() => f.Module.CanEditScripts);
                    if (!popup.IsOpen) { Click(FindButton(sources, "添加网络源")); await Drain(); }
                    form.UpdateLayout(); sources.UpdateLayout(); await Drain();
                    Equal(enabled, input.IsEnabled);
                    Equal(address, input.Text);
                    True(ReferenceEquals(enabledBinding, input.GetBindingExpression(UIElement.IsEnabledProperty)), "state changes preserve the original binding");
                    var expected = highContrast ? (enabled ? SystemColors.WindowTextBrush : SystemColors.GrayTextBrush) :
                        (Brush)input.FindResource(enabled ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Disabled");
                    Equal(((SolidColorBrush)expected).Color, ((SolidColorBrush)input.Foreground).Color);
                    sources.IsEnabled = enabled; sources.UpdateLayout(); await Drain();
                    Equal(enabled, popup.IsOpen);
                    Equal(address, input.Text);
                    foreach (var platform in row.Platforms)
                    {
                        var text = Visuals(sources).OfType<TextBlock>().Single(element => element.IsVisible && ReferenceEquals(element.DataContext, platform) && element.Text == platform.Name);
                        var foreground = highContrast ? SystemColors.WindowTextBrush :
                            (Brush)text.FindResource(platform.State == "unsupported" ? "MeowFilm.Brush.Muted" : "MeowFilm.Brush.Text");
                        Equal(((SolidColorBrush)foreground).Color, ((SolidColorBrush)text.Foreground).Color);
                    }
                    True(states.SequenceEqual(row.Platforms.Select(item => item.State)), "visual state never changes platform support facts");
                }
            }
            True(popup.IsOpen, "form state changes preserve the current add session");
            sources.IsEnabled = false; await Drain();
            True(!popup.IsOpen && input.Text == address, "disabling source management dismisses the popup without discarding its draft");
            True(ReferenceEquals(enabledBinding, input.GetBindingExpression(UIElement.IsEnabledProperty)), "retirement preserves the input's business availability binding");
        });
        yield return Case("source settings table lists built-in and script rows with type, health and fixed actions", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 1);
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view);
            var sources = (MusicSourcesSettingsView)view.FindName("SourcesPage");
            var category = Visuals(view).OfType<RadioButton>().Single(button => Equals(button.Content, "音源"));
            category.IsChecked = true;
            await Until(() => f.Calls.Count("music.officialSources.settings.get") > 0 && f.Module.CanEdit);
            sources.UpdateLayout(); await Drain();
            True(f.Module.Sources.Select(row => row.Name).SequenceEqual(["网易云音乐", "QQ 音乐", "酷狗音乐", "测试音源 0"]),
                "built-in providers and the installed script share one table");
            True(f.Module.Sources.Select(row => row.TypeText).SequenceEqual(["内置", "内置", "内置", "本地"]), "source settings expectation");
            var script = f.Module.Sources[^1];
            True(script.Version == "1" && script.HasVersion && !f.Module.Sources[0].HasVersion, "the version sits under a script's name");
            True(f.Module.Sources.All(row => row.StatusText == "未检测"), "enablement is not health");
            True(script.Platforms.Select(cell => cell.Platform).SequenceEqual(["wy", "tx", "kw", "kg", "mg"]) &&
                script.Platforms.All(cell => cell.State == "idle"),
                "a never-loaded script has no declaration yet: every platform is unchecked, none is claimed unsupported");
            var builtIn = f.Module.Sources.Single(row => row.Platform == "tx");
            True(builtIn.Platforms.Single(cell => cell.Platform == "tx").State == "idle" &&
                builtIn.Platforms.Where(cell => cell.Platform != "tx").All(cell => cell.State == "unsupported"), "only the built-in platform is supported");
            var header = Visuals(sources).OfType<TextBlock>().Select(text => text.Text).ToArray();
            foreach (string column in new[] { "启用", "名称", "类型", "状态", "平台", "操作" })
                True(header.Contains(column), "column " + column);
            foreach (var row in f.Module.Sources)
            {
                var actions = Visuals(sources).OfType<Button>().Where(button => ReferenceEquals(button.DataContext, row))
                    .Where(button => button is not ManagementStatus)
                    .Select(button => (button.Content as string, button.IsEnabled)).ToArray();
                True(actions.Select(action => action.Item1).SequenceEqual(["检测", "编辑", "更新", "删除"]), "fixed inline actions");
                True(actions[0].IsEnabled && actions.Skip(1).All(action => action.IsEnabled == row.IsScript),
                    "built-in rows keep the action positions but only check");
            }
            True(Visuals(sources).OfType<Button>().All(button => !Equals(button.Content, "更多")), "no hidden more menu");
            var toggle = Visuals(sources).OfType<CheckBox>().Single(box => ReferenceEquals(box.DataContext, builtIn) &&
                box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "Enabled");
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => f.Module.CanEdit && !f.Module.Sources.Single(row => row.Platform == "tx").Enabled);
            Equal(1, f.Calls.Count("music.officialSources.settings.save"));
            var persisted = await f.Rpc("music.officialSources.settings.get");
            True(persisted.GetProperty("settings").GetProperty("enabled").EnumerateArray()
                .Select(value => value.GetString()).SequenceEqual(["wy", "kg"]), "only the toggled provider changes");
        });

        yield return Case("source settings check rows without changing enablement and keep results across refresh", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 1);
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            var script = f.Module.Sources[^1];
            await f.Module.CheckSourcesAsync([script, f.Module.Sources[0]]);
            await Until(() => f.Module.Sources.All(row => !row.Checking));
            var checkedScript = f.Module.Sources[^1];
            True(!checkedScript.Enabled && checkedScript.StatusText == "正常" &&
                checkedScript.Platforms.Single(cell => cell.Platform == "wy").State == "ok" &&
                checkedScript.Platforms.Where(cell => cell.Platform != "wy").All(cell => cell.State == "unsupported"),
                "a disabled script is checked temporarily and stays disabled");
            var wy = f.Module.Sources[0];
            True(wy.Platforms.Single(cell => cell.Platform == "wy").State is "ok" or "failed" &&
                wy.StatusText is "正常" or "异常", "a built-in check reports its sample result");
            Equal(1, f.Calls.Count("music.official.check"));
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            True(f.Module.Sources[^1].StatusText == "正常" && f.Module.Sources[0].StatusText == wy.StatusText,
                "derived check facts survive a refresh");
            True((await f.Rpc("music.sources.list")).GetProperty("items")[0].GetProperty("check").GetProperty("summary")
                .GetString() == "normal", "check facts are saved by the shared owner");
        });

        yield return Case("source settings enable update edit rollback and remove one installation", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 1);
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            await f.Module.SetSourceEnabledAsync(f.Module.Sources[^1], true);
            await Until(() => f.Module.CanEdit && f.Module.Sources[^1].Enabled);
            True(f.Module.Sources.Where(row => row.IsBuiltIn).All(row => row.Enabled),
                "enabling a script leaves every built-in provider eligible");
            f.Dialogs.ScriptPath = Path.Combine(f.Root, "update.js");
            File.WriteAllText(f.Dialogs.ScriptPath, TestScript(version: "2"), new UTF8Encoding(false));
            await f.Module.UpdateSourceAsync(f.Module.Sources[^1]);
            await Until(() => f.Module.CanEdit);
            True(f.Module.Sources[^1].Version == "2" && f.Module.Sources[^1].RollbackAvailable && f.Module.Sources[^1].Enabled, "source settings expectation");
            await f.Module.EditSourceAsync(f.Module.Sources[^1], " 改名 ", null);
            await Until(() => f.Module.CanEdit);
            Equal("改名", f.Module.Sources[^1].Name);
            await f.Module.RollbackSourceAsync(f.Module.Sources[^1]);
            await Until(() => f.Module.CanEdit);
            True(f.Module.Sources[^1].Version == "1" && f.Module.Sources[^1].Name == "改名", "rollback keeps the management name");
            await f.Module.RemoveSourceAsync(f.Module.Sources[^1]);
            await Until(() => f.Module.CanEdit);
            True(f.Module.Sources.All(row => row.IsBuiltIn) && f.Runtime.Calls.Count(method => method == "music.script.sources.update") == 1 &&
                f.Calls.Count("music.sources.edit") == 1 && f.Calls.Count("music.sources.rollback") == 1 &&
                f.Calls.Count("music.sources.remove") == 1, "row actions reach the shared module");
        });

        yield return Case("source settings select rows for batch check and remove only scripts", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 3);
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEdit);
            True(!f.Module.CanCheckSelectedSources && !f.Module.CanRemoveSelectedSources, "source settings expectation");
            f.Module.Sources[0].Selected = true;
            True(f.Module.CanCheckSelectedSources && !f.Module.CanRemoveSelectedSources, "built-in rows are never deleted");
            f.Module.Sources[3].Selected = true; f.Module.Sources[4].Selected = true;
            True(f.Module.CanRemoveSelectedSources, "source settings expectation");
            await f.Module.CheckSelectedSourcesAsync();
            await Until(() => f.Module.Sources.All(row => !row.Checking));
            Equal(2, f.Calls.Count("music.sources.check"));
            True(f.Module.Sources[3].Selected && f.Module.Sources[4].Selected, "selection survives row replacement");
            await f.Module.RemoveSelectedSourcesAsync();
            await Until(() => f.Module.CanEdit);
            True(f.Module.Sources.Select(row => row.Name).SequenceEqual(["网易云音乐", "QQ 音乐", "酷狗音乐", "测试音源 2"]), "source settings expectation");
            f.Module.AllSourcesSelected = true;
            True(f.Module.Sources.All(row => row.Selected), "source settings expectation");
        });

        yield return Case("source settings edit popup changes management facts only and stale row actions are ignored", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 6);
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            mount.Window.Width = 1100; mount.Window.Height = 430;
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var list = SourceItems(view);
            Equal(9, list.Items.Count);
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            // ManagementList reports a logical row count through ViewportHeight.
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            True(viewport.ActualHeight >= 64 && scroll.ScrollableHeight > 0, "a visible row and independent scroll survive a short host");
            var row = f.Module.Sources[3];
            var edit = Visuals(view).OfType<Button>().First(button => ReferenceEquals(button.DataContext, row) && Equals(button.Content, "编辑"));
            Click(edit); await Drain();
            var popup = (Popup)view.FindName("EditPopup");
            var name = (TextBox)view.FindName("EditName");
            True(popup.IsOpen && name.Text == row.Name && ((FrameworkElement)view.FindName("EditAddressPanel")).Visibility == Visibility.Collapsed,
                "a local source has no address to edit");
            name.Text = "新名称";
            Click((Button)view.FindName("EditSave"));
            await Until(() => f.Module.CanEdit && f.Module.Sources[3].Name == "新名称");
            var stale = Visuals(view).OfType<Button>().First(button => button.DataContext is MusicSourceRow { IsScript: true } &&
                Equals(button.Content, "删除"));
            ReplicaAnchors(settings)[0].IsChecked = true; await Drain();
            stale.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(0, f.Calls.Count("music.sources.remove"));
            Equal(9, f.Module.Sources.Count);
        });

        yield return Case("source edit survives resize offset coercion and retains its session until hidden or unloaded", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 6);
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            mount.Window.Left = 80; mount.Window.Top = 60;
            mount.Window.Width = 1100; mount.Window.Height = 430;
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var list = SourceItems(view);
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            var popup = (Popup)view.FindName("EditPopup");
            var name = (TextBox)view.FindName("EditName");
            await OpenLast();
            name.Text = "缩放后保存";
            double oldOffset = scroll.VerticalOffset;
            True(oldOffset > 0, "editing begins at the bottom of an overflowing source list");
            int writes = f.Calls.Count("music.sources.edit");
            mount.Window.Width = 1000; mount.Window.Height = 820;
            mount.Window.UpdateLayout(); await Drain();
            True(scroll.VerticalOffset < oldOffset, "growing the viewport actually coerces the old bottom offset");
            True(popup.IsOpen && name.Text == "缩放后保存", "resize and its scroll change retain the open editor and draft");
            Equal(writes, f.Calls.Count("music.sources.edit"));
            var surface = (FrameworkElement)popup.Child;
            var actual = surface.PointToScreen(new(surface.ActualWidth / 2, surface.ActualHeight / 2));
            var expected = settings.PointToScreen(new(settings.ActualWidth / 2, settings.ActualHeight / 2));
            True((actual - expected).Length < 3, "the actual editor popup remains centered on window content after resize");
            Click((Button)view.FindName("EditSave"));
            await Until(() => f.Module.CanEdit && f.Module.Sources.Last().Name == "缩放后保存");
            Equal(writes + 1, f.Calls.Count("music.sources.edit"));
            True(!popup.IsOpen, "saving the retained edit session closes the editor");
            await OpenLast(); name.Text = "隐藏时放弃";
            ReplicaAnchors(settings)[0].IsChecked = true; await Drain();
            True(!popup.IsOpen, "hiding the source page closes the editor");
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            await OpenLast();
            Equal("缩放后保存", name.Text);
            mount.Window.Content = null; await Drain();
            True(!popup.IsOpen, "unloading the source page closes the editor");
            Equal(writes + 1, f.Calls.Count("music.sources.edit"));

            async Task OpenLast()
            {
                scroll.ScrollToEnd(); view.UpdateLayout(); await Drain();
                var row = f.Module.Sources.Last();
                Click(Visuals(view).OfType<Button>().Single(button => ReferenceEquals(button.DataContext, row) && Equals(button.Content, "编辑")));
                await Drain(); True(popup.IsOpen, "the real source edit action opens its editor");
            }
        });

        yield return Case("source settings import local files in order and reject a non-HTTPS address before any request", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 40);
            await Until(() => f.Module.CanEdit);
            var scripts = f.Module.Sources.Where(row => row.IsScript).ToArray();
            True(scripts.Select(source => source.Name).SequenceEqual(Enumerable.Range(0, 40).Select(index => "测试音源 " + index)),
                "local multi-selection preserves every installation in selection order");
            True(scripts.All(source => !source.Enabled && source.Kind == MusicSourceKind.Local), "imports are disabled local sources");
            await f.Module.ImportSourceFromUrlAsync("http://sources.invalid/source.js");
            await Until(() => f.Module.CanEdit);
            True(f.Module.Status == "请输入 HTTPS .js 音源地址" && f.Module.Sources.Count == 43, "the module validates the address");
        });

        yield return Case("source management shares list chrome and keeps aligned columns and actions without horizontal scrolling", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 6);
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var management = (ManagementList)view.FindName("SourceList");
            True(!management.ShowToolbar && !management.ShowBulkActions && !management.EnableRowDrag,
                "source management reuses the list without adding book-source tools or video ordering");
            var toolbar = (StackPanel)view.FindName("SourceTitleRow");
            True(ReferenceEquals(toolbar.Style, view.FindResource("MeowFilm.ManagementTitleRow")), "shared management title chrome");
            True(toolbar.Children.OfType<Button>().Select(button => button.Content as string)
                .SequenceEqual(["添加本地文件", "添加网络源", "检测所选", "删除所选"]), "source management toolbar order");
            var deleteSelected = toolbar.Children.OfType<Button>().Last();
            True(ReferenceEquals(deleteSelected.Style, view.FindResource("MeowFilm.ManagementDeleteSelected")) &&
                ((SolidColorBrush)deleteSelected.Background).Color.A == 0, "shared transparent outlined batch delete");
            foreach (double width in new[] { 1248, 1100, 750 })
            {
                mount.Window.Width = width; mount.Window.Height = 430;
                settings.UpdateLayout(); await Drain(); view.UpdateLayout();
                var list = SourceItems(view);
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                True(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                    scroll.ScrollableWidth == 0 && scroll.ScrollableHeight > 0, "vertical-only scrolling at " + width);
                var row = f.Module.Sources[0];
                var name = Visuals(view).OfType<TextBlock>().Single(text => text.Text == row.Name && ReferenceEquals(text.DataContext, row));
                var header = Visuals(management).OfType<TextBlock>().Single(text => text.Text == "名称");
                True(Math.Abs(name.TranslatePoint(new(), view).X - header.TranslatePoint(new(), view).X) < 1,
                    "shared name column aligns at " + width);
                foreach (var button in Visuals(view).OfType<Button>().Where(button => ReferenceEquals(button.DataContext, row) && button is not ManagementStatus))
                {
                    var bounds = new Rect(button.TranslatePoint(new(), view), button.RenderSize);
                    True(bounds.Left >= 0 && bounds.Right <= view.ActualWidth, "reachable source action at " + width);
                    True(button.FontWeight == FontWeights.Normal, "source actions retain regular text");
                }
                foreach (var platform in row.Platforms)
                    True(Visuals(view).OfType<TextBlock>().Any(text => text.IsVisible &&
                        ReferenceEquals(text.DataContext, platform) && text.Text == platform.Name), "platform remains visible at " + width);
                CaptureSettings(view, "music-source-management-" + (int)width + ".png");
            }
            var first = f.Module.Sources[0];
            var check = Visuals(management).OfType<CheckBox>().Single(box =>
                System.Windows.Automation.AutomationProperties.GetName(box) == "选择 " + first.Name);
            check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            True(first.Selected && f.Module.CanCheckSelectedSources && !f.Module.CanRemoveSelectedSources,
                "shared selection reaches the existing batch owner without making built-in sources deletable");
            await f.Module.RefreshSourceManagementAsync(); await Drain();
            True(f.Module.Sources[0].Selected && management.Selected.Contains(first.Key), "selection survives row replacement by stable identity");
        });

        yield return Case("network source popup retains drafts and errors while cancellation and retirement cancel the admitted transfer", async () =>
        {
            await using var f = await Fixture.Create();
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEditScripts); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var popup = (Popup)view.FindName("NetworkPopup");
            var input = (TextBox)view.FindName("NetworkAddress");
            var save = (Button)view.FindName("NetworkSave");
            var cancel = (Button)view.FindName("NetworkCancel");
            const string address = "https://example.invalid/source.js";
            Click(FindButton(view, "添加网络源")); await Drain();
            True(popup.IsOpen && !Visuals(view).Contains(input), "network address lives only inside the add dialog");
            input.Text = address;
            Click(cancel); await Drain();
            True(!popup.IsOpen && input.Text == address, "dismissal retains the draft without submitting");
            Equal(0, f.Calls.Count("music.sources.download"));
            Click(FindButton(view, "添加网络源")); await Drain();
            Equal(address, input.Text);
            f.Calls.FailNext("music.sources.download", "SOURCE_DOWNLOAD_FAILED");
            Click(save); await Until(() => f.Module.CanEditScripts && f.Module.Status == "音源下载失败"); await Drain();
            True(popup.IsOpen && input.Text == address && save.IsEnabled, "failed download keeps its form and draft for retry");
            CaptureSettings((FrameworkElement)popup.Child, "music-network-source-popup.png");
            foreach (bool dismissImmediately in new[] { true, false })
            {
                var held = f.Calls.HoldNext("music.sources.download", failure: "SOURCE_DOWNLOAD_FAILED");
                // Fail before the real network boundary, then hold its reply to
                // observe the actual admission token without contacting an upstream.
                Click(save);
                if (dismissImmediately) Click(cancel);
                await held.Arrived.Task;
                var token = f.Calls.Log.Last(call => call.Method == "music.sources.download").Token;
                if (!dismissImmediately)
                {
                    ReplicaAnchors(settings)[0].IsChecked = true; await Drain();
                }
                await Until(() => token.IsCancellationRequested);
                True(!popup.IsOpen && input.Text == address, "retirement cancels download and preserves the local draft");
                held.Release(); await Until(() => f.Module.CanEditScripts); await Drain();
                if (!dismissImmediately)
                {
                    ReplicaAnchors(settings)[^1].IsChecked = true;
                    await Until(() => f.Module.CanEditScripts); await Drain();
                }
                Click(FindButton(view, "添加网络源")); await Drain();
                True(popup.IsOpen && input.Text == address, "a stale completion cannot dismiss a later add session");
            }
            mount.Window.Content = null; await Drain();
            True(!popup.IsOpen, "unloading closes the add dialog");
        });
    }

    private static ListBox SourceItems(MusicSourcesSettingsView view) =>
        Visuals((ManagementList)view.FindName("SourceList")).OfType<ListBox>().Single();

    private static async Task InstallSettingsSources(Fixture f, int count)
    {
        f.Dialogs.ScriptPaths = Enumerable.Range(0, count).Select(index =>
        {
            var path = Path.Combine(f.Root, $"settings-source-{index}.js");
            File.WriteAllText(path, TestScript(name: "测试音源 " + index), new UTF8Encoding(false));
            return path;
        }).ToArray();
        try { await f.Module.ImportSourceAsync(); }
        finally { f.Dialogs.ScriptPaths = null; }
        await Until(() => f.Module.CanEdit);
    }
}
