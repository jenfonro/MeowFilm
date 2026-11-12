using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeowFilm.Desktop;

internal partial class UserDataResetDialog : UserControl
{
    internal event Action? ConfirmRequested;
    internal event Action? CancelRequested;
    internal event Action? ExitRequested;
    internal bool IsOpen => Visibility == Visibility.Visible;
    internal bool IsPending { get; private set; }
    private bool confirming;

    public UserDataResetDialog() => InitializeComponent();

    internal void Open()
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
        Visibility = Visibility.Visible;
        // Retain the safe initial choice even while the host window is inactive.
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(CancelChoice), CancelChoice);
        CancelChoice.Focus();
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
        DialogSurface.Focus();
    }

    internal void ShowFailure(string message)
    {
        IsPending = false;
        DialogTitle.Text = "清除未完成";
        ConfirmChoice.IsEnabled = ExitChoice.IsEnabled = true;
        FailureNotice.Show(this, message);
        DialogSurface.Focus();
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        OnCancelClick(sender, e);
    }
}
