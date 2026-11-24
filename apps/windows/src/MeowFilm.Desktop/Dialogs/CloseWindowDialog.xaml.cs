using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public partial class CloseWindowDialog : UserControl, IDesktopDebugModal
{
    internal event Action<WindowCloseBehavior>? ChoiceRequested;
    internal event Action? CancelRequested;
    internal event Action? ForceDismissRequested;
    internal bool IsOpen => Parent is DesktopModalHost host && host.IsPresented(this);
    internal bool IsPending { get; private set; }
    internal bool ShouldRemember => RememberChoice.IsChecked == true;
    internal FrameworkElement SurfaceElement => DialogSurface;

    public CloseWindowDialog() => InitializeComponent();

    internal void PrepareOpen()
    {
        RememberChoice.IsChecked = false;
        SetPending(false);
    }

    internal void FocusInitial()
    {
        UpdateLayout();
        CancelChoice.Focus(FocusState.Programmatic);
    }

    internal void SetPending(bool pending)
    {
        IsPending = pending;
        RememberChoice.IsEnabled = DismissButton.IsEnabled = !pending;
        foreach (var choice in Choices.Children.OfType<Control>()) choice.IsEnabled = !pending;
    }

    private void OnChoiceClick(object sender, RoutedEventArgs e)
    {
        if (!IsPending && sender is Button { Tag: string choice } && Enum.TryParse<WindowCloseBehavior>(choice, out var behavior))
            ChoiceRequested?.Invoke(behavior);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => TryCancel();

    internal bool TryCancel()
    {
        if (!IsOpen || IsPending) return false;
        CancelRequested?.Invoke();
        return true;
    }

    DesktopDebugDialogDescription IDesktopDebugModal.DescribeDebugDialog() => new(
        Id: "close-window",
        Kind: "close-window",
        FallbackTitle: "关闭主窗口",
        Message: "关闭行为选择框",
        Surface: DialogSurface,
        ExplicitButtons:
        [
            new { index = 0, label = "取消", automationName = "取消", enabled = !IsPending, kind = "cancel" },
            new { index = 1, label = "最小化到托盘", automationName = "最小化到托盘", enabled = !IsPending, kind = "tray" },
            new { index = 2, label = "退出应用", automationName = "退出", enabled = !IsPending, kind = "exit" }
        ]);

    object IDesktopDebugModal.HandleDebugAction(string act, int? btnIndex, string? btnLabel)
    {
        if (act == "button")
        {
            if (btnIndex == 0 || string.Equals(btnLabel, "取消", StringComparison.OrdinalIgnoreCase)) act = "cancel";
            else if (btnIndex == 1 || string.Equals(btnLabel, "最小化到托盘", StringComparison.OrdinalIgnoreCase)) act = "tray";
            else if (btnIndex == 2 || string.Equals(btnLabel, "退出应用", StringComparison.OrdinalIgnoreCase) || string.Equals(btnLabel, "退出", StringComparison.OrdinalIgnoreCase)) act = "exit";
        }
        if (act is "cancel" or "dismiss" or "close")
        {
            ForceDismissRequested?.Invoke();
            return new { dialog = "close-window", action = "cancel" };
        }
        if (act is "confirm" or "exit")
        {
            ChoiceRequested?.Invoke(WindowCloseBehavior.Exit);
            return new { dialog = "close-window", action = "exit" };
        }
        if (act is "tray" or "minimize")
        {
            ChoiceRequested?.Invoke(WindowCloseBehavior.MinimizeToTray);
            return new { dialog = "close-window", action = "tray" };
        }
        throw DebugApiException.InvalidArgument($"Unsupported action '{act}' for dialog 'close-window'.");
    }
}
