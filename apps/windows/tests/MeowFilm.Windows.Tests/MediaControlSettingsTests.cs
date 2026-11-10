using System.Text.Json.Nodes;
using System.Windows.Input;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class MediaControlSettingsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Media control settings: exact defaults and native document identity", Defaults);
        yield return new("Media control settings: independent switches bindings and clear survive reopening", Persistence);
        yield return new("Media control settings: conflicts lost receipts and failed writes follow authority", WriteFailures);
        yield return new("Media control settings: malformed or conflicting documents cannot be overwritten", CorruptDocuments);
        yield return new("Media control settings: read failure revokes input and explicit reread recovers", ReadFailures);
        yield return new("Media control settings: invalid gestures and cross action duplicates are rejected", Validation);
        yield return new("Media control settings: recording errors and refresh are transient runtime state", RuntimeState);
        yield return new("Media control settings: preparation drains accepted writes and cancellation restores admission", Shutdown);
        yield return new("Media control settings: shared preparation failure resumes settings before resource release", FailedPreparation);
        yield return new("Media control settings: forced release drains settings before storage disposal", ForceRelease);
    }

    private static async Task Defaults()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        Check(!owner.IsAvailable && !owner.CanEdit);
        await owner.LoadAsync();
        Check(owner.IsAvailable && owner.CanEdit && owner.Preferences.GlobalEnabled && owner.Preferences.SystemMediaEnabled);
        var expected = new[]
        {
            (MediaShortcutAction.TogglePlayback, Key.P, "播放/暂停"),
            (MediaShortcutAction.Previous, Key.Left, "上一首"),
            (MediaShortcutAction.Next, Key.Right, "下一首"),
            (MediaShortcutAction.VolumeUp, Key.Up, "音量加"),
            (MediaShortcutAction.VolumeDown, Key.Down, "音量减"),
            (MediaShortcutAction.ToggleFavorite, Key.L, "喜欢歌曲"),
            (MediaShortcutAction.ToggleDesktopLyrics, Key.D, "打开/关闭歌词"),
            (MediaShortcutAction.ToggleTranslation, Key.T, "翻译当前歌词")
        };
        Check(owner.Preferences.Bindings.Count == 8);
        foreach (var (action, key, label) in expected)
        {
            var binding = owner.Preferences.Bindings.Single(item => item.Action == action);
            Check(binding.Local == new MediaShortcutGesture(key, ModifierKeys.Control));
            Check(binding.Global == (action == MediaShortcutAction.ToggleTranslation ? null :
                new MediaShortcutGesture(key, ModifierKeys.Control | ModifierKeys.Alt)));
            Check(MediaControlDefaults.Label(action) == label);
        }
        Check(store.Writes == 0 && store.Names.SetEquals([AppMediaControlSettings.DocumentNamespace]));
        Check(AppIdentity.NativeNamespaces.Contains("app.media-controls.v1"));
        Check(!AppIdentity.ProtectedNamespaces.Contains("app.media-controls.v1"));
    }
    private static async Task Persistence()
    {
        var store = new DocumentStore();
        store.Data[AppAppearance.DocumentNamespace] = [1, 2, 3];
        store.Data["music.library.v1"] = [4, 5, 6];
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        Check(!owner.Preferences.GlobalEnabled && owner.Preferences.SystemMediaEnabled);
        await owner.SaveAsync(owner.Preferences with { SystemMediaEnabled = false });
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = true });
        Check(owner.Preferences.GlobalEnabled && !owner.Preferences.SystemMediaEnabled);
        var bindings = owner.Preferences.Bindings.ToArray();
        bindings[0] = bindings[0] with { Local = new(Key.F8, ModifierKeys.Shift), Global = null };
        await owner.SaveAsync(owner.Preferences with { Bindings = bindings.Reverse().ToArray() });
        bindings[0] = bindings[0] with { Local = new(Key.F9, ModifierKeys.Control) };
        Check(Binding(owner, MediaShortcutAction.TogglePlayback).Local == new MediaShortcutGesture(Key.F8, ModifierKeys.Shift));
        await using var reopened = new AppMediaControlSettings(store);
        await reopened.LoadAsync();
        Check(reopened.Preferences.GlobalEnabled && !reopened.Preferences.SystemMediaEnabled);
        Check(reopened.Preferences.Bindings.SequenceEqual(owner.Preferences.Bindings));
        var data = JsonNode.Parse(store.Data[AppMediaControlSettings.DocumentNamespace])!.AsObject();
        Check(data["schema"]!.GetValue<int>() == 1 && data["bindings"]!.AsArray().Count == 8);
        Check(data["bindings"]![0]!["global"] is null, "cleared keys remain unassigned after persistence");
        Check(store.WriteNames.SetEquals([AppMediaControlSettings.DocumentNamespace]));
        Check(store.Data[AppAppearance.DocumentNamespace].SequenceEqual(new byte[] { 1, 2, 3 }));
        Check(store.Data["music.library.v1"].SequenceEqual(new byte[] { 4, 5, 6 }));
    }
    private static async Task WriteFailures()
    {
        var store = new DocumentStore { FaultNamespace = AppMediaControlSettings.DocumentNamespace };
        await using var first = new AppMediaControlSettings(store);
        await using var second = new AppMediaControlSettings(store);
        await first.LoadAsync(); await second.LoadAsync();
        await second.SaveAsync(second.Preferences with { SystemMediaEnabled = false });
        await first.SaveAsync(first.Preferences with { GlobalEnabled = false });
        Check(first.Preferences.GlobalEnabled && !first.Preferences.SystemMediaEnabled &&
            first.Status.Length > 0 && first.CanEdit, "stale CAS reads authority without retrying the draft");
        Check(store.Writes == 2);
        store.Fault = store.FaultAfter = true;
        await first.SaveAsync(first.Preferences with { GlobalEnabled = false });
        Check(!first.Preferences.GlobalEnabled && first.Status == "", "accepted write with a lost receipt reconciles");
        store.Fault = true; store.FaultAfter = false;
        await first.SaveAsync(first.Preferences with { SystemMediaEnabled = true });
        Check(!first.Preferences.SystemMediaEnabled && first.Status.Length > 0 && first.IsAvailable);
        Check(store.Writes == 4, "write failure never silently retries");
    }
    private static async Task CorruptDocuments()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        var good = store.Data[AppMediaControlSettings.DocumentNamespace].ToArray();
        Action<JsonNode>[] corrupt =
        [
            node => node["schema"] = 9,
            node => node["extra"] = true,
            node => node["globalEnabled"] = "true",
            node => node["bindings"]!.AsArray().RemoveAt(0),
            node => node["bindings"]![1]!["action"] = "TogglePlayback",
            node => node["bindings"]![0]!["local"]!["key"] = "LeftCtrl",
            node => node["bindings"]![0]!["local"]!["modifiers"] = 32,
            node => node["bindings"]![0]!["local"]!["key"] = "F",
            node => node["bindings"]![7]!["global"] = node["bindings"]![0]!["global"]!.DeepClone(),
            node => node["bindings"]![1]!["local"] = node["bindings"]![0]!["local"]!.DeepClone()
        ];
        int writes = store.Writes;
        foreach (var change in corrupt)
        {
            var node = JsonNode.Parse(good)!; change(node);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);
            store.Data[AppMediaControlSettings.DocumentNamespace] = bytes;
            await owner.LoadAsync();
            Check(!owner.IsAvailable && !owner.CanEdit && owner.Status.Length > 0);
            await owner.SaveAsync(MediaControlDefaults.Preferences);
            Check(store.Writes == writes && store.Data[AppMediaControlSettings.DocumentNamespace].SequenceEqual(bytes));
        }
        store.Data[AppMediaControlSettings.DocumentNamespace] = [0xff, 0x00];
        await owner.LoadAsync();
        Check(!owner.IsAvailable);
        store.Data[AppMediaControlSettings.DocumentNamespace] = good;
        await owner.LoadAsync();
        Check(owner.IsAvailable && !owner.Preferences.GlobalEnabled && owner.Status == "");
    }
    private static async Task ReadFailures()
    {
        var inner = new DocumentStore { FaultNamespace = AppMediaControlSettings.DocumentNamespace };
        var store = new ReadFailureStore(inner);
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        owner.SetRecording(true);
        store.FailReads = true;
        await owner.LoadAsync();
        Check(!owner.IsAvailable && !owner.IsRecording && owner.Status.Length > 0);
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        Check(inner.Writes == 0);
        store.FailReads = false; await owner.LoadAsync();
        Check(owner.CanEdit && owner.Status == "");
        inner.Fault = true; inner.FaultAfter = false; store.FailReads = true;
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        Check(!owner.CanEdit && !owner.IsAvailable && owner.Status.Length > 0);
        store.FailReads = false; await owner.LoadAsync();
        Check(owner.Preferences.GlobalEnabled && owner.CanEdit);
    }
    private static async Task Validation()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        MediaShortcutGesture[] invalid =
        [
            new(Key.None, ModifierKeys.None), new(Key.System, ModifierKeys.None),
            new(Key.ImeProcessed, ModifierKeys.None), new(Key.DeadCharProcessed, ModifierKeys.None),
            new(Key.LeftCtrl, ModifierKeys.Control), new(Key.RightShift, ModifierKeys.Shift),
            new(Key.LWin, ModifierKeys.Windows), new(Key.A, (ModifierKeys)32),
            new(Key.F, ModifierKeys.Control), new(Key.Left, ModifierKeys.Alt),
            new(Key.Escape, ModifierKeys.None), new(Key.Space, ModifierKeys.None)
        ];
        foreach (var gesture in invalid)
            await Reject(owner, Replace(owner.Preferences, MediaShortcutAction.TogglePlayback, gesture, false));
        await Reject(owner, Replace(owner.Preferences, MediaShortcutAction.Previous, new(Key.P, ModifierKeys.Control), false));
        await Reject(owner, Replace(owner.Preferences, MediaShortcutAction.Previous, new(Key.P, ModifierKeys.Control), true));
        await Reject(owner, Replace(owner.Preferences, MediaShortcutAction.ToggleTranslation, new(Key.F10, ModifierKeys.Control), true));
        Check(store.Writes == 0);
        await owner.SaveAsync(Replace(owner.Preferences, MediaShortcutAction.TogglePlayback, new(Key.P, ModifierKeys.Control), true));
        Check(store.Writes == 1 && Binding(owner, MediaShortcutAction.TogglePlayback).Local == Binding(owner, MediaShortcutAction.TogglePlayback).Global,
            "the same action may share its local and global gesture");
        Check(MediaShortcutGestures.Display(new(Key.D5, ModifierKeys.Control | ModifierKeys.Shift)) == "Ctrl+Shift+5");
        Check(MediaShortcutGestures.Display(null) == "未设置");
    }
    private static async Task RuntimeState()
    {
        var store = new DocumentStore();
        await using var owner = new AppMediaControlSettings(store);
        int refreshes = 0; owner.RegistrationRefreshRequested += (_, _) => refreshes++;
        owner.SetRecording(true); owner.RequestRegistrationRefresh();
        Check(!owner.IsRecording && refreshes == 0);
        await owner.LoadAsync();
        owner.RequestRegistrationRefresh(); Check(refreshes == 1);
        owner.SetRecording(true); owner.RequestRegistrationRefresh();
        Check(owner.IsRecording && refreshes == 1);
        owner.SetRecording(false);
        var errors = new Dictionary<MediaShortcutAction, string> { [MediaShortcutAction.Next] = "热键被占用" };
        owner.SetGlobalErrors(errors);
        errors.Clear();
        Check(owner.GlobalErrors[MediaShortcutAction.Next] == "热键被占用");
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        owner.RequestRegistrationRefresh(); Check(refreshes == 1);
        await using var reopened = new AppMediaControlSettings(store);
        await reopened.LoadAsync();
        Check(!reopened.IsRecording && reopened.GlobalErrors.Count == 0);
    }
    private static async Task Shutdown()
    {
        var store = new DocumentStore { HoldNamespace = AppMediaControlSettings.DocumentNamespace };
        await using var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        owner.SetRecording(true);
        var save = owner.SaveAsync(owner.Preferences with { SystemMediaEnabled = false });
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepare = owner.PrepareShutdownAsync();
            Check(!owner.CanEdit && !owner.IsAvailable && !owner.IsRecording && !prepare.IsCompleted);
            await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
            store.Release.TrySetResult();
            await Task.WhenAll(save, prepare).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { store.Release.TrySetResult(); await save; }
        Check(!owner.Preferences.SystemMediaEnabled && owner.Preferences.GlobalEnabled && store.Writes == 1);
        await owner.CancelShutdownPreparationAsync();
        Check(owner.CanEdit && !owner.IsRecording);
        await owner.DisposeAsync(); await owner.CancelShutdownPreparationAsync(); await owner.LoadAsync();
        Check(!owner.CanEdit && !owner.IsAvailable, "cancellation cannot resurrect a disposed settings owner");
    }
    private static async Task FailedPreparation()
    {
        var store = new DocumentStore();
        var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        bool released = false;
        var lifecycle = new AppShutdown([new FailingParticipant()], [new(() => { released = true; return ValueTask.CompletedTask; })],
            sharedParticipants: [owner]);
        try { await lifecycle.CloseAsync(); throw new Exception("failed preparation was accepted"); }
        catch (InvalidOperationException error) when (error.Message == "SHUTDOWN_FAILED") { }
        Check(owner.CanEdit && !released);
        await lifecycle.CancelShutdownPreparationAsync();
        await owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        Check(!owner.Preferences.GlobalEnabled);
        await lifecycle.ForceReleaseAsync();
        Check(released && !owner.IsAvailable);
    }
    private static async Task ForceRelease()
    {
        var store = new DocumentStore { HoldNamespace = AppMediaControlSettings.DocumentNamespace };
        var owner = new AppMediaControlSettings(store);
        await owner.LoadAsync();
        var save = owner.SaveAsync(owner.Preferences with { GlobalEnabled = false });
        var lifecycle = new AppShutdown([], [new(() => store.DisposeAsync())], sharedParticipants: [owner]);
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var release = lifecycle.ForceReleaseAsync();
            Check(!release.IsCompleted && !owner.IsAvailable && !store.Disposed);
            store.Release.TrySetResult();
            await Task.WhenAll(save, release).WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.Disposed && !owner.Preferences.GlobalEnabled && lifecycle.IsReleased);
        }
        finally { store.Release.TrySetResult(); await save; await lifecycle.ForceReleaseAsync(); }
    }
    internal static MediaControlPreferences Replace(MediaControlPreferences value, MediaShortcutAction action,
        MediaShortcutGesture? gesture, bool global) => value with
        {
            Bindings = value.Bindings.Select(binding => binding.Action != action ? binding :
                global ? binding with { Global = gesture } : binding with { Local = gesture }).ToArray()
        };
    private static MediaShortcutBinding Binding(AppMediaControlSettings owner, MediaShortcutAction action) =>
        owner.Preferences.Bindings.Single(binding => binding.Action == action);
    private static async Task Reject(AppMediaControlSettings owner, MediaControlPreferences value)
    {
        try { await owner.SaveAsync(value); throw new Exception("invalid settings accepted"); }
        catch (ArgumentException) { }
    }
    private sealed class ReadFailureStore(IDocumentStore inner) : IDocumentStore
    {
        internal bool FailReads;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) =>
            FailReads ? Task.FromException<DocumentSnapshot>(new DocumentStoreException(DocumentStoreError.Storage)) : inner.ReadAsync(ns, token);
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default) =>
            inner.WriteAsync(ns, data, expected, token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class FailingParticipant : IDesktopShutdownParticipant
    {
        public Task PrepareShutdownAsync() => Task.FromException(new InvalidOperationException("synthetic preparation failure"));
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
