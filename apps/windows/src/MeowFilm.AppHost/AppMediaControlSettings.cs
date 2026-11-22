using System.Collections.ObjectModel;
using System.ComponentModel;
using Key = Windows.System.VirtualKey;
using ModifierKeys = Windows.System.VirtualKeyModifiers;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppMediaControlSettings(IStorageDatabase database) : IDesktopMediaControlSettings, IAsyncDisposable
{
    public const string DocumentNamespace = "app.media-controls.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
    private bool loaded, busy, stopping, disposed;
    public MediaControlPreferences Preferences { get; private set; } = MediaControlDefaults.Preferences;
    public bool IsAvailable => loaded && !stopping && !disposed;
    public bool CanEdit => IsAvailable && !busy;
    public string Status { get; private set; } = "";
    public bool IsRecording { get; private set; }
    public IReadOnlyDictionary<MediaShortcutAction, string> GlobalErrors { get; private set; } =
        new ReadOnlyDictionary<MediaShortcutAction, string>(new Dictionary<MediaShortcutAction, string>());
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? RegistrationRefreshRequested;

    private void Changed(params string[] properties)
    {
        foreach (var property in properties) PropertyChanged?.Invoke(this, new(property));
    }
    private void StateChanged() => Changed(nameof(IsAvailable), nameof(CanEdit), nameof(Status));
    private static MediaControlPreferences Snapshot(MediaControlPreferences value) =>
        new(value.GlobalEnabled, value.SystemMediaEnabled, Array.AsReadOnly(value.Bindings.OrderBy(binding => binding.Action).ToArray()));
    private static bool Equal(MediaControlPreferences first, MediaControlPreferences second) =>
        first.GlobalEnabled == second.GlobalEnabled && first.SystemMediaEnabled == second.SystemMediaEnabled &&
        first.Bindings.SequenceEqual(second.Bindings);
    private void Publish(MediaControlPreferences value)
    {
        if (Equal(Preferences, value)) return;
        Preferences = value;
        Changed(nameof(Preferences));
    }

    private async Task Read()
    {
        var (preferences, revision) = await database.ReadAsync(db =>
        {
            var loadedPrefs = ReadFromDb(db) ?? MediaControlDefaults.Preferences;
            return (loadedPrefs, db.Revision(DocumentNamespace));
        });
        storageRevision = revision;
        loaded = true;
        Publish(preferences);
    }
    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (stopping || disposed) return;
            busy = true; StateChanged();
            await Read();
            Status = "";
        }
        catch
        {
            loaded = false;
            SetRecording(false);
            Status = "快捷键设置读取失败";
        }
        finally { busy = false; StateChanged(); gate.Release(); }
    }

    public async Task SaveAsync(MediaControlPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (MediaShortcutGestures.ValidationError(preferences) is { } error)
            throw new ArgumentException(error, nameof(preferences));
        var draft = Snapshot(preferences);
        if (!CanEdit) return;
        await gate.WaitAsync();
        try
        {
            if (!CanEdit || Equal(Preferences, draft)) return;
            busy = true; Status = ""; StateChanged();
            try
            {
                var expected = storageRevision;
                storageRevision = await database.WriteAsync(db =>
                {
                    if (db.Revision(DocumentNamespace) != expected)
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                    var current = ReadFromDb(db);
                    if (current is not null && Equal(current, draft)) return expected;
                    db.Execute("""
                        INSERT INTO windows_app_media_controls(singleton,global_enabled,system_media_enabled)
                        VALUES(1,$global,$system)
                        ON CONFLICT(singleton) DO UPDATE SET global_enabled=excluded.global_enabled,system_media_enabled=excluded.system_media_enabled
                        """, ("$global", draft.GlobalEnabled), ("$system", draft.SystemMediaEnabled));
                    db.Execute("DELETE FROM windows_app_media_bindings");
                    for (int i = 0; i < draft.Bindings.Count; i++)
                    {
                        var binding = draft.Bindings[i];
                        db.Execute("""
                            INSERT INTO windows_app_media_bindings(action,position,local_key,local_modifiers,global_key,global_modifiers)
                            VALUES($action,$pos,$lk,$lm,$gk,$gm)
                            """,
                            ("$action", binding.Action.ToString()),
                            ("$pos", i),
                            ("$lk", binding.Local?.Key.ToString()),
                            ("$lm", binding.Local is { } l ? (long)(int)l.Modifiers : null),
                            ("$gk", binding.Global?.Key.ToString()),
                            ("$gm", binding.Global is { } g ? (long)(int)g.Modifiers : null));
                    }
                    return db.CommitRevision(DocumentNamespace, expected, Guid.NewGuid().ToString("D"));
                });
                Publish(draft);
            }
            catch
            {
                // A failed receipt may follow an accepted CAS. Only the stored
                // authority decides the value; never replay the stale draft.
                try { await Read(); Status = Equal(Preferences, draft) ? "" : "快捷键设置未保存，请重试"; }
                catch { loaded = false; SetRecording(false); Status = "快捷键设置保存失败，请重新读取"; }
            }
        }
        finally { busy = false; StateChanged(); gate.Release(); }
    }

    public void SetRecording(bool recording)
    {
        recording = recording && IsAvailable;
        if (IsRecording == recording) return;
        IsRecording = recording;
        Changed(nameof(IsRecording));
    }
    public void SetGlobalErrors(IReadOnlyDictionary<MediaShortcutAction, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (GlobalErrors.Count == errors.Count && errors.All(item =>
            GlobalErrors.TryGetValue(item.Key, out var value) && value == item.Value)) return;
        GlobalErrors = new ReadOnlyDictionary<MediaShortcutAction, string>(new Dictionary<MediaShortcutAction, string>(errors));
        Changed(nameof(GlobalErrors));
    }
    public void RequestRegistrationRefresh()
    {
        if (IsAvailable && Preferences.GlobalEnabled && !IsRecording)
            RegistrationRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task PrepareShutdownAsync()
    {
        stopping = true;
        SetRecording(false);
        StateChanged();
        await gate.WaitAsync();
        gate.Release();
    }

    public async Task AbortAsync()
    {
        disposed = true;
        await PrepareShutdownAsync();
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await PrepareShutdownAsync();
    }

    private static MediaControlPreferences? ReadFromDb(DatabaseSession db)
    {
        if (!db.TryQuerySingle(
            "SELECT global_enabled,system_media_enabled FROM windows_app_media_controls WHERE singleton=1",
            row => (Global: row.GetBool(0), System: row.GetBool(1)), out var head))
            return null;

        var bindings = db.Query(
            "SELECT action,local_key,local_modifiers,global_key,global_modifiers FROM windows_app_media_bindings ORDER BY position,action",
            row =>
            {
                var actionText = row.GetString(0);
                if (!Enum.TryParse<MediaShortcutAction>(actionText, out var action) ||
                    !Enum.IsDefined(action) || action.ToString() != actionText)
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                return new MediaShortcutBinding(
                    action,
                    DecodeGesture(row.GetNullableString(1), row.GetNullableInt32(2)),
                    DecodeGesture(row.GetNullableString(3), row.GetNullableInt32(4)));
            });

        var value = new MediaControlPreferences(head.Global, head.System, bindings);
        if (MediaShortcutGestures.ValidationError(value) is not null)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return Snapshot(value);

        static MediaShortcutGesture? DecodeGesture(string? keyText, int? modifiers)
        {
            if (keyText is null && modifiers is null) return null;
            if (keyText is null || modifiers is null ||
                !Enum.TryParse<Key>(keyText, out var key) || key.ToString() != keyText)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return new MediaShortcutGesture(key, (ModifierKeys)modifiers.Value);
        }
    }
}
