using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackControlsTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-playback-controls: " + name, () => MusicQualityConsumerTests.Sta(run));
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("source menu continuation keeps exact browsing selection and admits one request while enabled", SourceSearchMore);
        yield return Test("source menu continuation reports failure permits retry and ignores a retired page error", SourceSearchMoreFailure);
        yield return Test("source pan layout and rail controls share one row while seasons stay separate", BrowseToolbar);
        yield return Test("play pause seek rate aspect and volume drive the actual accepted occurrence", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            Click(scene.Element<Button>("PlayButton")); await Until(() => scene.Rig.Native.Paused);
            var seek = scene.Element<Slider>("SeekSlider");
            seek.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(seek)!, 0, Key.Right)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            seek.Value = 89;
            seek.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(seek)!, 0, Key.Right)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            await Until(() => scene.Rig.Native.Position == 89);
            scene.Element<ListBox>("RateBox").SelectedIndex = 4;
            scene.Element<ListBox>("AspectBox").SelectedIndex = 2;
            var volume = scene.Element<Slider>("VolumeSlider");
            volume.Value = 37;
            await Until(() => scene.Rig.Native.Rate == 1.5 && scene.Rig.Native.Aspect == "4:3" && scene.Rig.Native.Volume == 37);
            Check(scene.Rig.Session.Rate == 1.5 && scene.Rig.Session.Aspect == "4:3" &&
                scene.Rig.Native.Commits == 1 && scene.Rig.Page.ProgressLabel.StartsWith("1:29", StringComparison.Ordinal),
                "controls alter real owner state without opening another input or changing an episode selection");
            Click(scene.Element<Button>("PlayButton")); await Until(() => !scene.Rig.Native.Paused);
        });
        yield return Test("full screen and picture in picture reparent the same host and only an explicit live owner retains it", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            var frame = scene.Element<Grid>("PlayerFrame"); var host = scene.Element<ContentControl>("VideoHost");
            var view = scene.Rig.Native.View;
            Click(scene.Element<Button>("FullscreenButton"));
            var full = scene.Window.OwnedWindows.Cast<Window>().Single();
            Check(ReferenceEquals(host.Content, view) && frame.IsDescendantOf(full) && scene.Rig.Native.Commits == 1,
                "full screen moves the existing native surface rather than opening a new player");
            full.Close();
            await Until(() => frame.IsDescendantOf(scene.View) && !full.IsVisible &&
                !scene.Window.OwnedWindows.Cast<Window>().Contains(full));
            Click(scene.Element<Button>("PipButton"));
            var pip = scene.Window.OwnedWindows.Cast<Window>().Single();
            Check(ReferenceEquals(host.Content, view) && frame.IsDescendantOf(pip) && scene.Rig.Session.CanRetainMedia,
                "the visible PiP window owns the same accepted output");
            await scene.Rig.Session.LeaveAsync(retainedOwner: true);
            scene.View.Visibility = Visibility.Collapsed;
            Check(pip.IsVisible && scene.Rig.Session.HasOutput && scene.Rig.Session.HasSession,
                "leaving the ordinary page does not kill an explicitly retained PiP output");
            pip.Close();
            await Until(() => !scene.Rig.Session.HasSession);
            Check(scene.Rig.Native.Stops == 1 && ReferenceEquals(frame.Parent, scene.Element<Grid>("HeroGrid")),
                "closing the last explicit owner returns the same layout and waits for actual stop");
        });
        yield return Test("four actions use exact business commands and menu opening performs no request", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            scene.Rig.Runtime.Change(root => root["current"]!["candidate"]!["provider"] = "uc");
            await Until(() => scene.Rig.Page.PlayerPanLabel == "UC");
            int before = scene.Rig.Runtime.Calls.Count;
            var pan = scene.Element<Button>("PanButton"); Click(pan);
            Check(scene.Rig.Runtime.Calls.Count == before, "opening the pan menu does not request sources or start matching");
            ((MenuItem)pan.ContextMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            pan.ContextMenu.IsOpen = false;
            await Until(() => scene.Called("pan"));
            var selected = scene.Rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "pan");
            Check(FilmJson.Text(selected.Parameters, "provider") == "quark", "pan selection is a standard provider, never a label guess");
            var quality = scene.Element<Button>("QualityButton"); before = scene.Rig.Runtime.Calls.Count; Click(quality);
            Check(scene.Rig.Runtime.Calls.Count == before, "quality menu opening is local presentation");
            ((MenuItem)quality.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            quality.ContextMenu.IsOpen = false;
            await Until(() => scene.Called("quality"));
            selected = scene.Rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "quality");
            Check(FilmJson.Text(selected.Parameters, "quality") == "4k_hdr", "quality has an exact hard-constraint action");
            Click(scene.Element<Button>("SwitchButton")); await Until(() => scene.Called("switch"));
            Check(scene.Element<Button>("BlockButton").IsEnabled && scene.Rig.Native.Identity == "controls",
                "permanent block is available for a real current target while browse/switch acknowledgement does not destroy its donor");
            Click(scene.Element<Button>("BlockButton"));
            await Until(() => scene.Called("block"));
            selected = scene.Rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "block");
            Check(FilmJson.Text(selected.Parameters, "attemptId") == "controls",
                "the direct wrong-source action sends the exact current occurrence without a second confirmation");
        });
        yield return Test("same pending source actions follow snapshot constraints without blocking a different selection or a later retry", async () =>
        {
            foreach (var selection in new[]
            {
                (Action: "switch", Field: "", Value: "", Index: -1),
                (Action: "pan", Field: "provider", Value: "uc", Index: 3),
                (Action: "quality", Field: "quality", Value: "4k_hdr", Index: 0)
            })
            {
                await using var scene = await Scene.Create();
                await scene.Play();
                scene.Window.Width = 1900;
                await Until(() => scene.Element<Button>("PanButton").IsVisible &&
                    scene.Element<Button>("QualityButton").IsVisible && scene.Element<Button>("SwitchButton").IsVisible);
                var scope = scene.Rig.Native.Scope;
                JsonElement[] SourceCommands() => scene.Rig.Runtime.Calls
                    .Where(call => call.Method == "film.session.command" &&
                        FilmJson.Text(call.Parameters, "action") is "switch" or "pan" or "quality")
                    .Select(call => call.Parameters).ToArray();
                void Choose(string action, int index)
                {
                    var button = scene.Element<Button>(action switch
                    {
                        "pan" => "PanButton", "quality" => "QualityButton", _ => "SwitchButton"
                    });
                    Click(button);
                    if (index < 0) return;
                    ((MenuItem)button.ContextMenu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    button.ContextMenu.IsOpen = false;
                }
                scene.Rig.Runtime.Intercept = (method, input, _) =>
                {
                    string action = FilmJson.Text(input, "action");
                    if (method == "film.session.command" && action is "switch" or "pan" or "quality")
                        scene.Rig.Runtime.Change(root =>
                        {
                            root["stage"] = "resolving";
                            var constraint = new JsonObject { ["action"] = action };
                            if (action != "switch")
                            {
                                string field = action == "pan" ? "provider" : "quality";
                                constraint[field] = FilmJson.Text(input, field);
                            }
                            root["constraint"] = constraint;
                        });
                    // The RPC has completed; the server snapshot, not a client in-flight latch, remains pending.
                    return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
                };
                Choose(selection.Action, selection.Index);
                await Until(() => SourceCommands().Length == 1 && scene.Rig.Page.PlayerBusy);
                var first = SourceCommands().Single();
                Check(FilmJson.Text(first, "action") == selection.Action &&
                    (selection.Field.Length == 0 || FilmJson.Text(first, selection.Field) == selection.Value),
                    "the initial UI source action sends its exact business action and selected constraint");
                Choose(selection.Action, selection.Index);
                Choose(selection.Action, selection.Index);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(SourceCommands().Length == 1,
                    "repeating the same server-pending action and value sends no additional business command");

                scene.Rig.Runtime.Change(root => root["stage"] = "playing");
                await Until(() => !scene.Rig.Page.PlayerBusy);
                Choose(selection.Action, selection.Index);
                await Until(() => SourceCommands().Length == 2 && scene.Rig.Page.PlayerBusy);
                Check(FilmJson.Text(SourceCommands()[1], "action") == selection.Action &&
                    (selection.Field.Length == 0 || FilmJson.Text(SourceCommands()[1], selection.Field) == selection.Value),
                    "once busy ends the same retained constraint can be requested again");

                string differentAction = selection.Action == "switch" ? "pan" : selection.Action;
                string differentField = differentAction == "pan" ? "provider" : "quality";
                string differentValue = selection.Action switch { "pan" => "baidu", "quality" => "4k_fps", _ => "uc" };
                int differentIndex = selection.Action switch { "pan" => 4, "quality" => 1, _ => 3 };
                Choose(differentAction, differentIndex);
                await Until(() => SourceCommands().Length == 3);
                Check(FilmJson.Text(SourceCommands()[2], "action") == differentAction &&
                    FilmJson.Text(SourceCommands()[2], differentField) == differentValue &&
                    scene.Rig.Native.Scope == scope && scene.Rig.Native.Commits == 1 &&
                    scene.Rig.Native.Stops == 0 && scene.Rig.Native.SettledIdentity == "controls" && scene.Notices.Count == 0,
                    "busy allows a different action or constraint value without reopening or stopping its usable output");
            }
        });
        yield return Test("busy wrong-source clicks do nothing while the settled action sends its exact current occurrence", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            scene.Rig.Runtime.Change(root => root["stage"] = "resolving");
            await Until(() => scene.Rig.Page.PlayerBusy);
            int before = scene.Rig.Runtime.Calls.Count;
            Click(scene.Element<Button>("BlockButton"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(scene.Rig.Runtime.Calls.Count == before && scene.Rig.Native.SettledIdentity == "controls" &&
                scene.Rig.Native.Stops == 0, "loading ignores wrong-source input without writing, matching or stopping its donor");
            scene.Rig.Runtime.Change(root => root["stage"] = "playing");
            await Until(() => !scene.Rig.Page.PlayerBusy);
            Click(scene.Element<Button>("BlockButton"));
            await Until(() => scene.Called("block"));
            var command = scene.Rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "block");
            Check(FilmJson.Text(command.Parameters, "attemptId") == "controls" && scene.Rig.Runtime.Calls.Count == before + 1 &&
                scene.Rig.Native.Stops == 0 && scene.Rig.Native.SettledIdentity == "controls",
                "once loading settles the real action directly sends only the accepted occurrence");
        });
        yield return Test("failed wrong-source persistence retains the exact requested occurrence and its usable output", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            scene.Rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
                method == "film.session.command" && FilmJson.Text(input, "action") == "block"
                    ? FilmRuntimeProbe.Failure("STORAGE_ERROR") : scene.Rig.Runtime.Handle(method, input));
            Click(scene.Element<Button>("BlockButton"));
            await Until(() => scene.Notices.Count == 1);
            var command = scene.Rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "block");
            Check(FilmJson.Text(command.Parameters, "attemptId") == "controls" && scene.Notices.Single() == "本地存储失败" &&
                scene.Rig.Native.SettledIdentity == "controls" && scene.Rig.Native.Stops == 0 &&
                !scene.Rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") == "switch"),
                "a failed durable receipt is observable and never authorizes replacement of the still-usable output");
        });
        yield return Test("an in-flight wrong-source failure keeps its captured attempt and preserves the pending replacement", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            bool entered = false;
            var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            scene.Rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "block")
                {
                    Check(FilmJson.Text(input, "attemptId") == "controls",
                        "the view must send the original captured occurrence, never the later current");
                    entered = true;
                    return pending.Task;
                }
                return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            };
            try
            {
                Click(scene.Element<Button>("BlockButton"));
                await Until(() => entered);
                scene.Rig.Runtime.Attempt("replacement", Candidate("replacement", episode: 2));
                await Until(() => scene.Rig.Page.PlayerBusy);
                Check(scene.Rig.Native.SettledIdentity == "controls" && scene.Rig.Session.HasOutput,
                    "the pending business receipt keeps its usable donor while the replacement waits for admission");
                pending.SetResult(FilmRuntimeProbe.Failure("STALE_SESSION"));
                await Until(() => scene.Notices.Count == 1 && scene.Rig.Native.SettledIdentity == "replacement" &&
                    scene.Rig.Page.CurrentAttemptId == "replacement");
                Check(scene.Rig.Runtime.Calls.Count(call => FilmJson.Text(call.Parameters, "action") == "block") == 1 &&
                    FilmJson.Text(scene.Rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "block").Parameters, "attemptId") == "controls" &&
                    scene.Notices.Single() == "会话已失效，请重连" &&
                    scene.Rig.Native.SettledIdentity == "replacement" && scene.Rig.Session.HasOutput,
                    "the old attempt's classified rejection remains observable and leaves the newer occurrence playing");
            }
            finally { pending.TrySetResult(FilmRuntimeProbe.Failure("CANCELLED")); }
        });
        foreach (var rejected in new[]
        {
            (Action: "preview", Field: "previewEpisodes", Label: "点映", Code: "STORAGE_ERROR", Message: "本地存储失败"),
            (Action: "shortvideo", Field: "shortVideo", Label: "短视频", Code: "CLOSED", Message: "服务已关闭，请重连"),
            (Action: "shortvideo", Field: "shortVideo", Label: "短视频", Code: "STALE_SESSION", Message: "会话已失效，请重连")
        })
        {
            yield return Test($"rejected {rejected.Action} {rejected.Code} restores the actual checkbox without another snapshot and retries true", async () =>
            {
                foreach (bool accepted in new[] { false, true })
                {
                    await using var scene = await Scene.Create();
                    if (accepted) scene.Rig.Runtime.Change(root => root[rejected.Field] = true);
                    bool PageValue() => rejected.Action == "preview" ? scene.Rig.Page.PreviewEpisodes : scene.Rig.Page.ShortVideo;
                    var box = Visuals(scene.View).OfType<CheckBox>().Single(value => Equals(value.Content, rejected.Label));
                    await Until(() => PageValue() == accepted && box.IsChecked == accepted,
                        () => $"Initial {rejected.Action} state did not settle: accepted={accepted}, page={PageValue()}, checkbox={box.IsChecked}");
                    long sequence = scene.Rig.Runtime.Value.GetProperty("sequence").GetInt64();
                    int before = scene.Rig.Runtime.Calls.Count;
                    var toggledValues = new List<bool?>();
                    scene.Rig.Runtime.Intercept = (method, input, _) =>
                    {
                        if (method != "film.session.command" || FilmJson.Text(input, "action") != rejected.Action)
                            return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
                        toggledValues.Add(box.IsChecked);
                        return Task.FromResult(FilmRuntimeProbe.Failure(rejected.Code));
                    };
                    for (int click = 1; click <= 2; click++)
                    {
                        await ToggleByKeyboardAsync(box);
                        await Until(() => scene.Rig.Runtime.Calls.Count == before + click && scene.Notices.Count == 1,
                            () => $"{rejected.Action} rejection did not settle: accepted={accepted}, click={click}, " +
                                $"calls={scene.Rig.Runtime.Calls.Count - before}, notices={scene.Notices.Count}, page={PageValue()}, checkbox={box.IsChecked}");
                        var commands = scene.Rig.Runtime.Calls.Where(call =>
                            call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == rejected.Action).ToArray();
                        Console.WriteLine($"WFR01 action={rejected.Action} error={rejected.Code} accepted={accepted} click={click} " +
                            $"enabled={FilmJson.Bool(commands[^1].Parameters, "enabled")} page={PageValue()} checkbox={box.IsChecked} " +
                            $"sequence={sequence}->{scene.Rig.Runtime.Value.GetProperty("sequence").GetInt64()}");
                        Check(commands.Length == click && commands.All(call => FilmJson.Bool(call.Parameters, "enabled") == !accepted) &&
                            toggledValues.Count == click && toggledValues.All(value => value == !accepted),
                            "real WPF Space input toggles before Click and the next click retries the same rejected intent");
                        Check(PageValue() == accepted && box.IsChecked == accepted && scene.Notices[^1] == rejected.Message &&
                            scene.Rig.Runtime.Value.GetProperty("sequence").GetInt64() == sequence &&
                            scene.Rig.Runtime.Calls.Count == before + click && scene.Rig.Native.Commits == 0,
                            "without a new snapshot or a refresh RPC the actual checkbox returns to the accepted value and preserves its error");
                    }
                }
            });
        }
        yield return Test("open raw directory follows exact current A pending B started B while keeping its path and selection without browse requests", async () =>
        {
            await using var scene = await Scene.Create();
            JsonElement File(string id, string path)
            {
                var row = JsonNode.Parse(Candidate(id).GetRawText())!;
                row["name"] = "同名文件"; row["path"] = path;
                return JsonSerializer.SerializeToElement(row);
            }
            var a = File("directory-a", "/库/第二季/a.mp4");
            var b = File("directory-b", "/库/第二季/b.mp4");
            scene.Rig.Runtime.Change(root =>
            {
                root["sources"] = JsonNode.Parse(FilmJson.Encode(new[] { new { id = "directory-source", title = "目录来源" } }).GetRawText());
                root["browsing"] = JsonNode.Parse(FilmJson.Encode(new
                {
                    sourceId = "directory-source", lineId = 0, loading = false, error = (string?)null,
                    lines = new[] { new { id = 0, label = "原线路" } },
                    episodes = new[]
                    {
                        new { candidateId = "directory-a", name = "同名文件", path = "/库/第二季/a.mp4" },
                        new { candidateId = "directory-b", name = "同名文件", path = "/库/第二季/b.mp4" }
                    },
                    originalEpisodes = new[]
                    {
                        new { candidateId = "directory-a", name = "同名文件", path = "/库/第二季/a.mp4" },
                        new { candidateId = "directory-b", name = "同名文件", path = "/库/第二季/b.mp4" },
                        new { candidateId = "directory-other", name = "同名文件", path = "/库/其他季/a.mp4" }
                    }
                }).GetRawText());
            });
            scene.Rig.Runtime.Attempt("directory-output-a", a);
            await Until(() => scene.Rig.Page.CurrentCandidateId == "directory-a" && scene.Rig.Native.SettledIdentity == "directory-output-a");
            int before = scene.Rig.Runtime.Calls.Count;
            Click(scene.Element<Button>("RawDirectory"));
            var dialog = scene.Window.OwnedWindows.OfType<FilmDirectoryDialog>().Single();
            await Until(() => dialog.IsLoaded);
            var list = Visuals(dialog).OfType<ListBox>().Single();
            var play = Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "播放所选文件"));
            var back = Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "返回上级"));
            void Enter(string name)
            {
                list.SelectedItem = list.Items.Cast<FilmDirectoryRow>().Single(row => row.IsDirectory && row.Name == name);
                list.RaiseEvent(new KeyEventArgs(new KeyboardProbe(), PresentationSource.FromVisual(list)!, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            void Current(string expected, string phase)
            {
                list.UpdateLayout();
                var rows = list.Items.Cast<FilmDirectoryRow>().ToArray();
                var current = rows.Single(row => row.IsCurrent);
                Check(dialog.IsVisible && current.CandidateId == expected && current.Label == "当前 · 同名文件" &&
                    rows.Select(row => row.Path).SequenceEqual(new[] { "/库/第二季/a.mp4", "/库/第二季/b.mp4" }) &&
                    Visuals(dialog).OfType<TextBlock>().Any(text => text.Text == "/库/第二季") &&
                    Visuals(list).OfType<TextBlock>().Count(text => text.Text == "当前 · 同名文件") == 1,
                    "only the exact acknowledged file is marked in the live directory and neither path nor same-name identity changes");
                Console.WriteLine($"WFR02 phase={phase} page={scene.Rig.Page.CurrentCandidateId} current={current.CandidateId} " +
                    $"path=/库/第二季 selected={(list.SelectedItem as FilmDirectoryRow)?.CandidateId}");
            }
            Enter("库"); Enter("第二季");
            Current("directory-a", "current-A");
            Check(scene.Rig.Runtime.Calls.Count == before, "opening and traversing the original directory performs no request");
            var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scene.Rig.Native.Preparing = token => prepare.Task.WaitAsync(token);
            scene.Rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "select.candidate")
                {
                    Check(FilmJson.Text(input, "candidateId") == "directory-b", "the directory plays the exact selected file");
                    scene.Rig.Runtime.Attempt("directory-output-b", b);
                }
                return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            };
            try
            {
                list.SelectedItem = list.Items.Cast<FilmDirectoryRow>().Single(row => row.CandidateId == "directory-b");
                Click(play);
                await Until(() => scene.Rig.Native.Intents.Any(intent => intent.Identity == "directory-output-b"));
                Current("directory-a", "pending-B");
                Check((list.SelectedItem as FilmDirectoryRow)?.CandidateId == "directory-b" && play.IsEnabled,
                    "a pending replacement keeps the user's selected exact file");
                prepare.SetResult();
                await Until(() => scene.Rig.Page.CurrentCandidateId == "directory-b" && scene.Rig.Native.SettledIdentity == "directory-output-b");
                Current("directory-b", "started-B");
                Check((list.SelectedItem as FilmDirectoryRow)?.CandidateId == "directory-b" && play.IsEnabled,
                    "current-marker refresh preserves selection instead of returning to the directory root");
                before = scene.Rig.Runtime.Calls.Count;
                scene.Rig.Page.NotifyMedia(); scene.Rig.Page.NotifyMedia();
                Click(back); Enter("其他季");
                Check(list.Items.Cast<FilmDirectoryRow>().All(row => !row.IsCurrent),
                    "a same-name file in another exact path is never mistaken for current");
                Click(back); Enter("第二季");
                Current("directory-b", "return-to-path");
                Check(scene.Rig.Runtime.Calls.Count == before &&
                    scene.Rig.Runtime.Calls.Count(call => FilmJson.Text(call.Parameters, "action") == "select.candidate") == 1 &&
                    !scene.Rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") is "browse" or "search.more" ||
                        call.Method is "film.session.snapshot" || call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
                    "marker refresh and later traversal add no snapshot content or browse requests; only one explicit file selection was sent");
                scene.Rig.Runtime.Change(root => root["browsing"]!["lineId"] = "0");
                await Until(() => !dialog.IsVisible);
            }
            finally { prepare.TrySetResult(); }
        });
        yield return Test("movie buttons mark exact same-label current through pending failed replacement and started while retaining width and quality order", async () =>
        {
            await using var scene = await Scene.Create();
            JsonElement Movie(string id, string quality = "1080p")
            {
                var row = JsonNode.Parse(Candidate(id, episode: null, quality: quality).GetRawText())!;
                row["name"] = "同名电影文件"; row["path"] = "/电影/" + id + ".mp4";
                return JsonSerializer.SerializeToElement(row);
            }
            var a = Movie("movie-a"); var b = Movie("movie-b");
            // Product Node snapshots supply the already ordered movie candidates.
            var rows = FilmJson.Encode(new[] { Movie("movie-hdr", "4k_hdr"), a, b, Movie("movie-unknown", "") });
            void Choices() => scene.Rig.Runtime.Change(root => root["candidates"] = JsonNode.Parse(rows.GetRawText()));
            scene.Rig.Runtime.Target = scene.Rig.Runtime.Target with
            { Id = "tmdb:movie:100", Kind = "movie", Season = null, Episode = null, GlobalEpisode = null };
            scene.Rig.Runtime.Attempt("movie-output-a", a);
            Choices();
            await Until(() => scene.Rig.Page.CurrentCandidateId == "movie-a" && scene.Rig.Page.Candidates.Count == 4 &&
                scene.Rig.Native.SettledIdentity == "movie-output-a");
            var choices = scene.Element<ItemsControl>("MovieCandidates");
            Button ButtonFor(string id) => Visuals(choices).OfType<Button>().Single(button => button.Tag is FilmCandidateItem value && value.Id == id);
            void Current(string expected, string phase)
            {
                scene.View.UpdateLayout();
                var buttons = Visuals(choices).OfType<Button>().Where(button => button.Tag is FilmCandidateItem).ToArray();
                Check(buttons.Select(button => ((FilmCandidateItem)button.Tag).Id)
                    .SequenceEqual(new[] { "movie-hdr", "movie-a", "movie-b", "movie-unknown" }) &&
                    buttons.All(button => Math.Abs(button.ActualWidth - 190) < .5),
                    "all accepted-state updates retain Node quality order and the poster candidate width");
                Check(scene.Rig.Page.Candidates.Single(value => value.Id == "movie-a").DisplayLabel == "1080P · 契约来源" &&
                    scene.Rig.Page.Candidates.Single(value => value.Id == "movie-b").DisplayLabel == "1080P · 契约来源 (2)" &&
                    scene.Rig.Page.Candidates.Single(value => value.IsSelected).Id == expected,
                    "same-label numbering stays stable while only the exact current candidate gets the selected projection");
                foreach (var button in buttons)
                {
                    var item = (FilmCandidateItem)button.Tag;
                    Check((AutomationProperties.GetItemStatus(button) == "当前") == (item.Id == expected) &&
                        Visuals(button).OfType<TextBlock>().Any(text => text.Text == "当前" && text.IsVisible) == (item.Id == expected) &&
                        Visuals(button).OfType<TextBlock>().Single(text => text.Text == item.DisplayLabel).TextTrimming == TextTrimming.CharacterEllipsis,
                        "the live button's visible current badge and accessible status agree on exact identity without expanding its label");
                }
                Console.WriteLine($"WFR03 phase={phase} page={scene.Rig.Page.CurrentCandidateId} " +
                    $"visual={((FilmCandidateItem)buttons.Single(button => Visuals(button).OfType<TextBlock>().Any(text => text.Text == "当前" && text.IsVisible)).Tag).Id} " +
                    $"accessible={((FilmCandidateItem)buttons.Single(button => AutomationProperties.GetItemStatus(button) == "当前").Tag).Id}");
            }
            Current("movie-a", "current-A");
            var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool fail = true; int attempt = 0;
            scene.Rig.Native.Preparing = async token =>
            {
                await prepare.Task.WaitAsync(token);
                if (fail) throw new FilmPlaybackException("MEDIA_UNAVAILABLE");
            };
            scene.Rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "select.candidate")
                {
                    Check(FilmJson.Text(input, "candidateId") == "movie-b", "same display names never retarget the selected candidate");
                    scene.Rig.Runtime.Attempt("movie-output-b-" + ++attempt, b);
                    Choices();
                }
                return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            };
            try
            {
                Click(ButtonFor("movie-b"));
                await Until(() => scene.Rig.Native.Intents.Any(intent => intent.Identity == "movie-output-b-1"));
                Current("movie-a", "pending-B");
                prepare.SetResult();
                await Until(() => scene.Rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") == "media.failed" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "movie-output-b-1") &&
                    FilmJson.Member(scene.Rig.Runtime.Value, "attempt").ValueKind == JsonValueKind.Null);
                Current("movie-a", "failed-B");
                fail = false; prepare = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(ButtonFor("movie-b"));
                await Until(() => scene.Rig.Native.Intents.Any(intent => intent.Identity == "movie-output-b-2"));
                Current("movie-a", "retry-pending-B");
                prepare.SetResult();
                await Until(() => scene.Rig.Page.CurrentCandidateId == "movie-b" && scene.Rig.Native.SettledIdentity == "movie-output-b-2");
                foreach (double width in new[] { 1150d, 780d })
                {
                    scene.Window.Width = width;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Current("movie-b", "started-B-" + width);
                }
                Check(scene.Rig.Runtime.Calls.Count(call => FilmJson.Text(call.Parameters, "action") == "select.candidate") == 2 &&
                    !scene.Rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") is "browse" or "search.more" ||
                        call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
                    "rendering and current-state changes do not request additional candidates or merge two same-name selections");
            }
            finally { prepare.TrySetResult(); }
        });
        yield return Test("long movie choices keep fixed widths and raw-directory visibility follows the selected real list", async () =>
        {
            await using var scene = await Scene.Create();
            string longName = string.Concat(Enumerable.Repeat("这是一个很长但不能撑开选集块的站点名称", 12));
            var candidate = JsonNode.Parse(Candidate("movie-long", episode: null).GetRawText())!.AsObject();
            candidate["source"]!["name"] = longName;
            scene.Rig.Runtime.Change(root =>
            {
                root["target"]!["kind"] = "movie";
                root["target"]!.AsObject().Remove("season");
                root["target"]!.AsObject().Remove("episode");
                root["target"]!.AsObject().Remove("globalEpisode");
                root["candidates"] = new JsonArray(candidate);
                root["episodes"] = new JsonArray();
            });
            await Until(() => scene.Rig.Page.ShowMovieCandidates && scene.Rig.Page.Candidates.Count == 1);
            foreach (double width in new[] { 1150d, 780d })
            {
                scene.Window.Width = width;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                scene.View.UpdateLayout();
                var choices = scene.Element<ItemsControl>("MovieCandidates");
                var button = Visuals(choices).OfType<Button>().Single(candidate => candidate.Tag is FilmCandidateItem);
                var label = Visuals(button).OfType<TextBlock>().Single(value => value.Text.Contains(longName, StringComparison.Ordinal));
                Check(Math.Abs(button.ActualWidth - 190) < .5 && choices.ActualWidth <= scene.View.ActualWidth &&
                    label.TextWrapping == TextWrapping.NoWrap && label.TextTrimming == TextTrimming.CharacterEllipsis,
                    "wide and narrow movie rows constrain the real long label instead of expanding the content region");
            }
            Check(!scene.Element<Button>("RawDirectory").IsVisible, "pure metadata has no original site directory");
            void Browse(bool content, string? error = null) => scene.Rig.Runtime.Change(root =>
            {
                root["sources"] = JsonNode.Parse(FilmJson.Encode(new[] { new { id = "site-result", title = "真实站点" } }).GetRawText());
                root["browsing"] = JsonNode.Parse(FilmJson.Encode(new
                {
                    sourceId = "site-result", lineId = 0, loading = false, error,
                    lines = new[] { new { id = 0, label = "未知原线路" } }, episodes = Array.Empty<object>(),
                    originalEpisodes = content ? new[] { new { id = 0, name = "准确原始文件", path = "/目录/文件.mp4" } } : []
                }).GetRawText());
            });
            Browse(content: false);
            await Until(() => scene.Rig.Page.SourceId == "site-result");
            Check(!scene.Element<Button>("RawDirectory").IsVisible, "a site with no original content does not get a fake directory entry");
            Browse(content: true);
            await Until(() => scene.Element<Button>("RawDirectory").IsVisible);
            Browse(content: true, error: "UPSTREAM_HTTP");
            await Until(() => scene.Rig.Page.ListError.Length > 0);
            Check(!scene.Element<Button>("RawDirectory").IsVisible && scene.Rig.Page.Candidates.Count == 0 &&
                scene.Element<ComboBox>("SourcePicker").IsEnabled,
                "a failed selected list hides stale content while source selection remains usable");
            Browse(content: true);
            await Until(() => scene.Element<Button>("RawDirectory").IsVisible && scene.Rig.Page.ListError.Length == 0);
        });
        yield return Test("short-video wheel gestures drive next and previous while ordinary mode remains inert", async () =>
        {
            await using var scene = await Scene.Create();
            var gesture = scene.Element<Border>("ShortVideoGesture");
            int before = scene.Rig.Runtime.Calls.Count;
            gesture.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(scene.Rig.Runtime.Calls.Count == before, "ordinary mode never changes the target from a short-video gesture");
            scene.Rig.Runtime.Change(root => root["shortVideo"] = true);
            await Until(() => scene.Rig.Page.ShortVideo && gesture.IsVisible);
            gesture.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 1, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Until(() => scene.Called("next"));
            await Task.Delay(260); // Above the UI's 250 ms wheel-gesture coalescing window, not completion proof.
            gesture.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 2, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Until(() => scene.Called("previous"));
            Check(scene.Rig.Native.Commits == 0, "target navigation alone does not claim actual native output or buffered media");
        });
        yield return Test("rate and aspect failures retain rollback while their notices use typed classification or safe action fallback", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Play();
            var rate = scene.Element<ListBox>("RateBox");
            var aspect = scene.Element<ListBox>("AspectBox");
            int originalRate = rate.SelectedIndex, originalAspect = aspect.SelectedIndex;
            var rateChoice = (ListBoxItem)rate.Items[4];
            var aspectChoice = (ListBoxItem)aspect.Items[2];
            object rateTag = rateChoice.Tag, aspectTag = aspectChoice.Tag;
            try
            {
                rateChoice.Tag = "fixture-invalid-rate";
                rate.SelectedIndex = 4;
                await Until(() => scene.Notices.Count == 1);
                Check(rate.SelectedIndex == originalRate && scene.Rig.Native.Rate == 1 &&
                    scene.Rig.Native.SettledIdentity == "controls" && scene.Notices.Single() == "倍速设置失败",
                    "an unknown parse failure rolls back the visible choice and uses only the rate-specific safe fallback");
                aspectChoice.Tag = "fixture-invalid-aspect";
                aspect.SelectedIndex = 2;
                await Until(() => scene.Notices.Count == 2);
                Check(aspect.SelectedIndex == originalAspect && scene.Rig.Native.Aspect is null &&
                    scene.Rig.Native.SettledIdentity == "controls" && scene.Notices[^1] == "请求无效",
                    "a rejected aspect command retains the actual owner and rollback while preserving its typed failure");
            }
            finally { rateChoice.Tag = rateTag; aspectChoice.Tag = aspectTag; }
        });
        yield return Test("failed controls publish classified safe notices instead of collapsing formal error codes", async () =>
        {
            await using var scene = await Scene.Create();
            (string Code, string Message)[] failures =
            [
                ("TIMEOUT", "请求超时，请重试"),
                ("CLOSED", "服务已关闭，请重连"),
                ("DISCONNECTED", "连接已断开，请重连"),
                ("INVALID_RESPONSE", "响应无效，请重试"),
                ("UPSTREAM_FORMAT", "上游数据格式错误"),
                ("NO_MATCH", "未找到匹配片源"),
                ("PERMISSION_DENIED", "没有访问权限"),
                ("AUTH_REQUIRED", "请先登录账号"),
                ("AUTH_FAILED", "账号认证失败，请重新登录"),
                ("LOGIN_EXPIRED", "账号登录已失效，请重新登录"),
                ("MEDIA_UPSTREAM_TIMEOUT", "请求超时，请重试"),
                ("MEDIA_EMPTY_RESPONSE", "媒体响应为空"),
                (PlaybackError.HttpUnauthorized, "媒体认证失败")
            ];
            foreach (var failure in failures)
            {
                scene.Rig.Runtime.Intercept = (method, input, _) =>
                {
                    if (method != "film.session.command" || FilmJson.Text(input, "action") != "switch")
                        return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
                    return Task.FromResult(failure.Code == "INVALID_RESPONSE"
                        ? JsonSerializer.SerializeToElement(new { ok = true, contract = "film.playback.v1", value = (object?)null })
                        : FilmRuntimeProbe.Failure(failure.Code));
                };
                int before = scene.Notices.Count;
                Click(scene.Element<Button>("SwitchButton"));
                await Until(() => scene.Notices.Count == before + 1);
                Check(scene.Notices[^1] == failure.Message, "the real action publishes the formal failure category: " + failure.Code);
            }
            scene.Rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
                method == "film.session.command" && FilmJson.Text(input, "action") == "switch"
                    ? FilmRuntimeProbe.Failure("TIMEOUT: https://upstream.invalid/?token=fixture-secret Cookie=fixture-cookie")
                    : scene.Rig.Runtime.Handle(method, input));
            int count = scene.Notices.Count;
            Click(scene.Element<Button>("SwitchButton"));
            await Until(() => scene.Notices.Count == count + 1);
            Check(scene.Notices[^1] == "操作失败，请重试", "unknown codes reveal neither raw code nor upstream address or credentials");
            scene.Rig.Runtime.Intercept = (method, input, _) =>
                method == "film.session.command" && FilmJson.Text(input, "action") == "switch"
                    ? Task.FromException<JsonElement>(new BridgeException("PERMISSION_DENIED"))
                    : Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            count = scene.Notices.Count;
            Click(scene.Element<Button>("SwitchButton"));
            await Until(() => scene.Notices.Count == count + 1);
            Check(scene.Notices[^1] == "没有访问权限", "public bridge exceptions retain their typed code without reading Exception.Message");
        });
        yield return Test("unknown exceptions keep the action fallback while success cancellation and retired pages add no failure notice", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Rig.Runtime.Intercept = (method, input, _) =>
                method == "film.session.command" && FilmJson.Text(input, "action") == "switch"
                    ? Task.FromException<JsonElement>(new InvalidOperationException("TIMEOUT"))
                    : Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            Click(scene.Element<Button>("SwitchButton"));
            await Until(() => scene.Notices.Count == 1);
            Check(scene.Notices.Single() == "操作失败，请重试", "a plain exception message is never treated as a protocol code");
            scene.Rig.Runtime.Change(root => root["sources"] = System.Text.Json.Nodes.JsonNode.Parse(
                FilmJson.Encode(new[] { new { id = "source-failure", title = "可选来源" } }).GetRawText()));
            await Until(() => scene.Rig.Page.Sources.Any(source => source.Id == "source-failure"));
            scene.Rig.Runtime.Intercept = (method, input, _) =>
                method == "film.session.command" && FilmJson.Text(input, "action") == "browse"
                    ? Task.FromException<JsonElement>(new InvalidOperationException("https://upstream.invalid/?token=fixture-secret"))
                    : Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            scene.Element<ComboBox>("SourcePicker").SelectedItem = scene.Rig.Page.Sources.Single(source => source.Id == "source-failure");
            await Until(() => scene.Notices.Count == 2);
            Check(scene.Notices[^1] == "来源加载失败", "an unknown source failure retains its own safe operation fallback");
            scene.Rig.Runtime.Intercept = (method, input, _) =>
                method == "film.session.command" && FilmJson.Text(input, "action") == "switch"
                    ? Task.FromException<JsonElement>(new OperationCanceledException())
                    : Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            Click(scene.Element<Button>("SwitchButton"));
            await scene.Rig.Page.CommandAsync("search.more");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(scene.Notices.Count == 2, "an explicitly cancelled control adds no failure notice");
            scene.Rig.Runtime.Intercept = null;
            Click(scene.Element<Button>("SwitchButton"));
            await scene.Rig.Page.CommandAsync("search.more");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(scene.Notices.Count == 2, "successful control acknowledgement adds no failure notice");

            var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool entered = false;
            scene.Rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method != "film.session.command" || FilmJson.Text(input, "action") != "switch")
                    return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
                entered = true; return pending.Task;
            };
            try
            {
                Click(scene.Element<Button>("SwitchButton"));
                await Until(() => entered);
                scene.View.DataContext = new FilmPlaybackPage(scene.Rig.Page.Item, scene.Rig.Page.Request, null, null);
                pending.SetResult(FilmRuntimeProbe.Failure("AUTH_FAILED"));
                await scene.Rig.Page.CommandAsync("search.more");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(scene.Notices.Count == 2, "a retired page's late classified error cannot notify its replacement");
            }
            finally { pending.TrySetResult(FilmRuntimeProbe.Failure("CANCELLED")); }
        });
    }
    private static async Task SourceSearchMore()
    {
        await using var scene = await Scene.Create();
        BrowseSource(scene);
        await Until(() => scene.Rig.Page.SourceId == "site-result" && scene.Rig.Page.HasLines);
        var page = scene.Rig.Page;
        var source = scene.Element<ComboBox>("SourcePicker");
        var pan = scene.Element<ComboBox>("LinePicker");
        string? sourceId = page.SourceId;
        var lineId = page.SelectedLine!.WireId!.Value;
        int? season = page.Season;
        var target = page.ActiveTarget;
        int before = scene.Rig.Runtime.Calls.Count;
        source.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var popup = (Popup)source.Template.FindName("PART_Popup", source);
        var more = (Button)source.Template.FindName("SearchMoreSourcesButton", source);
        Check(popup.IsOpen && more.IsVisible && more.IsEnabled && more.IsDescendantOf(popup.Child) &&
            more.Content as string == "加载更多" && AutomationProperties.GetName(more) == "继续搜索未完成来源" &&
            source.Items.Cast<FilmSelection>().SequenceEqual(page.Sources) &&
            source.SelectedItem is FilmSelection selected && selected == page.SelectedSource &&
            scene.Rig.Runtime.Calls.Count == before,
            "opening the real source popup exposes continuation outside selectable source rows without requesting data");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Rig.Runtime.Intercept = async (method, input, token) =>
        {
            if (method != "film.session.command" || FilmJson.Text(input, "action") != "search.more")
                return scene.Rig.Runtime.Handle(method, input);
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return scene.Rig.Runtime.Handle(method, input);
        };
        try
        {
            Click(more);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!source.IsDropDownOpen && !more.IsEnabled && !page.CanSearchMore &&
                source.SelectedItem is FilmSelection sourceChoice && sourceChoice == page.SelectedSource &&
                pan.SelectedItem is FilmSelection panChoice && panChoice == page.SelectedLine,
                "the footer action closes its popup and disables continuation without becoming a source or pan selection");
            source.IsDropDownOpen = true;
            Click(more);
            finish.TrySetResult();
            await Until(() => page.CanSearchMore && more.IsEnabled);
            var commands = scene.Rig.Runtime.Calls.Skip(before)
                .Where(call => call.Method == "film.session.command").ToArray();
            Check(commands.Length == 1 && FilmJson.Text(commands[0].Parameters, "action") == "search.more" &&
                page.SourceId == sourceId && page.Season == season && page.ActiveTarget == target &&
                JsonElement.DeepEquals(page.SelectedLine!.WireId!.Value, lineId) &&
                source.SelectedItem is FilmSelection afterSource && afterSource == page.SelectedSource &&
                pan.SelectedItem is FilmSelection afterPan && afterPan == page.SelectedLine,
                "one continuation request preserves the exact source, typed line, season and playback target");
            foreach (string stage in new[] { "loading", "searching" })
            {
                scene.Rig.Runtime.Change(root => root["stage"] = stage);
                string status = stage == "loading" ? "加载中..." : "正在搜索片源...";
                await Until(() => page.PlayerStatusText == status && !more.IsEnabled && !page.CanSearchMore);
                Click(more);
            }
            scene.Rig.Runtime.Change(root =>
            {
                root["stage"] = "idle";
                root["search"] = JsonNode.Parse("""[{"sourceKey":"a","page":1,"completed":false,"loading":true}]""");
            });
            await Until(() => page.PlayerStatusText == "待播放" && !page.CanSearchMore && !more.IsEnabled);
            Click(more);
            scene.Rig.Runtime.Change(root => root["search"]![0]!["loading"] = false);
            await Until(() => page.CanSearchMore && more.IsEnabled);
            scene.Rig.Runtime.Change(root => { root["stage"] = "idle"; root["searchComplete"] = true; });
            await Until(() => page.PlayerStatusText == "待播放" && !page.CanSearchMore && !more.IsEnabled);
            Click(more);
            Check(scene.Rig.Runtime.Calls.Count(call => call.Method == "film.session.command" &&
                FilmJson.Text(call.Parameters, "action") == "search.more") == 1 &&
                page.SourceId == sourceId && page.Season == season &&
                JsonElement.DeepEquals(page.SelectedLine!.WireId!.Value, lineId),
                "loading, searching and completed owners cannot admit continuation or alter browsing selection");
        }
        finally { finish.TrySetResult(); source.IsDropDownOpen = false; }
    }

    private static async Task SourceSearchMoreFailure()
    {
        await using var scene = await Scene.Create();
        var page = scene.Rig.Page;
        var source = scene.Element<ComboBox>("SourcePicker");
        source.ApplyTemplate();
        var more = (Button)source.Template.FindName("SearchMoreSourcesButton", source);
        scene.Rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
            method == "film.session.command" && FilmJson.Text(input, "action") == "search.more"
                ? FilmRuntimeProbe.Failure("UPSTREAM_ERROR") : scene.Rig.Runtime.Handle(method, input));
        source.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Click(more);
        await Until(() => scene.Notices.Count == 1 && more.IsEnabled && page.CanSearchMore);
        Check(scene.Notices.Single() == "上游服务请求失败",
            "a rejected source continuation uses the existing floating notice and releases its pending state");
        scene.Rig.Runtime.Intercept = null;
        source.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Click(more);
        await Until(() => scene.Rig.Runtime.Calls.Count(call => call.Method == "film.session.command" &&
            FilmJson.Text(call.Parameters, "action") == "search.more") == 2 && page.CanSearchMore);
        Check(scene.Notices.Count == 1, "an explicit successful retry adds no failure notice");
        scene.Rig.Runtime.Intercept = (method, input, _) =>
            method == "film.session.command" && FilmJson.Text(input, "action") == "search.more"
                ? Task.FromException<JsonElement>(new OperationCanceledException())
                : Task.FromResult(scene.Rig.Runtime.Handle(method, input));
        source.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Click(more);
        await Until(() => scene.Rig.Runtime.Calls.Count(call => call.Method == "film.session.command" &&
            FilmJson.Text(call.Parameters, "action") == "search.more") == 3 && page.CanSearchMore);
        Check(scene.Notices.Count == 1 && more.IsEnabled,
            "a cancelled continuation releases its pending state without showing a failure notice");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Rig.Runtime.Intercept = (method, input, _) =>
        {
            if (method != "film.session.command" || FilmJson.Text(input, "action") != "search.more")
                return Task.FromResult(scene.Rig.Runtime.Handle(method, input));
            entered.TrySetResult();
            return finish.Task;
        };
        try
        {
            source.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(more);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scene.View.DataContext = new FilmPlaybackPage(page.Item, page.Request, null, null);
            finish.TrySetResult(FilmRuntimeProbe.Failure("AUTH_FAILED"));
            await Until(() => page.CanSearchMore);
            Check(scene.Notices.Count == 1, "a late continuation error cannot notify the replacement page");
        }
        finally { finish.TrySetResult(FilmRuntimeProbe.Failure("CANCELLED")); }
    }

    private static async Task BrowseToolbar()
    {
        await using var scene = await Scene.Create();
        BrowseSource(scene);
        await Until(() => scene.Rig.Page.HasSeasons && scene.Rig.Page.CanBrowseRaw);
        var page = scene.Rig.Page;
        var toolbar = scene.Element<DockPanel>("EpisodeToolbar");
        var source = scene.Element<ComboBox>("SourcePicker");
        var pan = scene.Element<ComboBox>("LinePicker");
        var preview = Visuals(toolbar).OfType<CheckBox>().Single();
        var controls = new FrameworkElement[] { source, pan, preview, scene.Element<Button>("RawDirectory"),
            scene.Element<Button>("HorizontalLayout"), scene.Element<Button>("ListLayout"),
            scene.Element<Button>("EpisodePrevious"), scene.Element<Button>("EpisodeNext") };
        foreach (double width in new[] { 1150d, 780d })
        {
            scene.Window.Width = width;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            scene.Window.UpdateLayout();
            double center = source.TranslatePoint(new(0, source.ActualHeight / 2), toolbar).Y;
            foreach (var control in controls)
            {
                var point = control.TranslatePoint(new(0, control.ActualHeight / 2), toolbar);
                Check(control.IsVisible && control.ActualWidth > 0 && Math.Abs(point.Y - center) < 1 &&
                    point.X >= -.5 && point.X + control.ActualWidth <= toolbar.ActualWidth + .5,
                    $"{control.Name} stays visible on the source toolbar at width {width}");
            }
            var seasons = scene.Element<ItemsControl>("SeasonPicker");
            Check(seasons.IsVisible && seasons.TranslatePoint(new(), scene.View).Y >=
                toolbar.TranslatePoint(new(0, toolbar.ActualHeight), scene.View).Y &&
                source.SelectedItem is FilmSelection selected && selected == page.SelectedSource &&
                pan.SelectedItem is FilmSelection line && line == page.SelectedLine,
                "real seasons remain below the toolbar while both exact browsing selections stay visible");
            int commands = scene.Rig.Runtime.Calls.Count;
            Click(scene.Element<Button>("ListLayout"));
            await Until(() => page.IsListLayout && !scene.Element<Button>("EpisodePrevious").IsVisible &&
                !scene.Element<Button>("EpisodeNext").IsVisible);
            Check(source.IsVisible && pan.IsVisible && preview.IsVisible && scene.Element<Button>("RawDirectory").IsVisible,
                "list mode keeps source, pan, preview and the real directory entry in place");
            Click(scene.Element<Button>("HorizontalLayout"));
            await Until(() => page.IsHorizontalLayout && scene.Element<Button>("EpisodePrevious").IsVisible &&
                scene.Element<Button>("EpisodeNext").IsVisible);
            Check(scene.Rig.Runtime.Calls.Count == commands,
                "changing episode presentation never refreshes a directory or searches a source");
        }
    }

    private static void BrowseSource(Scene scene) => scene.Rig.Runtime.Change(root =>
    {
        root["sources"] = JsonNode.Parse(FilmJson.Encode(new[]
        { new { id = "site-result", source = new { name = "准确站点" } } }).GetRawText());
        root["browsing"] = JsonNode.Parse(FilmJson.Encode(new
        {
            sourceId = "site-result", lineId = 0, season = 1, loading = false, error = (string?)null,
            lines = new[] { new { id = 0, provider = "quark", label = "夸克" } },
            episodes = new[] { new { id = "browse-file", candidateId = "browse-file", season = 1, episode = 1, globalEpisode = 1, name = "准确文件" } },
            originalEpisodes = new[] { new { id = "browse-file", candidateId = "browse-file", name = "准确文件", path = "/season/第1集.mp4" } }
        }).GetRawText());
        root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { Candidate("browse-file") }).GetRawText());
        root["metadata"]!["seasons"]!.AsArray().Add(JsonNode.Parse("""{"season_number":2,"name":"第二季","episode_count":8}"""));
    });

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmPlaybackRig Rig = new();
        internal readonly FilmPlaybackView View = new();
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        private Scene()
        {
            Window = new() { Content = View, Width = 1150, Height = 850, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            { if (args.Message.Length > 0) Notices.Add(args.Message); }));
        }
        internal static async Task<Scene> Create()
        {
            var scene = new Scene();
            try
            {
                await scene.Rig.OpenAsync(); scene.View.DataContext = scene.Rig.Page; scene.Window.Show();
                await Until(() => scene.View.IsLoaded && scene.View.ActualWidth > 0);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal async Task Play()
        {
            Rig.Runtime.Attempt("controls", Candidate("controls"));
            await Until(() => Rig.Native.SettledIdentity == "controls" && Called("media.started") && Rig.Session.HasOutput);
            View.UpdateLayout();
        }
        internal bool Called(string action) => Rig.Runtime.Calls.Any(call =>
            call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == action);
        internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
        public async ValueTask DisposeAsync()
        {
            foreach (Window owned in Window.OwnedWindows.Cast<Window>().ToArray())
            {
                owned.Close();
                await Until(() => !owned.IsVisible && !Window.OwnedWindows.Cast<Window>().Contains(owned));
            }
            View.DataContext = null; Window.Content = null; Window.Close(); await Rig.DisposeAsync();
        }
    }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task ToggleByKeyboardAsync(CheckBox box)
    {
        box.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // ButtonBase also consults the real mouse and modifiers for Space; the
        // KeyboardProbe only supplies the routed key, not those desktop states.
        await Until(() => Mouse.PrimaryDevice.LeftButton == MouseButtonState.Released &&
            Keyboard.Modifiers == ModifierKeys.None && !box.IsMouseCaptured,
            () => $"Keyboard input is not idle: mouse={Mouse.PrimaryDevice.LeftButton}, modifiers={Keyboard.Modifiers}, captured={box.IsMouseCaptured}");
        var keyboard = new KeyboardProbe();
        var source = PresentationSource.FromVisual(box)!;
        var down = new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent };
        box.RaiseEvent(down);
        Check(down.Handled && box.IsPressed,
            $"Space keydown must press the actual checkbox: handled={down.Handled}, pressed={box.IsPressed}, captured={box.IsMouseCaptured}, mouse={Mouse.PrimaryDevice.LeftButton}, modifiers={Keyboard.Modifiers}");
        var up = new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent };
        box.RaiseEvent(up);
        Check(up.Handled && !box.IsPressed,
            $"Space keyup must release the actual checkbox: handled={up.Handled}, pressed={box.IsPressed}, captured={box.IsMouseCaptured}, mouse={Mouse.PrimaryDevice.LeftButton}, modifiers={Keyboard.Modifiers}");
    }
    private sealed class KeyboardProbe() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }
    private static async Task Until(Func<bool> condition, Func<string>? failure = null)
    {
        try { await FilmPlaybackFixture.Until(condition); }
        catch (InvalidOperationException error) when (failure is not null)
        {
            throw new InvalidOperationException(failure(), error);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
