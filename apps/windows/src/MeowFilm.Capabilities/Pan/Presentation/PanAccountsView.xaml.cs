using MeowFilm.Desktop;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Capabilities;

public partial class PanAccountsView : UserControl
{
    private PanAccounts? observed;
    private bool loginShowing, logoutShowing, active;
    private Button? logoutAnchor;
    private DesktopWindow? window;
    private IDisposable? visibility;
    private DispatcherTimer? polling;
    private PanAccountRow? formRow;
    private bool formQr;
    private Button? loginAnchor;
    private (PanAccountRow Row, long? Revision)? logout;
    private PanAccounts? Owner => DataContext as PanAccounts;
    private bool Active => IsLoaded && DesktopVisual.IsVisible(this) && window?.WindowState != DesktopWindowState.Minimized;

    public PanAccountsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => SizePopup();
        DataContextChanged += (_, _) => { Detach(); if (IsLoaded) Attach(); };
    }
    private void Attach()
    {
        if (observed is not null) return;
        observed = Owner;
        if (observed is not null) observed.PropertyChanged += OwnerChanged;
        window = DesktopWindow.GetWindow(this);
        if (window is not null)
        {
            window.WindowStateChanged += WindowChanged;
            window.VisibilityChanged += WindowChanged;
        }
        visibility = DesktopVisual.ObserveVisibility(this, visible => { _ = VisibilityChanged(); });
    }
    private void Detach()
    {
        visibility?.Dispose(); visibility = null;
        var old = observed;
        observed = null;
        active = false;
        if (old is not null) old.PropertyChanged -= OwnerChanged;
        if (window is not null)
        {
            window.WindowStateChanged -= WindowChanged;
            window.VisibilityChanged -= WindowChanged;
        }
        window = null;
        StopPolling();
        ClearInputs();
        formRow = null; loginAnchor = null; logout = null;
        LoginPopup.Hide(); LogoutPopup.Hide();
        logoutAnchor = null;
        if (old is not null) _ = old.DeactivateAsync();
    }
    private async void WindowChanged(object? sender, EventArgs e) => await VisibilityChanged();
    private async Task VisibilityChanged()
    {
        bool next = Active;
        if (active == next) return;
        active = next;
        if (!active)
        {
            StopPolling();
            ClearInputs(); loginAnchor = null; logout = null;
            LoginPopup.Hide(); LogoutPopup.Hide();
            logoutAnchor = null;
            if (observed is { } old) await old.DeactivateAsync();
        }
        else if (observed is { } owner) await owner.ActivateAsync();
    }
    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, observed)) return;
        if (logout is { } confirmation &&
            (!confirmation.Row.CanLogout || confirmation.Row.Revision != confirmation.Revision))
            LogoutPopup.Hide();
        SyncLogin();
    }
    private void SyncLogin()
    {
        if (!Active || observed is not { LoginOpen: true } owner)
        {
            StopPolling();
            ClearInputs(); formRow = null;
            LoginPopup.Hide();
            return;
        }
        if (!ReferenceEquals(formRow, owner.LoginRow) || formQr != owner.QrMode)
        {
            ClearInputs(); CredentialInputs.Children.Clear();
            formRow = owner.LoginRow; formQr = owner.QrMode;
            if (!formQr)
                foreach (var field in formRow!.CredentialFields)
                {
                    CredentialInputs.Children.Add(new TextBlock
                    {
                        Text = field.Label, Margin = new(0, 0, 0, 8),
                        Style = (Style)DesktopVisual.Resource(this, "Settings.Text")
                    });
                    Control input = field.IsSecret
                        ? new PasswordBox { Style = (Style)DesktopVisual.Resource(this, "Account.Password") }
                        : new TextBox { Style = (Style)DesktopVisual.Resource(this, "Settings.TextBox") };
                    input.Tag = field;
                    input.SetBinding(Control.IsEnabledProperty, new Binding
                    {
                        Path = new PropertyPath(nameof(PanAccounts.CanSave)), Mode = BindingMode.OneWay
                    });
                    input.Margin = new(0, 0, 0, 14);
                    AutomationProperties.SetName(input, field.Label);
                    CredentialInputs.Children.Add(input);
                }
        }
        QrPanel.Visibility = formQr ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = formQr ? Visibility.Collapsed : Visibility.Visible;
        SizePopup();
        ShowLogin();
        if (formQr)
        {
            if (polling is not null) return;
            polling = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(2) };
            polling.Tick += Poll;
            polling.Start();
        }
        else StopPolling();
    }
    private async void ShowLogin()
    {
        if (loginShowing) return;
        loginShowing = true;
        try { await DialogPlacement.ShowAsync(this, LoginPopup); }
        finally { loginShowing = false; }
    }
    private async void ShowLogout()
    {
        if (logoutShowing) return;
        logoutShowing = true;
        try { await DialogPlacement.ShowAsync(this, LogoutPopup); }
        finally { logoutShowing = false; }
    }
    private void SizePopup()
    {
        LoginSurface.Width = Math.Min(360, Math.Max(0, ActualWidth - 32));
        LoginSurface.MaxHeight = Math.Max(0, (window?.Root.ActualHeight ?? ActualHeight) - 64);
    }
    private void StopPolling()
    {
        if (polling is null) return;
        polling.Stop(); polling.Tick -= Poll; polling = null;
    }
    private async void Poll(object? sender, object e)
    {
        if (Active && loginShowing && observed is { QrMode: true } owner) await owner.PollLoginAsync();
    }
    private void ClearInputs()
    {
        foreach (var input in CredentialInputs.Children.OfType<Control>())
            if (input is PasswordBox secret) secret.Password = "";
            else if (input is TextBox text) text.Text = "";
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !Active || Owner is not { } owner || sender is not Button { DataContext: PanAccountRow row } button) return;
        loginAnchor = button;
        await owner.OpenLoginAsync(row);
    }
    private async void EditCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Owner is { } owner && sender is Button { DataContext: PanAccountRow row })
            await owner.OpenCredentialsAsync(row);
    }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Owner is { } owner) await owner.ReloadAsync();
    }
    private async void QrMode_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Owner is { } owner) await owner.SwitchMethodAsync(qr: true);
    }
    private async void ManualMode_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Owner is { } owner) await owner.SwitchMethodAsync(qr: false);
    }
    private async void RetryQr_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Owner is { } owner) await owner.RetryQrAsync();
    }
    private void CloseLogin_Click(object sender, RoutedEventArgs e) => LoginPopup.Hide();
    private void LoginPopup_Opened(ContentDialog sender, ContentDialogOpenedEventArgs e)
    {
        if (formQr) QrModeButton.Focus(FocusState.Programmatic);
        else CredentialInputs.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);
    }
    private async void LoginPopup_Closed(ContentDialog sender, ContentDialogClosedEventArgs e)
    {
        loginShowing = false;
        StopPolling(); ClearInputs(); formRow = null;
        var anchor = loginAnchor; loginAnchor = null;
        if (observed is { } owner) await owner.CloseLoginAsync();
        if (Active && anchor is { IsEnabled: true } && DesktopVisual.IsVisible(anchor)) anchor.Focus(FocusState.Programmatic);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || Owner is not { CanSave: true } owner) return;
        var draft = new Dictionary<string, string>();
        foreach (var input in CredentialInputs.Children.OfType<Control>())
            if (input.Tag is PanCredentialField field)
                draft.Add(field.Id, input is PasswordBox secret ? secret.Password : ((TextBox)input).Text);
        var save = owner.SaveAsync(draft);
        draft.Clear(); ClearInputs();
        await save;
    }
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !Active || Owner is not { } owner || sender is not Button { DataContext: PanAccountRow row } button ||
            !owner.Rows.Contains(row) || !row.CanLogout) return;
        logout = (row, row.Revision);
        logoutAnchor = button;
        ShowLogout();
    }
    private void LogoutPopup_Opened(ContentDialog sender, ContentDialogOpenedEventArgs e) => CancelLogout.Focus(FocusState.Programmatic);
    private void LogoutPopup_Closed(ContentDialog sender, ContentDialogClosedEventArgs e)
    {
        logoutShowing = false;
        logout = null;
        var target = logoutAnchor;
        logoutAnchor = null;
        if (Active && target is { IsEnabled: true } && DesktopVisual.IsVisible(target)) target.Focus(FocusState.Programmatic);
    }
    private void CancelLogout_Click(object sender, RoutedEventArgs e) => LogoutPopup.Hide();
    private async void ConfirmLogout_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = logout;
        LogoutPopup.Hide();
        if (Active && Owner is { } owner && confirmation is { } captured &&
            captured.Row.Revision == captured.Revision) await owner.LogoutAsync(captured.Row);
    }
}
