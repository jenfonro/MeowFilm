using System.IO;
using System.Text;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class AppClosePreferencesTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Window close preferences: default and all saved choices restore and reset", Persistence);
        yield return new("Window close preferences: conflicts and failed receipts report only confirmed authority", SaveFailures);
        yield return new("Window close preferences: read errors and invalid data never enable default writes", ReadFailures);
        yield return new("Window close preferences: shutdown drains accepted writes and cancellation reopens editing", Shutdown);
        yield return new("Window close preferences: abort and disposal drain without disposing the shared store", Release);
        yield return new("Window close preferences: registered local document survives store restart", LocalRestart);
    }

    private static async Task Persistence()
    {
        var store = new DocumentStore();
        await using var owner = new AppClosePreferences(store);
        Check(owner.Behavior == WindowCloseBehavior.Ask && !owner.CanEdit);
        Check(!await owner.SaveAsync(WindowCloseBehavior.Exit) && store.Writes == 0,
            "saving before an authoritative read is rejected");
        await owner.LoadAsync();
        Check(owner.CanEdit && owner.Behavior == WindowCloseBehavior.Ask && owner.Status == "" && store.Writes == 0,
            "a confirmed missing document defaults to Ask without writing");
        Check(await owner.SaveAsync(WindowCloseBehavior.Ask) && store.Writes == 0,
            "the confirmed current behavior needs no write");
        var changes = new HashSet<string>();
        owner.PropertyChanged += (_, e) => { if (e.PropertyName is { } name) changes.Add(name); };
        foreach (var behavior in new[] { WindowCloseBehavior.MinimizeToTray, WindowCloseBehavior.Exit, WindowCloseBehavior.Ask })
        {
            int writes = store.Writes;
            Check(await owner.SaveAsync(behavior));
            Check(owner.Behavior == behavior && owner.CanEdit && owner.Status == "" && store.Writes == writes + 1);
            Check(await owner.SaveAsync(behavior) && store.Writes == writes + 1,
                "unchanged selections do not rewrite the document");
            await using var reopened = new AppClosePreferences(store);
            await reopened.LoadAsync();
            Check(reopened.CanEdit && reopened.Behavior == behavior && reopened.Status == "",
                "a fresh owner restores the saved choice, including reset to Ask");
        }
        Check(changes.IsSupersetOf([nameof(IDesktopClosePreferences.Behavior),
            nameof(IDesktopClosePreferences.CanEdit), nameof(IDesktopClosePreferences.Status)]),
            "the settings and close prompt can observe the same owner");
        Check(store.WriteNames.SetEquals(["app.window.v1"]));
        int before = store.Writes;
        try { await owner.SaveAsync((WindowCloseBehavior)99); throw new Exception("invalid behavior accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Check(store.Writes == before && owner.Behavior == WindowCloseBehavior.Ask);
    }

    private static async Task SaveFailures()
    {
        var store = new DocumentStore { FaultNamespace = AppClosePreferences.DocumentNamespace };
        await using var first = new AppClosePreferences(store);
        await using var second = new AppClosePreferences(store);
        await first.LoadAsync(); await second.LoadAsync();
        Check(await second.SaveAsync(WindowCloseBehavior.Exit));
        int writes = store.Writes;
        Check(!await first.SaveAsync(WindowCloseBehavior.MinimizeToTray));
        Check(first.Behavior == WindowCloseBehavior.Exit && first.CanEdit && first.Status.Length > 0 &&
            store.Writes == writes + 1, "a conflicting draft is not replayed or reported saved");
        Check(await first.SaveAsync(WindowCloseBehavior.Exit) && first.Status == "" && store.Writes == writes + 1,
            "accepting the already confirmed authority clears the stale failure");
        store.Fault = true; store.FaultAfter = false;
        Check(!await first.SaveAsync(WindowCloseBehavior.MinimizeToTray));
        Check(first.Behavior == WindowCloseBehavior.Exit && first.CanEdit && first.Status.Length > 0,
            "a failed write retains the saved behavior and reports failure");
        store.Fault = true; store.FaultAfter = true;
        writes = store.Writes;
        Check(await first.SaveAsync(WindowCloseBehavior.MinimizeToTray));
        Check(first.Behavior == WindowCloseBehavior.MinimizeToTray && first.Status == "" && first.CanEdit &&
            store.Writes == writes + 1, "a lost receipt succeeds only after an authoritative matching read");
        await second.LoadAsync();
        Check(second.Behavior == WindowCloseBehavior.MinimizeToTray);

        var guarded = new ReadFaultStore(store);
        await using var unknown = new AppClosePreferences(guarded);
        await unknown.LoadAsync();
        store.Fault = true; store.FaultAfter = true; guarded.FailReads = true;
        Check(!await unknown.SaveAsync(WindowCloseBehavior.Exit));
        Check(!unknown.CanEdit && unknown.Status.Length > 0 && unknown.Behavior == WindowCloseBehavior.MinimizeToTray,
            "unreadable acceptance never publishes the draft or promises success");
        writes = store.Writes;
        Check(!await unknown.SaveAsync(WindowCloseBehavior.Ask) && store.Writes == writes,
            "unconfirmed storage cannot be overwritten by a new default");
        guarded.FailReads = false;
        await unknown.LoadAsync();
        Check(unknown.CanEdit && unknown.Status == "" && unknown.Behavior == WindowCloseBehavior.Exit,
            "explicit recovery restores the actual accepted write");
    }

    private static async Task ReadFailures()
    {
        var store = new DocumentStore();
        var guarded = new ReadFaultStore(store) { FailReads = true };
        await using var owner = new AppClosePreferences(guarded);
        await owner.LoadAsync();
        Check(!owner.CanEdit && owner.Status.Length > 0);
        Check(!await owner.SaveAsync(WindowCloseBehavior.Ask) && store.Writes == 0,
            "a read error is not a writable missing-document default");
        await owner.PrepareShutdownAsync(); await owner.CancelShutdownPreparationAsync();
        Check(!owner.CanEdit, "cancelling shutdown cannot turn a failed read into a loaded configuration");
        guarded.FailReads = false;
        await owner.LoadAsync();
        Check(owner.CanEdit && owner.Behavior == WindowCloseBehavior.Ask && owner.Status == "");
        Check(await owner.SaveAsync(WindowCloseBehavior.Exit));
        foreach (string invalid in new[]
        {
            "not-json", "null", "[]", "{}",
            "{\"schema\":99,\"closeBehavior\":\"ask\"}",
            "{\"schema\":1,\"closeBehavior\":\"unknown\"}",
            "{\"schema\":1,\"closeBehavior\":1}",
            "{\"schema\":1,\"closeBehavior\":\"ask\",\"extra\":true}"
        })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(invalid);
            store.Data[AppClosePreferences.DocumentNamespace] = bytes;
            await owner.LoadAsync();
            int writes = store.Writes;
            Check(!owner.CanEdit && owner.Status.Length > 0 && owner.Behavior == WindowCloseBehavior.Exit,
                "invalid storage preserves the last confirmed value and blocks edits");
            Check(!await owner.SaveAsync(WindowCloseBehavior.Ask) && store.Writes == writes &&
                store.Data[AppClosePreferences.DocumentNamespace].SequenceEqual(bytes),
                "invalid storage is never repaired by an implicit default write");
        }
        store.Data.Remove(AppClosePreferences.DocumentNamespace);
        await owner.LoadAsync();
        Check(owner.CanEdit && owner.Behavior == WindowCloseBehavior.Ask && owner.Status == "");
    }

    private static async Task Shutdown()
    {
        var store = new DocumentStore { HoldNamespace = AppClosePreferences.DocumentNamespace };
        await using var owner = new AppClosePreferences(store);
        await owner.LoadAsync();
        var save = owner.SaveAsync(WindowCloseBehavior.MinimizeToTray);
        Task close, reload;
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            close = owner.PrepareShutdownAsync();
            reload = owner.LoadAsync();
            Check(!owner.CanEdit && !close.IsCompleted && !save.IsCompleted);
            Check(!await owner.SaveAsync(WindowCloseBehavior.Exit), "shutdown blocks admission of new writes");
            Check(owner.Behavior == WindowCloseBehavior.Ask,
                "an accepted but unfinished write does not publish the requested behavior");
        }
        finally { store.Release.TrySetResult(); }
        Check(await save);
        await Task.WhenAll(close, reload).WaitAsync(TimeSpan.FromSeconds(3));
        Check(owner.Behavior == WindowCloseBehavior.MinimizeToTray && !owner.CanEdit && store.Writes == 1);
        Check(!await owner.SaveAsync(WindowCloseBehavior.MinimizeToTray),
            "even an unchanged preference cannot claim a save while stopping");
        await owner.CancelShutdownPreparationAsync();
        Check(owner.CanEdit);
        Check(await owner.SaveAsync(WindowCloseBehavior.Exit) && owner.Behavior == WindowCloseBehavior.Exit &&
            store.Writes == 2 && !store.Disposed, "cancelling shutdown reopens the same owner without losing storage");
    }

    private static async Task Release()
    {
        foreach (bool abort in new[] { false, true })
        {
            var store = new DocumentStore { HoldNamespace = AppClosePreferences.DocumentNamespace };
            await using var owner = new AppClosePreferences(store);
            await owner.LoadAsync();
            var save = owner.SaveAsync(WindowCloseBehavior.Exit);
            Task release;
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                release = abort ? owner.AbortAsync() : owner.DisposeAsync().AsTask();
                Check(!release.IsCompleted && !owner.CanEdit);
                Check(!await owner.SaveAsync(WindowCloseBehavior.Ask));
            }
            finally { store.Release.TrySetResult(); }
            Check(await save);
            await release.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!owner.CanEdit && !store.Disposed && owner.Behavior == WindowCloseBehavior.Exit,
                "release drains accepted work and leaves the shared document store to its owner");
            await owner.LoadAsync();
            Check(!owner.CanEdit, "a later load cannot reopen a stopped participant");
            await owner.DisposeAsync();
        }
    }

    private static async Task LocalRestart()
    {
        string root = TestWorkspace.DataPath("window-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var behavior in new[] { WindowCloseBehavior.MinimizeToTray, WindowCloseBehavior.Exit, WindowCloseBehavior.Ask })
        {
            await using (var store = new LocalDocumentStore(root, AppIdentity.NativeNamespaces,
                AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces))
            await using (var owner = new AppClosePreferences(store))
            {
                await owner.LoadAsync();
                Check(owner.CanEdit && await owner.SaveAsync(behavior) && owner.Status == "");
            }
            await using var reopenedStore = new LocalDocumentStore(root, AppIdentity.NativeNamespaces,
                AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            await using var reopened = new AppClosePreferences(reopenedStore);
            await reopened.LoadAsync();
            Check(reopened.CanEdit && reopened.Behavior == behavior && reopened.Status == "",
                "the application namespace grants permit a durable choice across store restarts");
        }
    }

    private sealed class ReadFaultStore(IDocumentStore inner) : IDocumentStore
    {
        internal bool FailReads;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken token = default) =>
            FailReads ? throw new DocumentStoreException(DocumentStoreError.Unavailable) : inner.ReadAsync(documentNamespace, token);
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken token = default) => inner.WriteAsync(documentNamespace, data, expected, token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
