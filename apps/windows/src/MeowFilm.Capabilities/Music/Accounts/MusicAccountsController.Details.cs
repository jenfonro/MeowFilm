using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed class MusicPlatformSettingsRow : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly MusicAccountsController owner;
    private readonly MusicAccountDetailsViewModel details;
    private readonly MusicFeedbackPlatformRow feedback;
    private readonly MusicLibrarySyncPlatformRow sync;

    internal MusicPlatformSettingsRow(MusicAccountsController owner, MusicFeedbackPlatformRow feedback,
        string platform, string name, MusicAccountOwner source)
    {
        this.owner = owner;
        Platform = platform;
        Name = name;
        this.feedback = feedback;
        sync = owner.LibrarySync.Platforms.Single(row => row.Platform == platform);
        feedback.PropertyChanged += ChildChanged;
        sync.PropertyChanged += ChildChanged;
        owner.LibrarySync.PropertyChanged += ChildChanged;
        details = new(source);
        details.Observed += owner.AccountDetailsObserved;
        details.PropertyChanged += ChildChanged;
        Refresh();
    }

    private static readonly string[] AllProperties =
    [
        nameof(IsLoggedIn), nameof(ShowLogin), nameof(CanLogin), nameof(CanEditFeedback),
        nameof(CanEditSync), nameof(ShowActionDash), nameof(DisplayName), nameof(Membership),
        nameof(Avatar), nameof(CanRefresh), nameof(CanLogout), nameof(RefreshText), nameof(DetailsStatus),
        nameof(FeedbackEnabled), nameof(FavoritesSyncEnabled), nameof(PlaylistsSyncEnabled)
    ];
    private static readonly string[] FeedbackProperties = [nameof(FeedbackEnabled), nameof(CanEditFeedback)];
    private static readonly string[] SyncProperties = [nameof(FavoritesSyncEnabled), nameof(PlaylistsSyncEnabled), nameof(CanEditSync)];
    private bool refreshing;

    public string Platform { get; }
    public string Name { get; }
    internal MusicPlatformAccount Account => owner.Accounts.Single(row => row.Platform == Platform);
    public bool IsLoggedIn => Account.State is "saved" or "verified";
    public bool ShowLogin => Account.State is "none" or "revoked";
    public bool CanLogin => ShowLogin && owner.CanEdit && !owner.AccountBusy;
    public bool CanEditFeedback => IsLoggedIn && owner.CanEditFeedback;
    public bool CanEditSync => IsLoggedIn && owner.LibrarySync.CanEdit;
    public bool ShowActionDash => !IsLoggedIn && !ShowLogin;
    public string DisplayName => !IsLoggedIn ? Account.StateText :
        details.DisplayName is { Length: > 0 } detailName ? detailName :
        Account.DisplayName.Length > 0 ? Account.DisplayName : Name;
    public string Membership => IsLoggedIn ? details.MembershipSummary : "";
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
        if (refreshing) return;
        refreshing = true;
        try
        {
            details.SelectAccount(owner.AccountDetailsSelection(Platform));
            Notify(AllProperties);
        }
        finally { refreshing = false; }
    }

    internal Task ActivateAsync(CancellationToken token)
    {
        Refresh();
        return details.ActivateAsync(token);
    }

    internal Task DeactivateAsync() => details.DeactivateAsync();
    internal Task RefreshDetailsAsync() => CanRefresh ? details.RefreshAsync() : Task.CompletedTask;
    internal void InvalidateDetails() => details.Invalidate(Platform);

    private void ChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (refreshing) return;
        if (ReferenceEquals(sender, details))
        {
            string? mapped = e.PropertyName switch
            {
                nameof(MusicAccountDetailsViewModel.DisplayName) => nameof(DisplayName),
                nameof(MusicAccountDetailsViewModel.MembershipSummary) => nameof(Membership),
                nameof(MusicAccountDetailsViewModel.Avatar) => nameof(Avatar),
                nameof(MusicAccountDetailsViewModel.CanRefresh) => nameof(CanRefresh),
                nameof(MusicAccountDetailsViewModel.IsBusy) => nameof(RefreshText),
                nameof(MusicAccountDetailsViewModel.Status) => nameof(DetailsStatus),
                _ => null
            };
            if (mapped is not null) Changed(mapped);
            return;
        }
        if (ReferenceEquals(sender, feedback)) { Notify(FeedbackProperties); return; }
        if (ReferenceEquals(sender, sync)) { Notify(SyncProperties); return; }
        if (ReferenceEquals(sender, owner.LibrarySync)) Changed(nameof(CanEditSync));
    }
    private void Notify(string[] properties)
    {
        foreach (var property in properties) Changed(property);
    }
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

public sealed partial class MusicAccountsController
{
    public ObservableCollection<MusicPlatformSettingsRow> PlatformSettings { get; } = [];

    private void InitializePlatformSettings()
    {
        foreach (var platform in AccountRules.Platforms)
        {
            var feedback = settings.FeedbackPlatforms.Single(row => row.Platform == platform);
            PlatformSettings.Add(new(this, feedback, platform, MusicPlatformInfo.DisplayName(platform), application.Accounts));
        }
    }

    internal MusicPlatformAccount? AccountDetailsSelection(string platform)
    {
        if (stopping || disposed ||
            AccountBusy && accountOperationPlatform == platform) return null;
        var account = Accounts.FirstOrDefault(row => row.Platform == platform);
        return account?.State is "saved" or "verified" ? account : null;
    }

    internal void AccountDetailsObserved(MusicAccountDetails details)
    {
        if (stopping || disposed ||
            AccountBusy && accountOperationPlatform == details.Platform) return;
        var account = Accounts.FirstOrDefault(row => row.Platform == details.Platform);
        if (account is null || account.Generation > details.Generation) return;
        UpdateAccount(new(account.Platform, account.Name, details.State,
            details.Profile?.DisplayName ?? "", details.Profile?.UserId ?? "", details.Generation));
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
        if (!settings.FeedbackSettingsLoaded)
        {
            try { await settings.ReadFeedbackSettings(lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "feedback", "feedback_settings_preload_failed",
                    new { error = error.Message });
            }
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
        settings.SetFeedbackEnabledAsync(row.Feedback, enabled);

    public async Task SetPlatformSyncAsync(MusicPlatformSettingsRow row, string kind, bool enabled)
    {
        if (!PlatformSettings.Contains(row) || kind is not ("favorites" or "playlists"))
            return;
        bool confirmed = !enabled || await ConfirmAsync(
            $"开启{row.Name}{(kind == "favorites" ? "收藏歌曲" : "歌单")}同步？",
            "平台同步", lifetime.Token);
        await LibrarySync.SetAsync(row.Sync, kind, enabled, confirmed);
    }

    public async Task ConfirmPlatformSyncAsync()
    {
        if (!LibrarySync.HasConfirmation ||
            !await ConfirmAsync(LibrarySync.Confirmation, "平台同步", lifetime.Token)) return;
        await LibrarySync.ConfirmAsync(true);
    }

    internal async Task DeactivateAccountDetailsAsync()
    {
        await Task.WhenAll(PlatformSettings.Select(row => row.DeactivateAsync()));
    }

    internal async ValueTask DisposeAccountDetailsAsync()
    {
        application.Accounts.SessionChanged -= OnAccountSessionChanged;
        settings.PropertyChanged -= OnSettingsChanged;
        foreach (var row in PlatformSettings) await row.DisposeAsync();
        PlatformSettings.Clear();
    }
}
