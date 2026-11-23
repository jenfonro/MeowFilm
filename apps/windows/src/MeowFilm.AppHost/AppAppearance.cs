using System.ComponentModel;
using System.Text.Json;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppAppearance(IStorageDatabase database) : IDesktopAppearance, IAsyncDisposable
{
    private readonly TypedRecordRepository store = new(database, DocumentNamespace, AppStorageRecords.Appearance);
    public const string DocumentNamespace = "app.appearance.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
    private bool loaded, busy, stopping;
    public string ThemeColor { get; private set; } = AppearanceColors.DefaultColor;
    public string Status { get; private set; } = "";
    public bool CanEdit => loaded && !busy && !stopping;
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly PropertyChangedEventArgs[] AllProperties =
        [new(nameof(ThemeColor)), new(nameof(Status)), new(nameof(CanEdit))];
    private void Changed()
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        foreach (var args in AllProperties) handler(this, args);
    }
    private async Task Read()
    {
        var snapshot = await store.ReadAsync();
        string color = AppearanceColors.DefaultColor;
        if (snapshot.Data is { } bytes)
        {
            using var doc = JsonDocument.Parse(bytes);
            var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
                !value.TryGetProperty("themeColor", out var saved) ||
                !AppearanceColors.TryParse(saved.GetString(), out var parsed))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            color = AppearanceColors.Hex(parsed);
        }
        storageRevision = snapshot.Revision;
        ThemeColor = color;
        loaded = true;
    }
    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        bool changed = false;
        try
        {
            if (stopping) return;
            busy = true; changed = true; Changed();
            await Read();
            Status = "";
        }
        catch { loaded = false; Status = "主题设置读取失败"; }
        finally
        {
            busy = false;
            if (changed) Changed();
            gate.Release();
        }
    }
    public async Task SaveAsync(string color)
    {
        if (!AppearanceColors.TryParse(color, out var parsed)) throw new ArgumentException("Invalid color.", nameof(color));
        color = AppearanceColors.Hex(parsed);
        if (!CanEdit) return;
        await gate.WaitAsync();
        bool changed = false;
        try
        {
            if (!CanEdit || ThemeColor == color) return;
            busy = true; Status = ""; changed = true; Changed();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, themeColor = color });
            try
            {
                storageRevision = await store.WriteAsync(bytes, storageRevision);
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
        finally
        {
            busy = false;
            if (changed) Changed();
            gate.Release();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!stopping)
        {
            stopping = true;
            Changed();
        }
        await gate.WaitAsync();
        gate.Release();
    }
}
