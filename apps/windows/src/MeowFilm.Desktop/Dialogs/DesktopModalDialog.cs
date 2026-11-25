using Microsoft.UI.Xaml;

namespace MeowFilm.Desktop;

public enum DesktopDialogDefaultButton { None, Primary, Secondary, Close }

public sealed class DesktopDialogRequest
{
    private bool isPrimaryEnabled = true;
    public string Title { get; init; } = "";
    public string? Message { get; init; }
    public UIElement? Content { get; init; }
    public string? PrimaryButtonText { get; init; }
    public string? SecondaryButtonText { get; init; }
    public string? CloseButtonText { get; init; } = "取消";
    public DesktopDialogDefaultButton DefaultButton { get; init; } = DesktopDialogDefaultButton.Close;
    public double MinWidth { get; init; } = 320;
    public double MaxWidth { get; init; } = 460;
    public bool DismissOnOutside { get; init; } = true;
    public Func<Task<bool>>? OnPrimaryAsync { get; init; }
    public Action? OnOpened { get; init; }
    public Func<bool>? CanRestoreFocus { get; init; }

    public bool IsPrimaryEnabled
    {
        get => isPrimaryEnabled;
        set
        {
            if (isPrimaryEnabled == value) return;
            isPrimaryEnabled = value;
            PrimaryEnabledChanged?.Invoke(value);
        }
    }

    internal event Action<bool>? PrimaryEnabledChanged;
}

internal readonly record struct DesktopDebugButton(
    string Label,
    string Action,
    bool Enabled,
    string? AutomationName = null);

internal sealed record DesktopDebugDialogDescription(
    string Id,
    string Kind,
    string FallbackTitle,
    string Message,
    FrameworkElement Surface,
    DesktopDebugButton[] Buttons);

internal interface IDesktopDebugModal
{
    DesktopDebugDialogDescription DescribeDebugDialog();
    object HandleDebugAction(string action);
}
