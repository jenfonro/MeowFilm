using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class NovelConfigurationTests
{
    private const string Definition = """{"bookSourceName":"离线书源","bookSourceUrl":"https://books.invalid","ruleSearch":{},"searchUrl":"https://books.invalid/search","exploreUrl":"月票榜::https://books.invalid/rank"}""";
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel configuration: offline import edit metadata export and reload never require the runtime", Offline);
        yield return new("novel configuration: definition beyond sixteen MiB imports exports and reopens completely", LargeDefinition);
        yield return new("novel configuration: atomic failure and conflicting owner preserve the committed source", Failure);
        yield return new("novel configuration: accepted cancellation drains the real write and publishes its receipt", Receipt);
        yield return new("novel configuration: slow application cannot block saving and cannot publish an older snapshot", Latest);
        yield return new("novel configuration: a fresh runtime epoch receives the latest native snapshot before invocation", Recovery);
        yield return new("novel configuration: a new draft persists offline without replacing an existing source", Draft);
        yield return new("novel configuration: typed definition extensions and sibling sources survive independent edits", RawInputs);
        yield return new("novel configuration: cold search applies before selecting actions and cancellation drains preparation", SearchPreparation);
        yield return new("novel configuration: malformed current documents fail without publishing or overwriting data", Format);
    }
    private static void Check(bool value, string message = "Native configuration assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Offline()
    {
        var runtime = new Probe { Offline = true }; var store = new NovelSourceMemoryStore();
        var service = new NovelSourceService(runtime, store);
        try
        {
            await service.RefreshAsync(); Check(service.Sources.Count == 0 && runtime.Calls.Count == 0);
            var preview = await service.PreviewImportAsync(text: Definition);
            Check(preview.GetProperty("items").GetArrayLength() == 1 && runtime.Calls.Count == 0);
            var imported = await service.ImportAsync(text: Definition);
            Check(imported.GetProperty("sources").GetArrayLength() == 1 && store.Writes == 1);
            var source = service.Sources.Single();
            using var definition = JsonDocument.Parse((await service.GetAsync(source)).GetProperty("definition").GetString()!);
            Check(definition.RootElement.GetProperty("bookSourceName").GetString() == "离线书源");
            await service.UpdateAsync(source, new { group = "离线组", enabled = false });
            source = service.Sources.Single();
            Check(source.Group == "离线组" && !source.Enabled && source.Revision == 2);
            var saved = await service.SaveAsync(source, Definition.Replace("离线书源", "已编辑"));
            source = service.Sources.Single(); Check(source.Name == "已编辑" && source.Revision == 3);
            Check(saved.GetProperty("source").GetProperty("revision").GetInt64() == 3);
            var exported = await service.ExportAsync([source.InstallationId]);
            using var output = JsonDocument.Parse(exported.GetProperty("text").GetString()!);
            Check(output.RootElement[0].GetProperty("bookSourceName").GetString() == "已编辑");
            var reopened = new NovelSourceService(runtime, store); await reopened.RefreshAsync();
            Check(reopened.Sources.Single().Revision == source.Revision && reopened.Sources.Single().Name == "已编辑");
            await reopened.PrepareShutdownAsync();
            Check(runtime.Calls.All(call => call == "source.runtime.library.apply"));
            await service.RemoveAsync(source); Check(service.Sources.Count == 0);
        }
        finally { await service.PrepareShutdownAsync(); }
    }
    private static async Task LargeDefinition()
    {
        var definition = System.Text.Json.Nodes.JsonNode.Parse(Definition)!.AsObject();
        string text = new('x', 16 * 1024 * 1024 + 1);
        definition["bookSourceComment"] = text;
        var runtime = new Probe { Offline = true }; var store = new NovelSourceMemoryStore();
        var service = new NovelSourceService(runtime, store);
        try
        {
            string input = definition.ToJsonString();
            var preview = await service.PreviewImportAsync(text: input);
            Check(preview.GetProperty("items").GetArrayLength() == 1);
            await service.ImportAsync(text: input);
            Check(store.Writes == 1 && store.Bytes!.Length > 16 * 1024 * 1024);
            var reopened = new NovelSourceService(runtime, store);
            try
            {
                await reopened.RefreshAsync();
                var exported = await reopened.ExportAsync([reopened.Sources.Single().InstallationId]);
                using var output = JsonDocument.Parse(exported.GetProperty("text").GetString()!);
                Check(output.RootElement[0].GetProperty("bookSourceComment").GetString() == text);
            }
            finally { await reopened.PrepareShutdownAsync(); }
        }
        finally { await service.PrepareShutdownAsync(); }
    }

    private static async Task Failure()
    {
        var runtime = new Probe { Offline = true }; var store = new NovelSourceMemoryStore();
        var service = new NovelSourceService(runtime, store);
        try
        {
            await service.ImportAsync(text: Definition); var source = service.Sources.Single(); var bytes = store.Bytes!.ToArray();
            store.FailWrite = true;
            try { await service.SaveAsync(source, Definition.Replace("离线书源", "失败草稿")); throw new InvalidOperationException("Write failure passed"); }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Storage); }
            Check(store.Bytes!.SequenceEqual(bytes) && service.Sources.Single().Name == "离线书源");
            store.FailWrite = false;
            var other = new NovelSourceService(runtime, store); await other.RefreshAsync();
            await other.SaveAsync(other.Sources.Single(), Definition.Replace("离线书源", "另一写入者"));
            try { await service.SaveAsync(source, Definition); throw new InvalidOperationException("CAS conflict passed"); }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Conflict); }
            Check(service.Sources.Single().Revision == 1);
            await service.RefreshAsync(); Check(service.Sources.Single().Name == "另一写入者");
            await other.PrepareShutdownAsync();
        }
        finally { await service.PrepareShutdownAsync(); }
    }
    private static async Task Receipt()
    {
        var runtime = new Probe { Offline = true };
        var store = new NovelSourceMemoryStore { Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new NovelSourceService(runtime, store);
        using var cancellation = new CancellationTokenSource();
        var write = service.ImportAsync(text: Definition, token: cancellation.Token);
        await store.Accepted.Task; cancellation.Cancel();
        var drain = service.PrepareShutdownAsync(); Check(!drain.IsCompleted && service.Sources.Count == 0);
        store.Hold.SetResult(); await write; await drain;
        Check(store.Writes == 1 && service.Sources.Single().Name == "离线书源");
    }
    private static async Task Latest()
    {
        var runtime = new Probe { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var store = new NovelSourceMemoryStore(); var service = new NovelSourceService(runtime, store);
        try
        {
            await service.ImportAsync(text: Definition); await runtime.Started.Task;
            var save = service.SaveAsync(service.Sources.Single(), Definition.Replace("离线书源", "最新书源"));
            await save.WaitAsync(TimeSpan.FromSeconds(2));
            Check(service.Sources.Single().Name == "最新书源" && runtime.Applied.Count == 0);
            runtime.Hold.SetResult(); runtime.Hold = null;
            await service.InvokeAsync(service.Sources.Single(), "search", new { keyword = "书", page = 1 });
            Check(runtime.Applied.Last().Sources.Single().Name == "最新书源" &&
                service.Sources.Single().Name == "最新书源");
        }
        finally { runtime.Hold?.TrySetResult(); await service.PrepareShutdownAsync(); }
    }
    private static async Task Recovery()
    {
        var runtime = new Epochs(); var store = new NovelSourceMemoryStore();
        var service = new NovelSourceService(runtime, store);
        try
        {
            await service.ImportAsync(text: Definition);
            await service.InvokeAsync(service.Sources.Single(), "search", new { keyword = "书", page = 1 });
            var first = runtime.Current; runtime.Renew();
            await service.InvokeAsync(service.Sources.Single(), "search", new { keyword = "书", page = 1 });
            Check(first.Applied.Count > 0 && runtime.Current.Applied.Single().Sources.Single().Definition.Contains("books.invalid"));
        }
        finally { await service.PrepareShutdownAsync(); runtime.Dispose(); }
    }
    private static async Task Draft()
    {
        var runtime = new Probe { Offline = true }; var store = new NovelSourceMemoryStore();
        var service = new NovelSourceService(runtime, store);
        try
        {
            await service.SaveDraftAsync("legado", Definition);
            var first = service.Sources.Single();
            var result = await service.SaveDraftAsync("qysg",
                """{"bookSourceName":"另一书源","bookSourceUrl":"https://other.invalid","html":"<script>function invalid({</script>"}""");
            Check(result.GetProperty("source").GetProperty("protocol").GetString() == "qysg" && store.Writes == 2);
            Check(service.Sources.Count == 2 && service.Sources.Any(s => s.InstallationId == first.InstallationId && s.Revision == first.Revision));
            var before = store.Bytes!.ToArray();
            try
            {
                await service.SaveAsync(first, """{"bookSourceName":"覆盖","bookSourceUrl":"https://books.invalid","html":"<html></html>"}""");
                throw new InvalidOperationException("Protocol replacement passed");
            }
            catch (BridgeException error) { Check(error.Code == "SOURCE_INTEGRITY"); }
            Check(store.Bytes!.SequenceEqual(before));
        }
        finally { await service.PrepareShutdownAsync(); }
    }
    private static async Task RawInputs()
    {
        var runtime = new Probe { Offline = true };
        await using var store = new NovelSourceMemoryStore();
        var configuration = new NovelSourceConfiguration(store);
        string definition = Definition.Insert(1, "\"preciseId\":9007199254740993,\"future\":1e400,\"unused\":\"合成🎬扩展\",\"é\":1,\"e\\u0301\":2,");
        await configuration.ImportAsync(definition, null, null, default);
        await configuration.ImportAsync(Definition.Replace("books.invalid", "sibling.invalid"), null, null, default);
        var input = store.Project(NovelStorageRecords.Sources, "device")!;
        string sibling = input["sources"]![1]!.ToJsonString();
        var service = new NovelSourceService(runtime, store);
        try
        {
            await service.RefreshAsync();
            Check(service.Sources.Count == 2, "the native list restores independently stored source records");
            var selected = service.Sources.Single(source => source.SourceUrl == "https://books.invalid");
            await service.UpdateAsync(selected, new { name = "已修改" });
            using var saved = JsonDocument.Parse(store.Bytes!);
            var result = saved.RootElement;
            Check(result.GetProperty("sources")[1].GetRawText() == sibling,
                "editing one source does not rewrite its sibling's definition or management fields");
            Check(service.Sources.Single(source => source.InstallationId == selected.InstallationId).Revision == 2,
                "the selected source still advances its revocation revision");
            var savedDefinition = result.GetProperty("sources")[0].GetProperty("definition");
            Check(savedDefinition.GetProperty("preciseId").GetRawText() == "9007199254740993" &&
                savedDefinition.GetProperty("future").GetRawText() == "1e400" &&
                savedDefinition.GetProperty("unused").GetString() == "合成🎬扩展" &&
                savedDefinition.GetProperty("é").GetInt32() == 1 && savedDefinition.GetProperty("e\u0301").GetInt32() == 2,
                "typed definition nodes retain exact numeric literals and distinct Unicode property identities");
            var exported = await service.ExportAsync([selected.InstallationId]);
            using var external = JsonDocument.Parse(exported.GetProperty("text").GetString()!);
            Check(external.RootElement[0].GetProperty("future").GetRawText() == "1e400",
                "explicit user JSON export retains third-party extensions");
        }
        finally { await service.PrepareShutdownAsync(); }
    }

    private static async Task SearchPreparation()
    {
        foreach (bool shutdown in new[] { false, true })
        {
            var store = new NovelSourceMemoryStore();
            await new NovelSourceConfiguration(store).ImportAsync(Definition, null, null, default);
            var runtime = new Probe { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously), DrainApply = true };
            var service = new NovelSourceService(runtime, store);
            await using var rules = new NovelRuleProgram(runtime);
            var search = new NovelSearch(service, rules);
            service.Changed += (_, _) => search.SourcesChanged();
            try
            {
                var pending = search.StartAsync("旧词");
                await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(search.Loading && search.States.Count == 0 && service.Sources.Single().Actions.Length == 0);
                Task drain = Task.CompletedTask;
                if (shutdown) drain = search.PrepareShutdownAsync();
                else search.Cancel();
                Check(runtime.ApplicationToken.IsCancellationRequested && !pending.IsCompleted &&
                    (!shutdown || !drain.IsCompleted), "preparation cannot outlive cancellation or shutdown admission");
                runtime.Hold.SetResult();
                try { await pending; throw new InvalidOperationException("Cancelled preparation published"); }
                catch (OperationCanceledException) { }
                await drain;
                Check(search.States.Count == 0 && !runtime.Calls.Contains("source.runtime.library.invoke"));
                if (!shutdown)
                {
                    await search.StartAsync("新词");
                    Check(search.Query == "新词" && search.States.Single().Status == "无结果" &&
                        runtime.Calls.IndexOf("source.runtime.library.apply") < runtime.Calls.IndexOf("source.runtime.library.invoke"));
                }
            }
            finally
            {
                runtime.Hold.TrySetResult();
                await search.PrepareShutdownAsync(); await service.PrepareShutdownAsync();
            }
        }
    }

    private static async Task Format()
    {
        foreach (string sql in new[]
        {
            "UPDATE windows_novel_sources SET version=2",
            "UPDATE windows_novel_sources SET revision=0",
            "UPDATE windows_novel_sources SET shape_mask=shape_mask & ~4",
            "UPDATE windows_novel_sources_sources SET revision=0"
        })
        {
            var runtime = new Probe { Offline = true };
            await using var store = new NovelSourceMemoryStore();
            await new NovelSourceConfiguration(store).ImportAsync(Definition, null, null, default);
            await store.Inner.WriteAsync(db => db.Execute(sql));
            var service = new NovelSourceService(runtime, store); var before = store.Bytes!.ToArray();
            var revision = await store.RevisionAsync(NovelSourceConfiguration.DocumentNamespace);
            try
            {
                try { await service.RefreshAsync(); throw new InvalidOperationException("Corrupt document loaded"); }
                catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
                try { await service.ImportAsync(text: Definition); throw new InvalidOperationException("Corrupt document overwritten"); }
                catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt); }
                Check(service.Sources.Count == 0 && (await store.RevisionAsync(NovelSourceConfiguration.DocumentNamespace)) == revision &&
                    store.Bytes!.SequenceEqual(before) && runtime.Calls.Count == 0);
            }
            finally { await service.PrepareShutdownAsync(); }
        }
    }
    private sealed class Probe : IRuntimeClient
    {
        internal bool Offline;
        internal bool DrainApply;
        internal CancellationToken ApplicationToken;
        internal TaskCompletionSource? Hold;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Calls = [];
        internal readonly List<NovelSourceConfigurationSnapshot> Applied = [];
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            Calls.Add(method); if (Offline) throw new BridgeException("CLOSED");
            var input = BridgeCodec.Value(parameters);
            object result;
            if (method == "source.runtime.library.apply")
            {
                Check(input.GetProperty("sources").EnumerateArray().All(source =>
                    source.EnumerateObject().Select(field => field.Name).Order().SequenceEqual(new[]
                    { "definition", "enabled", "enabledExplore", "group", "installationId", "name", "order", "protocol", "revision", "sourceUrl" })),
                    "The native snapshot must use the exact runtime wire field names.");
                ApplicationToken = token; Started.TrySetResult();
                if (Hold is { } hold)
                {
                    if (DrainApply) await hold.Task;
                    else await hold.Task.WaitAsync(token);
                }
                var sources = NovelJson.Decode<NovelSourceDefinition[]>(input.GetProperty("sources"));
                var snapshot = new NovelSourceConfigurationSnapshot(1, input.GetProperty("revision").GetInt64(), sources);
                Applied.Add(snapshot); result = new { revision = snapshot.Revision, sources = sources.Select(item => item.SourceFromDefinition()).ToArray() };
            }
            else result = method == "source.runtime.web.pending" ? new { requests = Array.Empty<object>() } : (object)new { books = Array.Empty<object>() };
            return NovelJson.Value(new { ok = true, contract = "source.runtime.v2", value = result });
        }
        public Task<JsonElement> UploadSourceAsync(string module, string id, long revision, ReadOnlyMemory<byte> source,
            string? basename = null, CancellationToken token = default) => throw new InvalidOperationException();
    }
    private sealed class Epochs : IRuntimeClient, IRuntimeConnectionSource, IDisposable
    {
        internal Probe Current = new();
        private CancellationTokenSource revoked = new();
        internal void Renew() { revoked.Cancel(); revoked.Dispose(); revoked = new(); Current = new(); }
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken token = default) =>
            Task.FromResult(new RuntimeConnection(Current, new Uri("http://127.0.0.1:1"), revoked.Token));
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default) =>
            Current.CallAsync(method, parameters, token);
        public Task<JsonElement> UploadSourceAsync(string module, string id, long revision, ReadOnlyMemory<byte> source,
            string? basename = null, CancellationToken token = default) => throw new InvalidOperationException();
        public void Dispose() { revoked.Cancel(); revoked.Dispose(); }
    }
}
