using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

internal sealed partial class DesktopModalDialogView : UserControl
{
    private readonly DesktopDialogRequest request;
    private readonly Action<FrameworkElement> closeSurface;
    private Action<bool>? primaryEnabledHandler;
    private DesktopDialogResult outcome = DesktopDialogResult.None;
    private bool busy;

    internal DesktopDialogResult Result => outcome;
    internal bool IsBusy => busy;

    internal DesktopModalDialogView(DesktopDialogRequest request, Action<FrameworkElement> closeSurface)
    {
        this.request = request;
        this.closeSurface = closeSurface;
        InitializeComponent();

        MinWidth = request.MinWidth;
        MaxWidth = request.MaxWidth;

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            HeadingText.Text = request.Title;
            HeadingText.Visibility = Visibility.Visible;
            AutomationProperties.SetName(this, request.Title);
        }

        UIElement? body = request.Content;
        if (body is null && !string.IsNullOrWhiteSpace(request.Message))
            body = new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap };
        if (body is not null)
        {
            BodyScroller.Content = body;
            BodyScroller.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(request.CloseButtonText))
        {
            CloseButton.Content = request.CloseButtonText;
            CloseButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(request.SecondaryButtonText))
        {
            SecondaryButton.Content = request.SecondaryButtonText;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(request.PrimaryButtonText))
        {
            PrimaryButton.Content = request.PrimaryButtonText;
            PrimaryButton.IsEnabled = request.IsPrimaryEnabled;
            PrimaryButton.Visibility = Visibility.Visible;
            primaryEnabledHandler = enabled => { if (!busy) PrimaryButton.IsEnabled = enabled; };
            request.PrimaryEnabledChanged += primaryEnabledHandler;
        }
    }

    internal void FocusInitial()
    {
        Button? closeBtn = CloseButton.Visibility == Visibility.Visible ? CloseButton : null;
        Button? secondaryBtn = SecondaryButton.Visibility == Visibility.Visible ? SecondaryButton : null;
        Button? primaryBtn = PrimaryButton.Visibility == Visibility.Visible ? PrimaryButton : null;
        var target = request.DefaultButton switch
        {
            DesktopDialogDefaultButton.Primary => primaryBtn,
            DesktopDialogDefaultButton.Secondary => secondaryBtn,
            DesktopDialogDefaultButton.Close => closeBtn,
            _ => null
        } ?? primaryBtn ?? closeBtn ?? secondaryBtn;
        target?.Focus(FocusState.Programmatic);
    }

    internal void Detach()
    {
        if (primaryEnabledHandler is not null)
        {
            request.PrimaryEnabledChanged -= primaryEnabledHandler;
            primaryEnabledHandler = null;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        outcome = DesktopDialogResult.None;
        closeSurface(this);
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        outcome = DesktopDialogResult.Secondary;
        closeSurface(this);
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => _ = InvokePrimaryAsync();

    private async Task InvokePrimaryAsync()
    {
        if (busy || !request.IsPrimaryEnabled) return;
        if (request.OnPrimaryAsync is not null)
        {
            busy = true;
            PrimaryButton.IsEnabled = false;
            SecondaryButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            try
            {
                if (!await request.OnPrimaryAsync()) return;
            }
            catch (OperationCanceledException) { return; }
            catch { return; }
            finally
            {
                busy = false;
                PrimaryButton.IsEnabled = request.IsPrimaryEnabled;
                SecondaryButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
        outcome = DesktopDialogResult.Primary;
        closeSurface(this);
    }

    private void OnSurfaceKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || busy) return;
        if (args.OriginalSource is TextBox { AcceptsReturn: true }) return;
        if (args.OriginalSource is Button) return;
        switch (request.DefaultButton)
        {
            case DesktopDialogDefaultButton.Primary when PrimaryButton.Visibility == Visibility.Visible && PrimaryButton.IsEnabled:
                args.Handled = true;
                _ = InvokePrimaryAsync();
                break;
            case DesktopDialogDefaultButton.Secondary when SecondaryButton.Visibility == Visibility.Visible && SecondaryButton.IsEnabled:
                args.Handled = true;
                outcome = DesktopDialogResult.Secondary;
                closeSurface(this);
                break;
            case DesktopDialogDefaultButton.Close when CloseButton.Visibility == Visibility.Visible && CloseButton.IsEnabled:
                args.Handled = true;
                outcome = DesktopDialogResult.None;
                closeSurface(this);
                break;
        }
    }
}
