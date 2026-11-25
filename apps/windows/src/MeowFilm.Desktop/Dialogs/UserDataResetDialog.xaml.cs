using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public partial class UserDataResetDialog : UserControl, IDesktopDebugModal
{
    private bool confirming;

    internal event Action? ConfirmRequested;
    internal event Action? CancelRequested;
    internal event Action? ExitRequested;
    internal bool IsOpen => Parent is DesktopModalHost host && host.IsPresented(this);
    internal bool IsPending { get; private set; }
    internal FrameworkElement SurfaceElement => DialogSurface;

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

    DesktopDebugDialogDescription IDesktopDebugModal.DescribeDebugDialog() => new(
        Id: "user-data-reset",
        Kind: "user-data-reset",
        FallbackTitle: confirming ? "清除全部本机数据" : "清除未完成",
        Message: confirming ? "清空本机数据确认框" : "清空本机数据失败提示框",
        Surface: DialogSurface,
        Buttons: confirming
            ? [new("取消", "cancel", !IsPending), new("确认清除", "confirm", !IsPending, "清除全部数据")]
            : [new("退出", "exit", !IsPending), new("重试清除", "confirm", !IsPending)]);

    object IDesktopDebugModal.HandleDebugAction(string act)
    {
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
