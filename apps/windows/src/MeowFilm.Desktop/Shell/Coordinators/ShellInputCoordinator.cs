using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

internal sealed class ShellInputCoordinator(ShellWindow shell)
{
    public void OnGotKeyboardFocus()
    {
        if (shell.CurrentSession is { } current && DesktopVisual.ContainsFocus(current.MainHost))
            current.ContentFocus = DesktopVisual.Focused(shell.Root);
    }

    public void OnSearchKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Enter) return;
        e.Handled = TrySubmitSearch(DesktopVisual.Modifiers, shell.SearchCoordinator.IsComposing);
    }

    public bool TrySubmitSearch(VirtualKeyModifiers modifiers, bool composing)
    {
        if (shell.IsClosing || composing || modifiers != VirtualKeyModifiers.None || !shell.SearchBoxElement.IsEnabled) return false;
        shell.RunUiAction(SearchAsync);
        return true;
    }

    public Task SearchAsync()
    {
        if (!shell.IsClosing && shell.CurrentSession is { IsSettingsOpen: false } && shell.SearchCoordinator.SearchPage is { } searchPage &&
            shell.SearchBoxElement.IsEnabled && !string.IsNullOrWhiteSpace(shell.SearchBoxElement.Text) &&
            shell.SearchTargetPickerElement.SelectedValue is string target)
        {
            string query = shell.SearchBoxElement.Text.Trim();
            var page = shell.Pages.Single(item => ReferenceEquals(item.Page, searchPage));
            page.SearchDraft = shell.SearchBoxElement.Text;
            return shell.ActivatePageAsync(page, token => searchPage.SubmitAsync(query, target, token));
        }
        if (shell.CurrentSession is not { } current || current.IsSettingsOpen ||
            shell.SearchCoordinator.OwnerSession is not { } owner || !shell.SearchBoxElement.IsEnabled ||
            string.IsNullOrWhiteSpace(shell.SearchBoxElement.Text)) return Task.CompletedTask;
        owner.SearchDrafts[owner.ActiveSearchKey] = shell.SearchBoxElement.Text;
        owner.IsSettingsOpen = false;
        shell.Modules.SettingsReturn = null;
        if (shell.CurrentPage is not null || !ReferenceEquals(current, owner))
            shell.PreparePresentation(owner);
        return shell.RunRequestAsync(new(owner, new(shell.SearchBoxElement.Text.Trim(), IsSearch: true)));
    }

    public void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (shell.CloseDialogElement.IsOpen || shell.ResetDialogElement.IsOpen) return;
        if (shell.IsClosing) return;
        if (shell.ChannelDialogHostElement.IsOpen)
        {
            if (!shell.ChannelDialogHostElement.HandleEscape(e)) ProtectChannelKeyboard(e);
            if (e.Key is VirtualKey.F or VirtualKey.K && DesktopVisual.Modifiers == VirtualKeyModifiers.Control ||
                e.Key is VirtualKey.Left or VirtualKey.Right && DesktopVisual.Modifiers == VirtualKeyModifiers.Menu)
                e.Handled = true;
            return;
        }
        e.Handled = TryExecuteShortcut(e.Key, DesktopVisual.Modifiers);
    }

    public bool TryExecuteShortcut(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (shell.IsClosing || shell.CloseDialogElement.IsOpen || shell.ResetDialogElement.IsOpen || shell.ChannelDialogHostElement.IsOpen) return false;
        if (key == VirtualKey.Escape && DesktopVisual.Focused(shell.Root) is Slider) return false;
        if (key is VirtualKey.F or VirtualKey.K && modifiers == VirtualKeyModifiers.Control &&
            shell.SearchInputElement.Visibility == Visibility.Visible && shell.SearchBoxElement.IsEnabled)
        {
            shell.SearchBoxElement.Focus(FocusState.Programmatic);
            shell.SearchBoxElement.SelectAll();
            return true;
        }
        if (key == VirtualKey.Left && modifiers == VirtualKeyModifiers.Menu)
        {
            shell.RunUiAction(shell.GoBackAsync);
            return true;
        }
        if (key == VirtualKey.Right && modifiers == VirtualKeyModifiers.Menu)
        {
            shell.RunUiAction(shell.GoForwardAsync);
            return true;
        }
        if (key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None && shell.State.CanCancel)
        {
            shell.CancelFromUi();
            return true;
        }
        if (key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None &&
            (shell.IsQueueOpen || shell.IsPlayerDetailOpen))
        {
            shell.RunUiAction(shell.GoBackAsync);
            return true;
        }
        return false;
    }

    public void OnPreviewKeyUp(KeyRoutedEventArgs e)
    {
        if (!shell.CloseDialogElement.IsOpen && !shell.ResetDialogElement.IsOpen && shell.ChannelDialogHostElement.IsOpen)
            ProtectChannelKeyboard(e);
    }

    public void OnChannelHeaderPointerPressed(PointerRoutedEventArgs e) => OnChannelHeaderPointer(e, pressed: true);
    public void OnChannelHeaderPointerReleased(PointerRoutedEventArgs e) => OnChannelHeaderPointer(e, pressed: false);

    private void ProtectChannelKeyboard(KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject target ||
            !DesktopVisual.Contains(shell.ChannelDialogHostElement, target)) e.Handled = true;
    }

    private void OnChannelHeaderPointer(PointerRoutedEventArgs e, bool pressed)
    {
        if (shell.CloseDialogElement.IsOpen || shell.ResetDialogElement.IsOpen || !shell.ChannelDialogHostElement.IsOpen) return;
        bool In(FrameworkElement control) => e.OriginalSource is DependencyObject target && DesktopVisual.Contains(control, target);
        if (shell.ChannelDialogHostElement.HasOutsidePress || In(shell.SearchChromeElement) || In(shell.SearchInputElement) ||
            In(shell.SettingsButtonElement) || In(shell.PlayerDetailBackButtonElement) || In(shell.PageHeaderHostElement))
            shell.ChannelDialogHostElement.HandleOutsidePointer(e, shell.RootLayoutElement, pressed);
    }
}
