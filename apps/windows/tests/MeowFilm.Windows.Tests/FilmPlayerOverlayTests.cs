using Colors = Microsoft.UI.Colors;
using System.Diagnostics;
using Microsoft.UI;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Media;
using IconPath = Microsoft.UI.Xaml.Shapes.Path;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;
using static MeowFilm.Windows.Tests.FilmUiTest;

namespace MeowFilm.Windows.Tests;

public static partial class FilmPlayerOverlayTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-player-overlay: " + name, () => TestUiHost.InvokeAsync(run));
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("picture controls and application overlays share one composition tree", CompositionTree);
        yield return Test("ordinary fake views mount without pretending to be native surfaces", PlainView);
        yield return Test("full screen and PiP move the same tree and preserve its occurrence", WindowRoundTrips);
        yield return Test("resize scroll menus and temporary coverage never detach controls", LayoutChanges);
        yield return Test("the owner can close before the composition surface is disposed", OwnerClose);
        yield return Test("local settings retain all rates and aspects without business commands", Settings);
        yield return Test("idle playing controls fold while paused busy and open-menu controls remain usable", ControlVisibility);
        yield return Test("native buffering alone controls the noninteractive progress ring", Buffering);
        yield return Test("narrow controls remove optional actions in order and keep essential actions", NarrowWidths);
        yield return Test("short video remains a page action and enables the picture gesture only after acceptance", ShortVideo);
        yield return Test("delayed picture action is cancelled by fullscreen replacement and unload", DelayedClick);
        yield return Test("keyboard ownership rejects control targets and cancelled held gestures", KeyboardOwnership);
        yield return Test("seek preview commits once and cancellation cannot cross focus or occurrence boundaries", SeekFences);
        foreach (var test in NativeShellCases()) yield return test;
    }

    private static async Task CompositionTree()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay");
        Check(ReferenceEquals(scene.Surface.Content, overlay) && DesktopVisual.Contains(scene.Surface, overlay) &&
            ReferenceEquals(scene.Element<ContentControl>("VideoHost").Content, scene.Surface) &&
            scene.Surface.Children.OfType<SwapChainPanel>().Count() == 1 &&
            ReferenceEquals(overlay.XamlRoot, scene.View.XamlRoot),
            "controls are surface children beside the picture in the same application XamlRoot");
        var picture = scene.Element<Grid>("VideoPicture");
        Check(picture.Background is SolidColorBrush { Color: var color } && color == Colors.Black &&
            Math.Abs(picture.ActualHeight - picture.ActualWidth * 9 / 16) < 1,
            "the picture keeps its black 16:9 canvas");
        var bar = scene.Element<StackPanel>("ControlBar");
        var left = scene.Element<Border>("LeftPill"); var right = scene.Element<Border>("RightPill");
        Check(bar.Children.SequenceEqual(new UIElement[] { scene.Element<Slider>("SeekSlider"), scene.Element<Grid>("ControlsRow") }) &&
            bar.VerticalAlignment == VerticalAlignment.Bottom && DesktopVisual.Contains(scene.Element<Grid>("PlayerChrome"), bar) &&
            DesktopVisual.Contains(overlay, bar) && Bounds(scene.Element<Slider>("SeekSlider"), overlay).Top < Bounds(left, overlay).Top,
            "the progress rail precedes the two capsules within the picture");
        Check(((StackPanel)left.Child).Children.SequenceEqual(new UIElement[] { scene.Element<Button>("PlayButton"),
                scene.Action("上一集"), scene.Action("下一集"), scene.Element<StackPanel>("VolumeGroup"), scene.Element<TextBlock>("PlayerTime") }),
            "the left capsule preserves play previous next volume and elapsed-time order");
        Check(((StackPanel)right.Child).Children.OfType<Button>().Select(AutomationProperties.GetName)
            .SequenceEqual(new[] { "切换网盘", "切换画质", "临时换源", "屏蔽当前片源", "画中画", "播放器设置", "全屏" }),
            "right-hand actions preserve their functional order");
        Check(left.HorizontalAlignment == HorizontalAlignment.Left && right.HorizontalAlignment == HorizontalAlignment.Right &&
            Bounds(left, overlay).Right < Bounds(right, overlay).Left,
            "the wide picture keeps its capsules on their respective sides");
        var seek = scene.Element<Slider>("SeekSlider");
        var pan = scene.Element<Button>("PanButton"); var quality = scene.Element<Button>("QualityButton");
        Check(ReferenceEquals(pan.DataContext, scene.Rig.Page) && ReferenceEquals(seek.DataContext, scene.Rig.Page) &&
            seek.Maximum == scene.Rig.Page.Duration && Equals(pan.Content, "夸克") && Equals(quality.Content, "1080P"),
            "current provider quality and progress inherit the accepted page through the surface");
        foreach (var button in new[] { scene.Element<Button>("PlayButton"), scene.Action("上一集"), scene.Action("下一集"),
            scene.Element<Button>("MuteButton"), scene.Element<Button>("PipButton"), scene.Element<Button>("SettingsButton"),
            scene.Element<Button>("FullscreenButton") })
        {
            Check(button.Content is IconPath { Data: not null, Clip: null }, "each icon action retains its complete uncut vector");
            var bounds = Bounds((IconPath)button.Content, button);
            Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= button.ActualWidth &&
                bounds.Bottom <= button.ActualHeight, "the full icon remains inside its circular action");
        }
        int requests = scene.Rig.Runtime.Calls.Count;
        Click(scene.Action("上一集")); await Until(() => scene.ActionCount("previous") == 1);
        Click(scene.Action("下一集")); await Until(() => scene.ActionCount("next") == 1);
        Check(scene.Rig.Runtime.Calls.Count == requests + 2 && scene.Rig.Native.Commits == 1 &&
            scene.Rig.Native.SettledIdentity == "overlay",
            "each adjacent-episode action sends one exact command without claiming another output");
        var cover = new Grid { Background = new SolidColorBrush(Colors.White) };
        scene.Root.Children.Add(cover);
        await Pump();
        Check(scene.Root.Children.IndexOf(cover) > scene.Root.Children.IndexOf(scene.View) &&
            DesktopVisual.Contains(scene.Root, scene.Surface) && ReferenceEquals(scene.Surface.Content, overlay) && scene.Rig.Native.Commits == 1,
            "an application overlay is composed above the whole player without a second control window");
        scene.Root.Children.Remove(cover);
    }

    private static async Task PlainView()
    {
        await using var rig = new FilmPlaybackRig(new Border());
        await rig.OpenAsync();
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var window = FilmUiTest.Window(view); window.Show();
        try
        {
            await Until(() => view.IsLoaded);
            Check(ReferenceEquals(Named<ContentControl>(view, "VideoHost").Content, rig.Native.View) &&
                DesktopVisual.Contains(Named<Grid>(view, "VideoPicture"), Named<Grid>(view, "PlayerOverlay")),
                "a general FrameworkElement does not need a fabricated native Content contract");
        }
        finally { view.DataContext = null; window.Close(); }
    }

    private static async Task WindowRoundTrips()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay"); var frame = scene.Element<Grid>("PlayerFrame");
        var scope = scene.Rig.Native.Scope;
        for (int round = 0; round < 2; round++)
            foreach (string action in new[] { "FullscreenButton", "PipButton" })
            {
                Click(scene.Element<Button>(action));
                await Until(() => scene.PlayerWindow is not null && overlay.IsLoaded);
                var player = scene.PlayerWindow!;
                Check(ReferenceEquals(scene.Surface.Content, overlay) && ReferenceEquals(overlay.XamlRoot, player.Root.XamlRoot) &&
                    DesktopVisual.Contains(player.Root, frame) && scene.Rig.Native.Scope == scope && scene.Rig.Native.Commits == 1 &&
                    scene.Rig.Session.CanRetainMedia == (action == "PipButton"),
                    "window presentation moves one stable media and control tree, retaining only explicit PiP");
                player.Close();
                await Until(() => scene.PlayerWindow is null && ReferenceEquals(overlay.XamlRoot, scene.View.XamlRoot));
                Check(ReferenceEquals(scene.Surface.Content, overlay) && scene.Rig.Native.Stops == 0,
                    "returning to an active page neither retires nor recreates playback");
            }
    }

    private static async Task LayoutChanges()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay"); int unloads = 0;
        overlay.Unloaded += (_, _) => unloads++;
        int calls = scene.Rig.Runtime.Calls.Count;
        foreach (int width in new[] { 900, 1200, 780, 1400 })
        {
            scene.Window.Resize(width, 850); await Pump(); scene.View.UpdateLayout();
            var source = scene.Element<ComboBox>("SourcePicker"); source.IsDropDownOpen = true; await Pump();
            Check(ReferenceEquals(scene.Surface.Content, overlay) && unloads == 0,
                "resizing and opening a source popup do not tear down the player tree");
            source.IsDropDownOpen = false;
        }
        var scroll = scene.Element<ScrollViewer>("PageScroll");
        scroll.ChangeView(null, 100, null, true); await Pump();
        scene.View.Visibility = Visibility.Collapsed; await Pump();
        Check(!Visible(scene.Surface) && scene.Rig.Session.HasOutput && scene.Rig.Native.Stops == 0,
            "ancestor visibility hides the whole composed player without retiring its occurrence");
        scene.View.Visibility = Visibility.Visible; await Pump();
        Check(unloads == 0 && scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Commits == 1,
            "returning from coverage restores presentation without a business request or new commit");
    }

    private static async Task OwnerClose()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        scene.Window.Close(); await Pump();
        Check(scene.Surface.Content is null && scene.Element<ContentControl>("VideoHost").Content is null,
            "unloaded page releases its presentation references before surface disposal");
        scene.Closed = true;
    }

    private static async Task Settings()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        await scene.Rig.Session.SetPausedAsync(true);
        var rate = scene.Element<ListBox>("RateBox"); var aspect = scene.Element<ListBox>("AspectBox");
        var panel = scene.Element<Border>("SettingsPanel");
        Check(DesktopVisual.Contains(scene.Element<Grid>("PlayerChrome"), panel) && !Visible(panel) &&
            !Visible(rate) && !Visible(aspect), "rates and aspects live only in the initially closed picture menu");
        Check(rate.Items.Cast<ListBoxItem>().Select(item => (string)item.Tag).SequenceEqual(new[] { "0.5", "0.75", "1", "1.25", "1.5", "2" }) &&
            aspect.Items.Cast<ListBoxItem>().Select(item => (string)item.Tag).SequenceEqual(new[] { "", "16:9", "4:3" }) &&
            rate.SelectedIndex == 2 && aspect.SelectedIndex == 0,
            "the full supported control choices survive migration");
        int requests = scene.Rig.Runtime.Calls.Count, commands = scene.Rig.Native.Controls.Count;
        Click(scene.Element<Button>("SettingsButton")); await Pump();
        Check(Visible(panel) && Visible(rate) && Visible(aspect) && DesktopVisual.Contains(panel, rate) &&
            DesktopVisual.Contains(panel, aspect) && scene.Rig.Runtime.Calls.Count == requests &&
            scene.Rig.Native.Controls.Count == commands &&
            AutomationProperties.GetItemStatus(scene.Element<Button>("SettingsButton")) == "已展开",
            "opening the same settings menu changes only presentation and accessibility");
        rate.SelectedIndex = 0; aspect.SelectedIndex = 1;
        await Until(() => scene.Rig.Native.Rate == .5 && scene.Rig.Native.Aspect == "16:9");
        Check(scene.Rig.Session.Rate == .5 && scene.Rig.Session.Aspect == "16:9" &&
            scene.Rig.Native.Controls.Count == commands + 2 && scene.Rig.Native.Commits == 1 &&
            scene.Rig.Native.SettledIdentity == "overlay" && scene.Rig.Native.Controls.Skip(commands).All(call => call.Scope == scene.Rig.Native.Scope),
            "settings only control the accepted occurrence");
        Click(scene.Element<Button>("SettingsButton")); await Pump();
        Check(!Visible(panel) && AutomationProperties.GetItemStatus(scene.Element<Button>("SettingsButton")) == "已收起",
            "the same settings menu closes in place with its collapsed accessible state");
        Check(!Visible(scene.Element<Slider>("VolumeSlider")), "volume starts folded beside mute");
        Call(scene.View, "Volume_Enter", scene.Element<StackPanel>("VolumeGroup"), null);
        Check(Visible(scene.Element<Slider>("VolumeSlider")), "volume entry reveals the existing slider");
        scene.Element<Slider>("VolumeSlider").Value = 37;
        await Until(() => scene.Rig.Native.Volume == 37);
        Click(scene.Element<Button>("MuteButton")); await Until(() => scene.Rig.Native.Volume == 0);
        Check(AutomationProperties.GetName(scene.Element<Button>("MuteButton")) == "取消静音",
            "muting updates the accessible action");
        Click(scene.Element<Button>("MuteButton")); await Until(() => scene.Rig.Native.Volume == 37);
        Call(scene.View, "Volume_Leave", scene.Element<StackPanel>("VolumeGroup"), null);
        Check(!Visible(scene.Element<Slider>("VolumeSlider")) && scene.Rig.Native.Commits == 1,
            "unmute restores the audible volume and pointer exit folds the same slider without new media");
    }

    private static async Task ControlVisibility()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        var chrome = scene.Element<Grid>("PlayerChrome");
        var overlay = scene.Element<Grid>("PlayerOverlay");
        int commands = scene.Rig.Native.Controls.Count;
        var elapsed = Stopwatch.StartNew();
        Call(scene.View, "Player_MouseEnter", overlay, null); await Task.Delay(2000);
        Check(chrome.Opacity == 1 && chrome.IsHitTestVisible, "playing controls remain available before the 2200ms deadline");
        await Until(() => chrome.Opacity == 0);
        Check(elapsed.ElapsedMilliseconds >= 2200 && !chrome.IsHitTestVisible && chrome.Visibility == Visibility.Visible,
            "idle controls lose input but keep their measured tree instead of flashing through destruction");
        var playBounds = Bounds(scene.Element<Button>("PlayButton"), overlay);
        Check(VisualTreeHelper.FindElementsInHostCoordinates(new global::Windows.Foundation.Point(
                playBounds.X + playBounds.Width / 2, playBounds.Y + playBounds.Height / 2), overlay)
            .All(hit => !DesktopVisual.Contains(chrome, hit)), "the folded play action cannot receive a hit at its former location");
        Call(scene.View, "Player_MouseMove", overlay, null);
        Check(chrome.Opacity == 1, "pointer presentation operation restores controls immediately");
        Call(scene.View, "Player_MouseLeave", overlay, null);
        Check(chrome.Opacity == 0 && !chrome.IsHitTestVisible, "leaving ordinary playing video folds its controls immediately");
        Call(scene.View, "Player_MouseEnter", overlay, null);
        Check(chrome.Opacity == 1 && scene.Rig.Native.Controls.Count == commands && scene.Rig.Native.Commits == 1,
            "pointer visibility alone never dispatches a playback command");
        await scene.Rig.Session.SetPausedAsync(true); Call(scene.View, "Player_MouseLeave", overlay, null); await Hold();
        Click(scene.Element<Button>("PlayButton")); await Until(() => scene.Rig.Page.IsPlaying);
        scene.Rig.Runtime.Change(root => root["stage"] = "resolving"); await Until(() => scene.Rig.Page.PlayerBusy);
        Call(scene.View, "Player_MouseLeave", overlay, null); await Hold();
        scene.Rig.Runtime.Change(root => root["stage"] = "playing"); await Until(() => !scene.Rig.Page.PlayerBusy);
        int requests = scene.Rig.Runtime.Calls.Count;
        Click(scene.Element<Button>("SettingsButton")); Call(scene.View, "Player_MouseLeave", overlay, null); await Hold();
        Check(Visible(scene.Element<Border>("SettingsPanel")) && scene.Rig.Runtime.Calls.Count == requests,
            "the open settings menu survives the idle deadline and pointer exit without a business read");
        Click(scene.Element<Button>("PanButton")); Call(scene.View, "Player_MouseLeave", overlay, null); await Hold();
        var menu = Field<MenuFlyout>(scene.View, "playerMenu");
        Check(menu.IsOpen && !Visible(scene.Element<Border>("SettingsPanel")) && scene.Rig.Runtime.Calls.Count == requests,
            "the provider menu closes local settings without fetching data");
        menu.Hide(); await Pump();
        var play = scene.Element<Button>("PlayButton");
        Check(play.Focus(FocusState.Keyboard), "the mounted control accepts actual framework focus");
        await scene.View.HandlePlayerKeyAsync(VirtualKey.F8, false, VirtualKeyModifiers.None, play);
        Call(scene.View, "Player_MouseLeave", overlay, null); await Hold();
        scene.Element<Button>("HeroPlayButton").Focus(FocusState.Programmatic);
        Call(scene.View, "Player_MouseMove", overlay, null); Call(scene.View, "Player_MouseLeave", overlay, null);
        Check(chrome.Opacity == 0 && scene.Rig.Native.Commits == 1,
            "releasing control focus and menu reasons restores normal pointer-exit behavior on the same output");
        async Task Hold() { await Task.Delay(2300); Check(chrome.Opacity == 1 && chrome.IsHitTestVisible, "busy paused or menu-open controls stay usable"); }
    }

    private static async Task Buffering()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        var spinner = scene.Element<ProgressRing>("BufferSpinner"); int requests = scene.Rig.Runtime.Calls.Count;
        Check(!spinner.IsActive && !Visible(spinner), "settled output has no fabricated buffering");
        scene.Rig.Native.SetBuffering(true); await Until(() => spinner.IsActive);
        Check(!scene.Rig.Page.PlayerBusy && !spinner.IsHitTestVisible && DesktopVisual.Contains(scene.Surface, spinner),
            "the actual native buffering state owns the same-tree noninteractive indicator");
        await scene.Rig.Session.SetPausedAsync(true); await Until(() => !spinner.IsActive);
        await scene.Rig.Session.SetPausedAsync(false); await Until(() => spinner.IsActive);
        scene.Rig.Native.SetBuffering(false); await Until(() => !spinner.IsActive);
        Check(scene.Rig.Runtime.Calls.Count == requests && scene.Rig.Native.Commits == 1,
            "buffering changes do not ask the runtime for data or replace media");
    }

    private static async Task NarrowWidths()
    {
        await using var scene = await Scene.Create(); await scene.Play(); await scene.Rig.Session.SetPausedAsync(true);
        var frame = scene.Element<Grid>("PlayerFrame");
        var optional = new[] { "PanButton", "QualityButton", "SwitchButton", "BlockButton", "PipButton" }.Select(scene.Element<Button>).ToArray();
        foreach (double width in new[] { 1200d, 900, 700, 500, 380, 264, 380, 700, 1200 })
        {
            frame.Width = width; scene.View.UpdateLayout(); await Pump();
            var hidden = optional.Select(button => button.Visibility == Visibility.Collapsed).ToArray();
            Check(hidden.SequenceEqual(Enumerable.Range(0, hidden.Length).Select(index => index < hidden.Count(value => value))),
                "narrowing only removes the configured optional prefix");
            var row = scene.Element<Grid>("ControlsRow");
            var left = scene.Element<Border>("LeftPill"); var right = scene.Element<Border>("RightPill");
            Check(Visible(scene.Element<Button>("SettingsButton")) && Visible(scene.Element<Button>("PlayButton")) &&
                Visible(scene.Element<Button>("FullscreenButton")) &&
                (Grid.GetRow(right) == 1 || Bounds(left, row).Right + 10 <= Bounds(right, row).Left),
                "essential actions remain accessible, wrapping their capsule when one row cannot fit");
        }
        Check(optional.All(Visible) && scene.Rig.Native.Commits == 1, "widening restores all actions without another media commit");
    }

    private static async Task ShortVideo()
    {
        await using var scene = await Scene.Create();
        var button = scene.Element<CheckBox>("ShortVideoButton"); var hero = scene.Element<StackPanel>("HeroActions");
        var gesture = scene.Element<Border>("ShortVideoGesture");
        Check(hero.Children.IndexOf(button) == hero.Children.IndexOf(scene.Element<Button>("HeroPlayButton")) + 1 &&
            !DesktopVisual.Contains(scene.Surface, button) && !Visible(gesture), "short-video choice belongs to the hero action, not a player overlay duplicate");
        Click(button); await Until(() => scene.ActionCount("shortvideo") == 1);
        scene.Rig.Runtime.Change(root => root["shortVideo"] = true); await Until(() => Visible(gesture));
        Check(gesture.Child is null && DesktopVisual.Contains(scene.Surface, gesture) && scene.Rig.Native.Commits == 0,
            "accepted short-video state enables only the existing gesture plane");
    }

    private static async Task DelayedClick()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        Call(scene.View, "SchedulePictureClick"); await Task.Delay(120);
        Check(!scene.Rig.Native.Paused, "picture activation waits for the double-tap decision");
        await Until(() => scene.Rig.Native.Paused);
        await scene.Rig.Session.SetPausedAsync(false);
        int pauses = scene.CommandCount("pause");
        Call(scene.View, "SchedulePictureClick"); Call(scene.View, "ToggleFullscreen"); await Task.Delay(300);
        Check(scene.CommandCount("pause") == pauses && !scene.Rig.Native.Paused, "fullscreen cancels the delayed single action");
        scene.PlayerWindow!.Close(); await Until(() => scene.PlayerWindow is null);
        Call(scene.View, "SchedulePictureClick"); await scene.Replace("new-click"); await Task.Delay(300);
        Check(!scene.Rig.Native.Paused, "a delayed click cannot reach a replacement occurrence");
    }

    private static async Task KeyboardOwnership()
    {
        await using var scene = await Scene.Create(); await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay");
        int pauses = scene.CommandCount("pause");
        Check(await scene.View.HandlePlayerKeyAsync(VirtualKey.Space, false, VirtualKeyModifiers.None, overlay), "picture Space is handled");
        Check(!await scene.View.HandlePlayerKeyAsync(VirtualKey.Space, true, VirtualKeyModifiers.None, overlay) && scene.CommandCount("pause") == pauses + 1,
            "a held Space does not repeat the transport action");
        await scene.Rig.Session.SeekAsync(40);
        await scene.View.HandlePlayerKeyAsync(VirtualKey.Right, false, VirtualKeyModifiers.None, overlay);
        Check(scene.Rig.Native.Position == 45, "picture arrow advances five seconds");
        await scene.View.HandlePlayerKeyAsync(VirtualKey.Left, false, VirtualKeyModifiers.None, overlay);
        Check(scene.Rig.Native.Position == 40, "picture arrow retreats five seconds");
        int commands = scene.Rig.Native.Controls.Count;
        foreach (var control in new FrameworkElement[] { scene.Element<Button>("PlayButton"), scene.Element<Slider>("SeekSlider"),
            scene.Element<ListBox>("RateBox"), scene.Element<ComboBox>("SourcePicker"), scene.Element<CheckBox>("ShortVideoButton") })
            foreach (var key in new[] { VirtualKey.Space, VirtualKey.Left, VirtualKey.Right })
                Check(!await scene.View.HandlePlayerKeyAsync(key, false, VirtualKeyModifiers.None, control), "controls retain their own key ownership");
        Check(scene.Rig.Native.Controls.Count == commands &&
            !await scene.View.HandlePlayerKeyAsync(VirtualKey.Space, false, VirtualKeyModifiers.Control, overlay),
            "control and modified key routes never dispatch a second playback action");
        var seek = scene.Element<Slider>("SeekSlider");
        scene.View.BeginKeyboardSeek(false); seek.Value = 83;
        Call(scene.View, "Seek_LostFocus", seek, null);
        scene.View.BeginKeyboardSeek(true); await (Task)Call(scene.View, "CommitSeekAsync")!;
        Check(scene.Rig.Native.Controls.Count == commands && scene.Rig.Native.Position == 40,
            "a held key after focus cancellation cannot reopen the old transaction");
        scene.View.BeginKeyboardSeek(false); seek.Value = 120;
        await scene.Replace("keyboard-replacement");
        int afterReplacement = scene.CommandCount("seek");
        scene.View.BeginKeyboardSeek(true); await (Task)Call(scene.View, "CommitSeekAsync")!;
        Check(scene.CommandCount("seek") == afterReplacement && scene.Rig.Native.Position == 0,
            "a held key cannot adopt a replacement occurrence");
        scene.View.BeginKeyboardSeek(false); seek.Value = .1; await (Task)Call(scene.View, "CommitSeekAsync")!;
        Check(scene.Rig.Native.Position == .1 && scene.CommandCount("seek") == afterReplacement + 1,
            "a fresh keyboard gesture controls the new occurrence exactly once");
    }

    private static async Task SeekFences()
    {
        await using var scene = await Scene.Create(); await scene.Play(); await scene.Rig.Session.SetPausedAsync(true);
        await scene.Rig.Session.SeekAsync(31); var seek = scene.Element<Slider>("SeekSlider");
        int seeks = scene.CommandCount("seek");
        Call(scene.View, "Seek_Begin", seek, null); seek.Value = 83;
        Check(scene.Rig.Native.Position == 31 && scene.Element<TextBlock>("PlayerTime").Text.StartsWith("1:23 /", StringComparison.Ordinal),
            "range edits preview without controlling native media");
        await (Task)Call(scene.View, "CommitSeekAsync")!;
        Check(scene.Rig.Native.Position == 83 && scene.CommandCount("seek") == seeks + 1, "release commits once to its captured occurrence");
        foreach (string cancellation in new[] { "Seek_LostFocus", "Seek_CaptureLost" })
        {
            seeks = scene.CommandCount("seek"); Call(scene.View, "Seek_Begin", seek, null); seek.Value = 120;
            Call(scene.View, cancellation, seek, null); await (Task)Call(scene.View, "CommitSeekAsync")!;
            Check(scene.Rig.Native.Position == 83 && seek.Value == 83 && scene.CommandCount("seek") == seeks,
                "focus or capture loss cancels without committing an old release");
        }
        seeks = scene.CommandCount("seek"); Call(scene.View, "Seek_Begin", seek, null); seek.Value = 150;
        await scene.Replace("seek-replacement"); seek.Value = 170; await (Task)Call(scene.View, "CommitSeekAsync")!;
        Check(scene.Rig.Native.Position == 0 && seek.Value == 0 && scene.CommandCount("seek") == seeks,
            "occurrence replacement invalidates the pending range transaction");
        Call(scene.View, "Seek_Begin", seek, null); seek.Value = 25; await (Task)Call(scene.View, "CommitSeekAsync")!;
        Check(scene.Rig.Native.Position == 25 && scene.CommandCount("seek") == seeks + 1, "a fresh range transaction controls the new occurrence");
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmVideoSurface Surface = new();
        internal readonly FilmPlaybackRig Rig;
        internal readonly FilmPlaybackView View = new();
        internal readonly Grid Root = new();
        internal readonly DesktopWindow Window;
        internal bool Closed;
        private Scene() { Rig = new(Surface); Root.Children.Add(View); Window = FilmUiTest.Window(Root, 1900, 950); }
        internal static async Task<Scene> Create()
        {
            var scene = new Scene();
            try { await scene.Rig.OpenAsync(); scene.View.DataContext = scene.Rig.Page; scene.Window.Show(); await Until(() => scene.Surface.IsLoaded); return scene; }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal T Element<T>(string name) where T : FrameworkElement => Named<T>(View, name);
        internal Button Action(string name) => Visuals(View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        internal DesktopWindow? PlayerWindow => DesktopWindow.GetWindow(Element<Grid>("PlayerFrame")) is { } owner && !ReferenceEquals(owner, Window) ? owner : null;
        internal int ActionCount(string action) => Rig.Runtime.Calls.Count(call => FilmJson.Text(call.Parameters, "action") == action);
        internal int CommandCount(string action) => Rig.Native.Controls.Count(call => call.Command == action);
        internal Task Play() => Replace("overlay", 1);
        internal async Task Replace(string id, int episode = 2)
        {
            Rig.Runtime.Attempt(id, Candidate(id, episode: episode), target: Rig.Runtime.Target with { Season = 1, Episode = episode, GlobalEpisode = episode });
            await Until(() => Rig.Native.SettledIdentity == id && Rig.Page.CurrentAttemptId == id && Rig.Session.HasOutput);
            View.UpdateLayout(); await Pump();
        }
        public async ValueTask DisposeAsync()
        {
            PlayerWindow?.Close(); View.DataContext = null;
            if (!Closed) Window.Close();
            await Rig.DisposeAsync(); Surface.Dispose();
        }
    }
}
