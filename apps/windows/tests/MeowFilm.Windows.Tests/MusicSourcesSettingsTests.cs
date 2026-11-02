using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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
        yield return Case("demo source settings reproduce the five reference rows and a separate official current target", async () =>
        {
            var view = new MusicSourcesSettingsView { DataContext = new SourceDataContextTrap() };
            using var mount = new SourceDemoMount(view);
            await Drain();
            var demo = SourceDemoOwner(view);
            Equal(5, demo.Sources.Count);
            True(demo.Sources.Select(row => row.Name).SequenceEqual(new[] { "星河音源", "轻听音源", "漫游音源", "森屿音源", "极简音源" }), "reference names");
            True(demo.Sources.Select(row => row.Version).SequenceEqual(new[] { "1.4.2", "1.2.0", "2.1.0", "1.0.6", "1.0.0" }), "reference versions");
            True(demo.Sources.Select(row => row.PreviousVersion).SequenceEqual(new[] { "1.4.1", "1.1.0", "2.0.4", "1.0.5", "" }), "reference rollback versions");
            True(demo.Sources.Select(row => row.Address).SequenceEqual(new[] { "https://sources.example/stellar.js", "qingt.js", "https://sources.example/roaming.js", "https://sources.example/forest.js", "minimal.js" }), "only reserved example addresses and basenames");
            True(demo.Sources.Select(row => row.Origin).SequenceEqual(new[] { "网络", "本地", "网络", "网络", "本地" }), "reference origin");
            True(demo.Sources.Select(row => row.StateText).SequenceEqual(new[] { "可用", "可用", "待加载", "加载失败", "已停用" }), "reference states");
            True(demo.Sources.Select(row => row.Enabled).SequenceEqual(new[] { true, true, true, false, false }), "reference switches");
            True(demo.Sources.Select(row => row.PlatformsText).SequenceEqual(new[] { "网易云、QQ、酷狗、酷我、咪咕", "网易云、QQ、酷狗", "网易云、QQ、酷狗", "QQ、酷狗", "网易云、QQ" }), "reference platforms");
            Equal("连接超时", demo.Sources[3].Error);
            Equal("5 个自定义音源", demo.CustomCountText); Equal("已选择 1 项", demo.SelectedCountText);
            True(demo.Sources[2].IsSelected && demo.Sources.Count(row => row.IsSelected) == 1, "one owner selects roaming initially");
            Equal<bool?>(null, demo.AllVisibleSelected);
            True(demo.UsesOfficialSources && demo.CurrentDemoSource is null, "selection is not current use");
            Equal(18d, ((TextBlock)view.FindName("SourceTitle")).FontSize);
            Equal(64d, ((Border)view.FindName("OfficialRow")).ActualHeight);
            Equal(36d, ((Grid)view.FindName("SourceHeader")).ActualHeight);
            True(Visuals(view).OfType<TextBlock>().Any(text => text.Text == "演示数据"), "demo state remains explicit");
            True(!Visuals(view).OfType<TabControl>().Any(), "the parent owns the common settings navigation");
            AssertSourceDemoGeometry(view, demo.Sources[2], 76);
            SourceDemoCapture(view, "source-demo-management.png");
        });
        yield return Case("demo source checkbox selection is single-owner and bulk actions disable single-row ordering", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view);
            var first = SourceDemoCheckbox(view, demo.Sources[0], "选择演示音源星河音源");
            SourceDemoToggle(first, true); await Drain();
            Equal(2, demo.SelectedCount); True(!demo.CanMoveUp && !demo.CanMoveDown, "multi-selection cannot move a single row");
            True(!((Button)view.FindName("MoveUp")).IsEnabled && !((Button)view.FindName("MoveDown")).IsEnabled, "toolbar shares owner guards");
            SourceDemoToggle((CheckBox)view.FindName("SelectAll"), true); await Drain();
            Equal(5, demo.SelectedCount); Equal<bool?>(true, demo.AllVisibleSelected);
            Click(FindButton(view, "停用选中演示音源")); await Drain();
            True(demo.Sources.All(row => !row.Enabled && row.State == MusicSourceDemoState.Disabled), "bulk disable changes only demo rows");
            Click(FindButton(view, "启用选中演示音源")); await Drain();
            True(demo.Sources.All(row => row.Enabled && row.State == MusicSourceDemoState.Idle), "bulk enable is not a script load");
            Click(FindButton(view, "检测选中演示音源")); await Drain();
            Equal(MusicSourceDemoState.Error, demo.Sources[3].State); Equal("连接超时", demo.Sources[3].Error);
            True(demo.Sources.Where(row => row.Id != "forest").All(row => row.State == MusicSourceDemoState.Ready), "deterministic demo checks");
            True(demo.Status.StartsWith("演示数据 · 检测完成", StringComparison.Ordinal), "result belongs to NoticeOutlet");
            Equal(demo.Status, Visuals(view).OfType<NoticeOutlet>().Single().Message);
            SourceDemoToggle((CheckBox)view.FindName("SelectAll"), false); await Drain();
            Equal(0, demo.SelectedCount); Equal<bool?>(false, demo.AllVisibleSelected);
            True(!demo.HasSelection && !demo.CanMoveUp && !demo.CanMoveDown, "zero selection retires batch actions");
        });
        yield return Case("demo source ordering honors first last and filtered boundaries without a playback policy", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var first = demo.Sources[0]; var last = demo.Sources[4];
            demo.SelectOnly(first); await Drain();
            True(!demo.CanMoveUp && demo.CanMoveDown && !demo.MoveSelected(-1), "first boundary");
            Click((Button)view.FindName("MoveDown")); await Drain();
            True(ReferenceEquals(first, demo.Sources[1]) && first.IsSelected, "stable identity after down");
            Click((Button)view.FindName("MoveUp")); await Drain(); True(ReferenceEquals(first, demo.Sources[0]), "stable identity after up");
            demo.SelectOnly(last); await Drain(); True(demo.CanMoveUp && !demo.CanMoveDown && !demo.MoveSelected(1), "last boundary");
            var current = demo.CurrentDemoSource;
            demo.MoveSelected(-1); await Drain(); True(ReferenceEquals(last, demo.Sources[3]), "last can move up");
            True(ReferenceEquals(current, demo.CurrentDemoSource) && demo.UsesOfficialSources, "sorting does not choose a player source");
            ((ComboBox)view.FindName("PlatformFilter")).SelectedIndex = 4; await Drain();
            Equal(1, demo.VisibleRows().Length); Equal(0, demo.SelectedCount);
            demo.SelectOnly(first); await Drain(); True(!demo.CanMoveUp && !demo.CanMoveDown, "one filtered row has both boundaries");
            ((ComboBox)view.FindName("PlatformFilter")).SelectedIndex = 0; await Drain();
            ((ComboBox)view.FindName("StateFilter")).SelectedIndex = 4; await Drain();
            Equal(1, demo.VisibleRows().Length); Equal(last, demo.VisibleRows()[0]); Equal(0, demo.SelectedCount);
            ((ComboBox)view.FindName("PlatformFilter")).SelectedIndex = 5; await Drain();
            Equal(0, demo.VisibleRows().Length); True(!demo.HasVisibleSources && !demo.CanMoveUp && !demo.CanMoveDown, "empty filter keeps fixed chrome");
            Equal(Visibility.Visible, ((Border)view.FindName("OfficialRow")).Visibility); Equal(5, demo.Sources.Count);
        });
        yield return Case("demo source menu load update rollback and current use have no production dependency", async () =>
        {
            var view = new MusicSourcesSettingsView { DataContext = new SourceDataContextTrap() };
            using var mount = new SourceDemoMount(view); await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2];
            await SourceDemoOpenMenu(view, row);
            SourceDemoCapture(view, "source-demo-menu.png");
            Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "加载音源")); await Drain();
            Equal(MusicSourceDemoState.Ready, row.State);
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "更新脚本")); await Drain();
            Equal("2.1.1", row.Version); Equal("2.1.0", row.PreviousVersion);
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "回退到 2.1.0")); await Drain();
            Equal("2.1.0", row.Version); Equal("2.1.1", row.PreviousVersion);
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "切换音源")); await Drain();
            True(ReferenceEquals(row, demo.CurrentDemoSource) && !demo.UsesOfficialSources, "current demo target is distinct from selection");
            Equal(Visibility.Collapsed, ((Border)view.FindName("OfficialCurrent")).Visibility);
            Click(FindButton(view, "演示切换至官方音源")); await Drain(); True(demo.UsesOfficialSources && row.IsSelected, "official does not clear inspection");
            var failure = demo.Sources[3]; await SourceDemoOpenMenu(view, failure);
            True(!FindButton((FrameworkElement)view.FindName("MenuLayer"), "切换音源").IsEnabled, "failed demo cannot become current");
            Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "加载音源")); await Drain();
            Equal("连接超时", failure.Error); True(demo.Status.Contains("加载失败", StringComparison.Ordinal), "no remote load");
        });
        yield return Case("demo source menus confirmations and detached row actions retire on escape filters resize and unload", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2];
            await SourceDemoOpenMenu(view, row); var retiredUpdate = FindButton((FrameworkElement)view.FindName("MenuLayer"), "更新脚本");
            True(RaiseKey(view, Key.Escape).Handled, "escape consumes local menu cancellation");
            retiredUpdate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal("2.1.0", row.Version);
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "删除音源")); await Drain();
            var retiredDelete = FindButton((FrameworkElement)view.FindName("ModalLayer"), "删除");
            Click(FindButton((FrameworkElement)view.FindName("ModalLayer"), "取消")); retiredDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(5, demo.Sources.Count);
            await SourceDemoOpenMenu(view, row); var filteredUpdate = FindButton((FrameworkElement)view.FindName("MenuLayer"), "更新脚本");
            ((ComboBox)view.FindName("PlatformFilter")).SelectedIndex = 4; await Drain();
            Equal(Visibility.Collapsed, ((Grid)view.FindName("MenuLayer")).Visibility);
            filteredUpdate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal("2.1.0", row.Version);
            ((ComboBox)view.FindName("PlatformFilter")).SelectedIndex = 0; await Drain();
            await SourceDemoOpenMenu(view, row); var resizedUpdate = FindButton((FrameworkElement)view.FindName("MenuLayer"), "更新脚本");
            mount.Window.Width = 900; mount.Window.UpdateLayout(); await Drain();
            resizedUpdate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal("2.1.0", row.Version);
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "删除音源")); await Drain();
            var hiddenDelete = FindButton((FrameworkElement)view.FindName("ModalLayer"), "删除");
            view.Visibility = Visibility.Collapsed; await Drain(); hiddenDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(5, demo.Sources.Count); True(!demo.IsActive, "hidden view closes admission");
            view.Visibility = Visibility.Visible; await Drain();
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "删除音源")); await Drain();
            var unloadedDelete = FindButton((FrameworkElement)view.FindName("ModalLayer"), "删除");
            mount.Window.Content = null; await Drain(); unloadedDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(5, demo.Sources.Count); True(!demo.IsActive, "unloaded view closes admission");
        });
        yield return Case("demo source edit cancel save and data replacement keep drafts separate and example-only", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2];
            await SourceDemoOpenEdit(view, row);
            var layer = (FrameworkElement)view.FindName("ModalLayer");
            SourceDemoCapture((FrameworkElement)view.FindName("ModalSurface"), "source-demo-edit.png");
            True(new[] { "当前版本", "上一版本", "导入方式", "支持平台", "2.1.0", "2.0.4", "网络地址" }
                .All(label => Visuals(layer).OfType<TextBlock>().Any(text => text.Text == label)), "complete reference fields");
            SourceDemoTextBox(layer, "演示音源名称").Text = "漫游演示编辑";
            SourceDemoTextBox(layer, "演示音源地址").Text = "https://sources.example/edited.js";
            var cancelledSave = FindButton(layer, "保存"); Click(FindButton(layer, "取消"));
            cancelledSave.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal("漫游音源", row.Name);
            Equal("https://sources.example/roaming.js", row.Address);
            await SourceDemoOpenEdit(view, row);
            SourceDemoTextBox(layer, "演示音源名称").Text = "漫游演示编辑";
            SourceDemoTextBox(layer, "演示音源地址").Text = "https://sources.example/edited.js";
            SourceDemoToggle(Visuals(layer).OfType<CheckBox>().Single(), false);
            Click(FindButton(layer, "保存")); await Drain();
            Equal("漫游演示编辑", row.Name); Equal("https://sources.example/edited.js", row.Address); True(!row.Enabled, "save belongs to demo row");
            Equal(Visibility.Collapsed, layer.Visibility);
            await SourceDemoOpenEdit(view, row); SourceDemoTextBox(layer, "演示音源地址").Text = "https://accounts.example.net/secret.js";
            Click(FindButton(layer, "保存")); await Drain(); Equal("https://sources.example/edited.js", row.Address);
            True(layer.Visibility == Visibility.Visible && demo.Status.StartsWith("演示数据", StringComparison.Ordinal), "invalid address leaves a cancellable draft");
            var retiredSave = FindButton(layer, "保存"); view.DataContext = new SourceDataContextTrap(); await Drain();
            retiredSave.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal("https://sources.example/edited.js", row.Address);
            Equal(Visibility.Collapsed, layer.Visibility);
            await SourceDemoOpenEdit(view, demo.Sources[1]);
            SourceDemoTextBox(layer, "演示音源地址").Text = "renamed-demo.js"; Click(FindButton(layer, "保存")); await Drain();
            Equal("renamed-demo.js", demo.Sources[1].Address); Equal("本地", demo.Sources[1].Origin);
        });
        yield return Case("demo source network and file import previews use only fixed memory results and append disabled by default", async () =>
        {
            var view = new MusicSourcesSettingsView { DataContext = new SourceDataContextTrap() }; using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var layer = (FrameworkElement)view.FindName("ModalLayer");
            Click(FindButton(view, "导入演示音源")); await Drain();
            Equal("https://sources.example/ambient.js", SourceDemoTextBox(layer, "演示网络导入地址").Text);
            True(Visuals(layer).OfType<TextBlock>().Any(text => text.Text == "拾光音源"), "fixed network result is already a demo preview");
            SourceDemoCapture((FrameworkElement)view.FindName("ModalSurface"), "source-demo-network.png");
            Equal<bool?>(false, Visuals(layer).OfType<CheckBox>().Single().IsChecked);
            var cancelledImport = FindButton(layer, "导入"); Click(FindButton(layer, "取消")); cancelledImport.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal(5, demo.Sources.Count);
            Click(FindButton(view, "导入演示音源")); await Drain();
            SourceDemoTextBox(layer, "演示网络导入地址").Text = "https://sources.example/another.js";
            True(!FindButton(layer, "导入").IsEnabled, "changed address retires the preview");
            Click(FindButton(layer, "读取演示网络音源")); await Drain();
            Equal("https://sources.example/ambient.js", SourceDemoTextBox(layer, "演示网络导入地址").Text);
            Click(FindButton(layer, "导入")); await Drain();
            Equal(6, demo.Sources.Count); var network = demo.Sources[5];
            Equal("拾光音源", network.Name); Equal("1.0.2", network.Version); Equal("https://sources.example/ambient.js", network.Address);
            True(!network.Enabled && network.State == MusicSourceDemoState.Disabled, "network default is disabled");
            Click(FindButton(view, "导入演示音源")); await Drain(); Click(FindButton(layer, "演示本地文件导入")); await Drain();
            True(new[] { "listenwind.js", "moonlight.js", "听风音源 · 1.0.0 · 184 KB", "皓月音源 · 1.1.3 · 76 KB", "2 个音源" }
                .All(label => Visuals(layer).OfType<TextBlock>().Any(text => text.Text == label)), "fixed two-file preview");
            SourceDemoCapture((FrameworkElement)view.FindName("ModalSurface"), "source-demo-file.png");
            var retiredFileImport = FindButton(layer, "导入");
            Click(FindButton(layer, "移除演示文件listenwind.js")); await Drain();
            retiredFileImport.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal(6, demo.Sources.Count);
            True(Visuals(layer).OfType<TextBlock>().Any(text => text.Text == "1 个音源"), "removal is draft-only");
            Click(FindButton(layer, "选择演示文件")); await Drain();
            Equal(2, Visuals(layer).OfType<TextBlock>().Count(text => text.Text.EndsWith(".js", StringComparison.Ordinal)));
            Click(FindButton(layer, "导入")); await Drain();
            Equal(8, demo.Sources.Count); True(demo.Sources.Skip(6).Select(row => row.Name).SequenceEqual(new[] { "听风音源", "皓月音源" }), "fixed local append order");
            True(demo.Sources.Skip(6).All(row => !row.Enabled && row.State == MusicSourceDemoState.Disabled && row.PreviousVersion.Length == 0), "files default disabled without execution");
            Equal(8, demo.Sources.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
        });
        yield return Case("demo source export is read-only selectable memory text and a fresh view owns a fresh five-row demo", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var first = demo.Sources[0]; demo.SelectOnly(first); demo.MoveSelected(1); await Drain();
            Click(FindButton(view, "演示导出音源")); await Drain(); var layer = (FrameworkElement)view.FindName("ModalLayer");
            var export = SourceDemoTextBox(layer, "演示导出文本"); True(export.IsReadOnly, "export is a copyable read-only text box");
            using var json = JsonDocument.Parse(export.Text);
            Equal("meowfilm.music-sources.demo", json.RootElement.GetProperty("kind").GetString());
            Equal(false, json.RootElement.GetProperty("production").GetBoolean());
            Equal("qingt", json.RootElement.GetProperty("sources")[0].GetProperty("id").GetString());
            Click(FindButton(layer, "全选")); Equal(export.Text.Length, export.SelectionLength);
            Equal(5, demo.Sources.Count); Click(FindButton(layer, "关闭"));
            var second = new MusicSourcesSettingsView(); Render(second, 1110, 760);
            var fresh = SourceDemoOwner(second); Equal(5, fresh.Sources.Count); Equal("stellar", fresh.Sources[0].Id);
            True(!ReferenceEquals(demo, fresh) && !ReferenceEquals(demo.Sources[0], fresh.Sources[0]), "no shared or persisted demo state");
        });
        yield return Case("demo source drag slides passed rows without a marker and fences cancel filters resize and stale completion", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2];
            var list = (ItemsControl)view.FindName("SourceList"); var initial = demo.Sources.ToArray();
            var grip = SourceDemoGrip(view, row);
            view.BeginDemoDrag(grip, new Point(60, 190)); view.UpdateDemoDrag(new Point(60, 65)); view.UpdateLayout();
            True(ReferenceEquals(row, demo.DraggingSource), "drag has one stable source identity");
            True(!demo.CanMoveUp && !demo.CanMoveDown && !demo.HasSelection, "toolbar is fenced during a gesture");
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(list)!;
            True(layer.GetAdorners(list)?.Length == 1, "one clipped drag preview");
            var preview = layer.GetAdorners(list)!.Single();
            True(!preview.IsHitTestVisible && preview.ClipToBounds && VisualTreeHelper.GetChildrenCount(preview) == 1 &&
                VisualTreeHelper.GetChild(preview, 0) is Border, "drag preview is only the lifted row without an insertion marker");
            var passed = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
            var shift = ((TransformGroup)passed.RenderTransform).Children.OfType<TranslateTransform>().Single();
            Equal(76d, (double)shift.GetAnimationBaseValue(TranslateTransform.YProperty));
            True(((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)).RenderTransform is not TransformGroup &&
                demo.Sources.SequenceEqual(initial), "the passed neighbour slides while the order waits for release");
            SourceDemoCapture(view, "source-demo-sorting.png");
            view.CompleteDemoDrag(new Point(60, 65), true); view.UpdateLayout();
            True(demo.Sources.SequenceEqual(initial) && demo.DraggingSource is null, "cancel leaves the order untouched");
            True(layer.GetAdorners(list) is null or { Length: 0 }, "cancel removes the adorner");
            True(passed.RenderTransform is not TransformGroup, "cancel restores neighbour geometry");
            view.BeginDemoDrag(SourceDemoGrip(view, row), new Point(60, 190)); view.UpdateDemoDrag(new Point(60, 65));
            ((ComboBox)view.FindName("StateFilter")).SelectedIndex = 2; await Drain();
            view.CompleteDemoDrag(new Point(60, 0), false); True(demo.Sources.SequenceEqual(initial), "filter retires an old drag completion");
            ((ComboBox)view.FindName("StateFilter")).SelectedIndex = 0; await Drain();
            view.BeginDemoDrag(SourceDemoGrip(view, row), new Point(60, 190)); view.UpdateDemoDrag(new Point(60, 65));
            mount.Window.Width = 920; mount.Window.UpdateLayout(); await Drain();
            view.CompleteDemoDrag(new Point(60, 0), false); True(demo.Sources.SequenceEqual(initial), "resize fences geometry and old completion");
            await SourceDemoScrollTo(view, row);
            view.BeginDemoDrag(SourceDemoGrip(view, row), new Point(60, 210)); view.UpdateDemoDrag(new Point(60, 10));
            SourceDemoGrip(view, row).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            view.CompleteDemoDrag(new Point(60, 10), false); await Drain();
            True(ReferenceEquals(row, demo.Sources[0]) && row.IsSelected, "commit moves the captured demo row, not a recycled item");
            True(demo.UsesOfficialSources, "drag does not alter a playback target");
            view.BeginDemoDrag(SourceDemoGrip(view, row), new Point(60, 25)); view.UpdateDemoDrag(new Point(60, 140));
            var order = demo.Sources.ToArray(); mount.Window.Content = null; await Drain();
            view.CompleteDemoDrag(new Point(60, 240), false); True(demo.Sources.SequenceEqual(order), "unload fences the gesture");
        });
        yield return Case("demo source imports remain bounded virtualized and reject all retired handles without IO", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view, 860, 600);
            await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2]; var retired = demo.Capture(row)!.Value;
            demo.Filter("QQ", "");
            True(!demo.Update(retired) && !demo.Save(retired, new MusicSourceDemoEdit(row) { Name = "旧句柄" }) && !demo.Remove(retired), "filter invalidates every mutation handle");
            var importTicket = demo.Epoch; demo.Fence(); True(!demo.Import(importTicket, [MusicSourceDemo.NetworkImport()], false), "retired import session cannot append");
            demo.Filter("", "");
            while (demo.Sources.Count < MusicSourceDemo.MaximumSources)
                True(demo.Import(demo.Epoch, [MusicSourceDemo.NetworkImport()], false), "bounded demo append");
            Equal(MusicSourceDemo.MaximumSources, demo.Sources.Count);
            True(!demo.CanImport && !demo.Import(demo.Epoch, MusicSourceDemo.LocalImports(), false), "limit refuses the complete batch without partial append");
            await Drain(); view.UpdateLayout();
            var list = (ItemsControl)view.FindName("SourceList"); var scroll = SourceDemoScroll(view);
            True(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling && scroll.CanContentScroll, "bounded recycling viewport");
            int realized = Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
            True(realized > 0 && realized < 40, "multi-source growth does not materialize the complete table");
            True(scroll.ScrollableHeight > 0 && list.ActualHeight < view.ActualHeight - 100, "source body occupies only remaining viewport");
            var removed = demo.Sources[0]; var remove = demo.Capture(removed)!.Value; True(demo.Remove(remove), "local deletion works");
            True(!demo.Load(remove) && !demo.Use(remove) && !demo.Rollback(remove), "deleted row never becomes an action target");
            True(!MusicSourceDemo.ValidAddress("https://real.invalid/demo.js", "网络") && !MusicSourceDemo.ValidAddress("C:\\real\\source.js", "本地"), "demo cannot accept production addresses or paths");
        });
        yield return Case("demo source all reference modals stay inside a narrow available settings viewport", async () =>
        {
            var view = new MusicSourcesSettingsView(); using var mount = new SourceDemoMount(view, 620, 430);
            await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2];
            AssertSourceDemoGeometry(view, demo.Sources[0], 84);
            await SourceDemoOpenEdit(view, row); AssertSourceDemoOverlay(view); SourceDemoCapture(view, "source-demo-edit-narrow.png");
            Click(FindButton((FrameworkElement)view.FindName("ModalLayer"), "取消"));
            Click(FindButton(view, "导入演示音源")); await Drain(); AssertSourceDemoOverlay(view); SourceDemoCapture(view, "source-demo-network-narrow.png");
            Click(FindButton((FrameworkElement)view.FindName("ModalLayer"), "演示本地文件导入")); await Drain();
            AssertSourceDemoOverlay(view); SourceDemoCapture(view, "source-demo-file-narrow.png");
            Click(FindButton((FrameworkElement)view.FindName("ModalLayer"), "取消"));
            Click(FindButton(view, "演示导出音源")); await Drain(); AssertSourceDemoOverlay(view); SourceDemoCapture(view, "source-demo-export-narrow.png");
            Click(FindButton((FrameworkElement)view.FindName("ModalLayer"), "关闭"));
            await SourceDemoOpenMenu(view, row); AssertSourceDemoOverlay(view);
            SourceDemoCapture(view, "source-demo-menu-narrow.png");
        });
        yield return Case("demo source page fits the real 860 by 600 shell and switching away retires its independent UI", async () =>
        {
            await using var f = await Fixture.Create();
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask)
            {
                Width = 860, Height = 600, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                shell.Show(); await Drain(); await Until(() => f.Module.CanEdit);
                Click((Button)shell.FindName("SettingsButton")); await Drain(); await Until(() => f.Module.CanEdit);
                var settings = (MusicSettingsView)f.Module.SettingsView;
                var navigation = (Panel)settings.FindName("SettingsNavigation"); var anchors = navigation.Children.OfType<RadioButton>().ToArray();
                int reads = f.Calls.Count("music.sources.list"); anchors.Last().IsChecked = true; await Drain(); shell.UpdateLayout();
                var view = (MusicSourcesSettingsView)settings.FindName("SourcesPage"); var demo = SourceDemoOwner(view);
                True(view.IsVisible && ((ScrollViewer)settings.FindName("SettingsScroller")).Visibility == Visibility.Collapsed, "independent finite source content");
                var sidebar = (ScrollViewer)shell.FindName("SidebarScroll"); var footer = (Border)shell.FindName("PlayerFrame");
                var content = (Grid)shell.FindName("ContentDeck");
                Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), (FrameworkElement)shell.Content), element.RenderSize);
                True(view.ActualWidth is >= 560 and < 700, "test uses the real sidebar-reduced 600-ish viewport, not a large standalone window");
                True(Bounds(view).Left >= Bounds(sidebar).Right - 1 && Bounds(view).Bottom <= Bounds(footer).Top + .5, "source never overlays sidebar or footer");
                True(Bounds(content).Bottom <= Bounds(footer).Top + .5, "shell content is above the player row");
                Equal(Visibility.Visible, sidebar.Visibility); Equal(203d, ((Grid)shell.FindName("RootLayout")).ColumnDefinitions[0].ActualWidth);
                True(SourceDemoScroll(view).ViewportHeight >= 82 && ((Grid)view.FindName("SourceHeader")).ActualHeight == 32,
                    "compact fixed header leaves one visible row and an independently scrolling body");
                Click(FindButton(view, "导入演示音源")); await Drain(); AssertSourceDemoOverlay(view);
                SourceDemoCapture((FrameworkElement)shell.Content, "source-demo-shell-860x600.png");
                var retired = FindButton((FrameworkElement)view.FindName("ModalLayer"), "导入"); anchors[1].IsChecked = true; await Drain();
                retired.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Equal(5, demo.Sources.Count);
                True(!view.IsVisible && ((ScrollViewer)settings.FindName("SettingsScroller")).IsVisible, "normal settings regain the same host");
                Equal(reads, f.Calls.Count("music.sources.list")); Equal(0, f.Runtime.Calls.Count);
                True(!f.Runtime.HasStarted && f.Playback.Opens.Count == 0, "no script or player activation for demo shell actions");
            }
            finally
            {
                shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            }
        });
        yield return Case("demo source mounting every edit import export and menu action leaves native settings untouched", async () =>
        {
            await using var f = await Fixture.Create(); await Drain();
            int calls = f.Calls.Log.Count; var nativeSources = f.Module.Sources.ToArray(); var nativeSelection = f.Module.SelectedSource;
            string target = f.Module.PlaybackTarget; bool official = f.Module.UsesOfficialSources;
            var view = new MusicSourcesSettingsView { DataContext = f.Module }; using var mount = new SourceDemoMount(view);
            await Drain(); var demo = SourceDemoOwner(view); var row = demo.Sources[2];
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "切换音源")); await Drain();
            await SourceDemoOpenEdit(view, row); var layer = (FrameworkElement)view.FindName("ModalLayer");
            SourceDemoTextBox(layer, "演示音源名称").Text = "漫游演示"; Click(FindButton(layer, "保存")); await Drain();
            Click(FindButton(view, "导入演示音源")); await Drain(); Click(FindButton(layer, "导入")); await Drain();
            Click(FindButton(view, "导入演示音源")); await Drain(); Click(FindButton(layer, "演示本地文件导入")); await Drain(); Click(FindButton(layer, "导入")); await Drain();
            demo.SelectOnly(row); Click(FindButton(view, "检测选中演示音源")); await Drain();
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "更新脚本")); await Drain();
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "回退到 2.1.0")); await Drain();
            Click(FindButton(view, "演示导出音源")); await Drain(); Click(FindButton(layer, "关闭"));
            await SourceDemoOpenMenu(view, row); Click(FindButton((FrameworkElement)view.FindName("MenuLayer"), "删除音源")); await Drain(); Click(FindButton(layer, "删除")); await Drain();
            Equal(calls, f.Calls.Log.Count); True(f.Module.Sources.SequenceEqual(nativeSources) && ReferenceEquals(nativeSelection, f.Module.SelectedSource), "no native source reads or writes");
            Equal(target, f.Module.PlaybackTarget); Equal(official, f.Module.UsesOfficialSources); Equal(0, f.Runtime.Calls.Count);
            True(!f.Runtime.HasStarted && f.Playback.Opens.Count == 0, "source UI does not execute scripts or open media");
        });
    }

    private sealed class SourceDataContextTrap
    {
        public object Sources => throw new InvalidOperationException("Production Sources must not be read");
        public object SelectedSource => throw new InvalidOperationException("Production SelectedSource must not be read");
        public object PlaybackTarget => throw new InvalidOperationException("Production PlaybackTarget must not be read");
        public object CanEdit => throw new InvalidOperationException("Demo admission must not depend on business");
    }
    private sealed class SourceDemoMount : IDisposable
    {
        internal Window Window { get; }
        internal SourceDemoMount(MusicSourcesSettingsView view, int width = 1110, int height = 760)
        {
            Window = new() { Content = view, Width = width, Height = height, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            Window.Show(); Window.UpdateLayout();
        }
        public void Dispose() { Window.Content = null; Window.Close(); }
    }
    private static MusicSourceDemo SourceDemoOwner(MusicSourcesSettingsView view) =>
        (MusicSourceDemo)((Grid)view.FindName("SourceLayout")).DataContext;
    private static ScrollViewer SourceDemoScroll(MusicSourcesSettingsView view) =>
        (ScrollViewer)((ItemsControl)view.FindName("SourceList")).Template.FindName("ItemsScroll", (ItemsControl)view.FindName("SourceList"));
    private static FrameworkElement SourceDemoContainer(MusicSourcesSettingsView view, MusicSourceDemoRow row) =>
        (FrameworkElement)((ItemsControl)view.FindName("SourceList")).ItemContainerGenerator.ContainerFromItem(row);
    private static Thumb SourceDemoGrip(MusicSourcesSettingsView view, MusicSourceDemoRow row) =>
        Visuals(SourceDemoContainer(view, row)).OfType<Thumb>().Single();
    private static CheckBox SourceDemoCheckbox(MusicSourcesSettingsView view, MusicSourceDemoRow row, string name) =>
        Visuals(SourceDemoContainer(view, row)).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == name);
    private static TextBox SourceDemoTextBox(FrameworkElement layer, string name) =>
        Visuals(layer).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == name);
    private static void SourceDemoToggle(CheckBox box, bool value)
    {
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, value); box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static async Task SourceDemoScrollTo(MusicSourcesSettingsView view, MusicSourceDemoRow row)
    {
        int index = Array.IndexOf(SourceDemoOwner(view).VisibleRows(), row);
        SourceDemoScroll(view).ScrollToVerticalOffset(Math.Max(0, index) * (view.IsNarrow ? 84 : 76));
        view.UpdateLayout(); await Drain(); view.UpdateLayout();
    }
    private static async Task SourceDemoOpenMenu(MusicSourcesSettingsView view, MusicSourceDemoRow row)
    {
        await SourceDemoScrollTo(view, row);
        Click(FindButton(SourceDemoContainer(view, row), "演示音源" + row.Name + "更多操作")); await Drain(); view.UpdateLayout();
        Equal(Visibility.Visible, ((Grid)view.FindName("MenuLayer")).Visibility);
    }
    private static async Task SourceDemoOpenEdit(MusicSourcesSettingsView view, MusicSourceDemoRow row)
    {
        await SourceDemoScrollTo(view, row);
        Click(FindButton(SourceDemoContainer(view, row), "编辑演示音源" + row.Name)); await Drain(); view.UpdateLayout();
        Equal(Visibility.Visible, ((Grid)view.FindName("ModalLayer")).Visibility);
    }
    private static void AssertSourceDemoGeometry(MusicSourcesSettingsView view, MusicSourceDemoRow row, int expectedHeight)
    {
        var container = SourceDemoContainer(view, row);
        var surface = Visuals(container).OfType<Border>().Single(border => border.Name == "SourceRow");
        Equal((double)expectedHeight, surface.ActualHeight);
        var checkbox = SourceDemoCheckbox(view, row, "选择演示音源" + row.Name);
        Equal(24d, checkbox.ActualWidth); Equal(32d, checkbox.ActualHeight);
        Equal(BindingMode.OneWay, BindingOperations.GetBinding(checkbox, ToggleButton.IsCheckedProperty)!.Mode);
        var visualBox = (Border)checkbox.Template.FindName("Box", checkbox); Equal(16d, visualBox.ActualWidth); Equal(16d, visualBox.ActualHeight);
        var grip = SourceDemoGrip(view, row); Equal(36d, grip.ActualWidth); Equal(36d, grip.ActualHeight);
        var enabled = SourceDemoCheckbox(view, row, "演示音源" + row.Name + "启用状态");
        Equal(32d, enabled.ActualWidth); Equal(18d, enabled.ActualHeight);
        Equal(BindingMode.OneWay, BindingOperations.GetBinding(enabled, ToggleButton.IsCheckedProperty)!.Mode);
        foreach (var action in Visuals(container).OfType<Button>())
        {
            Equal(32d, action.ActualHeight);
            var bounds = new Rect(action.TranslatePoint(new Point(), container), action.RenderSize);
            True(bounds.Left >= 0 && bounds.Right <= container.ActualWidth + .5, "row actions remain inside available content");
        }
        True(Visuals(container).OfType<TextBlock>().Any(text => text.Text == row.Name && text.FontSize == 14), "14 DIP source identity");
        True(!Visuals(container).OfType<TextBlock>().Any(text => text.Text.Length == 2 && text.Text.All(char.IsAsciiDigit)) &&
            !Visuals((FrameworkElement)view.FindName("SourceHeader")).OfType<TextBlock>().Any(text => text.Text == "顺序"), "source rows carry no serial number");
        True(Visuals(container).OfType<TextBlock>().Any(text => text.Text == row.Metadata && text.FontSize == 12), "12 DIP source subline");
    }
    private static void AssertSourceDemoOverlay(MusicSourcesSettingsView view)
    {
        var overlay = (FrameworkElement)view.FindName(((Grid)view.FindName("ModalLayer")).Visibility == Visibility.Visible ? "ModalSurface" : "SourceMenuSurface");
        var bounds = new Rect(overlay.TranslatePoint(new Point(), view), overlay.RenderSize);
        True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= view.ActualWidth + .5 && bounds.Bottom <= view.ActualHeight + .5, "modal/menu stays inside settings host");
        if (((Grid)view.FindName("ModalLayer")).Visibility != Visibility.Visible) return;
        foreach (var button in ((Panel)view.FindName("ModalFooter")).Children.OfType<Button>())
        {
            var action = new Rect(button.TranslatePoint(new Point(), view), button.RenderSize);
            True(action.Top >= 0 && action.Bottom <= view.ActualHeight + .5 && button.ActualHeight == 36, "modal actions are reachable without leaving the content viewport");
        }
    }
    private static void SourceDemoCapture(FrameworkElement view, string name) =>
        CaptureSettings(view, name);
}
