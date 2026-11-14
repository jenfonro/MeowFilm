using System.Windows.Input;

namespace MeowFilm.Desktop;

public record NavigationItem(
    string Route,
    string Title,
    string Glyph,
    string? Group = null,
    bool ShowInSidebar = true,
    DesktopNavigationGroupAction? GroupAction = null)
{
    public string? IconData { get; init; }
}

public sealed class DesktopNavigationGroupAction : ICommand
{
    private readonly Action execute;
    private readonly Func<bool> canExecute;

    public DesktopNavigationGroupAction(string label, Action execute,
        Func<bool>? canExecute = null, string? automationName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? (() => true);
        AutomationName = string.IsNullOrWhiteSpace(automationName) ? label : automationName;
    }

    public string AutomationName { get; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
