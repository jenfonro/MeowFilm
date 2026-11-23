using System.Collections.ObjectModel;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class PanAccountsView : UserControl
{
    private readonly List<Control> mountedInputs = [];
    private PanAccounts? observed;
    private bool attached, loginShowing, logoutShowing, active;
    private Button? logoutAnchor;
    private DesktopWindow? window;
    private IDisposable? visibility;
    private DispatcherTimer? polling;
    private PanAccountRow? formRow;
    private bool formQr;
    private Button? loginAnchor;
    private (PanAccountRow Row, long? Revision)? logout;
    public ObservableCollection<PanCredentialField> CredentialFields { get; } = [];
    public PanAccounts? Accounts => DataContext as PanAccounts;
    private bool Active => IsLoaded && DesktopVisual.IsVisible(this) && window?.WindowState != DesktopWindowState.Minimized;

    public PanAccountsView()
    {
        InitializeComponent();
        ModalStash.Children.Clear();
        Loaded += (_, _) => { Bindings.Update(); Attach(); };
        Unloaded += (_, _) => { if (!IsLoaded) Detach(); };
        SizeChanged += (_, _) => SizePopup();
        DataContextChanged += (_, _) =>
        {
            Bindings.Update();
            if (ReferenceEquals(observed, Accounts)) return;
            Detach();
            if (IsLoaded) Attach();
        };
    }
    private void Attach()
    {
        if (attached && ReferenceEquals(observed, Accounts)) return;
        if (observed is not null) observed.PropertyChanged -= OwnerChanged;
        observed = Accounts;
        if (observed is not null) observed.PropertyChanged += OwnerChanged;
        if (window is not null)
        {
            window.WindowStateChanged -= WindowChanged;
            window.VisibilityChanged -= WindowChanged;
        }
        window = DesktopWindow.GetWindow(this);
        if (window is not null)
        {
            window.WindowStateChanged += WindowChanged;
            window.VisibilityChanged += WindowChanged;
        }
        visibility?.Dispose();
        visibility = DesktopVisual.ObserveVisibility(this, visible => { _ = VisibilityChanged(); });
        attached = true;
    }
    private void Detach()
    {
        attached = false;
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
        DesktopModalHost.CloseFor(this, LoginSurface);
        DesktopModalHost.CloseFor(this, LogoutPopupSurface);
        logoutAnchor = null;
        if (old is not null) _ = RunOwnerActionAsync(old.DeactivateAsync);
    }
    private async void WindowChanged(object? sender, EventArgs e) => await VisibilityChanged();
    private async Task VisibilityChanged()
    {
        bool next = Active;
        if (active == next) return;
        active = next;
        try
        {
            if (!active)
            {
                StopPolling();
                ClearInputs(); loginAnchor = null; logout = null;
                DesktopModalHost.CloseFor(this, LoginSurface);
                DesktopModalHost.CloseFor(this, LogoutPopupSurface);
                logoutAnchor = null;
                if (observed is { } old) await old.DeactivateAsync();
            }
            else if (observed is { } owner) await owner.ActivateAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, observed)) return;
        if (logout is { } confirmation &&
            (!confirmation.Row.CanLogout || confirmation.Row.Revision != confirmation.Revision))
            DesktopModalHost.CloseFor(this, LogoutPopupSurface);
        if (e.PropertyName is null or "" or nameof(PanAccounts.LoginOpen) or nameof(PanAccounts.LoginRow) or nameof(PanAccounts.QrMode))
            SyncLogin();
    }
    private void SyncLogin()
    {
        if (!Active || observed is not { LoginOpen: true } owner)
        {
            StopPolling();
            if (formRow is not null || loginShowing) ClearInputs();
            formRow = null;
            if (loginShowing) DesktopModalHost.CloseFor(this, LoginSurface);
            return;
        }
        if (!ReferenceEquals(formRow, owner.LoginRow) || formQr != owner.QrMode)
        {
            ClearInputs(); CredentialFields.Clear(); mountedInputs.Clear();
            formRow = owner.LoginRow; formQr = owner.QrMode;
            if (!formQr && formRow is not null)
                foreach (var field in formRow.CredentialFields)
                    CredentialFields.Add(field);
        }
        QrPanel.Visibility = formQr ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = formQr ? Visibility.Collapsed : Visibility.Visible;
        QrModeButton.Tag = QrPanel.Visibility;
        ManualModeButton.Tag = ManualPanel.Visibility;
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
    private void CredentialInput_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: PanCredentialField field } control) return;
        bool activeControl = field.IsSecret ? control is PasswordBox : control is TextBox;
        if (!activeControl) return;
        if (!mountedInputs.Contains(control)) mountedInputs.Add(control);
        control.Unloaded -= CredentialInput_Unloaded;
        control.Unloaded += CredentialInput_Unloaded;
    }
    private void CredentialInput_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control control && !control.IsLoaded)
            mountedInputs.Remove(control);
    }
    private async Task RunOwnerActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception)
        {
            if (Active && (Accounts ?? observed) is { } owner) owner.Notify("网盘操作失败，请重试");
        }
    }
    private async void ShowLogin()
    {
        if (loginShowing) return;
        loginShowing = true;
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(this, LoginSurface, onOpened: () =>
            {
                if (formQr) QrModeButton.Focus(FocusState.Programmatic);
                else mountedInputs.FirstOrDefault()?.Focus(FocusState.Programmatic);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (Active && (Accounts ?? observed) is { } owner) owner.Notify("网盘操作失败，请重试");
        }
        finally
        {
            loginShowing = false;
            StopPolling(); ClearInputs(); formRow = null;
            var anchor = loginAnchor; loginAnchor = null;
            if (observed is { } owner) await RunOwnerActionAsync(owner.CloseLoginAsync);
            if (!loginShowing && Active && anchor is { IsEnabled: true } && DesktopVisual.IsVisible(anchor)) anchor.Focus(FocusState.Programmatic);
        }
    }
    private async void ShowLogout()
    {
        if (logoutShowing) return;
        logoutShowing = true;
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(
                this,
                LogoutPopupSurface,
                onOpened: () => CancelLogout.Focus(FocusState.Programmatic));
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (Active && (Accounts ?? observed) is { } owner) owner.Notify("网盘操作失败，请重试");
        }
        finally
        {
            logoutShowing = false;
            logout = null;
            var target = logoutAnchor;
            logoutAnchor = null;
            if (!logoutShowing && Active && target is { IsEnabled: true } && DesktopVisual.IsVisible(target)) target.Focus(FocusState.Programmatic);
        }
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
        if (!Active || !loginShowing || observed is not { QrMode: true } owner) return;
        await RunOwnerActionAsync(owner.PollLoginAsync);
    }
    private void ClearInputs()
    {
        foreach (var input in mountedInputs)
            if (input is PasswordBox secret) secret.Password = "";
            else if (input is TextBox text) text.Text = "";
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !Active || Accounts is not { } owner || sender is not Button { DataContext: PanAccountRow row } button) return;
        loginAnchor = button;
        await RunOwnerActionAsync(() => owner.OpenLoginAsync(row));
    }
    private async void EditCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !Active || Accounts is not { } owner || sender is not Button { DataContext: PanAccountRow row } button) return;
        loginAnchor = button;
        await RunOwnerActionAsync(() => owner.OpenCredentialsAsync(row));
    }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Accounts is { } owner) await RunOwnerActionAsync(owner.ReloadAsync);
    }
    private async void QrMode_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Accounts is { } owner) await RunOwnerActionAsync(() => owner.SwitchMethodAsync(qr: true));
    }
    private async void ManualMode_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Accounts is { } owner) await RunOwnerActionAsync(() => owner.SwitchMethodAsync(qr: false));
    }
    private async void RetryQr_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Accounts is { } owner) await RunOwnerActionAsync(owner.RetryQrAsync);
    }
    private void CloseLogin_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, LoginSurface);
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || Accounts is not { CanSave: true } owner) return;
        var draft = new Dictionary<string, string>();
        foreach (var input in mountedInputs)
            if (input.Tag is PanCredentialField field)
                draft[field.Id] = input is PasswordBox secret ? secret.Password : ((TextBox)input).Text;
        var save = owner.SaveAsync(draft);
        draft.Clear(); ClearInputs();
        await RunOwnerActionAsync(() => save);
    }
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !Active || Accounts is not { } owner || sender is not Button { DataContext: PanAccountRow row } button ||
            !owner.Rows.Contains(row) || !row.CanLogout) return;
        logout = (row, row.Revision);
        logoutAnchor = button;
        ShowLogout();
    }
    private void CancelLogout_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, LogoutPopupSurface);
    private async void ConfirmLogout_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = logout;
        DesktopModalHost.CloseFor(this, LogoutPopupSurface);
        if (Active && Accounts is { } owner && confirmation is { } captured &&
            captured.Row.Revision == captured.Revision)
            await RunOwnerActionAsync(() => owner.LogoutAsync(captured.Row));
    }

    private PanAccountRow ResolveDebugRow(PanAccounts owner, string? platform)
    {
        if (owner.Rows.Count == 0)
            throw DebugApiException.InvalidState("Pan account rows are not loaded.");
        if (string.IsNullOrWhiteSpace(platform))
            return owner.Rows.FirstOrDefault(r => r.CanLogin) ?? owner.Rows[0];
        var key = platform.Trim();
        return owner.Rows.FirstOrDefault(r =>
            string.Equals(r.Provider, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? throw DebugApiException.NotFound($"Pan platform not found: '{platform}'.");
    }

    internal async Task<object> DebugAccountLoginAsync(string? platform, bool close, string? mode, CancellationToken cancellationToken)
    {
        if (Accounts is not { } owner)
            throw DebugApiException.InvalidState("Pan accounts owner is unavailable.");
        if (close)
        {
            DesktopModalHost.CloseFor(this, LoginSurface);
            await owner.CloseLoginAsync();
            return new { executed = true, domain = "film", command = "account.login", closed = true, loginOpen = owner.LoginOpen };
        }
        if (observed is null) Attach();
        var row = ResolveDebugRow(owner, platform);
        if (logoutShowing) DesktopModalHost.CloseFor(this, LogoutPopupSurface);
        var normalizedMode = mode?.Trim().ToLowerInvariant();
        if (normalizedMode == "manual")
        {
            if (!row.CanEdit)
                throw DebugApiException.InvalidState($"Pan account '{row.Provider}' cannot open credential editor right now.");
            await owner.OpenCredentialsAsync(row);
        }
        else if (row.CanLogin)
        {
            await owner.OpenLoginAsync(row);
            if (normalizedMode == "qr" && !owner.QrMode && row.SupportsQr)
                await owner.SwitchMethodAsync(qr: true);
        }
        else if (row.CanEdit)
        {
            await owner.OpenCredentialsAsync(row);
        }
        else
        {
            throw DebugApiException.InvalidState($"Pan account '{row.Provider}' cannot start login (status: {row.Status ?? "unknown"}).");
        }
        SyncLogin();
        return new
        {
            executed = true,
            domain = "film",
            command = "account.login",
            platform = row.Provider,
            name = row.Name,
            loginOpen = owner.LoginOpen,
            qrMode = owner.QrMode
        };
    }

    internal async Task<object> DebugAccountLogoutAsync(string? platform, bool close, bool confirm, CancellationToken cancellationToken)
    {
        if (Accounts is not { } owner)
            throw DebugApiException.InvalidState("Pan accounts owner is unavailable.");
        if (close)
        {
            DesktopModalHost.CloseFor(this, LogoutPopupSurface);
            logout = null;
            return new { executed = true, domain = "film", command = "account.logout", closed = true };
        }
        if (observed is null) Attach();
        var row = ResolveDebugRow(owner, platform);
        if (!row.CanLogout)
            throw DebugApiException.InvalidState($"Pan account '{row.Provider}' cannot logout (status: {row.Status ?? "unknown"}).");
        if (loginShowing)
        {
            DesktopModalHost.CloseFor(this, LoginSurface);
            await owner.CloseLoginAsync();
        }
        if (confirm)
        {
            DesktopModalHost.CloseFor(this, LogoutPopupSurface);
            await owner.LogoutAsync(row);
            return new
            {
                executed = true,
                domain = "film",
                command = "account.logout",
                platform = row.Provider,
                name = row.Name,
                confirmed = true,
                status = row.Status
            };
        }
        logout = (row, row.Revision);
        logoutAnchor = null;
        ShowLogout();
        return new
        {
            executed = true,
            domain = "film",
            command = "account.logout",
            platform = row.Provider,
            name = row.Name,
            popupOpen = true
        };
    }
}
