using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicSyncViewModelLifecycleTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music sync VM retired page cannot release reopened request", () => OnUi(Reopen));
        yield return new("Music sync VM repeated page retirement preserves current busy", () => OnUi(RepeatedRetirement));
        yield return new("Music sync VM final hide drains earlier page requests", () => OnUi(DrainEarlierPage));
        yield return new("Music sync VM cancelled hide waiter does not abandon physical drain", () => OnUi(CancelledWaiter));
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task Reopen()
    {
        var requests = new Requests();
        await using var model = new MusicLibrarySyncViewModel(requests.Execute);
        var tasks = new List<Task>();
        try
        {
            var first = model.ActivateAsync(); tasks.Add(first);
            var hidden = model.DeactivateAsync(); tasks.Add(hidden);
            Check(requests.Items[0].Token.IsCancellationRequested, "old page request must be cancelled");
            var next = model.ActivateAsync(); tasks.Add(next);
            Check(requests.Items.Count == 2, "reopened page must issue its own initial read");
            Check(!requests.Items[1].Token.IsCancellationRequested, "new page must have a separate token");
            _ = model.ActivateAsync();
            Check(requests.Items.Count == 2, "repeated activation must share the current read");

            requests.Items[0].Complete(1, true, false);
            await first; await hidden;
            Check(!model.CanEdit, "retired page must not clear new request busy");
            Check(!model.Platforms[0].Favorites, "retired response must not publish");
            requests.Items[1].Complete(2, false, true);
            await next;
            Check(model.CanEdit && model.Platforms[0].Playlists && !model.Platforms[0].Favorites,
                "only the reopened response may publish");
        }
        finally { requests.ReleaseAll(); await Task.WhenAll(tasks); }
    }

    private static async Task RepeatedRetirement()
    {
        var requests = new Requests();
        await using var model = new MusicLibrarySyncViewModel(requests.Execute);
        var tasks = new List<Task>();
        try
        {
            var first = model.ActivateAsync(); tasks.Add(first);
            var firstHide = model.DeactivateAsync(); tasks.Add(firstHide);
            var second = model.ActivateAsync(); tasks.Add(second);
            var secondHide = model.DeactivateAsync(); tasks.Add(secondHide);
            var current = model.ActivateAsync(); tasks.Add(current);
            Check(requests.Items.Count == 3, "each page generation must read once");
            requests.Items[0].Complete(1, true, false); await first; await firstHide;
            Check(!model.CanEdit, "first retirement must not release current request");
            requests.Items[1].Complete(2, true, false); await second; await secondHide;
            Check(!model.CanEdit && !model.Platforms[0].Favorites,
                "second retirement must not publish into the current page");
            requests.Items[2].Complete(3, false, true); await current;
            Check(model.CanEdit && model.Platforms[0].Playlists,
                "the current generation must recover normally");
        }
        finally { requests.ReleaseAll(); await Task.WhenAll(tasks); }
    }

    private static async Task DrainEarlierPage()
    {
        var requests = new Requests();
        await using var model = new MusicLibrarySyncViewModel(requests.Execute);
        var tasks = new List<Task>();
        try
        {
            var first = model.ActivateAsync(); tasks.Add(first);
            var oldHide = model.DeactivateAsync(); tasks.Add(oldHide);
            var next = model.ActivateAsync(); tasks.Add(next);
            Check(requests.Items.Count == 2, "reopened page read must not be dropped");
            requests.Items[1].Complete(2, false, true); await next;
            var finalHide = model.DeactivateAsync(); tasks.Add(finalHide);
            Check(!finalHide.IsCompleted, "final hide must retain the earlier physically unsettled request");
            requests.Items[0].Complete(1, true, false);
            await first; await oldHide; await finalHide;
            Check(!model.CanEdit && model.Platforms[0].Playlists && !model.Platforms[0].Favorites,
                "hidden page must not accept stale completion");
        }
        finally { requests.ReleaseAll(); await Task.WhenAll(tasks); }
    }

    private static async Task CancelledWaiter()
    {
        var requests = new Requests();
        var model = new MusicLibrarySyncViewModel(requests.Execute);
        using var waiting = new CancellationTokenSource();
        var tasks = new List<Task>();
        try
        {
            var first = model.ActivateAsync(); tasks.Add(first);
            var hidden = model.DeactivateAsync(waiting.Token);
            waiting.Cancel();
            bool cancelled = false;
            try { await hidden; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "caller may cancel waiting for hide");
            Check(requests.Items[0].Token.IsCancellationRequested,
                "old request cancellation must still be signalled");
            var next = model.ActivateAsync(); tasks.Add(next);
            Check(requests.Items.Count == 2, "cancelled hide must not suppress new page read");
            requests.Items[1].Complete(2, false, true); await next;
            var closing = model.DisposeAsync().AsTask(); tasks.Add(closing);
            Check(!closing.IsCompleted, "dispose must still await the retired physical request");
            requests.Items[0].Complete(1, true, false);
            await first; await closing;
            Check(!model.CanEdit && model.Platforms[0].Playlists,
                "disposed model cannot be released by an old completion");
            bool rejected = false;
            try { await model.ActivateAsync(); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "disposed model must reject activation");
        }
        finally { requests.ReleaseAll(); await Task.WhenAll(tasks); await model.DisposeAsync(); }
    }

    private sealed class Requests
    {
        internal readonly List<Request> Items = [];
        internal Task<JsonElement> Execute(string method, object? parameters, CancellationToken token)
        {
            Check(method == "music.librarySync.get", "busy page must not admit another operation");
            var request = new Request(token); Items.Add(request); return request.Response.Task;
        }
        internal void ReleaseAll()
        { foreach (var request in Items) request.Complete(99, false, false); }
    }

    private sealed class Request(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<JsonElement> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(long revision, bool favorites, bool playlists) =>
            Response.TrySetResult(JsonSerializer.SerializeToElement(new
            {
                ok = true,
                revision,
                platforms = new
                {
                    wy = new { favorites, playlists },
                    tx = new { favorites = false, playlists = false },
                    kg = new { favorites = false, playlists = false }
                },
                status = new { phase = "synced", code = "" }
            }));
    }

    private static Task OnUi(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = Run(); Dispatcher.Run();
            async Task Run()
            {
                await Task.Yield();
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
