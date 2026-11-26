using System.ComponentModel;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed class AppClosePreferences(IStorageDatabase database) : IDesktopClosePreferences, IAsyncDisposable
{
    public const string StorageOwner = "app.window.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
    private bool loaded, busy, stopping;
    public WindowCloseBehavior Behavior { get; private set; } = WindowCloseBehavior.Ask;
    public string Status { get; private set; } = "";
    public bool CanEdit => loaded && !busy && !stopping;
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly PropertyChangedEventArgs[] AllProperties =
        [new(nameof(Behavior)), new(nameof(Status)), new(nameof(CanEdit))];
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
                "SELECT close_behavior FROM windows_app_window WHERE singleton=1",
                row => row.GetString(0), out var rowValue)
                ? rowValue : null;
            return (value, db.Revision(StorageOwner));
        });
        storageRevision = revision;
        Behavior = saved switch
        {
            "minimizeToTray" => WindowCloseBehavior.MinimizeToTray,
            "exit" => WindowCloseBehavior.Exit,
            _ => WindowCloseBehavior.Ask
        };
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
        catch { loaded = false; Status = "窗口行为读取失败"; }
        finally
        {
            busy = false;
            if (changed) Changed();
            gate.Release();
        }
    }
    public async Task<bool> SaveAsync(WindowCloseBehavior behavior)
    {
        if (!Enum.IsDefined(behavior)) throw new ArgumentOutOfRangeException(nameof(behavior));
        if (!CanEdit) return false;
        await gate.WaitAsync();
        bool changed = false;
        try
        {
            if (!CanEdit) return false;
            if (Behavior == behavior)
            {
                if (Status.Length > 0) { Status = ""; changed = true; }
                return true;
            }
            busy = true; Status = ""; changed = true; Changed();
            string saved = behavior switch
            {
                WindowCloseBehavior.Ask => "ask",
                WindowCloseBehavior.MinimizeToTray => "minimizeToTray",
                WindowCloseBehavior.Exit => "exit",
                _ => throw new ArgumentOutOfRangeException(nameof(behavior))
            };
            try
            {
                var expected = storageRevision;
                storageRevision = await database.WriteAsync(db =>
                {
                    if (db.Revision(StorageOwner) != expected)
                        throw new StorageException(StorageError.Conflict);
                    db.Execute("""
                        INSERT INTO windows_app_window(singleton,close_behavior) VALUES(1,$behavior)
                        ON CONFLICT(singleton) DO UPDATE SET close_behavior=excluded.close_behavior
                        """, ("$behavior", saved));
                    return db.CommitRevision(StorageOwner, expected);
                });
                Behavior = behavior;
                return true;
            }
            catch
            {
                Status = "窗口行为保存失败";
                return false;
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
