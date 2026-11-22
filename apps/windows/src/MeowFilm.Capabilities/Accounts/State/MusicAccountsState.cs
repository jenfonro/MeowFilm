using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowFilm.Capabilities;

/// <summary>Isolated music platform accounts, QR/SMS login, feedback, and library sync state slice.</summary>
public sealed class MusicAccountsState(MusicCapability owner) : INotifyPropertyChanged
{
    internal MusicCapability Owner { get; } = owner;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public bool CanEdit => Owner.CanEdit;
    public bool CanEditFeedback => Owner.CanEditFeedback;
    public bool AccountBusy => Owner.AccountBusy;
    public string AccountNotice => Owner.AccountNotice;
    public string AccountStatus => Owner.AccountStatus;
    public string FeedbackStatus => Owner.FeedbackStatus;

    public bool HasAccountQr => Owner.HasAccountQr;
    public bool CanBeginAccountQr => Owner.CanBeginAccountQr;
    public bool CanCheckAccountQr => Owner.CanCheckAccountQr;
    public bool CanSaveAccountQr => Owner.CanSaveAccountQr;
    public WriteableBitmap? AccountQrImage => Owner.AccountQrImage;
    public string AccountQrAccessibleName => Owner.AccountQrAccessibleName;

    public bool ShowAccountSms => Owner.ShowAccountSms;
    public bool CanSendAccountSms => Owner.CanSendAccountSms;
    public bool CanLoginAccountSms => Owner.CanLoginAccountSms;
    public string AccountSmsSendText => Owner.AccountSmsSendText;
    public int AccountSmsRetrySeconds => Owner.AccountSmsRetrySeconds;
    public long AccountSmsInputEpoch => Owner.AccountSmsInputEpoch;

    public MusicPlatformAccount? SelectedAccount => Owner.SelectedAccount;
    public ObservableCollection<MusicPlatformSettingsRow> PlatformSettings => Owner.PlatformSettings;
    public MusicLibrarySyncViewModel LibrarySync => Owner.LibrarySync;
}
