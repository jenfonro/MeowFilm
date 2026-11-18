using FontWeights = Microsoft.UI.Text.FontWeights;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using global::Windows.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Windows.System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
        yield return Case("wrong source dialog does not reopen from a retired request after returning to settings", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.queue.replace", new { songs = new[] { MusicSongFixture.Online("wy", "LateForm") }, index = 0 });
            await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            ReplicaAnchors(view)[2].IsChecked = true; await Drain();
            var button = (Button)view.FindName("ExclusionAddButton");
            var popup = (ContentDialog)view.FindName("ExclusionPopup");
            var queue = (await f.Rpc("music.queue.get", new { offset = 0, limit = 100 })).GetRawText();
            int opened = 0;
            popup.Opened += (_, _) => opened++;
            foreach (string retirement in new[] { "hidden", "unloaded", "sources" })
            {
                var pending = f.Calls.HoldNext("music.queue.get");
                Click(button); await pending.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    True(!MusicTestUi.IsDialogOpen(popup), "the held form read has not opened a dialog");
                    switch (retirement)
                    {
                        case "hidden":
                            view.Visibility = Visibility.Collapsed; await Drain();
                            True(!DesktopVisual.IsVisible(button), "hiding settings retires the form entry");
                            view.Visibility = Visibility.Visible;
                            break;
                        case "unloaded":
                            mount.Window.Content = null; await Drain();
                            True(!button.IsLoaded, "unloading settings retires the form entry");
                            mount.Window.Content = view;
                            break;
                        default:
                            ReplicaAnchors(view)[^1].IsChecked = true; await Drain();
                            True(DesktopVisual.IsVisible(view) && !DesktopVisual.IsVisible(button), "source management retires the form without hiding the outer view");
                            ReplicaAnchors(view)[2].IsChecked = true;
                            break;
                    }
                    await Drain();
                    True(button.IsLoaded && DesktopVisual.IsVisible(button), "the original settings entry is visible again before the late reply");
                }
                finally { pending.Release(); }
                await Until(() => !f.Module.Busy);
                True(!MusicTestUi.IsDialogOpen(popup) && opened == 0, "a retired " + retirement + " request must not reopen the dialog");
                Equal(0, f.Calls.Count("music.sourceExclusions.add"));
                Equal(queue, (await f.Rpc("music.queue.get", new { offset = 0, limit = 100 })).GetRawText());
            }

            Click(button); await Until(() => MusicTestUi.IsDialogOpen(popup));
            Equal(1, opened);
            Click(Visuals((DependencyObject)popup.Content).OfType<Button>().Single(control => Equals(control.Content, "取消")));
            await Drain();
            True(!MusicTestUi.IsDialogOpen(popup), "a fresh form still cancels normally");
            Equal(0, f.Calls.Count("music.sourceExclusions.add"));
            Click(button); await Until(() => MusicTestUi.IsDialogOpen(popup));
            var song = (ComboBox)view.FindName("ExclusionSong");
            var provider = (ComboBox)view.FindName("ExclusionProvider");
            var platform = (ComboBox)view.FindName("ExclusionPlatform");
            song.SelectedIndex = 0;
            provider.SelectedItem = provider.Items.Cast<MusicExclusionOption>().Single(option => option.Key == "official");
            platform.SelectedItem = platform.Items.Cast<MusicExclusionOption>().Single(option => option.Key == "wy");
            string songId = ((MusicExclusionOption)song.SelectedItem).Key;
            Click(Visuals((DependencyObject)popup.Content).OfType<Button>().Single(control => Equals(control.Content, "添加")));
            await Until(() => !f.Module.Busy && f.Module.SourceExclusions.Count == 1);
            True(!MusicTestUi.IsDialogOpen(popup) && opened == 2, "a fresh confirmed form closes after a single add");
            Equal(1, f.Calls.Count("music.sourceExclusions.add"));
            Equal(songId, f.Module.SourceExclusions.Single().SongId);
            Equal(queue, (await f.Rpc("music.queue.get", new { offset = 0, limit = 100 })).GetRawText());
        });
        yield return Case("wrong source list keeps its viewport and restores rows without growing the page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain();
            var list = (ItemsControl)view.FindName("ExclusionList");
            var viewport = (Grid)list.Parent;
            Equal(264d, viewport.ActualHeight);
            foreach (int width in new[] { 1248, 600 })
            {
                mount.Window.Resize(width, 820); DesktopTestUi.WindowRoot(mount.Window).UpdateLayout(); await Drain();
                var section = (Border)view.FindName("ExclusionSection");
                var heading = (FrameworkElement)((Button)view.FindName("ExclusionAddButton")).Parent;
                var card = (Border)viewport.Parent;
                double headingBottom = heading.TransformToVisual(section).TransformPoint(new Point(0, heading.ActualHeight)).Y;
                double cardTop = card.TransformToVisual(section).TransformPoint(new Point()).Y;
                True(Math.Abs(cardTop - headingBottom - 8d) < .5,
                    "wrong source heading and card keep the required 8 DIP gap at width " + width);
                Equal(264d, viewport.ActualHeight);
            }
            for (int i = 0; i < 30; i++)
                await f.Rpc("music.sourceExclusions.add", new
                {
                    songId = "fixture:" + i, provider = "official", platform = "wy",
                    providerName = "网易云音乐", title = "错源歌曲 " + i, artist = "测试艺人"
                });
            await f.Module.RefreshSourceExclusionsAsync(); view.UpdateLayout(); await Drain();
            Equal(264d, viewport.ActualHeight);
            Equal(30, list.Items.Count);
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            True(scroll.ScrollableHeight > 0, "many exclusions scroll within their own fixed viewport");
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true); view.UpdateLayout(); await Drain();
            True(scroll.VerticalOffset > 0 && list.ContainerFromIndex(29) is not null,
                "the final record is reachable without restricting record count");
            await f.Module.RestoreSourceExclusionAsync(f.Module.SourceExclusions[^1]);
            await Drain(); view.UpdateLayout();
            Equal(29, list.Items.Count); Equal(264d, viewport.ActualHeight);
        });
        yield return Case("settings dropdown content follows normal highlighted and disabled foregrounds without changing values", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.queue.replace", new { songs = new[] { MusicSongFixture.Online("wy", "Dropdown") }, index = 0 });
            await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            ReplicaAnchors(view)[2].IsChecked = true; await Drain();
            var quality = Visuals(view).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "起播目标音质");
            var labels = new Dictionary<string, string>
            {
                ["auto"] = "自动", ["128k"] = "128 kbps", ["192k"] = "192 kbps", ["320k"] = "320 kbps",
                ["flac"] = "无损", ["flac24bit"] = "高解析度", ["master"] = "母带"
            };
            await CheckContent(quality, item => labels[(string)item]);
            Click((Button)view.FindName("ExclusionAddButton"));
            var popup = (ContentDialog)view.FindName("ExclusionPopup");
            await Until(() => MusicTestUi.IsDialogOpen(popup)); await Drain();
            var song = (ComboBox)view.FindName("ExclusionSong");
            song.SelectedIndex = 0; await Drain();
            await CheckContent(song, item =>
            {
                var option = (MusicExclusionOption)item;
                return option.Name + "  " + option.Detail;
            });
            popup.Hide();

            async Task CheckContent(ComboBox picker, Func<object, string> label)
            {
                object selected = picker.SelectedItem;
                picker.IsDropDownOpen = true; await Drain(); picker.UpdateLayout();
                foreach (var (item, index) in picker.Items.Cast<object>().Select((item, index) => (item, index)))
                {
                    var container = (ComboBoxItem)picker.ContainerFromIndex(index);
                    var text = Visuals(container).OfType<TextBlock>().Single();
                    Equal(label(item), text.Text);
                    True(text.Foreground is SolidColorBrush { Color.A: > 0 } && text.FontWeight == FontWeights.Normal,
                        "actual dropdown labels remain readable and regular");
                    foreach (var state in new[] { "PointerOver", "Normal" })
                    {
                        True(VisualStateManager.GoToState(container, state, false), "native option state exists");
                        Equal(label(item), text.Text);
                        True(text.Foreground is SolidColorBrush { Color.A: > 0 }, "highlight never hides the option label");
                    }
                }
                picker.IsDropDownOpen = false; await Drain();
                var closedText = Visuals(picker).OfType<TextBlock>().Single(text => text.Text == label(selected));
                DependencyObject? ancestor = VisualTreeHelper.GetParent(picker);
                while (ancestor is not null && ancestor is not Control) ancestor = VisualTreeHelper.GetParent(ancestor);
                var parent = ancestor as Control ?? throw new InvalidOperationException("Dropdown has no control owner");
                parent.IsEnabled = false; await Drain();
                True(!picker.IsEnabled && closedText.Foreground is SolidColorBrush { Color.A: > 0 }, "disabled selection stays readable");
                Equal(label(selected), closedText.Text); Equal(selected, picker.SelectedItem);
                parent.ClearValue(Control.IsEnabledProperty); await Drain();
                True(picker.IsEnabled, "parent disable preserves the selector's business binding");
            }
        });
        yield return Case("shared pair layout follows its container and retires resize on unload or disable", async () =>
        {
            var left = new Border { Height = 40 }; var right = new Border { Height = 40 };
            var pair = new Grid { Width = 544, HorizontalAlignment = HorizontalAlignment.Left };
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new(24) });
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
            pair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pair.Children.Add(left); pair.Children.Add(right);
            SettingsLayout.SetMode(pair, SettingsLayoutMode.Pair);
            using var mount = new ReplicaMount(pair); await Drain();
            foreach (double width in new[] { 544d, 543, 545, 400, 544 })
            {
                pair.Width = width; pair.UpdateLayout(); await Drain();
                bool stacked = width < 544;
                Equal(stacked ? 1 : 0, Grid.GetRow(right)); Equal(stacked ? 0 : 2, Grid.GetColumn(right));
                var a = DesktopTestUi.Bounds(left, pair); var b = DesktopTestUi.Bounds(right, pair);
                True(stacked ? b.Top >= a.Bottom && Math.Abs(b.Width - a.Width) < 1 :
                    Math.Abs(b.Left - a.Right - 24) < 1 && Math.Abs(a.Width - b.Width) < 1,
                    "the actual pair geometry follows the breakpoint");
            }
            mount.Window.Content = null; await Drain();
            pair.Width = 400; pair.Measure(new Size(400, 200)); pair.Arrange(new Rect(0, 0, 400, 200)); pair.UpdateLayout();
            Equal(0, Grid.GetRow(right)); True(!pair.IsLoaded, "detached measure cannot reactivate layout");
            mount.Window.Content = pair; mount.Window.AttachRoot(); await Drain();
            Equal(1, Grid.GetRow(right));
            SettingsLayout.SetMode(pair, SettingsLayoutMode.None);
            pair.Width = 544; pair.UpdateLayout(); await Drain();
            Equal(1, Grid.GetRow(right));
            SettingsLayout.SetMode(pair, SettingsLayoutMode.Pair); await Drain(); Equal(0, Grid.GetRow(right));
        });
        yield return Case("replica catalog source saves only its authority and preserves search audio sources queue and media", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();

            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView; using var mount = new ReplicaMount(view);
            await Until(() => f.Module.CanEditCatalogSource);
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            Equal("音乐数据源", AutomationProperties.GetName(picker));
            True(ReferenceEquals(picker.Style, DesktopVisual.Resource(view, "Settings.ComboBox")), "source picker reuses the established control style");
            True(picker.Items.Cast<MusicCatalogChoice>().Select(item => (item.Key, item.Name))
                .SequenceEqual(new[] { ("wy", "网易云音乐"), ("kg", "酷狗音乐") }), "only supported discovery sources are offered");
            Equal("wy", picker.SelectedValue); Equal(0, f.Calls.Count("music.catalogSource.settings.save"));
            var search = (await f.Rpc("music.search.settings.get")).GetProperty("settings").GetRawText();
            var audio = (await f.Rpc("music.officialSources.settings.get")).GetProperty("settings").GetRawText();
            var online = (await f.Rpc("music.settings.get")).GetProperty("settings").GetRawText();
            var queue = await f.Rpc("music.queue.get");
            var identity = f.Playback.Identity; int opens = f.Playback.Opens.Count;
            var hold = f.Calls.HoldNext("music.catalogSource.settings.save");
            True(picker.Focus(FocusState.Programmatic), "source selector accepts native focus");
            var selecting = DesktopTestUi.SelectAsync(picker, picker.Items.Cast<MusicCatalogChoice>().Single(item => item.Key == "kg"));
            await hold.Arrived.Task; await Drain();
            Equal("wy", f.Module.CatalogSourcePlatform);
            True(!picker.IsEnabled, "an in-flight receipt cannot admit another source save");
            hold.Release(); await selecting; await Until(() => f.Module.CanEditCatalogSource && Equals(picker.SelectedValue, "kg"));
            Equal("kg", f.Module.CatalogSourcePlatform); Equal(1, f.Calls.Count("music.catalogSource.settings.save"));
            Equal("kg", f.Calls.Last("music.catalogSource.settings.save").GetProperty("platform").GetString());
            Equal(search, (await f.Rpc("music.search.settings.get")).GetProperty("settings").GetRawText());
            Equal(audio, (await f.Rpc("music.officialSources.settings.get")).GetProperty("settings").GetRawText());
            Equal(online, (await f.Rpc("music.settings.get")).GetProperty("settings").GetRawText());
            var after = await f.Rpc("music.queue.get");
            Equal(queue.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
            Equal(queue.GetProperty("entries").GetRawText(), after.GetProperty("entries").GetRawText());
            Equal(identity, f.Playback.Identity); Equal(opens, f.Playback.Opens.Count);
            True(f.Playback.HasMedia && !f.Runtime.HasStarted, "source selection neither replaces media nor starts scripts");
        });
        yield return Case("general source hiding drains an admitted save without publishing to the closed page", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => f.Module.CanEditCatalogSource);
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            var held = f.Calls.HoldNext("music.catalogSource.settings.save");
            picker.SetValue(ComboBox.SelectedValueProperty, "kg"); await held.Arrived.Task;
            view.Visibility = Visibility.Collapsed; await Drain();
            True(!f.Module.CanEditCatalogSource, "hidden source still owns its admitted write until actual completion");
            var shutdown = f.Module.PrepareShutdownAsync(); await Drain();
            True(!shutdown.IsCompleted, "App close waits for the same source owner outside music settings");
            held.Release(); await shutdown;
            Equal("wy", f.Module.CatalogSourcePlatform);
            Equal(1, f.Calls.Count("music.catalogSource.settings.save"));
            var settings = await MusicTestStores.For(f.Store).ReadAsync("local");
            True(settings.Revision.Generation > 0, "the confirmed source receipt is durable");
            await f.Module.CancelShutdownPreparationAsync();
            view.Visibility = Visibility.Visible;
            await Until(() => f.Module.CanEditCatalogSource && f.Module.CatalogSourcePlatform == "kg");
            Equal(1, f.Calls.Count("music.catalogSource.settings.save"));
        });
        yield return Case("replica catalog source rejected and unknown saves use notices rollback and authoritative readback", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView; using var mount = new ReplicaMount(view);
            var notices = new List<string>();
            NoticeOutlet.SetReceiver(DesktopTestUi.WindowRoot(mount.Window), notice => notices.Add(notice.Message));
            await Until(() => f.Module.CanEditCatalogSource);
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            f.Calls.FailNext("music.catalogSource.settings.save", "STORAGE_UNAVAILABLE");
            picker.SetValue(ComboBox.SelectedValueProperty, "kg");
            await Until(() => f.Module.CanEditCatalogSource && f.Module.HasCatalogSourceError && Equals(picker.SelectedValue, "wy"));
            Equal("wy", f.Module.CatalogSourcePlatform);
            True(notices.Contains("数据源设置保存失败"), "save rejection is published through the existing notice outlet");
            var saved = await f.Rpc("music.catalogSource.settings.get");
            Equal("wy", saved.GetProperty("settings").GetProperty("platform").GetString());
            f.Calls.TransformNext("music.catalogSource.settings.save", _ => JsonSerializer.SerializeToElement(new { ok = false, code = "COMMIT_UNKNOWN" }));
            int reads = f.Calls.Count("music.catalogSource.settings.get");
            picker.SetValue(ComboBox.SelectedValueProperty, "kg");
            await Until(() => f.Module.CanEditCatalogSource && f.Module.CatalogSourcePlatform == "kg" && Equals(picker.SelectedValue, "kg"));
            Equal(reads + 1, f.Calls.Count("music.catalogSource.settings.get"));
            True(!f.Module.HasCatalogSourceError && f.Module.OwnedNotices.Count == 1,
                "matching readback restores the value but cannot acknowledge the unknown save");
            Equal(2, f.Calls.Count("music.catalogSource.settings.save"));
            await f.Module.SetCatalogSourceAsync("wy");
            Equal(0, f.Module.OwnedNotices.Count);
        });
        yield return Case("replica catalog source failed reads expose retry and remain inside narrow general layout", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView; using var mount = new ReplicaMount(view);
            await Until(() => f.Module.CanEditCatalogSource);
            f.Calls.FailNext("music.catalogSource.settings.get", "STORAGE_UNAVAILABLE");
            await f.Module.ReadCatalogSourceSettingsAsync(); await Drain();
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            var retry = (Button)view.FindName("CatalogSourceRetry");
            True(!picker.IsEnabled && DesktopVisual.IsVisible(retry) && retry.IsEnabled, "a failed restore requires a visible explicit retry");
            foreach (int width in new[] { 1248, 908, 600 })
            {
                mount.Window.Resize(width, 820); DesktopTestUi.WindowRoot(mount.Window).UpdateLayout(); await Drain();
                var section = (Border)view.FindName("HomeSourceSection");
                var label = Visuals(section).OfType<TextBlock>().Single(text => text.Text == "音乐数据源");
                var text = new Rect(label.TransformToVisual(section).TransformPoint(new Point()), new Size(label.ActualWidth, label.ActualHeight));
                var choice = new Rect(picker.TransformToVisual(section).TransformPoint(new Point()), new Size(picker.ActualWidth, picker.ActualHeight));
                var action = new Rect(retry.TransformToVisual(section).TransformPoint(new Point()), new Size(retry.ActualWidth, retry.ActualHeight));
                True(text.Bottom <= choice.Top && choice.Right <= action.Left && action.Right <= section.ActualWidth,
                    "source label picker and retry remain separate and within the general section");
            }
            int reads = f.Calls.Count("music.catalogSource.settings.get");
            FilmUiTest.Click(retry);
            await Until(() => picker.IsEnabled && !DesktopVisual.IsVisible(retry));
            Equal(reads + 1, f.Calls.Count("music.catalogSource.settings.get"));
            Equal("wy", picker.SelectedValue); Equal(0, f.Calls.Count("music.catalogSource.settings.save"));
        });
        yield return Case("replica hidden unloaded and inactive general source controls cannot save", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView;
            var film = new SettingsNavigationTests.Module("film", "影视");
            await using var appearance = new MeowFilm.AppHost.AppAppearance(f.Store);
            var shell = new ShellWindow([f.Module, film], [], () => Task.CompletedTask, () => Task.CompletedTask, appearance: appearance)
            {
            };
            try
            {
                shell.Show(); await Drain();
                DesktopTestUi.Invoke((Button)DesktopTestUi.WindowRoot(shell).FindName("SettingsButton"));
                await Drain();
                var header = (DesktopPageHeader)DesktopTestUi.WindowRoot(shell).FindName("PageHeaderHost");
                var slot = (ContentControl)header.FindName("LeadingSlot");
                var areas = ((Panel)slot.Content).Children.OfType<RadioButton>().ToArray();
                areas[0].IsChecked = true; await Drain();
                var general = Visuals(DesktopTestUi.WindowRoot(shell)).OfType<GeneralSettingsView>().Single();
                ((Panel)general.FindName("Areas")).Children.OfType<RadioButton>().Single(tab => Equals(tab.Tag, "sources")).IsChecked = true;
                await Drain(); await Until(() => DesktopVisual.IsVisible(view) && f.Module.CanEditCatalogSource);
                var picker = (ComboBox)view.FindName("CatalogSourcePicker");
                int saves = f.Calls.Count("music.catalogSource.settings.save");
                view.Visibility = Visibility.Collapsed; await Drain();
                picker.SetValue(ComboBox.SelectedValueProperty, "kg"); await Drain();
                Equal(saves, f.Calls.Count("music.catalogSource.settings.save"));
                view.Visibility = Visibility.Visible; await Drain();
                picker.SelectedValue = f.Module.CatalogSourcePlatform;
                areas[2].IsChecked = true; await Drain();
                True(DesktopVisual.IsVisible(film.SettingsView) && !DesktopVisual.IsVisible(view), "the shared shell owns nonmusic settings navigation");
                picker.SetValue(ComboBox.SelectedValueProperty, "kg"); await Drain();
                Equal(saves, f.Calls.Count("music.catalogSource.settings.save"));
                areas[0].IsChecked = true; await Drain();
                await Until(() => DesktopVisual.IsVisible(view) && f.Module.CanEditCatalogSource);
                picker.SelectedValue = f.Module.CatalogSourcePlatform;
                ((Panel)general.FindName("HomeSourceContributions")).Children.Remove(view);
                await Drain(); True(!view.IsLoaded, "the general source controls are actually unloaded");
                picker.SetValue(ComboBox.SelectedValueProperty, "kg"); await Drain();
                Equal(saves, f.Calls.Count("music.catalogSource.settings.save"));
                Equal("wy", f.Module.CatalogSourcePlatform);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
        yield return Case("replica settings keep music anchors and continuous groups with an independent source page", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view);
            var owner = mount.Window;
            try
            {
                await RenderSettings(view, 1200, 760); await Drain();
                True(view.IsLoaded, "reference navigation uses the actual mounted native tree");
                foreach (var surface in new Control[] { view, (MusicSourcesSettingsView)view.FindName("SourcesPage") })
                    True(surface.FontFamily.Source == "XamlAutoFontFamily" && surface.FontWeight == FontWeights.Normal,
                        "settings surfaces keep regular system typography");
                var settingsText = Visuals(view).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
                True(settingsText.Length > 0 && settingsText.All(text =>
                    text.FontStyle == FontStyle.Normal),
                    "actual settings labels and values use Display and Auto");
                var anchors = ReplicaAnchors(view);
                True(anchors.Select(anchor => (string)anchor.Content).SequenceEqual(new[] { "账号与平台", "搜索", "音质", "歌词与封面", "音源" }), "latest v2 section names");
                int calls = f.Calls.Log.Count;
                var scroll = (ScrollViewer)view.FindName("SettingsScroller");
                for (int index = 0; index < anchors.Length - 1; index++)
                {
                    anchors[index].IsChecked = true; await RenderSettings(view, 1200, 760); await Drain(); view.UpdateLayout();
                    var section = (Border)view.FindName((string)anchors[index].Tag);
                    True(Math.Abs(section.TransformToVisual(scroll).TransformPoint(new Point()).Y) <= 1, "selected section starts at the viewport top");
                    Equal(index, Array.IndexOf(anchors, anchors.Single(anchor => anchor.IsChecked == true)));
                }
                anchors[^1].IsChecked = true; await RenderSettings(view, 1200, 760); await Drain();
                Equal(Visibility.Collapsed, scroll.Visibility); Equal(Visibility.Visible, ((MusicSourcesSettingsView)view.FindName("SourcesPage")).Visibility);
                anchors[0].IsChecked = true; await RenderSettings(view, 1200, 760); await Drain();
                Equal(Visibility.Visible, scroll.Visibility); Equal(Visibility.Collapsed, ((MusicSourcesSettingsView)view.FindName("SourcesPage")).Visibility);
                True(f.Calls.Log.Skip(calls).All(call => call.Method is "music.officialSources.settings.get" or
                    "music.quality.settings.get" or "music.sources.list"),
                    "source navigation reads its native settings and installations without writing");
                True(!f.Runtime.HasStarted, "reference navigation never starts script execution");
            }
            finally { owner.Content = null; owner.Close(); }
        });
        yield return Case("replica priority lists preserve 52 pixel rows unnumbered 36 pixel grips and paired panels", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view);
            foreach (int width in new[] { 1248, 1164, 1163, 908, 704, 703, 600, 1248 })
            {
                // Keep breakpoint inputs independent of native offscreen window constraints.
                view.Width = width;
                mount.Window.Resize(width, 760);
                await RenderSettings(view, width, 760); await Drain(); view.UpdateLayout();
                True(Math.Abs(view.ActualWidth - width) <= .5 / view.XamlRoot.RasterizationScale,
                    "the content reaches the requested breakpoint width within one half display pixel");
                var content = (Grid)view.FindName("MusicContent");
                True(Math.Abs(content.ActualWidth - (view.ActualWidth - 64)) < .5 / view.XamlRoot.RasterizationScale,
                    "the responsive container keeps both 32 DIP page margins at the current DPI");
                var accountSection = (Border)view.FindName("AccountsSection");
                var accountPanel = (StackPanel)accountSection.Child;
                var accountTitle = accountPanel.Children.OfType<Grid>().Single();
                var accounts = (MusicAccountsView)view.FindName("AccountsPanel");
                var titleBounds = new Rect(accountTitle.TransformToVisual(accountSection).TransformPoint(new Point()), new Size(accountTitle.ActualWidth, accountTitle.ActualHeight));
                var accountBounds = new Rect(accounts.TransformToVisual(accountSection).TransformPoint(new Point()), new Size(accounts.ActualWidth, accounts.ActualHeight));
                True(Math.Abs(titleBounds.Left - accountBounds.Left) < .5 && accountBounds.Top >= titleBounds.Bottom &&
                    accountBounds.Top - titleBounds.Bottom <= 9 && Math.Abs(accountBounds.Width - accountSection.ActualWidth) < 1,
                    "at every width the account heading is above a full-width table with a compact gap");
                var priority = (SettingsPriorityList)view.FindName("QualityPriorities");
                var list = (ListView)priority.FindName("PriorityItems");
                Equal(7, list.Items.Count);
                for (int index = 0; index < list.Items.Count; index++)
                {
                    var row = (ListViewItem)list.ContainerFromIndex(index);
                    Equal(52d, row.ActualHeight);
                    var grip = Visuals(row).OfType<Thumb>().Single(); Equal(36d, grip.ActualWidth); Equal(36d, grip.ActualHeight);
                    var label = Visuals(row).OfType<TextBlock>().Single(text => text.Text == ((MusicQualityRow)list.Items[index]).Label);
                    Equal(14d, label.FontSize); True(grip.UseSystemFocusVisuals && grip.IsTabStop, "dedicated grip has keyboard focus");
                    True(!Visuals(row).OfType<TextBlock>().Any(text => text.Text == (index + 1).ToString("00") || text.Text == (index + 1).ToString()),
                        "priority rows carry no serial number");
                    var toggle = Visuals(row).OfType<CheckBox>().Single(); Equal(32d, toggle.ActualWidth); Equal(18d, toggle.ActualHeight);
                }
                list.SelectedIndex = 2; view.UpdateLayout();
                var selected = (ListViewItem)list.ContainerFromIndex(2);
                Equal(((SolidColorBrush)DesktopVisual.Resource(view, "Settings.Brush.Selected")).Color, ((SolidColorBrush)selected.Background).Color);
                var pixels = await DesktopTestUi.Pixels(selected);
                True(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] > 0 &&
                    pixels[index * 4] < 150 && pixels[index * 4 + 1] < 150 && pixels[index * 4 + 2] < 150) > 30,
                    "non-first source rows retain their rendered labels in a local-coordinate snapshot");
                foreach (var pair in new[] { ("SearchPanels", "SongSearchPanel", "PlaylistSearchPanel"),
                    ("QualityPanels", "QualityPriorities", "SpatialPriorities"), ("MetadataPanels", "MetadataPriorities", "ArtworkPriorities") })
                {
                    var panels = (Grid)view.FindName(pair.Item1);
                    var left = (FrameworkElement)view.FindName(pair.Item2); var right = (FrameworkElement)view.FindName(pair.Item3);
                    bool stacked = panels.ActualWidth < 544;
                    Equal(stacked, Grid.GetRow(right) == 1); Equal(width == 600, stacked);
                    var a = new Rect(left.TransformToVisual(panels).TransformPoint(new Point()), new Size(left.ActualWidth, left.ActualHeight));
                    var b = new Rect(right.TransformToVisual(panels).TransformPoint(new Point()), new Size(right.ActualWidth, right.ActualHeight));
                    // Equal star columns distribute an odd physical pixel to one tile.
                    double dpi = panels.XamlRoot.RasterizationScale;
                    double availablePixels = Math.Round((panels.ActualWidth - 24) * dpi);
                    bool equalTiles = Math.Abs(Math.Min(a.Width, b.Width) * dpi - Math.Floor(availablePixels / 2)) < .01 &&
                        Math.Abs(Math.Max(a.Width, b.Width) * dpi - Math.Ceiling(availablePixels / 2)) < .01;
                    True(stacked ? b.Top >= a.Bottom && Math.Abs(a.Width - panels.ActualWidth) < .5 :
                        Math.Abs(b.Left - a.Right - 24) < .5 && equalTiles && Math.Abs(a.Top - b.Top) < .5 &&
                        Math.Abs(a.Left) < .5 && Math.Abs(b.Right - panels.ActualWidth) < .5,
                        pair.Item1 + " splits into two equal tiles and stacks only below its threshold" +
                        $"; width={width}; DPI=({panels.XamlRoot.RasterizationScale}, {panels.XamlRoot.RasterizationScale})" +
                        $"; panelWidth={panels.ActualWidth}; columnsActual=({panels.ColumnDefinitions[0].ActualWidth}, {panels.ColumnDefinitions[1].ActualWidth}, {panels.ColumnDefinitions[2].ActualWidth})" +
                        $"; left={a}; right={b}; IsStacked={Grid.GetRow(right) == 1}");
                }
            }
        });
        yield return Case("replica priority handle commands clamp first last rows and save once", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view);
            await Drain(); await Until(() => !f.Module.Busy);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListView)priority.FindName("PriorityItems");
            Thumb Grip(int index) => Visuals((ListViewItem)list.ContainerFromIndex(index)).OfType<Thumb>().Single();
            var first = Grip(0);
            True(!priority.CanMove(first, -1) && priority.CanMove(first, 1), "first handle can only move down");
            int writes = f.Calls.Count("music.quality.settings.save"); var source = f.Module.QualityCategories[0];
            priority.ProcessKey(first, VirtualKey.Up, VirtualKeyModifiers.Menu); Equal(writes, f.Calls.Count("music.quality.settings.save"));
            priority.ProcessKey(first, VirtualKey.Down, VirtualKeyModifiers.Menu); await Until(() => f.Module.CanEditQuality && f.Module.QualityCategories[1] == source);
            Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            var last = Grip(list.Items.Count - 1);
            True(priority.CanMove(last, -1) && !priority.CanMove(last, 1), "last handle can only move up");
            priority.ProcessKey(last, VirtualKey.Down, VirtualKeyModifiers.Menu); Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            priority.ProcessKey(last, VirtualKey.Up, VirtualKeyModifiers.Menu); await Until(() => f.Module.CanEditQuality);
            Equal(writes + 2, f.Calls.Count("music.quality.settings.save"));
            True(!f.Runtime.HasStarted, "keyboard ordering uses the native quality owner");
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
                var priority = (SettingsPriorityList)view.FindName(item.Item1); priority.StartBringIntoView(); await Drain(); view.UpdateLayout();
                var list = (ListView)priority.FindName("PriorityItems"); var scroll = priority.DragViewport;
                const int from = 2, to = 3;
                var grip = Visuals((ListViewItem)list.ContainerFromIndex(from)).OfType<Thumb>().Single();
                var original = list.Items.Cast<object>().ToArray(); var target = (ListViewItem)list.ContainerFromIndex(to);
                var above = (ListViewItem)list.ContainerFromIndex(from - 1);
                var drop = target.TransformToVisual(scroll).TransformPoint(new Point(60, target.ActualHeight - 2)); int writes = f.Calls.Count(item.Item2);
                priority.Drag.Begin(grip, list, grip.TransformToVisual(scroll).TransformPoint(new Point(18, 18))); priority.Drag.Update(drop);
                True(priority.IsDragging && priority.Drag.DestinationIndex == to, "drag tracks the lifted row centre");
                True(!priority.CanMove(grip, -1) && !priority.CanMove(grip, 1), "drag disables keyboard reordering");
                await Until(() => ((ListViewItem)list.ContainerFromIndex(from)).Opacity == 0);
                var shift = ((TransformGroup)target.RenderTransform).Children.OfType<TranslateTransform>().Single();
                await Until(() => Math.Abs(shift.Y + target.ActualHeight) < .1);
                True(above.RenderTransform is not TransformGroup && list.Items.Cast<object>().SequenceEqual(original),
                    "only the passed neighbour slides and data waits for release");
                var layer = (Canvas)priority.FindName("DragOverlay");
                Equal(1, layer.Children.Count);
                True(layer.Children[0] is Image { Source: not null }, "lifted row uses one actual native snapshot and no marker");
                layer.UpdateLayout(); await CaptureSettings(priority, "replica-drag-" + item.Item1 + ".png");
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
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); priority.StartBringIntoView(); await Drain(); view.UpdateLayout();
            var list = (ListView)priority.FindName("PriorityItems"); var scroll = priority.DragViewport;
            void Begin()
            {
                priority.UpdateLayout(); var grip = Visuals((ListViewItem)list.ContainerFromIndex(0)).OfType<Thumb>().Single();
                priority.Drag.Begin(grip, list, grip.TransformToVisual(scroll).TransformPoint(new Point(18, 18)));
                var row = (ListViewItem)list.ContainerFromIndex(2); priority.Drag.Update(row.TransformToVisual(scroll).TransformPoint(new Point(60, 2)));
                True(priority.IsDragging, "current row owns the gesture");
            }
            int writes = f.Calls.Count("music.quality.settings.save"); Begin(); priority.CompleteDrag(new Point(scroll.ActualWidth + 20, 100), false);
            True(!priority.IsDragging, "off-list final pointer rejects cached insertion");
            Begin(); priority.Visibility = Visibility.Collapsed; True(!priority.IsDragging, "hidden view retires gesture"); priority.Visibility = Visibility.Visible; view.UpdateLayout();
            Begin(); mount.Window.Resize(908, 820); DesktopTestUi.WindowRoot(mount.Window).UpdateLayout(); True(!priority.IsDragging, "resize retires old geometry");
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
            using var mount = new ReplicaMount(view); mount.Window.Resize(1248, 300);
            await Drain(); ReplicaAnchors(view)[2].IsChecked = true; await Drain(); view.UpdateLayout();
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListView)priority.FindName("PriorityItems");
            var scroll = priority.DragViewport; var source = (ListViewItem)list.ContainerFromIndex(0);
            var grip = Visuals(source).OfType<Thumb>().Single();
            var clippedDrop = view.TransformToVisual(scroll).TransformPoint(new Point(priority.TransformToVisual(view).TransformPoint(new Point(80, 0)).X, view.ActualHeight + 10));
            True(clippedDrop.Y > 0 && clippedDrop.Y < scroll.ActualHeight, "footer pointer is inside the unclipped inner list");
            int writes = f.Calls.Count("music.quality.settings.save");
            priority.Drag.Begin(grip, list, grip.TransformToVisual(scroll).TransformPoint(new Point(18, 18)));
            priority.Drag.Update(clippedDrop); priority.CompleteDrag(clippedDrop, false);
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!priority.IsDragging && source.Opacity == 1, "outer viewport clipping fences the final drop");
            list.SelectedIndex = 2;
            var toggle = Visuals(source).OfType<CheckBox>().Single();
            view.Visibility = Visibility.Collapsed; await Drain();
            priority.ProcessKey(grip, VirtualKey.Down, VirtualKeyModifiers.Menu); FilmUiTest.Click(toggle); await Drain();
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!priority.CanMove(grip, 1), "hidden controls cannot submit");
            view.Visibility = Visibility.Visible; await Drain();
            mount.Window.Content = null; await Drain();
            priority.ProcessKey(grip, VirtualKey.Down, VirtualKeyModifiers.Menu); FilmUiTest.Click(toggle); await Drain();
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!priority.CanMove(grip, 1), "unloaded controls cannot submit");
        });
        yield return Case("replica settings remain within shell content and retain the hidden global player", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await InstallSettingsSources(f, 6);
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask)
            {
            };
            try
            {
                shell.Show(); await Drain(); await Until(() => f.Module.CanEdit);
                var region = (Grid)DesktopTestUi.WindowRoot(shell).FindName("ContentRegion"); Equal(new Thickness(0), region.Margin);
                var player = ((ContentControl)DesktopTestUi.WindowRoot(shell).FindName("PlayerHost")).Content;
                True(player is not null, "ordinary page mounts the global player");
                DesktopTestUi.Invoke((Button)DesktopTestUi.WindowRoot(shell).FindName("SettingsButton"));
                await Drain(); await Until(() => f.Module.CanEdit);
                var root = (FrameworkElement)shell.Content;
                var header = (DesktopPageHeader)DesktopTestUi.WindowRoot(shell).FindName("PageHeaderHost");
                var heading = (TextBlock)header.FindName("Heading"); var slot = (ContentControl)header.FindName("LeadingSlot");
                var back = (Button)DesktopTestUi.WindowRoot(shell).FindName("BackButton");
                True(DesktopVisual.IsVisible(heading) && heading.Text == "设置" && DesktopVisual.IsVisible(slot) && region.Margin == new Thickness(0) &&
                    !DesktopVisual.IsVisible((FrameworkElement)DesktopTestUi.WindowRoot(shell).FindName("SearchInput")), "settings title and category sit in the header beside back");
                True(heading.TransformToVisual(root).TransformPoint(new Point()).X > back.TransformToVisual(root).TransformPoint(new Point(back.ActualWidth, 0)).X &&
                    slot.TransformToVisual(root).TransformPoint(new Point()).X > heading.TransformToVisual(root).TransformPoint(new Point(heading.ActualWidth, 0)).X,
                    "header order is back, settings, category");
                var areaTabs = (Panel)slot.Content; var area = areaTabs.Children.OfType<RadioButton>().ToArray(); Equal("设置类别", AutomationProperties.GetName(areaTabs));
                True(area.Select(tab => (string)tab.Content).SequenceEqual(new[] { "音乐" }) &&
                    area[0].IsChecked == true, "shared category tabs reflect the supplied modules");
                var settingsView = (MusicSettingsView)f.Module.SettingsView; var tabs = (Panel)settingsView.FindName("SettingsNavigation");
                True(area.All(tab => ReferenceEquals(tab.Style, DesktopVisual.Resource(DesktopTestUi.WindowRoot(shell), "MeowFilm.SettingsArea")) &&
                    tab.FontWeight == FontWeights.Normal), "header categories use the shared regular style");
                double tabsTop = tabs.TransformToVisual(root).TransformPoint(new Point()).Y, regionTop = region.TransformToVisual(root).TransformPoint(new Point()).Y;
                True(tabsTop - regionTop < 1, "settings content starts directly below the compact header");
                foreach (var size in new[] { new Size(1440, 900), new Size(1110, 869), new Size(860, 600) })
                {
                    // RDP's current monitor can clamp an offscreen HWND. A fixed test
                    // viewport exercises the requested size without resizing the desktop.
                    shell.Resize(size.Width, size.Height); DesktopTestUi.WindowRoot(shell).UpdateLayout(); await Drain(); root.UpdateLayout();
                    True(root.ActualWidth > 0 && root.ActualHeight > 0,
                        "the shell visual uses the actual requested screenshot viewport");
                    var sidebar = (ScrollViewer)DesktopTestUi.WindowRoot(shell).FindName("SidebarScroll"); var footer = (Border)DesktopTestUi.WindowRoot(shell).FindName("PlayerFrame");
                    Equal(Visibility.Visible, sidebar.Visibility); Equal(Visibility.Collapsed, footer.Visibility);
                    True(ReferenceEquals(player, ((ContentControl)DesktopTestUi.WindowRoot(shell).FindName("PlayerHost")).Content),
                        "settings retain the same global player instance");
                    var view = (MusicSettingsView)f.Module.SettingsView; var bounds = new Rect(view.TransformToVisual(root).TransformPoint(new Point()), new Size(view.ActualWidth, view.ActualHeight));
                    True(bounds.Left >= 203 && bounds.Bottom <= root.ActualHeight + 1,
                        "settings remain inside the shell while the player footer is hidden");
                    if (size.Width == 860)
                    {
                        var accountSection = (Border)view.FindName("AccountsSection");
                        var accountLabel = ((StackPanel)accountSection.Child).Children.OfType<Grid>().Single()
                            .Children.OfType<TextBlock>().Single(text => text.Text == "账号");
                        var accounts = (MusicAccountsView)view.FindName("AccountsPanel");
                        True(accounts.TransformToVisual(accountSection).TransformPoint(new Point()).Y >=
                            accountLabel.TransformToVisual(accountSection).TransformPoint(new Point(0, accountLabel.ActualHeight)).Y,
                            "the minimum shell does not waste a label column beside its account table");
                    }
                    ReplicaAnchors(view)[^1].IsChecked = true; await Drain(); root.UpdateLayout();
                    True(DesktopVisual.IsVisible((MusicSourcesSettingsView)view.FindName("SourcesPage")) &&
                        footer.Visibility == Visibility.Collapsed, "independent source management keeps the player hidden");
                    var sources = (MusicSourcesSettingsView)view.FindName("SourcesPage");
                    var management = (ManagementList)sources.FindName("SourceList");
                    var items = Visuals(management).OfType<ListView>().Single();
                    var body = Visuals(items).OfType<ScrollViewer>().First();
                    var firstRow = Visuals((DependencyObject)items.ContainerFromIndex(0))
                        .OfType<ManagementRowView>().Single();
                    var firstName = firstRow.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 2);
                    var nameHeader = Visuals(management).OfType<TextBlock>().Single(text => text.Text == "名称");
                    True(Math.Abs(firstName.TransformToVisual(sources).TransformPoint(new Point()).X -
                        nameHeader.TransformToVisual(sources).TransformPoint(new Point()).X) < 1,
                        $"table name alignment at {size.Width}: header={nameHeader.TransformToVisual(sources).TransformPoint(new Point()).X} " +
                        $"row={firstName.TransformToVisual(sources).TransformPoint(new Point()).X}; headerWidth={management.ActualWidth} " +
                        $"viewport={body.ViewportWidth} extent={body.ExtentWidth} rowWidth={firstRow.ActualWidth}");
                    if (size.Width == 860)
                    {
                        // ManagementList scrolls in logical rows; measure the rendered viewport in DIP.
                        var viewport = Visuals(body).OfType<ScrollContentPresenter>().Single();
                        True(viewport.ActualHeight >= 82 && body.ScrollableHeight > 0,
                            "the minimum shell retains a visible source row and its internal scrollbar");
                    }
                    await CaptureSettings(root, "replica-shell-source-" + (int)size.Width + ".png");
                    body.ChangeView(body.ScrollableWidth, null, null, true); await Drain(); root.UpdateLayout();
                    True(body.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && body.ScrollableWidth == 0,
                        "source columns fit the management card without horizontal scrolling");
                    var remove = Visuals(sources).OfType<Button>().First(button => Equals(button.Content, "删除"));
                    var removeBounds = new Rect(remove.TransformToVisual(sources).TransformPoint(new Point()), new Size(remove.ActualWidth, remove.ActualHeight));
                    True(removeBounds.Left >= 0 && removeBounds.Right <= sources.ActualWidth,
                        "the four fixed row actions are reachable at every supported window width");
                    await CaptureSettings(root, "replica-shell-source-actions-" + (int)size.Width + ".png");
                    body.ChangeView(0, null, null, true); await Drain();
                    ReplicaAnchors(view)[0].IsChecked = true; await Drain(); root.UpdateLayout();
                    await CaptureSettings(root, "replica-shell-accounts-" + (int)size.Width + ".png");
                    True(!f.Runtime.Calls.Any(method => method is "music.script.invoke" or "music.script.resolve"),
                        "source management reads the shared catalog without invoking scripts");
                }
                var content = (FrameworkElement)settingsView.FindName("MusicContent");
                True(DesktopVisual.IsVisible(content), "music content remains owned by the visible module settings");
                FilmUiTest.Click(back); await Drain(); await Until(() => !DesktopVisual.IsVisible(heading));
                True(region.Margin == new Thickness(0) && !DesktopVisual.IsVisible(slot) && slot.Content is null,
                    "home keeps the compact shared content gap");
                Equal(Visibility.Visible, ((Border)DesktopTestUi.WindowRoot(shell).FindName("PlayerFrame")).Visibility);
                True(ReferenceEquals(player, ((ContentControl)DesktopTestUi.WindowRoot(shell).FindName("PlayerHost")).Content),
                    "home restores the same global player instance");
            }
            finally { shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5)); await Drain(); }
        });
        yield return Case("replica search advanced bubble saves each kind and only source management keeps wheel input", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            await InstallSettingsSources(f, 6);
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view); mount.Window.Resize(1248, 560);
            await Drain(); await Until(() => !f.Module.Busy); view.UpdateLayout();
            True(!Visuals(view).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box).Contains("搜索模式")),
                "search mode controls are removed");
            var popup = (ContentDialog)view.FindName("SearchAdvancedPopup");
            foreach (var (kind, label, pages, concurrency) in new[]
            {
                ("songs", "歌曲搜索高级设置", 11, 12),
                ("playlists", "歌单搜索高级设置", 2, 0)
            })
            {
                var button = Visuals(view).OfType<Button>().Single(b => AutomationProperties.GetName(b) == label);
                FilmUiTest.Click(button); await Drain();
                True(MusicTestUi.IsDialogOpen(popup) && ReferenceEquals(popup.XamlRoot, view.XamlRoot), "advanced settings open as a window-centered dialog");
                ((TextBox)view.FindName("SearchPagesBox")).Text = pages.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ((TextBox)view.FindName("SearchConcurrencyBox")).Text = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);
                popup.Hide(); await Drain(); await Until(() => !f.Module.Busy && f.Module.CanEditSearchPreferences);
                var saved = kind == "songs" ? f.Module.SongSearch : f.Module.PlaylistSearch;
                True(saved.Pages == pages && saved.Concurrency == Math.Max(1, concurrency) && saved.CacheMinutes == 60,
                    "positive page and concurrency values are saved; invalid fields are ignored");
                view.UpdateLayout();
                True(DesktopVisual.IsVisible((FrameworkElement)view.FindName("SongPriorities")) && DesktopVisual.IsVisible((FrameworkElement)view.FindName("PlaylistPriorities")),
                    "platform lists remain visible");
            }
            var settings = (await f.Rpc("music.search.settings.get")).GetProperty("settings");
            True(settings.GetProperty("songs").GetProperty("pages").GetInt32() == 11 &&
                settings.GetProperty("songs").GetProperty("concurrency").GetInt32() == 12 &&
                settings.GetProperty("playlists").GetProperty("pages").GetInt32() == 2 &&
                settings.GetProperty("playlists").GetProperty("concurrency").GetInt32() == 1,
                "advanced settings persisted per kind");
            var scroll = (ScrollViewer)view.FindName("SettingsScroller");
            double before = scroll.VerticalOffset;
            var scrollPeer = new Microsoft.UI.Xaml.Automation.Peers.ScrollViewerAutomationPeer(scroll);
            var scrolling = (Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)scrollPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll);
            scrolling.SetScrollPercent(-1, 0); await Drain();
            scrolling.Scroll(Microsoft.UI.Xaml.Automation.ScrollAmount.NoAmount, Microsoft.UI.Xaml.Automation.ScrollAmount.LargeIncrement);
            await Drain(); True(scroll.VerticalOffset > 0, "the page's native scroll action reaches ordinary settings");
            var picker = Visuals(view).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "起播目标音质");
            double offset = scroll.VerticalOffset;
            picker.IsDropDownOpen = true; await Drain(); picker.IsDropDownOpen = false; await Drain();
            Equal(offset, scroll.VerticalOffset);
            ReplicaAnchors(view)[^1].IsChecked = true; await Drain();
            var sources = Visuals((ManagementList)((MusicSourcesSettingsView)view.FindName("SourcesPage")).FindName("SourceList")).OfType<ListView>().Single();
            var body = Visuals(sources).OfType<ScrollViewer>().First();
            True(body.ScrollableHeight > 0, "source management overflows its own list");
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ScrollViewerAutomationPeer(body);
            var sourceScroll = (Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll);
            sourceScroll.Scroll(Microsoft.UI.Xaml.Automation.ScrollAmount.NoAmount, Microsoft.UI.Xaml.Automation.ScrollAmount.LargeIncrement);
            await Drain(); True(body.VerticalOffset > 0 && scroll.VerticalOffset == offset, "source scrolling does not move the independent ordinary page");
        });
        yield return Case("replica priority switch changes enabled only and preserves exact saved order", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListView)priority.FindName("PriorityItems");
            var row = (ListViewItem)list.ContainerFromIndex(0); var toggle = Visuals(row).OfType<CheckBox>().Single();
            var order = f.Module.QualityCategories.Select(value => value.Key).ToArray(); bool next = !f.Module.QualityCategories[0].Enabled;
            int writes = f.Calls.Count("music.quality.settings.save"); FilmUiTest.Click(toggle);
            await Until(() => f.Module.CanEditQuality); Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            True(order.SequenceEqual(f.Module.QualityCategories.Select(value => value.Key)) && f.Module.QualityCategories[0].Enabled == next && !priority.IsDragging, "enable is not an ordering gesture");
        });
        yield return Case("replica narrow settings separate labels controls and stacked appearance fields", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var viewMount = new ReplicaMount(view); await Drain();
            await RenderSettings(view, 600, 760); await Drain(); view.UpdateLayout();
            var cacheView = f.Module.CacheSettingsView;
            using var cacheMount = new ReplicaMount(cacheView); await Drain();
            await RenderSettings(cacheView, 600, 240); await Drain();
            var cache = (Border)cacheView.FindName("CacheSection");
            var label = Visuals(cache).OfType<TextBlock>().Single(text => text.Text == "播放缓存");
            var clear = Visuals(cache).OfType<Button>().Single(button => Equals(button.Content, "清空播放缓存"));
            True(clear.TransformToVisual(cache).TransformPoint(new Point()).Y >= label.TransformToVisual(cache).TransformPoint(new Point(0, label.ActualHeight)).Y,
                "narrow cache control is below its section label");
            var appearance = f.Module.AppearanceSettingsView;
            using var appearanceMount = new ReplicaMount(appearance); await Drain();
            await RenderSettings(appearance, 600, 700); await Drain();
            var pickers = Visuals(appearance).OfType<ComboBox>().ToArray(); Equal(3, pickers.Length);
            var mode = pickers.Single(picker => AutomationProperties.GetName(picker) == "播放器配色");
            var color = pickers.Single(picker => AutomationProperties.GetName(picker) == "播放器固定配色");
            var playlist = pickers.Single(picker => AutomationProperties.GetName(picker) == "歌单样式");
            True(color.TransformToVisual(appearance).TransformPoint(new Point()).Y >
                mode.TransformToVisual(appearance).TransformPoint(new Point(0, mode.ActualHeight)).Y + 16 &&
                playlist.TransformToVisual(appearance).TransformPoint(new Point()).Y > color.TransformToVisual(appearance).TransformPoint(new Point(0, color.ActualHeight)).Y + 16,
                "mode, preset dropdown and playlist retain separate labeled rows at narrow width");
        });
        yield return Case("native settings navigation visits actual anchors and retains focus", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            var anchors = ReplicaAnchors(view); True(anchors[0].Focus(FocusState.Programmatic), "anchor accepts actual focus");
            foreach (var (key, index) in new[] { (VirtualKey.End, 4), (VirtualKey.Home, 0), (VirtualKey.Right, 1), (VirtualKey.Left, 0) })
            {
                True(view.NavigateAnchor((RadioButton)DesktopVisual.Focused(view)!, key), "production semantic navigation handles the requested key");
                await Drain();
                True(anchors[index].IsChecked == true && ReferenceEquals(DesktopVisual.Focused(view), anchors[index]), "navigation selects and focuses the actual destination");
            }
        });
        yield return Case("general data source and cache areas retain their owners without opening music settings", async () =>
        {
            await using var f = await Fixture.Create(withPlaybackCache: true);
            await using var appearance = new MeowFilm.AppHost.AppAppearance(f.Store);
            var general = new GeneralSettingsView(appearance, [f.Module]);
            using var mount = new ReplicaMount(general); await Drain(); await Until(() => !f.Module.Busy);
            var tabs = ((Panel)general.FindName("Areas")).Children.OfType<RadioButton>().ToArray();
            True(tabs.Select(tab => (string)tab.Content).SequenceEqual(new[] { "外观", "首页数据源", "缓存控制" }), "general areas match their contents");
            int qualityReads = f.Calls.Count("music.quality.settings.get");
            int sourceReads = f.Calls.Count("music.catalogSource.settings.get");
            tabs[1].IsChecked = true; await Until(() => f.Module.CanEditCatalogSource);
            True(DesktopVisual.IsVisible(f.Module.HomeSourceSettingsView) && !DesktopVisual.IsVisible(f.Module.AppearanceSettingsView), "source controls only mount in their own area");
            Equal(sourceReads + 1, f.Calls.Count("music.catalogSource.settings.get"));
            tabs[2].IsChecked = true; await Drain();
            var cache = f.Module.CacheSettingsView;
            var clear = Visuals(cache).OfType<Button>().Single(button => Equals(button.Content, "清空播放缓存"));
            True(clear.IsEnabled && DesktopVisual.IsVisible(cache) && !DesktopVisual.IsVisible(f.Module.HomeSourceSettingsView), "cache control belongs to General cache area");
            f.Dialogs.Confirmation = false;
            FilmUiTest.Click(clear); await Until(() => !f.Module.Busy);
            True(f.Cache.ReadMemory(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace).Data is null, "cancelled confirmation does not create cache metadata");
            f.Dialogs.Confirmation = true;
            FilmUiTest.Click(clear); await Until(() => !f.Module.Busy);
            True(f.Cache.ReadMemory(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace).Data is not null, "confirmed action reaches the actual isolated cache owner");
            Equal("播放缓存文件已清理（当前播放不中断）", f.Module.Status);
            Equal(qualityReads, f.Calls.Count("music.quality.settings.get"));
            True(!f.Runtime.HasStarted && !f.Module.SettingsView.IsLoaded, "General does not activate music accounts sources or scripts");
            foreach (int width in new[] { 900, 600 })
            {
                mount.Window.Resize(width, 820); DesktopTestUi.WindowRoot(mount.Window).UpdateLayout(); await Drain();
                foreach (int area in new[] { 1, 2 })
                {
                    tabs[area].IsChecked = true; await Drain();
                    await CaptureSettings(general, $"general-area-{area}-{width}.png");
                }
            }
        });
        yield return Case("settings switches preserve on color disabled binding and row separators", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            foreach (string name in new[] { "SongPriorities", "PlaylistPriorities", "QualityPriorities", "SpatialPriorities", "MetadataPriorities", "ArtworkPriorities" })
            {
                var priority = (SettingsPriorityList)view.FindName(name); priority.StartBringIntoView(); await Drain();
                var list = (ListView)priority.FindName("PriorityItems");
                var row = (ListViewItem)list.ContainerFromIndex(1);
                var toggle = Visuals(row).OfType<CheckBox>().Single(); var grip = Visuals(row).OfType<Thumb>().Single();
                True(DesktopTestUi.Bounds(grip, row).Left >= DesktopTestUi.Bounds(toggle, row).Right, "grip follows the independent switch");
                var track = Visuals(toggle).OfType<Border>().Single(border => border.Name == "Track");
                bool enabled = toggle.IsChecked == true;
                var color = ((SolidColorBrush)track.Background).Color;
                if (enabled && !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                    Equal(Color.FromArgb(255, 34, 197, 94), color);
                row.IsEnabled = false; await Drain();
                Equal(color, ((SolidColorBrush)track.Background).Color);
            row.ClearValue(Control.IsEnabledProperty); await Drain();
                Equal(enabled, toggle.IsChecked == true);
                True(Visuals(row).OfType<Border>().Any(border => border.BorderThickness.Bottom == 1), "priority rows retain a separator");
            }
            await CaptureSettings(view, "settings-states.png");
        });
        yield return Case("replica screenshot states use real owner data and official resources without remote reads", async () =>
        {
            await using var f = await DetailsScene.Create(); await f.Module.ActivateSettingsAsync(); var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            foreach (int width in new[] { 1248, 908, 600 })
            {
                mount.Window.Resize(width, 820); DesktopTestUi.WindowRoot(mount.Window).UpdateLayout(); await Drain();
                foreach (var target in new[] { (0, "accounts"), (1, "search"), (2, "quality"), (3, "metadata") })
                {
                    ReplicaAnchors(view)[target.Item1].IsChecked = true; await Drain(); view.UpdateLayout();
                    await CaptureSettings(view, "replica-" + target.Item2 + "-" + width + ".png");
                }
            }
            Equal(0, f.Peer.Requests);
            True(f.Runtime.Calls.All(call => call.Method.StartsWith("music.platform.", StringComparison.Ordinal)), "no script IO");
        });
    }
    private static RadioButton[] ReplicaAnchors(MusicSettingsView view) => ((Panel)view.FindName("SettingsNavigation")).Children.OfType<RadioButton>().ToArray();
    private sealed class ReplicaMount : IDisposable
    {
        internal Window Window { get; }
        internal ReplicaMount(FrameworkElement view)
        {
            Window = FilmUiTest.Window(view, 1248, 820);
            Window.Activate();
        }
        public void Dispose() { Window.Content = null; Window.Close(); }
    }
    private static async Task CaptureSettings(FrameworkElement view, string name) =>
        await GeneralAppearanceTests.SavePreview(view, Path.GetFileNameWithoutExtension(name),
            Math.Max(1, (int)Math.Ceiling(view.ActualWidth)), Math.Max(1, (int)Math.Ceiling(view.ActualHeight)));
    private static async Task RenderSettings(FrameworkElement view, int width, int height)
    {
        True(view.XamlRoot is not null, "native rendering requires a mounted shared root");
        view.Width = width; view.Height = height;
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        await Drain();
        var pixels = await DesktopTestUi.Pixels(view, width, height);
        True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 1000, "native render contains actual controls and text");
    }
    private static Brush SystemSettingsBrush(global::Windows.UI.ViewManagement.UIElementType type) =>
        new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(type));
}
