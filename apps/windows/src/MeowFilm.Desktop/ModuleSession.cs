using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MeowFilm.Desktop;

internal sealed class ModuleSession(IDesktopModule module)
{
    public IDesktopModule Module { get; } = module;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Initialized { get; set; }
    public bool Mounted { get; set; }
    public bool IsSettingsOpen { get; set; }
    public string ActiveSearchKey { get; set; } = "default";
    public Dictionary<string, string> SearchDrafts { get; } = new(StringComparer.Ordinal);
    public Destination? Current { get; set; }
    public IInputElement? ContentFocus { get; set; }
    public ObservableCollection<NavigationItem>? Navigation { get; set; }
    public Grid Surface { get; } = new() { Visibility = Visibility.Collapsed, DataContext = module };
    public ContentControl MainHost { get; } = CreateHost(module);
    public ContentControl SettingsHost { get; } = CreateHost(module);

    private static ContentControl CreateHost(IDesktopModule owner) => new()
    {
        DataContext = owner,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        Focusable = false,
        IsTabStop = false
    };
}

internal sealed record Destination(string Value, bool IsSearch = false);
internal sealed record RequestPlan(ModuleSession Session, Destination? Destination = null, bool IsBack = false);

internal sealed class ShellRequest(RequestPlan plan) : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private Task _cancellation = Task.CompletedTask;
    private bool _disposed;
    public RequestPlan Plan { get; } = plan;

    // Access the token before starting work; CancelAsync can then be safely observed independently.
    public CancellationToken GetToken() => _source.Token;
    public Task Cancellation => _cancellation;

    public void Cancel()
    {
        if (_disposed || _source.IsCancellationRequested) return;
        try { _cancellation = _source.CancelAsync(); }
        catch (ObjectDisposedException) { }
        // Cancellation callbacks are supplied by modules. Their exceptions must never escape a UI event.
        _ = ObserveAsync(_cancellation);
    }

    public void Dispose()
    {
        _disposed = true;
        _source.Dispose();
    }

    internal static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* No raw exceptions, tokens, URLs or credentials enter UI/log output. */ }
    }
}
