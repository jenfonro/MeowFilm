using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private const string IrregularSourceFields = """{"weight":{},"lastUpdateTime":[],"respondTime":"unknown","hasExplore":17}""";
    private static NovelSourceDefinition SourceDefinitionFields(NovelSourceDefinition source, string fields)
    {
        var definition = JsonNode.Parse(source.Definition)!.AsObject();
        foreach (string key in new[] { "weight", "lastUpdateTime", "respondTime", "hasExplore" }) definition.Remove(key);
        foreach (var (key, material) in JsonNode.Parse(fields)!.AsObject()) definition[key] = material?.DeepClone();
        return source with { Definition = definition.ToJsonString() };
    }
    // Apply owns execution receipts, not the native definition's management projection.
    private static JsonElement SourceApplyFields(JsonElement value, string fields)
    {
        var response = JsonNode.Parse(value.GetRawText())!.AsObject();
        var overrides = JsonNode.Parse(fields)!.AsObject();
        foreach (var record in response["sources"]!.AsArray().OfType<JsonObject>())
        {
            foreach (string key in record.Select(pair => pair.Key).ToArray())
                if (key is not ("installationId" or "revision" or "actions" or "errorCode")) record.Remove(key);
            foreach (var (key, material) in overrides) record[key] = material?.DeepClone();
        }
        return JsonSerializer.SerializeToElement(response, NovelJson.Options);
    }
    private static void CheckSourceProjection(NovelSource actual, NovelSource expected) =>
        Check((actual with { Actions = expected.Actions }) == expected && actual.Actions.SequenceEqual(expected.Actions),
            "The source projection changed identity, revision, actions or native management facts.");
    private static void CheckSourceDefinition(string actual, string expected) =>
        Check(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(actual), JsonSerializer.Deserialize<JsonElement>(expected)),
            "An unrelated native operation rewrote raw definition material.");
    private static async Task SourceProjectionContract()
    {
        foreach (var (protocol, fields, weight, updated, response, explore) in new (string, string, double, double, double, bool)[]
        {
            ("legado", "{}", 0, 0, 180000, true),
            ("legado", IrregularSourceFields, 0, 0, 180000, true),
            ("legado", """{"weight":null,"lastUpdateTime":null,"respondTime":null,"hasExplore":null}""", 0, 0, 180000, true),
            ("legado", """{"weight":1e400,"lastUpdateTime":-1e400,"respondTime":1e400,"hasExplore":"true"}""", 0, 0, 180000, true),
            ("legado", """{"weight":-2.5,"lastUpdateTime":123.5,"respondTime":27.25,"hasExplore":false}""", -2.5, 123.5, 27.25, true),
            ("legado", """{"weight":"-2.5","lastUpdateTime":"123.5","respondTime":"27.25","hasExplore":false}""", -2.5, 123.5, 27.25, true),
            ("legado", """{"exploreUrl":"","hasExplore":true}""", 0, 0, 180000, false),
            ("qysg", """{"html":"<html></html>","exploreUrl":"","hasExplore":false}""", 0, 0, 180000, true)
        })
        {
            var runtime = new Runtime { Records = [Source() with { Actions = ["search"] }] };
            var definition = SourceDefinitionFields(NovelTestSources.Definition(Source()) with { Protocol = protocol }, fields);
            runtime.SourceStore.Bytes = JsonSerializer.SerializeToUtf8Bytes(new NovelSourceConfigurationSnapshot(1, 1, [definition]), NovelJson.Options);
            runtime.Project = (method, value) => method == "source.runtime.library.apply" ? SourceApplyFields(value, fields) : value;
            var sources = NovelTestSources.Create(runtime);
            try
            {
                await sources.RefreshAsync();
                string[] actions = protocol == "qysg"
                    ? ["image", "loginStatus", "search", "exploreKinds", "explore", "bookInfo", "toc", "content", "login"]
                    : explore ? ["image", "loginStatus", "search", "exploreKinds", "explore", "bookInfo", "toc", "content", "payAction", "login"]
                    : ["image", "loginStatus", "search", "bookInfo", "toc", "content", "payAction", "login"];
                var expected = Source() with { Protocol = protocol, Actions = actions, Weight = weight, LastUpdateTime = updated,
                    RespondTime = response, HasExplore = explore };
                var projected = sources.Sources.Single();
                var configured = expected with { Actions = [], Weight = 0, LastUpdateTime = 0, RespondTime = 180000, HasExplore = false };
                CheckSourceProjection(projected, configured);
                Check(!NovelCapability.CanExplore(projected) && runtime.Calls.Count == 0 && runtime.SourceStore.Reads == 1,
                    "cold management reads do not parse definitions or claim execution capabilities");
                var current = await sources.GetAsync(projected);
                CheckSourceProjection(NovelJson.Decode<NovelSource>(current.GetProperty("source")), configured);
                Check(current.GetProperty("definition").GetString() == definition.Definition && runtime.Calls.Count == 0);
                var result = await sources.InvokeAsync(projected, "search", new { keyword = "合成", page = 1 });
                Check(result.GetProperty("books").ValueKind == JsonValueKind.Array);
                CheckSourceProjection(sources.Sources.Single(), expected with { Actions = ["search"] });
                Check(!NovelCapability.CanExplore(sources.Sources.Single()), "Native discovery metadata replaced the applied actions.");
                var apply = runtime.Calls.Single(call => call.Method == "source.runtime.library.apply").Payload;
                Check(apply.GetProperty("revision").GetInt64() == 1 &&
                    apply.GetProperty("sources")[0].GetProperty("installationId").GetString() == projected.InstallationId &&
                    apply.GetProperty("sources")[0].GetProperty("revision").GetInt64() == projected.Revision &&
                    apply.GetProperty("sources")[0].GetProperty("definition").GetString() == definition.Definition);
                var invoke = runtime.Calls.Single(call => call.Method == "source.runtime.library.invoke").Payload;
                Check(invoke.GetProperty("installationId").GetString() == projected.InstallationId &&
                    invoke.GetProperty("revision").GetInt64() == projected.Revision && invoke.GetProperty("action").GetString() == "search");
                runtime.Listing = _ => throw new IOException("native reload unavailable");
                var imported = await sources.ImportAsync(text: "[" + definition.Definition + "]");
                Check(imported.GetProperty("sources").GetArrayLength() == 1 && imported.GetProperty("failures").GetArrayLength() == 0);
                CheckSourceProjection(NovelJson.Decode<NovelSource>(imported.GetProperty("sources")[0]), expected with { Revision = 2 });
                CheckSourceDefinition((await sources.GetAsync(sources.Sources.Single())).GetProperty("definition").GetString()!, definition.Definition);
                foreach (long revision in new[] { 3L, 4L })
                {
                    var receipt = await sources.SaveAsync(sources.Sources.Single(), definition.Definition);
                    CheckSourceProjection(NovelJson.Decode<NovelSource>(receipt.GetProperty("source")), expected with { Revision = revision });
                    Check(sources.Sources.Single().Revision == revision);
                    CheckSourceDefinition((await sources.GetAsync(sources.Sources.Single())).GetProperty("definition").GetString()!, definition.Definition);
                }
                var snapshot = JsonSerializer.Deserialize<NovelSourceConfigurationSnapshot>(runtime.SourceStore.Bytes!, NovelJson.Options)!;
                Check(snapshot is { Version: 1, Revision: 4 } && snapshot.Sources.Single().InstallationId == projected.InstallationId &&
                    snapshot.Sources.Single().Revision == 4 && runtime.SourceStore.Writes == 3 && runtime.SourceStore.Reads == 1);
                CheckSourceDefinition(snapshot.Sources.Single().Definition, definition.Definition);
                var committed = runtime.SourceStore.Bytes!.ToArray();
                try { await sources.SaveAsync(projected, definition.Definition); throw new InvalidOperationException("A stale source save passed."); }
                catch (BridgeException error) { Check(error.Code == "STALE_SOURCE"); }
                Check(runtime.SourceStore.Bytes!.SequenceEqual(committed) && runtime.SourceStore.Writes == 3);
                Check(runtime.Calls.All(call => call.Method is "source.runtime.library.apply" or "source.runtime.library.invoke" or "source.runtime.web.pending"));
            }
            finally { await sources.PrepareShutdownAsync(); }
        }
        foreach (var (field, material, errorType) in new (string, string?, Type)[]
        {
            ("installationId", null, typeof(KeyNotFoundException)), ("revision", null, typeof(KeyNotFoundException)),
            ("actions", null, typeof(KeyNotFoundException)),
            ("installationId", "{}", typeof(InvalidOperationException)), ("revision", "{}", typeof(InvalidOperationException)),
            ("actions", "{}", typeof(JsonException)), ("actions", "[17]", typeof(JsonException)),
            ("actions", "null", typeof(InvalidDataException)), ("errorCode", "{}", typeof(InvalidOperationException)),
            ("installationId", "\"other\"", typeof(BridgeException)), ("revision", "2", typeof(BridgeException))
        })
        {
            var runtime = new Runtime();
            runtime.Project = (method, value) =>
            {
                if (method != "source.runtime.library.apply") return value;
                var response = JsonNode.Parse(SourceApplyFields(value, "{}").GetRawText())!;
                var record = response["sources"]![0]!.AsObject();
                record.Remove(field);
                if (material is not null) record[field] = JsonNode.Parse(material);
                return JsonSerializer.SerializeToElement(response, NovelJson.Options);
            };
            var sources = NovelTestSources.Create(runtime);
            try
            {
                await sources.RefreshAsync();
                var prior = sources.Sources;
                var committed = runtime.SourceStore.Bytes!.ToArray();
                bool rejected = false;
                try { await sources.InvokeAsync(prior.Single(), "search", new { keyword = "拒绝", page = 1 }); }
                catch (Exception error) when (error.GetType() == errorType)
                {
                    rejected = true;
                    if (error is BridgeException bridge) Check(bridge.Code == "INVALID_RESPONSE");
                    if (error is InvalidDataException) Check(error.Message == "INVALID_RESPONSE");
                }
                Check(rejected, "Invalid apply material admitted an action: " + field);
                Check(ReferenceEquals(prior, sources.Sources) && runtime.SourceStore.Bytes!.SequenceEqual(committed) &&
                    runtime.SourceStore.Writes == 0 && runtime.Calls.Count(call => call.Method == "source.runtime.library.apply") == 1 &&
                    !runtime.Calls.Any(call => call.Method == "source.runtime.library.invoke"),
                    "A rejected apply changed native configuration or reached the action.");
            }
            finally { await sources.PrepareShutdownAsync(); }
        }
        var retiringRuntime = new Runtime();
        var raw = SourceDefinitionFields(NovelTestSources.Definition(Source()), IrregularSourceFields);
        retiringRuntime.SourceStore.Bytes = JsonSerializer.SerializeToUtf8Bytes(new NovelSourceConfigurationSnapshot(1, 1, [raw]), NovelJson.Options);
        var retiring = NovelTestSources.Create(retiringRuntime);
        try
        {
            await retiring.RefreshAsync();
            var committed = retiringRuntime.SourceStore.Bytes!.ToArray();
            retiringRuntime.Project = (method, value) => method == "source.runtime.library.apply"
                ? SourceApplyFields(value, """{"errorCode":"SOURCE_INTEGRITY"}""") : value;
            try { await retiring.InvokeAsync(retiring.Sources.Single(), "search", new { keyword = "错误", page = 1 }); throw new InvalidOperationException("An apply error admitted an action."); }
            catch (BridgeException error) { Check(error.Code == "SOURCE_INTEGRITY"); }
            Check(retiring.Sources.Single().ErrorCode == "SOURCE_INTEGRITY" && retiringRuntime.SourceStore.Bytes!.SequenceEqual(committed) &&
                !retiringRuntime.Calls.Any(call => call.Method == "source.runtime.library.invoke"));
            retiringRuntime.Project = (method, value) => method == "source.runtime.library.apply" ? SourceApplyFields(value, "{}") : value;
            foreach (bool enabled in new[] { true, false })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken accepted = default;
                retiringRuntime.Read = (_, _, _, token) => { accepted = token; entered.TrySetResult(); return release.Task; };
                var original = retiring.Sources.Single();
                var pending = retiring.InvokeAsync(original, "search", new { keyword = "退休", page = 1 });
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await retiring.UpdateAsync(original, new { group = "更新组", enabled });
                    Check(accepted.IsCancellationRequested && !pending.IsCompleted && !retiring.IsCurrent(original));
                    var current = retiring.Sources.Single();
                    Check(current.Revision == original.Revision + 1 && current.Enabled == enabled && current.Group == "更新组" && current.ErrorCode is null);
                    var expected = JsonNode.Parse(raw.Definition)!; expected["bookSourceGroup"] = "更新组"; expected["enabled"] = enabled;
                    CheckSourceDefinition((await retiring.GetAsync(current)).GetProperty("definition").GetString()!, expected.ToJsonString());
                }
                finally { release.TrySetResult(Json(new { books = Array.Empty<object>() })); }
                try { await pending; throw new InvalidOperationException("A retired action published its late result."); }
                catch (OperationCanceledException) { }
            }
        }
        finally { await retiring.PrepareShutdownAsync(); }
    }
    private static async Task SourceProjectionEditors()
    {
        var runtime = new Runtime();
        var definition = SourceDefinitionFields(NovelTestSources.Definition(Source()), IrregularSourceFields);
        runtime.SourceStore.Bytes = JsonSerializer.SerializeToUtf8Bytes(new NovelSourceConfigurationSnapshot(1, 1, [definition]), NovelJson.Options);
        runtime.Project = (method, value) => method == "source.runtime.library.apply" ? SourceApplyFields(value, IrregularSourceFields) : value;
        var sources = NovelTestSources.Create(runtime);
        await using var store = new MemoryStore();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(runtime));
        await module.InitializeAsync(default);
        int reads = 0, notices = 0;
        module.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(module.Notice) && module.Notice == "书源已保存") notices++; };
        var settings = (NovelSettingsView)module.SettingsView;
        var root = new Grid(); root.Children.Add(module.SettingsView);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = GeneralAppearanceTests.Window(root); NovelUi.Theme(DesktopTestUi.WindowRoot(window)); dialogs.Attach(window); window.Show();
        try
        {
            await Until(() => Find<Button>(module.SettingsView).Any(button => AutomationProperties.GetName(button) == "编辑 书源a"));
            await settings.DrainAsync(); settings.Resume();
            await DesktopTestUi.DrainAsync();
            reads = runtime.SourceStore.Reads;
            runtime.Listing = _ => throw new IOException("native reload unavailable");
            DesktopTestUi.Invoke(Find<Button>(module.SettingsView).Single(button => AutomationProperties.GetName(button) == "编辑 书源a"));
            await Until(() => Find<NovelSourceEditor>(dialogs).Any(candidate => candidate.IsLoaded));
            var editor = Find<NovelSourceEditor>(dialogs).Single();
            CheckSourceDefinition(editor.Draft.Definition(), definition.Definition);
            string originalId = sources.Sources.Single().InstallationId;
            await Save(editor, originalId, Source().SourceUrl, "已编辑一", 2, 2, 1, definition.Definition);
            await Save(editor, originalId, Source().SourceUrl, "已编辑二", 3, 3, 2, definition.Definition);
            Cancel();
            await Until(() => !dialogs.IsOpen);
            await settings.DrainAsync(); settings.Resume();
            await DesktopTestUi.DrainAsync();
            var create = Find<Button>(module.SettingsView).Single(button => AutomationProperties.GetName(button) == "添加书源")
                .ContextFlyout as MenuFlyout ?? throw new InvalidOperationException("Source menu absent");
            var item = (MenuFlyoutItem)create.Items.Single(candidate => candidate is MenuFlyoutItem { Text: "新建书源" });
            Check(create.Items.OfType<MenuFlyoutSubItem>().Count() == 0);
            create.ShowAt(Find<Button>(module.SettingsView).Single(button => AutomationProperties.GetName(button) == "添加书源"));
            await DesktopTestUi.DrainAsync(); MusicTestUi.Invoke(item);
            await Until(() => Find<NovelSourceEditor>(dialogs).Any(candidate => candidate.IsLoaded));
            editor = Find<NovelSourceEditor>(dialogs).Single();
            Field(editor, "bookSourceName").Text = "新源";
            Field(editor, "bookSourceUrl").Text = Source("b").SourceUrl;
            await Until(() => editor.Draft.Text(new("bookSourceName", "书源名称")) == "新源" &&
                editor.Draft.Text(new("bookSourceUrl", "书源地址")) == Source("b").SourceUrl);
            string newDefinition = editor.Draft.Definition();
            await Save(editor, null, Source("b").SourceUrl, "新源一", 1, 4, 3, newDefinition);
            var added = sources.Sources.Single(source => source.SourceUrl == Source("b").SourceUrl);
            Check(added.InstallationId.Length > 0 && added.InstallationId != originalId && !added.HasExplore);
            await Save(editor, added.InstallationId, added.SourceUrl, "新源二", 2, 5, 4, newDefinition);
            Check(sources.Sources.Count == 2 && sources.Sources.Single(source => source.InstallationId == originalId) is { Revision: 3, Name: "已编辑二" });
            Cancel(); await Until(() => !dialogs.IsOpen);
            await module.PrepareShutdownAsync();
            Check(notices == 4 && runtime.SourceStore.Writes == 4 && runtime.SourceStore.Reads == reads &&
                runtime.Calls.All(call => call.Method == "source.runtime.library.apply"));
        }
        finally { dialogs.Close(); window.Close(); }
        static TextBox Field(NovelSourceEditor editor, string key) => Find<TextBox>(editor).Single(input =>
            AutomationProperties.GetAutomationId(input) == "novel.source.field." + key);
        async Task Save(NovelSourceEditor editor, string? id, string url, string name, long revision, long configurationRevision,
            int writes, string original)
        {
            Field(editor, "bookSourceName").Text = name;
            await Until(() => editor.Draft.Text(new("bookSourceName", "书源名称")) == name &&
                Find<Button>(dialogs).Any(button => Equals(button.Content, "保存") && button.IsEnabled));
            DesktopTestUi.Invoke(Find<Button>(dialogs).Single(button => Equals(button.Content, "保存")));
            await Until(() => sources.Sources.Any(source => source.SourceUrl == url && source.Revision == revision) && notices == writes &&
                !editor.Draft.HasInput &&
                Find<Button>(dialogs).Any(button => Equals(button.Content, "保存") && button.IsEnabled));
            Check(dialogs.IsOpen && ReferenceEquals(Find<NovelSourceEditor>(dialogs).Single(), editor),
                "A save receipt closed or replaced the editor before its next revision could be used.");
            var source = sources.Sources.Single(source => source.SourceUrl == url);
            Check((id is null || source.InstallationId == id) && source.Name == name && source.Weight == 0 &&
                source.LastUpdateTime == 0 && source.RespondTime == 180000 && module.Notice == "书源已保存");
            var snapshot = JsonSerializer.Deserialize<NovelSourceConfigurationSnapshot>(runtime.SourceStore.Bytes!, NovelJson.Options)!;
            var saved = snapshot.Sources.Single(item => item.InstallationId == source.InstallationId);
            Check(snapshot.Version == 1 && snapshot.Revision == configurationRevision && saved.Revision == revision &&
                saved.Name == name && saved.SourceUrl == url && runtime.SourceStore.Writes == writes);
            Check(source.HasExplore == (url == Source().SourceUrl) && source.Protocol == "legado" &&
                source.Enabled && source.EnabledExplore && source.ErrorCode is null);
            var expected = JsonNode.Parse(original)!; expected["bookSourceName"] = name;
            expected["bookSourceGroup"] ??= ""; expected["customOrder"] ??= 1;
            CheckSourceDefinition(saved.Definition, expected.ToJsonString());
            var current = await sources.GetAsync(source);
            CheckSourceDefinition(current.GetProperty("definition").GetString()!, saved.Definition);
            Check(NovelJson.Decode<NovelSource>(current.GetProperty("source")).Revision == revision);
        }
        void Cancel() => DesktopTestUi.Invoke(Find<Button>(dialogs).Single(button => Equals(button.Content, "取消")));
    }
}
