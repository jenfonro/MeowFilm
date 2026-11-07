using System.IO;
using System.Text.Json;
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
        yield return Case("wrong source dialog does not reopen from a retired request after returning to settings", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.queue.replace", new { songs = new[] { MusicSongFixture.Online("wy", "LateForm") }, index = 0 });
            await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            ReplicaAnchors(view)[2].IsChecked = true; await Drain();
            var button = (Button)view.FindName("ExclusionAddButton");
            var popup = (Popup)view.FindName("ExclusionPopup");
            var queue = (await f.Rpc("music.queue.get", new { offset = 0, limit = 100 })).GetRawText();
            int opened = 0;
            popup.Opened += (_, _) => opened++;
            foreach (string retirement in new[] { "hidden", "unloaded", "sources" })
            {
                var pending = f.Calls.HoldNext("music.queue.get");
                Click(button); await pending.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    True(!popup.IsOpen, "the held form read has not opened a dialog");
                    switch (retirement)
                    {
                        case "hidden":
                            view.Visibility = Visibility.Collapsed; await Drain();
                            True(!button.IsVisible, "hiding settings retires the form entry");
                            view.Visibility = Visibility.Visible;
                            break;
                        case "unloaded":
                            mount.Window.Content = null; await Drain();
                            True(!button.IsLoaded, "unloading settings retires the form entry");
                            mount.Window.Content = view;
                            break;
                        default:
                            ReplicaAnchors(view)[^1].IsChecked = true; await Drain();
                            True(view.IsVisible && !button.IsVisible, "source management retires the form without hiding the outer view");
                            ReplicaAnchors(view)[2].IsChecked = true;
                            break;
                    }
                    await Drain();
                    True(button.IsLoaded && button.IsVisible, "the original settings entry is visible again before the late reply");
                }
                finally { pending.Release(); }
                await Until(() => !f.Module.Busy);
                True(!popup.IsOpen && opened == 0, "a retired " + retirement + " request must not reopen the dialog");
                Equal(0, f.Calls.Count("music.sourceExclusions.add"));
                Equal(queue, (await f.Rpc("music.queue.get", new { offset = 0, limit = 100 })).GetRawText());
            }

            Click(button); await Until(() => popup.IsOpen);
            Equal(1, opened);
            Click(Visuals(popup.Child).OfType<Button>().Single(control => Equals(control.Content, "取消")));
            await Drain();
            True(!popup.IsOpen, "a fresh form still cancels normally");
            Equal(0, f.Calls.Count("music.sourceExclusions.add"));
            Click(button); await Until(() => popup.IsOpen);
            var song = (ComboBox)view.FindName("ExclusionSong");
            var provider = (ComboBox)view.FindName("ExclusionProvider");
            var platform = (ComboBox)view.FindName("ExclusionPlatform");
            song.SelectedIndex = 0;
            provider.SelectedItem = provider.Items.Cast<MusicExclusionOption>().Single(option => option.Key == "official");
            platform.SelectedItem = platform.Items.Cast<MusicExclusionOption>().Single(option => option.Key == "wy");
            string songId = ((MusicExclusionOption)song.SelectedItem).Key;
            Click(Visuals(popup.Child).OfType<Button>().Single(control => Equals(control.Content, "添加")));
            await Until(() => !f.Module.Busy && f.Module.SourceExclusions.Count == 1);
            True(!popup.IsOpen && opened == 2, "a fresh confirmed form closes after a single add");
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
                mount.Window.Width = width; mount.Window.UpdateLayout(); await Drain();
                var section = (Border)view.FindName("ExclusionSection");
                var heading = (FrameworkElement)((Button)view.FindName("ExclusionAddButton")).Parent;
                var card = (Border)viewport.Parent;
                double headingBottom = heading.TranslatePoint(new Point(0, heading.ActualHeight), section).Y;
                double cardTop = card.TranslatePoint(new Point(), section).Y;
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
            scroll.ScrollToEnd(); view.UpdateLayout(); await Drain();
            True(scroll.VerticalOffset > 0 && list.ItemContainerGenerator.ContainerFromIndex(29) is not null,
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
            var popup = (Popup)view.FindName("ExclusionPopup");
            await Until(() => popup.IsOpen); await Drain();
            var song = (ComboBox)view.FindName("ExclusionSong");
            song.SelectedIndex = 0; await Drain();
            await CheckContent(song, item =>
            {
                var option = (MusicExclusionOption)item;
                return option.Name + "  " + option.Detail;
            });
            popup.IsOpen = false;

            async Task CheckContent(ComboBox picker, Func<object, string> label)
            {
                object selected = picker.SelectedItem;
                DesktopTheme.SetIsHighContrast(view, false);
                picker.IsDropDownOpen = true; await Drain(); picker.UpdateLayout();
                var items = picker.Items.Cast<object>().Select((item, index) =>
                    ((ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(index), label(item))).ToArray();
                foreach (var (container, expected) in items)
                {
                    var text = Visuals(container).OfType<TextBlock>().Single();
                    Equal(expected, new TextRange(text.ContentStart, text.ContentEnd).Text);
                    Equal(((SolidColorBrush)picker.FindResource("MeowFilm.Brush.Text")).Color, ((SolidColorBrush)text.Foreground).Color);
                }
                DesktopTheme.SetIsHighContrast(view, true); await Drain();
                var current = items[0].Item1;
                var actualText = Visuals(current).OfType<TextBlock>().Single();
                SimulateFocus(current, UIElement.IsMouseOverProperty, true); await Drain();
                Equal(SystemColors.HighlightColor, ((SolidColorBrush)current.Background).Color);
                Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)actualText.Foreground).Color);
                SimulateFocus(current, UIElement.IsMouseOverProperty, false);
                picker.IsDropDownOpen = false; await Drain();
                var closedText = Visuals(picker).OfType<TextBlock>().Single();
                Equal(label(selected), new TextRange(closedText.ContentStart, closedText.ContentEnd).Text);
                Equal(SystemColors.WindowTextColor, ((SolidColorBrush)closedText.Foreground).Color);
                picker.SetCurrentValue(UIElement.IsEnabledProperty, false); await Drain();
                Equal(SystemColors.GrayTextColor, ((SolidColorBrush)closedText.Foreground).Color);
                DesktopTheme.SetIsHighContrast(view, false); await Drain();
                Equal(((SolidColorBrush)picker.FindResource("MeowFilm.Brush.Disabled")).Color, ((SolidColorBrush)closedText.Foreground).Color);
                Equal(label(selected), new TextRange(closedText.ContentStart, closedText.ContentEnd).Text); Equal(selected, picker.SelectedItem);
                picker.SetCurrentValue(UIElement.IsEnabledProperty, true);
                picker.GetBindingExpression(UIElement.IsEnabledProperty)?.UpdateTarget();
            }
        });
        yield return Case("shared settings layout follows container breakpoints and retires resize on unload or disable", async () =>
        {
            var sections = new Grid { Width = 1100, Height = 80, HorizontalAlignment = HorizontalAlignment.Left };
            var sectionChild = new Border(); sections.Children.Add(sectionChild);
            var pair = new Grid { Width = 544, Height = 80, HorizontalAlignment = HorizontalAlignment.Left };
            var pairChild = new Border(); pair.Children.Add(pairChild);
            var host = new StackPanel(); host.Children.Add(sections); host.Children.Add(pair);
            SettingsLayout.SetMode(sections, SettingsLayoutMode.Sections);
            SettingsLayout.SetMode(pair, SettingsLayoutMode.Pair);
            using var mount = new ReplicaMount(host);
            void CheckLayout(bool narrow, bool stacked)
            {
                Equal(narrow, SettingsLayout.GetIsNarrow(sections));
                Equal(narrow, SettingsLayout.GetIsNarrow(sectionChild));
                Equal(stacked, SettingsLayout.GetIsStacked(pair));
                Equal(stacked, SettingsLayout.GetIsStacked(pairChild));
            }
            await Drain(); mount.Window.UpdateLayout(); CheckLayout(false, false);
            foreach (var size in new[]
            {
                (Sections: 1099d, Pair: 543d, Narrow: false, Stacked: true),
                (Sections: 640d, Pair: 544d, Narrow: false, Stacked: false),
                (Sections: 639d, Pair: 545d, Narrow: true, Stacked: false),
                (Sections: 1100d, Pair: 544d, Narrow: false, Stacked: false)
            })
            {
                sections.Width = size.Sections; pair.Width = size.Pair;
                mount.Window.UpdateLayout(); await Drain();
                Equal(size.Sections, sections.ActualWidth); Equal(size.Pair, pair.ActualWidth);
                CheckLayout(size.Narrow, size.Stacked);
            }
            for (int cycle = 0; cycle < 2; cycle++)
            {
                mount.Window.Content = null; await Drain();
                True(!sections.IsLoaded && !pair.IsLoaded, "both layout containers are actually unloaded");
                var unmounted = new Border { Child = host };
                sections.Width = 500; pair.Width = 400;
                unmounted.Measure(new Size(1248, 820)); unmounted.Arrange(new Rect(0, 0, 1248, 820)); unmounted.UpdateLayout(); await Drain();
                Equal(500d, sections.ActualWidth); Equal(400d, pair.ActualWidth);
                CheckLayout(false, false);
                True(!sections.IsLoaded && !pair.IsLoaded, "measuring detached content does not reactivate the behavior");
                unmounted.Child = null;
                mount.Window.Content = host; mount.Window.UpdateLayout(); await Drain();
                True(sections.IsLoaded && pair.IsLoaded, "the same layout containers are reloaded");
                CheckLayout(true, true);
                sections.Width = 1100; pair.Width = 544;
                mount.Window.UpdateLayout(); await Drain(); CheckLayout(false, false);
            }
            sections.Width = 500; pair.Width = 400;
            mount.Window.UpdateLayout(); await Drain(); CheckLayout(true, true);
            SettingsLayout.SetMode(sections, SettingsLayoutMode.None);
            SettingsLayout.SetMode(pair, SettingsLayoutMode.None);
            CheckLayout(false, false);
            sections.Width = 501; pair.Width = 401;
            mount.Window.UpdateLayout(); await Drain(); CheckLayout(false, false);
            SettingsLayout.SetMode(sections, SettingsLayoutMode.Sections);
            SettingsLayout.SetMode(pair, SettingsLayoutMode.Pair);
            CheckLayout(true, true);
        });
        yield return Case("replica catalog source saves only its authority and preserves search audio sources queue and media", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();

            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView; using var mount = new ReplicaMount(view);
            await Until(() => f.Module.CanEditCatalogSource);
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            Equal("音乐数据源", AutomationProperties.GetName(picker));
            True(ReferenceEquals(picker.Style, view.FindResource("Settings.ComboBox")), "source picker reuses the established control style");
            True(picker.Items.Cast<MusicCatalogChoice>().Select(item => (item.Key, item.Name))
                .SequenceEqual(new[] { ("wy", "网易云音乐"), ("kg", "酷狗音乐") }), "only supported discovery sources are offered");
            Equal("wy", picker.SelectedValue); Equal(0, f.Calls.Count("music.catalogSource.settings.save"));
            var search = (await f.Rpc("music.search.settings.get")).GetProperty("settings").GetRawText();
            var audio = (await f.Rpc("music.officialSources.settings.get")).GetProperty("settings").GetRawText();
            var online = (await f.Rpc("music.settings.get")).GetProperty("settings").GetRawText();
            var queue = await f.Rpc("music.queue.get");
            var identity = f.Playback.Identity; int opens = f.Playback.Opens.Count;
            var hold = f.Calls.HoldNext("music.catalogSource.settings.save");
            True(picker.Focusable && picker.Focus(), "the source selector accepts native keyboard focus");
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Down)
                { RoutedEvent = Keyboard.KeyDownEvent };
            picker.RaiseEvent(input); await hold.Arrived.Task; await Drain();
            True(input.Handled, "native ComboBox keyboard selection owns the source change");
            Equal("wy", f.Module.CatalogSourcePlatform);
            True(!picker.IsEnabled, "an in-flight receipt cannot admit another source save");
            hold.Release(); await Until(() => f.Module.CanEditCatalogSource && Equals(picker.SelectedValue, "kg"));
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
        yield return Case("general source hiding and shutdown drain an admitted save before accepting its receipt", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => f.Module.CanEditCatalogSource);
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            var held = f.Calls.HoldNext("music.catalogSource.settings.save");
            picker.SetCurrentValue(ComboBox.SelectedValueProperty, "kg"); await held.Arrived.Task;
            view.Visibility = Visibility.Collapsed; await Drain();
            True(!f.Module.CanEditCatalogSource, "hidden source still owns its admitted write until actual completion");
            var shutdown = f.Module.PrepareShutdownAsync(); await Drain();
            True(!shutdown.IsCompleted, "App close waits for the same source owner outside music settings");
            held.Release(); await shutdown;
            Equal("kg", f.Module.CatalogSourcePlatform);
            Equal(1, f.Calls.Count("music.catalogSource.settings.save"));
            var settings = await f.Store.ReadAsync("music.library.v1");
            True(settings.Data is not null, "the confirmed source receipt is durable");
        });
        yield return Case("replica catalog source rejected and unknown saves use notices rollback and authoritative readback", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicHomeSourceSettingsView)f.Module.HomeSourceSettingsView; using var mount = new ReplicaMount(view);
            var notices = new List<string>();
            mount.Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, notice) => notices.Add(notice.Message)));
            await Until(() => f.Module.CanEditCatalogSource);
            var picker = (ComboBox)view.FindName("CatalogSourcePicker");
            f.Calls.FailNext("music.catalogSource.settings.save", "STORAGE_UNAVAILABLE");
            picker.SetCurrentValue(ComboBox.SelectedValueProperty, "kg");
            await Until(() => f.Module.CanEditCatalogSource && f.Module.HasCatalogSourceError && Equals(picker.SelectedValue, "wy"));
            Equal("wy", f.Module.CatalogSourcePlatform);
            True(notices.Contains("数据源设置保存失败"), "save rejection is published through the existing notice outlet");
            var saved = await f.Rpc("music.catalogSource.settings.get");
            Equal("wy", saved.GetProperty("settings").GetProperty("platform").GetString());
            f.Calls.TransformNext("music.catalogSource.settings.save", _ => JsonSerializer.SerializeToElement(new { ok = false, code = "COMMIT_UNKNOWN" }));
            int reads = f.Calls.Count("music.catalogSource.settings.get");
            picker.SetCurrentValue(ComboBox.SelectedValueProperty, "kg");
            await Until(() => f.Module.CanEditCatalogSource && f.Module.CatalogSourcePlatform == "kg" && Equals(picker.SelectedValue, "kg"));
            Equal(reads + 1, f.Calls.Count("music.catalogSource.settings.get"));
            True(!f.Module.HasCatalogSourceError, "unknown receipt is successful only after a matching authoritative read");
            Equal(2, f.Calls.Count("music.catalogSource.settings.save"));
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
            True(!picker.IsEnabled && retry.IsVisible && retry.IsEnabled, "a failed restore requires a visible explicit retry");
            foreach (int width in new[] { 1248, 908, 600 })
            {
                mount.Window.Width = width; mount.Window.UpdateLayout(); await Drain();
                var section = (Border)view.FindName("HomeSourceSection");
                var label = Visuals(section).OfType<TextBlock>().Single(text => text.Text == "音乐数据源");
                var text = new Rect(label.TranslatePoint(new Point(), section), label.RenderSize);
                var choice = new Rect(picker.TranslatePoint(new Point(), section), picker.RenderSize);
                var action = new Rect(retry.TranslatePoint(new Point(), section), retry.RenderSize);
                True(text.Bottom <= choice.Top && choice.Right <= action.Left && action.Right <= section.ActualWidth,
                    "source label picker and retry remain separate and within the general section");
            }
            int reads = f.Calls.Count("music.catalogSource.settings.get");
            retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => picker.IsEnabled && !retry.IsVisible);
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
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                shell.Show(); await Drain();
                ((Button)shell.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Drain();
                var header = (DesktopPageHeader)shell.FindName("PageHeaderHost");
                var slot = (ContentControl)header.FindName("LeadingSlot");
                var areas = ((Panel)slot.Content).Children.OfType<RadioButton>().ToArray();
                areas[0].IsChecked = true; await Drain();
                var general = Visuals(shell).OfType<GeneralSettingsView>().Single();
                ((Panel)general.FindName("Areas")).Children.OfType<RadioButton>().Single(tab => Equals(tab.Tag, "sources")).IsChecked = true;
                await Drain(); await Until(() => view.IsVisible && f.Module.CanEditCatalogSource);
                var picker = (ComboBox)view.FindName("CatalogSourcePicker");
                int saves = f.Calls.Count("music.catalogSource.settings.save");
                view.Visibility = Visibility.Collapsed; await Drain();
                picker.SetCurrentValue(ComboBox.SelectedValueProperty, "kg"); await Drain();
                Equal(saves, f.Calls.Count("music.catalogSource.settings.save"));
                view.Visibility = Visibility.Visible; await Drain();
                picker.GetBindingExpression(ComboBox.SelectedValueProperty)!.UpdateTarget();
                areas[2].IsChecked = true; await Drain();
                True(film.SettingsView.IsVisible && !view.IsVisible, "the shared shell owns nonmusic settings navigation");
                picker.SetCurrentValue(ComboBox.SelectedValueProperty, "kg"); await Drain();
                Equal(saves, f.Calls.Count("music.catalogSource.settings.save"));
                areas[0].IsChecked = true; await Drain();
                await Until(() => view.IsVisible && f.Module.CanEditCatalogSource);
                picker.GetBindingExpression(ComboBox.SelectedValueProperty)!.UpdateTarget();
                ((Panel)general.FindName("HomeSourceContributions")).Children.Remove(view);
                await Drain(); True(!view.IsLoaded, "the general source controls are actually unloaded");
                picker.SetCurrentValue(ComboBox.SelectedValueProperty, "kg"); await Drain();
                Equal(saves, f.Calls.Count("music.catalogSource.settings.save"));
                Equal("wy", f.Module.CatalogSourcePlatform);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
        yield return Case("replica settings keep music anchors and continuous groups with an independent source page", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            Render(view, 1200, 760); await Drain();
            foreach (var surface in new Control[] { view, (MusicSourcesSettingsView)view.FindName("SourcesPage") })
            {
                Equal(SystemFonts.MessageFontFamily, surface.FontFamily);
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(surface));
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(surface));
                True(surface.UseLayoutRounding && surface.SnapsToDevicePixels, "settings surfaces preserve pixel-aligned system typography");
            }
            var settingsText = Visuals(view).OfType<TextBlock>().Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            True(settingsText.Length > 0 && settingsText.All(text =>
                TextOptions.GetTextFormattingMode(text) == TextFormattingMode.Display &&
                TextOptions.GetTextRenderingMode(text) == TextRenderingMode.Auto),
                "actual settings labels and values use Display and Auto");
            var anchors = ReplicaAnchors(view);
            True(anchors.Select(anchor => (string)anchor.Content).SequenceEqual(new[] { "账号与平台", "搜索", "音质", "歌词与封面", "音源" }), "latest v2 section names");
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
            True(f.Calls.Log.Skip(calls).All(call => call.Method is "music.officialSources.settings.get" or
                "music.quality.settings.get" or "music.sources.list"),
                "source navigation reads its native settings and installations without writing");
            True(!f.Runtime.HasStarted, "reference navigation never starts script execution");
        });
        yield return Case("replica priority lists preserve 52 pixel rows unnumbered 36 pixel grips and paired panels", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view);
            mount.Window.ResizeMode = ResizeMode.NoResize;
            foreach (int width in new[] { 1248, 1164, 1163, 908, 704, 703, 600, 1248 })
            {
                // Keep breakpoint inputs independent of native offscreen window constraints.
                view.Width = width;
                mount.Window.Width = width; mount.Window.Height = 760;
                Render(view, width, 760); await Drain(); view.UpdateLayout();
                True(Math.Abs(view.ActualWidth - width) <= .5 / VisualTreeHelper.GetDpi(view).DpiScaleX,
                    "the content reaches the requested breakpoint width within one half display pixel");
                var content = (Grid)view.FindName("MusicContent");
                True(Math.Abs(content.ActualWidth - (view.ActualWidth - 64)) < .5 / VisualTreeHelper.GetDpi(view).DpiScaleX,
                    "the responsive container keeps both 32 DIP page margins at the current DPI");
                var accountSection = (Border)view.FindName("AccountsSection");
                var accountPanel = (StackPanel)accountSection.Child;
                var accountTitle = accountPanel.Children.OfType<TextBlock>().Single();
                var accounts = (MusicAccountsView)view.FindName("AccountsPanel");
                var titleBounds = new Rect(accountTitle.TranslatePoint(new Point(), accountSection), accountTitle.RenderSize);
                var accountBounds = new Rect(accounts.TranslatePoint(new Point(), accountSection), accounts.RenderSize);
                True(Math.Abs(titleBounds.Left - accountBounds.Left) < .5 && accountBounds.Top >= titleBounds.Bottom &&
                    accountBounds.Top - titleBounds.Bottom <= 9 && Math.Abs(accountBounds.Width - accountSection.ActualWidth) < 1,
                    "at every width the account heading is above a full-width table with a compact gap");
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
                    // Equal star columns distribute an odd physical pixel to one tile.
                    double dpi = VisualTreeHelper.GetDpi(panels).DpiScaleX;
                    double availablePixels = Math.Round((panels.ActualWidth - 24) * dpi);
                    bool equalTiles = Math.Abs(Math.Min(a.Width, b.Width) * dpi - Math.Floor(availablePixels / 2)) < .01 &&
                        Math.Abs(Math.Max(a.Width, b.Width) * dpi - Math.Ceiling(availablePixels / 2)) < .01;
                    True(stacked ? b.Top >= a.Bottom && Math.Abs(a.Width - panels.ActualWidth) < .5 :
                        Math.Abs(b.Left - a.Right - 24) < .5 && equalTiles && Math.Abs(a.Top - b.Top) < .5 &&
                        Math.Abs(a.Left) < .5 && Math.Abs(b.Right - panels.ActualWidth) < .5,
                        pair.Item1 + " splits into two equal tiles and stacks only below its threshold" +
                        $"; width={width}; DPI=({VisualTreeHelper.GetDpi(panels).DpiScaleX}, {VisualTreeHelper.GetDpi(panels).DpiScaleY})" +
                        $"; panelWidth={panels.ActualWidth}; columnsActual=({panels.ColumnDefinitions[0].ActualWidth}, {panels.ColumnDefinitions[1].ActualWidth}, {panels.ColumnDefinitions[2].ActualWidth})" +
                        $"; left={a}; right={b}; IsStacked={SettingsLayout.GetIsStacked(panels)}");
                }
            }
        });
        yield return Case("replica priority handle commands clamp first last rows and save once", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view);
            await Drain(); await Until(() => !f.Module.Busy);
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities"); var list = (ListBox)priority.FindName("PriorityItems");
            Thumb Grip(int index) => Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<Thumb>().Single();
            var first = Grip(0);
            True(!ComponentCommands.MoveUp.CanExecute(null, first) && ComponentCommands.MoveDown.CanExecute(null, first), "first handle can only move down");
            True(first.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.Down && binding.Modifiers == ModifierKeys.Alt && binding.Command == ComponentCommands.MoveDown), "Alt+Down reaches the native command without input injection");
            int writes = f.Calls.Count("music.quality.settings.save"); var source = f.Module.QualityCategories[0];
            ComponentCommands.MoveUp.Execute(null, first); Equal(writes, f.Calls.Count("music.quality.settings.save"));
            ComponentCommands.MoveDown.Execute(null, first); await Until(() => f.Module.CanEditQuality && f.Module.QualityCategories[1] == source);
            Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            var last = Grip(list.Items.Count - 1);
            True(ComponentCommands.MoveUp.CanExecute(null, last) && !ComponentCommands.MoveDown.CanExecute(null, last), "last handle can only move up");
            ComponentCommands.MoveDown.Execute(null, last); Equal(writes + 1, f.Calls.Count("music.quality.settings.save"));
            ComponentCommands.MoveUp.Execute(null, last); await Until(() => f.Module.CanEditQuality);
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
                var priority = (SettingsPriorityList)view.FindName(item.Item1); priority.BringIntoView(); await Drain(); view.UpdateLayout();
                var list = (ListBox)priority.FindName("PriorityItems"); var scroll = priority.DragViewport;
                const int from = 2, to = 3;
                var grip = Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(from)).OfType<Thumb>().Single();
                var original = list.Items.Cast<object>().ToArray(); var target = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(to);
                var above = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(from - 1);
                var drop = target.TranslatePoint(new Point(60, target.ActualHeight - 2), scroll); int writes = f.Calls.Count(item.Item2);
                priority.Drag.Begin(grip, list, grip.TranslatePoint(new Point(18, 18), scroll)); priority.Drag.Update(drop);
                True(priority.IsDragging && priority.Drag.DestinationIndex == to, "drag tracks the lifted row centre");
                True(!ComponentCommands.MoveUp.CanExecute(null, grip) && !ComponentCommands.MoveDown.CanExecute(null, grip), "drag disables keyboard reordering");
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
            var toggle = Visuals(source).OfType<CheckBox>().Single();
            view.Visibility = Visibility.Collapsed; await Drain();
            ComponentCommands.MoveDown.Execute(null, grip); toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!ComponentCommands.MoveDown.CanExecute(null, grip), "hidden controls cannot submit");
            view.Visibility = Visibility.Visible; await Drain();
            mount.Window.Content = null; await Drain();
            ComponentCommands.MoveDown.Execute(null, grip); toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Equal(writes, f.Calls.Count("music.quality.settings.save")); True(!ComponentCommands.MoveDown.CanExecute(null, grip), "unloaded controls cannot submit");
        });
        yield return Case("replica settings remain within shell content and retain the hidden global player", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await InstallSettingsSources(f, 6);
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
                var header = (DesktopPageHeader)shell.FindName("PageHeaderHost");
                var heading = (TextBlock)header.FindName("Heading"); var slot = (ContentControl)header.FindName("LeadingSlot");
                var back = (Button)shell.FindName("BackButton");
                True(heading.IsVisible && heading.Text == "设置" && slot.IsVisible && region.Margin == new Thickness(0) &&
                    !((FrameworkElement)shell.FindName("SearchInput")).IsVisible, "settings title and category sit in the header beside back");
                True(heading.TranslatePoint(new Point(), root).X > back.TranslatePoint(new Point(back.ActualWidth, 0), root).X &&
                    slot.TranslatePoint(new Point(), root).X > heading.TranslatePoint(new Point(heading.ActualWidth, 0), root).X,
                    "header order is back, settings, category");
                var areaTabs = (Panel)slot.Content; var area = areaTabs.Children.OfType<RadioButton>().ToArray(); Equal("设置类别", AutomationProperties.GetName(areaTabs));
                True(area.Select(tab => (string)tab.Content).SequenceEqual(new[] { "音乐" }) &&
                    area[0].IsChecked == true, "shared category tabs reflect the supplied modules");
                var settingsView = (MusicSettingsView)f.Module.SettingsView; var tabs = (Panel)settingsView.FindName("SettingsNavigation");
                var accent = ((SolidColorBrush)shell.FindResource("MeowFilm.Brush.Accent")).Color; var selectedLine = (Border)area[0].Template.FindName("Underline", area[0]);
                True(area.All(tab => ReferenceEquals(tab.Style, shell.FindResource("MeowFilm.SettingsArea"))) &&
                    selectedLine.BorderBrush is SolidColorBrush line && line.Color == accent,
                    "header category uses the shared shell settings style");
                double tabsTop = tabs.TranslatePoint(new Point(), root).Y, regionTop = region.TranslatePoint(new Point(), root).Y;
                True(tabsTop - regionTop < 1, "settings content starts directly below the compact header");
                foreach (var size in new[] { new Size(1440, 900), new Size(1110, 869), new Size(860, 600) })
                {
                    // RDP's current monitor can clamp an offscreen HWND. A fixed test
                    // viewport exercises the requested size without resizing the desktop.
                    shell.MinWidth = shell.MaxWidth = size.Width;
                    shell.MinHeight = shell.MaxHeight = size.Height;
                    shell.Width = size.Width; shell.Height = size.Height; shell.UpdateLayout(); await Drain(); root.UpdateLayout();
                    True(Math.Abs(root.ActualWidth - size.Width) < 1 && Math.Abs(root.ActualHeight - size.Height) < 1,
                        "the shell visual uses the actual requested screenshot viewport");
                    var sidebar = (ScrollViewer)shell.FindName("SidebarScroll"); var footer = (Border)shell.FindName("PlayerFrame");
                    Equal(Visibility.Visible, sidebar.Visibility); Equal(Visibility.Collapsed, footer.Visibility);
                    True(ReferenceEquals(player, ((ContentControl)shell.FindName("PlayerHost")).Content),
                        "settings retain the same global player instance");
                    var view = (MusicSettingsView)f.Module.SettingsView; var bounds = new Rect(view.TranslatePoint(new Point(), root), view.RenderSize);
                    True(bounds.Left >= 203 && bounds.Bottom <= root.ActualHeight + 1,
                        "settings remain inside the shell while the player footer is hidden");
                    if (size.Width == 860)
                    {
                        var accountSection = (Border)view.FindName("AccountsSection");
                        var accountLabel = ((StackPanel)accountSection.Child).Children.OfType<TextBlock>().Single(text => text.Text == "账号");
                        var accounts = (MusicAccountsView)view.FindName("AccountsPanel");
                        True(accounts.TranslatePoint(new Point(), accountSection).Y >=
                            accountLabel.TranslatePoint(new Point(0, accountLabel.ActualHeight), accountSection).Y,
                            "the minimum shell does not waste a label column beside its account table");
                    }
                    ReplicaAnchors(view)[^1].IsChecked = true; await Drain(); root.UpdateLayout();
                    True(((MusicSourcesSettingsView)view.FindName("SourcesPage")).IsVisible &&
                        footer.Visibility == Visibility.Collapsed, "independent source management keeps the player hidden");
                    var sources = (MusicSourcesSettingsView)view.FindName("SourcesPage");
                    var items = (ItemsControl)sources.FindName("SourceList");
                    var body = (ScrollViewer)items.Template.FindName("ItemsScroll", items);
                    var sourceHeader = (Grid)sources.FindName("SourceHeader");
                    var firstRow = Visuals((DependencyObject)items.ItemContainerGenerator.ContainerFromIndex(0))
                        .OfType<Grid>().Single(grid => grid.ColumnDefinitions.Count == 7);
                    var firstName = firstRow.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 2);
                    var nameHeader = sourceHeader.Children.OfType<TextBlock>().Single(text => text.Text == "名称");
                    True(Math.Abs(firstName.TranslatePoint(new Point(), sources).X -
                        nameHeader.TranslatePoint(new Point(), sources).X) < 1,
                        $"table name alignment at {size.Width}: header={nameHeader.TranslatePoint(new Point(), sources).X} " +
                        $"row={firstName.TranslatePoint(new Point(), sources).X}; headerWidth={sourceHeader.ActualWidth} " +
                        $"viewport={body.ViewportWidth} extent={body.ExtentWidth} rowWidth={firstRow.ActualWidth}");
                    if (size.Width == 860)
                    {
                        True(body.ViewportHeight >= 82 && body.ScrollableHeight > 0,
                            "the minimum shell retains a visible source row and its internal scrollbar");
                    }
                    CaptureSettings(root, "replica-shell-source-" + (int)size.Width + ".png");
                    body.ScrollToRightEnd(); await Drain(); root.UpdateLayout();
                    var headerScroll = (ScrollViewer)sources.FindName("HeaderScroll");
                    True(Math.Abs(headerScroll.HorizontalOffset - body.HorizontalOffset) < 1,
                        "the fixed header follows horizontal source-table scrolling");
                    var remove = Visuals(sources).OfType<Button>().First(button => Equals(button.Content, "删除"));
                    var removeBounds = new Rect(remove.TranslatePoint(new Point(), sources), remove.RenderSize);
                    True(removeBounds.Left >= 0 && removeBounds.Right <= sources.ActualWidth,
                        "the four fixed row actions are reachable at every supported window width");
                    CaptureSettings(root, "replica-shell-source-actions-" + (int)size.Width + ".png");
                    body.ScrollToLeftEnd(); await Drain();
                    ReplicaAnchors(view)[0].IsChecked = true; await Drain(); root.UpdateLayout();
                    CaptureSettings(root, "replica-shell-accounts-" + (int)size.Width + ".png");
                    True(!f.Runtime.Calls.Any(method => method is "music.script.invoke" or "music.script.resolve"),
                        "source management reads the shared catalog without invoking scripts");
                }
                var content = (FrameworkElement)settingsView.FindName("MusicContent");
                True(content.IsVisible, "music content remains owned by the visible module settings");
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
            await InstallSettingsSources(f, 6);
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
                True(popup.IsOpen && DialogPlacement.GetCentered(popup), "advanced settings open as a window-centered dialog");
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
            var picker = Visuals(view).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "起播目标音质");
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
            var cacheView = f.Module.CacheSettingsView;
            Render(cacheView, 600, 240); await Drain();
            var cache = (Border)cacheView.FindName("CacheSection");
            var label = Visuals(cache).OfType<TextBlock>().Single(text => text.Text == "播放缓存");
            var clear = Visuals(cache).OfType<Button>().Single(button => Equals(button.Content, "清空播放缓存"));
            True(clear.TranslatePoint(new Point(), cache).Y >= label.TranslatePoint(new Point(0, label.ActualHeight), cache).Y,
                "narrow cache control is below its section label");
            var appearance = f.Module.AppearanceSettingsView;
            Render(appearance, 600, 700); await Drain();
            var pickers = Visuals(appearance).OfType<ComboBox>().ToArray(); Equal(3, pickers.Length);
            var mode = pickers.Single(picker => AutomationProperties.GetName(picker) == "播放器配色");
            var color = pickers.Single(picker => AutomationProperties.GetName(picker) == "播放器固定配色");
            var playlist = pickers.Single(picker => AutomationProperties.GetName(picker) == "歌单样式");
            True(color.TranslatePoint(new Point(), appearance).Y >
                mode.TranslatePoint(new Point(0, mode.ActualHeight), appearance).Y + 16 &&
                playlist.TranslatePoint(new Point(), appearance).Y > color.TranslatePoint(new Point(0, color.ActualHeight), appearance).Y + 16,
                "mode, preset dropdown and playlist retain separate labeled rows at narrow width");
        });
        yield return Case("replica native keyboard anchors and high contrast preserve current semantics", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync(); var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            var anchors = ReplicaAnchors(view); True(anchors[0].Focus(), "anchor has real native focus");
            foreach (var step in new[] { (Key.End, 4), (Key.Home, 0), (Key.Right, 1), (Key.Left, 0) })
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, step.Item1) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                ((UIElement)Keyboard.FocusedElement).RaiseEvent(input); await Drain(); mount.Window.UpdateLayout();
                True(input.Handled && anchors[step.Item2].IsChecked == true && anchors[step.Item2].IsKeyboardFocused, "anchors navigate and retain focus");
            }
            DesktopTheme.SetIsHighContrast(view, true); view.UpdateLayout();
            Equal(SystemColors.WindowColor, ((SolidColorBrush)view.Background).Color);
            var picker = Visuals(view).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "起播目标音质");
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
            tabs[1].IsChecked = true; await Drain();
            True(f.Module.HomeSourceSettingsView.IsVisible && !f.Module.AppearanceSettingsView.IsVisible, "source controls only mount in their own area");
            Equal(sourceReads, f.Calls.Count("music.catalogSource.settings.get"));
            tabs[2].IsChecked = true; await Drain();
            var cache = f.Module.CacheSettingsView;
            var clear = Visuals(cache).OfType<Button>().Single(button => Equals(button.Content, "清空播放缓存"));
            True(clear.IsEnabled && cache.IsVisible && !f.Module.HomeSourceSettingsView.IsVisible, "cache control belongs to General cache area");
            f.Dialogs.Confirmation = false;
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !f.Module.Busy);
            True((await f.Store.ReadAsync(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace)).Data is null, "cancelled confirmation does not create cache metadata");
            f.Dialogs.Confirmation = true;
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Until(() => !f.Module.Busy);
            True((await f.Store.ReadAsync(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace)).Data is not null, "confirmed action reaches the actual isolated cache owner");
            Equal("播放缓存已清空", f.Module.Status);
            Equal(qualityReads, f.Calls.Count("music.quality.settings.get"));
            True(!f.Runtime.HasStarted && !f.Module.SettingsView.IsLoaded, "General does not activate music accounts sources or scripts");
            foreach (int width in new[] { 900, 600 })
            {
                mount.Window.Width = width; mount.Window.UpdateLayout(); await Drain();
                foreach (int area in new[] { 1, 2 })
                {
                    tabs[area].IsChecked = true; await Drain();
                    CaptureSettings(general, $"general-area-{area}-{width}.png");
                }
            }
        });
        yield return Case("settings switches keep green on state and menus highlight the pointed option only", async () =>
        {
            await using var f = await Fixture.Create(); await f.Module.ActivateSettingsAsync();
            var view = (MusicSettingsView)f.Module.SettingsView; using var mount = new ReplicaMount(view);
            await Drain(); await Until(() => !f.Module.Busy);
            foreach (string name in new[] { "SongPriorities", "PlaylistPriorities", "QualityPriorities", "SpatialPriorities", "MetadataPriorities", "ArtworkPriorities" })
            {
                var priority = (SettingsPriorityList)view.FindName(name);
                var list = (ListBox)priority.FindName("PriorityItems");
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                var toggle = Visuals(row).OfType<CheckBox>().Single(); var grip = Visuals(row).OfType<Thumb>().Single();
                True(grip.TranslatePoint(new Point(), row).X >= toggle.TranslatePoint(new Point(toggle.ActualWidth, 0), row).X,
                    "drag handle follows the switch on " + name);
                var track = (Border)toggle.Template.FindName("Track", toggle);
                Equal(Color.FromRgb(34, 197, 94), ((SolidColorBrush)track.Background).Color);
                toggle.IsEnabled = false;
                Equal(Color.FromRgb(34, 197, 94), ((SolidColorBrush)track.Background).Color);
                toggle.IsEnabled = true;
                var line = (Border)row.Template.FindName("Line", row);
                True(line.IsVisible && line.BorderThickness.Bottom == 1, "every adjacent priority row has a separator");
                list.SelectedIndex = 1; SimulateFocus(row, UIElement.IsMouseOverProperty, true);
                True(line.IsVisible, "hover and selection do not erase row separators");
                SimulateFocus(row, UIElement.IsMouseOverProperty, false);
            }
            var surface = new StackPanel();
            surface.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MeowFilm.Capabilities;component/Settings/SettingsTheme.xaml", UriKind.Relative) });
            var on = new CheckBox { IsChecked = true, Style = (Style)surface.FindResource("Settings.Switch") };
            var off = new CheckBox { IsChecked = false, Style = on.Style };
            var selected = new ComboBoxItem { Content = "网易云音乐", IsSelected = true, Style = (Style)surface.FindResource("MeowFilm.SettingsComboItem") };
            var hovered = new ComboBoxItem { Content = "酷狗音乐", Style = selected.Style };
            surface.Children.Add(on); surface.Children.Add(off); surface.Children.Add(selected); surface.Children.Add(hovered);
            mount.Window.Content = surface; await Drain(); surface.UpdateLayout();
            var offTrack = (Border)off.Template.FindName("Track", off);
            True(((SolidColorBrush)offTrack.Background).Color != Color.FromRgb(34, 197, 94), "off is visually distinct from on");
            var rest = ((SolidColorBrush)selected.Background).Color;
            Equal((byte)0, rest.A);
            SimulateFocus(hovered, UIElement.IsMouseOverProperty, true);
            Equal(((SolidColorBrush)surface.FindResource("MeowFilm.Brush.AccentSoft")).Color, ((SolidColorBrush)hovered.Background).Color);
            Equal(rest, ((SolidColorBrush)selected.Background).Color);
            SimulateFocus(hovered, UIElement.IsMouseOverProperty, false); Equal(rest, ((SolidColorBrush)hovered.Background).Color);
            GeneralAppearanceTests.SavePreview(surface, "settings-states", 260, 180);
            DesktopTheme.SetIsHighContrast(surface, true); surface.UpdateLayout();
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)((Border)on.Template.FindName("Track", on)).Background).Color);
            SimulateFocus(hovered, UIElement.IsMouseOverProperty, true);
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)hovered.Background).Color);
        });
        yield return Case("replica screenshot states use real owner data and official resources without remote reads", async () =>
        {
            await using var f = await DetailsScene.Create(); await f.Module.ActivateSettingsAsync(); var view = (MusicSettingsView)f.Module.SettingsView;
            using var mount = new ReplicaMount(view); await Drain(); await Until(() => !f.Module.Busy);
            foreach (int width in new[] { 1248, 908, 600 })
            {
                mount.Window.Width = width; mount.Window.UpdateLayout(); await Drain();
                foreach (var target in new[] { (0, "accounts"), (1, "search"), (2, "quality"), (3, "metadata") })
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
