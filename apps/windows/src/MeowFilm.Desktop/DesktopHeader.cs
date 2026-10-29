using System.Windows;

namespace MeowFilm.Desktop;

/// <summary>
/// Narrow page contribution consumed by the one shell-owned header.
/// Contributors never supply another header, search box, or back button.
/// </summary>
public interface IDesktopHeaderSource
{
    DesktopHeaderContribution Header { get; }
    event EventHandler? HeaderChanged;
}

public sealed record DesktopHeaderContribution(
    string Title,
    string Subtitle = "",
    bool SearchVisible = true,
    bool SearchEnabled = true,
    IReadOnlyList<DesktopHeaderAction>? Actions = null,
    FrameworkElement? Slot = null)
{
    public IReadOnlyList<DesktopHeaderAction> HeaderActions => Actions ?? [];
}

public sealed class DesktopHeaderAction
{
    private readonly Func<CancellationToken, Task> execute;
    private readonly Func<bool> canExecute;

    public DesktopHeaderAction(string id, string label, Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null, bool isPrimary = false, string? automationName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? (() => true);
        Id = id;
        Label = label;
        IsPrimary = isPrimary;
        AutomationName = string.IsNullOrWhiteSpace(automationName) ? label : automationName;
    }

    public string Id { get; }
    public string Label { get; }
    public string AutomationName { get; }
    public bool IsPrimary { get; }
    public bool IsEnabled => canExecute();
    public Task ExecuteAsync(CancellationToken cancellationToken) => execute(cancellationToken);
}
