using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicSettingsLayoutTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SettingsReplicaCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SettingsReplicaCases()
    {
        yield return Case("replica settings keep six reference anchors and continuous groups with an independent source page", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            Render(view, 1200, 760); await Drain();
            var anchors = ReplicaAnchors(view);
            True(anchors.Select(anchor => (string)anchor.Content).SequenceEqual(new[] { "账号与平台", "搜索", "音质与缓存", "外观", "歌词与封面", "音源" }), "latest v2 section names");
            int calls = f.Calls.Log.Count;
            var scroll = (ScrollViewer)view.FindName("SettingsScroller");
            for (int index = 0; index < anchors.Length - 1; index++)
            {
                anchors[index].IsChecked = true; Render(view, 1200, 760); await Drain(); view.UpdateLayout();
                var section = (Border)view.FindName((string)anchors[index].Tag);
                True(Math.Abs(section.TranslatePoint(new Point(), scroll).Y) <= 1, "selected section starts at the viewport top");
                Equal(index, Array.IndexOf(anchors, anchors.Single(anchor => anchor.IsChecked == true)));
            }
            anchors[^1].IsChecked = true; Render(view, 1200, 760); await Drain();
            Equal(Visibility.Collapsed, scroll.Visibility); Equal(Visibility.Visible, ((MusicSourcesSettingsView)view.FindName("SourcesPage")).Visibility);
            anchors[0].IsChecked = true; Render(view, 1200, 760); await Drain();
            Equal(Visibility.Visible, scroll.Visibility); Equal(Visibility.Collapsed, ((MusicSourcesSettingsView)view.FindName("SourcesPage")).Visibility);
            Equal(calls, f.Calls.Log.Count); True(!f.Runtime.HasStarted, "reference navigation never starts script execution");
        });
        yield return Case("replica priority lists preserve 52 pixel rows unnumbered 36 pixel grips and paired panels", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view);
            foreach (int width in new[] { 1248, 908, 600 })
            {
                mount.Window.Width = width; mount.Window.Height = 760;
                Render(view, width, 760); await Drain(); view.UpdateLayout();
                var priority = (SettingsPriorityList)view.FindName("QualityPriorities");
                var list = (ListBox)priority.FindName("PriorityItems");
                Equal(7, list.Items.Count);
                for (int index = 0; index < list.Items.Count; index++)
                {
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                    Equal(52d, row.ActualHeight);
                    var grip = Visuals(row).OfType<Thumb>().Single(); Equal(36d, grip.ActualWidth); Equal(36d, grip.ActualHeight);
                    var label = Visuals(row).OfType<TextBlock>().Single(text => text.Text == ((MusicQualityRow)list.Items[index]).Label);
                    Equal(14d, label.FontSize); True(grip.FocusVisualStyle is not null && grip.Focusable, "dedicated grip has keyboard focus");
                    True(!Visuals(row).OfType<TextBlock>().Any(text => text.Text == (index + 1).ToString("00") || text.Text == (index + 1).ToString()),
                        "priority rows carry no serial number");
                    var toggle = Visuals(row).OfType<CheckBox>().Single(); Equal(32d, toggle.ActualWidth); Equal(18d, toggle.ActualHeight);
                }
                list.SelectedIndex = 2; view.UpdateLayout();
                var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
                Equal(((SolidColorBrush)view.FindResource("Settings.Brush.Selected")).Color, ((SolidColorBrush)selected.Background).Color);
                True(priority.CanMoveUp && priority.CanMoveDown, "selected row owns both toolbar moves");
                var snapshot = SettingsReorderDrag.Snapshot(selected);
                var pixels = new byte[snapshot.PixelWidth * snapshot.PixelHeight * 4];
                snapshot.CopyPixels(pixels, snapshot.PixelWidth * 4, 0);
                True(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] > 0 &&
                    pixels[index * 4] < 150 && pixels[index * 4 + 1] < 150 && pixels[index * 4 + 2] < 150) > 30,
                    "non-first source rows retain their rendered labels in a local-coordinate snapshot");
                foreach (var pair in new[] { ("SearchPanels", "SongSearchPanel", "PlaylistSearchPanel"),
                    ("QualityPanels", "QualityPriorities", "SpatialPriorities"), ("MetadataPanels", "MetadataPriorities", "ArtworkPriorities") })
                {
                    var panels = (Grid)view.FindName(pair.Item1);
                    var left = (FrameworkElement)view.FindName(pair.Item2); var right = (FrameworkElement)view.FindName(pair.Item3);
                    bool stacked = panels.ActualWidth < 544;
                    Equal(stacked, SettingsLayout.GetIsStacked(panels)); Equal(width == 600, stacked);
                    var a = new Rect(left.TranslatePoint(new Point(), panels), left.RenderSize);
                    var b = new Rect(right.TranslatePoint(new Point(), panels), right.RenderSize);
                    True(stacked ? b.Top >= a.Bottom && Math.Abs(a.Width - panels.ActualWidth) < .5 :
                        Math.Abs(b.Left - a.Right - 24) < .5 && Math.Abs(a.Width - b.Width) < .5 && Math.Abs(a.Top - b.Top) < .5,
                        pair.Item1 + " splits into two equal tiles and stacks only below its threshold");
                }
            }
        });
        yield return Case("replica priority toolbar clamps first last and unselected rows and saves once", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view);
            await Drain(); await Until(() => !f.Module.Busy);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListBox)priority.FindName("PriorityItems");
            var up = (Button)priority.FindName("MoveUpButton"); var down = (Button)priority.FindName("MoveDownButton");
            True(!priority.CanMoveUp && !priority.CanMoveDown && !up.IsEnabled && !down.IsEnabled, "unselected list has no toolbar move");
            list.SelectedIndex = 0; True(!priority.CanMoveUp && priority.CanMoveDown, "first row can only move down");
            int writes = f.Calls.Count("music.quality.settings.save"); var source = f.Module.QualityCategories[0];
            down.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => f.Module.CanEditQuality && f.Module.QualityCategories[1] == source);
            Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            list.SelectedIndex = list.Items.Count - 1; True(priority.CanMoveUp && !priority.CanMoveDown, "last row can only move up");
            up.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => f.Module.CanEditQuality);
            Equal(writes + 2, f.Calls.Count("music.quality.settings.save"));
            True(!f.Runtime.HasStarted, "toolbar uses the native quality owner");
        });
        yield return Case("replica captured priority gestures slide neighbours without a marker and commit one native move", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view);
            await Drain(); await Until(() => !f.Module.Busy);
            foreach (var item in new[] { ("SongPriorities", "music.search.settings.save"), ("PlaylistPriorities", "music.search.settings.save"),
                ("QualityPriorities", "music.quality.settings.save"), ("SpatialPriorities", "music.quality.settings.save"),
                ("MetadataPriorities", "music.metadata.settings.save"), ("ArtworkPriorities", "music.metadata.settings.save") })
            {
                var priority = (SettingsPriorityList)view.FindName(item.Item1); priority.BringIntoView(); await Drain(); view.UpdateLayout();
                var list = (ListBox)priority.FindName("PriorityItems"); var scroll = priority.DragViewport;
                const int from = 2, to = 3;
                var grip = Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(from)).OfType<Thumb>().Single();
                var original = list.Items.Cast<object>().ToArray(); var target = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(to);
                var above = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(from - 1);
                var drop = target.TranslatePoint(new Point(60, target.ActualHeight - 2), scroll); int writes = f.Calls.Count(item.Item2);
                priority.Drag.Begin(grip, list, grip.TranslatePoint(new Point(18, 18), scroll)); priority.Drag.Update(drop);
                True(priority.IsDragging && priority.Drag.DestinationIndex == to, "drag tracks the lifted row centre");
                True(!priority.CanMoveUp && !priority.CanMoveDown, "drag disables toolbar");
                Equal(0d, ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(from)).Opacity);
                var shift = ((TransformGroup)target.RenderTransform).Children.OfType<TranslateTransform>().Single();
                Equal(-target.ActualHeight, (double)shift.GetAnimationBaseValue(TranslateTransform.YProperty));
                True(above.RenderTransform is not TransformGroup && list.Items.Cast<object>().SequenceEqual(original),
                    "only the passed neighbour slides and data waits for release");
                var layer = AdornerLayer.GetAdornerLayer(scroll)!; var preview = layer.GetAdorners(scroll)!.Single();
                Equal(1, VisualTreeHelper.GetChildrenCount(preview));
                True(VisualTreeHelper.GetChild(preview, 0) is Border, "the lifted row has no insertion marker or vacancy outline");
                layer.UpdateLayout(); CaptureSettings(priority, "replica-drag-" + item.Item1 + ".png");
                priority.CompleteDrag(drop, false); await Until(() => !f.Module.Busy && list.Items[to] == original[from]);
                True(target.RenderTransform is not TransformGroup, "release restores neighbour geometry");
                var expected = original.ToList(); expected.RemoveAt(from); expected.Insert(to, original[from]);
                Equal(writes + 1, f.Calls.Count(item.Item2)); True(list.Items.Cast<object>().SequenceEqual(expected), "only the intended position changed");
                priority.CompleteDrag(drop, false); Equal(writes + 1, f.Calls.Count(item.Item2));
            }
        });
        yield return Case("replica gestures discard stale release on hide resize disable collection and final off-list hit", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view);
            await Drain(); await Until(() => !f.Module.Busy);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); priority.BringIntoView(); await Drain(); view.UpdateLayout();
            var list = (ListBox)priority.FindName("PriorityItems"); var scroll = priority.DragViewport;
            void Begin()
            {
                priority.UpdateLayout(); var grip = Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).OfType<Thumb>().Single();
                priority.Drag.Begin(grip, list, grip.TranslatePoint(new Point(18, 18), scroll));
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2); priority.Drag.Update(row.TranslatePoint(new Point(60, 2), scroll));
                True(priority.IsDragging, "current row owns the gesture");
            }
            int writes = f.Calls.Count("music.quality.settings.save"); Begin(); priority.CompleteDrag(new Point(scroll.ActualWidth + 20, 100), false);
            True(!priority.IsDragging, "off-list final pointer rejects cached insertion");
            Begin(); priority.Visibility = Visibility.Collapsed; True(!priority.IsDragging, "hidden view retires gesture"); priority.Visibility = Visibility.Visible; view.UpdateLayout();
            Begin(); mount.Window.Width = 908; mount.Window.UpdateLayout(); True(!priority.IsDragging, "resize retires old geometry");
            Begin(); view.IsEnabled = false; True(!priority.IsDragging, "lost authority retires gesture"); view.IsEnabled = true;
            Begin(); priority.Drag.Cancel(); priority.CompleteDrag(new Point(40, 80), false); Equal(writes, f.Calls.Count("music.quality.settings.save"));
            Begin(); await f.Module.MoveQualityAsync(f.Module.QualityCategories[3], f.Module.QualityCategories[1], false);
            True(!priority.IsDragging && priority.Drag.Complete(new Point(40, 80), false) is null, "changed data fences stale gesture");
            Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
        });
        yield return Case("replica clipped priority lists reject footer drops and retired controls cannot save", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); mount.Window.Height = 300;
            await Drain(); ReplicaAnchors(view)[2].IsChecked = true; await Drain(); view.UpdateLayout();
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListBox)priority.FindName("PriorityItems");
            var scroll = priority.DragViewport; var source = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var grip = Visuals(source).OfType<Thumb>().Single();
            var clippedDrop = view.TranslatePoint(new Point(priority.TranslatePoint(new Point(80, 0), view).X, view.ActualHeight + 10), scroll);
            True(clippedDrop.Y > 0 && clippedDrop.Y < scroll.ActualHeight, "footer pointer is inside the unclipped inner list");
            int writes = f.Calls.Count("music.quality.settings.save");
            priority.Drag.Begin(grip, list, grip.TranslatePoint(new Point(18, 18), scroll));
            priority.Drag.Update(clippedDrop); priority.CompleteDrag(clippedDrop, false);
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!priority.IsDragging && source.Opacity == 1, "outer viewport clipping fences the final drop");
            list.SelectedIndex = 2;
            var down = (Button)priority.FindName("MoveDownButton"); var toggle = Visuals(source).OfType<CheckBox>().Single();
            view.Visibility = Visibility.Collapsed; await Drain();
            down.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!priority.CanMoveUp && !priority.CanMoveDown, "hidden controls cannot submit");
            view.Visibility = Visibility.Visible; await Drain();
            mount.Window.Content = null; await Drain();
            down.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!priority.CanMoveUp && !priority.CanMoveDown, "unloaded controls cannot submit");
        });
        yield return Case("replica settings remain within shell content and retain the hidden global player", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask)
            {
                Width = 1440, Height = 900, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                shell.Show(); await Drain(); await Until(() => f.Module.CanEdit);
                var region = (Grid)shell.FindName("ContentRegion"); Equal(new Thickness(0), region.Margin);
                var player = ((ContentControl)shell.FindName("PlayerHost")).Content;
                True(player is not null, "ordinary page mounts the global player");
                ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Drain(); await Until(() => f.Module.CanEdit);
                var root = (FrameworkElement)shell.Content;
                var heading = (TextBlock)shell.FindName("SettingsHeading"); var slot = (ContentControl)shell.FindName("SettingsHeaderSlot");
                var back = (Button)shell.FindName("BackButton");
                True(heading.IsVisible && heading.Text == "设置" && slot.IsVisible && region.Margin == new Thickness(0) &&
                    !((FrameworkElement)shell.FindName("SearchInput")).IsVisible, "settings title and category sit in the header beside back");
                True(heading.TranslatePoint(new Point(), root).X > back.TranslatePoint(new Point(back.ActualWidth, 0), root).X &&
                    slot.TranslatePoint(new Point(), root).X > heading.TranslatePoint(new Point(heading.ActualWidth, 0), root).X,
                    "header order is back, settings, category");
                var areaTabs = (Panel)slot.Content; var area = areaTabs.Children.OfType<RadioButton>().ToArray(); Equal("设置类别", AutomationProperties.GetName(areaTabs));
                True(area.Select(tab => (string)tab.Content).SequenceEqual(new[] { "音乐", "影视", "小说" }) &&
                    area[0].IsChecked == true && area.Skip(1).All(tab => tab.IsChecked == false), "category tabs offer music video and books");
                var settingsView = (MusicSettingsView)f.Module.SettingsView; var tabs = (Panel)settingsView.FindName("SettingsNavigation");
                var accent = ((SolidColorBrush)settingsView.FindResource("Settings.Brush.Accent")).Color; var selectedLine = (Border)area[0].Template.FindName("Underline", area[0]);
                True(area.All(tab => ReferenceEquals(tab.Style, settingsView.FindResource("Settings.Anchor"))) && selectedLine.BorderBrush is SolidColorBrush line && line.Color == accent &&
                    ((Border)area[1].Template.FindName("Underline", area[1])).BorderBrush is SolidColorBrush { Color.A: 0 }, "header category reuses the settings anchor tabs outside the view");
                double tabsTop = tabs.TranslatePoint(new Point(), root).Y, regionTop = region.TranslatePoint(new Point(), root).Y;
                True(tabsTop - regionTop < 1, "settings content starts directly below the compact header");
                foreach (var size in new[] { new Size(1440, 900), new Size(1110, 869), new Size(860, 600) })
                {
                    shell.Width = size.Width; shell.Height = size.Height; shell.UpdateLayout(); await Drain(); root.UpdateLayout();
                    var sidebar = (ScrollViewer)shell.FindName("SidebarScroll"); var footer = (Border)shell.FindName("PlayerFrame");
                    Equal(Visibility.Visible, sidebar.Visibility); Equal(Visibility.Collapsed, footer.Visibility);
                    True(ReferenceEquals(player, ((ContentControl)shell.FindName("PlayerHost")).Content),
                        "settings retain the same global player instance");
                    var view = (MusicSettingsView)f.Module.SettingsView; var bounds = new Rect(view.TranslatePoint(new Point(), root), view.RenderSize);
                    True(bounds.Left >= 203 && bounds.Bottom <= root.ActualHeight + 1,
                        "settings remain inside the shell while the player footer is hidden");
                    if (size.Width == 860)
                    {
                        True(SettingsLayout.GetIsNarrow(view), "sidebar-reduced content uses stacked section labels");
                        var accountSection = (Border)view.FindName("AccountsSection");
                        var accountLabel = ((Grid)accountSection.Child).Children.OfType<TextBlock>().Single(text => text.Text == "账号");
                        var accounts = (MusicAccountsView)view.FindName("AccountsPanel");
                        True(accounts.TranslatePoint(new Point(), accountSection).Y >=
                            accountLabel.TranslatePoint(new Point(0, accountLabel.ActualHeight), accountSection).Y,
                            "the minimum shell does not waste a label column beside its account table");
                    }
                    ReplicaAnchors(view)[^1].IsChecked = true; await Drain(); root.UpdateLayout();
                    True(((MusicSourcesSettingsView)view.FindName("SourcesPage")).IsVisible &&
                        footer.Visibility == Visibility.Collapsed, "independent source management keeps the player hidden");
                    if (size.Width == 860)
                    {
                        var sources = (MusicSourcesSettingsView)view.FindName("SourcesPage");
                        var items = (ItemsControl)sources.FindName("SourceList");
                        var body = (ScrollViewer)items.Template.FindName("ItemsScroll", items);
                        True(body.ViewportHeight >= 82 && body.ScrollableHeight > 0,
                            "the minimum shell retains a visible source row and its internal scrollbar");
                    }
                    CaptureSettings(root, "replica-shell-source-" + (int)size.Width + ".png");
                    ReplicaAnchors(view)[0].IsChecked = true; await Drain(); root.UpdateLayout();
                    CaptureSettings(root, "replica-shell-accounts-" + (int)size.Width + ".png");
                    True(!f.Runtime.HasStarted, "ordinary replica settings never activate scripts");
                }
                var content = (FrameworkElement)settingsView.FindName("MusicContent");
                area[1].IsChecked = true; await Drain(); root.UpdateLayout();
                True(!content.IsVisible && !Visuals(settingsView).OfType<TextBlock>().Any(text => text.IsVisible) && ((ScrollViewer)shell.FindName("SidebarScroll")).IsVisible &&
                    ((Border)shell.FindName("PlayerFrame")).Visibility == Visibility.Collapsed &&
                    ReferenceEquals(player, ((ContentControl)shell.FindName("PlayerHost")).Content) &&
                    heading.IsVisible && slot.IsVisible && area[1].IsChecked == true && area[0].IsChecked == false,
                    "video settings are an empty page that retains but hides the global player");
                area[2].IsChecked = true; await Drain(); True(!content.IsVisible && area[1].IsChecked == false, "book settings are an empty page");
                area[0].IsChecked = true; await Drain(); await Until(() => f.Module.CanEdit); root.UpdateLayout();
                True(content.IsVisible, "music settings return");
                back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain(); await Until(() => !heading.IsVisible);
                True(region.Margin == new Thickness(0) && !slot.IsVisible && slot.Content is null,
                    "home keeps the compact shared content gap");
                Equal(Visibility.Visible, ((Border)shell.FindName("PlayerFrame")).Visibility);
                True(ReferenceEquals(player, ((ContentControl)shell.FindName("PlayerHost")).Content),
                    "home restores the same global player instance");
            }
            finally { shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5)); await Drain(); }
        });
        yield return Case("replica search advanced bubble saves each kind and only source management keeps wheel input", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view); mount.Window.Height = 560;
            await Drain(); await Until(() => !f.Module.Busy); view.UpdateLayout();
            True(!Visuals(view).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box).Contains("搜索模式")),
                "search mode controls are removed");
            var popup = (Popup)view.FindName("SearchAdvancedPopup");
            foreach (var (kind, label, pages, concurrency) in new[]
            {
                ("songs", "歌曲搜索高级设置", 11, 12),
                ("playlists", "歌单搜索高级设置", 2, 0)
            })
            {
                var button = Visuals(view).OfType<Button>().Single(b => AutomationProperties.GetName(b) == label);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
                True(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, button), "advanced bubble opens below its button");
                ((TextBox)view.FindName("SearchPagesBox")).Text = pages.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ((TextBox)view.FindName("SearchConcurrencyBox")).Text = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);
                popup.IsOpen = false; await Drain(); await Until(() => !f.Module.Busy && f.Module.CanEditSearchPreferences);
                var saved = kind == "songs" ? f.Module.SongSearch : f.Module.PlaylistSearch;
                True(saved.Pages == pages && saved.Concurrency == Math.Max(1, concurrency) && saved.CacheMinutes == 60,
                    "positive page and concurrency values are saved; invalid fields are ignored");
                view.UpdateLayout();
                True(((FrameworkElement)view.FindName("SongPriorities")).IsVisible && ((FrameworkElement)view.FindName("PlaylistPriorities")).IsVisible,
                    "platform lists remain visible");
            }
            var settings = (await f.Rpc("music.search.settings.get")).GetProperty("settings");
            True(settings.GetProperty("songs").GetProperty("pages").GetInt32() == 11 &&
                settings.GetProperty("songs").GetProperty("concurrency").GetInt32() == 12 &&
                settings.GetProperty("playlists").GetProperty("pages").GetInt32() == 2 &&
                settings.GetProperty("playlists").GetProperty("concurrency").GetInt32() == 1,
                "advanced settings persisted per kind");
            var scroll = (ScrollViewer)view.FindName("SettingsScroller");
            void Wheel(UIElement target, RoutedEvent routed)
            {
                target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = routed });
                view.UpdateLayout();
            }
            var accounts = (ItemsControl)((MusicAccountsView)view.FindName("AccountsPanel")).FindName("PlatformSettingsList");
            var rows = new UIElement[] { (UIElement)accounts.ItemContainerGenerator.ContainerFromIndex(0) }
                .Concat(new[] { "SongPriorities", "QualityPriorities", "MetadataPriorities" }.Select(name =>
                    (UIElement)((ListBox)((SettingsPriorityList)view.FindName(name)).FindName("PriorityItems")).ItemContainerGenerator.ContainerFromIndex(0)));
            foreach (var row in rows)
            {
                scroll.ScrollToVerticalOffset(0); view.UpdateLayout();
                Wheel(row, UIElement.PreviewMouseWheelEvent); Wheel(row, UIElement.MouseWheelEvent);
                True(scroll.VerticalOffset > 0, "ordinary settings rows pass the wheel to the page");
            }
            var picker = Visuals(view).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "播放请求音质");
            scroll.ScrollToVerticalOffset(0); view.UpdateLayout(); picker.IsDropDownOpen = true; await Drain();
            Wheel(picker, UIElement.PreviewMouseWheelEvent); Equal(0d, scroll.VerticalOffset);
            picker.IsDropDownOpen = false; await Drain();
            ReplicaAnchors(view)[^1].IsChecked = true; await Drain(); view.UpdateLayout();
            var sources = (ItemsControl)((MusicSourcesSettingsView)view.FindName("SourcesPage")).FindName("SourceList");
            var body = (ScrollViewer)sources.Template.FindName("ItemsScroll", sources);
            True(body.ScrollableHeight > 0, "source fixture overflows its own list");
            Wheel((UIElement)sources.ItemContainerGenerator.ContainerFromIndex(0), UIElement.PreviewMouseWheelEvent);
            Wheel((UIElement)sources.ItemContainerGenerator.ContainerFromIndex(0), UIElement.MouseWheelEvent);
            True(body.VerticalOffset > 0, "source management keeps its independent list scroll");
        });
        yield return Case("replica priority switch changes enabled only and preserves exact saved order", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListBox)priority.FindName("PriorityItems");
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0); var toggle = Visuals(row).OfType<CheckBox>().Single();
            var order = f.Module.QualityCategories.Select(value => value.Key).ToArray(); bool next = !f.Module.QualityCategories[0].Enabled;
            int writes = f.Calls.Count("music.quality.settings.save"); toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, next); toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => f.Module.CanEditQuality); Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            True(order.SequenceEqual(f.Module.QualityCategories.Select(value => value.Key)) && f.Module.QualityCategories[0].Enabled == next && !priority.IsDragging, "enable is not an ordering gesture");
        });
        yield return Case("replica narrow settings separate labels controls and stacked appearance fields", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            Render(view, 600, 760); await Drain(); view.UpdateLayout();
            var cache = (Border)view.FindName("CacheSection");
            var label = Visuals(cache).OfType<TextBlock>().Single(text => text.Text == "播放缓存");
            var clear = Visuals(cache).OfType<Button>().Single(button => Equals(button.Content, "清空播放缓存"));
            True(clear.TranslatePoint(new Point(), cache).Y >= label.TranslatePoint(new Point(0, label.ActualHeight), cache).Y,
                "narrow cache control is below its section label");
            var fields = (UniformGrid)view.FindName("AppearanceFields"); Equal(1, fields.Columns);
            var pickers = Visuals(fields).OfType<ComboBox>().ToArray(); Equal(3, pickers.Length);
            for (int index = 1; index < pickers.Length; index++)
                True(pickers[index].TranslatePoint(new Point(), fields).Y >
                    pickers[index - 1].TranslatePoint(new Point(0, pickers[index - 1].ActualHeight), fields).Y + 16,
                    "stacked appearance controls retain field spacing");
        });
        yield return Case("replica native keyboard anchors and high contrast preserve current semantics", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync(); var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            var anchors = ReplicaAnchors(view); True(anchors[0].Focus(), "anchor has real native focus");
            foreach (var step in new[] { (Key.End, 5), (Key.Home, 0), (Key.Right, 1), (Key.Left, 0) })
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, step.Item1) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                ((UIElement)Keyboard.FocusedElement).RaiseEvent(input); await Drain(); mount.Window.UpdateLayout();
                True(input.Handled && anchors[step.Item2].IsChecked == true && anchors[step.Item2].IsKeyboardFocused, "anchors navigate and retain focus");
            }
            DesktopTheme.SetIsHighContrast(view, true); view.UpdateLayout();
            Equal(SystemColors.WindowColor, ((SolidColorBrush)view.Background).Color);
            var picker = Visuals(view).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "播放请求音质");
            SimulateFocus(picker, UIElement.IsKeyboardFocusWithinProperty, true); view.UpdateLayout();
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)picker.BorderBrush).Color);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities");
            var list = (ListBox)priority.FindName("PriorityItems"); list.SelectedIndex = 2; view.UpdateLayout();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
            var label = Visuals(row).OfType<TextBlock>().Single(text => text.Text == ((MusicQualityRow)list.Items[2]).Label);
            Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)label.Foreground).Color);
            var grip = Visuals(row).OfType<Thumb>().Single();
            var bars = (System.Windows.Shapes.Path)grip.Template.FindName("Bars", grip);
            Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)bars.Stroke).Color);
            True(bars.Data.Bounds.Width > bars.Data.Bounds.Height, "grip draws three horizontal bars");
        });
        yield return Case("replica screenshot states use real owner data and official resources without remote reads", async () =>
        {
            await using var f = await DetailsScene.Create(); await f.Module.ActivateSettingsAsync(); var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            foreach (int width in new[] { 1248, 908, 600 })
            {
                mount.Window.Width = width; mount.Window.UpdateLayout(); await Drain();
                foreach (var target in new[] { (0, "accounts"), (1, "search"), (2, "quality"), (3, "appearance"), (4, "metadata") })
                {
                    ReplicaAnchors(view)[target.Item1].IsChecked = true; await Drain(); view.UpdateLayout();
                    CaptureSettings(view, "replica-" + target.Item2 + "-" + width + ".png");
                }
            }
            Equal(0, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
        });
    }
    private static RadioButton[] ReplicaAnchors(MusicSettingsView view) => ((Panel)view.FindName("SettingsNavigation")).Children.OfType<RadioButton>().ToArray();
    private sealed class ReplicaMount : IDisposable
    {
        internal Window Window { get; }
        internal ReplicaMount(FrameworkElement view)
        {
            Window = new() { Content = view, Width = 1248, Height = 820, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            Window.Show();
        }
        public void Dispose() { Window.Content = null; Window.Close(); }
    }
    private static void CaptureSettings(FrameworkElement view, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT") is not { Length: > 0 } root) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root); Directory.CreateDirectory(root);
        var bitmap = SettingsReorderDrag.Snapshot(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(root, name)); encoder.Save(output);
    }
}
