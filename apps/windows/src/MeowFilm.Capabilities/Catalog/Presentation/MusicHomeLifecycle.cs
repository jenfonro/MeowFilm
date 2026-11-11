using System.Collections.Concurrent;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private readonly MusicHomeSnapshotStore homeSnapshotStore;
    private MusicHomeSnapshot? homeSnapshot;
    private Task? startupPreparation;
    private CancellationTokenSource? startupCancellation;
    private bool homeSnapshotRead, homeRefreshRequired = true;
    private string homePlatform = "";
    private int homePage = 1;
    private JsonElement?[] homeCursors = [null];
    private readonly List<JsonElement> homeContentPages = [];
    private Task homeAccountRecovery = Task.CompletedTask;
    private readonly ConcurrentDictionary<string, long> catalogAccountObservations = new();
    private readonly HashSet<string> changedCatalogAccounts = [];
    private string catalogAccountNotice = "";
    internal Task HomeRefresh { get; private set; } = Task.CompletedTask;

    public Task PrepareStartupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping) return Task.FromException(new MusicOperationException("CLOSED"));
        if (startupPreparation is null)
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loadDrains.Add(drained.Task);
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startupCancellation = request;
            startupPreparation = Prepare(request, drained);
            _ = Observe(startupPreparation);
        }
        return startupPreparation.WaitAsync(cancellationToken);

        static async Task Observe(Task work) { try { await work; } catch { /* The caller owns startup failure. */ } }
        async Task Prepare(CancellationTokenSource request, TaskCompletionSource drained)
        {
            await Task.Yield();
            try
            {
                await EnsureAccountsForRequestAsync(request.Token);
                await EnsureCatalogSourceSettingsAsync(request.Token);
                if (!homeSnapshotRead)
                {
                    homeSnapshot = await homeSnapshotStore.ReadAsync(request.Token);
                    homeSnapshotRead = true;
                }
            }
            catch { startupPreparation = null; throw; }
            finally
            {
                if (ReferenceEquals(startupCancellation, request)) startupCancellation = null;
                request.Dispose();
                loadDrains.Remove(drained.Task); drained.TrySetResult();
            }
        }
    }

    private async Task NavigateHomeAsync(CancellationToken token)
    {
        long submission = navigationSubmission;
        try
        {
            await PrepareStartupAsync(token);
            await EnsureCatalogSourceSettingsAsync(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (!stopping && submission == navigationSubmission)
            {
                if (!catalogSourceLoaded) PublishCatalogSourceUnavailable("catalog-home");
                else { status = ErrorText(error); Changed(nameof(Status)); }
            }
            return;
        }
        token.ThrowIfCancellationRequested();
        if (stopping || submission != navigationSubmission) return;
        if (publishedHomeState is null && IsCatalogHome && catalogLoadFlight is { } flight && !flight.Work.IsCompleted &&
            flight.Request.Key.Route == "catalog-home" && flight.Request.Key.Platform == catalogSourcePlatform)
        {
            await SubscribeCatalogLoad(flight, token);
            return;
        }
        RetireCatalogLoad();
        catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
        if (publishedHomeState is { } published && homePlatform == catalogSourcePlatform)
        {
            catalogCursors.Clear(); catalogCursors.AddRange(homeCursors);
            PublishCatalog("catalog-home", "", homePlatform, "歌曲", homePage, published,
                initializeEntries: !homeRefreshRequired);
        }
        else if (homeSnapshot is { } saved && saved.Platform == catalogSourcePlatform)
        {
            MusicCatalogSection[] sections = [];
            foreach (var page in saved.Pages)
                sections = MergeCatalogSections(sections, MusicCatalogDto.Read(page, saved.Platform, "home", "发现音乐").ToArray());
            var state = new CatalogPageState(sections, [], "", "", "发现音乐", "", false, null,
                sections.Sum(section => section.Items.Count));
            homePlatform = saved.Platform;
            homeContentPages.Clear(); homeContentPages.AddRange(saved.Pages);
            PublishCatalog("catalog-home", "", homePlatform, "歌曲", 1, state, initializeEntries: false);
        }
        if (publishedHomeState is { } current && homePlatform == catalogSourcePlatform)
        {
            catalogPages[homePage] = current;
            if (homeRefreshRequired) StartHomeRefresh();
            else StartHomeContinuation();
            return;
        }
        homeRefreshRequired = false;
        var first = LoadCatalogAsync("catalog-home", "", 1, null, token, refresh: true);
        HomeRefresh = Finish();
        await first;
        async Task Finish() { await first; await HomeInitialization; }
    }

    private void StartHomeRefresh()
    {
        if (stopping || !IsCatalogHome || !catalogSourceLoaded || settingsRequested) return;
        homeRefreshRequired = false;
        HomeRefresh = Refresh();
        async Task Refresh()
        {
            await LoadCatalogAsync("catalog-home", "", 1, null, lifetime.Token, refresh: true);
            await HomeInitialization;
        }
    }

    private void SaveHomeContent(string platform, int number, JsonElement reply)
    {
        if (number == 1) homeContentPages.Clear();
        homeContentPages.Add(reply.Clone());
        homePlatform = platform;
        homeSnapshot = new(platform, homeContentPages.ToArray());
        homeCursors = catalogCursors.ToArray();
        var saved = homeSnapshot;
        var write = Save();
        loadDrains.Add(write);
        _ = Observe();
        async Task Save()
        {
            try { await homeSnapshotStore.SaveAsync(saved, lifetime.Token); }
            catch (OperationCanceledException) { }
        }
        async Task Observe()
        {
            try { await write; }
            finally { loadDrains.Remove(write); }
        }
    }

    private void CatalogAccountChanged(string platform)
    {
        var observation = catalogAccountObservations.AddOrUpdate(platform, 1, (_, value) => value + 1);
        Interlocked.Increment(ref accountObservation);
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return;
        _ = Presentation.Dispatcher.InvokeAsync(() =>
        {
            if (stopping) return;
            changedCatalogAccounts.Add(platform);
            // The notification fences authority immediately; delayed UI cleanup
            // must not retire a read or action admitted after that fence.
            if (catalogActionAuthority is { } action && action.Platform == platform && action.Observation < observation)
                InvalidateCatalogAction();
            bool currentRead = catalogLoadFlight is { } flight && flight.Request.Key.Platform == platform &&
                flight.Request.Key.AccountObservation >= observation;
            if (platform == catalogSourcePlatform &&
                !(currentRead && catalogLoadFlight!.Request.Key.Route == "catalog-home") &&
                publishedHomeState?.AccountStamp != CatalogAccountKey(platform))
                RetireHomeAuthority();
            if (platform == catalogLoadedPlatform && !currentRead && catalogAccountStamp != CatalogAccountKey(platform))
                RetireCatalogContent();
            BeginHomeAccountRecovery(forceRead: false);
        });
    }

    private long CatalogAccountObservation(string platform) => catalogAccountObservations.GetValueOrDefault(platform);

    private void RetireHomeAuthority()
    {
        homeRefreshRequired = true;
        homePage = 1; homeCursors = [null];
        if (publishedHomeState is { } home)
            publishedHomeState = home with { More = false, Next = null, AccountStamp = "", ExpiresAt = null };
    }

    private void BeginHomeAccountRecovery(bool forceRead)
    {
        if (stopping || !catalogSourceLoaded || !homeAccountRecovery.IsCompleted) return;
        if (forceRead) accountsLoaded = false;
        homeAccountRecovery = Recover();
        loadDrains.Add(homeAccountRecovery);
        _ = Drain(homeAccountRecovery);
        async Task Drain(Task work) { try { await work; } finally { loadDrains.Remove(work); } }
        async Task Recover()
        {
            await Task.Yield();
            try
            {
                // A UI account mutation owns its settlement in the command lane.
                // Observe after that receipt, never the temporarily fenced session.
                await commandTail;
                if (stopping) return;
                await EnsureAccountsForRequestAsync(lifetime.Token);
                if (stopping) return;
                var account = Accounts.Single(a => a.Platform == catalogSourcePlatform);
                if (forceRead && account.State is "none" or "revoked") RetireHomeAuthority();
                if (homeRefreshRequired && IsCatalogHome) StartHomeRefresh();
                if (changedCatalogAccounts.Any(platform => Accounts.Single(a => a.Platform == platform).State == "revoked"))
                    PublishCatalogAccountNotice("AUTH_EXPIRED");
                changedCatalogAccounts.Clear();
            }
            catch (OperationCanceledException) { }
            catch (MusicOperationException) { /* Preserve the original operation notice. */ }
        }
    }

    private void PublishCatalogAccountNotice(string code)
    {
        // Background content success cannot consume an unobserved action notice.
        // The next explicit navigation/command owns clearing it.
        catalogAccountNotice = MusicCatalogDto.ErrorText(code);
        Changed(nameof(Status));
    }

    private void RecoverHomeAfterAuthenticationFailure()
    {
        // The native account owner revokes the exact failed lease. A local account
        // observation decides whether a guest read is possible; errors never invent one.
        if (stopping || !IsCatalogHome ||
            Accounts.Single(a => a.Platform == catalogSourcePlatform).State is not ("saved" or "verified")) return;
        BeginHomeAccountRecovery(forceRead: true);
    }

    private static bool IsPrivateCatalogCard(MusicCatalogCard? card) =>
        card is not null && (card.RequiresAccount || card.Locator is not null || card.Kind is "daily" or "fm" or "similar");

    private void RequireCatalogAccount(MusicCatalogCard? card)
    {
        if (!IsPrivateCatalogCard(card)) return;
        if (Accounts.Single(account => account.Platform == card!.Platform).State is not ("saved" or "verified"))
            throw new MusicOperationException("AUTH_REQUIRED");
    }
}
