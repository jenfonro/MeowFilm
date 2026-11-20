using FontWeights = Microsoft.UI.Text.FontWeights;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using global::Windows.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
        yield return Case("late script file picker result cannot import into a retired source settings owner", async () =>
        {
            await using var f = await Fixture.Create();
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.Sources.Count > 0 && f.Module.CanEditScripts);
            await Drain();
            string path = Path.Combine(f.Root, "retired-picker-source.js");
            await File.WriteAllTextAsync(path, TestScript(name: "不应安装"), new UTF8Encoding(false));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var picked = new TaskCompletionSource<string[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Dialogs.ScriptPicker = owner =>
            {
                True(ReferenceEquals(owner, settings), "script picker receives its source settings window owner");
                entered.TrySetResult();
                return picked.Task;
            };
            var importing = f.Module.ImportSourceAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                ReplicaAnchors(settings)[0].IsChecked = true;
                await Drain();
            }
            finally { picked.TrySetResult([path]); await importing; }
            Equal(0, (await f.Rpc("music.sources.list")).GetProperty("items").GetArrayLength());
            True(f.Module.Sources.All(row => row.IsBuiltIn), "retired picker never installs or publishes a script");
            Equal("", f.Module.Status);
        });
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
            var popup = (ContentDialog)sources.FindName("NetworkPopup");
            var form = (FrameworkElement)popup.Content;
            var enabledBinding = input.GetBindingExpression(Control.IsEnabledProperty);
            True(enabledBinding is not null, "the real input retains its business availability binding");
            const string address = "https://example.invalid/source.js";
            input.Text = address;
            var row = f.Module.Sources.Single(item => item.Platform == "tx");
            var states = row.Platforms.Select(item => item.State).ToArray();
            True(row.Platforms.Any(item => item.State == "unsupported") && row.Platforms.Any(item => item.State != "unsupported"),
                "the built-in row supplies both supported and unsupported real platform cells");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                settings.RequestedTheme = theme;
                bool highContrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
                foreach (bool enabled in new[] { true, false, true })
                {
                    // The input now inherits enabled state from its popup form, not the page's visual tree.
                    sources.IsEnabled = true;
                    popup.IsEnabled = enabled;
                    await Until(() => f.Module.CanEditScripts);
                    if (!MusicTestUi.IsDialogOpen(popup)) { Click(FindButton(sources, "添加网络源")); await Drain(); }
                    form.UpdateLayout(); sources.UpdateLayout(); await Drain();
                    Equal(enabled, input.IsEnabled);
                    Equal(address, input.Text);
            True(ReferenceEquals(enabledBinding, input.GetBindingExpression(Control.IsEnabledProperty)), "state changes preserve the original binding");
                    var expected = highContrast ? (enabled ? SystemSettingsBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) : SystemSettingsBrush(global::Windows.UI.ViewManagement.UIElementType.GrayText)) :
                        (Brush)DesktopVisual.Resource(input, enabled ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Disabled");
                    Equal(((SolidColorBrush)expected).Color, ((SolidColorBrush)input.Foreground).Color);
                    sources.IsEnabled = enabled; sources.UpdateLayout(); await Drain();
                    Equal(enabled, MusicTestUi.IsDialogOpen(popup));
                    Equal(address, input.Text);
                    foreach (var platform in row.Platforms)
                    {
                        var text = Visuals(sources).OfType<TextBlock>().Single(element => DesktopVisual.IsVisible(element) && ReferenceEquals(element.DataContext, platform) && element.Text == platform.Name);
                        var foreground = highContrast ? SystemSettingsBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText) :
                            (Brush)DesktopVisual.Resource(text, platform.State == "unsupported" ? "MeowFilm.Brush.Muted" : "MeowFilm.Brush.Text");
                        Equal(((SolidColorBrush)foreground).Color, ((SolidColorBrush)text.Foreground).Color);
                    }
                    True(states.SequenceEqual(row.Platforms.Select(item => item.State)), "visual state never changes platform support facts");
                }
            }
            True(MusicTestUi.IsDialogOpen(popup), "form state changes preserve the current add session");
            sources.IsEnabled = false; await Drain();
            True(!MusicTestUi.IsDialogOpen(popup) && input.Text == address, "disabling source management dismisses the popup without discarding its draft");
            True(ReferenceEquals(enabledBinding, input.GetBindingExpression(Control.IsEnabledProperty)), "retirement preserves the input's business availability binding");
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
            toggle.SetValue(ToggleButton.IsCheckedProperty, false);
            FilmUiTest.Click(toggle);
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
            mount.Window.Resize(1100, 430);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var list = SourceItems(view);
            Equal(9, list.Items.Count);
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            // ManagementList reports a logical row count through ViewportHeight.
            var viewport = Visuals(scroll).OfType<ScrollContentPresenter>().Single();
            True(viewport.ActualHeight >= 64 && scroll.ScrollableHeight > 0, "a visible row and independent scroll survive a short host");
            var row = f.Module.Sources[3];
            var edit = Visuals(view).OfType<Button>().First(button => ReferenceEquals(button.DataContext, row) && Equals(button.Content, "编辑"));
            Click(edit); await Drain();
            var popup = (ContentDialog)view.FindName("EditPopup");
            var name = (TextBox)view.FindName("EditName");
            True(MusicTestUi.IsDialogOpen(popup) && name.Text == row.Name && ((FrameworkElement)view.FindName("EditAddressPanel")).Visibility == Visibility.Collapsed,
                "a local source has no address to edit");
            name.Text = "新名称";
            Click((Button)view.FindName("EditSave"));
            await Until(() => f.Module.CanEdit && f.Module.Sources[3].Name == "新名称");
            var stale = Visuals(view).OfType<Button>().First(button => button.DataContext is MusicSourceRow { IsScript: true } &&
                Equals(button.Content, "删除"));
            ReplicaAnchors(settings)[0].IsChecked = true; await Drain();
            FilmUiTest.Click(stale);
            Equal(0, f.Calls.Count("music.sources.remove"));
            Equal(9, f.Module.Sources.Count);
        });

        yield return Case("source edit survives resize offset coercion and retains its session until hidden or unloaded", async () =>
        {
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 6);
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            mount.Window.AppWindow.Move(new global::Windows.Graphics.PointInt32(80, 60));
            mount.Window.Resize(1100, 430);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var list = SourceItems(view);
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            var popup = (ContentDialog)view.FindName("EditPopup");
            var name = (TextBox)view.FindName("EditName");
            await OpenLast();
            name.Text = "缩放后保存";
            double oldOffset = scroll.VerticalOffset;
            True(oldOffset > 0, "editing begins at the bottom of an overflowing source list");
            int writes = f.Calls.Count("music.sources.edit");
            mount.Window.Resize(1000, 820);
            DesktopTestUi.WindowRoot(mount.Window).UpdateLayout(); await Drain();
            await Until(() => scroll.VerticalOffset < oldOffset);
            True(scroll.VerticalOffset < oldOffset, "growing the viewport actually coerces the old bottom offset");
            True(MusicTestUi.IsDialogOpen(popup) && name.Text == "缩放后保存", "resize and its scroll change retain the open editor and draft");
            Equal(writes, f.Calls.Count("music.sources.edit"));
            var surface = (FrameworkElement)popup.Content;
            // The native dialog frame is centered; its content has template padding and title space.
            FrameworkElement? frame = null;
            for (var node = VisualTreeHelper.GetParent(surface); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is FrameworkElement { Name: "BackgroundElement" } background) { frame = background; break; }
            True(frame is not null, "the actual popup contains the native dialog frame");
            var root = (FrameworkElement)settings.XamlRoot.Content;
            Point ActualCenter() => frame!.TransformToVisual(root).TransformPoint(new(frame.ActualWidth / 2, frame.ActualHeight / 2));
            Point ExpectedCenter() => new(root.ActualWidth / 2, root.ActualHeight / 2);
            bool Centered() => MusicTestUi.IsDialogOpen(popup) && frame!.ActualWidth > 0 && frame.ActualHeight > 0 &&
                Math.Abs(ActualCenter().X - ExpectedCenter().X) < 3 && Math.Abs(ActualCenter().Y - ExpectedCenter().Y) < 3;
            InvalidOperationException? centeringFailure = null;
            try { await Until(Centered); }
            catch (TimeoutException error)
            {
                FrameworkElement? layout = null;
                for (var node = VisualTreeHelper.GetParent(frame!); node is not null; node = VisualTreeHelper.GetParent(node))
                    if (node is FrameworkElement { Name: "LayoutRoot" } nativeRoot) { layout = nativeRoot; break; }
                centeringFailure = new InvalidOperationException($"the actual native editor remains centered on the shared root after resize: " +
                    $"frame={frame!.ActualWidth}x{frame.ActualHeight} center={ActualCenter()}; " +
                    $"root={root.ActualWidth}x{root.ActualHeight} center={ExpectedCenter()}; XamlRoot.Size={settings.XamlRoot.Size}; " +
                    $"LayoutRoot={layout?.ActualWidth}x{layout?.ActualHeight} " +
                    $"LayoutRootOrigin={layout?.TransformToVisual(root).TransformPoint(new())}; dialogOpen={MusicTestUi.IsDialogOpen(popup)}", error);
            }
            Click((Button)view.FindName("EditSave"));
            await Until(() => f.Module.CanEdit && f.Module.Sources.Last().Name == "缩放后保存");
            Equal(writes + 1, f.Calls.Count("music.sources.edit"));
            True(!MusicTestUi.IsDialogOpen(popup), "saving the retained edit session closes the editor");
            await OpenLast(); name.Text = "隐藏时放弃";
            ReplicaAnchors(settings)[0].IsChecked = true; await Drain();
            True(!MusicTestUi.IsDialogOpen(popup), "hiding the source page closes the editor");
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            await OpenLast();
            Equal("缩放后保存", name.Text);
            mount.Window.Content = null; await Drain();
            True(!MusicTestUi.IsDialogOpen(popup), "unloading the source page closes the editor");
            Equal(writes + 1, f.Calls.Count("music.sources.edit"));
            if (centeringFailure is not null) throw centeringFailure;

            async Task OpenLast()
            {
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); view.UpdateLayout();
                await Until(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);
                var row = f.Module.Sources.Last();
                await Until(() => Visuals(view).OfType<Button>().Any(button => button.IsLoaded &&
                    ReferenceEquals(button.DataContext, row) && Equals(button.Content, "编辑")));
                var button = Visuals(view).OfType<Button>().Single(button => button.IsLoaded &&
                    ReferenceEquals(button.DataContext, row) && Equals(button.Content, "编辑"));
                Click(button);
                try { await Until(() => MusicTestUi.IsDialogOpen(popup)); }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException($"source editor did not open: button loaded={button.IsLoaded} " +
                        $"visible={DesktopVisual.IsVisible(button)} enabled={button.IsEnabled}; CanEdit={f.Module.CanEdit} " +
                        $"rowCurrent={f.Module.Sources.Contains(row)} contentLoaded={(popup.Content as FrameworkElement)?.IsLoaded}", error);
                }
                True(MusicTestUi.IsDialogOpen(popup), "the real source edit action opens its editor");
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
            True(Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT") is { Length: > 0 },
                "source management fixture requires MEOWFILM_TEST_RUNTIME_ROOT with the packaged aggregate runtime");
            await using var f = await Fixture.Create();
            await InstallSettingsSources(f, 6);
            True(f.Module.Sources.Count == 9 && f.Module.Sources.Count(row => row.IsScript) == 6,
                "source management fixture requires six imported scripts and three built-in sources; " +
                $"sources={f.Module.Sources.Count}; scripts={f.Module.Sources.Count(row => row.IsScript)}; status={f.Module.Status}");
            var settings = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(settings);
            ReplicaAnchors(settings)[^1].IsChecked = true;
            await Until(() => f.Module.CanEdit); await Drain();
            var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage");
            var management = (ManagementList)view.FindName("SourceList");
            True(!management.ShowToolbar && !management.ShowBulkActions && !management.EnableRowDrag,
                "source management reuses the list without adding book-source tools or video ordering");
            var toolbar = (StackPanel)view.FindName("SourceTitleRow");
            True(ReferenceEquals(toolbar.Style, DesktopVisual.Resource(view, "MeowFilm.ManagementTitleRow")), "shared management title chrome");
            True(toolbar.Children.OfType<Button>().Select(button => button.Content as string)
                .SequenceEqual(["添加本地文件", "添加网络源", "检测所选", "删除所选"]), "source management toolbar order");
            True(toolbar.Children.OfType<Button>().Take(3).All(button =>
                ReferenceEquals(button.Style, DesktopVisual.Resource(view, "Settings.PrimaryButton"))),
                "source import and selected detection actions reuse the shared save button style");
            var deleteSelected = toolbar.Children.OfType<Button>().Last();
            True(ReferenceEquals(deleteSelected.Style, DesktopVisual.Resource(view, "MeowFilm.ManagementDeleteSelected")) &&
                ((SolidColorBrush)deleteSelected.Background).Color.A == 0, "shared transparent outlined batch delete");
            foreach (double width in new[] { 1248, 1100, 750 })
            {
                mount.Window.Resize(width, 430);
                settings.UpdateLayout(); await Drain(); view.UpdateLayout();
                var list = SourceItems(view);
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                await CaptureSettings(DesktopTestUi.WindowRoot(mount.Window), "music-source-management-" + (int)width + ".png");
                Equal(9, list.Items.Count);
                True(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                    scroll.ScrollableWidth == 0 && scroll.ScrollableHeight > 0, "vertical-only scrolling at " + width);
                var row = f.Module.Sources[0];
                var name = Visuals(view).OfType<TextBlock>().Single(text => text.Text == row.Name && ReferenceEquals(text.DataContext, row));
                var header = Visuals(management).OfType<TextBlock>().Single(text => text.Text == "名称");
                True(Math.Abs(name.TransformToVisual(view).TransformPoint(new()).X - header.TransformToVisual(view).TransformPoint(new()).X) < 1,
                    "shared name column aligns at " + width);
                foreach (var button in Visuals(view).OfType<Button>().Where(button => ReferenceEquals(button.DataContext, row) && button is not ManagementStatus))
                {
                    var bounds = new Rect(button.TransformToVisual(view).TransformPoint(new()), new Size(button.ActualWidth, button.ActualHeight));
                    True(bounds.Left >= 0 && bounds.Right <= view.ActualWidth, "reachable source action at " + width);
                    True(button.FontWeight == FontWeights.Normal, "source actions retain regular text");
                }
                foreach (var platform in row.Platforms)
                    True(Visuals(view).OfType<TextBlock>().Any(text => DesktopVisual.IsVisible(text) &&
                        ReferenceEquals(text.DataContext, platform) && text.Text == platform.Name), "platform remains visible at " + width);
            }
            var first = f.Module.Sources[0];
            var check = Visuals(management).OfType<CheckBox>().Single(box =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) == "选择 " + first.Name);
            True(check.IsChecked != true && !first.Selected, "the built-in source starts unselected");
            FilmUiTest.Click(check);
            await Until(() => first.Selected);
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
            var popup = (ContentDialog)view.FindName("NetworkPopup");
            var input = (TextBox)view.FindName("NetworkAddress");
            var save = (Button)view.FindName("NetworkSave");
            var cancel = (Button)view.FindName("NetworkCancel");
            const string address = "https://example.invalid/source.js";
            Click(FindButton(view, "添加网络源")); await Drain();
            True(MusicTestUi.IsDialogOpen(popup) && !Visuals(view).Contains(input), "network address lives only inside the add dialog");
            input.Text = address;
            Click(cancel); await Drain();
            True(!MusicTestUi.IsDialogOpen(popup) && input.Text == address, "dismissal retains the draft without submitting");
            Equal(0, f.Calls.Count("music.sources.download"));
            Click(FindButton(view, "添加网络源")); await Drain();
            Equal(address, input.Text);
            f.Calls.FailNext("music.sources.download", "SOURCE_DOWNLOAD_FAILED");
            Click(save); await Until(() => f.Module.CanEditScripts && f.Module.Status == "音源下载失败"); await Drain();
            True(MusicTestUi.IsDialogOpen(popup) && input.Text == address && save.IsEnabled, "failed download keeps its form and draft for retry");
            await CaptureSettings((FrameworkElement)popup.Content, "music-network-source-popup.png");
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
                True(!MusicTestUi.IsDialogOpen(popup) && input.Text == address, "retirement cancels download and preserves the local draft");
                held.Release(); await Until(() => f.Module.CanEditScripts); await Drain();
                if (!dismissImmediately)
                {
                    ReplicaAnchors(settings)[^1].IsChecked = true;
                    await Until(() => f.Module.CanEditScripts); await Drain();
                }
                Click(FindButton(view, "添加网络源")); await Drain();
                True(MusicTestUi.IsDialogOpen(popup) && input.Text == address, "a stale completion cannot dismiss a later add session");
            }
            mount.Window.Content = null; await Drain();
            True(!MusicTestUi.IsDialogOpen(popup), "unloading closes the add dialog");
        });
    }

    private static ListView SourceItems(MusicSourcesSettingsView view) =>
        Visuals((ManagementList)view.FindName("SourceList")).OfType<ListView>().Single();

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
