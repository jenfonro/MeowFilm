using MeowFilm.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Desktop;

internal sealed class ShellCloseCoordinator(
    ShellWindow shell,
    IDesktopClosePreferences? closePreferences,
    Func<DesktopWindow, Action, Action, Action, IDesktopTrayIcon>? trayIconFactory) : IDisposable
{
    private IDesktopTrayIcon? _trayIcon;
    private DependencyObject? _closeChoiceFocus;
    private DesktopWindowState _trayRestoreState = DesktopWindowState.Normal;
    private bool _hiddenToTray;
    private bool _closeChoiceQueued;
    private bool _sessionEnding;
    private long _closeChoiceGeneration;

    public bool HasPreferences => closePreferences is not null;
    public bool IsHiddenToTray => _hiddenToTray;
    public bool IsChoiceQueued => _closeChoiceQueued;
    public bool IsSessionEnding => _sessionEnding;

    public void InitializeTrayIcon()
    {
        if (closePreferences is null) return;
        _trayIcon = trayIconFactory is { } factory
            ? factory(shell, RestoreFromTray, ExitFromTray, OnTrayUnavailable)
            : new DesktopTrayIcon(shell, RestoreFromTray, ExitFromTray, OnTrayUnavailable);
        if (!_trayIcon.TryShow()) OnTrayUnavailable();
    }

    public void OnWindowStateChanged(DesktopWindowState state)
    {
        if (state != DesktopWindowState.Minimized) _trayRestoreState = state;
    }

    public void QueueCloseChoice()
    {
        if (_closeChoiceQueued || shell.CloseDialogElement.IsOpen) return;
        _closeChoiceQueued = true;
        _ = ChooseCloseBehaviorAsync();
    }

    public async Task ChooseCloseBehaviorAsync()
    {
        try
        {
            // Do not change visibility or re-enter Close inside the native Closing callback.
            await OwnerDispatcher.YieldAsync(DispatcherQueuePriority.Low);
            if (shell.IsClosing || shell.IsClosed || _sessionEnding) return;
            switch (closePreferences!.Behavior)
            {
                case WindowCloseBehavior.MinimizeToTray:
                    HideToTray();
                    break;
                case WindowCloseBehavior.Exit:
                    shell.BeginShutdown();
                    break;
                case WindowCloseBehavior.Ask:
                    _closeChoiceFocus = DesktopVisual.Focused(shell.Root);
                    shell.SetModalGatesEnabled(false);
                    shell.CloseDialogElement.Open();
                    break;
            }
        }
        catch (OperationCanceledException) { /* Dispatcher teardown owns the window. */ }
        finally { _closeChoiceQueued = false; }
    }

    public async void OnCloseChoiceRequested(WindowCloseBehavior behavior)
    {
        if (shell.IsClosing || shell.IsClosed || _sessionEnding || !shell.CloseDialogElement.IsOpen || shell.CloseDialogElement.IsPending) return;
        var generation = _closeChoiceGeneration;
        shell.CloseDialogElement.SetPending(true);
        try
        {
            if (shell.CloseDialogElement.ShouldRemember)
            {
                if (behavior == WindowCloseBehavior.MinimizeToTray && !EnsureTrayAvailable()) return;
                if (!await closePreferences!.SaveAsync(behavior))
                {
                    if (generation == _closeChoiceGeneration && !shell.IsClosing && !shell.IsClosed)
                        shell.NoticesElement.Show(shell.CloseDialogElement, string.IsNullOrEmpty(closePreferences.Status)
                            ? "窗口行为未保存，请重试" : closePreferences.Status);
                    return;
                }
            }
            // A tray exit, startup abort or system shutdown can retire a pending save.
            if (generation != _closeChoiceGeneration || shell.IsClosing || shell.IsClosed || _sessionEnding) return;
            if (behavior == WindowCloseBehavior.MinimizeToTray) HideToTray();
            else shell.BeginShutdown();
        }
        catch
        {
            if (generation == _closeChoiceGeneration && !shell.IsClosing && !shell.IsClosed)
                shell.NoticesElement.Show(shell.CloseDialogElement, "窗口行为未保存，请重试");
        }
        finally
        {
            if (generation == _closeChoiceGeneration) shell.CloseDialogElement.SetPending(false);
        }
    }

    public void CancelCloseChoice()
    {
        if (!shell.CloseDialogElement.IsPending) DismissCloseChoice();
    }

    public void DismissCloseChoice(bool restoreFocus = true)
    {
        if (!shell.CloseDialogElement.IsOpen) return;
        _closeChoiceGeneration++;
        shell.CloseDialogElement.Visibility = Visibility.Collapsed;
        shell.SetModalGatesEnabled(true);
        shell.NoticesElement.Show(shell.CloseDialogElement, "");
        var focus = _closeChoiceFocus;
        _closeChoiceFocus = null;
        if (restoreFocus && focus is Control { IsEnabled: true, Visibility: Visibility.Visible } target)
            target.Focus(FocusState.Programmatic);
    }

    public void RestoreFromTray()
    {
        shell.Dispatcher.VerifyAccess();
        if (shell.IsClosed || _sessionEnding) return;
        var restore = _trayRestoreState;
        bool restoreState = _hiddenToTray || shell.WindowState == DesktopWindowState.Minimized;
        _hiddenToTray = false;
        if (!shell.IsVisible) shell.Show();
        if (restoreState) shell.WindowState = restore;
        shell.Activate();
    }

    public void PrepareForSessionEnding()
    {
        shell.Dispatcher.VerifyAccess();
        if (_sessionEnding) return;
        _sessionEnding = true;
        DismissCloseChoice(restoreFocus: false);
        shell.BeginShutdown();
    }

    public async Task<object> HandleDebugCloseRequestAsync()
    {
        if (shell.IsClosing || shell.IsClosed || shell.ResetDialogElement.IsOpen)
        {
            return new
            {
                requested = false,
                dialogOpen = shell.CloseDialogElement.IsOpen,
                hiddenToTray = _hiddenToTray,
                isClosing = shell.IsClosing
            };
        }
        if (closePreferences is not null)
        {
            if (!shell.CloseDialogElement.IsOpen && !_closeChoiceQueued)
            {
                _closeChoiceQueued = true;
                await ChooseCloseBehaviorAsync();
            }
        }
        else
        {
            shell.Close();
        }
        return new
        {
            requested = true,
            dialogOpen = shell.CloseDialogElement.IsOpen,
            hiddenToTray = _hiddenToTray,
            isClosing = shell.IsClosing
        };
    }

    public void Dispose()
    {
        DismissCloseChoice(restoreFocus: false);
        _trayIcon?.Dispose();
        _trayIcon = null;
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
        if (shell.WindowState != DesktopWindowState.Minimized) _trayRestoreState = shell.WindowState;
        _hiddenToTray = true;
        shell.Hide();
    }

    private void ExitFromTray()
    {
        if (shell.IsClosed || _sessionEnding) return;
        RestoreFromTray();
        if (shell.ResetDialogElement.IsOpen) shell.OnUserDataResetExit();
        else shell.BeginShutdown();
    }

    private void OnTrayUnavailable()
    {
        if (shell.IsClosed || _sessionEnding) return;
        if (_hiddenToTray) RestoreFromTray();
        shell.NoticesElement.Show(shell.CloseDialogElement, "系统托盘不可用，主窗口保持打开");
    }
}
