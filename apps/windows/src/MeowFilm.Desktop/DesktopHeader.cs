using System.Windows;

namespace MeowFilm.Desktop;

/// <summary>
/// Page contribution consumed by a shared presentation host.
/// A slot can compose page-specific controls without duplicating navigation or window chrome.
/// A mounted ordinary capability with enabled search becomes the owner of the retained shell search.
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

    // Automatic preserves existing consumers: beside a visible heading, otherwise before actions.
    public DesktopHeaderSlotPlacement SlotPlacement { get; init; } = DesktopHeaderSlotPlacement.Automatic;
    public string? SearchTargetId { get; init; }
    public string? SearchContextId { get; init; }
}

public enum DesktopHeaderSlotPlacement { Automatic, Leading, Trailing }

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
