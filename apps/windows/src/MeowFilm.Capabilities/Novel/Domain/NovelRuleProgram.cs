using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public sealed record NovelSourceCandidateMatch(
    string InstallationId,
    string BookUrl,
    string IdentityName,
    string IdentityAuthor,
    string IdentityKey,
    bool SameBook,
    bool SameSource,
    bool NeedsConfirmation);

public sealed record NovelSourceCandidatesResult(
    string TargetName,
    string TargetAuthor,
    string TargetKey,
    NovelSourceCandidateMatch[] Candidates);

/// <summary>Borrowed source RPCs; native state remains local and Node owns pure rule execution.</summary>
public sealed class NovelRuleProgram(IRuntimeClient runtime) : IAsyncDisposable
{
    private readonly object lifecycle = new();
    private readonly NovelWork work = new();
    private Task? preparation, disposal;
    private bool closed;
    public Task<JsonElement> InvokeAsync(object request, CancellationToken token = default)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var snapshot = NovelJson.Value(request);
            return work.Run(async ct =>
            {
                var envelope = await runtime.CallAsync("source.runtime.rules.process", new { request = snapshot }, ct);
                ct.ThrowIfCancellationRequested();
                if (!envelope.GetProperty("ok").GetBoolean())
                    throw new BridgeException(envelope.GetProperty("code").GetString()!);
                return envelope.GetProperty("value").Clone();
            }, token);
        }
    }
    public async Task<string> IdentityAsync(NovelBook book, CancellationToken token = default) =>
        NovelJson.Text(await InvokeAsync(new { operation = "identity", book = book.Json() }, token), "key");
    public async Task<NovelSourceCandidatesResult> SourceCandidatesAsync(
        IEnumerable<NovelBook> books,
        NovelBook target,
        CancellationToken token = default)
    {
        var rawBooks = books.Select(b => b.Json()).ToArray();
        var value = await InvokeAsync(new { operation = "sourceCandidates", books = rawBooks, target = target.Json() }, token);
        var targetIdentity = value.GetProperty("targetIdentity");
        var matches = new List<NovelSourceCandidateMatch>();
        foreach (var item in value.GetProperty("candidates").EnumerateArray())
        {
            var book = item.GetProperty("book");
            var identity = item.GetProperty("identity");
            matches.Add(new NovelSourceCandidateMatch(
                NovelJson.Text(book, "installationId"),
                NovelJson.Text(book, "bookUrl"),
                NovelJson.Text(identity, "name"),
                NovelJson.Text(identity, "author"),
                NovelJson.Text(identity, "key"),
                item.GetProperty("sameBook").GetBoolean(),
                item.GetProperty("sameSource").GetBoolean(),
                item.GetProperty("needsConfirmation").GetBoolean()));
        }
        return new NovelSourceCandidatesResult(
            NovelJson.Text(targetIdentity, "name"),
            NovelJson.Text(targetIdentity, "author"),
            NovelJson.Text(targetIdentity, "key"),
            matches.ToArray());
    }
    public async Task<NovelChapter[]> ConvertChapterNamesAsync(
        IReadOnlyList<NovelChapter> chapters,
        string conversion,
        CancellationToken token = default)
    {
        var labels = chapters.ToArray();
        if (conversion == "none" || labels.Length == 0) return labels;
        var result = await InvokeAsync(new
        {
            operation = "transform",
            title = "",
            blocks = labels.Select(c => new NovelBlock("text", c.Name)).ToArray(),
            rules = Array.Empty<NovelRule>(),
            conversion
        }, token);
        var names = result.GetProperty("blocks");
        for (int i = 0; i < labels.Length; i++)
            labels[i] = labels[i] with { Name = NovelJson.Text(names[i], "text") };
        return labels;
    }
    public Task<JsonElement> TransformAsync(string title, IReadOnlyList<NovelBlock> blocks, NovelBook book,
        NovelSource source, IReadOnlyList<NovelRule> rules, string conversion, CancellationToken token = default) =>
        InvokeAsync(new
        {
            operation = "transform", title, blocks, book = book.Json(),
            source = new { installationId = source.InstallationId, sourceUrl = source.SourceUrl,
                bookSourceUrl = source.SourceUrl, name = source.Name },
            rules = rules.Select(rule => new
            {
                pattern = rule.Pattern, replacement = rule.Replacement, isRegex = rule.IsRegex,
                isEnabled = rule.Enabled, scopeTitle = rule.ScopeTitle, scopeContent = rule.ScopeContent,
                scope = rule.Scope, excludeScope = rule.ExcludeScope, order = rule.Order,
                timeoutMillisecond = rule.TimeoutMillisecond
            }).ToArray(),
            conversion
        }, token);
    public async Task<bool> MatchUrlAsync(string pattern, string url, CancellationToken token = default) =>
        (await InvokeAsync(new { operation = "matchUrl", pattern, url }, token)).GetBoolean();
    public Task PrepareShutdownAsync()
    {
        lock (lifecycle) return preparation ??= work.PauseAsync();
    }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            closed = true;
            return new(disposal ??= PrepareShutdownAsync());
        }
    }
}
