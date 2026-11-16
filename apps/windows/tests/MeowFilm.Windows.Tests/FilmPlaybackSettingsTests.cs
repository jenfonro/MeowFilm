using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackSettingsTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-playback-settings: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("native preferences read save and reopen offline while manual business still fails", async () =>
        {
            var runtime = new FilmRuleConsumerRuntime { Respond = (_, _, _) => throw new BridgeException("CLOSED") };
            await using var store = new FilmNativeSettingsFixture();
            var service = new FilmPlaybackSettings(runtime, store);
            var original = await service.ReadPreferencesAsync(default);
            var next = original with { Keywords = ["offline"] };
            await service.SavePreferencesAsync(next, "keywords");
            Check(runtime.Calls.Count == 0 && runtime.Notifications.SequenceEqual(["film.preferences"]) &&
                store.Value("film.preferences.v1").GetProperty("version").GetInt32() == 1,
                "ordinary preferences use the native version-one document and only a block notification");
            var reopened = new FilmPlaybackSettings(runtime, store);
            Check((await reopened.ReadPreferencesAsync(default)).Keywords.SequenceEqual(["offline"]), "new consumer reads the same native owner while offline");
            foreach (var error in new[] { DocumentStoreError.Storage, DocumentStoreError.Conflict })
            {
                store.WriteError = error;
                await PreferenceStoreError(error, () => service.SavePreferencesAsync(next with { Keywords = ["not-committed"] }, "keywords"));
                Check((await service.ReadPreferencesAsync(default)).Keywords.SequenceEqual(["offline"]) && runtime.Notifications.Count == 1,
                    "failed native CAS cannot notify or replace the committed value");
            }
            store.WriteError = null;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await service.ReadPreferencesAsync(cancelled.Token); throw new Exception("Cancelled read succeeded"); }
            catch (OperationCanceledException) { }
            await BridgeTests.Expect("CLOSED", () => service.SaveManualAsync(FilmJson.Encode(new { id = "offline" })));
            Check(runtime.Calls.Single().Method == "film.manual.update", "manual remains a real runtime business operation without native fallback");
            store.SeedBytes("film.preferences.v1", [123]);
            await PreferenceStoreError(DocumentStoreError.Corrupt, () => service.ReadPreferencesAsync(default));
            await PreferenceStoreError(DocumentStoreError.Corrupt, () => service.SavePreferencesAsync(next));
            Check(runtime.Notifications.Count == 1, "corrupt native preferences are not replaced by defaults");
        });
        yield return Test("native validation rejects edited preferences offline before any write or notification", async () =>
        {
            var runtime = new FilmRuleConsumerRuntime { Respond = (_, _, _) => throw new BridgeException("CLOSED") };
            await using var store = new FilmNativeSettingsFixture();
            store.Seed("film.preferences.v1", FilmJson.Encode(new { version = 1, preferences = FilmPreferences.Defaults }));
            var service = new FilmPlaybackSettings(runtime, store);
            var original = await service.ReadPreferencesAsync(default);
            var document = store.Value("film.preferences.v1");
            foreach (var (field, draft) in new (string, FilmPreferences)[]
            {
                ("ruleOrder", original with { RuleOrder = ["quality", "pan"] }),
                ("ruleOrder", original with { RuleOrder = ["quality", "pan", "pan"] }),
                ("ruleOrder", original with { RuleOrder = ["quality", "pan", "future"] }),
                ("ruleOrder", original with { RuleOrder = ["Quality", "pan", "keyword"] }),
                ("panOrder", original with { PanOrder = ["115"] }),
                ("panOrder", original with { PanOrder = ["139"] }),
                ("panOrder", original with { PanOrder = ["quark", " quark "] }),
                ("keywords", original with { RuleOrder = ["keyword", "pan", "quality"], Keywords = ["DDP", " ddp "] }),
                ("keywords", original with { Keywords = [""] }),
                ("keywords", original with { Keywords = [null!] }),
                ("keywords", original with { Keywords = null! }),
                ("siteCleanKeywords", original with { SiteCleanKeywords = ["Live", " live "] }),
                ("siteCleanKeywords", original with { SiteCleanKeywords = ["\ufeff\u00a0"] })
            })
            {
                foreach (string? editedField in new string?[] { field, null })
                {
                    await InvalidPreferences(() => service.SavePreferencesAsync(draft, editedField));
                    Check(store.Writes == 0 && runtime.Notifications.Count == 0 && runtime.Calls.Count == 0 &&
                        document.GetRawText() == store.Value("film.preferences.v1").GetRawText(),
                        "an invalid explicit or inferred edit fails before native admission without touching the offline runtime");
                }
            }
            await using var empty = new FilmNativeSettingsFixture();
            var first = new FilmPlaybackSettings(runtime, empty);
            await InvalidPreferences(() => first.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["DDP", "ddp"] }));
            Check((await empty.ReadAsync("film.preferences.v1")).Data is null && empty.Writes == 0 &&
                runtime.Notifications.Count == 0 && runtime.Calls.Count == 0, "an invalid first full save cannot create a document");
        });
        yield return Test("native preference receipts and reopen return trimmed values and complete partial pan orders", async () =>
        {
            var runtime = new FilmRuleConsumerRuntime { Respond = (_, _, _) => throw new BridgeException("CLOSED") };
            await using var store = new FilmNativeSettingsFixture();
            var service = new FilmPlaybackSettings(runtime, store);
            var draft = new FilmPreferences([" keyword ", "\tpan", "quality\u00a0"], [" baidu ", "\ufeffquark"],
                ["\ufeffDDP\u00a0", " H.265 ", "\u0085keep\u0085"], ["\u3000直播\ufeff"], false);
            var saved = await service.SavePreferencesAsync(draft);
            Check(saved.RuleOrder.SequenceEqual(["keyword", "pan", "quality"]) &&
                saved.PanOrder.SequenceEqual(["baidu", "quark", "yidong", "tianyi", "uc"]) &&
                saved.Keywords.SequenceEqual(["DDP", "H.265", "\u0085keep\u0085"]) &&
                saved.SiteCleanKeywords.SequenceEqual(["直播"]) && !saved.IosAutoPrepare,
                "first typed input uses JS whitespace, preserves non-whitespace NEL and keyword casing, and appends missing providers");
            Check(draft.PanOrder.SequenceEqual([" baidu ", "\ufeffquark"]) && draft.Keywords[0] == "\ufeffDDP\u00a0",
                "normalizing a save does not mutate its caller's arrays");
            var reopened = await new FilmPlaybackSettings(runtime, store).ReadPreferencesAsync(default);
            Check(JsonElement.DeepEquals(FilmJson.Encode(saved), FilmJson.Encode(reopened)) &&
                JsonElement.DeepEquals(store.Value("film.preferences.v1").GetProperty("preferences"), FilmJson.Encode(saved)) &&
                store.Writes == 1 && runtime.Notifications.SequenceEqual(["film.preferences"]) && runtime.Calls.Count == 0,
                "the canonical response, stored native value and reopened owner agree without a runtime call");
            saved = await service.SavePreferencesAsync(saved with { PanOrder = [] }, "panOrder");
            Check(saved.PanOrder.SequenceEqual(FilmPreferences.Defaults.PanOrder), "an empty edited pan order expands to every provider");
            saved = await service.SavePreferencesAsync(saved with { Keywords = [] }, "keywords");
            saved = await service.SavePreferencesAsync(saved with { SiteCleanKeywords = [] }, "siteCleanKeywords");
            Check(saved.Keywords.Length == 0 && saved.SiteCleanKeywords.Length == 0 && !saved.IosAutoPrepare &&
                saved.RuleOrder.SequenceEqual(["keyword", "pan", "quality"]) && store.Writes == 4 &&
                runtime.Notifications.SequenceEqual(Enumerable.Repeat("film.preferences", 4)) && runtime.Calls.Count == 0,
                "explicit empty keyword groups stay empty and leave other groups and the typed switch alone");
        });
        yield return Test("explicit and inferred preference edits preserve untouched raw shapes and missing fields", async () =>
        {
            var runtime = new FilmRuleConsumerRuntime { Respond = (_, _, _) => throw new BridgeException("CLOSED") };
            await using var store = new FilmNativeSettingsFixture();
            var service = new FilmPlaybackSettings(runtime, store);
            var raw = JsonSerializer.Deserialize<JsonElement>("""
                {"ruleOrder":["future-rule","quality","future-rule"],"panOrder":{"future":["pan"]},"keywords":["visible",7],
                 "siteCleanKeywords":[false,{"nested":true}],"iosAutoPrepare":{"future":1},"extra":{"nested":[7]}}
                """);
            store.Seed("film.preferences.v1", FilmJson.Encode(new { version = 1, preferences = raw }));
            var original = await service.ReadPreferencesAsync(default);
            await service.SavePreferencesAsync(original);
            Check(store.Writes == 0 && JsonElement.DeepEquals(raw, store.Value("film.preferences.v1").GetProperty("preferences")),
                "an unchanged projection does not validate, normalize or rewrite legacy branches");
            int writes = 0;
            foreach (string? field in new string?[] { "keywords", null })
            {
                store.Seed("film.preferences.v1", FilmJson.Encode(new { version = 1, preferences = raw }));
                original = await service.ReadPreferencesAsync(default);
                var draft = original with { Keywords = ["\ufeff visible \u00a0"] };
                if (field is not null) draft = draft with { RuleOrder = ["future"], PanOrder = ["115"], SiteCleanKeywords = ["x", "X"] };
                var saved = await service.SavePreferencesAsync(draft, field);
                var value = store.Value("film.preferences.v1").GetProperty("preferences");
                foreach (string untouched in new[] { "ruleOrder", "panOrder", "siteCleanKeywords", "iosAutoPrepare", "extra" })
                    Check(JsonElement.DeepEquals(raw.GetProperty(untouched), value.GetProperty(untouched)),
                        "only the selected or actually changed field is replaced: " + untouched);
                Check(saved.Keywords.SequenceEqual(["visible"]) && value.GetProperty("keywords").GetArrayLength() == 1 &&
                    store.Writes == ++writes, "a real edit replaces the raw branch even when trimming restores its old projected value");
            }
            original = await service.ReadPreferencesAsync(default);
            var changed = await service.SavePreferencesAsync(original with
                { PanOrder = [" baidu "], SiteCleanKeywords = [], IosAutoPrepare = true });
            var committed = store.Value("film.preferences.v1").GetProperty("preferences");
            Check(changed.PanOrder.SequenceEqual(["baidu", "yidong", "tianyi", "quark", "uc"]) &&
                changed.SiteCleanKeywords.Length == 0 && changed.IosAutoPrepare &&
                JsonElement.DeepEquals(raw.GetProperty("ruleOrder"), committed.GetProperty("ruleOrder")) &&
                JsonElement.DeepEquals(raw.GetProperty("extra"), committed.GetProperty("extra")),
                "multiple inferred edits normalize only their changed branches and retain future order values");
            store.Seed("film.preferences.v1", FilmJson.Encode(new { version = 1, preferences = new { keywords = new[] { "visible" }, extra = 7 } }));
            original = await service.ReadPreferencesAsync(default);
            await service.SavePreferencesAsync(original with { Keywords = [" next "] });
            committed = store.Value("film.preferences.v1").GetProperty("preferences");
            Check(committed.EnumerateObject().Count() == 2 && committed.GetProperty("keywords")[0].GetString() == "next" &&
                committed.GetProperty("extra").GetInt32() == 7 && runtime.Calls.Count == 0,
                "an inferred keyword edit never materializes unedited missing fields");
        });
        yield return Test("duplicate keyword drafts fail before storage and a corrected draft can still save", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Runtime.Intercept = (_, _, _) => throw new BridgeException("CLOSED");
            int writes = scene.Writes, calls = scene.Runtime.Calls.Count;
            var original = scene.Preferences;
            scene.Element<TextBox>("Keywords").Text = "DDP, ddp";
            Click(scene.Button("保存关键字匹配规则"));
            await Until(() => scene.View.Idle && scene.Notices.Contains("保存失败"));
            Check(scene.Writes == writes && scene.Runtime.Calls.Count == calls &&
                JsonElement.DeepEquals(scene.Preferences, original) && !scene.Notices.Contains("设置已保存") &&
                scene.Element<TextBox>("Keywords").Text == "DDP, ddp",
                "the UI keeps the rejected draft and reports failure without committing it or contacting the offline runtime");
            scene.Element<TextBox>("Keywords").Text = "\ufeffDDP\u00a0, H.265";
            Click(scene.Button("保存关键字匹配规则"));
            await Until(() => scene.View.Idle && scene.Notices.Contains("设置已保存"));
            Check(scene.Writes == writes + 1 && scene.Runtime.Calls.Count == calls &&
                FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["DDP", "H.265"]) &&
                scene.Element<TextBox>("Keywords").Text == "DDP,H.265", "a valid retry publishes the canonical native receipt");
        });
        yield return Test("unrelated preference shapes survive edits and unknown order values remain visible", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Preferences = JsonSerializer.Deserialize<JsonElement>("""
                {"ruleOrder":["future-rule","quality","future-rule"],"panOrder":{"future":["pan"]},"keywords":["visible"],
                 "siteCleanKeywords":[false,{"nested":true}],"iosAutoPrepare":{"future":1},"extra":{"nested":[7]}}
                """);
            await scene.View.ShowAsync("clean");
            await scene.View.ShowAsync("preferences");
            await Until(() => scene.View.Idle);
            Check(!scene.View.ReadFailed && scene.Element<TextBox>("Keywords").Text == "visible" &&
                scene.Element<SettingsPriorityList>("RuleOrder").ItemsSource!.Cast<object>().Count() == 2 &&
                Visuals(scene.View).OfType<TextBlock>().Any(block => block.Text == "future-rule"),
                "another group's shape and duplicate unknown order ids do not reject the current editable group");
            var original = scene.Preferences;
            scene.Element<TextBox>("Keywords").Text = "changed";
            Click(scene.Button("保存关键字匹配规则")); await Until(() => scene.View.Idle);
            foreach (string field in new[] { "ruleOrder", "panOrder", "siteCleanKeywords", "iosAutoPrepare", "extra" })
                Check(JsonElement.DeepEquals(original.GetProperty(field), scene.Preferences.GetProperty(field)),
                    "saving keywords preserves the original unedited field: " + field);
            Check(scene.Preferences.GetProperty("keywords")[0].GetString() == "changed",
                "the explicit field is really saved");
            await scene.View.ShowAsync("clean");
            scene.Element<TextBox>("CleanKeywords").Text = "";
            Click(scene.Button("保存站点净化")); await Until(() => scene.View.Idle);
            Check(scene.Preferences.GetProperty("siteCleanKeywords").GetArrayLength() == 0 &&
                JsonElement.DeepEquals(original.GetProperty("iosAutoPrepare"), scene.Preferences.GetProperty("iosAutoPrepare")),
                "an explicit empty edit replaces only its own formerly unconsumable branch");
        });
        yield return Test("full preferences preserve empty keywords and restore only the chosen group across a new view", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Element<TextBox>("Keywords").Text = "";
            Click(scene.Button("保存关键字匹配规则"));
            await Until(() => scene.View.Idle);
            Check(FilmPreferences.Read(scene.Preferences).Keywords.Length == 0 &&
                FilmPreferences.Read(scene.Preferences).PanOrder[0] == "baidu", "empty is legal and other groups survive the full save");
            var restore = Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Tag, "rule"));
            Click(restore); await Until(() => scene.View.Idle);
            var value = FilmPreferences.Read(scene.Preferences);
            Check(value.RuleOrder.SequenceEqual(FilmPreferences.Defaults.RuleOrder) && value.PanOrder[0] == "baidu" &&
                value.Keywords.Length == 0 && value.SiteCleanKeywords.SequenceEqual(["fixture-clean"]) && !value.IosAutoPrepare,
                "restore does not reset unrelated groups or the platform prepare switch");
            scene.Window.Content = null;
            var next = new FilmPlaybackSettingsView(scene.Service, () => Card().PlaybackContext!.Target);
            scene.Window.Content = next; await next.ShowAsync("preferences");
            await Until(() => next.Idle && ((SettingsPriorityList)next.FindName("RuleOrder")).ItemsSource?.Cast<object>().Count() == 3);
            Check(((TextBox)next.FindName("Keywords")).Text == "" &&
                !FilmPreferences.Read(scene.Preferences).IosAutoPrepare, "a new presentation reads the same durable owner response");
            await next.PrepareShutdownAsync(); scene.Window.Content = scene.View;
        });
        yield return Test("a retired section's late read cannot overwrite the next collection", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.View.ShowAsync("clean");
            var entered = Gate(); var finish = Gate();
            int reads = 0; CancellationToken oldToken = default;
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.library.get" && ++reads == 1)
                {
                    oldToken = token; entered.TrySetResult(); await finish.Task;
                    return FilmRuntimeProbe.Ok(FilmJson.Encode(new { history = new[] { new { title = "旧历史" } } }));
                }
                return scene.Response(method, input);
            };
            try
            {
                var old = scene.View.ShowAsync("preferences"); await entered.Task;
                var next = scene.View.ShowAsync("clean");
                Check(oldToken.IsCancellationRequested && !next.IsCompleted, "the old consumer retires but its actual read is still drained");
                finish.TrySetResult(); await Task.WhenAll(old, next);
                await Until(() => scene.View.Idle && scene.Element<TextBox>("CleanKeywords").Text == "fixture-clean");
                Check(scene.View.Rows.Count == 0 && scene.View.ManualGroups.Count == 0 && scene.View.BlockGroups.Count == 0,
                    "late library data never becomes the site-clean collection");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("admitted mutations outlive section changes and shutdown waits without cancelling the original payload", async () =>
        {
            await using var scene = await Scene.Create();
            var entered = Gate(); var finish = Gate(); CancellationToken writeToken = default;
            scene.Store.Writing = async (_, token) =>
            { writeToken = token; entered.TrySetResult(); await finish.Task; };
            try
            {
                scene.Element<TextBox>("Keywords").Text = " first,，second, ";
                Click(scene.Button("保存关键字匹配规则")); await entered.Task;
                var switching = scene.View.ShowAsync("clean");
                var close = scene.View.PrepareShutdownAsync();
                Check(!close.IsCompleted && !writeToken.IsCancellationRequested && !scene.View.Idle,
                    "UI deactivation revokes publication, not an accepted durable mutation");
                finish.TrySetResult(); await Task.WhenAll(switching, close);
                Check(FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["first", "second"]) &&
                    !scene.Notices.Contains("设置已保存"), "the original save settles without a success notification in a retired section");
                scene.View.Resume();
                await Until(() => scene.View.Idle && scene.Element<TextBox>("CleanKeywords").Text == "fixture-clean");
                Check(scene.Element<Border>("CleanForm").IsVisible && scene.View.Rows.Count == 0,
                    "a cancelled shutdown can read the current section again");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("a failed write is notified without publishing success or permanently blocking shutdown", async () =>
        {
            await using var scene = await Scene.Create();
            scene.FailWrite = true;
            scene.Element<TextBox>("Keywords").Text = "unsaved";
            Click(scene.Button("保存关键字匹配规则")); await Until(() => scene.View.Idle);
            Check(scene.Notices.Contains("保存失败") && !scene.Notices.Contains("设置已保存") &&
                !FilmPreferences.Read(scene.Preferences).Keywords.Contains("unsaved"),
                "failed storage never becomes a successful preference snapshot");
            await scene.View.PrepareShutdownAsync(); await scene.View.PrepareShutdownAsync();
            scene.FailWrite = false; scene.View.Resume();
            await Until(() => scene.View.Idle && scene.Element<TextBox>("Keywords").Text == "fixture-keyword");
        });
        yield return Test("ordering saves exactly once per real move without saving or discarding keyword drafts", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Element<TextBox>("Keywords").Text = "not-saved-yet";
            int writes = scene.Writes;
            foreach (var name in new[] { "RuleOrder", "PanOrder" })
            {
                var priority = scene.Element<SettingsPriorityList>(name);
                priority.BringIntoView(); await Until(() => priority.CanEdit);
                var list = (ListBox)priority.FindName("PriorityItems");
                var original = list.Items.Cast<object>().ToArray();
                var first = Grip(list, 0);
                ComponentCommands.MoveUp.Execute(null, first);
                Check(scene.Writes == writes, "moving the first row up is a no-op, not a save");
                ComponentCommands.MoveDown.Execute(null, first); await Until(() => scene.View.Idle);
                Check(scene.Writes == ++writes && ReferenceEquals(list.Items[1], original[0]),
                    "one keyboard move saves once and retains the moved item identity");
                Check(scene.Element<TextBox>("Keywords").Text == "not-saved-yet" &&
                    FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["fixture-keyword"]),
                    "ordering neither persists nor clears a separate keyword draft");
            }
            var saved = FilmPreferences.Read(scene.Preferences);
            Check(saved.RuleOrder.SequenceEqual(["pan", "keyword", "quality"]) &&
                saved.PanOrder.SequenceEqual(["uc", "baidu", "quark", "tianyi", "yidong"]),
                "both order controls save the exact chosen position without resetting each other");
            Click(scene.Button("恢复优先片源默认规则")); await Until(() => scene.View.Idle);
            Check(scene.Element<TextBox>("Keywords").Text == "not-saved-yet" &&
                FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["fixture-keyword"]) &&
                FilmPreferences.Read(scene.Preferences).PanOrder.SequenceEqual(saved.PanOrder),
                "restoring an order also leaves every unrelated draft and saved group alone");
            scene.Element<TextBox>("Keywords").Text = "";
            await Until(() => scene.Element<TextBlock>("KeywordsPlaceholder").IsVisible);
            Check(scene.Element<TextBlock>("KeywordsPlaceholder").Text == "DDP,H.265",
                "the server keyword example is visible only for an empty field");
            Click(scene.Button("保存关键字匹配规则")); await Until(() => scene.View.Idle);
            Check(FilmPreferences.Read(scene.Preferences).Keywords.Length == 0, "the placeholder is never submitted as data");
        });
        yield return Test("drag cancellation and same-position release do not save but an actual drop does", async () =>
        {
            await using var scene = await Scene.Create();
            var priority = scene.Element<SettingsPriorityList>("RuleOrder");
            var list = (ListBox)priority.FindName("PriorityItems");
            var first = list.Items[0]; int writes = scene.Writes;
            Point Begin(int destination)
            {
                var grip = Grip(list, 0);
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(destination);
                var drop = row.TranslatePoint(new Point(60, row.ActualHeight - 2), priority.DragViewport);
                priority.Drag.Begin(grip, list, grip.TranslatePoint(new Point(18, 18), priority.DragViewport));
                priority.Drag.Update(drop);
                Check(priority.IsDragging, "the real shared control owns a captured gesture");
                return drop;
            }
            priority.CompleteDrag(Begin(2), true);
            priority.CompleteDrag(Begin(0), false);
            Check(scene.Writes == writes && ReferenceEquals(list.Items[0], first), "cancel and in-place release do not mutate");
            var point = Begin(2);
            priority.CompleteDrag(point, false); await Until(() => scene.View.Idle);
            priority.CompleteDrag(point, false);
            Check(scene.Writes == writes + 1 && ReferenceEquals(list.Items[2], first) &&
                FilmPreferences.Read(scene.Preferences).RuleOrder.SequenceEqual(["pan", "quality", "keyword"]),
                "one accepted drop saves exactly its final order, duplicate release does nothing");
        });
        yield return Test("failed ordering restores the authoritative sequence and preserves the keyword draft", async () =>
        {
            await using var scene = await Scene.Create();
            var priority = scene.Element<SettingsPriorityList>("RuleOrder");
            var list = (ListBox)priority.FindName("PriorityItems");
            var original = list.Items.Cast<object>().ToArray();
            scene.Element<TextBox>("Keywords").Text = "keep-draft";
            scene.FailWrite = true; int writes = scene.Writes;
            ComponentCommands.MoveDown.Execute(null, Grip(list, 0)); await Until(() => scene.View.Idle);
            Check(scene.Writes == writes + 1 && list.Items.Cast<object>().SequenceEqual(original) &&
                FilmPreferences.Read(scene.Preferences).RuleOrder.SequenceEqual(["keyword", "pan", "quality"]),
                "failure rolls back displayed order using the same row identities");
            Check(scene.Notices.Contains("保存失败") && !scene.Notices.Contains("设置已保存") &&
                scene.Element<TextBox>("Keywords").Text == "keep-draft", "failure is visible and does not discard the text draft");
        });
        yield return Test("an admitted order keeps keyboard ownership and drains on section retirement without stale success", async () =>
        {
            await using var scene = await Scene.Create();
            var priority = scene.Element<SettingsPriorityList>("RuleOrder");
            var list = (ListBox)priority.FindName("PriorityItems");
            var moved = list.Items[0];
            var entered = Gate(); var finish = Gate(); CancellationToken writeToken = default;
            scene.Store.Writing = async (_, token) =>
            { writeToken = token; entered.TrySetResult(); await finish.Task; };
            try
            {
                scene.Window.Focus(); var handle = Grip(list, 0); handle.Focus();
                Check(ReferenceEquals(Keyboard.FocusedElement, handle), "the actual handle initially owns keyboard focus");
                ComponentCommands.MoveDown.Execute(null, handle); await entered.Task;
                Check(!priority.CanEdit && priority.IsEnabled && ReferenceEquals(Keyboard.FocusedElement, priority),
                    "only list editing pauses while the shared control retains its focus-restoration owner");
                finish.TrySetResult(); await Until(() => scene.View.Idle);
                Check(ReferenceEquals(list.Items[1], moved) && ReferenceEquals(Keyboard.FocusedElement, Grip(list, 1)),
                    "the original row regains its handle after the automatic save");
                var retired = Gate(); var settle = Gate();
                scene.Store.Writing = async (_, token) =>
                { writeToken = token; retired.TrySetResult(); await settle.Task; };
                try
                {
                    int notices = scene.Notices.Count;
                    ComponentCommands.MoveDown.Execute(null, Grip(list, 1)); await retired.Task;
                    var switching = scene.View.ShowAsync("clean");
                    var closing = scene.View.PrepareShutdownAsync();
                    Check(!closing.IsCompleted && !writeToken.IsCancellationRequested,
                        "a durable order write is drained, not cancelled with its page");
                    settle.TrySetResult(); await Task.WhenAll(switching, closing); await Until(() => !priority.CanEdit);
                    Check(FilmPreferences.Read(scene.Preferences).RuleOrder.SequenceEqual(["pan", "quality", "keyword"]) &&
                        scene.Notices.Count == notices, "the exact order settles without publishing success to a retired section");
                }
                finally { settle.TrySetResult(); }
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("read failure exposes only a retry action and successful retry never writes preferences", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.View.ShowAsync("clean");
            int writes = scene.Writes;
            scene.Store.ReadError = DocumentStoreError.Storage;
            await scene.View.ShowAsync("preferences");
            var retry = scene.Button("重新读取智能播放设置");
            await Until(() => scene.View.ReadFailed && retry.IsVisible);
            Check(scene.View.ReadFailed && retry.IsVisible && scene.Notices.Contains("影视设置读取失败"),
                "failed reads have an explicit visible retry");
            scene.Store.ReadError = null; Click(retry);
            await Until(() => scene.View.Idle && !scene.View.ReadFailed);
            Check(!retry.IsVisible && scene.Writes == writes &&
                scene.Element<TextBox>("Keywords").Text == "fixture-keyword", "recovery only reads the authoritative settings");
        });
        yield return Test("runtime library wait and failure never block native preference editing", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.View.ShowAsync("clean");
            var entered = Gate(); var finish = Gate();
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.library.get")
                {
                    entered.TrySetResult(); await finish.Task;
                    throw new BridgeException("CLOSED");
                }
                return scene.Response(method, input);
            };
            try
            {
                var opening = scene.View.ShowAsync("preferences");
                await entered.Task;
                Check(scene.View.Idle && !scene.View.CanEditLibrary &&
                    scene.Element<TextBox>("Keywords").Text == "fixture-keyword",
                    "local settings publish before the runtime business request completes");
                scene.Element<TextBox>("Keywords").Text = "offline-saved";
                Click(scene.Button("保存关键字匹配规则"));
                await Until(() => scene.View.Idle && FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["offline-saved"]));
                finish.TrySetResult(); await opening;
                await Until(() => scene.Notices.Contains("匹配记录读取失败"));
                Check(!scene.View.ReadFailed && scene.View.LibraryFailed && scene.View.Idle &&
                    scene.Notices.Contains("匹配记录读取失败") &&
                    scene.Element<TextBox>("Keywords").Text == "offline-saved",
                    "runtime failure is isolated from the committed native settings and current editor");
                scene.Element<TextBox>("Keywords").Text = "unsaved-draft";
                scene.Runtime.Intercept = (method, input, _) => Task.FromResult(scene.Response(method, input));
                Click(scene.Button("重新读取智能播放设置"));
                await Until(() => !scene.View.LibraryFailed && scene.View.CanEditLibrary);
                Check(scene.Element<TextBox>("Keywords").Text == "unsaved-draft" &&
                    FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["offline-saved"]),
                    "retrying runtime records neither saves nor discards an unrelated native draft");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("clear collections and typed block identities reach the one business owner", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Service.RemoveBlockAsync(null);
            var calls = scene.Runtime.Calls.Where(call => call.Method == "film.library.remove").ToArray();
            Check(calls.Length == 1 && FilmJson.Text(calls[0].Parameters, "kind") == "blocks" &&
                FilmJson.Member(calls[0].Parameters, "block").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                "clear is the official omitted/null identity operation, not repeated guessed deletions");
            var block = FilmJson.Encode(new { targetId = "tmdb:tv:100", sourceKey = "source:fixture", detailId = 0, lineId = 0 });
            await scene.Service.RemoveBlockAsync(block);
            var sent = scene.Runtime.Calls.Last().Parameters.GetProperty("block");
            Check(JsonElement.DeepEquals(sent, block), "numeric zero and the whole exact block identity survive the consumer");
        });
        yield return Test("manual editing preserves numeric zero special season and plain editable share controls", async () =>
        {
            await using var scene = await Scene.Create();
            scene.LibraryValue = FilmJson.Encode(new { manual = new[] { new
            { id = "fixture-manual", targetId = "tmdb:tv:100", title = "人工样例", kind = "site",
                sourceKey = "source:fixture", detailId = 0, seasonHint = 0, enabled = true, autoDisable = true, failureCount = 2 } }, blocks = Array.Empty<object>() });
            await scene.View.ShowAsync("clean"); await scene.View.ShowAsync("preferences"); scene.View.UpdateLayout();
            Click(Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Content, "编辑")));
            var dialog = scene.Window.OwnedWindows.Cast<Window>().Single();
            Check(Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "详情标识").Text == "0" &&
                Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "季线索").Text == "0",
                "editing does not treat zero as a missing identity or season");
            Check(!Visuals(dialog).OfType<PasswordBox>().Any() &&
                Visuals(dialog).OfType<TextBox>().Count(box => AutomationProperties.GetName(box) is "分享提取码" or "分享链接") == 2,
                "share fields use the same plain text editing model as data source credentials");
            Click(Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存")));
            await Until(() => !dialog.IsVisible && scene.View.Idle);
            var saved = scene.Runtime.Calls.Last(call => call.Method == "film.manual.update").Parameters.GetProperty("item");
            Check(saved.GetProperty("detailId").ValueKind == JsonValueKind.Number && saved.GetProperty("detailId").GetInt32() == 0 &&
                saved.GetProperty("seasonHint").GetInt32() == 0 && saved.GetProperty("failureCount").GetInt32() == 2 &&
                saved.GetProperty("autoDisable").GetBoolean(), "the exact typed edit reaches the business owner without resetting failure state");
        });
        yield return Test("a late manual save cannot reopen or populate a retired editor", async () =>
        {
            await using var scene = await Scene.Create();
            Click((Button)scene.View.FindName("AddManual"));
            var dialog = scene.Window.OwnedWindows.Cast<Window>().Single();
            Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "来源标识").Text = "source:fixture";
            Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "详情标识").Text = "fixture-detail";
            var entered = Gate(); var finish = Gate();
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.manual.update") { entered.TrySetResult(); await finish.Task; }
                return scene.Response(method, input);
            };
            try
            {
                Click(Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存")));
                await entered.Task;
                var next = scene.View.ShowAsync("clean");
                Check(!dialog.IsVisible, "section retirement closes the editor immediately, not only after its save finishes");
                finish.TrySetResult(); await next;
                Check(!dialog.IsVisible && scene.Window.OwnedWindows.Count == 0 && scene.View.Rows.Count == 0 && scene.Element<Border>("CleanForm").IsVisible,
                    "late durable success does not reopen the editor or overwrite the next collection");
            }
            finally { finish.TrySetResult(); }
        });
    }

    private static async Task InvalidPreferences(Func<Task> operation)
    {
        try { await operation(); throw new Exception("Expected invalid native preferences"); }
        catch (FilmPlaybackException error) { Check(error.Code == "INVALID_PREFERENCES", "the native owner preserves the fixed validation code"); }
    }

    private static async Task PreferenceStoreError(DocumentStoreError code, Func<Task> operation)
    {
        try { await operation(); throw new Exception("Expected native storage failure"); }
        catch (DocumentStoreException error) { Check(error.Error == code, "native storage code is preserved"); }
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmRuntimeProbe Runtime = new();
        internal readonly FilmPlaybackSettings Service;
        internal readonly FilmPlaybackSettingsView View;
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        internal readonly FilmNativeSettingsFixture Store = new();
        internal bool FailWrite { set => Store.WriteError = value ? DocumentStoreError.Storage : null; }
        internal int Writes => Store.Writes;
        internal JsonElement Preferences
        {
            get => Store.Value("film.preferences.v1").GetProperty("preferences");
            set => Store.Seed("film.preferences.v1", FilmJson.Encode(new { version = 1, preferences = value }));
        }
        internal JsonElement LibraryValue = FilmJson.Encode(new
        {
            history = new[] { new { title = "当前历史", targetId = "tmdb:tv:100", position = 10d, duration = 600d } },
            blocks = new[] { new { title = "当前屏蔽", targetId = "tmdb:tv:100", sourceKey = "fixture", detailId = 0, lineId = 0 } },
            manual = Array.Empty<object>()
        });
        private Scene()
        {
            Preferences = FilmJson.Encode(new FilmPreferences(["keyword", "pan", "quality"],
                ["baidu", "uc", "quark", "tianyi", "yidong"], ["fixture-keyword"], ["fixture-clean"], false));
            Service = new(Runtime, Store); View = new(Service, () => Card().PlaybackContext!.Target);
            Runtime.Intercept = (method, input, _) => Task.FromResult(Response(method, input));
            Window = new() { Content = View, Width = 1000, Height = 800, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            { if (args.Message.Length > 0) Notices.Add(args.Message); }));
        }
        internal JsonElement Response(string method, JsonElement input)
        {
            if (method == "film.library.get") return FilmRuntimeProbe.Ok(LibraryValue);
            return Runtime.Handle(method, input);
        }
        internal static async Task<Scene> Create()
        {
            var scene = new Scene();
            try
            {
                await scene.View.ShowAsync("preferences"); scene.Window.Show();
                await Until(() => scene.View.Idle && scene.Element<SettingsPriorityList>("RuleOrder").ItemsSource?.Cast<object>().Count() == 3);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
        internal Button Button(string name) => Visuals(View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        public async ValueTask DisposeAsync()
        {
            await View.PrepareShutdownAsync();
            foreach (Window owned in Window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            Window.Content = null; Window.Close(); await Runtime.DisposeAsync(); await Store.DisposeAsync();
        }
    }
    private static Thumb Grip(ListBox list, int index)
    {
        list.UpdateLayout();
        return Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<Thumb>().Single();
    }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        await FilmPlaybackFixture.Until(condition);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
