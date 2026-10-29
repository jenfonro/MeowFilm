using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

public sealed class MusicPlatformSettingsRow : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly MusicCapability owner;
    private readonly MusicAccountDetailsViewModel details;
    private readonly MusicFeedbackPlatformRow feedback;
    private readonly MusicLibrarySyncPlatformRow sync;

    internal MusicPlatformSettingsRow(MusicCapability owner, string platform, string name,
        IMusicAccountDetailsSource source)
    {
        this.owner = owner;
        Platform = platform;
        Name = name;
        feedback = owner.FeedbackPlatforms.Single(row => row.Platform == platform);
        sync = owner.LibrarySync.Platforms.Single(row => row.Platform == platform);
        feedback.PropertyChanged += ChildChanged;
        sync.PropertyChanged += ChildChanged;
        owner.LibrarySync.PropertyChanged += ChildChanged;
        details = new(source);
        details.Observed += owner.AccountDetailsObserved;
        details.PropertyChanged += ChildChanged;
        Refresh();
    }

    public string Platform { get; }
    public string Name { get; }
    internal MusicPlatformAccount Account => owner.Accounts.Single(row => row.Platform == Platform);
    public bool IsLoggedIn => Account.State is "saved" or "verified";
    public bool ShowPlatformLogo => Account.State == "none";
    public bool ShowLogin => Account.State is "none" or "revoked";
    public string DisplayName => !IsLoggedIn ? Account.StateText :
        details.DisplayName is { Length: > 0 } detailName ? detailName :
        Account.DisplayName.Length > 0 ? Account.DisplayName : Name;
    public string Membership => IsLoggedIn ? details.MembershipSummary : "";
    public string Subtitle => IsLoggedIn ? Membership : Name;
    public string Avatar => IsLoggedIn ? details.Avatar : "";
    public bool CanRefresh => IsLoggedIn && owner.CanEdit && !owner.AccountBusy && details.CanRefresh;
    public bool CanLogout => owner.CanEdit && owner.CanLogoutAccount(Account);
    public string RefreshText => details.IsBusy ? "刷新中" : "刷新资料";
    public string DetailsStatus => details.Status;
    public bool FeedbackEnabled => feedback.Enabled;
    public bool FavoritesSyncEnabled => sync.Favorites;
    public bool PlaylistsSyncEnabled => sync.Playlists;
    internal MusicFeedbackPlatformRow Feedback => feedback;
    internal MusicLibrarySyncPlatformRow Sync => sync;

    internal void Refresh()
    {
        details.SelectAccount(owner.AccountDetailsSelection(Platform));
        foreach (var property in new[]
        {
            nameof(IsLoggedIn), nameof(ShowPlatformLogo), nameof(ShowLogin), nameof(DisplayName), nameof(Membership), nameof(Subtitle),
            nameof(Avatar), nameof(CanRefresh), nameof(CanLogout), nameof(RefreshText), nameof(DetailsStatus),
            nameof(FeedbackEnabled), nameof(FavoritesSyncEnabled),
            nameof(PlaylistsSyncEnabled)
        })
            Changed(property);
    }

    internal Task ActivateAsync(CancellationToken token)
    {
        Refresh();
        return details.ActivateAsync(token);
    }

    internal Task DeactivateAsync() => details.DeactivateAsync();
    internal Task RefreshDetailsAsync() => CanRefresh ? details.RefreshAsync() : Task.CompletedTask;
    internal void InvalidateDetails() => details.Invalidate(Platform);

    private void ChildChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new(name));

    public async ValueTask DisposeAsync()
    {
        feedback.PropertyChanged -= ChildChanged;
        sync.PropertyChanged -= ChildChanged;
        owner.LibrarySync.PropertyChanged -= ChildChanged;
        details.Observed -= owner.AccountDetailsObserved;
        details.PropertyChanged -= ChildChanged;
        await details.DisposeAsync();
    }
}

public sealed partial class MusicCapability
{
    public ObservableCollection<MusicPlatformSettingsRow> PlatformSettings { get; } = [];

    private void InitializePlatformSettings()
    {
        PlatformSettings.Add(new(this, "wy", "网易云音乐", application));
        PlatformSettings.Add(new(this, "tx", "QQ 音乐", application));
        PlatformSettings.Add(new(this, "kg", "酷狗音乐", application));
        application.AccountDetailsInvalidated += AccountDetailsInvalidated;
    }

    internal MusicPlatformAccount? AccountDetailsSelection(string platform)
    {
        if (stopping || disposed || accountSaveUncertain ||
            AccountBusy && accountOperationPlatform == platform) return null;
        var account = Accounts.FirstOrDefault(row => row.Platform == platform);
        return account?.State is "saved" or "verified" ? account : null;
    }

    internal void AccountDetailsObserved(MusicAccountDetails details)
    {
        if (stopping || disposed || accountSaveUncertain ||
            AccountBusy && accountOperationPlatform == details.Platform) return;
        var account = Accounts.FirstOrDefault(row => row.Platform == details.Platform);
        if (account is null || account.Generation > details.Generation) return;
        UpdateAccount(new(account.Platform, account.Name, details.State,
            details.Profile?.DisplayName ?? "", details.Profile?.UserId ?? "", details.Generation));
    }

    private void AccountDetailsInvalidated(string platform)
    {
        if (page.Dispatcher.HasShutdownStarted || page.Dispatcher.HasShutdownFinished) return;
        _ = page.Dispatcher.InvokeAsync(() =>
        {
            if (!stopping && !disposed)
                PlatformSettings.FirstOrDefault(row => row.Platform == platform)?.InvalidateDetails();
        });
    }

    private void AccountDetailsBindings() => RefreshPlatformSettings();

    internal void RefreshPlatformSetting(string platform) =>
        PlatformSettings.FirstOrDefault(row => row.Platform == platform)?.Refresh();
    private void RefreshPlatformSettings()
    {
        foreach (var row in PlatformSettings) row.Refresh();
    }

    public async Task ActivatePlatformSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (stopping || disposed) return;
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureAccountsAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!feedbackSettingsLoaded)
        {
            try { await ReadFeedbackSettings(lifetime.Token); }
            catch { }
            cancellationToken.ThrowIfCancellationRequested();
        }
        await LibrarySync.ActivateAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(PlatformSettings.Select(row => row.ActivateAsync(cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task OpenPlatformLoginAsync(MusicPlatformSettingsRow row)
    {
        if (stopping || disposed || !PlatformSettings.Contains(row) || row.IsLoggedIn)
            return Task.CompletedTask;
        SelectedAccount = Accounts.First(account => account.Platform == row.Platform);
        return BeginAccountQrAsync();
    }

    public Task SetPlatformFeedbackAsync(MusicPlatformSettingsRow row, bool enabled) =>
        !PlatformSettings.Contains(row) ? Task.CompletedTask :
        SetFeedbackEnabledAsync(row.Feedback, enabled);

    public Task SetPlatformSyncAsync(MusicPlatformSettingsRow row, string kind, bool enabled)
    {
        if (!PlatformSettings.Contains(row) || kind is not ("favorites" or "playlists"))
            return Task.CompletedTask;
        bool confirmed = !enabled || dialogs.Confirm(
            $"开启{row.Name}{(kind == "favorites" ? "收藏歌曲" : "歌单")}同步？",
            "平台同步");
        return LibrarySync.SetAsync(row.Sync, kind, enabled, confirmed);
    }

    public Task ConfirmPlatformSyncAsync()
    {
        if (!LibrarySync.HasConfirmation ||
            !dialogs.Confirm(LibrarySync.Confirmation, "平台同步")) return Task.CompletedTask;
        return LibrarySync.ConfirmAsync(true);
    }

    private async Task DeactivateAccountDetailsAsync()
    {
        await Task.WhenAll(PlatformSettings.Select(row => row.DeactivateAsync()));
    }

    private async ValueTask DisposeAccountDetailsAsync()
    {
        application.AccountDetailsInvalidated -= AccountDetailsInvalidated;
        foreach (var row in PlatformSettings) await row.DisposeAsync();
        PlatformSettings.Clear();
    }
}
