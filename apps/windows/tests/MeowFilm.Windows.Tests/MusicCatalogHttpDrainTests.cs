// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Windows.Tests.MusicNativeHttpSeamTests;
namespace MeowFilm.Windows.Tests;
public static class MusicCatalogHttpDrainTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string reason in new[] { "cancel", "invalidate", "close" })
        {
            string r = reason;
            yield return new("music.catalog.runtime.drain." + r + "-retains-physical-work", async () =>
            {
                var f = new MusicPlatformRuntimeFixture(); var entered = Signal(); var cancelled = Signal(); var release = Signal();
                f.OnRequest = async (_, token) =>
                {
                    using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task;
                    if (r == "cancel") token.ThrowIfCancellationRequested();
                    return MusicPlatformRuntimeFixture.Json(new { items = new object[] { } });
                };
                using var cancel = new CancellationTokenSource(); var read = f.Catalog.SearchAsync(new("wy", "x"), cancel.Token);
                Task? close = null;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (r == "cancel") cancel.Cancel(); else if (r == "invalidate") f.Client.Invalidate("wy"); else close = f.DisposeAsync().AsTask();
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!read.IsCompleted && !f.Client.DrainAsync().IsCompleted);
                    if (close is not null) { Check(!close.IsCompleted); await Failure("CLOSED", () => f.Client.ReadAsync("wy", "song", new { }, default)); }
                }
                finally { release.TrySetResult(); }
                await Failure(r == "cancel" ? "CANCELLED" : r == "close" ? "CLOSED" : "STALE_SESSION", () => read);
                if (close is not null) await close; else await f.Client.DrainAsync();
                Check(f.Requests.Count == 1); await f.DisposeAsync();
            });
        }
        yield return new("music.catalog.runtime.close-shares-physical-publication-drain", async () =>
        {
            var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { }));
            await f.Catalog.HomeAsync(new("wy")); var entered = Signal(); var release = Signal();
            f.OnSessionUpdate = async (_, _) => { entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { published = true }); };
            var first = f.DisposeAsync().AsTask(); var second = f.DisposeAsync().AsTask();
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!first.IsCompleted && !second.IsCompleted); }
            finally { release.TrySetResult(); }
            await Task.WhenAll(first, second); Check(f.Calls.Count(call => call.Method == "music.platform.session.update") == 2);
        });
        yield return new("music.catalog.runtime.invalidation-publication-failure-survives-to-drain", async () =>
        {
            var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { }));
            await f.Catalog.HomeAsync(new("wy")); f.OnSessionUpdate = (_, _) => throw new MusicPlatformException("NETWORK_ERROR");
            f.Client.Invalidate("wy"); await Failure("NETWORK_ERROR", () => f.Client.DrainAsync());
            f.OnSessionUpdate = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { published = true })); await f.DisposeAsync();
        });
        yield return new("music.catalog.runtime.publication-must-confirm-before-business-dispatch", async () =>
        {
            var f = new MusicPlatformRuntimeFixture(); f.OnSessionUpdate = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { published = false }));
            await Failure("COMMIT_UNKNOWN", () => f.Catalog.HomeAsync(new("wy"))); Check(f.Requests.Count == 0);
            f.OnSessionUpdate = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { published = true })); await f.DisposeAsync();
        });
    }
}
