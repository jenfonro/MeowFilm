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

internal sealed class NovelSourceMemoryStore : IDocumentStore
{
    internal byte[]? Bytes;
    internal Func<CancellationToken, Task>? BeforeRead;
    internal TaskCompletionSource? Accepted, Hold;
    internal bool FailWrite;
    internal int Reads, Writes;
    internal void Seed(IEnumerable<NovelSource> sources)
    {
        using var previous = Bytes is null ? null : JsonDocument.Parse(Bytes);
        long revision = previous is null ? 1 : previous.RootElement.GetProperty("revision").GetInt64() + 1;
        Bytes = JsonSerializer.SerializeToUtf8Bytes(new NovelSourceConfigurationSnapshot(1, revision,
            sources.Select(NovelTestSources.Definition).ToArray()), NovelJson.Options);
    }
    public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
    {
        if (ns != NovelSourceConfiguration.DocumentNamespace) throw new InvalidOperationException("Unexpected source namespace");
        Reads++; if (BeforeRead is not null) await BeforeRead(token);
        token.ThrowIfCancellationRequested();
        return new(Bytes?.ToArray(), Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes));
    }
    public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var copy = data.ToArray(); Accepted?.TrySetResult();
        if (Hold is not null) await Hold.Task;
        if (FailWrite) throw new DocumentStoreException(DocumentStoreError.Storage);
        if (expected != (Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)))
            throw new DocumentStoreException(DocumentStoreError.Conflict);
        Bytes = copy; Writes++; return DocumentFingerprint.FromBytes(copy);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
