using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public partial class MusicAccountsView : UserControl
{
    public static readonly DependencyProperty IsCompactTableProperty = DependencyProperty.RegisterAttached(
        "IsCompactTable", typeof(bool), typeof(MusicAccountsView),
        new PropertyMetadata(false));
    public static bool GetIsCompactTable(DependencyObject target) => (bool)target.GetValue(IsCompactTableProperty);
    public static void SetIsCompactTable(DependencyObject target, bool value) => target.SetValue(IsCompactTableProperty, value);

    private readonly ObservableCollection<MusicAccountTableRow> tableRows = [];
    private readonly MusicAccountTableRow[] publicCatalogs = [new("kw", "酷我音乐"), new("mg", "咪咕音乐")];
    private ObservableCollection<MusicPlatformSettingsRow>? tableSource;
    private MusicAccountsController? observed;
    private bool loginShowing, logoutShowing;
    private Button? logoutAnchor;
    private DesktopWindow? window;
    private IDisposable? visibility;
    private CancellationTokenSource? pollingCancellation;
    private Task? polling;
    private DispatcherTimer? smsCountdown;
    private long seenSmsInputEpoch = -1;
    private MusicAccountsController? loginOwner;
    private (MusicAccountsController Owner, MusicPlatformAccount Account)? logoutConfirmation;
    public MusicAccountsController? Accounts { get; private set; }
    public MusicSettingsController? Settings { get; private set; }
    public ReadOnlyObservableCollection<MusicAccountTableRow> AccountTableRows { get; }
    private bool SurfaceVisible => IsLoaded && DesktopVisual.IsVisible(this) && window?.WindowState != DesktopWindowState.Minimized;
    private bool Visible => SurfaceVisible && loginShowing;
    private bool QrVisible => Visible && QrPanel.Visibility == Visibility.Visible;
    private MusicAccountsController? Owner => Accounts;

    public MusicAccountsView()
    {
        AccountTableRows = new(tableRows);
        InitializeComponent();
        ModalStash.Children.Clear();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (!IsLoaded) Detach(); };
        RebuildTableRows();
    }

    internal void Bind(MusicAccountsController accounts, MusicSettingsController settings)
    {
        Detach();
        Accounts = accounts;
        Settings = settings;
        Bindings.Update();
        if (IsLoaded) Attach();
        else RebuildTableRows();
    }

    private async void ShowLogin()
    {
        if (loginShowing) return;
        loginShowing = true;
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(this, LoginSurface, onOpened: () =>
            {
                if (loginShowing) loginOwner ??= Owner;
            });
        }
        finally
        {
            loginShowing = false;
            RetireLoginPopup();
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
                onOpened: () => CancelLogoutButton.Focus(FocusState.Programmatic),
                canRestoreFocus: () => logoutConfirmation is not null);
        }
        finally
        {
            logoutShowing = false;
            var target = logoutAnchor;
            logoutConfirmation = null;
            logoutAnchor = null;
            if (SurfaceVisible && target is { IsEnabled: true } && DesktopVisual.IsVisible(target))
                target.Focus(FocusState.Programmatic);
        }
    }

    private void ObserveTableRows()
    {
        var source = Owner?.PlatformSettings;
        if (ReferenceEquals(tableSource, source) && tableRows.Count > 0) return;
        if (!ReferenceEquals(tableSource, source))
        {
            if (tableSource is not null)
                tableSource.CollectionChanged -= PlatformSettingsChanged;
            tableSource = source;
            if (tableSource is not null)
                tableSource.CollectionChanged += PlatformSettingsChanged;
        }
        RebuildTableRows();
    }

    private void RebuildTableRows()
    {
        tableRows.Clear();
        if ((tableSource ?? Owner?.PlatformSettings) is { } rows)
            foreach (var source in rows) tableRows.Add(new(source));
        foreach (var row in publicCatalogs) tableRows.Add(row);
    }

    private void PlatformSettingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, tableSource)) return;
        CloseTransientPopups();
        RebuildTableRows();
    }

    private void AccountTableScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AccountTable.Width = e.NewSize.Width;
        SetIsCompactTable(this, e.NewSize.Width < 850);
        UpdateColumns(AccountTableHeader);
        UpdateRows(PlatformSettingsList);
    }

    private void AccountCells_Loaded(object sender, RoutedEventArgs e)
    {
        var cells = (Grid)sender;
        UpdateColumns(cells);
        if (cells.Parent is Border row) row.BorderThickness = new(0, ReferenceEquals(cells.DataContext, tableRows.FirstOrDefault()) ? 0 : 1, 0, 0);
    }

    private void UpdateRows(DependencyObject parent)
    {
        if (parent is Grid { Name: "AccountCells" } cells) UpdateColumns(cells);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            UpdateRows(VisualTreeHelper.GetChild(parent, index));
    }

    private void UpdateColumns(Grid cells)
    {
        bool compact = GetIsCompactTable(this);
        for (int index = 0; index < cells.ColumnDefinitions.Count; index++)
            cells.ColumnDefinitions[index].Width = index switch
            {
                0 => new(compact ? 132 : 196),
                2 => new(1, GridUnitType.Star),
                10 => new(compact ? 144 : 146),
                4 or 6 or 8 => new(compact ? 72 : 112),
                _ => new(compact ? 6 : 8)
            };
    }

    public void CloseTransientPopups()
    {
        RetireLogoutPopup();
        RetireLoginPopup();
    }

    private void RetireLoginPopup()
    {
        var owner = loginOwner;
        loginOwner = null;
        DesktopModalHost.CloseFor(this, LoginSurface);
        StopPolling();
        StopSmsCountdown();
        ClearSmsInputs();
        if (owner is not null) _ = Leave(owner);
    }

    private void RetireLogoutPopup()
    {
        logoutConfirmation = null;
        logoutAnchor = null;
        DesktopModalHost.CloseFor(this, LogoutPopupSurface);
    }

    private void Attach()
    {
        if (observed is not null) return;
        observed = Owner;
        if (observed is not null) observed.PropertyChanged += OwnerChanged;
        ObserveTableRows();
        window = DesktopWindow.GetWindow(this);
        if (window is not null)
        {
            window.WindowStateChanged += WindowChanged;
            window.VisibilityChanged += WindowChanged;
        }
        ClearSmsInputs();
        visibility = DesktopVisual.ObserveVisibility(this, _ => VisibilityChanged());
    }

    private void Detach()
    {
        visibility?.Dispose(); visibility = null;
        var old = observed;
        var retiringLogin = loginOwner;
        observed = null;
        if (old is not null) old.PropertyChanged -= OwnerChanged;
        if (tableSource is not null)
            tableSource.CollectionChanged -= PlatformSettingsChanged;
        tableSource = null;
        tableRows.Clear();
        if (window is not null)
        {
            window.WindowStateChanged -= WindowChanged;
            window.VisibilityChanged -= WindowChanged;
        }
        window = null;
        CloseTransientPopups();
        if (old is not null && !ReferenceEquals(old, retiringLogin)) _ = Leave(old);
    }

    private static async Task Leave(MusicAccountsController owner)
    {
        try
        {
            await owner.LeaveAccountQrAsync();
            await owner.LeaveAccountSmsAsync();
        }
        catch { }
    }

    private void WindowChanged(object? sender, EventArgs e) => VisibilityChanged();

    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, observed)) return;
        if (logoutConfirmation is { } confirmation &&
            (!ReferenceEquals(observed, confirmation.Owner) || !confirmation.Owner.CanLogoutAccount(confirmation.Account)))
            RetireLogoutPopup();
        if (e.PropertyName == nameof(MusicAccountsController.AccountSmsInputEpoch)) ClearSmsInputsIfChanged();
        if (e.PropertyName == nameof(MusicAccountsController.AccountSmsRetrySeconds)) RefreshSmsCountdown();
        if (e.PropertyName is nameof(MusicAccountsController.HasAccountQr) or nameof(MusicAccountsController.CanCheckAccountQr) or
            nameof(MusicAccountsController.AccountBusy))
            RefreshPolling();
        if (loginShowing && observed?.SelectedAccount is { HasSession: true } &&
            !observed.AccountBusy && !observed.HasAccountQr)
            RetireLoginPopup();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !SurfaceVisible || Owner is not { CanEdit: true, AccountBusy: false } owner ||
            sender is not Button { Tag: MusicPlatformSettingsRow row } ||
            !owner.PlatformSettings.Contains(row) || !row.ShowLogin) return;
        LoginSurface.Title = row.Name;
        RetireLoginPopup();
        loginOwner = owner;
        QrMode();
        ShowLogin();
        await owner.OpenPlatformLoginAsync(row);
        if (ReferenceEquals(loginOwner, owner)) VisibilityChanged();
    }

    private async void RefreshDetails_Click(object sender, RoutedEventArgs e)
    {
        if (SurfaceVisible && Owner is { } owner && sender is Button { Tag: MusicPlatformSettingsRow row } &&
            owner.PlatformSettings.Contains(row))
            await row.RefreshDetailsAsync();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (loginShowing || logoutShowing || !SurfaceVisible || Owner is not { } owner ||
            sender is not Button { Tag: MusicPlatformSettingsRow row } button ||
            !owner.PlatformSettings.Contains(row) || !row.CanLogout) return;
        RetireLogoutPopup();
        logoutConfirmation = (owner, row.Account);
        logoutAnchor = button;
        ShowLogout();
    }

    private void CancelLogout_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, LogoutPopupSurface);
    private void LogoutPopup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        DesktopModalHost.CloseFor(this, LogoutPopupSurface);
    }

    private async void ConfirmLogout_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = logoutConfirmation;
        DesktopModalHost.CloseFor(this, LogoutPopupSurface);
        if (SurfaceVisible && confirmation is { } captured && ReferenceEquals(Owner, captured.Owner))
            await captured.Owner.LogoutAccountAsync(captured.Account, confirmed: true);
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        if (SurfaceVisible && Owner is { CanEditFeedback: true } owner &&
            sender is CheckBox { Tag: MusicPlatformSettingsRow row } &&
            owner.PlatformSettings.Contains(row) && row.IsLoggedIn)
            await owner.SetPlatformFeedbackAsync(row, !row.FeedbackEnabled);
    }

    private async void FavoritesSync_Click(object sender, RoutedEventArgs e)
    {
        if (SurfaceVisible && Owner is { LibrarySync.CanEdit: true } owner &&
            sender is CheckBox { Tag: MusicPlatformSettingsRow row } &&
            owner.PlatformSettings.Contains(row) && row.IsLoggedIn)
            await owner.SetPlatformSyncAsync(row, "favorites", !row.FavoritesSyncEnabled);
    }

    private async void PlaylistsSync_Click(object sender, RoutedEventArgs e)
    {
        if (SurfaceVisible && Owner is { LibrarySync.CanEdit: true } owner &&
            sender is CheckBox { Tag: MusicPlatformSettingsRow row } &&
            owner.PlatformSettings.Contains(row) && row.IsLoggedIn)
            await owner.SetPlatformSyncAsync(row, "playlists", !row.PlaylistsSyncEnabled);
    }

    private async void ConfirmSync_Click(object sender, RoutedEventArgs e)
    {
        if (SurfaceVisible && Owner is { LibrarySync.CanEdit: true } owner) await owner.ConfirmPlatformSyncAsync();
    }

    private void CloseLogin_Click(object sender, RoutedEventArgs e) => RetireLoginPopup();
    private void QrMode_Click(object sender, RoutedEventArgs e) => QrMode();
    private void SmsMode_Click(object sender, RoutedEventArgs e) => SmsMode();

    private void SmsMode()
    {
        if (Owner?.ShowAccountSms != true) return;
        QrPanel.Visibility = Visibility.Collapsed;
        SmsPanel.Visibility = Visibility.Visible;
        QrModeButton.Tag = Visibility.Collapsed;
        SmsModeButton.Tag = Visibility.Visible;
        StopPolling();
        RefreshSmsCountdown();
        SmsPhoneInput.Focus(FocusState.Programmatic);
    }

    private void QrMode()
    {
        QrPanel.Visibility = Visibility.Visible;
        SmsPanel.Visibility = Visibility.Collapsed;
        QrModeButton.Tag = Visibility.Visible;
        SmsModeButton.Tag = Visibility.Collapsed;
        StopSmsCountdown();
        RefreshPolling();
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) await owner.BeginAccountQrAsync();
    }

    private async void SaveQr_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) await owner.SaveAccountQrAsync();
    }

    private void VisibilityChanged()
    {
        if (!SurfaceVisible)
        {
            CloseTransientPopups();
            return;
        }
        if (!Visible)
        {
            StopPolling();
            StopSmsCountdown();
            ClearSmsInputs();
            if (loginShowing) RetireLoginPopup();
            return;
        }
        RefreshPolling();
        RefreshSmsCountdown();
    }

    private void StopPolling()
    {
        try { pollingCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    private void RefreshPolling()
    {
        if (!QrVisible || observed is not { HasAccountQr: true } owner)
        {
            StopPolling();
            return;
        }
        if (polling is { IsCompleted: false }) return;
        var cancellation = new CancellationTokenSource();
        pollingCancellation = cancellation;
        polling = PollVisible(owner, cancellation);
    }

    private async Task PollVisible(MusicAccountsController owner, CancellationTokenSource cancellation)
    {
        string? challenge = owner.AccountQrIdentity;
        try
        {
            while (QrVisible && ReferenceEquals(observed, owner) &&
                owner.HasAccountQr && owner.AccountQrIdentity == challenge)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
                if (!QrVisible || !ReferenceEquals(observed, owner) || !owner.HasAccountQr ||
                    owner.AccountQrIdentity != challenge) break;
                if (owner.CanCheckAccountQr) await owner.CheckAccountQrAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (ReferenceEquals(observed, owner)) await Leave(owner);
        }
        finally
        {
            if (ReferenceEquals(pollingCancellation, cancellation)) pollingCancellation = null;
            cancellation.Dispose();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (polling?.IsCompleted == true) RefreshPolling();
            });
        }
    }

    private void ClearSmsInputs()
    {
        SmsPhoneInput.Text = "";
        SmsCodeInput.Password = "";
        seenSmsInputEpoch = Owner?.AccountSmsInputEpoch ?? -1;
    }

    private void ClearSmsInputsIfChanged()
    {
        if (seenSmsInputEpoch != Owner?.AccountSmsInputEpoch) ClearSmsInputs();
    }

    private void StopSmsCountdown()
    {
        if (smsCountdown is null) return;
        smsCountdown.Stop();
        smsCountdown.Tick -= SmsTick;
        smsCountdown = null;
    }

    private void RefreshSmsCountdown()
    {
        if (!Visible || SmsPanel.Visibility != Visibility.Visible ||
            observed is not { AccountSmsRetrySeconds: > 0, ShowAccountSms: true })
        {
            StopSmsCountdown();
            return;
        }
        if (smsCountdown is not null) return;
        smsCountdown = new DispatcherTimer()
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        smsCountdown.Tick += SmsTick;
        smsCountdown.Start();
    }

    private void SmsTick(object? sender, object e)
    {
        if (!Visible || observed is null)
        {
            StopSmsCountdown();
            return;
        }
        observed.RefreshAccountSmsCountdown();
        RefreshSmsCountdown();
    }

    private async void SmsSend_Click(object sender, RoutedEventArgs e)
    {
        if (!Visible || Owner is not { CanSendAccountSms: true } owner) return;
        SmsCodeInput.Password = "";
        await owner.SendAccountSmsAsync(SmsPhoneInput.Text, "86");
    }

    private async void SmsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!Visible || Owner is not { CanLoginAccountSms: true } owner) return;
        string captcha = SmsCodeInput.Password;
        SmsCodeInput.Password = "";
        await owner.LoginAccountSmsAsync(SmsPhoneInput.Text, "86", captcha);
    }

    private MusicPlatformSettingsRow ResolveDebugRow(MusicAccountsController owner, string? platform)
    {
        if (owner.PlatformSettings.Count == 0)
            throw DebugApiException.InvalidState("Music account rows are not loaded.");
        if (string.IsNullOrWhiteSpace(platform))
            return owner.PlatformSettings.FirstOrDefault(r => r.ShowLogin) ?? owner.PlatformSettings[0];
        var key = platform.Trim();
        return owner.PlatformSettings.FirstOrDefault(r =>
            string.Equals(r.Platform, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? throw DebugApiException.NotFound($"Music platform not found: '{platform}'.");
    }

    internal async Task<object> DebugAccountLoginAsync(string? platform, bool close, string? mode, CancellationToken cancellationToken)
    {
        if (Owner is not { } owner)
            throw DebugApiException.InvalidState("Music accounts owner is unavailable.");
        if (close)
        {
            RetireLoginPopup();
            return new { executed = true, domain = "music", command = "account.login", closed = true, loginOpen = loginShowing };
        }
        if (observed is null) Attach();
        var row = ResolveDebugRow(owner, platform);
        if (!row.ShowLogin)
            throw DebugApiException.InvalidState($"Music account '{row.Platform}' cannot start login right now.");
        if (logoutShowing) RetireLogoutPopup();
        LoginSurface.Title = row.Name;
        RetireLoginPopup();
        loginOwner = owner;
        QrMode();
        ShowLogin();
        await owner.OpenPlatformLoginAsync(row);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedMode = mode?.Trim().ToLowerInvariant();
        if (normalizedMode == "sms")
        {
            if (!owner.ShowAccountSms)
                throw DebugApiException.InvalidState($"Music account '{row.Platform}' does not support SMS login.");
            SmsMode();
        }
        else if (normalizedMode == "qr")
        {
            QrMode();
        }
        if (ReferenceEquals(loginOwner, owner)) VisibilityChanged();
        return new
        {
            executed = true,
            domain = "music",
            command = "account.login",
            platform = row.Platform,
            name = row.Name,
            loginOpen = loginShowing,
            mode = SmsPanel.Visibility == Visibility.Visible ? "sms" : "qr",
            hasQr = owner.HasAccountQr,
            supportsSms = owner.ShowAccountSms
        };
    }

    internal async Task<object> DebugAccountLogoutAsync(string? platform, bool close, bool confirm, CancellationToken cancellationToken)
    {
        if (Owner is not { } owner)
            throw DebugApiException.InvalidState("Music accounts owner is unavailable.");
        if (close)
        {
            RetireLogoutPopup();
            return new { executed = true, domain = "music", command = "account.logout", closed = true };
        }
        if (observed is null) Attach();
        var row = ResolveDebugRow(owner, platform);
        if (!row.CanLogout)
            throw DebugApiException.InvalidState($"Music account '{row.Platform}' cannot logout right now.");
        if (loginShowing) RetireLoginPopup();
        if (confirm)
        {
            RetireLogoutPopup();
            await owner.LogoutAccountAsync(row.Account, confirmed: true);
            cancellationToken.ThrowIfCancellationRequested();
            return new
            {
                executed = true,
                domain = "music",
                command = "account.logout",
                platform = row.Platform,
                name = row.Name,
                confirmed = true,
                isLoggedIn = row.IsLoggedIn
            };
        }
        RetireLogoutPopup();
        logoutConfirmation = (owner, row.Account);
        logoutAnchor = null;
        ShowLogout();
        return new
        {
            executed = true,
            domain = "music",
            command = "account.logout",
            platform = row.Platform,
            name = row.Name,
            popupOpen = true
        };
    }
}
