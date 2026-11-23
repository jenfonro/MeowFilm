using System.ComponentModel;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppAppearance(IStorageDatabase database) : IDesktopAppearance, IAsyncDisposable
{
    public const string StorageOwner = "app.appearance.v1";
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
        var (saved, revision) = await database.ReadSnapshotAsync(db =>
        {
            string? value = db.TryQuerySingle(
                "SELECT theme_color FROM windows_app_appearance WHERE singleton=1",
                row => row.GetString(0), out var rowColor)
                ? rowColor : null;
            return (value, db.Revision(StorageOwner));
        });
        string color = AppearanceColors.DefaultColor;
        if (saved is not null)
        {
            if (!AppearanceColors.TryParse(saved, out var parsed))
                throw new StorageException(StorageError.Corrupt);
            color = AppearanceColors.Hex(parsed);
        }
        storageRevision = revision;
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
            try
            {
                var expected = storageRevision;
                storageRevision = await database.WriteAsync(db =>
                {
                    if (db.Revision(StorageOwner) != expected)
                        throw new StorageException(StorageError.Conflict);
                    db.Execute("""
                        INSERT INTO windows_app_appearance(singleton,theme_color) VALUES(1,$color)
                        ON CONFLICT(singleton) DO UPDATE SET theme_color=excluded.theme_color
                        """, ("$color", color));
                    return db.CommitRevision(StorageOwner, expected);
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
