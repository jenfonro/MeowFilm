namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private readonly object settingsLifecycleGate = new();
    private Task settingsLifecycleTail = Task.CompletedTask;
    private CancellationTokenSource? settingsActivation;
    private bool settingsRequested;

    public Task ActivateSettingsAsync() => SetSettingsActive(true);
    public Task DeactivateSettingsAsync() => SetSettingsActive(false);

    private Task SetSettingsActive(bool active)
    {
        lock (settingsLifecycleGate)
        {
            if (active && (stopping || disposed)) return Task.CompletedTask;
            if (settingsRequested == active) return settingsLifecycleTail;
            settingsRequested = active;

            CancellationTokenSource? activation = null;
            CancellationTokenSource? retirement = null;
            if (active)
            {
                activation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                settingsActivation = activation;
            }
            else
            {
                retirement = settingsActivation;
                settingsActivation = null;
                try { retirement?.Cancel(); } catch (ObjectDisposedException) { }
            }

            var previous = settingsLifecycleTail;
            return settingsLifecycleTail = Transition(previous, active, activation, retirement);
        }
    }

    private async Task Transition(Task previous, bool active,
        CancellationTokenSource? activation, CancellationTokenSource? retirement)
    {
        try { await previous; }
        catch
        {
            // A failed visible generation cannot prevent the following explicit
            // Loaded/Unloaded generation from owning and cleaning the surface.
        }

        if (active)
        {
            try
            {
                var token = activation!.Token;
                token.ThrowIfCancellationRequested();
                await ActivateSettingsCoreAsync(token);
            }
            catch (OperationCanceledException) when (activation!.IsCancellationRequested) { }
            return;
        }

        try { await DeactivateSettingsCoreAsync(); }
        finally { retirement?.Dispose(); }
    }

    private async Task ActivateSettingsCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var searchHistory = ActivateSearchHistoryAsync(SearchHistorySurface.Settings);
        await ActivatePlatformSettingsAsync(token);
        token.ThrowIfCancellationRequested();

        if (!searchPreferencesLoaded)
        {
            try { await ReadSearchPreferences(lifetime.Token); }
            catch { }
            token.ThrowIfCancellationRequested();
        }
        if (!metadataLoaded)
        {
            try { await ReadMetadataSettings(lifetime.Token); }
            catch { }
            token.ThrowIfCancellationRequested();
        }
        if (!qualityLoaded)
            await EnsureQualitySettingsAsync();
        token.ThrowIfCancellationRequested();
        try { await ReloadSourceExclusions(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { status = ErrorText(error); Changed(nameof(Status)); }
        await searchHistory;
    }

    private async Task DeactivateSettingsCoreAsync()
    {
        Exception? failure = null;
        foreach (var retire in new Func<Task>[]
        {
            RetireAccountTransferAsync,
            () => DeactivateSearchHistoryAsync(SearchHistorySurface.Settings),
            DeactivateQualityAsync,
            () => LibrarySync.DeactivateAsync(),
            DeactivateAccountDetailsAsync
        })
        {
            try { await retire(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
