using System.IO;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

/// <summary>Owns a real test runtime and its client, without UI-owned business services.</summary>
internal sealed class NovelRuleRuntimeFixture : IRuntimeClient, IAsyncDisposable
{
    private readonly object gate = new();
    private Task<NodeRuntime>? starting;
    private NativeDatabase? database;
    private Task? disposal;
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (method != "source.runtime.rules.process") throw new InvalidOperationException("Unexpected rule consumer call: " + method);
        Task<NodeRuntime> startup;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            startup = starting ??= StartAsync();
        }
        var runtime = await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }
    private async Task<NodeRuntime> StartAsync()
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
            ?? throw new InvalidOperationException("A real eight-module Windows runtime bundle is required.");
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), AppIdentity.RuntimeId, AppIdentity.ScriptModules);
        string root = TestWorkspace.DataPath("novel-rule-rpc", Guid.NewGuid().ToString("N"));
        database = new NativeDatabase(root);
        await database.Ready.ConfigureAwait(false);
        return await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, AppIdentity.ScriptModules,
            root, settingsStore: new FilmRuntimeSettings(database)).ConfigureAwait(false);
    }
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Pure rules never upload source.");
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        try
        {
            if (starting is { } startup)
            {
                var runtime = await startup.ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { if (database is not null) await database.DisposeAsync().ConfigureAwait(false); }
    }
}
