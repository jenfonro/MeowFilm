using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class PanAccountsView : UserControl
{
    private PanAccounts? observed;
    private Window? window;
    private DispatcherTimer? polling;
    private PanAccountRow? formRow;
    private bool formQr;
    private Button? loginAnchor;
    private (PanAccountRow Row, long Revision)? logout;
    private PanAccounts? Owner => DataContext as PanAccounts;
    private bool Active => IsLoaded && IsVisible && window?.WindowState != WindowState.Minimized;

    public PanAccountsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += async (_, _) => await VisibilityChanged();
        SizeChanged += (_, _) => SizePopup();
        DataContextChanged += (_, _) => { Detach(); if (IsLoaded) Attach(); };
    }
    private void Attach()
    {
        if (observed is not null) return;
        observed = Owner;
        if (observed is not null) observed.PropertyChanged += OwnerChanged;
        window = Window.GetWindow(this);
        if (window is not null) window.StateChanged += WindowChanged;
        _ = VisibilityChanged();
    }
    private void Detach()
    {
        var old = observed;
        observed = null;
        if (old is not null) old.PropertyChanged -= OwnerChanged;
        if (window is not null) window.StateChanged -= WindowChanged;
        window = null;
        StopPolling();
        ClearInputs();
        formRow = null; loginAnchor = null; logout = null;
        LoginPopup.IsOpen = false; LogoutPopup.IsOpen = false;
        LogoutPopup.PlacementTarget = null;
        if (old is not null) _ = old.DeactivateAsync();
    }
    private async void WindowChanged(object? sender, EventArgs e) => await VisibilityChanged();
    private async Task VisibilityChanged()
    {
        if (!Active)
        {
            StopPolling();
            ClearInputs(); loginAnchor = null; logout = null;
            LoginPopup.IsOpen = false; LogoutPopup.IsOpen = false;
            LogoutPopup.PlacementTarget = null;
            if (observed is { } old) await old.DeactivateAsync();
        }
        else if (observed is { } owner) await owner.ActivateAsync();
    }
    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, observed)) return;
        if (logout is { } confirmation &&
            (!confirmation.Row.CanLogout || confirmation.Row.Revision != confirmation.Revision))
            LogoutPopup.IsOpen = false;
        SyncLogin();
    }
    private void SyncLogin()
    {
        if (!Active || observed is not { LoginOpen: true } owner)
        {
            StopPolling();
            ClearInputs(); formRow = null;
            LoginPopup.IsOpen = false;
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
                        Style = (Style)FindResource("Settings.Text")
                    });
                    Control input = field.IsSecret
                        ? new PasswordBox { Style = (Style)FindResource("Account.Password") }
                        : new TextBox { Style = (Style)FindResource("Settings.TextBox") };
                    input.Tag = field;
                    input.Margin = new(0, 0, 0, 14);
                    AutomationProperties.SetName(input, field.Label);
                    CredentialInputs.Children.Add(input);
                }
        }
        QrPanel.Visibility = formQr ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = formQr ? Visibility.Collapsed : Visibility.Visible;
        SizePopup();
        if (!LoginPopup.IsOpen) LoginPopup.IsOpen = true;
        if (formQr)
        {
            if (polling is not null) return;
            polling = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            polling.Tick += Poll;
            polling.Start();
        }
        else StopPolling();
    }
    private void SizePopup()
    {
        LoginSurface.Width = Math.Min(360, Math.Max(0, ActualWidth - 32));
        LoginSurface.MaxHeight = Math.Max(0, (window?.ActualHeight ?? ActualHeight) - 64);
    }
    private void StopPolling()
    {
        if (polling is null) return;
        polling.Stop(); polling.Tick -= Poll; polling = null;
    }
    private async void Poll(object? sender, EventArgs e)
    {
        if (Active && LoginPopup.IsOpen && observed is { QrMode: true } owner) await owner.PollLoginAsync();
    }
    private void ClearInputs()
    {
        foreach (var input in CredentialInputs.Children.OfType<Control>())
            if (input is PasswordBox secret) secret.Clear();
            else if (input is TextBox text) text.Clear();
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || Owner is not { } owner || sender is not Button { DataContext: PanAccountRow row } button) return;
        loginAnchor = button;
        await owner.OpenLoginAsync(row);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Owner is { } owner && sender is Button { DataContext: PanAccountRow row })
            await owner.RefreshAsync(row);
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
    private void CloseLogin_Click(object sender, RoutedEventArgs e) => LoginPopup.IsOpen = false;
    private void LoginPopup_Opened(object? sender, EventArgs e)
    {
        if (formQr) QrModeButton.Focus();
        else CredentialInputs.Children.OfType<Control>().FirstOrDefault()?.Focus();
    }
    private async void LoginPopup_Closed(object? sender, EventArgs e)
    {
        StopPolling(); ClearInputs(); formRow = null;
        var anchor = loginAnchor; loginAnchor = null;
        if (observed is { } owner) await owner.CloseLoginAsync();
        if (Active && anchor is { IsVisible: true, IsEnabled: true }) anchor.Focus();
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
        if (!Active || Owner is not { } owner || sender is not Button { DataContext: PanAccountRow row } button ||
            !owner.Rows.Contains(row) || !row.CanLogout) return;
        logout = (row, row.Revision);
        LogoutPopup.PlacementTarget = button;
        LogoutPopup.IsOpen = true;
    }
    private void LogoutPopup_Opened(object? sender, EventArgs e) => CancelLogout.Focus();
    private void LogoutPopup_Closed(object? sender, EventArgs e)
    {
        logout = null;
        var target = LogoutPopup.PlacementTarget as Button;
        LogoutPopup.PlacementTarget = null;
        if (Active && target is { IsVisible: true, IsEnabled: true }) target.Focus();
    }
    private void CancelLogout_Click(object sender, RoutedEventArgs e) => LogoutPopup.IsOpen = false;
    private async void ConfirmLogout_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = logout;
        LogoutPopup.IsOpen = false;
        if (Active && Owner is { } owner && confirmation is { } captured &&
            captured.Row.Revision == captured.Revision) await owner.LogoutAsync(captured.Row);
    }
}
