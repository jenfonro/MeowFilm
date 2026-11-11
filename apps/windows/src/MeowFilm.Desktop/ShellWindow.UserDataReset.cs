using System.Windows;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly Func<Task>? _resetUserData;
    private IInputElement? _resetFocus;
    private bool _userDataResetStarted, _userDataResetBusy;

    private void ConfirmUserDataReset()
    {
        if (_resetUserData is null || _isClosing || _isClosed || _sessionEnding ||
            _closeChoiceQueued || CloseDialog.IsOpen || ResetDialog.IsOpen) return;
        _resetFocus = Keyboard.FocusedElement;
        RootLayout.IsEnabled = NotificationOverlay.IsEnabled = false;
        ResetDialog.Open();
    }

    private void CancelUserDataReset()
    {
        if (_userDataResetStarted || !ResetDialog.IsOpen || _isClosing || _sessionEnding) return;
        ResetDialog.Visibility = Visibility.Collapsed;
        RootLayout.IsEnabled = NotificationOverlay.IsEnabled = true;
        var focus = _resetFocus;
        _resetFocus = null;
        if (focus is UIElement { IsEnabled: true, IsVisible: true } target) target.Focus();
    }

    private async void OnUserDataResetConfirmed()
    {
        if (_resetUserData is null || _userDataResetBusy || _isClosed || _sessionEnding || !ResetDialog.IsOpen) return;
        _userDataResetBusy = _userDataResetStarted = true;
        _resetFocus = null;
        _isClosing = true;
        _state.IsInteractive = false;
        _state.SetShutdownConfirmation(false);
        _retryPlan = null;
        Interlocked.Increment(ref _presentationGeneration);
        RetireHeaderPresentation();
        SetStatus();
        CancelActive();
        CancelPageActivation();
        foreach (var request in _requests.ToArray()) request.Cancel();
        ResetDialog.SetBusy("正在清除…");
        try
        {
            // The App owns request persistence, actual release, deletion and
            // same-process replacement. Its successful callback closes this shell.
            await _resetUserData();
            ShowUserDataResetFailure("清除尚未完成，请重试");
        }
        catch { ShowUserDataResetFailure("清除未完成，请重试"); }
        finally { _userDataResetBusy = false; }
    }

    private async void OnUserDataResetExit()
    {
        if (!_userDataResetStarted || _userDataResetBusy || _isClosed || _sessionEnding || !ResetDialog.IsOpen) return;
        _userDataResetBusy = true;
        ResetDialog.SetBusy("正在退出…");
        try
        {
            await AbortAsync();
            ShowUserDataResetFailure("退出尚未完成，请重试");
        }
        catch { ShowUserDataResetFailure("退出未完成，请重试"); }
        finally { _userDataResetBusy = false; }
    }

    private void ShowUserDataResetFailure(string message)
    {
        if (_isClosed || _sessionEnding) return;
        RootLayout.IsEnabled = NotificationOverlay.IsEnabled = false;
        _state.SetShutdownConfirmation(false);
        ResetDialog.ShowFailure(message);
    }
}
