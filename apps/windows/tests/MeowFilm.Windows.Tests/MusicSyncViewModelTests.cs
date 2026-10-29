using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicSyncViewModelTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("loads the sole current state and repeated activation does not duplicate reads", LoadsOnce);
        yield return Case("each confirmed switch writes its exact row immediately and runs sync once", ImmediateSet);
        yield return Case("declined consent and invalid requests do not create local drafts or writes", Declined);
        yield return Case("matching authoritative state resolves an unknown commit without replaying CAS", UnknownCommitted);
        yield return Case("failed CAS restores the authoritative state and reports the failure", UnknownNotCommitted);
        yield return Case("revision conflict restores the newer authoritative state", RevisionConflict);
        yield return Case("run failure preserves saved switches and its error after authoritative reread", RunFailure);
        yield return Case("malformed and older views never partially replace current switches", InvalidViews);
        yield return Case("confirmation submits the exact token and runs sync once", Confirmation);
        yield return Case("busy state admits no second switch write", Busy);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("Music sync settings " + name, () => OnUi(run));
    private static void Check(bool value, string message = "ASSERTION_FAILED")
    { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Failure(string code) => E(new { ok = false, code });
    private sealed record Flags(bool favorites = false, bool playlists = false);
    private sealed record Call(string Method, JsonElement Input, CancellationToken Token);

    private sealed class Probe
    {
        internal long Revision = 1;
        internal readonly Dictionary<string, Flags> State = new()
        {
            ["wy"] = new(), ["tx"] = new(), ["kg"] = new()
        };
        internal readonly List<Call> Calls = [];
        internal Func<Call, Task<JsonElement>?>? Intercept;
        internal string ConfirmationToken = "";

        internal JsonElement View(long? revision = null)
        {
            object? confirmation = ConfirmationToken.Length == 0 ? null : new
            {
                token = ConfirmationToken,
                code = "DELETE_REMOTE_PLAYLIST",
                count = 2
            };
            return E(new
            {
                ok = true,
                revision = revision ?? Revision,
                platforms = State,
                confirmation,
                status = new { phase = "synced", code = "" }
            });
        }

        internal Task<JsonElement> Execute(string method, object? parameters, CancellationToken token)
        {
            var call = new Call(method["music.librarySync.".Length..], E(parameters ?? new { }), token);
            Calls.Add(call);
            return Intercept?.Invoke(call) ?? Task.FromResult(Default(call));
        }

        internal JsonElement Default(Call call)
        {
            if (call.Method == "set")
            {
                if (call.Input.GetProperty("ifRevision").GetInt64() != Revision)
                    return Failure("REVISION_CONFLICT");
                string platform = call.Input.GetProperty("platform").GetString()!;
                State[platform] = new(call.Input.GetProperty("favorites").GetBoolean(),
                    call.Input.GetProperty("playlists").GetBoolean());
                Revision++;
            }
            else if (call.Method == "confirm")
            {
                if (call.Input.GetProperty("ifRevision").GetInt64() != Revision ||
                    call.Input.GetProperty("token").GetString() != ConfirmationToken)
                    return Failure("CONFIRMATION_EXPIRED");
                ConfirmationToken = "";
                Revision++;
            }
            return View();
        }

        internal Call LastSet => Calls.Last(call => call.Method == "set");
        internal int Count(string method) => Calls.Count(call => call.Method == method);
    }

    private static async Task LoadsOnce()
    {
        var probe = new Probe { Revision = 3 };
        probe.State["wy"] = new(favorites: true);
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        Check(model.CanEdit && model.Platforms.Single(row => row.Platform == "wy").Favorites);
        await model.ActivateAsync();
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get"]),
            "an already active settings surface must not reread");
    }

    private static async Task ImmediateSet()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        var row = model.Platforms.Single(item => item.Platform == "wy");
        await model.SetAsync(row, "favorites", true, confirmed: true);
        Check(row.Favorites && !row.Playlists && probe.State["wy"].favorites);
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get", "set", "run"]));
        var saved = probe.LastSet.Input;
        Check(saved.GetProperty("platform").GetString() == "wy" &&
            saved.GetProperty("favorites").GetBoolean() &&
            !saved.GetProperty("playlists").GetBoolean() &&
            saved.GetProperty("ifRevision").GetInt64() == 1);
    }

    private static async Task Declined()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        var row = model.Platforms[0];
        await model.SetAsync(row, "favorites", true, confirmed: false);
        await model.SetAsync(row, "unknown", true, confirmed: true);
        await model.SetAsync(new("wy", "duplicate"), "favorites", true, confirmed: true);
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get"]));
        Check(!row.Favorites && !row.Playlists);
    }

    private static async Task UnknownCommitted()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        probe.Intercept = call => call.Method == "set" ? Task.FromResult(Unknown(call)) : null;
        JsonElement Unknown(Call call)
        {
            _ = probe.Default(call);
            return Failure("COMMIT_UNKNOWN");
        }
        var row = model.Platforms[0];
        await model.SetAsync(row, "favorites", true, confirmed: true);
        Check(row.Favorites && model.Status.Length == 0);
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get", "set", "get", "run"]));
        Check(probe.Count("set") == 1, "unknown receipt must not replay the CAS write");
    }

    private static async Task UnknownNotCommitted()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        probe.Intercept = call => call.Method == "set"
            ? Task.FromResult(Failure("COMMIT_UNKNOWN")) : null;
        var row = model.Platforms[0];
        await model.SetAsync(row, "favorites", true, confirmed: true);
        Check(!row.Favorites && model.Status == "同步设置保存失败");
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get", "set", "get"]));
    }

    private static async Task RevisionConflict()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        probe.Intercept = call => call.Method == "set" ? Task.FromResult(Conflict()) : null;
        JsonElement Conflict()
        {
            probe.Revision = 2;
            probe.State["tx"] = new(playlists: true);
            return Failure("REVISION_CONFLICT");
        }
        await model.SetAsync(model.Platforms[0], "favorites", true, confirmed: true);
        Check(!model.Platforms[0].Favorites && model.Platforms[1].Playlists &&
            model.Status == "同步设置已更新");
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get", "set", "get"]));
    }

    private static async Task RunFailure()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        probe.Intercept = call => call.Method == "run"
            ? Task.FromResult(Failure("AUTH_REQUIRED")) : null;
        var row = model.Platforms[0];
        await model.SetAsync(row, "favorites", true, confirmed: true);
        Check(row.Favorites && model.Status == "未登录");
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get", "set", "run", "get"]));
    }

    private static async Task InvalidViews()
    {
        var probe = new Probe();
        probe.State["wy"] = new(favorites: true);
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        bool malformed = true;
        probe.Intercept = call => call.Method == "get" && malformed
            ? Task.FromResult(Malformed()) : null;
        JsonElement Malformed()
        {
            malformed = false;
            return E(new { ok = true, revision = 9, platforms = new { wy = new { favorites = false } } });
        }
        await model.RefreshStatusAsync();
        Check(model.Platforms[0].Favorites && !model.Platforms[0].Playlists &&
            model.Status == "同步设置保存失败");
        probe.Intercept = call => call.Method == "get"
            ? Task.FromResult(probe.View(revision: 0)) : null;
        probe.State["wy"] = new(playlists: true);
        await model.RefreshStatusAsync();
        Check(model.Platforms[0].Favorites && !model.Platforms[0].Playlists,
            "an older view must not roll back the current revision");
    }

    private static async Task Confirmation()
    {
        var probe = new Probe { ConfirmationToken = "confirm-1" };
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        Check(model.HasConfirmation && model.Confirmation.Contains("2", StringComparison.Ordinal));
        await model.ConfirmAsync(false);
        Check(probe.Calls.Select(call => call.Method).SequenceEqual(["get"]));
        await model.ConfirmAsync(true);
        Check(!model.HasConfirmation && probe.Calls.Select(call => call.Method)
            .SequenceEqual(["get", "confirm", "run"]));
        var input = probe.Calls.Single(call => call.Method == "confirm").Input;
        Check(input.GetProperty("token").GetString() == "confirm-1" &&
            input.GetProperty("ifRevision").GetInt64() == 1);
    }

    private static async Task Busy()
    {
        var probe = new Probe();
        await using var model = new MusicLibrarySyncViewModel(probe.Execute);
        await model.ActivateAsync();
        var held = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Intercept = call => call.Method == "set" ? held.Task : null;
        var first = model.SetAsync(model.Platforms[0], "favorites", true, confirmed: true);
        Check(!model.CanEdit && probe.Count("set") == 1);
        await model.SetAsync(model.Platforms[1], "playlists", true, confirmed: true);
        Check(probe.Count("set") == 1, "busy settings must not queue a hidden second write");
        held.SetResult(probe.Default(probe.LastSet));
        await first;
        Check(model.CanEdit && model.Platforms[0].Favorites && !model.Platforms[1].Playlists);
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
