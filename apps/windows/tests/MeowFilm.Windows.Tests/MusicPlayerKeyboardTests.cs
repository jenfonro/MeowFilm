using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.System;

namespace MeowFilm.Windows.Tests;

public static class MusicPlayerKeyboardTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PlayerKeyboardCases();
}

public static partial class MusicUiTests
{
    // These exercise the real key-admission owners and native controls. They do
    // not synthesize KeyRoutedEventArgs or claim physical keyboard delivery.
    internal static IEnumerable<TestCase> PlayerKeyboardCases()
    {
        foreach (bool reset in new[] { false, true })
            yield return Case("player channel cancellation preserves the channel below " + (reset ? "reset confirmation" : "close confirmation"), () =>
                WithPlayerKeyboard(async (f, detail, shell) =>
                {
                    var host = (PlayerDialogHost)DesktopTestUi.WindowRoot(shell).FindName("ChannelDialogHost");
                    host.Show(detail, new Border { Width = 368, Height = 200 }, () => { });
                    FrameworkElement confirmation;
                    if (reset) { var dialog = (UserDataResetDialog)DesktopTestUi.WindowRoot(shell).FindName("ResetDialog"); dialog.Open(); confirmation = dialog; }
                    else { var dialog = (CloseWindowDialog)DesktopTestUi.WindowRoot(shell).FindName("CloseDialog"); dialog.Open(); confirmation = dialog; }
                    await Drain();
                    True(!shell.TryExecuteShortcut(VirtualKey.Escape, VirtualKeyModifiers.None),
                        "the top confirmation owns cancellation rather than shell navigation");
                    Click((Button)confirmation.FindName("CancelChoice")); await Drain();
                    True(confirmation.Visibility == Visibility.Collapsed && host.IsOpen && f.Module.IsLyrics,
                        "native cancellation removes only the top confirmation");
                    host.RequestClose();
                }));
        yield return Case("player channel keyboard blocks underlying search and navigation", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var host = (PlayerDialogHost)DesktopTestUi.WindowRoot(shell).FindName("ChannelDialogHost");
                var target = new Button { Content = "channel action" };
                host.Show(detail, new Border { Child = target, Width = 368, Height = 200 }, () => { }); await Drain();
                target.Focus(FocusState.Programmatic);
                True(DesktopVisual.Contains(host, DesktopVisual.Focused(DesktopTestUi.WindowRoot(shell))!), "native focus belongs to the live channel");
                foreach (var (key, modifiers) in new[] { (VirtualKey.F, VirtualKeyModifiers.Control),
                    (VirtualKey.K, VirtualKeyModifiers.Control), (VirtualKey.Left, VirtualKeyModifiers.Menu),
                    (VirtualKey.Right, VirtualKeyModifiers.Menu) })
                    True(!shell.TryExecuteShortcut(key, modifiers), "underlying shell commands are not admitted while the channel is open");
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes); host.RequestClose();
            }));
        yield return Case("player channel blocks lower transport hits in the actual XAML tree", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var host = (PlayerDialogHost)DesktopTestUi.WindowRoot(shell).FindName("ChannelDialogHost");
                host.Show(detail, new Border { Width = 368, Height = 200 }, () => { }); await Drain();
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                foreach (string name in new[] { "PlayButton", "NextButton", "Progress", "VolumeButton" })
                {
                    var control = (FrameworkElement)footer.FindName(name);
                    var point = control.TransformToVisual(DesktopTestUi.WindowRoot(shell)).TransformPoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
                    var hit = VisualTreeHelper.FindElementsInHostCoordinates(point, DesktopTestUi.WindowRoot(shell)).First();
                    True(DesktopVisual.Contains(host, hit), "the shared channel barrier owns actual lower-control hit geometry");
                }
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes); host.RequestClose();
            }));
        yield return Case("player channel native action and close preserve queue and full-player ownership", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var footer = (MusicPlayerView)f.Module.PlayerView; Click((Button)footer.FindName("QueueButton")); await Drain();
                var host = (PlayerDialogHost)DesktopTestUi.WindowRoot(shell).FindName("ChannelDialogHost"); int actions = 0, closes = 0;
                var target = new Button { Content = "channel action" }; target.Click += (_, _) => actions++;
                host.Show(detail, new Border { Child = target, Width = 368, Height = 200 }, () => closes++); await Drain();
                Click(target); Equal(1, actions); True(host.IsOpen && f.Module.IsLyrics, "channel action cannot toggle or leave the full player");
                host.RequestClose(); Equal(1, closes);
                True(f.Module.IsLyrics && PlayerSurfaceInput.GetQueueOpen(DesktopTestUi.WindowRoot(shell)), "closing the channel leaves both underlying owners intact");
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
            }));
        yield return Case("player keyboard Space toggles through the visible detail admission only", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var lyrics = (ListView)detail.FindName("LyricList");
                var press = detail.HandlePlaybackSpace(lyrics, true); True(press.Handled, "detail admits bare Space"); await press.Work;
                True(detail.HandlePlaybackSpace(lyrics, false).Handled, "detail consumes the paired release");
                Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                press = detail.HandlePlaybackSpace(lyrics, true); await press.Work; detail.HandlePlaybackSpace(lyrics, false);
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
                True(!detail.HandlePlaybackSpace(f.Module.PlayerView, true).Handled, "ordinary footer is not owned by the detail key handler");
            }));
        yield return Case("player keyboard focused play button cannot arm a second release command", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                var play = (Button)footer.FindName("PlayButton"); play.Focus(FocusState.Programmatic);
                for (int i = 0; i < 2; i++)
                {
                    var press = detail.HandlePlaybackSpace(play, true); True(press.Handled, "the detail owns the focused native button press"); await press.Work;
                    True(detail.HandlePlaybackSpace(play, false).Handled, "the paired release is consumed without another toggle");
                }
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
            }));
        yield return Case("player keyboard held Space is consumed while the accepted toggle is queued", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var lyrics = (ListView)detail.FindName("LyricList");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = f.Module.RunAsync(() => release.Task); Task? toggle = null;
                try
                {
                    True(f.Module.Busy && f.Module.CanTogglePlayback, "unrelated work retains actual playback admission");
                    var press = detail.HandlePlaybackSpace(lyrics, true); True(press.Handled, "initial press is admitted"); toggle = press.Work;
                    for (int i = 0; i < 4; i++) True(detail.HandlePlaybackSpace(lyrics, true, repeat: true).Handled, "held repeats remain consumed");
                    Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                }
                finally { release.TrySetResult(); await pending; if (toggle is not null) await toggle; }
                Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                True(detail.HandlePlaybackSpace(lyrics, false).Handled, "one release retires the held press");
                await detail.HandlePlaybackSpace(lyrics, true).Work; detail.HandlePlaybackSpace(lyrics, false);
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
            }));
        yield return Case("player keyboard preserves native text password rich editor and editable combo input", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var editors = new StackPanel(); var combo = new ComboBox { IsEditable = true, Width = 160 }; combo.Items.Add("editable");
                Control[] controls = [new TextBox { Text = "native text" }, new RichEditBox(), new PasswordBox(), combo];
                foreach (var control in controls) editors.Children.Add(control);
                var layout = (Grid)detail.Content; layout.Children.Add(editors);
                try
                {
                    await Drain(); combo.ApplyTemplate();
                    foreach (var control in controls.Concat(DesktopTestUi.Visuals(combo).OfType<TextBox>()))
                        True(!detail.HandlePlaybackSpace(control, true).Handled, "the native editor retains its own Space input");
                    Equal("native text", ((TextBox)controls[0]).Text); Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                }
                finally { layout.Children.Remove(editors); }
            }));
        yield return Case("player keyboard leaves real menu and Popup input with their owners", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var item = new MenuFlyoutItem { Text = "menu action" }; var menu = new MenuFlyout { Items = { item } };
                var anchor = (Button)((MusicPlayerView)f.Module.PlayerView).FindName("ModeButton"); menu.ShowAt(anchor); await Drain();
                try { True(!detail.HandlePlaybackSpace(item, true).Handled, "a native flyout is outside detail key ownership"); }
                finally { menu.Hide(); }
                var body = new StackPanel(); var editor = new TextBox(); var target = new Button { Content = "popup action" };
                body.Children.Add(editor); body.Children.Add(target);
                var popup = new Popup { XamlRoot = detail.XamlRoot, Child = body, IsOpen = true }; await Drain();
                try
                {
                    True(body.IsLoaded && popup.IsOpen, "the actual native popup is loaded");
                    foreach (var control in new Control[] { editor, target })
                        True(!detail.HandlePlaybackSpace(control, true).Handled, "native popup input stays with its own tree");
                    Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                }
                finally { popup.IsOpen = false; popup.Child = null; }
            }));
        yield return Case("player keyboard hidden and closing detail reject Space and retire held input", () =>
            WithPlayerKeyboard(async (f, detail, shell) =>
            {
                var lyrics = (ListView)detail.FindName("LyricList"); await detail.HandlePlaybackSpace(lyrics, true).Work;
                detail.Visibility = Visibility.Collapsed; detail.HandlePlaybackSpace(lyrics, false);
                True(!detail.HandlePlaybackSpace(lyrics, true).Handled, "hidden detail rejects a new press");
                detail.Visibility = Visibility.Visible; await Drain();
                True(detail.HandlePlaybackSpace(lyrics, true, repeat: true).Handled, "a revealed held repeat does not become a new command");
                detail.HandlePlaybackSpace(lyrics, false); Equal(0, f.Playback.Resumes);
                await detail.HandlePlaybackSpace(lyrics, true).Work; detail.HandlePlaybackSpace(lyrics, false);
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                True(!detail.HandlePlaybackSpace(lyrics, true).Handled, "closed admission rejects input before visual retirement");
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
            }));
        yield return Case("player keyboard modifiers preserve their own Space input", () =>
            WithPlayerKeyboard((f, detail, shell) =>
            {
                var lyrics = (ListView)detail.FindName("LyricList");
                foreach (var modifiers in new[] { VirtualKeyModifiers.Control, VirtualKeyModifiers.Menu, VirtualKeyModifiers.Shift,
                    VirtualKeyModifiers.Windows, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu })
                    True(!detail.HandlePlaybackSpace(lyrics, true, modifiers: modifiers).Handled, "modified Space is not a media command");
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes); return Task.CompletedTask;
            }));
        yield return Case("player keyboard requires playback admission and rejects shutdown input", async () =>
        {
            await WithPlayerKeyboard((f, detail, shell) =>
            {
                True(!f.Module.CanTogglePlayback && !detail.HandlePlaybackSpace(detail.FindName("LyricList"), true).Handled,
                    "empty media cannot admit playback input");
                Equal(0, f.Playback.Opens.Count); return Task.CompletedTask;
            }, withCurrent: false);
            await WithPlayerKeyboard(async (f, detail, shell) =>
            {
                await f.Module.PrepareShutdownAsync();
                True(!detail.HandlePlaybackSpace(detail.FindName("LyricList"), true).Handled, "shutdown seals playback admission");
                Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
            });
        });
    }
    private static async Task WithPlayerKeyboard(Func<Fixture, MusicPlayerDetailView, ShellWindow, Task> body, bool withCurrent = true)
    {
        await using var f = await Fixture.Create();
        if (withCurrent) { await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start(); await Drain(); }
        var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
        try
        {
            shell.PrepareForRender(); shell.Resize(1110, 800); shell.Activate(); await TestUiHost.LoadedAsync(DesktopTestUi.WindowRoot(shell));
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            await f.Module.OpenPlayerDetailAsync(); await Until(() => f.Module.IsLyrics && DesktopVisual.IsVisible(detail));
            await body(f, detail, shell);
        }
        finally { shell.Close(); await shell.ShutdownCompletion; }
    }
}
