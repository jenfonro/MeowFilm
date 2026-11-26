using System.Collections.ObjectModel;
using System.ComponentModel;
using Key = Windows.System.VirtualKey;
using ModifierKeys = Windows.System.VirtualKeyModifiers;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppMediaControlSettings(IStorageDatabase database) : IDesktopMediaControlSettings, IAsyncDisposable
{
    public const string StorageOwner = "app.media-controls.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
    private static readonly PropertyChangedEventArgs PreferencesChangedArgs = new(nameof(Preferences));
    private static readonly PropertyChangedEventArgs IsRecordingChangedArgs = new(nameof(IsRecording));
    private static readonly PropertyChangedEventArgs GlobalErrorsChangedArgs = new(nameof(GlobalErrors));
    private static readonly PropertyChangedEventArgs[] StateProperties =
        [new(nameof(IsAvailable)), new(nameof(CanEdit)), new(nameof(Status))];

    private bool loaded, busy, stopping;
    public MediaControlPreferences Preferences { get; private set; } = MediaControlDefaults.Preferences;
    public bool IsAvailable => loaded && !stopping;
    public bool CanEdit => IsAvailable && !busy;
    public string Status { get; private set; } = "";
    public bool IsRecording { get; private set; }
    public IReadOnlyDictionary<MediaShortcutAction, string> GlobalErrors { get; private set; } =
        new ReadOnlyDictionary<MediaShortcutAction, string>(new Dictionary<MediaShortcutAction, string>());
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? RegistrationRefreshRequested;

    private void Changed(PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
    private void StateChanged()
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        foreach (var args in StateProperties) handler(this, args);
    }
    private static MediaControlPreferences Snapshot(MediaControlPreferences value) =>
        new(value.GlobalEnabled, value.SystemMediaEnabled, Array.AsReadOnly(value.Bindings.OrderBy(binding => binding.Action).ToArray()));
    private static bool Equal(MediaControlPreferences first, MediaControlPreferences second) =>
        first.GlobalEnabled == second.GlobalEnabled && first.SystemMediaEnabled == second.SystemMediaEnabled &&
        first.Bindings.SequenceEqual(second.Bindings);
    private void Publish(MediaControlPreferences value)
    {
        if (Equal(Preferences, value)) return;
        Preferences = value;
        Changed(PreferencesChangedArgs);
    }

    private async Task Read()
    {
        var (preferences, revision) = await database.ReadSnapshotAsync(db =>
        {
            var loadedPrefs = ReadFromDb(db) ?? MediaControlDefaults.Preferences;
            return (loadedPrefs, db.Revision(StorageOwner));
        });
        storageRevision = revision;
        loaded = true;
        Publish(preferences);
    }
    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        bool changed = false;
        try
        {
            if (stopping) return;
            busy = true; changed = true; StateChanged();
            await Read();
            Status = "";
        }
        catch
        {
            loaded = false;
            SetRecording(false);
            Status = "快捷键设置读取失败";
        }
        finally
        {
            busy = false;
            if (changed) StateChanged();
            gate.Release();
        }
    }

    public async Task SaveAsync(MediaControlPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (MediaShortcutGestures.ValidationError(preferences) is { } error)
            throw new ArgumentException(error, nameof(preferences));
        var draft = Snapshot(preferences);
        if (!CanEdit) return;
        await gate.WaitAsync();
        bool changed = false;
        try
        {
            if (!CanEdit || Equal(Preferences, draft)) return;
            busy = true; Status = ""; changed = true; StateChanged();
            try
            {
                var expected = storageRevision;
                var previous = Preferences;
                storageRevision = await database.WriteAsync(db =>
                {
                    if (db.Revision(StorageOwner) != expected)
                        throw new StorageException(StorageError.Conflict);
                    db.Execute("""
                        INSERT INTO windows_app_media_controls(singleton,global_enabled,system_media_enabled)
                        VALUES(1,$global,$system)
                        ON CONFLICT(singleton) DO UPDATE SET global_enabled=excluded.global_enabled,system_media_enabled=excluded.system_media_enabled
                        """, ("$global", draft.GlobalEnabled), ("$system", draft.SystemMediaEnabled));
                    var nextActions = draft.Bindings.Select(b => b.Action).ToHashSet();
                    foreach (var old in previous.Bindings)
                        if (!nextActions.Contains(old.Action))
                            db.Execute("DELETE FROM windows_app_media_bindings WHERE action=$action", ("$action", old.Action.ToString()));
                    for (int i = 0; i < draft.Bindings.Count; i++)
                    {
                        var binding = draft.Bindings[i];
                        db.Execute("""
                            INSERT INTO windows_app_media_bindings(action,position,local_key,local_modifiers,global_key,global_modifiers)
                            VALUES($action,$pos,$lk,$lm,$gk,$gm)
                            ON CONFLICT(action) DO UPDATE SET
                                position=excluded.position,
                                local_key=excluded.local_key,
                                local_modifiers=excluded.local_modifiers,
                                global_key=excluded.global_key,
                                global_modifiers=excluded.global_modifiers
                            """,
                            ("$action", binding.Action.ToString()),
                            ("$pos", i),
                            ("$lk", binding.Local?.Key.ToString()),
                            ("$lm", binding.Local is { } l ? (long)(int)l.Modifiers : null),
                            ("$gk", binding.Global?.Key.ToString()),
                            ("$gm", binding.Global is { } g ? (long)(int)g.Modifiers : null));
                    }
                    return db.CommitRevision(StorageOwner, expected);
                });
                Publish(draft);
            }
            catch
            {
                Status = "快捷键设置保存失败";
            }
        }
        finally
        {
            busy = false;
            if (changed) StateChanged();
            gate.Release();
        }
    }

    public void SetRecording(bool recording)
    {
        recording = recording && IsAvailable;
        if (IsRecording == recording) return;
        IsRecording = recording;
        Changed(IsRecordingChangedArgs);
    }
    public void SetGlobalErrors(IReadOnlyDictionary<MediaShortcutAction, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (GlobalErrors.Count == errors.Count && errors.All(item =>
            GlobalErrors.TryGetValue(item.Key, out var value) && value == item.Value)) return;
        GlobalErrors = new ReadOnlyDictionary<MediaShortcutAction, string>(new Dictionary<MediaShortcutAction, string>(errors));
        Changed(GlobalErrorsChangedArgs);
    }
    public void RequestRegistrationRefresh()
    {
        if (IsAvailable && Preferences.GlobalEnabled && !IsRecording)
            RegistrationRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (!stopping)
        {
            stopping = true;
            SetRecording(false);
            StateChanged();
        }
        await gate.WaitAsync();
        gate.Release();
    }

    private static MediaControlPreferences? ReadFromDb(DatabaseSession db)
    {
        if (!db.TryQuerySingle(
            "SELECT global_enabled,system_media_enabled FROM windows_app_media_controls WHERE singleton=1",
            row => (Global: row.GetBool(0), System: row.GetBool(1)), out var head))
            return null;

        var bindings = db.Query(
            "SELECT action,local_key,local_modifiers,global_key,global_modifiers FROM windows_app_media_bindings ORDER BY position,action",
            row => new MediaShortcutBinding(
                Enum.Parse<MediaShortcutAction>(row.GetString(0)),
                DecodeGesture(row.GetNullableString(1), row.GetNullableInt32(2)),
                DecodeGesture(row.GetNullableString(3), row.GetNullableInt32(4))));

        return Snapshot(new MediaControlPreferences(head.Global, head.System, bindings));

        static MediaShortcutGesture? DecodeGesture(string? keyText, int? modifiers) =>
            keyText is not null && modifiers is not null && Enum.TryParse<Key>(keyText, out var key)
                ? new MediaShortcutGesture(key, (ModifierKeys)modifiers.Value)
                : null;
    }
}
