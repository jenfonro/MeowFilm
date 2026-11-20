using System.ComponentModel;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Capabilities;

public sealed class MusicVipPage : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMusicVipPageSource source;
    private readonly OwnerDispatcher dispatcher;
    private CancellationTokenSource? request;
    private Task transition = Task.CompletedTask;
    private Task? preparation, disposal;
    private MusicVipAccountSummary? account;
    private MusicVipSignCardData? sign;
    private MusicVipBannerData? banner;
    private long epoch, generation;
    private bool signedToday, signUnconfirmed, viewLoaded, vipRoute, surfaceActive, stopping, disposed;

    internal MusicVipPage(IMusicVipPageSource source, OwnerDispatcher dispatcher)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        source.VipPageInvalidated += Invalidated;
    }

    public MusicVipAccountSummary? Account => account;
    public MusicVipSignCardData? Sign => sign;
    public MusicVipBannerData? Banner => banner;
    public bool ShowAccount => account is not null;
    public bool ShowSign => sign is not null;
    public bool ShowBanner => banner is not null;
    public bool HasCards => ShowAccount || ShowSign || ShowBanner;
    public bool IsBusy => request is not null;
    public bool SignedToday => signedToday;
    public string SignButtonText => signedToday && sign?.ButtonText.Length > 0
        ? "已打卡" : sign?.ButtonText ?? "";
    public bool ShowSignButton => SignButtonText.Length > 0;
    public bool CanSign => surfaceActive && !stopping && !disposed && request is null &&
        generation > 0 && !signedToday && !signUnconfirmed &&
        sign is { ButtonText.Length: > 0 };
    public bool CanOpenBanner => surfaceActive && !stopping && !disposed && banner is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task SetSurfaceAsync(bool loaded, bool isVipRoute)
    {
        viewLoaded = loaded;
        vipRoute = isVipRoute;
        var shouldActivate = viewLoaded && vipRoute && !stopping && !disposed;
        if (!shouldActivate)
        {
            surfaceActive = false;
            return Retire();
        }
        if (surfaceActive) return transition;
        surfaceActive = true;
        Clear();
        return QueueRead();
    }

    public Task SignAsync()
    {
        if (!CanSign) return Task.CompletedTask;
        var expectedGeneration = generation;
        var previous = transition;
        var cancellation = new CancellationTokenSource();
        var ticket = ++epoch;
        request = cancellation;
        Notify();
        var work = SignCoreAsync(previous, cancellation, ticket, expectedGeneration);
        transition = ObserveSignAsync(work);
        return work;
    }

    internal bool Owns(MusicVipBannerData value) =>
        surfaceActive && !stopping && !disposed && ReferenceEquals(banner, value);

    internal Task PrepareShutdownAsync()
    {
        if (preparation is not null) return preparation;
        stopping = true;
        surfaceActive = false;
        return preparation = Retire();
    }


    private Task QueueRead()
    {
        if (!surfaceActive || stopping || disposed) return transition;
        var previous = transition;
        var cancellation = new CancellationTokenSource();
        var ticket = ++epoch;
        request = cancellation;
        Notify();
        return transition = ReadCoreAsync(previous, cancellation, ticket);
    }

    private async Task ReadCoreAsync(Task previous, CancellationTokenSource cancellation, long ticket)
    {
        try
        {
            await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            var snapshot = await source.ReadVipPageAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current(cancellation, ticket)) return;
            generation = snapshot.Generation;
            account = snapshot.AccountError.Length == 0 ? snapshot.Account : null;
            sign = snapshot.SignError.Length == 0 ? snapshot.Sign : null;
            signedToday = snapshot.SignError.Length == 0 && snapshot.SignedToday;
            signUnconfirmed = !signedToday && snapshot.SignUnconfirmed;
            banner = snapshot.BannerError.Length == 0 ? snapshot.Banner : null;
            Notify();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            if (Current(cancellation, ticket)) Clear();
        }
        finally { Finish(cancellation); }
    }

    private async Task SignCoreAsync(Task previous, CancellationTokenSource cancellation,
        long ticket, long expectedGeneration)
    {
        try
        {
            await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            var receipt = await source.SignVipPageAsync(expectedGeneration, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Current(cancellation, ticket)) return;
            if (receipt.Generation != expectedGeneration)
                throw new MusicOperationException("STALE_SESSION");
            signedToday = true;
            signUnconfirmed = false;
            if (receipt.RefreshedSign is not null) sign = receipt.RefreshedSign;
            Notify();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (MusicOperationException error) when (error.Code is
            "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "STALE_SESSION" or "CLOSED")
        {
            if (Current(cancellation, ticket)) Clear();
        }
        catch (MusicOperationException error) when (error.Code == "COMMIT_UNKNOWN")
        {
            if (Current(cancellation, ticket))
            {
                signUnconfirmed = true;
                Notify();
            }
        }
        catch when (!Current(cancellation, ticket)) { }
        finally { Finish(cancellation); }
    }

    private static async Task ObserveSignAsync(Task work)
    {
        // The foreground operation receives the error. The serial lifecycle tail only
        // observes physical completion, so later refresh/sign and close can still drain.
        try { await work; }
        catch { }
    }

    private bool Current(CancellationTokenSource cancellation, long ticket) =>
        surfaceActive && !stopping && !disposed && ticket == epoch &&
        ReferenceEquals(request, cancellation) && !cancellation.IsCancellationRequested;

    private void Finish(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(request, cancellation)) request = null;
        cancellation.Dispose();
        Notify();
    }

    private Task Retire()
    {
        epoch++;
        var cancellation = request;
        if (cancellation is not null)
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        Clear();
        return transition;
    }

    private void Clear()
    {
        generation = 0;
        account = null;
        sign = null;
        signedToday = false;
        signUnconfirmed = false;
        banner = null;
        Notify();
    }

    private void Invalidated()
    {
        if (dispatcher.IsClosing) return;
        _ = dispatcher.EnqueueAsync(() =>
        {
            if (disposed || stopping) return;
            var reload = surfaceActive && viewLoaded && vipRoute;
            Retire();
            if (reload) _ = QueueRead();
        });
    }

    private void Notify()
    {
        foreach (var name in new[]
        {
            nameof(Account), nameof(Sign), nameof(Banner), nameof(ShowAccount), nameof(ShowSign),
            nameof(ShowBanner), nameof(HasCards), nameof(IsBusy), nameof(SignedToday),
            nameof(SignButtonText), nameof(ShowSignButton), nameof(CanSign), nameof(CanOpenBanner)
        })
            PropertyChanged?.Invoke(this, new(name));
    }

    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        disposed = true;
        stopping = true;
        surfaceActive = false;
        source.VipPageInvalidated -= Invalidated;
        return new(disposal = DisposeCoreAsync(Retire()));
    }

    private static async Task DisposeCoreAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch { }
    }
}

public sealed partial class MusicCapability
{
    private readonly MusicVipPage vipPage;
    public MusicVipPage VipPage => vipPage;

    internal Task UpdateVipPageAsync(bool viewVisible) =>
        vipPage.SetSurfaceAsync(viewVisible, IsVipChannel);

    public Task SignVipPageAsync() => RunAsync(async () =>
    {
        try { await vipPage.SignAsync(); }
        catch (Exception error) { await Notify(OperationErrorText(error)); }
    });

    public Task OpenVipBannerAsync()
    {
        var current = vipPage.Banner;
        if (!CanCatalogBrowse || current is null || !vipPage.Owns(current))
            return Task.CompletedTask;
        if (current.ResourceType == "SONG")
            return PlayCatalogSongAsync("wy", current.ResourceId, () => vipPage.Owns(current));
        if (current.ResourceType != "PLAYLIST") return Task.CompletedTask;
        RememberCatalogLocation();
        return RequestNavigationAsync("catalog-playlist/wy/" +
            Uri.EscapeDataString(current.ResourceId));
    }
}
