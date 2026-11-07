using System.ComponentModel;
using System.Text.Json;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppAppearance(IDocumentStore store) : IDesktopAppearance, IDesktopShutdownParticipant
{
    public const string DocumentNamespace = "app.appearance.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private bool loaded, busy, stopping;
    public string ThemeColor { get; private set; } = AppearanceColors.DefaultColor;
    public string Status { get; private set; } = "";
    public bool CanEdit => loaded && !busy && !stopping;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed()
    {
        foreach (var property in new[] { nameof(ThemeColor), nameof(Status), nameof(CanEdit) })
            PropertyChanged?.Invoke(this, new(property));
    }
    private async Task Read()
    {
        var snapshot = await store.ReadAsync(DocumentNamespace);
        string color = AppearanceColors.DefaultColor;
        if (snapshot.Data is { } bytes)
        {
            using var doc = JsonDocument.Parse(bytes);
            var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
                !value.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
                !value.TryGetProperty("themeColor", out var saved) ||
                !AppearanceColors.TryParse(saved.GetString(), out var parsed))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            color = AppearanceColors.Hex(parsed);
        }
        fingerprint = snapshot.Fingerprint;
        ThemeColor = color;
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
        catch { loaded = false; Status = "主题设置读取失败"; }
        finally { busy = false; Changed(); gate.Release(); }
    }
    public async Task SaveAsync(string color)
    {
        if (!AppearanceColors.TryParse(color, out var parsed)) throw new ArgumentException("Invalid color.", nameof(color));
        color = AppearanceColors.Hex(parsed);
        if (!CanEdit) return;
        await gate.WaitAsync();
        try
        {
            if (!CanEdit || ThemeColor == color) return;
            busy = true; Status = ""; Changed();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, themeColor = color });
            try
            {
                fingerprint = await store.WriteAsync(DocumentNamespace, bytes, fingerprint);
                ThemeColor = color;
            }
            catch
            {
                // A storage failure can follow acceptance. Read the authority,
                // never overwrite it or pretend the in-memory draft was saved.
                try { await Read(); Status = ThemeColor == color ? "" : "主题设置未保存，请重试"; }
                catch { loaded = false; Status = "主题设置保存失败，请重新读取"; }
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
