using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicPlayerKeyboardTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PlayerKeyboardCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> PlayerKeyboardCases()
    {
        foreach (bool reset in new[] { false, true })
            yield return Case("player channel Escape preserves the channel below " + (reset ? "reset confirmation" : "close confirmation"), () =>
                WithPlayerKeyboard(async (f, detail, source) =>
                {
                    var shell = (ShellWindow)Window.GetWindow(detail);
                    var host = (PlayerDialogHost)shell.FindName("ChannelDialogHost");
                    var target = new ContentControl { Focusable = true };
                    host.Show(detail, new Border { Child = target, Width = 368, Height = 200 }, () => { });
                    Control confirmation;
                    if (reset)
                    {
                        var dialog = (UserDataResetDialog)shell.FindName("ResetDialog");
                        dialog.Open(); confirmation = dialog;
                    }
                    else
                    {
                        var dialog = (CloseWindowDialog)shell.FindName("CloseDialog");
                        dialog.Open(); confirmation = dialog;
                    }
                    Render((FrameworkElement)source.RootVisual, 1110, 800); await Drain();
                    var escape = new KeyEventArgs(new PlayerKeyboardProbe(), source, Environment.TickCount, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    ((UIElement)confirmation.FindName("CancelChoice")).RaiseEvent(escape);
                    await Drain();
                    True(escape.Handled && !confirmation.IsVisible, "only the top confirmation handles Escape and closes");
                    True(host.IsOpen && detail.IsVisible && f.Module.IsLyrics, "channel and full player stay open");
                    Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                }, keepShellOwner: true));

        yield return Case("player channel keyboard blocks underlying search navigation and stale Space focus", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var shell = (ShellWindow)Window.GetWindow(detail);
                var host = (PlayerDialogHost)shell.FindName("ChannelDialogHost");
                var target = new TextBox { Text = "频道", Width = 160 };
                host.Show(detail, new Border { Child = target, Width = 368, Height = 200 }, () => { });
                Render((FrameworkElement)source.RootVisual, 1110, 800); await Drain();
                var search = (TextBox)shell.FindName("SearchBox");
                True(search.IsVisible && search.IsEnabled, "the underlying search entry is available to the shell shortcut");
                search.Text = "header-search"; search.Select(2, 0);
                FocusManager.SetFocusedElement(host, target);
                var focus = Keyboard.FocusedElement;
                var find = new KeyEventArgs(new PlayerKeyboardProbe(ModifierKeys.Control), source, Environment.TickCount, Key.F)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(find);
                True(find.Handled && ReferenceEquals(focus, Keyboard.FocusedElement) &&
                    ReferenceEquals(target, FocusManager.GetFocusedElement(host)), "Ctrl+F cannot move channel focus to SearchBox");
                Equal(2, search.SelectionStart); Equal(0, search.SelectionLength);
                var back = new KeyEventArgs(new PlayerKeyboardProbe(ModifierKeys.Alt), source, Environment.TickCount, Key.Left)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(back); await Drain();
                True(back.Handled && host.IsOpen && f.Module.IsLyrics, "Alt+Left cannot navigate below the channel");
                var play = (Button)((MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content).FindName("PlayButton");
                var keyboard = new PlayerKeyboardProbe();
                True(PlayerKey(play, source, keyboard, down: true).Handled, "stale underlying focus cannot arm Space");
                True(PlayerKey(play, source, keyboard, down: false).Handled, "its release is also blocked");
                await Drain(); Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                host.Close();
            }, keepShellOwner: true));

        yield return Case("player channel header pointer consumes application controls but admits system chrome", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var shell = Window.GetWindow(f.Module.PlayerView);
                var host = (PlayerDialogHost)shell.FindName("ChannelDialogHost");
                var root = (FrameworkElement)source.RootVisual;
                var footer = (FrameworkElement)f.Module.PlayerView;
                using var pointer = new PlaybackPointer(root, source);
                var back = (Button)shell.FindName("BackButton");
                back.IsEnabled = true;
                var search = (TextBox)shell.FindName("SearchBox");
                var settings = (Button)shell.FindName("SettingsButton");
                int clicks = 0;
                back.Click += (_, _) => clicks++;
                settings.Click += (_, _) => clicks++;
                void Open()
                {
                    host.Show(footer, new Border { Width = 368, Height = 200 }, () => { });
                    Render(root, 1110, 800);
                }
                foreach (var control in new FrameworkElement[] { search, back, settings })
                {
                    Open();
                    var point = pointer.Center(control);
                    True(pointer.Hit(point) is DependencyObject hit &&
                        (ReferenceEquals(control, hit) || control.IsAncestorOf(hit)), "the real root hits the exposed application header control");
                    var focus = Keyboard.FocusedElement;
                    True(pointer.Button(point, true) && host.IsOpen, "header Down is consumed without removing the barrier");
                    True(ReferenceEquals(focus, Keyboard.FocusedElement), "header Down cannot move focus below the channel");
                    True(pointer.Button(point, false), "matching header Up is consumed");
                    await Drain();
                    True(!host.IsOpen, "only the channel closes after the complete header gesture");
                    Equal(0, clicks);
                }
                Open();
                var maximize = (Button)shell.FindName("MaximizeButton");
                int admitted = 0;
                MouseButtonEventHandler probe = (_, e) =>
                {
                    True(!e.Handled, "the shell leaves system caption input available");
                    admitted++;
                    e.Handled = true; // Observe admission without issuing an OS window command from this hidden fixture.
                };
                maximize.PreviewMouseDown += probe; maximize.PreviewMouseUp += probe;
                try
                {
                    var point = pointer.Center(maximize);
                    True(pointer.Hit(point) is DependencyObject hit &&
                        (ReferenceEquals(maximize, hit) || maximize.IsAncestorOf(hit)), "root hit-test reaches the real system caption button");
                    pointer.Click(point); await Drain();
                    Equal(2, admitted);
                    True(host.IsOpen && !host.HasOutsidePress, "system chrome neither dismisses the channel nor starts its capture");
                }
                finally { maximize.PreviewMouseDown -= probe; maximize.PreviewMouseUp -= probe; host.Close(); }
                await f.Module.OpenPlayerDetailAsync();
                await Until(() => f.Module.IsLyrics && detail.IsVisible);
                var detailFooter = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                host.Show(detailFooter, new Border { Width = 368, Height = 200 }, () => { });
                Render(root, 1110, 800); await Drain();
                var collapse = (Button)shell.FindName("PlayerDetailBackButton");
                var collapsePoint = pointer.Center(collapse);
                True(pointer.Hit(collapsePoint) is DependencyObject collapseHit &&
                    (ReferenceEquals(collapse, collapseHit) || collapse.IsAncestorOf(collapseHit)), "full-player header back participates in the root hit-test");
                True(pointer.Button(collapsePoint, true) && host.IsOpen, "full-player header Down keeps the channel barrier");
                True(pointer.Button(collapsePoint, false), "full-player header Up is consumed"); await Drain();
                True(!host.IsOpen && detail.IsVisible && f.Module.IsLyrics, "full-player header click only dismisses the channel");
            }, keepShellOwner: true, openDetail: false));

        yield return Case("player channel keyboard owns Space and Escape above queue and full player", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var shell = (ShellWindow)Window.GetWindow(detail);
                var host = (PlayerDialogHost)shell.FindName("ChannelDialogHost");
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                await f.Module.OpenPlayQueueAsync(); await Drain();
                var queue = (FrameworkElement)shell.FindName("QueueSurface");
                True(queue.IsVisible && detail.IsVisible, "both underlying player surfaces are open");
                var target = new ContentControl { Content = "频道", Focusable = true };
                var panel = new Border { Child = target, Width = 368, Height = 200 };
                int closes = 0;
                host.Show(footer, panel, () => closes++);
                Render((FrameworkElement)source.RootVisual, 1110, 800); await Drain();
                var keyboard = new PlayerKeyboardProbe();
                True(!PlayerKey(target, source, keyboard, down: true).Handled, "channel content keeps its Space press");
                PlayerKey(target, source, keyboard, down: false); await Drain();
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                True(!MediaShortcutInput.AllowsLocal(shell, source, target), "the channel also protects the shell media-shortcut route");
                var escape = new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(escape); await Drain();
                True(escape.Handled && !host.IsOpen, "Escape retires the highest layer");
                Equal(1, closes);
                True(queue.IsVisible && detail.IsVisible && f.Module.IsLyrics, "the same Escape cannot close queue or full player");
            }, keepShellOwner: true));

        yield return Case("player keyboard Space toggles through the visible detail route only", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var keyboard = new PlayerKeyboardProbe();
                var lyrics = (ListBox)detail.FindName("LyricList");
                int opens = f.Playback.Opens.Count;
                True(PlayerKey(lyrics, source, keyboard, down: true).Handled, "detail accepts bare Space");
                True(PlayerKey(lyrics, source, keyboard, down: false).Handled, "detail consumes its release");
                await Until(() => !f.Module.Busy);
                Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                True(!f.Module.IsPlaying, "the existing music action pauses the current media");

                PlayerKey(lyrics, source, keyboard, down: true);
                PlayerKey(lyrics, source, keyboard, down: false);
                await Until(() => !f.Module.Busy);
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
                Equal(opens, f.Playback.Opens.Count);
                True(f.Module.IsPlaying, "a new press resumes the same media");

                var ordinary = (Grid)f.Module.PlayerView.FindName("MetadataPanel");
                True(!PlayerKey(ordinary, source, keyboard, down: true).Handled, "ordinary footer has no bare-Space action");
                PlayerKey(ordinary, source, keyboard, down: false);
                await Drain();
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
            }));

        yield return Case("player keyboard focused play button cannot arm a second Space KeyUp click", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                var play = (Button)footer.FindName("PlayButton");
                var keyboard = new PlayerKeyboardProbe();
                int clicks = 0;
                play.Click += (_, _) => clicks++;
                Equal(ClickMode.Release, play.ClickMode);
                SimulateFocus(play, UIElement.IsKeyboardFocusedProperty, true);
                try
                {
                    True(PlayerKey(play, source, keyboard, down: true).Handled, "preview owns the focused button press");
                    True(!play.IsPressed && !play.IsMouseCaptured, "ButtonBase never arms its native Space gesture");
                    await Until(() => !f.Module.Busy);
                    Equal(1, f.Playback.Pauses);
                    True(PlayerKey(play, source, keyboard, down: false).Handled, "paired KeyUp is consumed after playback settles");
                    await Drain();
                    Equal(0, clicks); Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                    True(!play.IsPressed && !play.IsMouseCaptured, "release leaves no native button gesture");
                }
                finally { SimulateFocus(play, UIElement.IsKeyboardFocusedProperty, false); }
            }));

        yield return Case("player keyboard held Space is consumed while the accepted toggle is queued", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var keyboard = new PlayerKeyboardProbe();
                var lyrics = (ListBox)detail.FindName("LyricList");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = f.Module.RunAsync(() => release.Task);
                try
                {
                    True(f.Module.Busy && f.Module.CanTogglePlayback, "active media retains playback admission during unrelated work");
                    True(PlayerKey(lyrics, source, keyboard, down: true).Handled, "initial press is admitted");
                    for (int i = 0; i < 4; i++)
                    {
                        var repeat = PlayerKey(lyrics, source, keyboard, down: true, repeat: true);
                        True(repeat.IsRepeat && repeat.Handled, "every held-key repeat is consumed");
                    }
                    Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                }
                finally { release.TrySetResult(); await pending; }
                await Until(() => !f.Module.Busy);
                Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                True(PlayerKey(lyrics, source, keyboard, down: false).Handled, "one release retires the held press");
                PlayerKey(lyrics, source, keyboard, down: true);
                PlayerKey(lyrics, source, keyboard, down: false);
                await Until(() => !f.Module.Busy);
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
            }));

        yield return Case("player keyboard preserves text password and editable combo input", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var editors = new StackPanel();
                // The App selector template has no editor; use WPF's real editable template here.
                var combo = new ComboBox { IsEditable = true, Width = 160, Style = new Style(typeof(ComboBox)) };
                combo.Items.Add("editable");
                UIElement[] controls = [new TextBox(), new RichTextBox(), new PasswordBox(), combo];
                foreach (var control in controls) editors.Children.Add(control);
                var layout = (Grid)detail.Content;
                layout.Children.Add(editors);
                try
                {
                    Render(detail, 1110, 800); await Drain(); combo.ApplyTemplate();
                    var comboText = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox
                        ?? throw new InvalidOperationException("The editable ComboBox must expose its real text editor");
                    var keyboard = new PlayerKeyboardProbe();
                    int previews = 0;
                    KeyEventHandler observe = (_, e) =>
                    {
                        if (e.Key != Key.Space) return;
                        True(!e.Handled, "detail leaves the editor preview available to its owner");
                        previews++;
                    };
                    detail.AddHandler(Keyboard.PreviewKeyDownEvent, observe, true);
                    try
                    {
                        foreach (var control in controls.Append(comboText))
                        {
                            PlayerKey(control, source, keyboard, down: true);
                            PlayerKey(control, source, keyboard, down: false);
                        }
                        Equal(5, previews);
                    }
                    finally { detail.RemoveHandler(Keyboard.PreviewKeyDownEvent, observe); }
                    await Drain();
                    Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                    True(f.Module.IsPlaying, "editing input does not reach media playback");
                }
                finally { layout.Children.Remove(editors); }
            }));

        yield return Case("player keyboard leaves menu and separate Popup input with their owners", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var layout = (Grid)detail.Content;
                var menu = new Menu();
                var item = new MenuItem { Header = "menu action" };
                menu.Items.Add(item);
                var popupBody = new StackPanel();
                var popupEditor = new TextBox();
                var popupTarget = new ContentControl { Content = "popup content", Focusable = true };
                popupBody.Children.Add(popupEditor); popupBody.Children.Add(popupTarget);
                var popup = new Popup { Child = popupBody, PlacementTarget = detail };
                layout.Children.Add(menu); layout.Children.Add(popup);
                try
                {
                    Render(detail, 1110, 800); await Drain();
                    var keyboard = new PlayerKeyboardProbe();
                    True(!PlayerKey(item, source, keyboard, down: true).Handled, "menu preview remains with the menu");
                    PlayerKey(item, source, keyboard, down: false);
                    // Give the Popup child a separate, invisible HWND without opening an OS popup.
                    using var popupSource = PlayerKeyboardSource("player-keyboard-popup-fixture");
                    popupSource.RootVisual = popupBody;
                    try
                    {
                        Render(popupBody, 320, 120); await Drain();
                        foreach (var target in new UIElement[] { popupEditor, popupTarget })
                        {
                            True(!PlayerKey(target, popupSource, keyboard, down: true).Handled,
                                "separate Popup input is not owned by the detail shortcut");
                            PlayerKey(target, popupSource, keyboard, down: false);
                        }
                        await Drain();
                        Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                        True(!popup.IsOpen, "fixture never opens or activates a native popup");
                    }
                    finally { popupSource.RootVisual = null; }
                }
                finally { layout.Children.Remove(menu); layout.Children.Remove(popup); }
            }));

        yield return Case("player keyboard hidden and closing detail reject Space and retire held input", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var keyboard = new PlayerKeyboardProbe();
                var lyrics = (ListBox)detail.FindName("LyricList");
                PlayerKey(lyrics, source, keyboard, down: true);
                await Until(() => !f.Module.Busy);
                Equal(1, f.Playback.Pauses);
                detail.Visibility = Visibility.Hidden;
                PlayerKey(lyrics, source, keyboard, down: false);
                PlayerKey(lyrics, source, keyboard, down: true);
                PlayerKey(lyrics, source, keyboard, down: false);
                await Drain();
                Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);

                detail.Visibility = Visibility.Visible;
                True(PlayerKey(lyrics, source, keyboard, down: true, repeat: true).Handled,
                    "a hold entering a revealed detail is consumed without becoming a new press");
                PlayerKey(lyrics, source, keyboard, down: false);
                await Drain();
                Equal(0, f.Playback.Resumes);
                PlayerKey(lyrics, source, keyboard, down: true);
                PlayerKey(lyrics, source, keyboard, down: false);
                await Until(() => !f.Module.Busy);
                Equal(1, f.Playback.Resumes);

                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                True(detail.IsVisible && !f.Module.IsLyrics, "closing admission precedes the visual exit");
                True(!PlayerKey(lyrics, source, keyboard, down: true).Handled, "retired detail rejects a new press");
                PlayerKey(lyrics, source, keyboard, down: false);
                await Drain();
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
            }));

        yield return Case("player keyboard modifiers preserve their own Space input", () =>
            WithPlayerKeyboard(async (f, detail, source) =>
            {
                var lyrics = (ListBox)detail.FindName("LyricList");
                foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift,
                    ModifierKeys.Windows, ModifierKeys.Control | ModifierKeys.Alt })
                {
                    var keyboard = new PlayerKeyboardProbe(modifiers);
                    // WPF reports Alt/Ctrl/Shift here, but Win state is queried separately.
                    Equal(modifiers & ~ModifierKeys.Windows, keyboard.Modifiers);
                    Equal(modifiers.HasFlag(ModifierKeys.Windows), keyboard.IsKeyDown(Key.LWin));
                    True(!PlayerKey(lyrics, source, keyboard, down: true).Handled, "modified preview is not a media command");
                    PlayerKey(lyrics, source, keyboard, down: false);
                }
                await Drain();
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
            }));

        yield return Case("player keyboard requires playback admission and rejects shutdown input", async () =>
        {
            await WithPlayerKeyboard(async (f, detail, source) =>
            {
                var keyboard = new PlayerKeyboardProbe();
                var lyrics = (ListBox)detail.FindName("LyricList");
                True(!f.Module.CanTogglePlayback, "empty detail has no playable current occurrence");
                True(!PlayerKey(lyrics, source, keyboard, down: true).Handled, "unavailable playback is not admitted");
                PlayerKey(lyrics, source, keyboard, down: false);
                await Drain();
                Equal(0, f.Playback.Opens.Count); Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
            }, withCurrent: false);
            await WithPlayerKeyboard(async (f, detail, source) =>
            {
                await f.Module.PrepareShutdownAsync();
                True(!f.Module.CanTogglePlayback, "shutdown seals playback admission");
                var keyboard = new PlayerKeyboardProbe();
                var lyrics = (ListBox)detail.FindName("LyricList");
                PlayerKey(lyrics, source, keyboard, down: true);
                PlayerKey(lyrics, source, keyboard, down: false);
                await Drain();
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
            });
        });
    }

    private static async Task WithPlayerKeyboard(
        Func<Fixture, MusicPlayerDetailView, HwndSource, Task> body, bool withCurrent = true, bool keepShellOwner = false,
        bool openDetail = true)
    {
        await using var f = await Fixture.Create();
        if (withCurrent)
        {
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            f.Playback.Start(); await Drain();
        }
        var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
        try
        {
            shell.PrepareForRender();
            var root = (Border)shell.Content;
            if (!keepShellOwner) shell.Content = null;
            root.Resources.MergedDictionaries.Add(shell.Resources); root.DataContext = shell.DataContext;
            using var source = PlayerKeyboardSource("player-keyboard-detail-fixture");
            source.RootVisual = root;
            try
            {
                Render(root, 1110, 800); await Drain();
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                if (openDetail)
                {
                    await f.Module.OpenPlayerDetailAsync();
                    await Until(() => f.Module.IsLyrics && detail.IsVisible);
                }
                await body(f, detail, source);
            }
            finally { source.RootVisual = null; }
        }
        finally { shell.Close(); await shell.ShutdownCompletion; }
    }

    private static HwndSource PlayerKeyboardSource(string name) => new(new HwndSourceParameters(name)
    {
        WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
        PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
    });

    private static KeyEventArgs PlayerKey(UIElement target, PresentationSource source, KeyboardDevice keyboard,
        bool down, bool repeat = false)
    {
        var preview = new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Space)
        { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent };
        // IsRepeat is supplied by WPF's keyboard device and has no public setter.
        typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, [repeat]);
        target.RaiseEvent(preview);
        // WPF promotes only unhandled previews. Exercise the actual control handlers without OS input injection.
        if (!preview.Handled)
            target.RaiseEvent(new KeyEventArgs(keyboard, source, preview.Timestamp, Key.Space)
            { RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent });
        return preview;
    }

    private sealed class PlayerKeyboardProbe(ModifierKeys modifiers = ModifierKeys.None) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            Key.LeftShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            Key.LWin when modifiers.HasFlag(ModifierKeys.Windows) => KeyStates.Down,
            _ => KeyStates.None
        };
    }
}
