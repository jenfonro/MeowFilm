using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly Func<Task>? _resetUserData;
    private DependencyObject? _resetFocus;
    private bool _userDataResetStarted, _userDataResetBusy;

    internal bool IsUserDataResetStarted => _userDataResetStarted;

    internal void ConfirmUserDataReset()
    {
        if (_resetUserData is null || _isClosing || _isClosed || _closeCoordinator.IsSessionEnding ||
            _closeCoordinator.IsChoiceQueued || CloseDialog.IsOpen || ResetDialog.IsOpen) return;
        _resetFocus = DesktopVisual.Focused(Root);
        SetModalGatesEnabled(false);
        ResetDialog.Open();
    }

    internal void CancelUserDataReset()
    {
        if (_userDataResetStarted || !ResetDialog.IsOpen || _isClosing || _closeCoordinator.IsSessionEnding) return;
        ResetDialog.Visibility = Visibility.Collapsed;
        SetModalGatesEnabled(true);
        var focus = _resetFocus;
        _resetFocus = null;
        if (focus is Control { IsEnabled: true, Visibility: Visibility.Visible } target) target.Focus(FocusState.Programmatic);
    }

    internal void ConfirmUserDataResetAction() => OnUserDataResetConfirmed();

    private async void OnUserDataResetConfirmed()
    {
        if (_resetUserData is null || _userDataResetBusy || _isClosed || _closeCoordinator.IsSessionEnding || !ResetDialog.IsOpen) return;
        _userDataResetBusy = _userDataResetStarted = true;
        _resetFocus = null;
        _isClosing = true;
        _state.IsInteractive = false;
        _modules.RetryPlan = null;
        _modules.IncrementGeneration();
        RetireHeaderPresentation();
        SetStatus();
        CancelActive();
        CancelPageActivation();
        _modules.CancelAllRequests();
        ResetDialog.SetBusy("正在清除…");
        try
        {
            await _resetUserData();
            ShowUserDataResetFailure("清除尚未完成，请重试");
        }
        catch { ShowUserDataResetFailure("清除未完成，请重试"); }
        finally { _userDataResetBusy = false; }
    }

    internal void OnUserDataResetExit()
    {
        if (!_userDataResetStarted || _userDataResetBusy || _isClosed || _closeCoordinator.IsSessionEnding || !ResetDialog.IsOpen) return;
        _userDataResetStarted = false;
        _ = _closeCoordinator.EnsureShutdownAsync();
        _ = _closeCoordinator.RequestFinalCloseAsync();
    }

    private void ShowUserDataResetFailure(string message)
    {
        if (_isClosed || _closeCoordinator.IsSessionEnding) return;
        SetModalGatesEnabled(false);
        ResetDialog.ShowFailure(message);
    }
}
