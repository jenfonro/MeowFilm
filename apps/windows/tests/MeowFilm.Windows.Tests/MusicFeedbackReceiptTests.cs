using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicFeedbackReceiptTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.FeedbackReceiptCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> FeedbackReceiptCases()
    {
        yield return Case("feedback confirmed save retires its projection and reopening waits for the physical write",
            FeedbackConfirmedAfterRetirement);
        foreach (bool applied in new[] { false, true })
        {
            bool committed = applied;
            yield return Case($"feedback {(applied ? "applied" : "unapplied")} unknown survives retirement and only the same full settings write clears it",
                () => FeedbackUnknownAfterRetirement(committed));
        }
        foreach (bool failure in new[] { false, true })
        {
            bool fails = failure;
            yield return Case($"feedback retired page read cannot publish its late {(failure ? "error" : "value")}",
                () => FeedbackRetiredRead(fails));
            yield return Case($"feedback retired save readback cannot publish its late {(failure ? "error" : "value")}",
                () => FeedbackRetiredReadback(fails));
        }
        foreach (var scenario in new[] { (Applied: true, ReadFails: false),
            (Applied: false, ReadFails: false), (Applied: true, ReadFails: true) })
        {
            var selected = scenario;
            yield return Case($"feedback current unknown reconciles without waiting for itself applied={scenario.Applied} readFails={scenario.ReadFails}",
                () => FeedbackCurrentReadback(selected.Applied, selected.ReadFails));
        }
    }

    private static async Task FeedbackConfirmedAfterRetirement()
    {
        await using var f = await FeedbackReceiptFixture.Create();
        var row = f.Module.FeedbackPlatforms.Single(value => value.Platform == "wy");
        int changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        var write = f.Store.HoldWrite();
        var saving = f.Module.SetFeedbackEnabledAsync(row, true);
        try
        {
            await write.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!write.Token.CanBeCanceled, "the actual store write owns its admitted settlement");
            await f.Module.DeactivateSettingsAsync();
            int reads = f.Store.Reads;
            var read = f.Store.HoldRead();
            var reopening = f.Module.ActivateSettingsAsync();
            try
            {
                await Drain();
                True(!saving.IsCompleted && !reopening.IsCompleted && f.Store.Reads == reads,
                    "reopening cannot read ahead of the previous physical save");
                write.Release();
                await read.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await saving.WaitAsync(TimeSpan.FromSeconds(5));
                True(!row.Enabled && changes == 0 && !f.Module.CanEditFeedback,
                    "the retired confirmed reply does not publish the closed page");
                Equal(0, f.Module.OwnedNotices.Count);
                read.Release();
                await reopening.WaitAsync(TimeSpan.FromSeconds(5));
                True(row.Enabled && f.Module.CanEditFeedback, "the reopened page adopts a real store read");
                Equal(reads + 1, f.Store.Reads);
                Equal(1, f.Store.Writes);
                True((await f.StoredFeedback()).Enabled.SequenceEqual(["wy"]), "the confirmed write really persisted");
            }
            finally { write.Release(); read.Release(); await Task.WhenAll(saving, reopening); }
        }
        finally { write.Release(); await saving; }
    }

    private static async Task FeedbackUnknownAfterRetirement(bool applied)
    {
        await using var f = await FeedbackReceiptFixture.Create();
        var metadata = f.Module.MetadataPlatforms.Single(value => value.Platform == "mg");
        f.Calls.TransformNext("music.metadata.settings.save", UnknownSetting);
        await f.Module.SetMetadataEnabledAsync(metadata, false);
        object metadataNotice = f.Module.OwnedNotices.Single().Owner;
        var row = f.Module.FeedbackPlatforms.Single(value => value.Platform == "wy");
        var write = f.Store.HoldWrite(unknown: true, applied: applied);
        var saving = f.Module.SetFeedbackEnabledAsync(row, true);
        try
        {
            await write.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.DeactivateSettingsAsync();
            string status = f.Module.FeedbackStatus;
            int reads = f.Store.Reads;
            write.Release();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            True(!row.Enabled && !f.Module.CanEditFeedback, "the late unknown cannot publish its retired draft");
            Equal(status, f.Module.FeedbackStatus);
            Equal(reads, f.Store.Reads);
            Equal(2, f.Module.OwnedNotices.Count);
            object feedbackNotice = f.Module.OwnedNotices.Single(value => value.Message.Contains("播放上报")).Owner;
            Equal(applied, (await f.StoredFeedback()).Enabled.Contains("wy"));

            await f.Module.ActivateSettingsAsync();
            Equal(applied, row.Enabled);
            Equal(reads + 1, f.Store.Reads);
            Equal(1, f.Store.Writes);
            True(ReferenceEquals(feedbackNotice,
                f.Module.OwnedNotices.Single(value => value.Message.Contains("播放上报")).Owner),
                "fresh readback cannot acknowledge or replay an unknown write");
            f.Module.Volume = 37;
            await f.Module.SaveVolumeAsync();
            Equal(2, f.Module.OwnedNotices.Count);
            f.Calls.FailNext("music.device.settings.save", "AUTH_REQUIRED");
            f.Module.Volume = 38;
            await f.Module.SaveVolumeAsync();
            string newerError = f.Module.Status;
            True(newerError.Length > 0, "a later independent operation publishes its own error");

            // Every platform is written in the same native feedback document.
            await f.Module.SetFeedbackEnabledAsync(f.Module.FeedbackPlatforms.Single(value => value.Platform == "kg"), true);
            Equal(2, f.Store.Writes);
            Equal(newerError, f.Module.Status);
            True(f.Module.OwnedNotices.Count == 1 &&
                ReferenceEquals(metadataNotice, f.Module.OwnedNotices.Single().Owner),
                "a confirmed full feedback write clears only feedback, not another setting or its notice owner");
            var stored = await f.StoredFeedback();
            True(stored.Enabled.Contains("kg") && stored.Enabled.Contains("wy") == applied,
                "the later platform edit preserves the rest of the actual settings write set");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(value => value.Platform == "mg"), true);
            Equal(0, f.Module.OwnedNotices.Count);
        }
        finally { write.Release(); await saving; }
    }

    private static async Task FeedbackRetiredRead(bool failure)
    {
        await using var f = await FeedbackReceiptFixture.Create();
        f.Calls.TransformNext("music.metadata.settings.save", UnknownSetting);
        await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(value => value.Platform == "mg"), false);
        object metadataNotice = f.Module.OwnedNotices.Single().Owner;
        var row = f.Module.FeedbackPlatforms.Single(value => value.Platform == "wy");
        await f.Module.DeactivateSettingsAsync();
        await f.StoreFeedback("wy");
        var read = f.Store.HoldRead(failure);
        var reading = f.Module.ActivateSettingsAsync();
        Task leaving = Task.CompletedTask;
        try
        {
            await read.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            leaving = f.Module.DeactivateSettingsAsync();
            f.Calls.FailNext("music.device.settings.save", "AUTH_REQUIRED");
            f.Module.Volume = 37;
            await f.Module.SaveVolumeAsync();
            string status = f.Module.Status, feedbackStatus = f.Module.FeedbackStatus;
            int changes = 0;
            row.PropertyChanged += (_, _) => changes++;
            read.Release();
            await Task.WhenAll(reading, leaving).WaitAsync(TimeSpan.FromSeconds(5));
            True(!row.Enabled && changes == 0 && !f.Module.CanEditFeedback,
                "a retired read cannot enable or mutate its old page");
            Equal(feedbackStatus, f.Module.FeedbackStatus);
            Equal(status, f.Module.Status);
            True(ReferenceEquals(metadataNotice, f.Module.OwnedNotices.Single().Owner),
                "the retired read cannot clear another setting's stable unknown receipt");
            int reads = f.Store.Reads;
            await f.Module.ActivateSettingsAsync();
            True(row.Enabled && f.Module.CanEditFeedback, "reopening performs a fresh successful read");
            Equal(reads + 1, f.Store.Reads);
            Equal(0, f.Store.Writes);
            Equal("", f.Module.FeedbackStatus);
            Equal("", f.Module.Status);
            True(ReferenceEquals(metadataNotice, f.Module.OwnedNotices.Single().Owner),
                "reopening retires the ordinary action error, not an unresolved setting receipt");
        }
        finally { read.Release(); await Task.WhenAll(reading, leaving); }
    }

    private static async Task FeedbackRetiredReadback(bool failure)
    {
        await using var f = await FeedbackReceiptFixture.Create();
        var row = f.Module.FeedbackPlatforms.Single(value => value.Platform == "wy");
        var write = f.Store.HoldWrite(unknown: true);
        var saving = f.Module.SetFeedbackEnabledAsync(row, true);
        FeedbackStorePause? read = null;
        Task leaving = Task.CompletedTask;
        try
        {
            await write.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            read = f.Store.HoldRead(failure);
            write.Release();
            await read.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            object notice = f.Module.OwnedNotices.Single().Owner;
            leaving = f.Module.DeactivateSettingsAsync();
            string status = f.Module.Status, feedbackStatus = f.Module.FeedbackStatus;
            read.Release();
            await Task.WhenAll(saving, leaving).WaitAsync(TimeSpan.FromSeconds(5));
            True(!row.Enabled && !f.Module.CanEditFeedback, "late save reconciliation cannot publish a retired page");
            Equal(status, f.Module.Status);
            Equal(feedbackStatus, f.Module.FeedbackStatus);
            int reads = f.Store.Reads;
            await f.Module.ActivateSettingsAsync();
            True(row.Enabled && f.Module.CanEditFeedback, "only a new generation adopts the stored setting");
            Equal(reads + 1, f.Store.Reads);
            Equal(1, f.Store.Writes);
            True(ReferenceEquals(notice, f.Module.OwnedNotices.Single().Owner),
                "late reconciliation and reopening retain the same stable unknown notice");
        }
        finally { write.Release(); read?.Release(); await Task.WhenAll(saving, leaving); }
    }

    private static async Task FeedbackCurrentReadback(bool applied, bool readFails)
    {
        await using var f = await FeedbackReceiptFixture.Create();
        var row = f.Module.FeedbackPlatforms.Single(value => value.Platform == "wy");
        var write = f.Store.HoldWrite(unknown: true, applied: applied);
        var saving = f.Module.SetFeedbackEnabledAsync(row, true);
        FeedbackStorePause? read = null;
        try
        {
            await write.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            read = f.Store.HoldRead(readFails);
            write.Release();
            await read.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!saving.IsCompleted, "the save retains its real readback, without waiting for its own work task");
            object notice = f.Module.OwnedNotices.Single().Owner;
            read.Release();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(applied && !readFails, row.Enabled);
            Equal(!readFails, f.Module.CanEditFeedback);
            Equal(readFails ? "播放上报设置保存失败" : applied ? "" : "播放上报设置未生效", f.Module.FeedbackStatus);
            Equal(1, f.Store.Writes);
            await f.Module.DeactivateSettingsAsync();
            int reads = f.Store.Reads;
            await f.Module.ActivateSettingsAsync();
            Equal(applied, row.Enabled);
            Equal("", f.Module.FeedbackStatus);
            Equal(reads + 1, f.Store.Reads);
            Equal(1, f.Store.Writes);
            True(ReferenceEquals(notice, f.Module.OwnedNotices.Single().Owner),
                "reconciliation is not a confirmed write receipt");
        }
        finally { write.Release(); read?.Release(); await saving; }
    }

    private sealed class FeedbackReceiptFixture : IAsyncDisposable
    {
        private readonly DocumentProbe backing = new();
        private readonly NativeMusicScriptTestRuntime runtime = new();
        public FeedbackReceiptStore Store { get; }
        public ObservedApplication Calls { get; }
        public MusicCapability Module { get; }
        private FeedbackReceiptFixture()
        {
            Store = new(backing);
            Calls = new(new MusicNativeApplication(Store, MusicTestStores.For(Store), runtime));
            Module = new(Calls, Store, new PlaybackProbe(), dialogs: new Dialogs());
        }
        public static async Task<FeedbackReceiptFixture> Create()
        {
            var fixture = new FeedbackReceiptFixture();
            try
            {
                await fixture.Module.InitializeAsync(default);
                await fixture.Module.ActivateSettingsAsync();
                True(fixture.Module.CanEditFeedback && !fixture.runtime.HasStarted,
                    "real native feedback settings load without starting a protocol runtime");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task<MusicFeedbackSettings> StoredFeedback()
        {
            await using var owner = new MusicFeedbackSettingsOwner(backing);
            return await owner.ReadAsync(refresh: true);
        }
        public async Task StoreFeedback(params string[] enabled)
        {
            await using var owner = new MusicFeedbackSettingsOwner(backing);
            var current = await owner.ReadAsync(refresh: true);
            await owner.SaveAsync(current.Revision, enabled);
        }
        public async ValueTask DisposeAsync()
        {
            Store.ReleaseAll(); Calls.ReleaseAll();
            try { await Module.DisposeAsync(); }
            finally { try { await runtime.DisposeAsync(); } finally { await Store.DisposeAsync(); } }
        }
    }

    // Only the feedback namespace is gated. All results still come from the
    // real feedback owner and native document CAS, never a replacement owner.
    private sealed class FeedbackReceiptStore(IDocumentStore inner) : IDocumentStore
    {
        private readonly object sync = new();
        private readonly List<FeedbackStorePause> pauses = [];
        private FeedbackStorePause? nextRead, nextWrite;
        private int reads, writes;
        public int Reads => Volatile.Read(ref reads);
        public int Writes => Volatile.Read(ref writes);
        public FeedbackStorePause HoldRead(bool failure = false)
        {
            lock (sync) { var pause = new FeedbackStorePause(failure); pauses.Add(pause); return nextRead = pause; }
        }
        public FeedbackStorePause HoldWrite(bool unknown = false, bool applied = true)
        {
            lock (sync) { var pause = new FeedbackStorePause(unknown, applied); pauses.Add(pause); return nextWrite = pause; }
        }
        public void ReleaseAll()
        { lock (sync) foreach (var pause in pauses) pause.Release(); }
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            if (ns != MusicFeedbackSettingsOwner.DocumentNamespace) return await inner.ReadAsync(ns, token);
            Interlocked.Increment(ref reads);
            FeedbackStorePause? pause;
            lock (sync) { pause = nextRead; nextRead = null; }
            var snapshot = await inner.ReadAsync(ns, token);
            if (pause is not null)
            {
                await pause.Wait(token);
                if (pause.Fail) throw new DocumentStoreException(DocumentStoreError.Unavailable);
            }
            return snapshot;
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            if (ns != MusicFeedbackSettingsOwner.DocumentNamespace)
                return await inner.WriteAsync(ns, data, expected, token);
            Interlocked.Increment(ref writes);
            FeedbackStorePause? pause;
            lock (sync) { pause = nextWrite; nextWrite = null; }
            if (pause is not null)
            {
                await pause.Wait(token);
                if (!pause.Applied) throw new DocumentStoreException(DocumentStoreError.Unavailable);
            }
            var receipt = await inner.WriteAsync(ns, data, expected, token);
            if (pause?.Fail == true) throw new DocumentStoreException(DocumentStoreError.Unavailable);
            return receipt;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FeedbackStorePause(bool fail, bool applied = true)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; } = fail;
        public bool Applied { get; } = applied;
        public CancellationToken Token { get; private set; }
        public Task Wait(CancellationToken token)
        {
            Token = token; Arrived.TrySetResult();
            return release.Task;
        }
        public void Release() => release.TrySetResult();
    }
}
