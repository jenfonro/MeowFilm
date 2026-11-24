namespace MeowFilm.Capabilities;

internal sealed class CatalogOptionalReads
{
    public string Platform = "", Account = "";
    public long Observation;
    public bool Retired;
    public readonly CancellationTokenSource Cancellation = new();
    public readonly Dictionary<string, CatalogOptionalRead> Previews = [], Collections = [];
}

internal sealed class CatalogOptionalRead
{
    public Task Work = Task.CompletedTask;
    public string Name = "", Cover = "", Error = "", CollectionId = "";
    public long? PlayCount;
    public IReadOnlyList<MusicRankPreview> Songs = [];
    public bool Admitted;
}

/// <summary>
/// Standalone non-UI scheduler for bounded viewport optional metadata reads
/// (rank/playlist song previews, FM artwork, and batch playlist metadata).
/// </summary>
internal sealed class MusicCatalogOptionalReadService : IAsyncDisposable
{
    private readonly MusicRuntimeContext ctx;
    private readonly Func<string, string> getAccountKey;
    private readonly Func<string, long> getAccountObservation;
    private readonly Func<bool> isCatalogPage;
    private readonly Func<IEnumerable<MusicCatalogCard>> getDisplayCards;
    private readonly Func<string, bool> isRouteRetained;
    private readonly Action<MusicCatalogCard> requireCatalogAccount;

    private readonly SemaphoreSlim previewSlots = new(4);
    private readonly Dictionary<string, CatalogOptionalReads> snapshots = [];
    private readonly HashSet<MusicCatalogCard> visibleCards = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MusicCatalogCard> displayCards = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Task> retiring = [];
    private CatalogOptionalReads? activeScope;
    private bool disposed;
    private Task? disposal;

    internal MusicCatalogOptionalReadService(
        MusicRuntimeContext ctx,
        Func<string, string> getAccountKey,
        Func<string, long> getAccountObservation,
        Func<bool> isCatalogPage,
        Func<IEnumerable<MusicCatalogCard>> getDisplayCards,
        Func<string, bool> isRouteRetained,
        Action<MusicCatalogCard> requireCatalogAccount)
    {
        this.ctx = ctx;
        this.getAccountKey = getAccountKey;
        this.getAccountObservation = getAccountObservation;
        this.isCatalogPage = isCatalogPage;
        this.getDisplayCards = getDisplayCards;
        this.isRouteRetained = isRouteRetained;
        this.requireCatalogAccount = requireCatalogAccount;
    }

    internal void RetireSurface()
    {
        visibleCards.Clear();
        activeScope = null;
    }

    internal void Retire(string? route = null)
    {
        foreach (var pair in snapshots.Where(pair => route is null || pair.Key == route).ToArray())
            RetireEntry(pair.Key, pair.Value);
    }

    internal void RetireForAccount(string platform, long observation)
    {
        foreach (var pair in snapshots.Where(pair => pair.Value.Platform == platform && pair.Value.Observation < observation).ToArray())
            RetireEntry(pair.Key, pair.Value);
    }

    private void RetireEntry(string key, CatalogOptionalReads scope)
    {
        scope.Retired = true;
        MusicCatalogBrowseService.CancelCatalogLoad(scope.Cancellation);
        if (ReferenceEquals(activeScope, scope)) RetireSurface();
        snapshots.Remove(key);
        var works = scope.Previews.Values.Concat(scope.Collections.Values).Select(read => read.Work).ToArray();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (retiring) retiring.Add(settled.Task);
        _ = DisposeWhenSettled();

        async Task DisposeWhenSettled()
        {
            try
            {
                if (works.Length > 0) await Task.WhenAll(works).ConfigureAwait(false);
            }
            catch
            {
                // Individual optional-read errors are recorded on each CatalogOptionalRead;
                // settlement still disposes the retired CancellationTokenSource without unobserved faults.
            }
            finally
            {
                scope.Cancellation.Dispose();
                lock (retiring) retiring.Remove(settled.Task);
                settled.TrySetResult();
            }
        }
    }

    internal CatalogOptionalReads? Activate(string route, string platform, CatalogOptionalReads? existing, bool enabled)
    {
        var scope = existing;
        if (scope is null && enabled) scope = new();
        if (snapshots.TryGetValue(route, out var previous) && !ReferenceEquals(previous, scope))
            Retire(route);
        if (scope is not null)
        {
            if (scope.Account.Length == 0 && enabled)
            {
                scope.Platform = platform;
                scope.Account = getAccountKey(platform);
                scope.Observation = getAccountObservation(platform);
            }
            snapshots[route] = scope;
        }
        foreach (var old in snapshots.Keys.Where(key => key != route && !isRouteRetained(key)).ToArray())
            Retire(old);
        activeScope = enabled && scope is not null && ScopeCurrent(scope) ? scope : null;
        RefreshDemandMembers();
        ProcessVisibleCards(visibleCards);
        return scope;
    }

    internal void RefreshDemandMembers()
    {
        displayCards.Clear();
        displayCards.UnionWith(getDisplayCards());
        visibleCards.IntersectWith(displayCards);
    }

    internal void UpdateVisibleCards(IEnumerable<MusicCatalogCard> cards)
    {
        var current = new HashSet<MusicCatalogCard>(cards, ReferenceEqualityComparer.Instance);
        current.IntersectWith(displayCards);
        if (visibleCards.SetEquals(current)) return;
        var added = current.Where(card => !visibleCards.Contains(card)).ToArray();
        visibleCards.Clear();
        visibleCards.UnionWith(current);
        if (added.Length > 0) ProcessVisibleCards(added);
    }

    internal void RememberCollectionMetadata(string platform, string id, string name, string cover, long? playCount)
    {
        if (id.Length == 0 || name.Length == 0 && cover.Length == 0 && playCount is null) return;
        foreach (var scope in snapshots.Values.Where(scope => scope.Platform == platform && ScopeCurrent(scope)).ToArray())
        {
            if (!scope.Collections.TryGetValue(id, out var read))
                scope.Collections.Add(id, read = new());
            if (name.Length > 0) read.Name = name;
            if (read.Cover.Length == 0) read.Cover = cover;
            read.PlayCount ??= playCount;
            PublishResults(scope);
        }
    }

    private bool ScopeCurrent(CatalogOptionalReads scope) => !ctx.Stopping && !scope.Retired &&
        scope.Platform.Length > 0 && scope.Account == getAccountKey(scope.Platform) &&
        scope.Observation == getAccountObservation(scope.Platform);

    private bool HasDemand(CatalogOptionalReads scope, Func<MusicCatalogCard, bool> predicate) =>
        ReferenceEquals(activeScope, scope) && isCatalogPage() && visibleCards.Any(predicate);

    private static bool NeedsCollectionMetadata(MusicCatalogCard card, CatalogOptionalRead? root)
    {
        bool needsName = card.Title.Length == 0 || card.IsPreviewEntry && (!card.HasLabel || card.ContextText.Length == 0);
        return card.Cover.Length == 0 && string.IsNullOrEmpty(root?.Cover) ||
            needsName && string.IsNullOrEmpty(root?.Name) ||
            card.IsPreviewEntry && !card.HasLabel && card.PlayCount is null && root?.PlayCount is null;
    }

    private void ProcessVisibleCards(IEnumerable<MusicCatalogCard> cards)
    {
        if (disposed || activeScope is not { } scope || !ScopeCurrent(scope)) return;
        var current = cards.ToArray();
        PublishResults(scope, cards: current);
        foreach (var card in current.Where(card => card.CanRequestPreview && !card.HasPreview || card.CanRequestFMArtwork))
        {
            string key = MusicCatalogDispatcher.PreviewReadKey(card);
            if (scope.Previews.ContainsKey(key)) continue;
            var read = new CatalogOptionalRead { CollectionId = card.Kind == "playlist" ? card.ActionId : "" };
            scope.Previews.Add(key, read);
            read.Work = ReadPreviewAsync(scope, key, card, read);
            TrackWork(read.Work);
        }
        var reads = new Dictionary<string, CatalogOptionalRead>(StringComparer.Ordinal);
        foreach (var card in current)
        {
            string id = MusicCatalogDispatcher.CollectionReadKey(card);
            if (id.Length == 0) continue;
            scope.Collections.TryGetValue(id, out var root);
            if (root is { Admitted: true } or { Work.IsCompleted: false } || !NeedsCollectionMetadata(card, root)) continue;
            reads[id] = scope.Collections[id] = root ?? new();
        }
        if (reads.Count == 0) return;
        var work = ReadCollectionsAsync(scope, reads);
        foreach (var read in reads.Values) read.Work = work;
        TrackWork(work);
    }

    private void TrackWork(Task work)
    {
        ctx.LoadDrains.Add(work);
        _ = Drain();
        async Task Drain() { try { await work; } finally { ctx.LoadDrains.Remove(work); } }
    }

    private async Task ReadPreviewAsync(CatalogOptionalReads scope, string key, MusicCatalogCard card, CatalogOptionalRead read)
    {
        await Task.Yield();
        bool entered = false;
        try
        {
            await previewSlots.WaitAsync(scope.Cancellation.Token); entered = true;
            if (!ScopeCurrent(scope) || !HasDemand(scope, item => MusicCatalogDispatcher.PreviewReadKey(item) == key))
            { scope.Previews.Remove(key); return; }
            read.Admitted = true;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token, scope.Cancellation.Token);
            request.CancelAfter(TimeSpan.FromSeconds(8));
            requireCatalogAccount(card);
            var reply = await MusicCatalogDispatcher.InvokeCardReadAsync(ctx.Application, card, previewLimit: true, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!ScopeCurrent(scope)) return;
            read.Cover = MusicCatalogDto.Text(reply, "cover");
            if (card.Kind != "fm")
                read.Songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(section => section.Items)
                    .Take(3).Select((item, index) => new MusicRankPreview(item.Title, item.Subtitle, item.Cover, index + 1, null, item.Song)).ToArray();
            if (card.Kind == "playlist") RememberCollectionMetadata(scope.Platform, card.ActionId,
                MusicCatalogDto.Text(reply, "name"), read.Cover, MusicCatalogDto.LongNumber(reply, "playCount"));
        }
        catch (Exception error) { read.Error = OptionalReadError(error); }
        finally
        {
            if (entered) previewSlots.Release();
            PublishResults(scope, completing: read);
        }
    }

    private async Task ReadCollectionsAsync(CatalogOptionalReads scope, Dictionary<string, CatalogOptionalRead> reads)
    {
        await Task.Yield();
        bool entered = false;
        var admitted = new Dictionary<string, CatalogOptionalRead>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in reads)
            {
                var preview = scope.Previews.Values.FirstOrDefault(read => read.CollectionId == pair.Key);
                if (preview is not null) await preview.Work;
            }
            await previewSlots.WaitAsync(scope.Cancellation.Token); entered = true;
            foreach (var pair in reads)
            {
                if (!ScopeCurrent(scope) || !HasDemand(scope,
                    card => MusicCatalogDispatcher.CollectionReadKey(card) == pair.Key && NeedsCollectionMetadata(card, pair.Value)))
                {
                    pair.Value.Work = Task.CompletedTask;
                    continue;
                }
                admitted.Add(pair.Key, pair.Value);
                pair.Value.Admitted = true;
            }
            if (admitted.Count == 0) return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token, scope.Cancellation.Token);
            request.CancelAfter(TimeSpan.FromSeconds(8));
            var reply = await ctx.Application.GetCatalogPlaylistsBatchAsync(scope.Platform, admitted.Keys.ToArray(), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!ScopeCurrent(scope)) return;
            foreach (var item in MusicCatalogDto.Rows(MusicCatalogDto.Field(reply, "items")))
                if (admitted.TryGetValue(MusicCatalogDto.Text(item, "id"), out var read))
                {
                    if (read.Name.Length == 0) read.Name = MusicCatalogDto.Text(item, "name");
                    if (read.Cover.Length == 0) read.Cover = MusicCatalogDto.Text(item, "cover");
                    read.PlayCount ??= MusicCatalogDto.LongNumber(item, "playCount");
                }
        }
        catch (Exception error)
        {
            foreach (var read in admitted.Values) read.Error = OptionalReadError(error);
        }
        finally
        {
            if (entered) previewSlots.Release();
            PublishResults(scope);
        }
    }

    private static string OptionalReadError(Exception error) => error switch
    {
        MusicOperationException failure => failure.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };

    private void PublishResults(CatalogOptionalReads scope, CatalogOptionalRead? completing = null,
        IEnumerable<MusicCatalogCard>? cards = null)
    {
        if (!ScopeCurrent(scope) || !ReferenceEquals(activeScope, scope)) return;
        foreach (var card in (cards ?? visibleCards).ToArray())
        {
            if ((card.IsPreviewEntry || card.CanRequestFMArtwork) &&
                scope.Previews.TryGetValue(MusicCatalogDispatcher.PreviewReadKey(card), out var preview) &&
                (preview.Work.IsCompleted || ReferenceEquals(preview, completing)) && preview.Error.Length == 0)
            {
                if (card.Kind == "fm") card.FillCover(preview.Cover);
                else card.InitializePreview(preview.Songs);
            }
            if (scope.Collections.TryGetValue(MusicCatalogDispatcher.CollectionReadKey(card), out var root))
                card.FillCollectionMetadata(root.Name, root.Cover, root.PlayCount);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        disposed = true;
        Retire();
        return new(disposal = DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (retiring) pending = retiring.ToArray();
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Settlement waits for all retired previewSlots users to finish before disposing. */ }
        }
        previewSlots.Dispose();
    }
}
