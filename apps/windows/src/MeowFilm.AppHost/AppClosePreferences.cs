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
    private void Changed()
    {
        foreach (var property in new[] { nameof(Behavior), nameof(Status), nameof(CanEdit) })
            PropertyChanged?.Invoke(this, new(property));
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
        var behavior = WindowCloseBehavior.Ask;
        if (saved is not null)
        {
            behavior = saved switch
            {
                "ask" => WindowCloseBehavior.Ask,
                "minimizeToTray" => WindowCloseBehavior.MinimizeToTray,
                "exit" => WindowCloseBehavior.Exit,
                _ => throw new StorageException(StorageError.Corrupt)
            };
        }
        storageRevision = revision;
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
            try
            {
                var expected = storageRevision;
                storageRevision = await database.WriteAsync(db =>
                {
                    if (db.Revision(StorageOwner) != expected)
                        throw new StorageException(StorageError.Conflict);
                    string? current = db.TryQuerySingle(
                        "SELECT close_behavior FROM windows_app_window WHERE singleton=1",
                        row => row.GetString(0), out var existing)
                        ? existing : null;
                    if (string.Equals(current, saved, StringComparison.Ordinal)) return expected;
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
                // A storage failure can follow an accepted CAS. Only the authority can
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

    public Task AbortAsync() => PrepareShutdownAsync();
    public async ValueTask DisposeAsync() => await PrepareShutdownAsync();
}
