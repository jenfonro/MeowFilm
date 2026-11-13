using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using Path = System.Windows.Shapes.Path;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlayerOverlayTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("picture owns the progress rail and ordered left and right controls", PictureLayout);
        yield return Test("local settings contain the supported rates and aspects and preserve the native occurrence", Settings);
        yield return Test("playing controls hide after 2200ms and return on pointer movement", ControlsHideAndReturn);
        yield return Test("pause busy menus and keyboard control focus keep controls available", ControlsStayAvailable);
        yield return Test("native buffering pulses the picture without opening media or requesting business data", Buffering);
        yield return Test("narrow widths hide pan quality switch block and PiP in order and restore them", NarrowWidths);
        yield return Test("short video belongs beside the hero play action outside the picture", ShortVideoEntry);
        yield return Test("single click waits 220ms and double click changes only fullscreen", PictureClicks);
        yield return Test("routed Space and five second arrows run once and leave control keys to their controls", KeyboardInput);
        yield return Test("keyboard seek cancels on focus loss and cannot cross a media replacement", KeyboardSeekCancellation);
        yield return Test("range pointer previews before release and cancels on focus loss or occurrence replacement", PointerSeek);
        yield return Test("real HWND foreground survives two fullscreen and PiP round trips without decoding", NativeHostRoundTrips);
        yield return Test("real HWND foreground clips to the page and releases hidden and replaced views", NativeHostVisibility);
    }

    private static TestCase Test(string name, Func<Task> run) =>
        new("film-player-overlay: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task PictureLayout()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Pause();
        var picture = scene.Element<Grid>("VideoPicture");
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var chrome = scene.Element<Grid>("PlayerChrome");
        var bar = scene.Element<StackPanel>("ControlBar");
        var seek = scene.Element<Slider>("SeekSlider");
        var row = scene.Element<Grid>("ControlsRow");
        var left = scene.Element<Border>("LeftPill");
        var right = scene.Element<Border>("RightPill");
        Check(picture.Background is SolidColorBrush { Color: var color } && color == Colors.Black &&
            Math.Abs(picture.ActualHeight - picture.ActualWidth * 9 / 16) < 1,
            "the actual picture keeps its black 16:9 surface");
        Check(ReferenceEquals(scene.Rig.Native.View.Content, overlay) &&
            !picture.Children.Contains(overlay) && overlay.IsDescendantOf(scene.Rig.Native.View) &&
            ReferenceEquals(scene.Element<ContentControl>("VideoHost").Content, scene.Rig.Native.View),
            "the fake media surface owns the same overlay through Content rather than a sibling above the video HWND");
        Check(bar.IsDescendantOf(chrome) && chrome.IsDescendantOf(overlay) &&
            bar.Children.Cast<UIElement>().SequenceEqual(new UIElement[] { seek, row }) &&
            bar.VerticalAlignment == VerticalAlignment.Bottom &&
            seek.TranslatePoint(new Point(), overlay).Y < left.TranslatePoint(new Point(), overlay).Y,
            "the progress rail precedes both capsules at the bottom inside the picture");
        var leftItems = ((StackPanel)left.Child).Children.Cast<FrameworkElement>().ToArray();
        Check(leftItems.SequenceEqual(new FrameworkElement[]
        {
            scene.Element<Button>("PlayButton"), scene.Button("上一集"), scene.Button("下一集"),
            scene.Element<StackPanel>("VolumeGroup"), scene.Element<TextBlock>("PlayerTime")
        }), "the left capsule orders play previous next volume and elapsed time");
        var rightItems = ((StackPanel)right.Child).Children.Cast<FrameworkElement>().ToArray();
        Check(rightItems.SequenceEqual(new FrameworkElement[]
        {
            scene.Element<Button>("PanButton"), scene.Element<Button>("QualityButton"),
            scene.Element<Button>("SwitchButton"), scene.Element<Button>("BlockButton"),
            scene.Element<Button>("PipButton"), scene.Button("播放器设置"), scene.Element<Button>("FullscreenButton")
        }) && left.HorizontalAlignment == HorizontalAlignment.Left && right.HorizontalAlignment == HorizontalAlignment.Right &&
            right.TranslatePoint(new Point(), overlay).X > left.TranslatePoint(new Point(), overlay).X,
            "the right capsule keeps the required business and window actions in order");
        var pan = scene.Element<Button>("PanButton");
        var quality = scene.Element<Button>("QualityButton");
        var icons = new[] { scene.Element<Button>("PlayButton"), scene.Button("上一集"), scene.Button("下一集"),
            scene.Element<Button>("MuteButton"), scene.Element<Button>("PipButton"), scene.Element<Button>("SettingsButton"),
            scene.Element<Button>("FullscreenButton") };
        Check(ReferenceEquals(pan.DataContext, scene.Rig.Page) && ReferenceEquals(seek.DataContext, scene.Rig.Page) &&
            seek.Maximum == scene.Rig.Page.Duration,
            "both labels and progress inherit the current page through the reparented overlay");
        Check(Equals(pan.Content, "夸克"), $"current pan label must be 夸克; actual={Describe(pan.Content)}");
        Check(Equals(quality.Content, "1080P"), $"current quality label must be 1080P; actual={Describe(quality.Content)}");
        foreach (var button in icons)
        {
            Check(button.Content is Path,
                $"icon action must contain a Path; name={AutomationProperties.GetName(button)} button={Describe(button)} content={Describe(button.Content)}");
            var icon = (Path)button.Content;
            var clip = LayoutInformation.GetLayoutClip(icon);
            Check(clip is null || clip.FillContainsWithDetail(icon.RenderedGeometry) == IntersectionDetail.FullyContains,
                $"the complete icon must survive layout before scaling; name={AutomationProperties.GetName(button)}");
            Check(new Rect(button.RenderSize).Contains(icon.TransformToAncestor(button).TransformBounds(icon.RenderedGeometry.Bounds)),
                $"the full icon must fit its circular button; name={AutomationProperties.GetName(button)}");
        }

        int commands = scene.Rig.Runtime.Calls.Count;
        Click(scene.Button("上一集"));
        await Until(() => scene.ActionCount("previous") == 1);
        Click(scene.Button("下一集"));
        await Until(() => scene.ActionCount("next") == 1);
        Check(scene.Rig.Runtime.Calls.Count == commands + 2 && scene.Rig.Native.Commits == 1 &&
            scene.Rig.Native.SettledIdentity == "overlay",
            "the actual previous and next controls send one exact navigation command each without claiming another output");

        var volume = scene.Element<Slider>("VolumeSlider");
        var group = scene.Element<StackPanel>("VolumeGroup");
        Check(!volume.IsVisible, "volume starts folded beside mute");
        Move(group, Mouse.MouseEnterEvent);
        await Until(() => volume.IsVisible);
        volume.Value = 37;
        await Until(() => scene.Rig.Native.Volume == 37);
        Click(scene.Button("静音"));
        Check(scene.Rig.Native.Volume == 0 && AutomationProperties.GetName(scene.Element<Button>("MuteButton")) == "取消静音",
            "mute reaches the existing owner and updates the accessible action");
        Click(scene.Button("取消静音"));
        Check(scene.Rig.Native.Volume == 37, "unmute restores the user's actual audible volume");
        Move(group, Mouse.MouseLeaveEvent);
        Check(!volume.IsVisible && scene.Rig.Native.Commits == 1, "leaving volume folds its slider without opening media");
    }

    private static async Task Settings()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Pause();
        var panel = scene.Element<Border>("SettingsPanel");
        var rate = scene.Element<ListBox>("RateBox");
        var aspect = scene.Element<ListBox>("AspectBox");
        Check(panel.IsDescendantOf(scene.Element<Grid>("PlayerChrome")) &&
            !panel.IsVisible && !rate.IsVisible && !aspect.IsVisible,
            "rate and aspect live exclusively in the initially closed picture settings menu");
        Check(rate.Items.Cast<ListBoxItem>().Select(item => (string)item.Tag)
                .SequenceEqual(new[] { "0.5", "0.75", "1", "1.25", "1.5", "2" }) &&
            aspect.Items.Cast<ListBoxItem>().Select(item => (string)item.Tag)
                .SequenceEqual(new[] { "", "16:9", "4:3" }) &&
            rate.SelectedIndex == 2 && aspect.SelectedIndex == 0,
            "settings expose the frozen speed and aspect choices with the actual native defaults");
        int calls = scene.Rig.Runtime.Calls.Count, nativeControls = scene.Rig.Native.Controls.Count;
        Click(scene.Button("播放器设置"));
        await Until(() => panel.IsVisible && rate.IsVisible && aspect.IsVisible);
        Check(rate.IsDescendantOf(panel) && aspect.IsDescendantOf(panel),
            "the rendered settings scroll content keeps rate and aspect inside the picture menu");
        Check(scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Controls.Count == nativeControls &&
            AutomationProperties.GetItemStatus(scene.Element<Button>("SettingsButton")) == "已展开",
            "opening settings changes presentation and accessibility without a business or native command");
        await Task.Delay(200);
        Shot(scene.Element<Grid>("VideoPicture"), "settings-open");
        rate.SelectedIndex = 0;
        aspect.SelectedIndex = 1;
        await Until(() => scene.Rig.Native.Rate == .5 && scene.Rig.Native.Aspect == "16:9");
        Check(scene.Rig.Session.Rate == .5 && scene.Rig.Session.Aspect == "16:9" &&
            scene.Rig.Native.Controls.Count == nativeControls + 2 &&
            scene.Rig.Native.Controls.Skip(nativeControls).All(call => call.Scope == scene.Rig.Native.Scope) &&
            scene.Rig.Native.Commits == 1 && scene.Rig.Native.SettledIdentity == "overlay",
            "the added half speed and existing aspect choice control only the accepted occurrence");
        Click(scene.Button("播放器设置"));
        Check(!panel.IsVisible && AutomationProperties.GetItemStatus(scene.Element<Button>("SettingsButton")) == "已收起",
            "the settings action closes the same local menu");
    }

    private static async Task ControlsHideAndReturn()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var chrome = scene.Element<Grid>("PlayerChrome");
        var play = scene.Element<Button>("PlayButton");
        int controls = scene.Rig.Native.Controls.Count;
        var elapsed = Stopwatch.StartNew();
        Move(overlay, Mouse.MouseEnterEvent);
        await Task.Delay(2000);
        Check(chrome.IsVisible && chrome.IsHitTestVisible, "playing chrome remains available before the 2200ms deadline");
        await Until(() => !chrome.IsVisible);
        Check(elapsed.ElapsedMilliseconds >= 2200 && !chrome.IsHitTestVisible && !play.IsHitTestVisible,
            "the real dispatcher timer folds idle playing controls after 2200ms and removes their input hit area");
        var hit = overlay.InputHitTest(play.TranslatePoint(new Point(play.ActualWidth / 2, play.ActualHeight / 2), overlay));
        Check(hit is not DependencyObject target || !Within(target, chrome),
            "the hidden play button cannot receive a picture hit at its previous location");
        Move(overlay, Mouse.MouseMoveEvent);
        Check(chrome.IsVisible && chrome.IsHitTestVisible, "pointer movement immediately restores controls");
        Move(overlay, Mouse.MouseLeaveEvent);
        Check(!chrome.IsVisible && !chrome.IsHitTestVisible, "leaving ordinary playing video immediately folds its controls");
        Move(overlay, Mouse.MouseEnterEvent);
        Check(chrome.IsVisible && scene.Rig.Native.Controls.Count == controls && scene.Rig.Native.Commits == 1,
            "pointer visibility changes never execute a playback action");
    }

    private static async Task ControlsStayAvailable()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var chrome = scene.Element<Grid>("PlayerChrome");
        await scene.Pause();
        Move(overlay, Mouse.MouseLeaveEvent);
        await HoldVisible(chrome, "paused controls remain usable past the idle deadline and pointer exit");
        Click(scene.Element<Button>("PlayButton"));
        await Until(() => scene.Rig.Page.IsPlaying);
        scene.Rig.Runtime.Change(root => root["stage"] = "resolving");
        await Until(() => scene.Rig.Page.PlayerBusy);
        Move(overlay, Mouse.MouseLeaveEvent);
        await HoldVisible(chrome, "matching with a live donor keeps its controls available");
        scene.Rig.Runtime.Change(root => root["stage"] = "playing");
        await Until(() => !scene.Rig.Page.PlayerBusy);

        int calls = scene.Rig.Runtime.Calls.Count;
        Click(scene.Button("播放器设置"));
        Move(overlay, Mouse.MouseLeaveEvent);
        await HoldVisible(chrome, "an open settings panel survives the playing idle deadline and pointer exit");
        Check(scene.Element<Border>("SettingsPanel").IsVisible && scene.Rig.Runtime.Calls.Count == calls,
            "keeping settings open performs no business request");
        Click(scene.Element<Button>("PanButton"));
        var menu = scene.Element<Button>("PanButton").ContextMenu;
        Move(overlay, Mouse.MouseLeaveEvent);
        await HoldVisible(chrome, "an open provider menu keeps picture controls available");
        Check(menu.IsOpen && !scene.Element<Border>("SettingsPanel").IsVisible && scene.Rig.Runtime.Calls.Count == calls,
            "opening the provider menu locally closes settings and keeps the current data");
        menu.IsOpen = false;
        await Pump();

        var bar = scene.Element<StackPanel>("ControlBar");
        var focusKey = (DependencyPropertyKey)typeof(UIElement)
            .GetField("IsKeyboardFocusWithinPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        // Model WPF's control-focus state on this offscreen HWND; key handling still uses its full routed event path.
        bar.SetValue(focusKey, true);
        try
        {
            RouteKey(scene.Element<Button>("PlayButton"), Key.F8);
            Move(overlay, Mouse.MouseLeaveEvent);
            await HoldVisible(chrome, "keyboard focus in the control bar prevents the idle timer and pointer exit from hiding it");
        }
        finally { bar.SetValue(focusKey, false); }
        Move(overlay, Mouse.MouseMoveEvent);
        Move(overlay, Mouse.MouseLeaveEvent);
        Check(!chrome.IsVisible && scene.Rig.Native.Commits == 1,
            "releasing the keep-visible reasons restores ordinary pointer-exit behavior on the same output");
    }

    private static async Task Buffering()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        var spinner = scene.Element<Path>("BufferSpinner");
        var rotation = (RotateTransform)spinner.RenderTransform;
        var chrome = scene.Element<Grid>("PlayerChrome");
        Check(!spinner.IsVisible && !scene.Rig.Page.PlayerBuffering && !rotation.HasAnimatedProperties,
            "settled native output has no buffering pulse");
        int calls = scene.Rig.Runtime.Calls.Count, controls = scene.Rig.Native.Controls.Count;
        scene.Rig.Native.SetBuffering(true);
        await Until(() => scene.Rig.Page.PlayerBuffering && spinner.IsVisible);
        Check(spinner.IsDescendantOf(scene.Element<Grid>("PlayerOverlay")) && !spinner.IsHitTestVisible &&
            !scene.Rig.Page.PlayerBusy && !scene.Element<StackPanel>("PlayerStatusPanel").IsVisible &&
            spinner.HorizontalAlignment == HorizontalAlignment.Center && spinner.VerticalAlignment == VerticalAlignment.Center &&
            rotation.HasAnimatedProperties == SystemParameters.ClientAreaAnimation,
            "native buffering alone drives the central noninteractive pulse rather than inventing a preparation stage");
        await HoldVisible(chrome, "buffering keeps playback controls available beyond the idle deadline");
        Check(scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Controls.Count == controls &&
            scene.Rig.Native.Commits == 1 && scene.Rig.Native.SettledIdentity == "overlay",
            "a native buffering property pulse performs no business request or playback command");
        await scene.Pause();
        await Until(() => !scene.Rig.Page.PlayerBuffering && !spinner.IsVisible);
        Check(!rotation.HasAnimatedProperties, "pause releases the buffering animation clock");
        Click(scene.Element<Button>("PlayButton"));
        await Until(() => scene.Rig.Page.PlayerBuffering && spinner.IsVisible);
        scene.Rig.Native.SetBuffering(false);
        await Until(() => !scene.Rig.Page.PlayerBuffering && !spinner.IsVisible);
        Check(!rotation.HasAnimatedProperties && scene.Rig.Native.Commits == 1,
            "buffer completion removes the pulse and animation without replacing the accepted media");
    }

    private static async Task NarrowWidths()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Pause();
        var frame = scene.Element<Grid>("PlayerFrame");
        var optional = new[]
        {
            scene.Element<Button>("PanButton"), scene.Element<Button>("QualityButton"),
            scene.Element<Button>("SwitchButton"), scene.Element<Button>("BlockButton"), scene.Element<Button>("PipButton")
        };
        var hiddenCounts = new List<int>();
        var restoredCounts = new List<int>();
        async Task Resize(double width, List<int> states)
        {
            frame.Width = width;
            await Pump();
            scene.View.UpdateLayout();
            var hidden = optional.Select(button => button.Visibility == Visibility.Collapsed).ToArray();
            int count = hidden.Count(value => value);
            Check(hidden.SequenceEqual(Enumerable.Range(0, optional.Length).Select(index => index < count)),
                $"narrowing may only remove a prefix of pan quality switch block and PiP; requested={width} hidden={string.Join(",", hidden)}");
            Check(scene.Button("播放器设置").IsVisible && scene.Element<Button>("FullscreenButton").IsVisible &&
                scene.Element<Button>("PlayButton").IsVisible,
                "settings fullscreen and play remain available at each real layout width");
            var row = scene.Element<Grid>("ControlsRow");
            var left = scene.Element<Border>("LeftPill");
            var right = scene.Element<Border>("RightPill");
            double leftEnd = left.TranslatePoint(new Point(left.ActualWidth, 0), row).X;
            double rightStart = right.TranslatePoint(new Point(0, 0), row).X;
            Check(leftEnd + 11 <= rightStart &&
                right.TranslatePoint(new Point(right.ActualWidth, 0), row).X <= row.ActualWidth + 1,
                $"the essential capsules retain their gap without overlapping or escaping the picture; width={width} leftEnd={leftEnd} rightStart={rightStart}");
            if (states.Count == 0 || states[^1] != count) states.Add(count);
        }
        for (double width = 1200; width >= 264; width -= 12) await Resize(width, hiddenCounts);
        for (double width = 264; width <= 1200; width += 12) await Resize(width, restoredCounts);
        Check(hiddenCounts.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }),
            $"all five narrowing transitions must occur in order; actual={string.Join(",", hiddenCounts)} expected=0,1,2,3,4,5");
        Check(restoredCounts.SequenceEqual(new[] { 5, 4, 3, 2, 1, 0 }),
            $"all five widening transitions must occur in reverse; actual={string.Join(",", restoredCounts)} expected=5,4,3,2,1,0");
        Check(scene.Rig.Native.Commits == 1 && scene.Rig.Native.SettledIdentity == "overlay",
            $"resizing must preserve media; commits={scene.Rig.Native.Commits} identity={scene.Rig.Native.SettledIdentity}");
        frame.ClearValue(FrameworkElement.WidthProperty);
        foreach (int width in new[] { 420, 860 })
        {
            scene.Window.Width = width;
            await Pump();
            Shot(scene.View, $"window-{width}");
        }
        Move(scene.Element<StackPanel>("VolumeGroup"), Mouse.MouseEnterEvent);
        await Resize(380, new List<int>());
        Check(scene.Element<Slider>("VolumeSlider").IsVisible,
            "minimum PiP width still exposes the expanded volume control without overlapping capsules");
        Click(scene.Button("播放器设置"));
        await Pump();
        var settings = scene.Element<Border>("SettingsPanel");
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var scroll = scene.Element<ScrollViewer>("SettingsScroll");
        Check(settings.TranslatePoint(new Point(), overlay).Y >= 0 && scroll.ScrollableHeight > 0,
            "a short picture keeps settings within its bounds and makes clipped choices scrollable");
        scroll.ScrollToEnd();
        await Pump();
        var lastAspect = (ListBoxItem)scene.Element<ListBox>("AspectBox").Items[2];
        Check(lastAspect.TranslatePoint(new Point(lastAspect.ActualWidth, lastAspect.ActualHeight), settings).Y <= settings.ActualHeight,
            "the last aspect choice remains reachable in the minimum-size picture");
        Shot(scene.Element<Grid>("VideoPicture"), "settings-compact");
    }

    private static async Task ShortVideoEntry()
    {
        await using var scene = await Scene.Create();
        var hero = scene.Element<StackPanel>("HeroActions");
        var button = scene.Element<CheckBox>("ShortVideoButton");
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var gesture = scene.Element<Border>("ShortVideoGesture");
        Check(hero.Children.IndexOf(button) == hero.Children.IndexOf(scene.Element<Button>("HeroPlayButton")) + 1 &&
            !button.IsDescendantOf(overlay) && button.IsVisible &&
            !Visuals(overlay).OfType<CheckBox>().Any(box => Equals(box.Content, "短视频")) &&
            !gesture.IsVisible,
            "short video is the page action immediately beside hero play and its picture gesture starts hidden");
        int calls = scene.Rig.Runtime.Calls.Count;
        RouteKey(button, Key.Space);
        RouteKey(button, Key.Space, release: true);
        await Until(() => scene.ActionCount("shortvideo") == 1);
        var command = scene.Rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "shortvideo");
        Check(FilmJson.Bool(command.Parameters, "enabled") && scene.Rig.Runtime.Calls.Count == calls + 1 &&
            scene.Rig.Native.Commits == 0,
            "the actual hero checkbox key route sends only the explicit short-video intent");
        scene.Rig.Runtime.Change(root => root["shortVideo"] = true);
        await Until(() => scene.Rig.Page.ShortVideo && button.IsChecked == true && gesture.IsVisible);
        Check(gesture.IsDescendantOf(overlay) && gesture.Child is null &&
            Math.Abs(gesture.ActualWidth - overlay.ActualWidth) < 1 &&
            Math.Abs(gesture.ActualHeight - overlay.ActualHeight) < 1,
            "accepted short-video mode enables a full-picture gesture border without another text row");
    }

    private static async Task PictureClicks()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        var overlay = scene.Element<Grid>("PlayerOverlay");
        int pauses = scene.NativeCount("pause");
        var elapsed = Stopwatch.StartNew();
        RouteMouse(overlay, Mouse.PreviewMouseDownEvent);
        await Task.Delay(120);
        Check(scene.NativeCount("pause") == pauses && !scene.Rig.Native.Paused,
            "one picture click cannot toggle playback before the 220ms disambiguation deadline");
        await Until(() => scene.Rig.Native.Paused);
        Check(elapsed.ElapsedMilliseconds >= 220 && scene.NativeCount("pause") == pauses + 1,
            "one completed picture click produces one delayed native pause");
        Click(scene.Element<Button>("PlayButton"));
        await Until(() => !scene.Rig.Native.Paused);
        pauses = scene.NativeCount("pause");
        RouteMouse(overlay, Mouse.PreviewMouseDownEvent);
        RouteMouse(overlay, Mouse.PreviewMouseDownEvent, clicks: 2);
        await Until(() => scene.PlayerWindow is not null);
        var full = scene.PlayerWindow!;
        await Task.Delay(300);
        Check(scene.NativeCount("pause") == pauses && !scene.Rig.Native.Paused &&
            scene.Rig.Native.Commits == 1 && scene.Element<Grid>("PlayerFrame").IsDescendantOf(full),
            "the double-click route cancels pending single-click playback and moves only the existing fullscreen host");
        await scene.ClosePlayerWindow();

        pauses = scene.NativeCount("pause");
        RouteMouse(overlay, Mouse.PreviewMouseDownEvent);
        scene.Rig.Runtime.Attempt("replacement-click", Candidate("replacement-click", episode: 2));
        await Until(() => scene.Rig.Native.SettledIdentity == "replacement-click");
        await Task.Delay(300);
        Check(scene.NativeCount("pause") == pauses && !scene.Rig.Native.Paused &&
            scene.Rig.Native.SettledIdentity == "replacement-click",
            "a pending picture click cannot pause a replacement occurrence");
    }

    private static async Task KeyboardInput()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Rig.Session.SeekAsync(40);
        var overlay = scene.Element<Grid>("PlayerOverlay");
        int pauses = scene.NativeCount("pause");
        Check(RouteKey(overlay, Key.Space).Handled, "picture Space is handled by its actual preview route");
        await Until(() => scene.Rig.Native.Paused);
        await Pump();
        Check(scene.NativeCount("pause") == pauses + 1,
            "Window and Overlay key subscriptions produce one pause rather than toggling twice");
        int seeks = scene.NativeCount("seek");
        RouteKey(overlay, Key.Right);
        await Until(() => scene.Rig.Native.Position == 45);
        RouteKey(overlay, Key.Left);
        await Until(() => scene.Rig.Native.Position == 40);
        Check(scene.NativeCount("seek") == seeks + 2 &&
            scene.Rig.Native.Controls.Where(call => call.Command == "seek").TakeLast(2)
                .Select(call => (double)call.Value!).SequenceEqual(new[] { 45d, 40d }),
            "the routed picture arrows each seek the exact accepted owner by five seconds once");

        var seek = scene.Element<Slider>("SeekSlider");
        seeks = scene.NativeCount("seek");
        double position = scene.Rig.Native.Position;
        RouteKey(seek, Key.Right);
        Check(scene.Rig.Native.Position == position && scene.NativeCount("seek") == seeks,
            $"a slider arrow must not send a global seek; before={position}/{seeks} actual={scene.Rig.Native.Position}/{scene.NativeCount("seek")}");
        Check(Math.Abs(seek.Value - (position + seek.SmallChange)) < .001,
            $"the slider arrow must use SmallChange; before={position} small={seek.SmallChange} expected={position + seek.SmallChange} actual={seek.Value}");
        RouteKey(seek, Key.Right, release: true);
        await Until(() => Math.Abs(scene.Rig.Native.Position - (position + seek.SmallChange)) < .001);
        Check(scene.NativeCount("seek") == seeks + 1, "the seek control commits its own arrow value once on release");
        Click(scene.Button("播放器设置"));
        pauses = scene.NativeCount("pause");
        RouteKey(scene.Element<Button>("PlayButton"), Key.Space);
        RouteKey(scene.Element<Button>("PlayButton"), Key.Space, release: true);
        await Until(() => !scene.Rig.Native.Paused);
        Check(scene.NativeCount("pause") == pauses + 1,
            "Space on the actual play button activates its own click exactly once without the picture handler duplicating it");
        pauses = scene.NativeCount("pause");
        seeks = scene.NativeCount("seek");
        int shortVideos = scene.ActionCount("shortvideo");
        var shortVideo = scene.Element<CheckBox>("ShortVideoButton");
        RouteKey(shortVideo, Key.Space);
        RouteKey(shortVideo, Key.Space, release: true);
        await Until(() => scene.ActionCount("shortvideo") == shortVideos + 1);
        foreach (var target in new UIElement[] { scene.Element<ListBox>("RateBox"), scene.Element<ComboBox>("SourcePicker") })
        {
            RouteKey(target, Key.Space);
            RouteKey(target, Key.Space, release: true);
            RouteKey(target, Key.Left);
            RouteKey(target, Key.Left, release: true);
            RouteKey(target, Key.Right);
            RouteKey(target, Key.Right, release: true);
        }
        await Pump();
        Check(scene.NativeCount("pause") == pauses && scene.NativeCount("seek") == seeks && !scene.Rig.Native.Paused &&
            scene.Rig.Native.SettledIdentity == "overlay",
            "checkbox selector and page-input key routes retain their own actions without picture playback or five-second seeks");
    }

    private static async Task KeyboardSeekCancellation()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Pause();
        await scene.Rig.Session.SeekAsync(31);
        var seek = scene.Element<Slider>("SeekSlider");
        int seeks = scene.NativeCount("seek");
        RouteKey(seek, Key.Right);
        Check(Math.Abs(seek.Value - 31.1) < .001 && scene.NativeCount("seek") == seeks,
            "a held seek key only previews its small step");
        seek.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, seek, scene.Element<Button>("PlayButton"))
        { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        RouteKey(seek, Key.Right, release: true);
        await Pump();
        Check(seek.Value == 31 && scene.Rig.Native.Position == 31 && scene.NativeCount("seek") == seeks,
            "focus loss cancels the keyboard preview without committing on the old key release");

        RouteKey(seek, Key.Right);
        scene.Rig.Runtime.Attempt("replacement-keyboard", Candidate("replacement-keyboard", episode: 2),
            target: scene.Rig.Runtime.Target with { Season = 1, Episode = 2, GlobalEpisode = 2 });
        await Until(() => scene.Rig.Native.SettledIdentity == "replacement-keyboard" &&
            scene.Rig.Page.CurrentAttemptId == "replacement-keyboard");
        RouteKey(seek, Key.Right, repeat: true);
        RouteKey(seek, Key.Right, release: true);
        await Pump();
        Check(seek.Value == 0 && scene.Rig.Native.Position == 0 && scene.NativeCount("seek") == seeks,
            $"an old keyboard release cannot seek the replacement occurrence; slider={seek.Value} native={scene.Rig.Native.Position} " +
            $"page={scene.Rig.Page.Position} seeks={scene.NativeCount("seek")}/{seeks} identity={scene.Rig.Page.MediaIdentity} " +
            $"preserve={scene.Rig.Native.Intents.Last().PreserveCurrent}");
        RouteKey(seek, Key.Right);
        RouteKey(seek, Key.Right, release: true);
        await Until(() => Math.Abs(scene.Rig.Native.Position - .1) < .001);
        Check(scene.NativeCount("seek") == seeks + 1,
            "a fresh key gesture controls the replacement exactly once");
    }

    private static async Task PointerSeek()
    {
        await using var scene = await Scene.Create();
        await scene.Play();
        await scene.Pause();
        await scene.Rig.Session.SeekAsync(31);
        var seek = scene.Element<Slider>("SeekSlider");
        var range = Visuals(seek).OfType<Track>().Single().IncreaseRepeatButton!;
        int seeks = scene.NativeCount("seek");
        RouteMouse(range, Mouse.PreviewMouseDownEvent);
        seek.Value = 83;
        await Pump();
        Check(scene.Rig.Native.Position == 31 && scene.NativeCount("seek") == seeks,
            $"range preview must not seek its native owner; expected=31/{seeks} actual={scene.Rig.Native.Position}/{scene.NativeCount("seek")}");
        Check(scene.Element<TextBlock>("PlayerTime").Text.StartsWith("1:23 /", StringComparison.Ordinal),
            $"the range route must preview 83 seconds; slider={seek.Value} time={scene.Element<TextBlock>("PlayerTime").Text} expectedPrefix=1:23 /");
        RouteMouse(range, Mouse.PreviewMouseUpEvent);
        await Until(() => scene.Rig.Native.Position == 83);
        Check(scene.NativeCount("seek") == seeks + 1 &&
            scene.Rig.Native.Controls.Last(call => call.Command == "seek").Scope == scene.Rig.Native.Scope,
            "release commits the preview once against the captured native occurrence");

        seeks = scene.NativeCount("seek");
        RouteMouse(range, Mouse.PreviewMouseDownEvent);
        seek.Value = 120;
        seek.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, seek, scene.Element<Button>("PlayButton"))
        { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Check(seek.Value == 83 && scene.Element<TextBlock>("PlayerTime").Text == scene.Rig.Page.ProgressLabel,
            "focus loss restores the actual position and elapsed label before a pending pointer release");
        RouteMouse(range, Mouse.PreviewMouseUpEvent);
        await Pump();
        Check(scene.NativeCount("seek") == seeks && scene.Rig.Native.Position == 83,
            "the old release after focus loss cannot commit the cancelled preview");

        var oldScope = scene.Rig.Native.Scope;
        RouteMouse(range, Mouse.PreviewMouseDownEvent);
        seek.Value = 155;
        scene.Rig.Runtime.Attempt("replacement-seek", Candidate("replacement-seek", episode: 2),
            target: scene.Rig.Runtime.Target with { Season = 1, Episode = 2, GlobalEpisode = 2 });
        await Until(() => scene.Rig.Native.SettledIdentity == "replacement-seek" && scene.Rig.Page.CurrentAttemptId == "replacement-seek");
        // A range update queued by the retired gesture is not a new pointer-down.
        seek.Value = 170;
        RouteMouse(range, Mouse.PreviewMouseUpEvent);
        await Pump();
        Check(scene.NativeCount("seek") == seeks && scene.Rig.Native.Scope != oldScope &&
            scene.Rig.Native.Position == 0 && seek.Value == scene.Rig.Page.Position &&
            scene.Element<TextBlock>("PlayerTime").Text == scene.Rig.Page.ProgressLabel,
            $"an old occurrence's pointer transaction cannot seek or leave its preview on the replacement; slider={seek.Value} " +
            $"native={scene.Rig.Native.Position} page={scene.Rig.Page.Position} seeks={scene.NativeCount("seek")}/{seeks} " +
            $"scopeChanged={scene.Rig.Native.Scope != oldScope} identity={scene.Rig.Page.MediaIdentity} " +
            $"time={scene.Element<TextBlock>("PlayerTime").Text}/{scene.Rig.Page.ProgressLabel} preserve={scene.Rig.Native.Intents.Last().PreserveCurrent}");
    }

    private static async Task NativeHostRoundTrips()
    {
        // Construct the actual HwndSource/VideoView only. The injected probe owns media;
        // NativePlayback.OpenAsync is never called and no libVLC engine or decoder is created.
        await using var native = new NativePlayback();
        await using var scene = await Scene.Create(native.VideoView);
        await scene.Play();
        await scene.Pause();
        var surface = native.VideoView;
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var frame = scene.Element<Grid>("PlayerFrame");
        await Until(() => Foreground(scene)?.IsVisible == true);
        AssertNativeMount(scene, native, scene.Window);
        await Task.Delay(200);
        Shot((FrameworkElement)Foreground(scene)!.Content, "native-foreground-paused");
        int calls = scene.Rig.Runtime.Calls.Count;
        for (int round = 0; round < 2; round++)
        {
            Click(scene.Element<Button>("FullscreenButton"));
            await Until(() => scene.PlayerWindow is { IsVisible: true } owner && Foreground(scene)?.Owner == owner);
            AssertNativeMount(scene, native, scene.PlayerWindow!);
            Check(AutomationProperties.GetName(scene.Element<Button>("FullscreenButton")) == "退出全屏" &&
                Equals(scene.Element<Button>("FullscreenButton").ToolTip, "退出全屏"),
                "the stable fullscreen button exposes its current exit action");
            await scene.ClosePlayerWindow();
            await Until(() => Foreground(scene)?.Owner == scene.Window && Foreground(scene)?.IsVisible == true);
            AssertNativeMount(scene, native, scene.Window);
            Click(scene.Element<Button>("PipButton"));
            await Until(() => scene.PlayerWindow is { IsVisible: true } owner && Foreground(scene)?.Owner == owner);
            Check(scene.Rig.Session.CanRetainMedia &&
                AutomationProperties.GetName(scene.Element<Button>("PipButton")) == "退出画中画" &&
                Equals(scene.Element<Button>("PipButton").ToolTip, "退出画中画"),
                "the real-HWND PiP window retains the explicit owner and exposes its current exit action");
            AssertNativeMount(scene, native, scene.PlayerWindow!);
            await scene.ClosePlayerWindow();
            await Until(() => Foreground(scene)?.Owner == scene.Window && Foreground(scene)?.IsVisible == true);
            AssertNativeMount(scene, native, scene.Window);
            Check(ReferenceEquals(native.VideoView, surface) && ReferenceEquals(scene.Element<Grid>("PlayerOverlay"), overlay) &&
                ReferenceEquals(scene.Element<Grid>("PlayerFrame"), frame) && !scene.Rig.Session.CanRetainMedia &&
                scene.Window.OwnedWindows.Cast<Window>().Count(window => window.IsVisible) == 1 &&
                AutomationProperties.GetName(scene.Element<Button>("FullscreenButton")) == "全屏" &&
                AutomationProperties.GetName(scene.Element<Button>("PipButton")) == "画中画" &&
                Equals(scene.Element<Button>("FullscreenButton").ToolTip, "全屏") &&
                Equals(scene.Element<Button>("PipButton").ToolTip, "画中画") &&
                scene.Rig.Runtime.Calls.Count == calls && scene.Rig.Native.Commits == 1 && scene.Rig.Native.Stops == 0,
                "each repeated fullscreen and PiP return preserves the same surface frame overlay and output without a retired visible foreground");
        }
        Check(!native.HasMedia && native.NativeVersion is null,
            "these real-HWND round trips never initialize a native decoding session");
    }

    private static async Task NativeHostVisibility()
    {
        await using var native = new NativePlayback();
        await using var scene = await Scene.Create(native.VideoView);
        await scene.Play();
        await scene.Pause();
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var picture = scene.Element<Grid>("VideoPicture");
        var scroll = scene.Element<ScrollViewer>("PageScroll");
        await Until(() => Foreground(scene)?.IsVisible == true);
        AssertNativeMount(scene, native, scene.Window);
        Check(scroll.ScrollableHeight > 80, "the real page fixture has enough content to exercise viewport clipping");
        scroll.ScrollToVerticalOffset(80);
        await Until(() => scroll.VerticalOffset >= 79);
        var clipped = overlay.Clip?.Bounds ?? Rect.Empty;
        Check(!clipped.IsEmpty && clipped.Top > 0, "scrolling clips the foreground's portion above the page viewport");
        AssertViewportClip(scene);
        scroll.ScrollToTop();
        await Until(() => scroll.VerticalOffset == 0);
        Check(overlay.Clip is { } restoredClip && restoredClip.Bounds.Top == 0 && restoredClip.Bounds.Height > clipped.Height,
            "returning to the top restores the picture portion formerly outside the viewport");
        var front = Foreground(scene)!;
        scene.View.Visibility = Visibility.Collapsed;
        await Until(() => !VisibleWindow(front));
        Check(!overlay.IsVisible && scene.Rig.Session.HasOutput && scene.Rig.Native.Commits == 1,
            "hiding the playback view leaves no independent visible native foreground");
        scene.View.Visibility = Visibility.Visible;
        await Until(() => Foreground(scene)?.IsVisible == true);
        AssertNativeMount(scene, native, scene.Window);
        front = Foreground(scene)!;
        scene.Window.Hide();
        await Until(() => !VisibleWindow(front));
        scene.Window.Show();
        await Until(() => Foreground(scene)?.IsVisible == true);
        AssertNativeMount(scene, native, scene.Window);

        front = Foreground(scene)!;
        var replacement = new FilmPlaybackPage(scene.Rig.Page.Item, scene.Rig.Page.Request, null, null);
        scene.View.DataContext = replacement;
        await Until(() => !VisibleWindow(front) && overlay.IsDescendantOf(picture));
        Check(!Visuals(front).Contains(overlay) && scene.Element<ContentControl>("VideoHost").Content is null &&
            ReferenceEquals(overlay.DataContext, replacement),
            "a replacement page detaches the actual foreground child and cannot inherit the former media surface");
        scene.View.DataContext = scene.Rig.Page;
        await Until(() => Foreground(scene)?.IsVisible == true && !overlay.IsDescendantOf(picture));
        AssertNativeMount(scene, native, scene.Window);
        Check(scene.Rig.Native.Commits == 1 && scene.Rig.Native.Stops == 0 &&
            scene.Rig.Native.SettledIdentity == "overlay" && !native.HasMedia && native.NativeVersion is null,
            "remounting the retained page reuses its exact probe occurrence and real HWND without decoding");
    }

    private static void AssertNativeMount(Scene scene, NativePlayback native, Window owner)
    {
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var front = Foreground(scene);
        var part = native.VideoView.Template?.FindName("PART_PlayerHost", native.VideoView);
        var host = part as HwndHost;
        bool sameHostContent = ReferenceEquals(scene.Element<ContentControl>("VideoHost").Content, native.VideoView);
        bool samePage = ReferenceEquals(overlay.DataContext, scene.Rig.Page);
        bool insideForeground = front is not null && overlay.IsDescendantOf(front);
        bool insidePicture = overlay.IsDescendantOf(scene.Element<Grid>("VideoPicture"));
        nint foregroundHandle = front is null ? 0 : new WindowInteropHelper(front).Handle;
        nint actualOwnerHandle = front?.Owner is { } actualOwner ? new WindowInteropHelper(actualOwner).Handle : 0;
        nint expectedOwnerHandle = new WindowInteropHelper(owner).Handle;
        Console.WriteLine($"OVERLAY native sessionId={Process.GetCurrentProcess().SessionId} userInteractive={Environment.UserInteractive} " +
            $"content={Describe(native.VideoView.Content)} surface={Describe(native.VideoView)} loaded={native.VideoView.IsLoaded} " +
            $"foreground={Describe(front)} wpfVisible={front?.IsVisible} hwnd=0x{foregroundHandle:X} win32Visible={front is not null && VisibleWindow(front)} " +
            $"sameAsOwner={ReferenceEquals(front, owner)} sameOwner={ReferenceEquals(front?.Owner, owner)} " +
            $"actualOwner=0x{actualOwnerHandle:X} expectedOwner=0x{expectedOwnerHandle:X} " +
            $"insideForeground={insideForeground} insidePicture={insidePicture} overlayParent={Describe(VisualTreeHelper.GetParent(overlay))} " +
            $"hostContentMatches={sameHostContent} template={Describe(native.VideoView.Template)} part={Describe(part)} hostHwnd=0x{(host?.Handle ?? 0):X} " +
            $"pageMatches={samePage} dataContext={Describe(overlay.DataContext)}");
        Check(native.VideoView.Content is null, $"real VideoView.Content must read null; actual={Describe(native.VideoView.Content)}");
        Check(front is { IsVisible: true }, $"the real foreground must be WPF-visible; actual={Describe(front)} visible={front?.IsVisible}");
        Check(front is not null && VisibleWindow(front), $"the real foreground HWND must be visible; hwnd=0x{foregroundHandle:X}");
        Check(!ReferenceEquals(front, owner), "the foreground must be a separate window from the playback owner");
        Check(ReferenceEquals(front?.Owner, owner),
            $"the foreground must belong to the current playback owner; actual=0x{actualOwnerHandle:X} expected=0x{expectedOwnerHandle:X}");
        Check(insideForeground, $"the foreground visual tree must own the overlay; parent={Describe(VisualTreeHelper.GetParent(overlay))}");
        Check(!insidePicture, "the native overlay must not remain a WPF sibling in VideoPicture");
        Check(sameHostContent, "VideoHost.Content must retain the exact real VideoView instance");
        Check(host is not null, $"PART_PlayerHost must be an HwndHost; actual={Describe(part)}");
        Check(host is not null && host.Handle != 0, "the real video host must have a nonzero HWND");
        Check(samePage, $"the mounted overlay must retain the exact playback page; actual={Describe(overlay.DataContext)}");
        Check(ReferenceEquals(scene.Element<Slider>("SeekSlider").DataContext, scene.Rig.Page) &&
            scene.Element<Slider>("SeekSlider").Maximum == scene.Rig.Page.Duration &&
            Equals(scene.Element<Button>("PanButton").Content, scene.Rig.Page.PlayerPanLabel),
            "the native foreground carries the page bindings into its controls after every mount");
    }

    private static void AssertViewportClip(Scene scene)
    {
        var overlay = scene.Element<Grid>("PlayerOverlay");
        var scroll = scene.Element<ScrollViewer>("PageScroll");
        var clip = overlay.Clip!.Bounds;
        Point topLeft = overlay.PointToScreen(clip.TopLeft), bottomRight = overlay.PointToScreen(clip.BottomRight);
        Point viewportStart = scroll.PointToScreen(new Point()), viewportEnd = scroll.PointToScreen(new Point(scroll.ActualWidth, scroll.ActualHeight));
        Check(topLeft.X >= viewportStart.X - 2 && topLeft.Y >= viewportStart.Y - 2 &&
            bottomRight.X <= viewportEnd.X + 2 && bottomRight.Y <= viewportEnd.Y + 2,
            "the painted native overlay clip stays inside the actual page viewport in screen coordinates");
    }

    private static Window? Foreground(Scene scene)
    {
        var window = Window.GetWindow(scene.Element<Grid>("PlayerOverlay"));
        return window is not null && !ReferenceEquals(window, scene.Window) && !ReferenceEquals(window, scene.PlayerWindow)
            ? window : null;
    }

    private static bool VisibleWindow(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        return handle != 0 && IsWindowVisible(handle);
    }
    private static void Shot(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        Check(element.IsLoaded && element.ActualWidth > 0 && element.ActualHeight > 0,
            "screenshots use the existing loaded control and its actual arranged size");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = TestWorkspace.DataPath("player-shots");
        Directory.CreateDirectory(directory);
        string path = TestWorkspace.DataPath("player-shots", name + ".png");
        using (var output = File.Create(path)) encoder.Save(output);
        Console.WriteLine("SHOT " + path);
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmPlaybackRig Rig;
        internal readonly FilmPlaybackView View = new();
        internal readonly Window Window;
        private Scene(ContentControl? surface)
        {
            Rig = new(surface);
            Window = new()
            {
                Content = View, Width = 1900, Height = 950, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
            };
        }
        internal static async Task<Scene> Create(ContentControl? surface = null)
        {
            var scene = new Scene(surface);
            try
            {
                await scene.Rig.OpenAsync();
                scene.View.DataContext = scene.Rig.Page;
                scene.Window.Show();
                await Until(() => scene.View.IsLoaded && scene.View.ActualWidth > 0);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal async Task Play()
        {
            Rig.Runtime.Attempt("overlay", Candidate("overlay"));
            await Until(() => Rig.Native.SettledIdentity == "overlay" && Rig.Page.CurrentAttemptId == "overlay" &&
                Rig.Session.HasOutput && !Rig.Page.PlayerBusy);
            View.UpdateLayout();
        }
        internal async Task Pause()
        {
            Click(Element<Button>("PlayButton"));
            await Until(() => Rig.Native.Paused && !Rig.Page.IsPlaying);
        }
        internal int NativeCount(string command) => Rig.Native.Controls.Count(call => call.Command == command);
        internal int ActionCount(string action) => Rig.Runtime.Calls.Count(call =>
            call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == action);
        internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
        internal Button Button(string name) => Visuals(Element<Grid>("PlayerOverlay")).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == name);
        internal Window? PlayerWindow
        {
            get
            {
                var owner = Window.GetWindow(Element<Grid>("PlayerFrame"));
                return ReferenceEquals(owner, Window) ? null : owner;
            }
        }
        internal async Task ClosePlayerWindow()
        {
            var window = PlayerWindow ?? throw new InvalidOperationException("Expected a player window");
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            await Until(() => closed && !window.IsVisible && !Window.OwnedWindows.Cast<Window>().Contains(window) &&
                Element<Grid>("PlayerFrame").IsDescendantOf(View));
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (PlayerWindow is not null) await ClosePlayerWindow();
                View.DataContext = null;
                await Pump();
            }
            finally
            {
                try { Window.Content = null; Window.Close(); }
                finally { await Rig.DisposeAsync(); }
            }
        }
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Move(UIElement target, RoutedEvent routed) =>
        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routed });
    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        FrameworkElement element => $"{element.GetType().FullName}#{element.Name}",
        _ => $"{value}<{value.GetType().FullName}>"
    };
    private static void RouteMouse(UIElement target, RoutedEvent preview, int clicks = 1)
    {
        var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = preview };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(input, clicks);
        // Let WPF promote the generic mouse route to its direct left-button events,
        // preserving the real range/button OriginalSource and normal Handled semantics.
        target.RaiseEvent(input);
        if (input.Handled) return;
        input.RoutedEvent = preview == Mouse.PreviewMouseDownEvent ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;
        target.RaiseEvent(input);
    }
    private static KeyEventArgs RouteKey(UIElement target, Key key, bool release = false, bool repeat = false)
    {
        var input = new KeyEventArgs(new KeyboardProbe(), PresentationSource.FromVisual(target)!, Environment.TickCount, key)
        { RoutedEvent = release ? Keyboard.PreviewKeyUpEvent : Keyboard.PreviewKeyDownEvent };
        if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, [true]);
        target.RaiseEvent(input);
        if (!input.Handled)
        {
            input.RoutedEvent = release ? Keyboard.KeyUpEvent : Keyboard.KeyDownEvent;
            target.RaiseEvent(input);
        }
        return input;
    }
    private sealed class KeyboardProbe() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }
    private static async Task HoldVisible(UIElement chrome, string message)
    {
        await Task.Delay(2300);
        Check(chrome.IsVisible && chrome.IsHitTestVisible, message);
    }
    private static async Task Until(Func<bool> condition)
    {
        await FilmPlaybackFixture.Until(condition);
        await Pump();
    }
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static bool Within(DependencyObject child, DependencyObject parent) =>
        ReferenceEquals(child, parent) || child is Visual visual && parent is Visual ancestor && visual.IsDescendantOf(ancestor);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
