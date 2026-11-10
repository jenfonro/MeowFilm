using System.Diagnostics;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

// Real private-pipe/child epoch tests, selected separately from pure capture fakes.
public static class RuntimeConnectionTests
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Run the selected group through scripts/windows/test.ps1.");
    private static async Task<NodeRuntime> StartAsync(CancellationToken startup = default, CancellationToken owner = default)
    {
        var modules = RuntimeHttpClientTests.Modules;
        var bundle = RuntimeBundle.Verify(Path.Combine(Required("MEOWFILM_TEST_RUNTIME_ROOT"), "aggregate"), "aggregate", modules);
        string root = Path.Combine(Required("MEOWFILM_TEST_DATA_ROOT"), Guid.NewGuid().ToString("N"), "script-runtime");
        return await NodeRuntime.StartAsync(Required("MEOWFILM_TEST_NODE"), bundle, modules, root, startup, owner);
    }
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("native-runtime-capture: startup cancellation is not epoch revocation and owner cancellation revokes the fixed triple", async () =>
        {
            using var startup = new CancellationTokenSource();
            using var owner = new CancellationTokenSource();
            var runtime = await StartAsync(startup.Token, owner.Token);
            try
            {
                var connection = await runtime.CaptureConnectionAsync();
                startup.Cancel();
                Check(ReferenceEquals(connection.Client, runtime) && connection.BusinessOrigin.Scheme == "http" &&
                    connection.BusinessOrigin.Host == "127.0.0.1" && connection.BusinessOrigin.Port > 0 &&
                    connection.BusinessOrigin.AbsolutePath == "/" && connection.Revoked.CanBeCanceled &&
                    !connection.Revoked.IsCancellationRequested && runtime.IsConnected,
                    "the verified process publishes an actual origin and lifetime, not the caller's startup token");
                using var waiter = new CancellationTokenSource(); waiter.Cancel();
                try { await runtime.CaptureConnectionAsync(waiter.Token); throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { }
                Check(!connection.Revoked.IsCancellationRequested, "one abandoned capture cannot revoke a running epoch");
                owner.Cancel();
                Check(connection.Revoked.IsCancellationRequested && !runtime.IsConnected, "trusted owner revocation is immediate");
                await Error("CLOSED", () => connection.Client.CallAsync("film.session.snapshot"));
                await Error("CLOSED", () => runtime.CaptureConnectionAsync());
            }
            finally { await runtime.DisposeAsync(); }
            Check(runtime.Completion.IsCompletedSuccessfully, "native close still awaits the real child and pipe drains");
        });
        yield return new("native-runtime-capture: an exited process revokes its captured authority before any successor is published", async () =>
        {
            var runtime = await StartAsync();
            var old = await runtime.CaptureConnectionAsync();
            try
            {
                using var child = Process.GetProcessById(runtime.ProcessId);
                child.Kill(entireProcessTree: true);
                await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                Check(old.Revoked.IsCancellationRequested && !runtime.IsConnected, "physical process loss revokes its exact borrowed authority");
                await Error("CLOSED", () => old.Client.CallAsync("film.session.snapshot"));
                await using var next = await StartAsync();
                var connection = await next.CaptureConnectionAsync();
                Check(!ReferenceEquals(connection.Client, old.Client) && !connection.Revoked.IsCancellationRequested &&
                    old.Revoked.IsCancellationRequested, "a new process never revives an old captured client or token");
            }
            finally { await runtime.DisposeAsync(); }
        });
    }
}
