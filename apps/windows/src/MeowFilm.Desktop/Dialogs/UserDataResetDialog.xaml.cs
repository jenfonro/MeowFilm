using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

public partial class UserDataResetDialog : UserControl
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
        // The previously collapsed surface must participate in layout before
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
        FailureNotice.Show(this, message);
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

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || DesktopVisual.Modifiers != VirtualKeyModifiers.None) return;
        e.Handled = true;
        OnCancelClick(sender, new RoutedEventArgs());
    }
}
