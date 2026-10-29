using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class MusicAccountsView : UserControl
{
    private MusicCapability? observed;
    private Window? window;
    private CancellationTokenSource? pollingCancellation;
    private Task? polling;
    private DispatcherTimer? smsCountdown;
    private long seenSmsInputEpoch = -1;
    private bool Visible => IsLoaded && IsVisible && LoginPopup.IsOpen &&
        window?.WindowState != WindowState.Minimized;
    private MusicCapability? Owner => DataContext as MusicCapability;

    public MusicAccountsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => VisibilityChanged();
        DataContextChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            Detach();
            Attach();
        };
    }

    private void Attach()
    {
        if (observed is not null) return;
        observed = Owner;
        if (observed is not null) observed.PropertyChanged += OwnerChanged;
        window = Window.GetWindow(this);
        if (window is not null) window.StateChanged += WindowChanged;
        ClearSmsInputs();
        VisibilityChanged();
    }

    private void Detach()
    {
        var old = observed;
        observed = null;
        if (old is not null) old.PropertyChanged -= OwnerChanged;
        if (window is not null) window.StateChanged -= WindowChanged;
        window = null;
        StopPolling();
        StopSmsCountdown();
        LoginPopup.IsOpen = false;
        ClearSmsInputs();
        if (old is not null) _ = Leave(old);
    }

    private static async Task Leave(MusicCapability owner)
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
        if (e.PropertyName == nameof(MusicCapability.AccountSmsInputEpoch)) ClearSmsInputsIfChanged();
        if (e.PropertyName == nameof(MusicCapability.AccountSmsRetrySeconds)) RefreshSmsCountdown();
        if (e.PropertyName is nameof(MusicCapability.HasAccountQr) or nameof(MusicCapability.CanCheckAccountQr) or
            nameof(MusicCapability.AccountBusy))
            RefreshPolling();
        if (LoginPopup.IsOpen && observed?.SelectedAccount is { HasSession: true } &&
            !observed.AccountBusy && !observed.HasAccountQr)
            LoginPopup.IsOpen = false;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner || sender is not Button { Tag: MusicPlatformSettingsRow row }) return;
        LoginTitle.Text = row.Name;
        QrMode();
        LoginPopup.IsOpen = true;
        await owner.OpenPlatformLoginAsync(row);
        VisibilityChanged();
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is CheckBox { Tag: MusicPlatformSettingsRow row })
            await owner.SetPlatformFeedbackAsync(row, !row.FeedbackEnabled);
    }

    private async void FavoritesSync_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is CheckBox { Tag: MusicPlatformSettingsRow row })
            await owner.SetPlatformSyncAsync(row, "favorites", !row.FavoritesSyncEnabled);
    }

    private async void PlaylistsSync_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner && sender is CheckBox { Tag: MusicPlatformSettingsRow row })
            await owner.SetPlatformSyncAsync(row, "playlists", !row.PlaylistsSyncEnabled);
    }

    private async void ConfirmSync_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) await owner.ConfirmPlatformSyncAsync();
    }

    private void CloseLogin_Click(object sender, RoutedEventArgs e) => LoginPopup.IsOpen = false;
    private void QrMode_Click(object sender, RoutedEventArgs e) => QrMode();
    private void SmsMode_Click(object sender, RoutedEventArgs e)
    {
        if (Owner?.ShowAccountSms != true) return;
        QrPanel.Visibility = Visibility.Collapsed;
        SmsPanel.Visibility = Visibility.Visible;
        QrModeButton.Background = System.Windows.Media.Brushes.Transparent;
        SmsModeButton.Background = FindResource("MeowFilm.Brush.Surface") as System.Windows.Media.Brush;
        RefreshSmsCountdown();
        SmsPhoneInput.Focus();
    }

    private void QrMode()
    {
        QrPanel.Visibility = Visibility.Visible;
        SmsPanel.Visibility = Visibility.Collapsed;
        QrModeButton.Background = FindResource("MeowFilm.Brush.Surface") as System.Windows.Media.Brush;
        SmsModeButton.Background = System.Windows.Media.Brushes.Transparent;
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) await owner.BeginAccountQrAsync();
    }

    private async void SaveQr_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) await owner.SaveAccountQrAsync();
    }

    private void ShareQr_Click(object sender, RoutedEventArgs e)
    {
        if (Owner?.AccountQrImage is not BitmapSource image) return;
        try { Clipboard.SetImage(image); }
        catch { }
    }

    private void LoginPopup_Closed(object? sender, EventArgs e)
    {
        StopPolling();
        StopSmsCountdown();
        ClearSmsInputs();
        if (observed is { } owner) _ = Leave(owner);
    }

    private void VisibilityChanged()
    {
        if (!Visible)
        {
            StopPolling();
            StopSmsCountdown();
            ClearSmsInputs();
            if (LoginPopup.IsOpen) LoginPopup.IsOpen = false;
            return;
        }
        RefreshPolling();
        RefreshSmsCountdown();
    }

    private void StopPolling() => pollingCancellation?.Cancel();

    private void RefreshPolling()
    {
        if (!Visible || observed is not { HasAccountQr: true } owner)
        {
            StopPolling();
            return;
        }
        if (polling is { IsCompleted: false }) return;
        var cancellation = new CancellationTokenSource();
        pollingCancellation = cancellation;
        polling = PollVisible(owner, cancellation);
    }

    private async Task PollVisible(MusicCapability owner, CancellationTokenSource cancellation)
    {
        string? challenge = owner.AccountQrIdentity;
        try
        {
            for (int attempt = 0; attempt < 100 && Visible && ReferenceEquals(observed, owner) &&
                owner.HasAccountQr && owner.AccountQrIdentity == challenge; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                if (!Visible || !ReferenceEquals(observed, owner) || !owner.HasAccountQr ||
                    owner.AccountQrIdentity != challenge) break;
                if (owner.CanCheckAccountQr) await owner.CheckAccountQrAsync();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            if (!cancellation.IsCancellationRequested && ReferenceEquals(observed, owner) &&
                owner.HasAccountQr && owner.AccountQrIdentity == challenge)
                await Leave(owner);
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
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (polling?.IsCompleted == true) RefreshPolling();
            }));
        }
    }

    private void ClearSmsInputs()
    {
        SmsPhoneInput.Clear();
        SmsCodeInput.Clear();
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
        smsCountdown = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        smsCountdown.Tick += SmsTick;
        smsCountdown.Start();
    }

    private void SmsTick(object? sender, EventArgs e)
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
        SmsCodeInput.Clear();
        await owner.SendAccountSmsAsync(SmsPhoneInput.Text, "86");
    }

    private async void SmsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!Visible || Owner is not { CanLoginAccountSms: true } owner) return;
        string captcha = SmsCodeInput.Password;
        SmsCodeInput.Clear();
        await owner.LoginAccountSmsAsync(SmsPhoneInput.Text, "86", captcha);
    }
}
