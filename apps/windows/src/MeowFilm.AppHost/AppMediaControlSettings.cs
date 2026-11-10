using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppMediaControlSettings(IDocumentStore store) : IDesktopMediaControlSettings, IDesktopShutdownParticipant
{
    public const string DocumentNamespace = "app.media-controls.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
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
        var snapshot = await store.ReadAsync(DocumentNamespace);
        var preferences = snapshot.Data is { } bytes ? Decode(bytes) : MediaControlDefaults.Preferences;
        fingerprint = snapshot.Fingerprint;
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
                fingerprint = await store.WriteAsync(DocumentNamespace, Encode(draft), fingerprint);
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
    public Task CancelShutdownPreparationAsync()
    {
        if (!disposed) { stopping = false; StateChanged(); }
        return Task.CompletedTask;
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

    private static byte[] Encode(MediaControlPreferences value)
    {
        static object? Gesture(MediaShortcutGesture? gesture) => gesture is null ? null :
            new { key = gesture.Key.ToString(), modifiers = (int)gesture.Modifiers };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            globalEnabled = value.GlobalEnabled,
            systemMediaEnabled = value.SystemMediaEnabled,
            bindings = value.Bindings.Select(binding => new
            {
                action = binding.Action.ToString(), local = Gesture(binding.Local), global = Gesture(binding.Global)
            })
        });
    }
    private static MediaControlPreferences Decode(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!Fields(root, "schema", "globalEnabled", "systemMediaEnabled", "bindings") ||
            root.GetProperty("schema").GetInt32() != 1 ||
            root.GetProperty("bindings").ValueKind != JsonValueKind.Array)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        var bindings = new List<MediaShortcutBinding>();
        foreach (var binding in root.GetProperty("bindings").EnumerateArray())
        {
            if (!Fields(binding, "action", "local", "global") ||
                !Enum.TryParse<MediaShortcutAction>(binding.GetProperty("action").GetString(), out var action) ||
                !Enum.IsDefined(action) || action.ToString() != binding.GetProperty("action").GetString())
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            bindings.Add(new(action, Gesture(binding.GetProperty("local")), Gesture(binding.GetProperty("global"))));
        }
        var value = new MediaControlPreferences(root.GetProperty("globalEnabled").GetBoolean(),
            root.GetProperty("systemMediaEnabled").GetBoolean(), bindings);
        if (MediaShortcutGestures.ValidationError(value) is not null)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return Snapshot(value);

        static MediaShortcutGesture? Gesture(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null) return null;
            if (!Fields(element, "key", "modifiers") ||
                !Enum.TryParse<Key>(element.GetProperty("key").GetString(), out var key) ||
                key.ToString() != element.GetProperty("key").GetString())
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return new(key, (ModifierKeys)element.GetProperty("modifiers").GetInt32());
        }
    }
    private static bool Fields(JsonElement value, params string[] fields) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == fields.Length &&
        fields.All(field => value.TryGetProperty(field, out _));
}
