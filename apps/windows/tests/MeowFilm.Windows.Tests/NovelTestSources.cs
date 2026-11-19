using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

internal static class NovelTestSources
{
    internal static NovelSourceService Create(IRuntimeClient runtime) =>
        new(runtime, runtime is NovelTests.Runtime probe ? probe.SourceStore : new NovelSourceMemoryStore());

    internal static NovelSourceDefinition Definition(NovelSource source) => new(source.InstallationId, source.Revision,
        source.Name, source.SourceUrl, source.Group, source.Order, source.Enabled, source.EnabledExplore, source.Protocol,
        JsonSerializer.Serialize(new
        {
            bookSourceName = source.Name, bookSourceUrl = source.SourceUrl, bookSourceGroup = source.Group,
            customOrder = source.Order, enabled = source.Enabled, enabledExplore = source.EnabledExplore,
            ruleSearch = new { }, searchUrl = source.Actions.Contains("search") ? "https://books.invalid/search" : "",
            exploreUrl = source.Actions.Contains("explore") ? "榜单::https://books.invalid/rank" : "",
            ruleBookInfo = new { name = "name" }, ruleToc = new { chapterList = "list" },
            ruleContent = new { content = "content", payAction = source.Actions.Contains("payAction") ? "true" : "" },
            loginUrl = source.Actions.Contains("login") ? "https://books.invalid/login" : ""
        }, NovelJson.Options));
}

internal sealed class NovelSourceMemoryStore : NativeTestDatabase
{
    internal TaskCompletionSource? Accepted, Hold;
    internal bool FailWrite;
    internal byte[]? Bytes
    {
        get
        {
            if (Project(NovelStorageRecords.Sources, "device") is not { } value) return null;
            foreach (var row in value["sources"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
                row["definition"] = row["definition"]!.ToJsonString();
            return JsonSerializer.SerializeToUtf8Bytes(value);
        }
        set
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(value!)!.AsObject();
            foreach (var row in root["sources"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
                if (row["definition"] is System.Text.Json.Nodes.JsonValue text)
                    row["definition"] = System.Text.Json.Nodes.JsonNode.Parse(text.GetValue<string>());
            Seed(NovelStorageRecords.Sources, root, "device");
        }
    }
    internal void Seed(IEnumerable<NovelSource> sources)
    {
        long revision = (Project(NovelStorageRecords.Sources, "device")?["revision"]?.GetValue<long>() ?? 0) + 1;
        Bytes = JsonSerializer.SerializeToUtf8Bytes(new NovelSourceConfigurationSnapshot(1, revision,
            sources.Select(NovelTestSources.Definition).ToArray()), NovelJson.Options);
    }
    public override Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null) => base.WriteAsync(db =>
    {
        Accepted?.TrySetResult(); Hold?.Task.GetAwaiter().GetResult();
        if (FailWrite) throw new DocumentStoreException(DocumentStoreError.Storage);
        return write(db);
    }, token, retainedBytes, commit);
}
