namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly Func<Task>? _resetUserData;
    private bool _userDataResetStarted, _userDataResetBusy;

    internal bool IsUserDataResetStarted => _userDataResetStarted;

    internal void ConfirmUserDataReset()
    {
        if (_resetUserData is null || _isClosing || _isClosed || _closeCoordinator.IsSessionEnding ||
            _closeCoordinator.IsChoiceQueued || IsSystemModalOpen) return;
        _resetDialog.PrepareOpen();
        ShowSystemModal(_resetDialog, zIndex: 110, onCloseRequested: CancelUserDataReset);
        _resetDialog.FocusInitial();
    }

    internal void CancelUserDataReset()
    {
        if (_userDataResetStarted || !_resetDialog.IsOpen || _isClosing || _closeCoordinator.IsSessionEnding) return;
        DismissSystemModal(_resetDialog, restoreFocus: true);
    }

    private async void OnUserDataResetConfirmed()
    {
        if (_resetUserData is null || _userDataResetBusy || _isClosed || _closeCoordinator.IsSessionEnding || !_resetDialog.IsOpen) return;
        _userDataResetBusy = _userDataResetStarted = true;
        _isClosing = true;
        _state.IsInteractive = false;
        _modules.RetryPlan = null;
        _modules.IncrementGeneration();
        RetireHeaderPresentation();
        SetStatus();
        CancelActive();
        CancelPageActivation();
        _modules.CancelAllRequests();
        _resetDialog.SetBusy("正在清除…");
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
        if (!_userDataResetStarted || _userDataResetBusy || _isClosed || _closeCoordinator.IsSessionEnding || !_resetDialog.IsOpen) return;
        _userDataResetStarted = false;
        _ = _closeCoordinator.EnsureShutdownAsync();
        _ = _closeCoordinator.RequestFinalCloseAsync();
    }

    private void ShowUserDataResetFailure(string message)
    {
        if (_isClosed || _closeCoordinator.IsSessionEnding) return;
        SetModalGatesEnabled(false);
        _resetDialog.ShowFailure(message);
    }
}
