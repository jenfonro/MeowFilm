using System.Windows;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly Func<Task>? _forceShutdown, _cancelShutdown;
    private bool _forceStarted, _shutdownDecisionPending;
    private Task? _cancelShutdownWork;

    /// <summary>Trusted host cleanup for a constructed window whose startup failed.
    /// No close is authorized until the injected force callback actually succeeds.</summary>
    public async Task AbortAsync()
    {
        if (_forceShutdown is null) throw new InvalidOperationException("SHUTDOWN_FAILED");
        _isClosing = _forceStarted = true;
        _state.IsInteractive = false;
        RetireHeaderPresentation();
        CancelActive(); CancelPageActivation();
        var work = _forceShutdown() ?? throw new InvalidOperationException("SHUTDOWN_FAILED");
        _ = ShellRequest.ObserveAsync(work);
        await work;
        CleanupCompleted = true;
        await RequestFinalCloseAsync();
    }

    private async Task ForceShutdownAsync()
    {
        if (_shutdownDecisionPending || _forceShutdown is null) return;
        _shutdownDecisionPending = true;
        _forceStarted = true;
        _state.SetShutdownConfirmation(true);
        SetStatus("正在清理…", busy: true);
        try
        {
            var work = _forceShutdown() ?? throw new InvalidOperationException();
            _ = ShellRequest.ObserveAsync(work);
            await work.WaitAsync(ShutdownTimeout, _timeProvider);
            CleanupCompleted = true; // Not a claim that unsaved changes were saved.
            await RequestFinalCloseAsync();
        }
        catch
        {
            SetStatus("清理尚未完成", error: true);
            _state.SetShutdownConfirmation(true, canForce: true);
        }
        finally { _shutdownDecisionPending = false; }
    }
    private async void OnCancelShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!_isClosing || !_state.CanCancelShutdown || _shutdownDecisionPending || _cancelShutdown is null) return;
        _shutdownDecisionPending = true;
        _state.SetShutdownConfirmation(true);
        try
        {
            var work = _cancelShutdownWork ??= _cancelShutdown() ?? throw new InvalidOperationException();
            _ = ShellRequest.ObserveAsync(work);
            // Bound the observation, not the actual rollback. A retry observes
            // this same task; force still drains it through the App owner.
            await work.WaitAsync(ShutdownTimeout, _timeProvider);
            if (_forceStarted || _isClosed) return;
            _cancelShutdownWork = null;
            _shutdownTask = null;
            _shutdownStarted = 0;
            _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _isClosing = false;
            // Reopen only a fresh page admission. Operations cancelled by the failed
            // close retain their retired tokens and cannot navigate after rollback.
            if (_currentPage is not null) _pageNavigation = new CancellationTokenSource();
            _state.IsInteractive = true;
            _state.SetShutdownConfirmation(false);
            SetStatus();
            ApplyPresentation();
        }
        catch (TimeoutException) when (_cancelShutdownWork is { IsFaulted: false, IsCanceled: false })
        {
            SetStatus("取消关闭尚未完成", error: true);
            _state.SetShutdownConfirmation(true, canForce: _forceShutdown is not null, canCancel: true);
        }
        catch
        {
            _cancelShutdownWork = null;
            SetStatus("无法取消关闭", error: true);
            _state.SetShutdownConfirmation(true, canForce: _forceShutdown is not null);
        }
        finally { _shutdownDecisionPending = false; }
    }
}
