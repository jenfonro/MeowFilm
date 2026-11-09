using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    // Synthetic InputManager routing only; never injects physical mouse input or changes Handled.
    internal static void ProcessBlankInput(Grid blank, RoutedEvent routed, Action<string, MouseButtonEventArgs> snapshot)
    {
        var input = new MouseButtonEventArgs(Mouse.PrimaryDevice,
            Environment.TickCount, MouseButton.Left)
        { RoutedEvent = routed, Source = blank };
        var promotedEvent = routed == Mouse.PreviewMouseDownEvent ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;
        var stagingKey = new object();
        void TargetPromotion(object sender, PreProcessInputEventArgs args)
        {
            if (ReferenceEquals(args.StagingItem.Input, input))
                args.StagingItem.SetData(stagingKey, input);
            else if (ReferenceEquals(args.StagingItem.GetData(stagingKey), input) &&
                args.StagingItem.Input is MouseButtonEventArgs mouse && mouse.RoutedEvent == promotedEvent)
            {
                // WPF copies staging data, but not Source, when promoting preview input.
                snapshot("promotion-before", mouse);
                mouse.Source = blank;
                snapshot("promotion-target", mouse);
            }
        }
        var manager = InputManager.Current;
        manager.PreProcessInput += TargetPromotion;
        try
        {
            snapshot("submit", input);
            manager.ProcessInput(input);
            snapshot("complete", input);
        }
        finally { manager.PreProcessInput -= TargetPromotion; }
    }

    private static IEnumerable<TestCase> PlayerChromeCases()
    {
        yield return Case("player FM popup dismissal cannot open detail and releases its input hook", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var player = (MusicPlayerView)f.Module.PlayerView;
            var window = new Window { Content = player };
            using var surface = new HwndSource(new HwndSourceParameters("recommendation-fm-input-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 100
            });
            var activeKey = typeof(Window).GetFields(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(DependencyPropertyKey))
                .Select(field => (DependencyPropertyKey)field.GetValue(null)!)
                .Single(key => key.DependencyProperty == Window.IsActiveProperty);
            int requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) =>
            { if (args.Surface == DesktopGlobalSurface.PlayerDetail) requests++; };
            var popup = (System.Windows.Controls.Primitives.Popup)player.FindName("FmPopup");
            try
            {
                // A logical Window supplies ownership without Show/Activate or physical input.
                surface.RootVisual = player; Render(player, 1110, 100); await Drain();
                True(player.IsLoaded && player.IsVisible && ReferenceEquals(Window.GetWindow(player), window),
                    "offscreen player keeps its logical window owner");
                window.SetValue(activeKey, true);
                var inputField = typeof(MusicPlayerView).GetField("surfaceInput",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var input = (PlayerSurfaceInput)inputField.GetValue(player)!;
                var sourcesField = typeof(PlayerSurfaceInput).GetField("sources",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var sources = (System.Collections.IDictionary)sourcesField.GetValue(input)!;
                Equal(0, sources.Count);
                popup.IsOpen = true; await Drain();
                True(popup.IsOpen && PresentationSource.FromVisual(popup.Child) is HwndSource,
                    "FM layer owns a real offscreen popup source");
                Equal(1, sources.Count);
                var blank = (Grid)player.Content;
                void Input(RoutedEvent routed) => ProcessBlankInput(blank, routed, (_, _) => { });
                Input(Mouse.PreviewMouseDownEvent);
                popup.IsOpen = false;
                Input(Mouse.PreviewMouseUpEvent); await Drain();
                Equal(0, requests); Equal(0, sources.Count);
                Input(Mouse.PreviewMouseDownEvent); Input(Mouse.PreviewMouseUpEvent); await Drain();
                Equal(1, requests);
                var menu = (ContextMenu)player.FindName("ModeMenu");
                ((Button)player.FindName("ModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                True(menu.IsOpen, "ordinary mode uses the same observed floating input boundary");
                Equal(1, sources.Count);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                menu.Closed += (_, _) => closed.TrySetResult();
                Input(Mouse.PreviewMouseDownEvent); menu.IsOpen = false;
                Input(Mouse.PreviewMouseUpEvent);
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                Equal(1, requests); Equal(0, sources.Count);
                surface.RootVisual = null; window.Content = null; await Drain();
                True(inputField.GetValue(player) is null, "unload releases the input owner");
                Equal(0, sources.Count);
            }
            finally
            {
                window.SetValue(activeKey, false); player.Deactivate();
                surface.RootVisual = null; window.Content = null; window.Close();
            }
        });
        yield return Case("player synthetic WPF input routes suppress actual popup dismissal and admit the next gesture", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var player = (MusicPlayerView)f.Module.PlayerView;
            var shell = new Window { Content = player, Width = 1110, Height = 100,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None };
            // Offscreen fixture only. Active-state injection is not physical desktop activation.
            var activeKey = typeof(Window).GetFields(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(DependencyPropertyKey))
                .Select(field => (DependencyPropertyKey)field.GetValue(null)!)
                .Single(key => key.DependencyProperty == Window.IsActiveProperty);
            int requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) =>
            { if (args.Surface == DesktopGlobalSurface.PlayerDetail) requests++; };
            var blank = (Grid)player.Content;
            var routeTrace = new List<string>();
            string phase = "load";
            int previewDown = 0, bubbleDown = 0, previewUp = 0, bubbleUp = 0, viewDown = 0, viewUp = 0;
            string Describe(object? value) => value is FrameworkElement element
                ? element.GetType().Name + "#" + element.Name + (ReferenceEquals(element, blank) ? "(blank)" : "")
                : value?.GetType().Name ?? "<null>";
            void Snapshot(string point, MouseButtonEventArgs mouse) => routeTrace.Add(
                $"{point}/{mouse.RoutedEvent.Name}: Source={Describe(mouse.Source)} OriginalSource={Describe(mouse.OriginalSource)} " +
                $"Handled={mouse.Handled} IsActive={shell.IsActive} IsVisible={player.IsVisible} IsLoaded={player.IsLoaded} requests={requests}");
            void ObserveRoute(object sender, MouseButtonEventArgs mouse)
            {
                if (mouse.RoutedEvent == Mouse.PreviewMouseDownEvent) previewDown++;
                else if (mouse.RoutedEvent == Mouse.MouseDownEvent) bubbleDown++;
                else if (mouse.RoutedEvent == Mouse.PreviewMouseUpEvent) previewUp++;
                else if (mouse.RoutedEvent == Mouse.MouseUpEvent) bubbleUp++;
                else if (mouse.RoutedEvent == UIElement.PreviewMouseLeftButtonDownEvent) viewDown++;
                else if (mouse.RoutedEvent == UIElement.MouseLeftButtonUpEvent) viewUp++;
                Snapshot("view", mouse);
            }
            var observedEvents = new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent, Mouse.PreviewMouseUpEvent,
                Mouse.MouseUpEvent, UIElement.PreviewMouseLeftButtonDownEvent, UIElement.MouseLeftButtonUpEvent };
            MouseButtonEventHandler observer = ObserveRoute;
            foreach (var routed in observedEvents) blank.AddHandler(routed, observer, true);
            try
            {
                shell.Show(); shell.UpdateLayout(); await Drain();
                shell.SetValue(activeKey, true);
                void Input(RoutedEvent routed) => ProcessBlankInput(blank, routed, Snapshot);
                async Task Gesture(string label, Action? afterDown = null)
                {
                    phase = label; routeTrace.Clear();
                    var before = (previewDown, bubbleDown, previewUp, bubbleUp, viewDown, viewUp);
                    Input(Mouse.PreviewMouseDownEvent);
                    afterDown?.Invoke();
                    Input(Mouse.PreviewMouseUpEvent);
                    await Drain();
                    Equal((before.previewDown + 1, before.bubbleDown + 1, before.previewUp + 1,
                        before.bubbleUp + 1, before.viewDown + 1, before.viewUp + 1),
                        (previewDown, bubbleDown, previewUp, bubbleUp, viewDown, viewUp));
                }
                foreach (string name in new[] { "VolumePopup", "PlaylistPopup", "RatePopup", "SourcePopup", "QualityMenu", "MoreMenu" })
                {
                    phase = name + "/open"; routeTrace.Clear();
                    System.Windows.Controls.Primitives.Popup? popup = null;
                    ContextMenu? menu = null;
                    if (name == "QualityMenu")
                    {
                        menu = (ContextMenu)player.FindName(name);
                        menu.PlacementTarget = player; menu.IsOpen = true;
                    }
                    else if (name == "MoreMenu")
                    {
                        var more = (Button)player.FindName("MoreButton");
                        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); menu = more.ContextMenu;
                    }
                    else
                    {
                        popup = (System.Windows.Controls.Primitives.Popup)player.FindName(name);
                        if (name == "SourcePopup") f.Module.OpenSourceChoices();
                        popup.IsOpen = true;
                    }
                    await Drain();
                    True(menu?.IsOpen == true || popup?.IsOpen == true, name + " is an actual opened WPF layer");
                    int before = requests;
                    // Explicit close after processed Down isolates the contract from physical capture order.
                    await Gesture(name + "/dismiss", () => { if (menu is not null) menu.IsOpen = false; if (popup is not null) popup.IsOpen = false; });
                    Equal(before, requests);
                    await Gesture(name + "/next"); Equal(before + 1, requests);
                }
                int queueDismissals = 0;
                shell.AddHandler(PlayerSurfaceInput.DismissRequestedEvent, new RoutedEventHandler((_, _) =>
                {
                    queueDismissals++;
                    PlayerSurfaceInput.SetQueueOpen(shell, false);
                }));
                PlayerSurfaceInput.SetQueueOpen(shell, true);
                int beforeQueue = requests;
                await Gesture("queue/dismiss"); Equal(1, queueDismissals); Equal(beforeQueue, requests);
                await Gesture("queue/next"); Equal(beforeQueue + 1, requests);
                int prior = requests;
                phase = "orphan-up"; routeTrace.Clear();
                Input(Mouse.PreviewMouseUpEvent); await Drain(); Equal(prior, requests);
                phase = "unload-during-gesture"; routeTrace.Clear();
                Input(Mouse.PreviewMouseDownEvent);
                player.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Input(Mouse.PreviewMouseUpEvent); await Drain(); Equal(prior, requests);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"{phase}: {error.GetType().Name}: {error.Message}; IsActive={shell.IsActive}; requests={requests}; " +
                    $"arrivals PreviewDown={previewDown}, BubbleDown={bubbleDown}, PreviewUp={previewUp}, BubbleUp={bubbleUp}, " +
                    $"ViewDown={viewDown}, ViewUp={viewUp}\n" + string.Join("\n", routeTrace), error);
            }
            finally
            {
                foreach (var routed in observedEvents) blank.RemoveHandler(routed, observer);
                shell.SetValue(activeKey, false);
                player.Deactivate(); shell.Content = null; shell.Close();
            }
        });
        yield return Case("queue hover covers deletion area and reveals playable artwork without disabling recycling", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var queue = (MusicQueueView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
            Render(queue, 386, 420); await Drain();
            var list = (ListBox)queue.FindName("QueueList");
            var item = list.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem ??
                throw new InvalidOperationException("real queue data generates its real row template");
            var row = Visuals(item).OfType<Grid>().Single(grid => grid.Name == "QueueRow");
            var background = (Border)item.Template.FindName("RowSurface", item);
            var overlay = Visuals(row).OfType<Border>().Single(border =>
                border.Child is System.Windows.Shapes.Path);
            var mouseKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Equal(0d, overlay.Opacity);
            item.SetValue(mouseKey, true); row.SetValue(mouseKey, true);
            Render(queue, 386, 420);
            Equal(1d, overlay.Opacity);
            True(ReferenceEquals(background.Background, queue.FindResource("MeowFilm.Brush.ItemHover")),
                "whole container paints the hover including the remove column");
            var remove = Visuals(row).OfType<Button>().Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "从播放列表移除");
            var removeRect = new Rect(remove.TranslatePoint(new Point(), background), remove.RenderSize);
            True(new Rect(background.RenderSize).Contains(removeRect), "delete hit area sits within row highlight geometry");
            True(VirtualizingPanel.GetIsVirtualizing(list) &&
                VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling,
                "hover does not replace queue virtualization");
            GeneralAppearanceTests.SavePreview(queue, "context-queue-hover", 386, 420);
            item.SetValue(mouseKey, false); row.SetValue(mouseKey, false);
            Render(queue, 386, 420); Equal(0d, overlay.Opacity);
            var focusKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsKeyboardFocusWithinPropertyKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            row.SetValue(focusKey, true); Render(queue, 386, 420); Equal(1d, overlay.Opacity);
            row.SetValue(focusKey, false);
            DesktopTheme.SetIsHighContrast(queue, true);
            var texts = Visuals(row).OfType<TextBlock>().Where(text =>
                BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path is "Title" or "Artist" or "Duration").ToArray();
            True(texts.Length >= 3, "title artist and duration are real rendered text consumers");
            Render(queue, 386, 420);
            True(texts.All(text => ReferenceEquals(text.Foreground, SystemColors.WindowTextBrush)),
                "unhighlighted high contrast row uses system window text");
            item.SetValue(focusKey, true); row.SetValue(focusKey, true); Render(queue, 386, 420);
            True(ReferenceEquals(background.Background, SystemColors.HighlightBrush) &&
                texts.All(text => ReferenceEquals(text.Foreground, SystemColors.HighlightTextBrush)) &&
                ReferenceEquals(remove.Foreground, SystemColors.HighlightTextBrush),
                "keyboard focus high contrast colors include title artist duration and removal");
            GeneralAppearanceTests.SavePreview(queue, "context-queue-high-contrast-keyboard", 386, 420);
            item.SetValue(focusKey, false); row.SetValue(focusKey, false);
            item.SetValue(mouseKey, true); row.SetValue(mouseKey, true); Render(queue, 386, 420);
            True(ReferenceEquals(background.Background, SystemColors.HighlightBrush) &&
                texts.All(text => ReferenceEquals(text.Foreground, SystemColors.HighlightTextBrush)),
                "mouse high contrast uses the same readable row colors");
            var currentItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            True(Visuals(currentItem).OfType<TextBlock>().Any(text =>
                text.Text == f.Module.QueueSurfaceRows[0].Title && text.FontWeight == FontWeights.SemiBold),
                "current song retains a non-color indicator in high contrast");
            DesktopTheme.SetIsHighContrast(queue, false);
            item.SetValue(mouseKey, false); row.SetValue(mouseKey, false);
            var play = Visuals(row).OfType<Button>().Single(button => Grid.GetColumnSpan(button) == 3);
            string targetEntry = ((MusicQueueSurfaceRow)play.Tag).EntryId;
            var refreshField = typeof(MusicCapability).GetField("queueSurfacePending",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            async Task SettleQueue(string phase)
            {
                try
                {
                    await Until(() => !f.Module.Busy);
                    // Busy excludes this owner's existing refresh; do not issue another activation/read.
                    if (refreshField.GetValue(f.Module) is Task refresh)
                    {
                        await Until(() => refresh.IsCompleted);
                        await refresh;
                    }
                    await Drain();
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        $"queue {phase}: {error.Message}; Busy={f.Module.Busy}; Opens={f.Playback.Opens.Count}; " +
                        $"Rows={f.Module.QueueSurfaceRows.Count}; removeRPC={f.Calls.Count("music.queue.remove")}", error);
                }
            }
            int opens = f.Playback.Opens.Count;
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleQueue("play");
            Equal(opens + 1, f.Playback.Opens.Count);
            Render(queue, 386, 420);
            var currentRow = f.Module.QueueSurfaceRows.Single(value => value.EntryId == targetEntry);
            var currentContainer = list.ItemContainerGenerator.ContainerFromItem(currentRow) as ListBoxItem ??
                throw new InvalidOperationException("refreshed queue generates the played row's current container");
            var currentRemove = Visuals(currentContainer).OfType<Button>().Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "从播放列表移除");
            True(ReferenceEquals(currentRemove.Tag, currentRow), "remove targets the current owner row after refresh");
            int count = f.Module.QueueSurfaceRows.Count;
            int removals = f.Calls.Count("music.queue.remove");
            currentRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleQueue("remove");
            Equal(removals + 1, f.Calls.Count("music.queue.remove"));
            await Until(() => !f.Module.Busy && f.Module.QueueSurfaceRows.Count == count - 1);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        });
        yield return Case("player unload releases window input hooks and queue blank requests bubble to shell", async () =>
        {
            await using var f = await Fixture.Create();
            var player = (MusicPlayerView)f.Module.PlayerView;
            var window = new Window { Content = player, Width = 1110, Height = 100,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                True(player.IsLoaded && PresentationSource.FromVisual(player) is HwndSource,
                    "offscreen player is really loaded into its HWND without activation");
                var inputField = typeof(MusicPlayerView).GetField("surfaceInput",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var input = inputField.GetValue(player) as PlayerSurfaceInput ??
                    throw new InvalidOperationException("loaded player obtains window-scoped input owner in the offscreen window");
                var sourcesField = typeof(PlayerSurfaceInput).GetField("sources",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var registered = sourcesField.GetValue(input)!;
                Equal(1, (int)registered.GetType().GetProperty("Count")!.GetValue(registered)!);
                int dismiss = 0;
                window.AddHandler(PlayerSurfaceInput.DismissRequestedEvent, new RoutedEventHandler((_, _) => dismiss++));
                PlayerSurfaceInput.SetQueueOpen(window, true);
                True(PlayerSurfaceInput.GetQueueOpen(player), "queue fact reaches player via inheritance");
                var gesture = (SurfaceGesture)typeof(PlayerSurfaceInput).GetField("gesture",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(input)!;
                gesture.Down(false, true); input.Press(true); Equal(0, dismiss);
                gesture.Down(true, true);
                input.Press(false); Equal(0, dismiss);
                input.Press(true); Equal(1, dismiss);
                True(!input.Release(true), "queue dismissal cannot open detail");
                window.Content = null; await Drain();
                True(!player.IsLoaded, "detaching the real host raises unload");
                True(inputField.GetValue(player) is null, "unload drops the view's input subscription");
                var sources = sourcesField.GetValue(input)!;
                Equal(0, (int)sources.GetType().GetProperty("Count")!.GetValue(sources)!);
            }
            finally { player.Deactivate(); window.Content = null; window.Close(); await Drain(); }
        });
        yield return Case("player ordinary mode control and current heartbeat availability follow actual playback", async () =>
        {
            await using var f = await Fixture.Create();
            var footer = (MusicPlayerView)f.Module.PlayerView;
            using var source = new HwndSource(new HwndSourceParameters("player-mode-menu-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1000, Height = 100
            });
            source.RootVisual = footer; Render(footer, 1000, 100); await Drain();
            var mode = (Button)footer.FindName("ModeButton");
            var menu = (ContextMenu)footer.FindName("ModeMenu");
            var popup = (System.Windows.Controls.Primitives.Popup)footer.FindName("FmPopup");
            Equal(f.Module.ModeText, System.Windows.Automation.AutomationProperties.GetName(mode));
            True(!f.Module.HasPlaybackFm && !f.Module.CanHeartbeatCurrent, "no active recording implies no current-song heartbeat");
            mode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Drain();
            Equal(0, f.Calls.Count("music.queue.mode"));
            True(menu.IsOpen && !popup.IsOpen, "opening ordinary choices does not change the queue");
            Equal("列表循环,单曲循环,随机播放", string.Join(',', menu.Items.OfType<MenuItem>()
                .Select(System.Windows.Automation.AutomationProperties.GetName)));
            True(menu.Items.OfType<MenuItem>().All(item => item.IsEnabled),
                "the global loop choices do not depend on source eligibility");
            var random = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 2));
            ((IInvokeProvider)new MenuItemAutomationPeer(random).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => f.Calls.Count("music.queue.mode") == 1 && !f.Module.Busy);
            Equal(2, f.Module.Mode);
            True(!menu.IsOpen && !popup.IsOpen, "choosing a mode closes its menu");
            mode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Drain();
            True(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 2)).IsChecked, "saved random mode is selected");
            random.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
            Equal(1, f.Calls.Count("music.queue.mode"));
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 3)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => f.Module.Mode == 3 && !f.Module.Busy);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            True(!f.Module.HasPlaybackFm && !f.Module.CanHeartbeatCurrent, "local recording is never sent to WY heartbeat");
            var sourceButton = (Button)footer.FindName("PlaybackSourceButton");
            sourceButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Drain();
            var sourceMenu = (ContextMenu)footer.FindName("PlaybackSourceMenu");
            True(sourceMenu.IsOpen && sourceMenu.Items.OfType<MenuItem>().All(item =>
                !Equals(item.Header, "从当前歌曲进入心动") && !Equals(item.Header, "从当前歌曲进入推荐")),
                "the separate source menu hides unsupported local-recording entries");
            sourceMenu.IsOpen = false;
            mode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Drain();
            var retired = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 1));
            source.RootVisual = null; await Drain();
            True(!menu.IsOpen, "unload closes the ordinary mode menu");
            retired.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
            Equal(2, f.Calls.Count("music.queue.mode"));
        });
        yield return Case("player first visible frame has prepared artwork and no secondary footer disc", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var text = string.Join('\n', Enumerable.Range(0, 16).Select(i => $"[{i * 5 / 60:00}:{i * 5 % 60:00}]Prepared lyric {i}"));
            await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw, text });
            await f.Playback.SeekAsync(40);
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            try
            {
                shell.PrepareForRender();
                var root = (Border)shell.Content; shell.Content = null;
                root.Resources.MergedDictionaries.Add(shell.Resources); root.DataContext = shell.DataContext;
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var record = (MusicRecordView)detail.FindName("DetailRecord");
                var cover = (ArtworkImage)record.FindName("Cover");
                var loader = new HeldPlayerArtwork(); ArtworkScope.SetLoader(record, loader);
                record.Address = "fixture://prepared-cover";
                using var source = new HwndSource(new HwndSourceParameters("player-prepared-frame-fixture")
                {
                    WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                    PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
                });
                source.RootVisual = root;
                Render(root, 1110, 800); await Drain();
                var playerSurface = (Grid)shell.FindName("PlayerDetailSurface");
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                var readyOnReveal = false;
                playerSurface.IsVisibleChanged += (_, args) =>
                {
                    if (args.NewValue is true)
                    {
                        var lyrics = (ListBox)detail.FindName("LyricList");
                        var scroll = Visuals(lyrics).OfType<ScrollViewer>().First();
                        var row = lyrics.ItemContainerGenerator.ContainerFromIndex(8) as FrameworkElement;
                        readyOnReveal = ((Image)cover.Children[0]).Source is not null &&
                            ((GradientBrush)f.Module.PlayerBackground).GradientStops.All(stop => !stop.HasAnimatedProperties) &&
                            f.Module.Lyrics.Count == 16 && row is not null &&
                            Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y - scroll.ActualHeight * .35) < 2;
                    }
                };
                await f.Module.ShowLyricsAsync();
                await Until(() => loader.Calls.Count == 1);
                Equal(Visibility.Hidden, playerSurface.Visibility);
                True(((Image)cover.Children[0]).Source is null, "pending cover is not exposed by the entrance");
                var lease = await new PlayerArtworkFixture().AcquireAsync("fixture://prepared-cover");
                loader.Calls[0].Completion.SetResult(lease);
                await Until(() => playerSurface.Visibility == Visibility.Visible);
                Render(root, 1110, 800); await Drain();
                True(readyOnReveal, "cover pixels and final background exist at the first visible frame");
                True(ReferenceEquals(lease.Image, ((Image)cover.Children[0]).Source), "preparation's decoded image is used without refetching");
                Equal(1, loader.Calls.Count);
                Equal(0, Visuals(footer).OfType<MusicRecordView>().Count());
                Equal(1, Visuals(f.Module.PlayerView).OfType<MusicRecordView>().Count());
                var metadata = (StackPanel)footer.FindName("MetadataText");
                var panel = (Grid)footer.FindName("MetadataPanel");
                True(Math.Abs(metadata.TranslatePoint(new Point(), panel).X) < .5, "removed disc leaves no reserved column");
                await f.Module.OpenPlayerDetailAsync();
                await Until(() => playerSurface.Visibility == Visibility.Collapsed);
                True(((PlayerArtworkLease)lease).Disposed, "closing releases the prepared image on actual unmount");
                source.RootVisual = null;
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
        yield return Case("player preparation rechecks the cover replaced while lyrics are pending", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            detail.Visibility = Visibility.Hidden;
            var record = (MusicRecordView)detail.FindName("DetailRecord");
            var cover = (ArtworkImage)record.FindName("Cover");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            ArtworkScope.SetLoader(record, new CallbackArtworkLoader((address, token) =>
            {
                if (address == "fixture://first") return new PlayerArtworkFixture().AcquireAsync(address, token);
                Equal("fixture://second", address); entered.TrySetResult(); return completed.Task;
            }));
            record.Address = "fixture://first";
            using var source = new HwndSource(new HwndSourceParameters("player-changing-readiness-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            });
            source.RootVisual = detail;
            detail.Measure(new(1110, 800)); detail.Arrange(new(0, 0, 1110, 800)); detail.UpdateLayout();
            var lyrics = f.Calls.HoldNext("music.lyrics.local.get");
            var preparing = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            await lyrics.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            record.Address = "fixture://second";
            lyrics.Release();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            True(!preparing.IsCompleted, "lyrics completion cannot report readiness while the current cover is pending");
            var image = await new PlayerArtworkFixture().AcquireAsync("fixture://second");
            completed.SetResult(image); await preparing;
            True(ReferenceEquals(image.Image, ((Image)cover.Children[0]).Source), "prepared state uses the current cover");
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); source.RootVisual = null;
            True(((PlayerArtworkLease)image).Disposed, "retirement releases the current prepared cover");
        });
        yield return Case("ordinary player footer reveals changes to its host backdrop", async () =>
        {
            await using var f = await Fixture.Create();
            var host = new Border { Child = f.Module.PlayerView };
            DesktopTheme.SetIsHighContrast(host, false);
            foreach (var backdrop in new[] { Brushes.DarkSlateGray, Brushes.Linen })
            {
                host.Background = backdrop;
                Render(host, 1110, 80);
                var bitmap = new RenderTargetBitmap(1110, 80, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                foreach (var x in new[] { 1, 1108 })
                {
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(x, 40, 1, 1), pixel, 4, 0);
                    True(pixel.SequenceEqual(new[] { backdrop.Color.B, backdrop.Color.G, backdrop.Color.R, backdrop.Color.A }),
                        "footer blank areas expose the current host color");
                }
            }
        });
        yield return Case("footer rail touches top edge without a second rule", async () =>
        {
            await using var f = await Fixture.Create();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var progress = (SeekBar)footer.FindName("Progress");
            Equal(0d, progress.TranslatePoint(new Point(), footer).Y);
            True(progress.RailAtTop, "no inset track");
            var bar = new SeekBar { Maximum = 100, Value = 50, Height = 16, RailAtTop = true };
            var host = new Border { Background = Brushes.DarkSlateGray, Child = bar };
            byte[] Pixels()
            {
                host.Measure(new(400, 16)); host.Arrange(new(0, 0, 400, 16)); host.UpdateLayout();
                var bitmap = new RenderTargetBitmap(400, 16, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host); var bytes = new byte[400 * 16 * 4]; bitmap.CopyPixels(bytes, 1600, 0); return bytes;
            }
            bar.PlayedBrush = Brushes.CornflowerBlue;
            var first = Pixels();
            True(!first.AsSpan(300 * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 300) * 4, 4)), "ordinary faint track at top");
            True(!first.AsSpan(100 * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 100) * 4, 4)), "played part paints at y zero");
            bar.PlayedBrush = Brushes.SandyBrown;
            var second = Pixels();
            True(!first.AsSpan(100 * 4, 4).SequenceEqual(second.AsSpan(100 * 4, 4)), "cover-driven brush is consumed");
            True(first.AsSpan(300 * 4, 4).SequenceEqual(second.AsSpan(300 * 4, 4)), "unplayed stays neutral");
            True(first.AsSpan((4 * 400 + 300) * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 300) * 4, 4)), "no second lower line");
        });
        yield return Case("seek rail is continuous at rest active disabled and high contrast across DPI", async () =>
        {
            await using var f = await Fixture.Create();
            var bar = new SeekBar { Maximum = 180, Value = 90 };
            var host = new Border { Background = Brushes.White, Child = bar, Width = 400, Height = 8 };
            foreach (var highContrast in new[] { false, true })
            foreach (var enabled in new[] { false, true })
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                DesktopTheme.SetIsHighContrast(host, highContrast); bar.IsEnabled = enabled;
                host.Background = Brushes.White;
                host.Measure(new(400, 8)); host.Arrange(new(0, 0, 400, 8)); host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(400 * scale), (int)(8 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(host); int width = bitmap.PixelWidth, stride = width * 4;
                var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
                // A hidden Thumb formerly left 8 DIP uncovered. Both rail segments
                // must meet at exactly 50%, including their 0% and 100% endpoints.
                for (int x = 1; x < width - 1; x++)
                {
                    int i = (int)(4 * scale) * stride + x * 4, sample = (int)(4 * scale) * stride + (x < width / 2 ? 1 : width - 2) * 4;
                    True(pixels.AsSpan(i, 4).SequenceEqual(pixels.AsSpan(sample, 4)), "continuous rail without thumb hole or black seam");
                }
                True(!Visuals(bar).OfType<System.Windows.Controls.Primitives.Track>().Any(), "no system track paints another line");
                Equal(0d, bar.ValueAt(-20)); Equal(180d, bar.ValueAt(420)); Equal(90d, bar.ValueAt(200));
            }
            // Explicit active gesture drives a real animation clock; verify the
            // settled rail is 8 DIP rather than checking a style setter only.
            bar.IsEnabled = true; DesktopTheme.SetIsHighContrast(host, false);
            bar.BeginScrub(); await Task.Delay(170); await Drain();
            Render(host, 400, 8);
            bar.CancelScrub(); await Task.Delay(170); await Drain();
            Render(host, 400, 8);
        });
        yield return Case("footer hover paints a thicker rail and upper shade while its white capsule follows playback", async () =>
        {
            var bar = new SeekBar { Maximum = 100, Value = 25, Height = 16, RailAtTop = true,
                PlayedBrush = Brushes.CornflowerBlue, Margin = new Thickness(0, 32, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { Background = Brushes.White }; root.Children.Add(bar);
            root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MeowFilm.Capabilities;component/Presentation/CapabilityTheme.xaml", UriKind.Relative) });
            using var source = new HwndSource(new HwndSourceParameters("seek-hover-fixture") {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 400, Height = 80 });
            source.RootVisual = root; Render(root, 400, 80); await Drain();
            byte[] Capture()
            {
                var bitmap = new RenderTargetBitmap(400, 80, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var pixels = new byte[400 * 80 * 4]; bitmap.CopyPixels(pixels, 1600, 0); return pixels;
            }
            var resting = Capture();
            bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(160); await Drain(); Render(root, 400, 80);
            var active = Capture();
            // Inspect the actual tooltip and simulate its pointer coordinate, never the system pointer.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var tip = (ToolTip)typeof(SeekBar).GetField("preview", flags)!.GetValue(bar)!;
            var pointer = typeof(SeekBar).GetField("pointerX", flags)!;
            var update = typeof(SeekBar).GetMethod("UpdatePreview", flags)!;
            double HoverAt(double x) { pointer.SetValue(bar, x); update.Invoke(bar, null); return tip.HorizontalOffset + tip.DesiredSize.Width / 2; }
            double left = HoverAt(40), right = HoverAt(360);
            True(tip.IsOpen && Math.Abs(left - 100) < 1 && Math.Abs(right - 100) < 1,
                "hovering at either end of a paused track keeps the capsule centered on the 25% playback head");
            Equal("00:25 / 01:40", tip.Content as string);
            Equal(Colors.White, ((SolidColorBrush)tip.Background).Color); Equal(Colors.Black, ((SolidColorBrush)tip.Foreground).Color);
            True(Visuals(tip).OfType<Border>().Any(border => border.CornerRadius.TopLeft >= 16), "actual popup template is a white capsule");
            for (int y = 30; y <= 36; y++)
                True(active[(y * 400 + 50) * 4 + 2] < 240, "expanded played rail is visibly thicker than five DIP");
            True(active[(24 * 400 + 200) * 4 + 2] < resting[(24 * 400 + 200) * 4 + 2], "shade paints above the rail, not only inside its hit area");
            True(active[(12 * 400 + 200) * 4 + 2] > active[(24 * 400 + 200) * 4 + 2], "upper shade fades smoothly away from the rail");
            bar.PresentationKey = "hover-track"; bar.PlaybackRate = 1; bar.IsPlaying = true;
            double before = tip.HorizontalOffset; await Task.Delay(180); await Drain();
            if (SystemParameters.ClientAreaAnimation) True(tip.HorizontalOffset > before, "the native presentation clock moves both head and capsule without new media ticks");
            Equal(25d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(75); update.Invoke(bar, null);
            Equal("01:15 / 01:40", tip.Content as string);
            True(Math.Abs(tip.HorizontalOffset + tip.DesiredSize.Width / 2 - 300) < 1, "scrubbing uses the target head");
            bar.CancelScrub(); source.RootVisual = null; await Drain(); True(!tip.IsOpen, "unmount closes the popup");
        });
        yield return Case("footer shade stays light and continuous with a larger white head across colors and DPI", async () =>
        {
            var bar = new SeekBar { Maximum = 100, Value = 25, Height = 16, RailAtTop = true,
                Margin = new Thickness(0, 56, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { Background = Brushes.White }; root.Children.Add(bar); DesktopTheme.SetIsHighContrast(root, false);
            using var source = new HwndSource(new HwndSourceParameters("seek-color-fixture") {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 400, Height = 96 });
            source.RootVisual = root; Render(root, 400, 96); await Drain();
            var frames = new List<BitmapSource>();
            foreach (var backdrop in new[] { Brushes.White, Brushes.DarkSlateGray })
            foreach (var played in new[] { Brushes.DimGray, Brushes.Crimson })
            {
                root.Background = backdrop; bar.PlayedBrush = played; bar.Value = 25;
                Render(root, 400, 96); frames.Add(CaptureSeekFrame(root, 1));
                bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                await Task.Delay(170); await Drain(); Render(root, 400, 96);
                frames.Add(CaptureSeekFrame(root, 1));
                foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
                {
                    var frame = CaptureSeekFrame(root, scale);
                    // Infer composited coverage from the most distinct channel, not
                    // from the control's gradient stops or internal animation value.
                    byte[] background = [backdrop.Color.R, backdrop.Color.G, backdrop.Color.B];
                    byte[] foreground = [played.Color.R, played.Color.G, played.Color.B];
                    int channel = Enumerable.Range(0, 3).MaxBy(i => Math.Abs(foreground[i] - background[i]));
                    double quantization = 1d / Math.Abs(foreground[channel] - background[channel]);
                    double Coverage(double y)
                    {
                        var pixel = SeekPixel(frame, 300, y);
                        byte[] rgb = [pixel.R, pixel.G, pixel.B];
                        return (rgb[channel] - background[channel]) / (double)(foreground[channel] - background[channel]);
                    }
                    Equal(backdrop.Color, SeekPixel(frame, 300, 8));
                    True(Coverage(20) > .005 && Coverage(50) is > .08 and < .20,
                        $"at {scale}x the shade reaches beyond 32 DIP but its base stays below 20% coverage");
                    double previous = Coverage(14);
                    for (int y = 15; y <= 51; y++)
                    {
                        double next = Coverage(y);
                        // Compositing an 8-bit alpha gradient can round one channel
                        // down by one byte. A visual step must exceed that precision.
                        True(next >= previous - quantization - 1e-6 && next - previous < .015 + quantization,
                            $"continuous shade: background={backdrop.Color}, played={played.Color}, scale={scale}, y={y}, previous={previous:F4}, next={next:F4}");
                        previous = next;
                    }
                    True(Coverage(50) - Coverage(36) > .02, "the lower shade keeps fading instead of forming a flat strip");
                    foreach (double position in new[] { 0d, 25d, 100d })
                    {
                        bar.Value = position; Render(root, 400, 96);
                        var head = CaptureSeekFrame(root, scale);
                        double center = position == 0 ? 7.5 : position == 100 ? 392.5 : 100;
                        Equal(Colors.White, SeekPixel(head, center, 57));
                        int start = (int)(Math.Max(0, center - 12) * scale);
                        int end = (int)(Math.Min(400, center + 12) * scale);
                        int white = Enumerable.Range(start, end - start).Count(x =>
                        {
                            var pixel = SeekPixel(head, (x + .5) / scale, 57);
                            return pixel.R >= 250 && pixel.G >= 250 && pixel.B >= 250;
                        });
                        True(white / scale is >= 12 and <= 14.5,
                            $"at {scale}x and {position}% the white head is larger than the rail and not clipped at an endpoint");
                    }
                    bar.Value = 25; Render(root, 400, 96);
                }
                bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                await Task.Delay(170); await Drain(); Render(root, 400, 96);
                var resting = CaptureSeekFrame(root, 1);
                for (int y = 8; y <= 53; y++)
                    Equal(backdrop.Color, SeekPixel(resting, 300, y));
            }
            if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_SEEK_VISUALS") == "1")
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    for (int i = 0; i < frames.Count; i++)
                        drawing.DrawImage(frames[i], new Rect(i % 2 * 400, i / 2 * 96, 400, 96));
                var sheet = new RenderTargetBitmap(800, 384, 96, 96, PixelFormats.Pbgra32); sheet.Render(visual);
                SaveSeekFrame(sheet, "seekbar-states.png");
            }
            source.RootVisual = null;
        });
        yield return Case("footer shade retires after pointer exit despite focus and clears on disabled hidden unload and high contrast", async () =>
        {
            var bar = new SeekBar { Maximum = 100, Value = 25, Height = 16, RailAtTop = true,
                PlayedBrush = Brushes.DimGray, Margin = new Thickness(0, 56, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            var root = new Grid { Background = Brushes.White }; root.Children.Add(bar);
            DesktopTheme.SetIsHighContrast(root, false);
            var window = new Window { Content = root, Width = 400, Height = 96, WindowStyle = WindowStyle.None,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); Render(root, 400, 96); await Drain();
                var source = PresentationSource.FromVisual(root)!;
                async Task Shade(bool visible, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
                {
                    await Task.Delay(170); await Drain(); Render(root, 400, 96);
                    var pixel = SeekPixel(CaptureSeekFrame(root, 1), 300, 40);
                    True(visible ? pixel.R < 250 : pixel == Colors.White,
                        $"shade at line {line}: expectedVisible={visible}, pixel={pixel}, loaded={bar.IsLoaded}, visible={bar.IsVisible}, enabled={bar.IsEnabled}, focus={bar.IsKeyboardFocusWithin}, scrubbing={bar.IsScrubbing}");
                }
                void Hover(bool within) => bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                    { RoutedEvent = within ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
                Hover(true); await Shade(true);
                True(ReferenceEquals(root.InputHitTest(new Point(300, 40)), root) &&
                    ReferenceEquals(root.InputHitTest(new Point(300, 60)), bar),
                    "decorative pixels never extend the actual seek hit area into page content");
                True(bar.Focus() && bar.IsKeyboardFocusWithin, "offscreen HWND supplies real focus without moving the system pointer");
                Hover(false); await Shade(false);
                Equal(Colors.White, SeekPixel(CaptureSeekFrame(root, 1), 100, 57));
                True(SeekPixel(CaptureSeekFrame(root, 1), 50, 60).R < 200,
                    "keyboard focus retains the expanded rail without the decorative shade");
                bar.Value = 35; await Shade(false);
                bar.Value = 25; Hover(true); bar.BeginScrub(); Hover(false);
                bar.UpdateScrub(50); await Shade(true);
                bar.EndScrub(true); await Shade(false);
                bar.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Shade(true);
                bar.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right)
                    { RoutedEvent = Keyboard.PreviewKeyUpEvent });
                await Shade(false);
                Equal(55d, bar.Value);
                bar.Value = 50;
                Hover(true); DesktopTheme.SetIsHighContrast(root, true); await Shade(false);
                Equal(SystemColors.HighlightTextColor, SeekPixel(CaptureSeekFrame(root, 1), 200, 57));
                DesktopTheme.SetIsHighContrast(root, false); await Shade(true);
                bar.IsEnabled = false; await Shade(false);
                True(!bar.HasAnimatedProperties, "disabled seek surface releases highlight clocks");
                bar.IsEnabled = true; await Shade(true);
                bar.Visibility = Visibility.Collapsed; await Shade(false);
                bar.Visibility = Visibility.Visible; await Shade(false);
                Hover(true); await Shade(true);
                window.Content = null; await Drain();
                True(!bar.HasAnimatedProperties && !bar.IsScrubbing, "unmount removes presentation clocks and active gestures");
                window.Content = root; Render(root, 400, 96); await Drain(); await Shade(false);
            }
            finally { window.Close(); }
        });
        yield return Case("small record sampling retains antialiased cover edges at fractional DPI and detail grooves", async () =>
        {
            var record = new MusicRecordView { Address = "fixture://record", Width = 60, Height = 60 };
            ArtworkScope.SetLoader(record, new PlayerArtworkFixture());
            using var source = new HwndSource(new HwndSourceParameters("record-sampling-fixture") {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 400, Height = 400 });
            source.RootVisual = record; Render(record, 60, 60); await Drain();
            var cover = (ArtworkImage)record.FindName("Cover");
            await Until(() => ((Image)cover.Children[0]).Source is not null);
            var rotation = (RotateTransform)record.FindName("Rotation");
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            foreach (double angle in new[] { 0d, 17d, 43d })
            {
                rotation.Angle = angle;
                var bitmap = new RenderTargetBitmap((int)(60 * scale), (int)(60 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(record);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                True(pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0 && alpha < 255), "rotated circular edge retains antialiased coverage");
                True(RenderOptions.GetBitmapScalingMode(cover) == BitmapScalingMode.HighQuality, "actual cover inherits high quality minification");
            }
            var grooves = (System.Windows.Shapes.Ellipse)record.FindName("Grooves");
            var small = (DrawingBrush)grooves.Fill;
            record.Width = record.Height = 388; Render(record, 388, 388); await Drain();
            var detail = (DrawingBrush)grooves.Fill;
            True(!ReferenceEquals(small, detail) && small.IsFrozen && detail.IsFrozen, "small and large discs use size-appropriate retained artwork");
            True(((DrawingGroup)small.Drawing).Children.Count <= 6 && ((DrawingGroup)detail.Drawing).Children.Count > 20,
                "small discs omit subpixel dense rings while full-size detail keeps its original grooves");
            source.RootVisual = null;
        });
        yield return Case("seek gesture isolates live ticks commits once and rejects a retired track", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView; var bar = (SeekBar)footer.FindName("Progress");
            footer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Drain();
            bar.BeginScrub(); bar.UpdateScrub(74);
            await f.Playback.SeekAsync(12); await Drain();
            Equal(74d, bar.Value); Equal(12d, f.Playback.Seek);
            bar.EndScrub(true); await Until(() => !f.Module.Busy);
            Equal(74d, f.Playback.Seek); True(!bar.IsScrubbing, "released");
            bar.BeginScrub(); bar.UpdateScrub(95); bar.CancelScrub(); await Drain();
            Equal(74d, f.Playback.Seek); Equal(74d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(120);
            await f.Module.StepAsync(true); await Drain(); double replacementPosition = f.Playback.Seek;
            True(!bar.IsScrubbing, "track replacement immediately retires gesture");
            bar.EndScrub(true); await Drain(); Equal(replacementPosition, f.Playback.Seek);
            // UI Automation uses the same transaction and real media port.
            var peer = UIElementAutomationPeer.CreatePeerForElement(bar)!;
            ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)).SetValue(36);
            await Until(() => !f.Module.Busy); Equal(36d, f.Playback.Seek);
            using var inputSource = new HwndSource(new HwndSourceParameters("seek-input-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 80
            });
            inputSource.RootVisual = footer;
            footer.Measure(new(1110, 80)); footer.Arrange(new(0, 0, 1110, 80)); footer.UpdateLayout(); await Drain();
            void Key(Key key, RoutedEvent route) => bar.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, key) { RoutedEvent = route });
            Key(System.Windows.Input.Key.Right, Keyboard.PreviewKeyDownEvent);
            Equal(41d, bar.Value);
            await f.Playback.SeekAsync(37); await Drain(); Equal(41d, bar.Value);
            Key(System.Windows.Input.Key.Right, Keyboard.PreviewKeyUpEvent);
            await Until(() => !f.Module.Busy); Equal(41d, f.Playback.Seek);
            Key(System.Windows.Input.Key.End, Keyboard.PreviewKeyDownEvent); Equal(180d, bar.Value);
            Key(System.Windows.Input.Key.Escape, Keyboard.PreviewKeyDownEvent);
            Key(System.Windows.Input.Key.End, Keyboard.PreviewKeyUpEvent);
            await Drain(); Equal(41d, f.Playback.Seek); Equal(41d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(50); footer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            True(!bar.IsScrubbing, "unload cancels"); Equal(41d, f.Playback.Seek);
        });
        yield return Case("skip arrows animate direction without duplicate queue calls and clear on unload", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var next = (SkipButton)footer.FindName("NextButton");
            var previous = (SkipButton)footer.FindName("PreviousButton");
            True(next.Next && !previous.Next, "mirrored directions");
            next.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(70); await Drain();
            if (SystemParameters.ClientAreaAnimation) True(next.HasAnimatedProperties, "native hover motion active");
            Equal(0, f.Calls.Count("music.queue.next"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.next"));
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.previous"));
            next.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            True(!next.HasAnimatedProperties, "no retained clock after unload");
        });
        yield return Case("footer favorite toggles native membership for playing song not highlighted row", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            var song = f.Module.SelectedSong!; await f.Module.PlaySelectionAsync();
            f.Module.SelectedSong = f.Module.Songs.First(s => s.Id != song.Id);
            await f.Module.ToggleCurrentFavoriteAsync();
            True(f.Module.IsCurrentFavorite, "saved membership drives filled heart");
            Equal("取消收藏当前歌曲", f.Module.CurrentFavoriteLabel);
            await f.Module.NavigateAsync("favorites", default);
            Equal(1, f.Module.Songs.Count); Equal(song.Id, f.Module.Songs[0].Id);
            await f.Module.ToggleCurrentFavoriteAsync();
            True(!f.Module.IsCurrentFavorite, "remove updates heart without restarting playback");
            Equal(0, f.Module.Songs.Count);
            Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("footer favorite chrome matches the optional 43 by 32 badge geometry without song data", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            await f.Module.PlaySelectionAsync();
            await f.Module.ToggleCurrentFavoriteAsync();
            var footer = f.Module.PlayerView;
            // The footer is transparent; reference pixels are composited on the window backdrop it sits on.
            var host = new Border { Background = (Brush)footer.FindResource("MeowFilm.Brush.Window"), Child = footer };
            Render(host, 1110, 80);
            var button = (Button)footer.FindName("FavoriteButton");
            Equal(32d, button.ActualWidth);
            var badge = (TextBlock)button.Template.FindName("FavoriteBadge", button);
            Equal("", badge.Text);
            badge.Text = "10w+";
            Render(host, 1110, 80);
            Equal(43d, button.ActualWidth); Equal(32d, button.ActualHeight);
            var surface = (Border)button.Template.FindName("FavoriteSurface", button);
            Equal(new CornerRadius(16), surface.CornerRadius);
            var heart = Visuals(button).OfType<System.Windows.Shapes.Path>().Single();
            Equal(20d, heart.ActualWidth); Equal(18d, heart.ActualHeight);
            Equal(Color.FromRgb(0xFC, 0x3D, 0x49), ((SolidColorBrush)heart.Fill).Color);
            Equal(Color.FromRgb(0xFC, 0x3D, 0x49), ((SolidColorBrush)badge.Foreground).Color);

            var bitmap = new RenderTargetBitmap(1110, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var origin = button.TranslatePoint(new Point(), host);
            var crop = new CroppedBitmap(bitmap, new Int32Rect(
                (int)Math.Round(origin.X), (int)Math.Round(origin.Y), 43, 32));

            var pixels = new byte[43 * 32 * 4]; crop.CopyPixels(pixels, 43 * 4, 0);
            var points = Enumerable.Range(0, 43 * 32).Where(pixel =>
            {
                var offset = pixel * 4;
                var blue = pixels[offset]; var green = pixels[offset + 1];
                var red = pixels[offset + 2]; var alpha = pixels[offset + 3];
                return alpha > 0 && red > 240 && green < 220 && blue < 225 && red - green > 30;
            }).Select(pixel => new Point(pixel % 43, pixel / 43)).ToArray();
            True(points.Length > 250, "favorite reference paints the heart and badge");
            var bounds = new Rect(new Point(points.Min(point => point.X), points.Min(point => point.Y)),
                new Point(points.Max(point => point.X) + 1, points.Max(point => point.Y) + 1));
            True(Math.Abs(bounds.Left - 3) <= 1 && Math.Abs(bounds.Top - 7) <= 1 &&
                Math.Abs(bounds.Right - 40) <= 1 && Math.Abs(bounds.Bottom - 25) <= 1,
                $"favorite ink bounds match reference: {bounds}");
        });
        yield return Case("transport glyph painted centroid stays centered at 100 150 and 200 percent DPI", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var play = (Button)footer.FindName("PlayButton");
            foreach (var playing in new[] { false, true })
            {
                if (playing) f.Playback.Start();
                await Drain(); footer.UpdateLayout();
                foreach (double scale in new[] { 1d, 1.5, 2 })
                {
                    var size = (int)(40 * scale);
                    var full = new RenderTargetBitmap((int)(1110 * scale), (int)(80 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    full.Render(footer);
                    var origin = play.TranslatePoint(new Point(), footer);
                    var bitmap = new CroppedBitmap(full, new Int32Rect((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale), size, size));

                    var bytes = new byte[size * size * 4]; bitmap.CopyPixels(bytes, size * 4, 0);
                    double xSum = 0, ySum = 0, count = 0;
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int i = (y * size + x) * 4;
                            // Only the glyph inside the disc; a cropped footer also includes
                            // white page corners outside the circular red button.
                            double dx = (x + .5) / scale - 20, dy = (y + .5) / scale - 20;
                            if (dx * dx + dy * dy >= 225) continue;
                            if (bytes[i] < 240 || bytes[i + 1] < 240 || bytes[i + 2] < 240 || bytes[i + 3] < 240) continue;
                            xSum += (x + .5) / scale; ySum += (y + .5) / scale; count++;
                        }
                    True(count > 50, "glyph actually paints white pixels");
                    True(Math.Abs(xSum / count - 20) < .6 && Math.Abs(ySum / count - 20) < .6,
                        $"painted {(playing ? "pause" : "play")} centroid at {scale}: {xSum / count},{ySum / count}");
                }
            }
        });
        yield return Case("independent player footer shares playback and retires gestures and popups on exit", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var ordinary = (MusicPlayerView)f.Module.PlayerView;
            ordinary.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Drain();
            var ordinaryProgress = (SeekBar)ordinary.FindName("Progress");
            ordinaryProgress.BeginScrub(); ordinaryProgress.UpdateScrub(70);
            var opens = f.Playback.Opens.Count;
            var subscribers = f.Playback.Subscribers;
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            True(!ordinaryProgress.IsScrubbing, "covered ordinary footer retires its gesture without seeking");
            Equal(0d, f.Playback.Seek); Equal(opens, f.Playback.Opens.Count);
            var page = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var footer = (MusicPlayerView)((ContentControl)page.FindName("DetailPlayerHost")).Content;
            True(!ReferenceEquals(ordinary, footer), "player page owns its footer visual");
            True(ReferenceEquals(f.Module, footer.DataContext) && ReferenceEquals(ordinary.DataContext, footer.DataContext),
                "both visual controls consume the sole playback owner");
            using var source = new HwndSource(new HwndSourceParameters("player-page-controls-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            });
            source.RootVisual = page;
            Render(page, 1110, 800); await Drain();
            True(Visuals(page).OfType<FrameworkElement>().All(element => element.ToolTip is null) &&
                Visuals(ordinary).OfType<FrameworkElement>().All(element => element.ToolTip is null),
                "player titles and controls have no ordinary hover hints");
            Equal(subscribers, f.Playback.Subscribers);
            f.Playback.Start(); await Drain();
            var play = (Button)footer.FindName("PlayButton");
            Click(play); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Pauses);
            Click(play); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Resumes);
            var next = f.Calls.Count("music.queue.next");
            Click((Button)footer.FindName("NextButton")); await Until(() => !f.Module.Busy);
            Equal(next + 1, f.Calls.Count("music.queue.next")); Equal(opens + 1, f.Playback.Opens.Count);
            var progress = (SeekBar)footer.FindName("Progress");
            progress.BeginScrub(); progress.UpdateScrub(36); progress.EndScrub(true);
            await Until(() => !f.Module.Busy); Equal(36d, f.Playback.Seek);
            var requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) =>
            { if (args.Surface == DesktopGlobalSurface.PlayQueue) requests++; };
            Click(Visuals(footer).OfType<Button>().Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "播放队列"));
            Equal(1, requests);
            var more = (Button)footer.FindName("MoreButton");
            Click(more);
            ((MenuItem)more.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var rate = (System.Windows.Controls.Primitives.Popup)footer.FindName("RatePopup");
            True(rate.IsOpen, "page footer popup opens");
            progress.BeginScrub(); progress.UpdateScrub(90);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            True(!rate.IsOpen && !progress.IsScrubbing, "page exit closes its popup and cancels its own gesture");
            Equal(36d, f.Playback.Seek); Equal(opens + 1, f.Playback.Opens.Count);
            Equal(subscribers, f.Playback.Subscribers);
            source.RootVisual = null;
            ordinary.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
        yield return Case("complete player page preserves the underlying shell layout and lyrics at narrow bounds", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            try
            {
                shell.PrepareForRender();
                var root = (Border)shell.Content; shell.Content = null;
                root.Resources.MergedDictionaries.Add(shell.Resources);
                // The render fixture detaches WindowFrame from its Window. Keep
                // the production live backdrop binding instead of a stale brush snapshot.
                root.SetBinding(Border.BackgroundProperty, new Binding(nameof(Window.Background)) { Source = shell });
                root.DataContext = shell.DataContext;
                TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
                // Hidden native test presentation source admits real Loaded/visibility-dependent artwork.
                using var surface = new HwndSource(new HwndSourceParameters("player-chrome-code-fixture")
                {
                    WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                    PositionX = -32000, PositionY = -32000, Width = 1243, Height = 848
                });
                surface.RootVisual = root;
                root.Measure(new(1243, 848)); root.Arrange(new Rect(0, 0, 1243, 848)); root.UpdateLayout();
                var ordinaryFooter = f.Module.PlayerView;
                var ordinaryFooterHeight = ordinaryFooter.ActualHeight;

                await f.Module.ShowLyricsAsync();
                await Until(() => f.Module.IsLyrics);
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                True(!ReferenceEquals(ordinaryFooter, footer), "complete player page has its own footer");
                void AssertDetailBackdrop(Size size)
                {
                    var width = (int)size.Width; var height = (int)size.Height;
                    var actual = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    actual.Render(detail);
                    var canvas = new Border { Background = detail.Background };
                    canvas.Measure(size); canvas.Arrange(new Rect(size)); canvas.UpdateLayout();
                    var expected = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    expected.Render(canvas);
                    var y = (int)footer.TranslatePoint(new Point(0, footer.ActualHeight / 2), detail).Y;
                    foreach (var x in new[] { 1, width - 2 })
                    {
                        var area = new Int32Rect(x, y, 1, 1);
                        var pixel = new byte[4]; var backgroundPixel = new byte[4];
                        actual.CopyPixels(area, pixel, 4, 0); expected.CopyPixels(area, backgroundPixel, 4, 0);
                        True(pixel.SequenceEqual(backgroundPixel), "detail footer exposes the same full-page backdrop");
                    }
                }
                var record = (MusicRecordView)detail.FindName("DetailRecord");
                var cover = (ArtworkImage)record.FindName("Cover");
                ArtworkScope.SetLoader(record, new PlayerArtworkFixture());
                record.Address = "fixture://cover";
                f.Module.Lyrics.Clear();
                for (var i = 0; i < 16; i++) f.Module.Lyrics.Add(new(i * 5, "原生歌词排版测试 " + i, i == 2 ? "Translation test" : ""));
                var opens = f.Playback.Opens.Count;
                foreach (var size in new[] { new Size(1243, 848), new Size(860, 600) })
                {
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    cover.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Drain(); root.UpdateLayout();
                    var grid = (Grid)shell.FindName("RootLayout");
                    True(grid.Background is null, "player overlay does not repaint the shell");
                    Equal(Visibility.Visible, ((ScrollViewer)shell.FindName("SidebarScroll")).Visibility);
                    True(Math.Abs(grid.ColumnDefinitions[0].ActualWidth - 203) < .5, "underlying sidebar keeps its layout");
                    Equal(new Thickness(0), root.BorderThickness);
                    True(ReferenceEquals(root.Background, shell.Background), "rounded edges do not expose normal white canvas");
                    var title = Visuals(detail).OfType<TextBlock>()
                        .First(block => block.Text == f.Module.CurrentTitle);
                    var lyrics = (ListBox)detail.FindName("LyricList");
                    Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(title));
                    Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(title));
                    Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(lyrics));
                    Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(lyrics));
                    var viewport = Visuals(lyrics).OfType<ScrollViewer>().First();
                    True(Math.Abs(viewport.ViewportHeight - lyrics.ActualHeight) < 1,
                        "lyric edge spacing belongs to scrolling rows, not the visible viewport");
                    True(title.TranslatePoint(new Point(), root).X >= size.Width * .5, "title on right");
                    True(record.ActualWidth > 200 && record.ActualHeight > 200, "full disc not a thumbnail");
                    True(lyrics.ActualHeight >= 160, "usable lyric viewport at minimum window");
                    True(lyrics.TranslatePoint(new Point(lyrics.ActualWidth, 0), root).X < size.Width, "lyric column fits");
                    Equal(size, detail.RenderSize);
                    Equal(ordinaryFooterHeight, ordinaryFooter.ActualHeight);
                    True(ReferenceEquals(ordinaryFooter, ((ContentControl)shell.FindName("PlayerHost")).Content),
                        "ordinary footer is not moved into the player page");
                    True(Math.Abs(footer.TranslatePoint(new Point(0, footer.ActualHeight), root).Y - size.Height) < .5,
                        "page footer reaches the bottom of the whole window");
                    var play = (Button)footer.FindName("PlayButton");
                    True(Math.Abs(play.TranslatePoint(new Point(20, 0), root).X - size.Width / 2) < 1, "play remains window-centered in detail");
                    True(root.InputHitTest(play.TranslatePoint(new Point(20, 20), root)) is DependencyObject hit &&
                        Visuals(play).Contains(hit), "window chrome does not intercept the page footer");
                    Render(root, (int)size.Width, (int)size.Height);
                    DesktopTheme.SetIsHighContrast(grid, false); root.UpdateLayout();
                    Equal(Color.FromRgb(34, 197, 94), ((SolidColorBrush)play.Background).Color);
                    True(ReferenceEquals(f.Module.PlayerBackground, detail.Background), "player page root owns the cover backdrop");
                    True(footer.Background is null, "page footer paints no backdrop of its own");
                    TextBlock Title(FrameworkElement owner) => Visuals(owner).OfType<TextBlock>().First(block => block.Text == f.Module.CurrentTitle);
                    True(((SolidColorBrush)Title(footer).Foreground).Color != ((SolidColorBrush)Title(ordinaryFooter).Foreground).Color,
                        "page footer adapts its controls to the page backdrop");
                    foreach (var playerView in new[] { ordinaryFooter, footer })
                    {
                        var quality = (Button)playerView.FindName("QualityButton");
                        var label = Visuals(quality).OfType<TextBlock>().Single();
                        True(ReferenceEquals(quality.Foreground, label.Foreground), "quality label follows the control palette");
                        var artist = ((StackPanel)playerView.FindName("MetadataText")).Children.OfType<TextBlock>().Last();
                        True(ReferenceEquals(playerView.FindResource("Capability.Player.Muted"), artist.Foreground),
                            "artist text follows the footer's secondary text palette");
                    }
                    AssertDetailBackdrop(size);
                    True(f.Module.IsLyrics, "page footer consumes the existing active player state");
                    DesktopTheme.SetIsHighContrast(grid, true); root.UpdateLayout();
                    True(((SolidColorBrush)detail.Background).Color == SystemColors.WindowColor, "high contrast replaces the page backdrop");
                    var detailArtist = ((StackPanel)footer.FindName("MetadataText")).Children.OfType<TextBlock>().Last();
                    Equal(SystemColors.WindowTextColor, ((SolidColorBrush)detailArtist.Foreground).Color);
                    AssertDetailBackdrop(size);
                }
                True(record.PaletteBrush != Brushes.DimGray, "cover is loaded through public loader and sampled");
                Equal(opens, f.Playback.Opens.Count);
                var lyricList = (ListBox)detail.FindName("LyricList");
                var lyricScroll = Visuals(lyricList).OfType<ScrollViewer>().First();
                await f.Playback.SeekAsync(40); await Task.Delay(550); await Drain();
                var centered = lyricScroll.VerticalOffset;
                True(centered > 100, "actual playback position smoothly centers a later lyric");
                var row = lyricList.ItemContainerGenerator.ContainerFromIndex(8) as FrameworkElement;
                if (row is null) throw new InvalidOperationException("active lyric row is not realized after automatic centering");
                var rowCenter = row.TranslatePoint(new Point(0, row.ActualHeight / 2), lyricScroll).Y;
                var readingBand = lyricScroll.ActualHeight * .35;
                True(Math.Abs(rowCenter - readingBand) < 2,
                    $"active line reaches reference reading band: actual={rowCenter}, target={readingBand}, offset={centered}, " +
                    $"scrollable={lyricScroll.ScrollableHeight}, extent={lyricScroll.ExtentHeight}, viewport={lyricScroll.ViewportHeight}, row={row.ActualHeight}");
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                lyricList.RaiseEvent(wheel); await Task.Delay(250); await Drain();
                True(wheel.Handled && lyricScroll.VerticalOffset > centered, "wheel over actual lyrics scrolls");
                await f.Playback.SeekAsync(45); await Task.Delay(550); await Drain();
                True(Math.Abs(lyricScroll.VerticalOffset - centered) > 5, "manual scrolling is not immediately overwritten");
                // No new playback tick after this point: paused lyrics must still recover.
                // Synchronize with the timer plus animation settlement, not the
                // SelectedIndex bring-into-view behavior suppressed during manual scrolling.
                FrameworkElement? recoveredRow = null;
                try
                {
                    await Until(() =>
                    {
                        recoveredRow = lyricList.ItemContainerGenerator.ContainerFromIndex(9) as FrameworkElement;
                        return recoveredRow is not null &&
                            Math.Abs(recoveredRow.TranslatePoint(new Point(0, recoveredRow.ActualHeight / 2), lyricScroll).Y -
                                lyricScroll.ViewportHeight * .35) < 2;
                    });
                }
                catch (TimeoutException error)
                {
                    var actual = recoveredRow?.TranslatePoint(new Point(0, recoveredRow.ActualHeight / 2), lyricScroll).Y;
                    throw new TimeoutException($"Idle lyric recovery did not settle: font={detail.FontFamily.Source}, " +
                        $"index={f.Module.LyricDisplayIndex}, playing={f.Module.IsPlaying}, actual={actual}, " +
                        $"target={lyricScroll.ViewportHeight * .35}, row={recoveredRow?.ActualHeight}, " +
                        $"root={root.RenderSize}, detail={detail.RenderSize}, lyrics={lyricList.RenderSize}, " +
                        $"offset={lyricScroll.VerticalOffset}, extent={lyricScroll.ExtentHeight}, " +
                        $"viewport={lyricScroll.ViewportHeight}, scrollable={lyricScroll.ScrollableHeight}", error);
                }
                row = recoveredRow ?? throw new InvalidOperationException(
                    "active lyric row is not realized after manual-scroll recovery");
                True(Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), lyricScroll).Y - lyricScroll.ActualHeight * .35) < 2,
                    "idle timeout resumes lyric following without a new media tick");
                f.Module.PreviewLyric(f.Module.Lyrics[2]); await Drain();
                True(f.Module.HasLyricPreview, "preview remains available"); f.Module.CancelLyricPreview();
                lyricList.ScrollIntoView(f.Module.Lyrics[2]); lyricList.UpdateLayout();
                var previewRow = (FrameworkElement)lyricList.ItemContainerGenerator.ContainerFromIndex(2);
                var playLine = Visuals(previewRow).OfType<Button>().Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) == "播放此处" && ReferenceEquals(b.Tag, f.Module.Lyrics[2]));
                playLine.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                Equal(10d, f.Playback.PositionSeconds);
                True(f.Playback.IsPlaying, "inline lyric control seeks and resumes real playback port");
                Equal(opens, f.Playback.Opens.Count);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
        yield return Case("lyric centering settles under continuous 200ms positions and keyboard preview keeps its hold", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = new HwndSource(new HwndSourceParameters("lyric-follow-tick-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 860, Height = 600
            });
            try
            {
                source.RootVisual = detail;
                f.Module.Lyrics.Clear();
                for (var i = 0; i < 80; i++)
                    f.Module.Lyrics.Add(new(i * 2, "连续进度与长行排版 " + i,
                        i == 40 ? "A translated line that wraps with the lyric viewport width." : ""));
                detail.Measure(new(860, 600)); detail.Arrange(new(0, 0, 860, 600)); detail.UpdateLayout();
                await Drain();
                var list = (MusicLyricList)detail.FindName("LyricList");
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                Equal(f.Module.AcceptedPlaybackRate, list.PlaybackRate);
                Equal(f.Module.PlaybackPresentationKey, list.PresentationKey);
                double Error(int index)
                {
                    var row = list.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                    return row is null ? double.PositiveInfinity : Math.Abs(
                        row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y -
                        scroll.ViewportHeight * (f.Module.IsPlaying ? .24 : .35));
                }
                await f.Playback.SeekAsync(80);
                for (var i = 1; i <= 3; i++)
                {
                    await Task.Delay(200);
                    await f.Playback.SeekAsync(80 + i * .2);
                    await Drain();
                }
                True(Error(40) < 2, "same target finishes its 420ms motion while real positions continue every 200ms");
                var settled = scroll.VerticalOffset;
                await Task.Delay(200); await f.Playback.SeekAsync(80.8); await Drain();
                True(Math.Abs(scroll.VerticalOffset - settled) < .5, "another position on the same line leaves centering settled");
                await f.Playback.SeekAsync(82);
                for (var i = 1; i <= 3; i++)
                {
                    await Task.Delay(200); await f.Playback.SeekAsync(82 + i * .2); await Drain();
                }
                True(Error(41) < 2, "a different line receives its own completed motion");
                var activeRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(41);
                Visuals(activeRow).OfType<TextBlock>().Single(text => text.Text == f.Module.Lyrics[41].Text).FontSize = 28;
                await Task.Delay(550); await Drain();
                True(Error(41) < 2, "changed row text metrics recenter without a new playback position");

                var hold = System.Diagnostics.Stopwatch.StartNew();
                list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.PageDown)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                list.ScrollIntoView(f.Module.Lyrics[20]); detail.UpdateLayout(); await Drain();
                var previewRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(20);
                var preview = Visuals(previewRow).OfType<Button>()
                    .Single(button => ReferenceEquals(button.Tag, f.Module.Lyrics[20]) && button.Content is StackPanel);
                SimulateFocus(preview, UIElement.IsKeyboardFocusWithinProperty, true);
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                var held = scroll.VerticalOffset;
                await f.Playback.SeekAsync(20); await Task.Delay(650); await Drain();
                True(f.Module.HasLyricPreview && f.Module.LyricDisplayIndex == 20, "keyboard preview survives real ticks");
                True(Math.Abs(scroll.VerticalOffset - held) < 2, "preview and focus do not restart automatic centering");
                await Task.Delay(2450); await Drain();
                True(f.Module.HasLyricPreview, "the manual hold lasts four seconds");
                await Until(() => !f.Module.HasLyricPreview && Error(10) < 2);
                True(hold.Elapsed >= TimeSpan.FromSeconds(4) && hold.Elapsed < TimeSpan.FromSeconds(6),
                    "four-second hold plus centering settles within the interaction deadline");
                True(Error(10) < 2, "four-second recovery follows the actual line without another media tick");

                await f.Module.StepAsync(true);
                await Drain();
                Equal(f.Module.PlaybackPresentationKey, list.PresentationKey);
                f.Module.Lyrics.Clear();
                for (var i = 0; i < 20; i++) f.Module.Lyrics.Add(new(i * 2, "替换身份 " + i, ""));
                await f.Playback.SeekAsync(6); await Task.Delay(550); await Drain();
                True(Error(3) < 2, "track replacement retires the previous target and hold");
            }
            finally
            {
                source.RootVisual = null;
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                await Drain();
            }
        });
        yield return Case("lyric edges follow actual items through recycling snapshot changes and playback", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = new HwndSource(new HwndSourceParameters("lyric-edge-controls-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 860, Height = 600
            });
            var list = (ListBox)detail.FindName("LyricList");
            var seen = new Dictionary<ListBoxItem, object>(ReferenceEqualityComparer.Instance);
            var recycled = false;
            void Layout()
            { detail.Measure(new(860, 600)); detail.Arrange(new Rect(0, 0, 860, 600)); detail.UpdateLayout(); }
            void AssertEdges(int count)
            {
                var rows = Visuals(list).OfType<ListBoxItem>()
                    .Where(row => list.ItemContainerGenerator.IndexFromContainer(row) >= 0).ToArray();
                True(rows.Length > 0 && rows.Length < Math.Min(count, 64),
                    $"realized lyric containers stay viewport bounded: {rows.Length}/{count}");
                var anchor = f.Module.IsPlaying ? .24 : .35;
                foreach (var row in rows)
                {
                    var first = ReferenceEquals(row.DataContext, f.Module.Lyrics[0]);
                    var last = ReferenceEquals(row.DataContext, f.Module.Lyrics[^1]);
                    var top = first ? Math.Max(24, list.ActualHeight * anchor) : 0;
                    var bottom = last ? Math.Max(24, list.ActualHeight * (1 - anchor)) : 0;
                    True(Math.Abs(row.Margin.Top - top) < .01 && Math.Abs(row.Margin.Bottom - bottom) < .01,
                        $"only actual edge items have space: index={list.ItemContainerGenerator.IndexFromContainer(row)}, " +
                        $"actual={row.Margin}, expected={top}/{bottom}");
                    if (seen.TryGetValue(row, out var prior) && !ReferenceEquals(prior, row.DataContext)) recycled = true;
                    seen[row] = row.DataContext;
                }
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                True(Math.Abs(scroll.ViewportHeight - list.ActualHeight) < 1, "edge space never shrinks the viewport");
            }
            async Task Visit(int index)
            {
                list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.PageDown)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                list.ScrollIntoView(f.Module.Lyrics[index]); Layout(); await Drain(); Layout();
                try
                {
                    await Until(() =>
                    {
                        Layout();
                        return list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement;
                    });
                }
                catch (TimeoutException error)
                {
                    var scroll = Visuals(list).OfType<ScrollViewer>().First();
                    var rows = Visuals(list).OfType<ListBoxItem>()
                        .Select(row => list.ItemContainerGenerator.IndexFromContainer(row)).ToArray();
                    throw new TimeoutException($"Lyric visit did not settle: count={list.Items.Count}, index={index}, " +
                        $"realized={string.Join(',', rows)}, offset={scroll.VerticalOffset}, " +
                        $"scrollable={scroll.ScrollableHeight}", error);
                }
                Layout(); AssertEdges(f.Module.Lyrics.Count);
            }
            async Task AssertCentered(int index)
            {
                Layout();
                var scroll = Visuals(list).OfType<ScrollViewer>().First();
                try
                {
                    await Until(() =>
                    {
                        Layout();
                        var row = list.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                        var anchor = f.Module.IsPlaying ? .24 : .35;
                        return f.Module.LyricDisplayIndex == index && row is not null &&
                            Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y -
                                scroll.ViewportHeight * anchor) < 2;
                    });
                }
                catch (TimeoutException error)
                {
                    var row = list.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                    var anchor = f.Module.IsPlaying ? .24 : .35;
                    throw new TimeoutException($"Lyric edge follow did not settle: count={list.Items.Count}, " +
                        $"index={index}, active={f.Module.LyricDisplayIndex}, playing={f.Module.IsPlaying}, " +
                        $"actual={row?.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y}, " +
                        $"target={scroll.ViewportHeight * anchor}, margin={row?.Margin}, " +
                        $"offset={scroll.VerticalOffset}, scrollable={scroll.ScrollableHeight}", error);
                }
                AssertEdges(f.Module.Lyrics.Count);
            }
            try
            {
                source.RootVisual = detail; Layout(); await Drain();
                foreach (var count in new[] { 60, 120, 1000 })
                {
                    seen.Clear(); recycled = false;
                    f.Module.Lyrics.Clear();
                    for (var index = 0; index < count; index++)
                        f.Module.Lyrics.Add(new(index * .1, "歌词行 " + index, ""));
                    f.Module.PreviewLyric(f.Module.Lyrics[0]); Layout(); await Drain();
                    foreach (var index in new[] { 0, count - 1, count / 2, 0 }) await Visit(index);
                    True(recycled, "scrolling one snapshot reuses actual containers for different lyric items");
                    f.Module.Lyrics.Move(0, count / 2); Layout(); await Drain();
                    foreach (var index in new[] { 0, count / 2, count - 1 }) await Visit(index);
                    f.Module.Lyrics.RemoveAt(count - 1); Layout(); await Drain(); await Visit(count - 2);
                    f.Module.Lyrics.RemoveAt(0); Layout(); await Drain(); await Visit(0);
                    f.Module.Lyrics.Insert(0, new(0, "插入首行", ""));
                    f.Module.Lyrics.Add(new((count - 1) * .1, "替换末行", ""));
                    // Equal row values still represent new item identities.
                    f.Module.Lyrics[0] = f.Module.Lyrics[0] with { };
                    f.Module.Lyrics[^1] = f.Module.Lyrics[^1] with { };
                    Layout(); await Drain();
                    foreach (var index in new[] { 0, count - 1 }) await Visit(index);
                    await f.Module.StepAsync(true);
                    f.Module.Lyrics.Clear();
                    for (var index = 0; index < count; index++)
                        f.Module.Lyrics.Add(new(index * .1, "换歌歌词行 " + index, ""));
                    f.Module.PreviewLyric(f.Module.Lyrics[0]); Layout(); await Drain();
                    foreach (var index in new[] { count - 1, count / 2, 0 }) await Visit(index);
                    f.Module.CancelLyricPreview();
                    await f.Playback.SeekAsync(f.Module.Lyrics[^1].Time); await AssertCentered(count - 1);
                    await f.Playback.ResumeAsync(); await AssertCentered(count - 1);
                    await f.Playback.PauseAsync(); await AssertCentered(count - 1);
                    await f.Playback.SeekAsync(0); await AssertCentered(0);
                }
                f.Module.Lyrics.Clear();
                var only = new MusicLyricRow(0, "单行歌词", "");
                f.Module.Lyrics.Add(only); f.Module.PreviewLyric(only); Layout(); await Drain();
                await Until(() => list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem);
                var single = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                True(single.Margin.Top > 0 && single.Margin.Bottom > 0, "one actual lyric is both boundaries");
                f.Module.Lyrics.Clear(); Layout(); await Drain();
                True(f.Module.FirstLyric is null && f.Module.LastLyric is null, "empty snapshots retire both boundaries");
            }
            finally
            {
                source.RootVisual = null;
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); await Drain();
            }
        });
    }
    private static RenderTargetBitmap CaptureSeekFrame(FrameworkElement root, double scale)
    {
        var frame = new RenderTargetBitmap((int)(400 * scale), (int)(96 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        frame.Render(root); return frame;
    }
    private static Color SeekPixel(BitmapSource frame, double x, double y)
    {
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect((int)(x * frame.DpiX / 96), (int)(y * frame.DpiY / 96), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    private static void SaveSeekFrame(BitmapSource frame, string fileName)
    {
        string path = TestWorkspace.ValidatePath(TestWorkspace.ResultsRoot,
            Path.Combine(TestWorkspace.ResultsRoot, "windows-native", fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); encoder.Save(stream);
        Console.WriteLine("VISUAL " + path);
    }
    private sealed class CallbackArtworkLoader(Func<string, CancellationToken, Task<IArtworkLease>> acquire) : IArtworkLoader
    {
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default) =>
            acquire(address, cancellationToken);
    }
    private sealed class HeldPlayerArtwork : IArtworkLoader
    {
        internal readonly List<(TaskCompletionSource<IArtworkLease> Completion, CancellationToken Token)> Calls = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((completion, cancellationToken)); return completion.Task;
        }
    }
    // Explicit deterministic artwork fixture, never packaged into the product or fetched from a platform.
    private sealed class PlayerArtworkFixture : IArtworkLoader
    {
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Colors.SteelBlue, Colors.DarkSalmon, 45), null, new Rect(0, 0, 256, 256));
                dc.DrawEllipse(Brushes.LightGoldenrodYellow, null, new(168, 86), 38, 38);
                dc.DrawGeometry(Brushes.DarkSlateBlue, null, Geometry.Parse("M0,256 L0,200 86,105 175,210 256,135 256,256 Z"));
            }
            var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            return Task.FromResult<IArtworkLease>(new PlayerArtworkLease(bitmap));
        }
    }
    private sealed class PlayerArtworkLease(BitmapSource image) : IArtworkLease
    {
        public BitmapSource Image => image;
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
