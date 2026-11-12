using System.IO;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

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
    public Task<JsonElement> TransformAsync(string title, IReadOnlyList<NovelBlock> blocks, NovelBook book,
        NovelSource source, IReadOnlyList<NovelRule> rules, string conversion, CancellationToken token = default) =>
        InvokeAsync(new
        {
            operation = "transform", title, blocks, book = book.Json(),
            source = new { installationId = source.InstallationId, sourceUrl = source.SourceUrl,
                bookSourceUrl = source.SourceUrl, name = source.Name }, rules, conversion
        }, token);
    public async Task<bool> MatchUrlAsync(string pattern, string url, CancellationToken token = default) =>
        (await InvokeAsync(new { operation = "matchUrl", pattern, url }, token)).GetBoolean();
    public async Task ValidateAsync(NovelRule rule, CancellationToken token = default)
    {
        var result = await InvokeAsync(new { operation = "validateRule", rule }, token);
        if (!result.GetProperty("valid").GetBoolean()) throw new InvalidDataException("INVALID_RULE");
    }
    public Task PrepareShutdownAsync()
    {
        lock (lifecycle) return preparation ??= work.PauseAsync();
    }
    public void Resume()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (preparation is null) return;
            if (!preparation.IsCompletedSuccessfully) throw new InvalidOperationException("SHUTDOWN_NOT_DRAINED");
            work.Resume(); preparation = null;
        }
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
