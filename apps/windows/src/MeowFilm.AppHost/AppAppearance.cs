using System.ComponentModel;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppAppearance(IStorageDatabase database) : IDesktopAppearance, IAsyncDisposable
{
    public const string DocumentNamespace = "app.appearance.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
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
        var (saved, revision) = await database.ReadSnapshotAsync(db =>
        {
            string? value = db.TryQuerySingle(
                "SELECT theme_color FROM windows_app_appearance WHERE singleton=1",
                row => row.GetString(0), out var rowColor)
                ? rowColor : null;
            return (value, db.Revision(DocumentNamespace));
        });
        string color = AppearanceColors.DefaultColor;
        if (saved is not null)
        {
            if (!AppearanceColors.TryParse(saved, out var parsed))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            color = AppearanceColors.Hex(parsed);
        }
        storageRevision = revision;
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
            try
            {
                var expected = storageRevision;
                storageRevision = await database.WriteAsync(db =>
                {
                    if (db.Revision(DocumentNamespace) != expected)
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                    string? current = db.TryQuerySingle(
                        "SELECT theme_color FROM windows_app_appearance WHERE singleton=1",
                        row => row.GetString(0), out var existing)
                        ? existing : null;
                    if (string.Equals(current, color, StringComparison.Ordinal)) return expected;
                    db.Execute("""
                        INSERT INTO windows_app_appearance(singleton,theme_color) VALUES(1,$color)
                        ON CONFLICT(singleton) DO UPDATE SET theme_color=excluded.theme_color
                        """, ("$color", color));
                    return db.CommitRevision(DocumentNamespace, expected);
                });
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

    public Task AbortAsync() => PrepareShutdownAsync();
    public async ValueTask DisposeAsync() => await PrepareShutdownAsync();
}
