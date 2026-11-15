using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private const string IrregularSourceFields = """{"weight":{},"lastUpdateTime":[],"respondTime":"unknown","hasExplore":17}""";
    // The current Node producer normalizes these fields. Raw perturbations test the native consumer contract.
    private static JsonElement SourceResponseFields(JsonElement value, string fields)
    {
        var response = JsonNode.Parse(value.GetRawText())!.AsObject();
        var overrides = JsonNode.Parse(fields)!.AsObject();
        var records = response["sources"] is JsonArray array ? array.ToArray() : new[] { response["source"] };
        foreach (var record in records.OfType<JsonObject>())
        {
            foreach (string key in new[] { "weight", "lastUpdateTime", "respondTime", "hasExplore" }) record.Remove(key);
            foreach (var (key, material) in overrides) record[key] = material?.DeepClone();
        }
        return JsonSerializer.SerializeToElement(response, NovelJson.Options);
    }
    private static async Task SourceProjectionContract()
    {
        var runtime = new Runtime(); var sources = new NovelSourceService(runtime);
        try
        {
            foreach (var (fields, weight, updated, response, explore) in new (string, double, double, double, bool)[]
            {
                ("{}", 0, 0, 180000, false),
                (IrregularSourceFields, 0, 0, 180000, false),
                ("""{"weight":null,"lastUpdateTime":null,"respondTime":null,"hasExplore":null}""", 0, 0, 180000, false),
                ("""{"weight":1e400,"lastUpdateTime":-1e400,"respondTime":1e400,"hasExplore":"true"}""", 0, 0, 180000, false),
                ("""{"weight":-2.5,"lastUpdateTime":123.5,"respondTime":27.25,"hasExplore":true}""", -2.5, 123.5, 27.25, true)
            })
            {
                runtime.Project = (_, value) => SourceResponseFields(value, fields);
                await sources.RefreshAsync();
                var expected = Source() with { Weight = weight, LastUpdateTime = updated, RespondTime = response, HasExplore = explore };
                var projected = sources.Sources.Single();
                Check((projected with { Actions = expected.Actions }) == expected && projected.Actions.SequenceEqual(expected.Actions) &&
                    NovelCapability.CanExplore(projected), "Optional management materials changed source identity or executable actions.");
                var current = await sources.GetAsync(projected);
                var fromGet = NovelSource.From(current.GetProperty("source"));
                var definition = JsonNode.Parse(current.GetProperty("definition").GetString()!)!;
                Check((fromGet with { Actions = projected.Actions }) == projected && fromGet.Actions.SequenceEqual(projected.Actions) &&
                    definition["bookSourceName"]!.GetValue<string>() == "合成" &&
                    definition["bookSourceUrl"]!.GetValue<string>() == projected.SourceUrl);
                var imported = await sources.ImportAsync(text: "[]");
                var fromImport = NovelSource.From(imported.GetProperty("sources")[0]);
                Check((fromImport with { Actions = projected.Actions }) == projected && sources.Sources.Single().Revision == projected.Revision &&
                    imported.GetProperty("failures").GetArrayLength() == 0);
            }
            foreach (string fields in new[] { """{"installationId":{}}""", """{"revision":{}}""", """{"actions":{}}""" })
            {
                var prior = sources.Sources;
                runtime.Project = (_, value) => SourceResponseFields(value, fields);
                try { await sources.RefreshAsync(); throw new InvalidOperationException("Required source material was defaulted."); }
                catch (JsonException) { }
                Check(ReferenceEquals(prior, sources.Sources), "A failed source decode published a partial list.");
            }
            runtime.Project = (_, value) => SourceResponseFields(value, IrregularSourceFields);
            var original = sources.Sources.Single();
            runtime.Listing = _ => throw new IOException("list unavailable");
            await sources.SaveAsync(original, "{}");
            Check(sources.Sources.Single() is { Revision: 2, Weight: 0, LastUpdateTime: 0, RespondTime: 180000, HasExplore: false });
            await sources.SaveAsync(sources.Sources.Single(), "{}");
            Check(sources.Sources.Single().Revision == 3 && runtime.Calls.Where(call => call.Method == "source.runtime.library.save")
                .Select(call => call.Payload.GetProperty("expectedRevision").GetInt64()).SequenceEqual([1L, 2L]));
            runtime.Listing = null;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken accepted = default;
            runtime.Read = (_, _, _, token) => { accepted = token; entered.TrySetResult(); return release.Task; };
            var pending = sources.InvokeAsync(sources.Sources.Single(), "search", new { keyword = "退休", page = 1 });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                runtime.Records = [runtime.Records.Single() with { Revision = 4, Enabled = false }];
                await sources.RefreshAsync();
                Check(accepted.IsCancellationRequested && sources.Sources.Single() is { Revision: 4, Enabled: false } && !pending.IsCompleted);
            }
            finally { release.TrySetResult(Json(new { books = Array.Empty<object>() })); }
            try { await pending; throw new InvalidOperationException("Optional fields prevented source retirement."); }
            catch (OperationCanceledException) { }
        }
        finally { await sources.PrepareShutdownAsync(); }
    }
    private static async Task SourceProjectionEditors()
    {
        var runtime = new Runtime { Project = (_, value) => SourceResponseFields(value, IrregularSourceFields) };
        var sources = new NovelSourceService(runtime);
        await using var store = new MemoryStore();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        var root = new Grid(); root.Children.Add(module.SettingsView);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = GeneralAppearanceTests.Window(root); NovelUi.Theme(window); dialogs.Attach(window); window.Show();
        try
        {
            await Until(() => Find<Button>(module.SettingsView).Any(button => AutomationProperties.GetName(button) == "编辑 书源a"));
            Find<Button>(module.SettingsView).Single(button => AutomationProperties.GetName(button) == "编辑 书源a")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => Find<NovelSourceEditor>(dialogs).Any());
            Check(runtime.Calls.Count(call => call.Method == "source.runtime.library.get") == 1);
            await Save(2); await Save(3);
            Check(runtime.Calls.Where(call => call.Method == "source.runtime.library.save")
                .Select(call => call.Payload.GetProperty("expectedRevision").GetInt64()).SequenceEqual([1L, 2L]));
            Cancel();
            await Until(() => !dialogs.IsOpen);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var create = Find<Button>(module.SettingsView).Single(button => AutomationProperties.GetName(button) == "更多")
                .ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "新建书源"));
            create.Items.OfType<MenuItem>().Single(item => Equals(item.Header, NovelSourceFields.Title("legado")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => Find<NovelSourceEditor>(dialogs).Any());
            var editor = Find<NovelSourceEditor>(dialogs).Single();
            Find<TextBox>(editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.bookSourceName").Text = "新源";
            Find<TextBox>(editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.bookSourceUrl").Text = Source("b").SourceUrl;
            runtime.Records = [Source("b") with { Revision = 4 }];
            await Save(4); await Save(5);
            var last = runtime.Calls.Last(call => call.Method == "source.runtime.library.save").Payload;
            Check(runtime.Calls.Count(call => call.Method == "source.runtime.library.import") == 1 &&
                last.GetProperty("installationId").GetString() == "b" && last.GetProperty("expectedRevision").GetInt64() == 4 &&
                sources.Sources.Single() is { InstallationId: "b", Revision: 5, RespondTime: 180000 });
            Cancel(); await Until(() => !dialogs.IsOpen);
        }
        finally { dialogs.Close(); window.Close(); }
        async Task Save(long revision)
        {
            await Until(() => Find<Button>(dialogs).Any(button => Equals(button.Content, "保存") && button.IsEnabled));
            Find<Button>(dialogs).Single(button => Equals(button.Content, "保存")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => sources.Sources.Single().Revision == revision && module.Notice == "书源已保存" &&
                Find<Button>(dialogs).Any(button => Equals(button.Content, "保存") && button.IsEnabled));
            Check(dialogs.IsOpen, "A save receipt closed the editor before its next revision could be used.");
        }
        void Cancel() => Find<Button>(dialogs).Single(button => Equals(button.Content, "取消"))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}
