using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MeowFilm.Capabilities;

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
            True(f.Module.SourceExclusions.Single().SongId == songs[^1]["songId"]!.GetValue<string>(),
                "a song beyond the first two pages is selectable by its real identity");
            True(!f.Module.HasCurrent, "manual exclusion never begins playback");
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
            True(f.Module.Sources.All(row => row.IsBuiltIn) && f.Calls.Count("music.sources.update") == 1 &&
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
            var list = (ItemsControl)view.FindName("SourceList");
            Equal(9, list.Items.Count);
            var scroll = (ScrollViewer)list.Template.FindName("ItemsScroll", list);
            True(scroll.ViewportHeight >= 64 && scroll.ScrollableHeight > 0, "a visible row and independent scroll survive a short host");
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
    }

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
