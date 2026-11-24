using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public partial class UserDataResetDialog : UserControl, IDesktopDebugModal
{
    internal event Action? ConfirmRequested;
    internal event Action? CancelRequested;
    internal event Action? ExitRequested;
    internal bool IsOpen => Parent is DesktopModalHost host && host.IsPresented(this);
    internal bool IsPending { get; private set; }
    internal bool IsConfirming => confirming;
    internal FrameworkElement SurfaceElement => DialogSurface;
    private bool confirming;

    public UserDataResetDialog() => InitializeComponent();

    internal void PrepareOpen()
    {
        confirming = true;
        IsPending = false;
        DialogTitle.Text = "清除全部数据？";
        ConfirmationText.Visibility = CancelChoice.Visibility = DismissButton.Visibility = Visibility.Visible;
        ExitChoice.Visibility = Visibility.Collapsed;
        ConfirmChoice.Content = "清除全部数据";
        AutomationProperties.SetName(ConfirmChoice, "清除全部数据");
        ConfirmChoice.IsEnabled = true;
        FailureNotice.Clear();
    }

    internal void FocusInitial()
    {
        // The newly attached surface must participate in layout before
        // WinUI can move focus to its safe initial choice.
        UpdateLayout();
        CancelChoice.Focus(FocusState.Programmatic);
    }

    internal void SetBusy(string title)
    {
        confirming = false;
        IsPending = true;
        DialogTitle.Text = title;
        ConfirmationText.Visibility = CancelChoice.Visibility = DismissButton.Visibility = Visibility.Collapsed;
        ExitChoice.Visibility = Visibility.Visible;
        ConfirmChoice.Content = "重试清除";
        AutomationProperties.SetName(ConfirmChoice, "重试清除");
        ConfirmChoice.IsEnabled = ExitChoice.IsEnabled = false;
        FailureNotice.Clear();
        DialogFocusTarget.Focus(FocusState.Programmatic);
    }

    internal void ShowFailure(string message)
    {
        IsPending = false;
        DialogTitle.Text = "清除未完成";
        ConfirmChoice.IsEnabled = ExitChoice.IsEnabled = true;
        FailureNotice.Show(this, message, persistent: true);
        DialogFocusTarget.Focus(FocusState.Programmatic);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (IsOpen && !IsPending) ConfirmRequested?.Invoke();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (IsOpen && !confirming && !IsPending) ExitRequested?.Invoke();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (IsOpen && confirming && !IsPending) CancelRequested?.Invoke();
    }

    DesktopDebugDialogDescription IDesktopDebugModal.DescribeDebugDialog()
    {
        bool enabled = !IsPending;
        return new(
            Id: "user-data-reset",
            Kind: "user-data-reset",
            FallbackTitle: confirming ? "清除全部本机数据" : "清除未完成",
            Message: confirming ? "清空本机数据确认框" : "清空本机数据失败提示框",
            Surface: DialogSurface,
            ExplicitButtons: confirming
                ? [
                    new { index = 0, label = "取消", automationName = "取消", enabled, kind = "cancel" },
                    new { index = 1, label = "确认清除", automationName = "清除全部数据", enabled, kind = "confirm" }
                ]
                : [
                    new { index = 0, label = "退出", automationName = "退出", enabled, kind = "exit" },
                    new { index = 1, label = "重试清除", automationName = "重试清除", enabled, kind = "confirm" }
                ]);
    }

    object IDesktopDebugModal.HandleDebugAction(string act, int? btnIndex, string? btnLabel)
    {
        if (act == "button")
        {
            if (confirming)
            {
                if (btnIndex == 0 || string.Equals(btnLabel, "取消", StringComparison.OrdinalIgnoreCase)) act = "cancel";
                else if (btnIndex == 1 || string.Equals(btnLabel, "确认清除", StringComparison.OrdinalIgnoreCase) || string.Equals(btnLabel, "清除全部数据", StringComparison.OrdinalIgnoreCase)) act = "confirm";
            }
            else
            {
                if (btnIndex == 0 || string.Equals(btnLabel, "退出", StringComparison.OrdinalIgnoreCase)) act = "exit";
                else if (btnIndex == 1 || string.Equals(btnLabel, "重试清除", StringComparison.OrdinalIgnoreCase) || string.Equals(btnLabel, "确认清除", StringComparison.OrdinalIgnoreCase)) act = "confirm";
            }
        }
        if (act is "cancel" or "dismiss" or "close")
        {
            if (!confirming || IsPending)
                throw DebugApiException.InvalidState("User data reset dialog cannot be cancelled in its current state.");
            CancelRequested?.Invoke();
            return new { dialog = "user-data-reset", action = "cancel" };
        }
        if (act == "exit")
        {
            if (confirming || IsPending)
                throw DebugApiException.InvalidState("Exit action is only available after reset failure.");
            ExitRequested?.Invoke();
            return new { dialog = "user-data-reset", action = "exit" };
        }
        if (act == "confirm")
        {
            if (IsPending)
                throw DebugApiException.InvalidState("User data reset is currently in progress.");
            ConfirmRequested?.Invoke();
            return new { dialog = "user-data-reset", action = "confirm" };
        }
        throw DebugApiException.InvalidArgument($"Unsupported action '{act}' for dialog 'user-data-reset'.");
    }
}
