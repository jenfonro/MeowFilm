using System.ComponentModel;
using System.Text.Json;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppClosePreferences(IDocumentStore store) : IDesktopClosePreferences, IDesktopShutdownParticipant
{
    public const string DocumentNamespace = "app.window.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private bool loaded, busy, stopping;
    public WindowCloseBehavior Behavior { get; private set; } = WindowCloseBehavior.Ask;
    public string Status { get; private set; } = "";
    public bool CanEdit => loaded && !busy && !stopping;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed()
    {
        foreach (var property in new[] { nameof(Behavior), nameof(Status), nameof(CanEdit) })
            PropertyChanged?.Invoke(this, new(property));
    }
    private async Task Read()
    {
        var snapshot = await store.ReadAsync(DocumentNamespace);
        var behavior = WindowCloseBehavior.Ask;
        if (snapshot.Data is { } bytes)
        {
            using var doc = JsonDocument.Parse(bytes);
            var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
                !value.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
                !value.TryGetProperty("closeBehavior", out var saved))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            behavior = saved.GetString() switch
            {
                "ask" => WindowCloseBehavior.Ask,
                "minimizeToTray" => WindowCloseBehavior.MinimizeToTray,
                "exit" => WindowCloseBehavior.Exit,
                _ => throw new DocumentStoreException(DocumentStoreError.Corrupt)
            };
        }
        fingerprint = snapshot.Fingerprint;
        Behavior = behavior;
        loaded = true;
    }
    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (stopping) return;
            busy = true; Changed();
            await Read();
            Status = "";
        }
        catch { loaded = false; Status = "窗口行为读取失败"; }
        finally { busy = false; Changed(); gate.Release(); }
    }
    public async Task<bool> SaveAsync(WindowCloseBehavior behavior)
    {
        if (!Enum.IsDefined(behavior)) throw new ArgumentOutOfRangeException(nameof(behavior));
        if (!CanEdit) return false;
        await gate.WaitAsync();
        try
        {
            if (!CanEdit) return false;
            if (Behavior == behavior) { Status = ""; return true; }
            busy = true; Status = ""; Changed();
            string saved = behavior switch
            {
                WindowCloseBehavior.Ask => "ask",
                WindowCloseBehavior.MinimizeToTray => "minimizeToTray",
                WindowCloseBehavior.Exit => "exit",
                _ => throw new ArgumentOutOfRangeException(nameof(behavior))
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, closeBehavior = saved });
            try
            {
                fingerprint = await store.WriteAsync(DocumentNamespace, bytes, fingerprint);
                Behavior = behavior;
                return true;
            }
            catch
            {
                // A failed receipt can follow acceptance. Only the authority can
                // confirm that the requested behavior was actually saved.
                try
                {
                    await Read();
                    bool savedBehavior = Behavior == behavior;
                    Status = savedBehavior ? "" : "窗口行为未保存，请重试";
                    return savedBehavior;
                }
                catch { loaded = false; Status = "窗口行为保存失败，请重新读取"; return false; }
            }
        }
        finally { busy = false; Changed(); gate.Release(); }
    }
    public async Task PrepareShutdownAsync()
    {
        stopping = true; Changed();
        await gate.WaitAsync();
        gate.Release();
    }
    public Task CancelShutdownPreparationAsync()
    { stopping = false; Changed(); return Task.CompletedTask; }
    public Task AbortAsync() => PrepareShutdownAsync();
    public async ValueTask DisposeAsync() => await PrepareShutdownAsync();
}
