using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Key = System.Windows.Input.Key;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using KeyInterop = System.Windows.Input.KeyInterop;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class MediaControlSettingsUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Media control settings UI: eight rows align at default and minimum shell widths with registration errors",
            () => TestUiHost.InvokeAsync(Layout));
        yield return new("Media control settings UI: keyboard recording clear cancel and modifier input preserve authority",
            () => TestUiHost.InvokeAsync(Recording));
        yield return new("Media control settings UI: recorder consumes input before ancestor navigation and after completion",
            () => TestUiHost.InvokeAsync(InputBoundary));
        yield return new("Media control settings UI: a saved held candidate suppresses native registration until release",
            () => TestUiHost.InvokeAsync(RecordingRegistration));
        yield return new("Media control settings UI: failed and conflicting candidates restore saved keys and expose errors",
            () => TestUiHost.InvokeAsync(Failures));
        yield return new("Media control settings UI: focus hide unload and pending switch save retire recording",
            () => TestUiHost.InvokeAsync(Lifecycle));
        yield return new("Media control settings UI: shell receives the explicit settings owner",
            () => TestUiHost.InvokeAsync(ShellInjection));
    }

    private static async Task Layout()
    {
        await using var owner = new AppMediaControlSettings(new DocumentStore());
        await using var appearance = new AppAppearance(new DocumentStore());
        await owner.LoadAsync(); await appearance.LoadAsync();
        var general = new GeneralSettingsView(appearance, [], mediaControls: owner);
        using var mount = new Mount(general);
        var area = (RadioButton)general.FindName("ShortcutsArea");
        Check(area.Visibility == Visibility.Visible);
        area.IsChecked = true;
        await Drain();
        var view = (MediaControlSettingsView)((ContentControl)general.FindName("ShortcutsPage")).Content;
        Check(((ToggleSwitch)view.FindName("GlobalSwitch")).IsOn == true);
        Check(((ToggleSwitch)view.FindName("SystemMediaSwitch")).IsOn == true);
        var items = (ItemsControl)view.FindName("ShortcutRows");
        Check(items.Items.Count == 8);
        Check(Visuals(view).OfType<Button>().Count(button => DesktopVisual.IsVisible(button) && (button.Tag is "local" or "global") &&
            button.Content is TextBlock) == 15, "translation has no global editor");
        foreach (var action in Enum.GetValues<MediaShortcutAction>())
        {
            var local = Editor(view, action);
            Check(local.IsEnabled && local.IsTabStop && AutomationProperties.GetName(local).Length > 0);
            Check(((TextBlock)local.Content).Text == MediaShortcutGestures.Display(owner.Preferences.Bindings.Single(binding => binding.Action == action).Local));
        }
        int refreshes = 0; owner.RegistrationRefreshRequested += (_, _) => refreshes++;
        Click((Button)view.FindName("RefreshButton")); Check(refreshes == 1);
        var layoutFailures = new List<string>();
        void CheckLayout(bool value, string message)
        {
            if (!value) layoutFailures.Add(message);
        }
        foreach (bool occupied in new[] { false, true })
        {
            // The UI receives the attachment's reported error; it never infers
            // an OS conflict from a stored gesture or from an internal duplicate.
            owner.SetGlobalErrors(occupied ? new Dictionary<MediaShortcutAction, string>
            {
                [MediaShortcutAction.TogglePlayback] = "热键被占用",
                [MediaShortcutAction.Next] = "快捷键注册失败（错误 5）"
            } : new Dictionary<MediaShortcutAction, string>());
            foreach (var (width, name) in new[] { (1110 - 203, "default"), (860 - 203, "minimum") })
            {
                mount.Layout(width, 680);
                await Drain();
                string scenario = $"{name}/{(occupied ? "occupied" : "normal")}, width={width}";
                // Keep all four previews even when a layout assertion fails.
                await GeneralAppearanceTests.SavePreview(general,
                    $"media-shortcuts-{name}-{(occupied ? "occupied" : "normal")}", width, 680);
                var scroller = (ScrollViewer)general.FindName("SettingsScroller");
                CheckLayout(scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroller.ScrollableWidth == 0,
                    $"{scenario}: horizontal scrolling is disabled; actual={scroller.HorizontalScrollBarVisibility}, scrollable={scroller.ScrollableWidth:F2}");
                var origin = view.TransformToVisual(general).TransformPoint(new Point());
                CheckLayout(Math.Abs(origin.X - 32) < 1, $"{scenario}: General left gutter is 32px; actual={origin.X:F2}");
                foreach (var button in Visuals(view).OfType<Button>().Where(button => DesktopVisual.IsVisible(button)))
                {
                    var position = button.TransformToVisual(view).TransformPoint(new Point());
                    string control = AutomationProperties.GetName(button);
                    if (control.Length == 0) control = button.Content.ToString()!;
                    CheckLayout(position.X >= -1 && position.X + button.ActualWidth <= view.ActualWidth + 1,
                        $"{scenario}, {control}: control remains inside content; x={position.X:F2}, button={button.ActualWidth:F2}, content={view.ActualWidth:F2}");
                    if (button.Content is TextBlock text)
                    {
                        var probe = new TextBlock { Text = text.Text, FontFamily = text.FontFamily,
                            FontSize = text.FontSize, FontWeight = text.FontWeight, FontStyle = text.FontStyle };
                        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double required = probe.DesiredSize.Width;
                        var textOrigin = text.TransformToVisual(button).TransformPoint(new Point());
                        double contentRight = button.ActualWidth - button.Padding.Right - button.BorderThickness.Right;
                        CheckLayout(text.ActualWidth + 1 >= required &&
                            textOrigin.X >= button.Padding.Left + button.BorderThickness.Left - 1 &&
                            textOrigin.X + required <= contentRight + 1,
                            $"{scenario}, {control}, text=\"{text.Text}\": default gesture fits without ellipsis; " +
                            $"actualText={text.ActualWidth:F2}, required={required:F2}, textX={textOrigin.X:F2}, " +
                            $"contentRight={contentRight:F2}, button={button.ActualWidth:F2}, font={text.FontFamily.Source}/{text.FontSize}");
                    }
                }
                var messages = Visuals(view).OfType<TextBlock>().Where(text => DesktopVisual.IsVisible(text)).Select(text => text.Text).ToArray();
                CheckLayout(messages.Contains("热键被占用") == occupied, $"{scenario}: occupation status matches the reported registration error");
                CheckLayout(messages.Contains("快捷键注册失败（错误 5）") == occupied, $"{scenario}: other registration errors are not relabelled as occupied");
            }
        }
        Check(layoutFailures.Count == 0, string.Join(" | ", layoutFailures));
        DesktopTheme.SetIsHighContrast(view, true); await Drain();
        foreach (var text in Visuals(view).OfType<TextBlock>().Where(text => DesktopVisual.IsVisible(text) &&
                     text.Text is "热键被占用" or "快捷键注册失败（错误 5）"))
            Check(((SolidColorBrush)text.Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground));
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        Check(!((Button)view.FindName("RefreshButton")).IsEnabled);
        Click((Button)view.FindName("RefreshButton"));
        Check(refreshes == 1, "a disabled global setting never performs a probe");
    }

    private static async Task Recording()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        var view = new MediaControlSettingsView(owner);
        using var mount = new Mount(view); await Drain();
        var editor = Editor(view, MediaShortcutAction.TogglePlayback);
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(editor).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke(); await Drain();
        Check(owner.IsRecording && ((TextBlock)editor.Content).Text == "录制中");
        Check(PressKey(editor, mount.Source, Key.LeftCtrl, modifiers: ModifierKeys.Control).Handled);
        Check(owner.IsRecording && store.Writes == 0, "a modifier alone does not commit");
        Release(editor, mount.Source, Key.LeftCtrl, ModifierKeys.Control);
        Check(owner.IsRecording, "releasing a modifier alone keeps the visual recorder open");
        Check(PressKey(editor, mount.Source, Key.Escape).Handled);
        Check(owner.IsRecording && store.Writes == 0 && ((TextBlock)editor.Content).Text == "Ctrl+P",
            "Escape ends the visual edit but suppresses registration through its release");
        Release(editor, mount.Source, Key.Escape);
        Check(!owner.IsRecording && ((TextBlock)editor.Content).Text == "Ctrl+P");
        Click(editor);
        Check(PressKey(editor, mount.Source, Key.Tab).Handled);
        Release(editor, mount.Source, Key.Tab);
        Check(!owner.IsRecording && store.Writes == 0, "Tab leaves recording without assigning a key");
        Click(editor);
        Check(PressKey(editor, mount.Source, Key.F8, modifiers: ModifierKeys.Control | ModifierKeys.Shift).Handled);
        await Drain();
        Check(owner.IsRecording && store.Writes == 1 && ((TextBlock)editor.Content).Text == "Ctrl+Shift+F8",
            "the candidate is saved and the visual edit ends before global registration resumes");
        Release(editor, mount.Source, Key.F8, ModifierKeys.Control | ModifierKeys.Shift);
        await Drain();
        Check(!owner.IsRecording && store.Writes == 1 && ((TextBlock)editor.Content).Text == "Ctrl+Shift+F8");
        Check(owner.Preferences.Bindings.Single(binding => binding.Action == MediaShortcutAction.TogglePlayback).Local ==
            new MediaShortcutGesture(Key.F8, ModifierKeys.Control | ModifierKeys.Shift));
        foreach (var clear in new[] { Key.Delete, Key.Back })
        {
            Click(editor);
            Check(PressKey(editor, mount.Source, clear).Handled);
            Check(owner.IsRecording && ((TextBlock)editor.Content).Text == "未设置");
            Release(editor, mount.Source, clear);
            await Drain();
            Check(!owner.IsRecording && ((TextBlock)editor.Content).Text == "未设置");
            if (clear == Key.Delete)
            {
                Click(editor); PressKey(editor, mount.Source, Key.F9); Release(editor, mount.Source, Key.F9); await Drain();
            }
        }
        var global = Editor(view, MediaShortcutAction.TogglePlayback, true);
        Click(global);
        PressKey(global, mount.Source, Key.F10, modifiers: ModifierKeys.Control | ModifierKeys.Alt);
        Release(global, mount.Source, Key.F10, ModifierKeys.Control | ModifierKeys.Alt);
        await Drain();
        Check(((TextBlock)global.Content).Text == "Ctrl+Alt+F10" && !owner.IsRecording);
        Click(Visuals(view).OfType<Button>().Single(button =>
            AutomationProperties.GetName(button) == "清除播放/暂停，全局快捷键"));
        await Drain();
        Check(((TextBlock)global.Content).Text == "未设置" &&
            owner.Preferences.Bindings.Single(binding => binding.Action == MediaShortcutAction.TogglePlayback).Global is null);
    }

    private static async Task InputBoundary()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        var view = new MediaControlSettingsView(owner);
        using var mount = new Mount(view); await Drain();
        var editor = Editor(view, MediaShortcutAction.TogglePlayback);
        foreach (var (key, modifiers) in new[]
        {
            (Key.F, ModifierKeys.Control), (Key.Left, ModifierKeys.Alt),
            (Key.Escape, ModifierKeys.None), (Key.Space, ModifierKeys.None),
            (Key.Delete, ModifierKeys.None), (Key.Back, ModifierKeys.None), (Key.F8, ModifierKeys.Control)
        })
        {
            Click(editor);
            var down = KeyArgs(editor, mount.Source, key, modifiers, true);
            Check(down.Handled && owner.IsRecording && ((TextBlock)editor.Content).Text != "录制中");
            await Drain();
            Check(owner.IsRecording, "save completion cannot end the press-wide registration gate");
            var repeat = KeyArgs(editor, mount.Source, key, modifiers, true, repeat: true);
            Check(repeat.Handled && owner.IsRecording, "a held candidate stays consumed after the edit completes");
            var up = KeyArgs(editor, mount.Source, key, modifiers, false);
            Check(up.Handled && !owner.IsRecording);
            await Drain();
        }
        Click(editor);
        foreach (var modifier in new[] { Key.LeftCtrl, Key.LeftAlt })
        {
            var down = KeyArgs(editor, mount.Source, modifier, ModifierKeys.Control | ModifierKeys.Alt, true);
            Check(down.Handled && owner.IsRecording);
        }
        var candidate = KeyArgs(editor, mount.Source, Key.F9, ModifierKeys.Control | ModifierKeys.Alt, true);
        Check(candidate.Handled && owner.IsRecording);
        var candidateUp = KeyArgs(editor, mount.Source, Key.F9, ModifierKeys.Control | ModifierKeys.Alt, false);
        Check(candidateUp.Handled && owner.IsRecording, "observed modifiers keep the same recording gate until they are released");
        var controlUp = KeyArgs(editor, mount.Source, Key.LeftCtrl, ModifierKeys.Alt, false);
        Check(controlUp.Handled && owner.IsRecording);
        var altUp = KeyArgs(editor, mount.Source, Key.LeftAlt, ModifierKeys.None, false);
        Check(altUp.Handled && !owner.IsRecording);
        foreach (var (windowsKey, key, global, otherModifiers, expectedText) in new[]
        {
            (Key.LWin, Key.F10, false, ModifierKeys.None, "Win+F10"),
            (Key.RWin, Key.F11, true, ModifierKeys.Control, "Ctrl+Win+F11")
        })
        {
            var target = Editor(view, MediaShortcutAction.TogglePlayback, global);
            Click(target);
            int writes = store.Writes;
            var modifiers = otherModifiers | ModifierKeys.Windows;
            var winDown = KeyArgs(target, mount.Source, windowsKey, modifiers, true, windowsKey: windowsKey);
            Check(winDown.Handled && owner.IsRecording && store.Writes == writes &&
                ((TextBlock)target.Content).Text == "录制中", $"{windowsKey} alone does not submit a candidate");
            var winCandidate = KeyArgs(target, mount.Source, key, modifiers, true, windowsKey: windowsKey);
            await Drain();
            var binding = owner.Preferences.Bindings.Single(item => item.Action == MediaShortcutAction.TogglePlayback);
            Check(winCandidate.Handled && owner.IsRecording && store.Writes == writes + 1 &&
                (global ? binding.Global : binding.Local) == new MediaShortcutGesture(key, modifiers) &&
                ((TextBlock)target.Content).Text == expectedText,
                $"{windowsKey} records the complete {(global ? "global" : "local")} gesture without dropping Win");
            var winRepeat = KeyArgs(target, mount.Source, key, modifiers, true, repeat: true, windowsKey: windowsKey);
            Check(winRepeat.Handled && owner.IsRecording && store.Writes == writes + 1,
                $"{windowsKey} candidate repeat remains consumed without another save");
            var winCandidateUp = KeyArgs(target, mount.Source, key, modifiers, false, windowsKey: windowsKey);
            Check(winCandidateUp.Handled && owner.IsRecording, $"{windowsKey} keeps the recording gate until its own release");
            var winUp = KeyArgs(target, mount.Source, windowsKey, otherModifiers, false, windowsKey: windowsKey);
            Check(winUp.Handled && !owner.IsRecording, $"{windowsKey} final release retires the recording gate");
        }
        var ordinary = KeyArgs(editor, mount.Source, Key.F11, ModifierKeys.None, true);
        Check(!ordinary.Handled, "ordinary input is not consumed when recording has ended");
    }

    private static async Task RecordingRegistration()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        await owner.SaveAsync(ShortcutFixture.Preferences());
        store.HoldNamespace = AppMediaControlSettings.DocumentNamespace;
        await using var fixture = new ShortcutFixture();
        var view = new MediaControlSettingsView(owner);
        fixture.Root.Children.Clear();
        fixture.Root.Children.Add(view);
        fixture.Window.Resize(907, 680);
        fixture.Window.Show();
        await Drain();
        fixture.Native.Foreground = fixture.Handle;
        fixture.Attach(owner);
        string NativeState() =>
            $"available={owner.IsAvailable}, recording={owner.IsRecording}, editable={owner.CanEdit}, " +
            $"globalEnabled={owner.Preferences.GlobalEnabled}, registrations={fixture.Native.Calls.Count}, " +
            $"unregistered=[{string.Join(",", fixture.Native.Unregistered)}], actions={fixture.Actions.Count}, writes={store.Writes}, " +
            $"errors=[{string.Join("; ", owner.GlobalErrors.Select(item => $"{item.Key}={item.Value}"))}]";
        Check(fixture.Native.Calls.Count == 1, "initial native registration call; " + NativeState());
        var registration = fixture.Native.Latest;
        Check(registration.Success,
            $"initial native registration result: id={registration.Id}, modifiers={registration.Modifiers}, key={registration.Key}; {NativeState()}");
        var editor = Editor(view, MediaShortcutAction.TogglePlayback);
        var gesture = ShortcutFixture.GlobalGesture;
        int writes = store.Writes;
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void SaveStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (owner.CanEdit && owner.Preferences.Bindings.Single(binding =>
                    binding.Action == MediaShortcutAction.TogglePlayback).Local == gesture)
                saved.TrySetResult();
        }
        owner.PropertyChanged += SaveStateChanged;
        try
        {
            Click(editor);
            Check(owner.IsRecording && fixture.Native.Unregistered.Contains(registration.Id), "recording entry releases the registered key; " + NativeState());
            Check(fixture.SendKey(editor, gesture).Handled, "candidate KeyDown is consumed; " + NativeState());
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            Check(owner.IsRecording && !owner.CanEdit && editor.IsEnabled && ((TextBlock)editor.Content).Text == "未设置",
                "the input owner retains focus eligibility while its candidate save is pending");
            Check(!Editor(view, MediaShortcutAction.Next).IsEnabled, "other edits remain closed during the save");
            Check(fixture.SendKey(editor, gesture, repeat: true).Handled);
            fixture.Post(registration);
            await MediaShortcutTests.Pump();
            Check(fixture.Native.Calls.Count == 1 && fixture.Actions.Count == 0, "pending save cannot register or execute; " + NativeState());
            store.Release.TrySetResult();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            Check(owner.CanEdit && owner.IsRecording && store.Writes == writes + 1, "settled save retains the held-press gate; " + NativeState());
            Check(owner.Preferences.Bindings.Single(binding => binding.Action == MediaShortcutAction.TogglePlayback).Local == gesture &&
                ((TextBlock)editor.Content).Text == MediaShortcutGestures.Display(gesture));
            owner.RequestRegistrationRefresh();
            Check(fixture.SendKey(editor, gesture, repeat: true).Handled);
            fixture.Post(registration);
            await MediaShortcutTests.Pump();
            Check(fixture.Native.Calls.Count == 1 && fixture.Actions.Count == 0,
                "neither saved preferences, refresh nor held-key messages can reopen global input; " + NativeState());
            Check(fixture.SendKey(editor, gesture, down: false).Handled);
            Check(!owner.IsRecording && fixture.Native.Calls.Count == 2 && fixture.Native.Latest.Success,
                "the candidate release resumes real registration; " + NativeState());
            fixture.Post(fixture.Native.Latest);
            await MediaShortcutTests.Pump();
            Check(fixture.Actions.SequenceEqual([MediaShortcutAction.TogglePlayback]), "a subsequent press may execute normally");
            var global = Editor(view, MediaShortcutAction.TogglePlayback, true);
            registration = fixture.Native.Latest;
            int registrations = fixture.Native.Calls.Count;
            Click(global);
            Check(fixture.SendKey(global, gesture).Handled);
            await Drain();
            Check(owner.IsRecording && ((TextBlock)global.Content).Text == MediaShortcutGestures.Display(gesture) &&
                store.Writes == writes + 1, "an unchanged global candidate still retains the press-wide gate");
            Check(fixture.SendKey(global, gesture, repeat: true).Handled);
            fixture.Post(registration);
            await MediaShortcutTests.Pump();
            Check(fixture.Native.Calls.Count == registrations && fixture.Actions.Count == 1, "unchanged held global candidate remains suppressed; " + NativeState());
            Check(fixture.SendKey(global, gesture, down: false).Handled);
            Check(!owner.IsRecording && fixture.Native.Calls.Count == registrations + 1 && fixture.Native.Latest.Success,
                "unchanged global candidate release resumes registration; " + NativeState());
        }
        finally
        {
            store.Release.TrySetResult();
            owner.PropertyChanged -= SaveStateChanged;
        }
    }

    private static async Task Failures()
    {
        var store = new DocumentStore { FaultNamespace = AppMediaControlSettings.DocumentNamespace };
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        var view = new MediaControlSettingsView(owner);
        using var mount = new Mount(view); await Drain();
        var notices = new List<string>();
        NoticeOutlet.SetReceiver(mount.Root, e => { if (e.Message.Length > 0) notices.Add(e.Message); });
        var editor = Editor(view, MediaShortcutAction.Previous);
        Click(editor);
        PressKey(editor, mount.Source, Key.P, modifiers: ModifierKeys.Control);
        Check(owner.IsRecording && store.Writes == 0 && ((TextBlock)editor.Content).Text == "Ctrl+Left",
            "a conflicting held candidate restores authority without reopening registration");
        Release(editor, mount.Source, Key.P, ModifierKeys.Control); await Drain();
        Check(!owner.IsRecording && store.Writes == 0 && ((TextBlock)editor.Content).Text == "Ctrl+Left");
        Check(notices.Any(message => message.Contains("播放/暂停", StringComparison.Ordinal)));
        Check(Visuals(view).OfType<TextBlock>().Any(text => DesktopVisual.IsVisible(text) &&
            text.Text.Contains("播放/暂停与上一首之间重复", StringComparison.Ordinal)));
        store.Fault = true; store.FaultAfter = false;
        Click(editor);
        PressKey(editor, mount.Source, Key.F8, modifiers: ModifierKeys.Control);
        await Drain();
        Check(owner.IsRecording && ((TextBlock)editor.Content).Text == "Ctrl+Left" && owner.Status.Length > 0,
            "a failed candidate save retains the recording gate until release");
        Release(editor, mount.Source, Key.F8, ModifierKeys.Control); await Drain();
        Check(store.Writes == 1 && ((TextBlock)editor.Content).Text == "Ctrl+Left" && owner.Status.Length > 0);
        Check(notices.Contains(owner.Status), "storage failure uses the existing notice outlet");
        Check(DesktopVisual.IsVisible((Button)view.FindName("ReloadButton")));
        Click((Button)view.FindName("ReloadButton")); await Drain();
        Check(owner.Status == "" && ((TextBlock)editor.Content).Text == "Ctrl+Left");
        var global = (ToggleSwitch)view.FindName("GlobalSwitch");
        store.Fault = true; store.FaultAfter = false;
        global.IsOn = false;
         await Drain();
        Check(owner.Preferences.GlobalEnabled && global.IsOn, "failed switch save returns to its authoritative value");
    }

    private static async Task Lifecycle()
    {
        var store = new DocumentStore { HoldNamespace = AppMediaControlSettings.DocumentNamespace };
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        var view = new MediaControlSettingsView(owner);
        using var mount = new Mount(view); await Drain();
        var editor = Editor(view, MediaShortcutAction.TogglePlayback);
        foreach (bool complete in new[] { false, true })
        {
            void StartRecording()
            {
                Click(editor); Check(owner.IsRecording);
                if (!complete) return;
                PressKey(editor, mount.Source, Key.P, modifiers: ModifierKeys.Control);
                Check(owner.IsRecording && ((TextBlock)editor.Content).Text == "Ctrl+P",
                    "a new recording cannot inherit a previous press whose release was lost outside the window");
            }
            StartRecording();
            ((Button)view.FindName("RefreshButton")).Focus(FocusState.Programmatic);
            await Drain();
            Check(!owner.IsRecording, "focus loss releases even a visually completed held press");
            StartRecording();
            view.Visibility = Visibility.Collapsed; await Drain();
            Check(!owner.IsRecording, "hiding releases even a visually completed held press");
            if (complete) Release(editor, mount.Source, Key.P, ModifierKeys.Control);
            view.Visibility = Visibility.Visible; await Drain();
            StartRecording();
            mount.Root.Child = null; await Drain();
            Check(!owner.IsRecording, "unload always restores registration admission");
            mount.Root.Child = view; await Drain();
        }
        Click(editor); Check(owner.IsRecording);
        var global = (ToggleSwitch)view.FindName("GlobalSwitch");
        global.IsOn = false;

        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!owner.IsRecording && !owner.CanEdit && !editor.IsEnabled);
            mount.Root.Child = null; await Drain();
            var shutdown = owner.PrepareShutdownAsync();
            Check(!shutdown.IsCompleted);
            store.Release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { store.Release.TrySetResult(); }
        await Drain();
        Check(!owner.Preferences.GlobalEnabled && !owner.IsRecording);
        await owner.CancelShutdownPreparationAsync();
        mount.Root.Child = view; await Drain();
        Check(owner.CanEdit && !owner.IsRecording && ((ToggleSwitch)view.FindName("GlobalSwitch")).IsOn == false);
    }

    private static async Task ShellInjection()
    {
        await using var owner = new AppMediaControlSettings(new DocumentStore());
        await using var appearance = new AppAppearance(new DocumentStore());
        await using var closePreferences = new AppClosePreferences(new DocumentStore());
        await owner.LoadAsync(); await appearance.LoadAsync(); await closePreferences.LoadAsync();
        var shell = new ShellWindow([new SettingsNavigationTests.Module("music", "音乐")], [],
            () => Task.CompletedTask, () => Task.CompletedTask, appearance: appearance,
            closePreferences: closePreferences, mediaControls: owner);
        try
        {
            Check(ReferenceEquals(shell.MediaControls, owner));
            var general = Visuals(DesktopTestUi.WindowRoot(shell)).OfType<GeneralSettingsView>().Single();
            var page = (ContentControl)general.FindName("ShortcutsPage");
            Check(page.Content is MediaControlSettingsView view && ReferenceEquals(view.DataContext, owner));
            var windowPage = (StackPanel)general.FindName("WindowBehaviorPage");
            Check(ReferenceEquals(windowPage.DataContext, closePreferences));
            ((RadioButton)general.FindName("WindowArea")).IsChecked = true;
            Check(windowPage.Visibility == Visibility.Visible && page.Visibility == Visibility.Collapsed);
            ((RadioButton)general.FindName("ShortcutsArea")).IsChecked = true;
            Check(page.Visibility == Visibility.Visible && windowPage.Visibility == Visibility.Collapsed &&
                page.Content is MediaControlSettingsView restored && ReferenceEquals(restored.DataContext, owner));
        }
        finally { await shell.AbortAsync(); }
    }

    private static Button Editor(MediaControlSettingsView view, MediaShortcutAction action, bool global = false) =>
        Visuals(view).OfType<Button>().Single(button => DesktopVisual.IsVisible(button) &&
            AutomationProperties.GetName(button) == MediaControlDefaults.Label(action) + (global ? "，全局快捷键" : "，应用内快捷键"));
    private static void Check(bool value, string? message = null, [CallerLineNumber] int line = 0,
        [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value) throw new InvalidOperationException($"Media control settings UI line {line}: {message ?? expression}");
    }
    private static void Click(Button button) => DesktopTestUi.Invoke(button);
    private static ShortcutFixture.InputResult PressKey(UIElement target, FrameworkElement source, Key key,
        bool down = true, ModifierKeys modifiers = ModifierKeys.None) => KeyArgs(target, source, key, modifiers, down);
    private static void Release(UIElement target, FrameworkElement source, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        PressKey(target, source, key, false, modifiers);
    private static ShortcutFixture.InputResult KeyArgs(UIElement target, FrameworkElement source, Key key, ModifierKeys modifiers, bool down,
        bool repeat = false, Key windowsKey = Key.LWin)
    {
        var view = Visuals(source).OfType<MediaControlSettingsView>().Single();
        var projected = MediaShortcutInput.Modifiers(modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Alt), modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Windows) && windowsKey == Key.LWin,
            modifiers.HasFlag(ModifierKeys.Windows) && windowsKey == Key.RWin);
        Check(projected == modifiers, "the real modifier projection retains either Windows key");
        return new(view.ProcessKey(target, key, projected, down, repeat));
    }
    private sealed class Mount : IDisposable
    {
        internal Border Root { get; }
        internal FrameworkElement Source => Root;
        private readonly DesktopTestUi.Host host;
        internal Mount(FrameworkElement content)
        {
            Root = new Border { Child = content, Background = AppearanceColors.DefaultBackground };
            host = new(Root, 907, 680);
            Layout(907, 680);
        }
        internal void Layout(int width, int height)
        {
            host.Window.Resize(width, height);
            Root.Width = width; Root.Height = height;
            Root.Measure(new(width, height)); Root.Arrange(new(0, 0, width, height)); Root.UpdateLayout();
        }
        public void Dispose() => host.Dispose();
    }
    private static async Task Drain()
    { for (int i = 0; i < 5; i++) await DesktopTestUi.DrainAsync(); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
