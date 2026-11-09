using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly IDesktopClosePreferences? _closePreferences;
    private readonly Func<Window, Action, Action, Action, IDesktopTrayIcon>? _trayIconFactory;
    private IDesktopTrayIcon? _trayIcon;
    private IInputElement? _closeChoiceFocus;
    private WindowState _trayRestoreState = WindowState.Normal;
    private bool _hiddenToTray, _closeChoiceQueued, _sessionEnding;
    private long _closeChoiceGeneration;

    private void InitializeTrayIcon()
    {
        if (_closePreferences is null) return;
        _trayIcon = _trayIconFactory is { } factory
            ? factory(this, RestoreFromTray, ExitFromTray, OnTrayUnavailable)
            : new DesktopTrayIcon(this, RestoreFromTray, ExitFromTray, OnTrayUnavailable);
        if (!_trayIcon.TryShow()) OnTrayUnavailable();
    }

    private void QueueCloseChoice()
    {
        if (_closeChoiceQueued || CloseDialog.IsOpen) return;
        _closeChoiceQueued = true;
        _ = ChooseCloseBehaviorAsync();
    }

    private async Task ChooseCloseBehaviorAsync()
    {
        try
        {
            // Visibility/Close cannot be changed while WPF is dispatching Closing.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_isClosing || _isClosed || _sessionEnding) return;
            switch (_closePreferences!.Behavior)
            {
                case WindowCloseBehavior.MinimizeToTray:
                    HideToTray();
                    break;
                case WindowCloseBehavior.Exit:
                    BeginShutdown();
                    break;
                case WindowCloseBehavior.Ask:
                    _closeChoiceFocus = Keyboard.FocusedElement;
                    RootLayout.IsEnabled = NotificationOverlay.IsEnabled = false;
                    CloseDialog.Open();
                    break;
            }
        }
        catch (OperationCanceledException) { /* Dispatcher teardown owns the window. */ }
        finally { _closeChoiceQueued = false; }
    }

    private async void OnCloseChoiceRequested(WindowCloseBehavior behavior)
    {
        if (_isClosing || _isClosed || _sessionEnding || !CloseDialog.IsOpen || CloseDialog.IsPending) return;
        var generation = _closeChoiceGeneration;
        CloseDialog.SetPending(true);
        try
        {
            if (CloseDialog.ShouldRemember)
            {
                if (behavior == WindowCloseBehavior.MinimizeToTray && !EnsureTrayAvailable()) return;
                if (!await _closePreferences!.SaveAsync(behavior))
                {
                    if (generation == _closeChoiceGeneration && !_isClosing && !_isClosed)
                        Notices.Show(CloseDialog, string.IsNullOrEmpty(_closePreferences.Status)
                            ? "窗口行为未保存，请重试" : _closePreferences.Status);
                    return;
                }
            }
            // A tray exit, startup abort or system shutdown can retire a pending save.
            if (generation != _closeChoiceGeneration || _isClosing || _isClosed || _sessionEnding) return;
            if (behavior == WindowCloseBehavior.MinimizeToTray) HideToTray();
            else BeginShutdown();
        }
        catch
        {
            if (generation == _closeChoiceGeneration && !_isClosing && !_isClosed)
                Notices.Show(CloseDialog, "窗口行为未保存，请重试");
        }
        finally
        {
            if (generation == _closeChoiceGeneration) CloseDialog.SetPending(false);
        }
    }

    private void CancelCloseChoice()
    {
        if (!CloseDialog.IsPending) DismissCloseChoice();
    }

    private void DismissCloseChoice(bool restoreFocus = true)
    {
        if (!CloseDialog.IsOpen) return;
        _closeChoiceGeneration++;
        CloseDialog.Visibility = Visibility.Collapsed;
        RootLayout.IsEnabled = NotificationOverlay.IsEnabled = true;
        Notices.Show(CloseDialog, "");
        var focus = _closeChoiceFocus;
        _closeChoiceFocus = null;
        if (restoreFocus && focus is UIElement { IsEnabled: true, IsVisible: true } target) target.Focus();
    }

    private bool EnsureTrayAvailable()
    {
        if (_trayIcon?.TryShow() == true) return true;
        OnTrayUnavailable();
        return false;
    }

    private void HideToTray()
    {
        if (!EnsureTrayAvailable()) return;
        DismissCloseChoice(restoreFocus: false);
        if (WindowState != WindowState.Minimized) _trayRestoreState = WindowState;
        _hiddenToTray = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_isClosed || _sessionEnding) return;
        var restore = _trayRestoreState;
        bool restoreState = _hiddenToTray || WindowState == WindowState.Minimized;
        _hiddenToTray = false;
        if (!IsVisible) Show();
        if (restoreState) WindowState = restore;
        Activate();
    }

    private void ExitFromTray()
    {
        if (_isClosed || _sessionEnding) return;
        RestoreFromTray();
        BeginShutdown();
    }

    private void OnTrayUnavailable()
    {
        if (_isClosed || _sessionEnding) return;
        if (_hiddenToTray) RestoreFromTray();
        Notices.Show(CloseDialog, "系统托盘不可用，主窗口保持打开");
    }

    /// <summary>Starts best-effort cleanup without cancelling or awaiting the OS session shutdown.</summary>
    public void PrepareForSessionEnding()
    {
        Dispatcher.VerifyAccess();
        _sessionEnding = true;
        DismissCloseChoice(restoreFocus: false);
        BeginShutdown();
    }
}
