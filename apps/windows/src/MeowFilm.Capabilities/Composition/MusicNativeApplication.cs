using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Capabilities.Search;
using MeowFilm.Capabilities.Sync;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>
/// Complete in-process contract of the one current Windows music application.
/// The optional runtime executes source scripts only; application capabilities are not optional.
/// </summary>
public interface IMusicApplication : IAsyncDisposable, IScriptPlatformCapabilities,
    IMusicRecommendationSource, IMusicDiscoverySource, IMusicAccountDetailsSource,
    IMusicAccountDetailsNotifications, IMusicPlaybackFeedbackSource, IMusicLibrarySyncSource,
    IMusicSearchSource, IMusicChorusInvalidationSource, IMusicOfficialPlaybackAuthority,
    IMusicVipPageSource
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default);
    Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
    Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default);
    Task AbortAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Single native authority for the App music store. Owns its owners and transport;
/// borrows the App store/runtime. Never compose beside the old Node document writer.
/// </summary>
public interface IMusicPlaybackFeedbackSource
{
    IMusicPlaybackFeedback Feedback { get; }
}

public interface IMusicLibrarySyncSource
{
    event Action? LibrarySyncChanged;
}

public sealed partial class MusicNativeApplication : IMusicApplication
{
    private readonly MusicLibraryOwner library;
    private readonly MusicLibrarySyncService librarySync;
    private readonly MusicAccountOwner accounts;
    private readonly MusicPlatformClientContextOwner clients;
    private readonly MusicVipPageOwner vipPage;
    private readonly MusicPlatformHttp http;
    private readonly MusicPlatformCatalog catalog;
    private readonly MusicNativeCatalogOwner nativeCatalog;
    private readonly MusicScriptOwner scripts;
    private readonly MusicLyricApplication lyrics;
    private readonly CatalogSessions sessions;
    private readonly object gate = new();
    private CancellationTokenSource lifetime = new();
    private TaskCompletionSource drained = Completed();
    private Task? preparation, resumption, release, disposal;
    private int pending;
    private bool stopping, closed, accountReceiptUnknown;
    private long accountReceiptEpoch;

    public MusicNativeApplication(IProtectedDocumentStore store, IRuntimeClient scriptRuntime)
        : this(store, scriptRuntime, null) { }

    /// <summary>
    /// Trusted synthetic-transport seam. Fixed platform origins, redirect rejection and
    /// credential scopes still apply. The application owns the supplied handler.
    /// </summary>
    public MusicNativeApplication(IProtectedDocumentStore store, IRuntimeClient scriptRuntime,
        HttpMessageHandler? transportHandler)
        : this(store, scriptRuntime, transportHandler, null) { }

    public MusicNativeApplication(IProtectedDocumentStore store, IRuntimeClient scriptRuntime,
        HttpMessageHandler? transportHandler,
        Func<IMusicPlatformSessionSource, MusicPlatformHttp, IMusicFeedbackProtocol>? feedbackFactory)
        : this(store, scriptRuntime, transportHandler, feedbackFactory, null) { }

    // Internal deterministic lifecycle seam; public constructors always assemble
    // the real service with the same account owner and restricted HTTP transport.
    internal MusicNativeApplication(IProtectedDocumentStore store, IRuntimeClient scriptRuntime,
        HttpMessageHandler? transportHandler,
        Func<IMusicPlatformSessionSource, MusicPlatformHttp, IMusicFeedbackProtocol>? feedbackFactory,
        Func<IMusicPlatformSessionSource, MusicPlatformHttp, IMusicSearchDiscovery>? discoveryFactory,
        TimeProvider? searchClock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scriptRuntime);
        http = transportHandler is null ? new MusicPlatformHttp() : MusicPlatformHttp.CreateWithHandler(transportHandler);
        clients = new(store, token => AccountClientContextImport.ReadAsync(store, token));
        var clientProtocol = new MusicPlatformClientProtocol(http);
        library = new(store);
        accounts = new(store, new MusicAccountProtocol(http, clients, clientProtocol: clientProtocol), clients, clientProtocol);
        vipPage = new(accounts, new MusicVipPageProtocol(http, clients));
        officialAudio = new(http, clients);
        sourceCache = new(store);
        sessions = new(accounts);
        catalog = new(sessions, http, clients, clientProtocol: clientProtocol);
        nativeCatalog = new(catalog, library);
        recommendationQueue = new(library);
        recommendations = new(catalog, recommendationQueue, SelectRecommendationAsync, readAsync: ReadRecommendationsAsync);
        searchCoordinator = new(catalog, searchClock, searchAsync: SearchCatalogAsync);
        var discoverySessions = new DiscoverySessions(accounts);
        discovery = discoveryFactory?.Invoke(discoverySessions, http) ?? new NativeMusicSearchDiscovery(discoverySessions, http, clients);
        scripts = new(library, scriptRuntime);
        lyrics = new(library, new NativeMusicLyrics(sessions, http, clients), scripts.ExecuteAsync);
        librarySync = new(library, accounts, http, clients, http.SendMusicLibrarySyncCloudAsync);
        Feedback = new(new MusicFeedbackSettingsOwner(store),
            feedbackFactory?.Invoke(accounts, http) ?? new NativeMusicFeedbackProtocol(accounts, http, clients), accounts);
        // Fence recommendation callbacks synchronously before any catalog cleanup.
        accounts.SessionChanged += RecommendationAccountChanged;
        accounts.SessionChanged += nativeCatalog.Invalidate;
        accounts.SessionChanged += searchCoordinator.Invalidate;
        accounts.SessionChanged += lyrics.Invalidate;
        accounts.SessionChanged += Feedback.AccountChanged;
        accounts.SessionChanged += librarySync.Invalidate;
        accounts.SessionChanged += AccountDetailsChanged;
        accounts.SessionChanged += DiscoveryAccountChanged;
        accounts.SessionChanged += InvalidateChorus;
    }

    public MusicFeedbackController Feedback { get; }
    IMusicPlaybackFeedback IMusicPlaybackFeedbackSource.Feedback => Feedback;
    public event Action? LibrarySyncChanged
    {
        add => librarySync.StateChanged += value;
        remove => librarySync.StateChanged -= value;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping || closed) return Task.FromException(new InvalidOperationException("CLOSED"));
            generation = lifetime.Token;
        }
        return InitializeOwnersAsync(generation, cancellationToken);
    }
    private async Task InitializeOwnersAsync(CancellationToken generation, CancellationToken caller)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(generation, caller);
        // Explicit local startup/retry can reconcile a receipt; ordinary request
        // acquisition never replays or automatically clears an unknown write.
        if (clients.NeedsReconciliation) await clients.ReloadAsync(request.Token).ConfigureAwait(false);
        await clients.InitializeAsync(request.Token).ConfigureAwait(false);
        await librarySync.StartAsync(request.Token).ConfigureAwait(false);
    }

    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> ScriptCapabilities
        => scripts.ScriptCapabilities;

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping) return Failed("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            var token = request.Token;
            token.ThrowIfCancellationRequested();
            if (method is null) return Failed("INVALID_REQUEST");
            if (method == "music.official.candidates")
                return await ExecuteOfficialCandidatesAsync(parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.official.", StringComparison.Ordinal))
                return await ExecuteOfficialAudioAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.accountDetails.", StringComparison.Ordinal))
                return await ExecuteAccountDetailsAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.recommendations.", StringComparison.Ordinal))
                return await ExecuteRecommendationsAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.discovery.", StringComparison.Ordinal))
                return await ExecuteDiscoveryAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.search.", StringComparison.Ordinal))
                return await ExecuteSearchAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.librarySync.", StringComparison.Ordinal))
                return await librarySync.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
            if (method == "music.lyrics.lookup")
            {
                using var frame = sessions.Begin();
                var result = await lyrics.ExecuteAsync(parameters, token).ConfigureAwait(false);
                if (result.TryGetProperty("code", out var error) && error.GetString() is "AUTH_EXPIRED" or "AUTH_REQUIRED" &&
                    frame.Session is { } failed) accounts.Revoke(failed);
                return result;
            }
            if (method.StartsWith("music.accounts.", StringComparison.Ordinal))
            {
                if (method == "music.accounts.list" && clients.NeedsReconciliation)
                    await clients.ReloadAsync(token).ConfigureAwait(false);
                long receiptEpoch;
                lock (gate) receiptEpoch = accountReceiptEpoch;
                var receipt = await accounts.CallAsync(method, parameters, token).ConfigureAwait(false);
                lock (gate)
                {
                    if (receipt.TryGetProperty("code", out var error) &&
                        error.GetString() is "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED")
                    { accountReceiptUnknown = true; accountReceiptEpoch++; }
                    else if (method == "music.accounts.list" && receipt.TryGetProperty("ok", out var read) && read.ValueKind == JsonValueKind.True)
                    {
                        if (receiptEpoch == accountReceiptEpoch) accountReceiptUnknown = false;
                        // Explicit reconciliation only; an older concurrent read
                        // cannot clear a newer unknown receipt.
                    }
                }
                return receipt;
            }
            if (method.StartsWith("music.catalog.", StringComparison.Ordinal))
            {
                // Capture the exact issued lease used by the actual catalog request,
                // not a later Acquire after a failed response. Concurrent calls have
                // independent frames; an old auth failure cannot revoke a replacement.
                using var frame = sessions.Begin();
                var result = await ExecuteCatalogWithRecommendationLaneAsync(method, parameters, token).ConfigureAwait(false);
                if (result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False &&
                    result.TryGetProperty("code", out var code) &&
                    code.GetString() is "AUTH_EXPIRED" or "AUTH_REQUIRED")
                {
                    if (frame.Session is { } failed) accounts.Revoke(failed);
                    nativeCatalog.Invalidate(frame.Platform);
                }
                return result;
            }
            if (method.StartsWith("music.sources.", StringComparison.Ordinal) ||
                method.StartsWith("music.online.", StringComparison.Ordinal) ||
                method.StartsWith("music.transfer.", StringComparison.Ordinal))
                return await scripts.ExecuteAsync(method, parameters, token).ConfigureAwait(false);

            bool changesLyrics = method is "music.settings.save" or "music.metadata.settings.save" or
                "music.lyrics.local.save" or "music.lyrics.local.remove";
            if (changesLyrics) lyrics.Invalidate();
            // All final queue mutations share the coordinator's one lane.
            // Native recommendation commands stay private.
            var reply = await recommendationQueue.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
            if (changesLyrics) lyrics.Invalidate();
            if (method == "music.settings.save" && reply.TryGetProperty("ok", out var saved) && saved.ValueKind == JsonValueKind.True)
                scripts.CancelPlayback();
            return reply;
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicPlatformException error) { return Failed(error.Code); }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate)
        {
            if (resumption is { IsCompleted: false } recovering)
                return PrepareAfterResumeAsync(recovering, cancellationToken);
            stopping = true;
            task = preparation ??= PrepareCoreAsync(drained.Task);
        }
        // Caller cancellation only stops waiting, never abandons an admitted write
        // or restarts preparation. The App retains borrowed dependencies.
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task PrepareAfterResumeAsync(Task recovering, CancellationToken token)
    {
        await recovering.ConfigureAwait(false);
        await PrepareShutdownAsync(token).ConfigureAwait(false);
    }

    private async Task PrepareCoreAsync(Task pendingOperations)
    {
        await Task.Yield();
        Exception? failure = null;
        try { lifetime.Cancel(); } catch (Exception e) { failure = e; }
        async Task Attempt(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); } catch (Exception e) { failure ??= e; }
        }
        // Internal preparation must not wait a concurrent resumption which is
        // itself waiting for this preparation to finish.
        await Attempt(PrepareRecommendationCoreAsync).ConfigureAwait(false);
        await Attempt(vipPage.PrepareShutdownAsync).ConfigureAwait(false);
        try { nativeCatalog.Invalidate(); } catch (Exception e) { failure ??= e; }
        try { lyrics.Invalidate(); } catch (Exception e) { failure ??= e; }
        lock (gate) issuedDiscoveryGeneration = 0;
        try { discovery.Invalidate(); } catch (Exception e) { failure ??= e; }
        await Attempt(discovery.CancelAndDrainAsync).ConfigureAwait(false);
        await Attempt(() => searchCoordinator.PrepareShutdownAsync()).ConfigureAwait(false);
        await Attempt(() => librarySync.PrepareShutdownAsync()).ConfigureAwait(false);
        // Native mutations ignore cancellation after CAS admission. Their receipts
        // must settle before the final library preparation, even on earlier failure.
        await pendingOperations.ConfigureAwait(false);
        await Attempt(() => Feedback.PrepareShutdownAsync()).ConfigureAwait(false);
        await Attempt(() => scripts.PrepareShutdownAsync()).ConfigureAwait(false);
        await Attempt(() => library.PrepareShutdownAsync()).ConfigureAwait(false);
        await Attempt(clients.PrepareShutdownAsync).ConfigureAwait(false);
        // The account port may have returned an unknown receipt while its physical
        // CAS is still running. Without an explicit list/reconciliation, preparation
        // must fail rather than authorize the capability to dispose the shared App store.
        lock (gate) if (accountReceiptUnknown) failure ??= new MusicAccountException("COMMIT_UNKNOWN");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate)
        {
            if (closed) return Task.FromException(new InvalidOperationException("CLOSED"));
            if (preparation is null && resumption is null && !recommendationPreparing) return Task.CompletedTask;
            task = resumption ??= ResumeCoreAsync(preparation);
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task ResumeCoreAsync(Task? prepare)
    {
        await Task.Yield();
        if (prepare is not null) { try { await prepare.ConfigureAwait(false); } catch { /* Unknown state is not reconciled here. */ } }
        lock (gate) if (closed) throw new InvalidOperationException("CLOSED");
        // The Module may have sealed only recommendations before it ever reached
        // whole-app preparation. Cancel and settle that startup/read/callback scope
        // before restoring any recommendation admission.
        bool recommendationWasPreparing;
        lock (gate) recommendationWasPreparing = recommendationPreparing;
        bool requiresReconciliation = false;
        if (recommendationWasPreparing)
        {
            try { await PrepareRecommendationCoreAsync().ConfigureAwait(false); }
            catch (RecommendationException error) when (error.Code == "COMMIT_UNKNOWN")
            {
                // Coordinator raises this only after its actual work has settled.
                // Restore controls, not recommendation admission: the sole owner
                // must retain the nonce until an explicit read reconciliation.
                requiresReconciliation = true;
            }
        }
        await clients.CancelShutdownPreparationAsync().ConfigureAwait(false);
        await library.CancelShutdownPreparationAsync().ConfigureAwait(false);
        await scripts.CancelShutdownPreparationAsync().ConfigureAwait(false);
        await librarySync.CancelShutdownPreparationAsync().ConfigureAwait(false);
        await searchCoordinator.CancelShutdownPreparationAsync().ConfigureAwait(false);
        await vipPage.CancelShutdownPreparationAsync().ConfigureAwait(false);
        Feedback.CancelShutdownPreparation();
        // All borrowed owners are usable before the coordinator can admit new work.
        // Resume clears the old session; it does not restart playback or HTTP.
        if (!requiresReconciliation) recommendations.Resume();
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("CLOSED");
            if (!requiresReconciliation)
            {
                recommendationLifetime.Dispose(); recommendationLifetime = new();
                recommendationCancellation = null;
            }
            lifetime.Dispose(); lifetime = new();
            preparation = null; resumption = null; stopping = false;
            recommendationPreparing = requiresReconciliation;
            recommendationReconciliationPending = requiresReconciliation;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            var prepare = BeginFinalization();
            return new(disposal ??= DisposeCoreAsync(prepare, release!));
        }
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            _ = BeginFinalization();
            return cancellationToken.CanBeCanceled ? release!.WaitAsync(cancellationToken) : release!;
        }
    }

    // Called under gate. Explicit abort ignores a failed save preparation, but
    // never ignores actual cleanup/drain failures. Ordinary dispose preserves both.
    private Task BeginFinalization()
    {
        var recovering = resumption;
        closed = true; stopping = true;
        // Do not route through PrepareAfterResume: a concurrent resume is fenced
        // and may now fail CLOSED. We still must drain the current generation.
        var prepare = preparation ??= PrepareCoreAsync(drained.Task);
        release ??= ReleaseCoreAsync(prepare, recovering);
        return prepare;
    }

    private static async Task DisposeCoreAsync(Task prepare, Task cleanup)
    {
        Exception? failure = null;
        try { await prepare.ConfigureAwait(false); } catch (Exception e) { failure = e; }
        try { await cleanup.ConfigureAwait(false); } catch (Exception e) { failure ??= e; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task ReleaseCoreAsync(Task prepare, Task? recovering)
    {
        await Task.Yield();
        Exception? failure = null;
        async Task Attempt(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); } catch (Exception e) { failure ??= e; }
        }
        try { await prepare.ConfigureAwait(false); } catch { /* Preserve failure in DisposeCore, not the explicit abort receipt. */ }
        if (recovering is not null) { try { await recovering.ConfigureAwait(false); } catch { } }
        await Attempt(() => recommendations.DisposeAsync().AsTask()).ConfigureAwait(false);
        lock (gate)
        {
            if (recommendationBinding is { } binding) Interlocked.Exchange(ref binding.Revoked, 1);
            recommendationBinding = null; recommendationBindingEpoch++;
        }
        await Attempt(() => scripts.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => lyrics.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => nativeCatalog.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => searchCoordinator.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => discovery.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => Feedback.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => librarySync.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => vipPage.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => accounts.PrepareCloseAsync()).ConfigureAwait(false);
        await Attempt(() => accounts.DisposeAsync().AsTask()).ConfigureAwait(false);
        lock (gate) officialAccepted.Clear();
        accounts.SessionChanged -= RecommendationAccountChanged;
        accounts.SessionChanged -= nativeCatalog.Invalidate;
        accounts.SessionChanged -= searchCoordinator.Invalidate;
        accounts.SessionChanged -= lyrics.Invalidate;
        accounts.SessionChanged -= Feedback.AccountChanged;
        accounts.SessionChanged -= librarySync.Invalidate;
        accounts.SessionChanged -= AccountDetailsChanged;
        accounts.SessionChanged -= DiscoveryAccountChanged;
        accounts.SessionChanged -= InvalidateChorus;
        // Explicit finalization may follow failed preparation. Abort fences memory;
        // it neither retries a commit nor declares an unknown receipt rolled back.
        await Attempt(() => library.AbortAsync()).ConfigureAwait(false);
        try { catalog.Dispose(); } catch (Exception e) { failure ??= e; }
        await Attempt(() => clients.DisposeAsync().AsTask()).ConfigureAwait(false);
        try { http.Dispose(); } catch (Exception e) { failure ??= e; }
        try { recommendationLifetime.Dispose(); } catch (Exception e) { failure ??= e; }
        try { lifetime.Dispose(); } catch (Exception e) { failure ??= e; }
        searchPolicyLane.Dispose();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static JsonElement Failed(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(); return result;
    }

    /// <summary>Native-only operation context; credentials never enter result DTOs or diagnostics.</summary>
    private sealed class CatalogSessions(MusicAccountOwner owner) : IMusicPlatformSessionSource
    {
        private readonly AsyncLocal<Frame?> current = new();
        internal Frame Begin(bool awaitPhysical = false)
        {
            var frame = new Frame(this, current.Value, awaitPhysical);
            current.Value = frame; return frame;
        }
        public async ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        {
            // KW/MG public catalog calls do not create fictitious account owners.
            if (platform is "kw" or "mg") return null;
            var frame = current.Value;
            if (frame?.Captured == true)
                return frame.Reuse(platform, cancellationToken);
            var session = frame?.AwaitPhysical == true
                ? await owner.AcquireSettledAsync(platform, cancellationToken).ConfigureAwait(false)
                : await owner.AcquireAsync(platform, cancellationToken).ConfigureAwait(false);
            frame?.Capture(platform, session);
            return session;
        }
        public bool IsCurrent(MusicPlatformSession session) => owner.IsCurrent(session);
        internal sealed class Frame(CatalogSessions source, Frame? previous, bool awaitPhysical) : IDisposable
        {
            internal bool AwaitPhysical { get; } = awaitPhysical;
            private bool captured;
            internal bool Captured => captured;
            internal MusicPlatformSession? Session { get; private set; }
            internal string? Platform { get; private set; }
            internal MusicPlatformSession? Reuse(string platform, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Platform != platform) throw new MusicAccountException("INVALID_REQUEST");
                return Session;
            }
            internal void Capture(string platform, MusicPlatformSession? session)
            {
                if (captured) return;
                Platform = platform; Session = session; captured = true;
            }
            public void Dispose() { source.current.Value = previous; Session = null; }
            public override string ToString() => "Catalog session context (redacted)";
        }
    }
}
