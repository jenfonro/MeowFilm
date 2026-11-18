using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // A retained content snapshot owns optional metadata, including empty/error results.
    // Visibility admits work; it does not own an already admitted read.
    private sealed class CatalogOptionalReads
    {
        public string Platform = "", Account = "";
        public long Observation;
        public bool Retired;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Dictionary<string, CatalogOptionalRead> Previews = [], Covers = [];
    }
    private sealed class CatalogOptionalRead
    {
        public Task Work = Task.CompletedTask;
        public string Cover = "", Error = "", CollectionId = "";
        public IReadOnlyList<MusicRankPreview> Songs = [];
        public bool Admitted;
    }
    private readonly SemaphoreSlim catalogPreviewSlots = new(4);
    private readonly Dictionary<string, CatalogOptionalReads> catalogOptionalSnapshots = [];
    private readonly HashSet<MusicCatalogCard> visibleCatalogCards = new(ReferenceEqualityComparer.Instance);
    private CatalogOptionalReads? activeCatalogReads;
    internal Task CatalogEntryInitialization { get; private set; } = Task.CompletedTask;

    private long AdvanceLoadGeneration()
    {
        RetireCatalogPreviewSurface();
        if (stopping) RetireCatalogOptionalReads();
        CancelCatalogLoad(homeContinuationCancellation);
        homeContinuationCancellation = null;
        return ++loadGeneration;
    }
    private void RetireCatalogPreviewSurface()
    {
        visibleCatalogCards.Clear();
        activeCatalogReads = null;
        CatalogEntryInitialization = Task.CompletedTask;
    }
    private void RetireCatalogOptionalReads(string? route = null)
    {
        foreach (var pair in catalogOptionalSnapshots.Where(pair => route is null || pair.Key == route).ToArray())
        {
            var scope = pair.Value;
            scope.Retired = true;
            CancelCatalogLoad(scope.Cancellation);
            if (ReferenceEquals(activeCatalogReads, scope)) RetireCatalogPreviewSurface();
            catalogOptionalSnapshots.Remove(pair.Key);
            _ = DisposeWhenSettled();
            async Task DisposeWhenSettled()
            {
                await Task.WhenAll(scope.Previews.Values.Concat(scope.Covers.Values).Select(read => read.Work));
                scope.Cancellation.Dispose();
            }
        }
    }
    private void ActivateCatalogOptionalReads(string route, string platform, CatalogPageState page, bool enabled)
    {
        var scope = page.OptionalReads;
        if (catalogOptionalSnapshots.TryGetValue(route, out var previous) && !ReferenceEquals(previous, scope))
            RetireCatalogOptionalReads(route);
        if (scope.Account.Length == 0 && enabled)
        {
            scope.Platform = platform;
            scope.Account = CatalogAccountKey(platform);
            scope.Observation = CatalogAccountObservation(platform);
        }
        catalogOptionalSnapshots[route] = scope;
        // Use the same bounded retention as the catalog, not a second collection cache.
        foreach (var old in catalogOptionalSnapshots.Keys.Where(key => key != route && key != "catalog-home" &&
            key != lastChannelRoute && !catalogLocations.Values.Any(saved => saved.Route == key)).ToArray())
            RetireCatalogOptionalReads(old);
        activeCatalogReads = enabled && OptionalReadsCurrent(scope) ? scope : null;
        visibleCatalogCards.RemoveWhere(card => !ContainsCatalogCard(card));
        UpdateCatalogVisibleCards(visibleCatalogCards.ToArray());
    }
    private bool OptionalReadsCurrent(CatalogOptionalReads scope) => !stopping && !scope.Retired &&
        scope.Platform.Length > 0 && scope.Account == CatalogAccountKey(scope.Platform) &&
        scope.Observation == CatalogAccountObservation(scope.Platform);
    private bool HasOptionalDemand(CatalogOptionalReads scope, Func<MusicCatalogCard, bool> predicate) =>
        ReferenceEquals(activeCatalogReads, scope) && IsCatalogPage && visibleCatalogCards.Any(predicate);
    private string PreviewReadKey(MusicCatalogCard card) => card.Kind + ":" + JsonSerializer.Serialize(CardReadParameters(card));
    private static string CoverReadKey(MusicCatalogCard card) => card.CoverRef is { Kind: "playlist" } reference
        ? reference.Id : "";

    // Called with clipped, visible slots by MusicCatalogView. No Loaded/hover-driven API reads.
    internal void UpdateCatalogVisibleCards(IEnumerable<MusicCatalogCard> cards)
    {
        var current = cards.Where(ContainsCatalogCard).ToArray();
        visibleCatalogCards.Clear();
        foreach (var card in current) visibleCatalogCards.Add(card);
        if (activeCatalogReads is not { } scope || !OptionalReadsCurrent(scope)) return;
        PublishOptionalResults(scope);
        foreach (var card in current.Where(card => card.CanRequestPreview && !card.HasPreview || card.CanRequestFMArtwork))
        {
            string key = PreviewReadKey(card);
            if (scope.Previews.ContainsKey(key)) continue;
            var read = new CatalogOptionalRead { CollectionId = card.Kind == "playlist" ? card.ActionId : "" };
            scope.Previews.Add(key, read);
            read.Work = ReadCatalogPreview(scope, key, card, read);
            TrackCatalogOptionalRead(read.Work);
        }
        var missing = current.Where(card => card.Cover.Length == 0 && CoverReadKey(card).Length > 0)
            .Select(CoverReadKey).Distinct(StringComparer.Ordinal).Where(id => !scope.Covers.ContainsKey(id)).ToArray();
        if (missing.Length == 0) return;
        var reads = missing.ToDictionary(id => id, _ => new CatalogOptionalRead(), StringComparer.Ordinal);
        foreach (var pair in reads) scope.Covers.Add(pair.Key, pair.Value);
        var work = ReadCatalogCovers(scope, reads);
        foreach (var read in reads.Values) read.Work = work;
        TrackCatalogOptionalRead(work);
    }
    private void TrackCatalogOptionalRead(Task work)
    {
        CatalogEntryInitialization = Task.WhenAll(CatalogEntryInitialization, work);
        loadDrains.Add(work);
        _ = Drain();
        async Task Drain() { try { await work; } finally { loadDrains.Remove(work); } }
    }
    private async Task ReadCatalogPreview(CatalogOptionalReads scope, string key, MusicCatalogCard card, CatalogOptionalRead read)
    {
        await Task.Yield();
        bool entered = false;
        try
        {
            await catalogPreviewSlots.WaitAsync(scope.Cancellation.Token); entered = true;
            if (!OptionalReadsCurrent(scope) || !HasOptionalDemand(scope, item => PreviewReadKey(item) == key))
            { scope.Previews.Remove(key); return; }
            read.Admitted = true;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, scope.Cancellation.Token);
            request.CancelAfter(TimeSpan.FromSeconds(8));
            RequireCatalogAccount(card);
            string method = "music.catalog." + card.Kind;
            var reply = ValidateReply(method, await application.ExecuteAsync(method, CardReadParameters(card), request.Token));
            request.Token.ThrowIfCancellationRequested();
            if (!OptionalReadsCurrent(scope)) return;
            read.Cover = MusicCatalogDto.Text(reply, "cover");
            if (card.Kind != "fm")
                read.Songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(section => section.Items)
                    .Take(3).Select((item, index) => new MusicRankPreview(item.Title, item.Subtitle, item.Cover, index + 1, null, item.Song)).ToArray();
            if (card.Kind == "playlist") RememberCatalogCollectionCover(scope.Platform, card.ActionId, read.Cover);
        }
        catch (Exception error) { read.Error = OptionalReadError(error); }
        finally
        {
            if (entered) catalogPreviewSlots.Release();
            PublishOptionalResults(scope, completing: read);
        }
    }
    private async Task ReadCatalogCovers(CatalogOptionalReads scope, Dictionary<string, CatalogOptionalRead> reads)
    {
        await Task.Yield();
        bool entered = false;
        var admitted = new Dictionary<string, CatalogOptionalRead>(StringComparer.Ordinal);
        try
        {
            // An already requested preview owns its collection root cover.
            foreach (var pair in reads)
            {
                var preview = scope.Previews.Values.FirstOrDefault(read => read.CollectionId == pair.Key);
                if (preview is null) continue;
                await preview.Work;
                if (preview.Admitted && pair.Value.Cover.Length == 0)
                { pair.Value.Cover = preview.Cover; pair.Value.Error = preview.Error; }
            }
            await catalogPreviewSlots.WaitAsync(scope.Cancellation.Token); entered = true;
            foreach (var pair in reads)
            {
                if (pair.Value.Cover.Length > 0 || pair.Value.Error.Length > 0) continue;
                if (!OptionalReadsCurrent(scope) ||
                    !HasOptionalDemand(scope, card => card.Cover.Length == 0 && CoverReadKey(card) == pair.Key))
                { scope.Covers.Remove(pair.Key); continue; }
                admitted.Add(pair.Key, pair.Value);
                pair.Value.Admitted = true;
            }
            if (admitted.Count == 0) return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, scope.Cancellation.Token);
            request.CancelAfter(TimeSpan.FromSeconds(8));
            const string method = "music.catalog.playlists";
            var reply = ValidateReply(method, await application.ExecuteAsync(method,
                new { platform = scope.Platform, ids = admitted.Keys.ToArray() }, request.Token));
            request.Token.ThrowIfCancellationRequested();
            if (!OptionalReadsCurrent(scope)) return;
            foreach (var item in MusicCatalogDto.Rows(MusicCatalogDto.Field(reply, "items")))
                if (admitted.TryGetValue(MusicCatalogDto.Text(item, "id"), out var read) && read.Cover.Length == 0)
                    read.Cover = MusicCatalogDto.Text(item, "cover");
        }
        catch (Exception error)
        {
            foreach (var read in reads.Values.Where(read => read.Cover.Length == 0)) read.Error = OptionalReadError(error);
        }
        finally
        {
            if (entered) catalogPreviewSlots.Release();
            foreach (var read in reads.Values) PublishOptionalResults(scope, completing: read);
        }
    }
    private static string OptionalReadError(Exception error) => error switch
    {
        MusicOperationException failure => failure.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };
    private void PublishOptionalResults(CatalogOptionalReads scope, CatalogOptionalRead? completing = null)
    {
        if (!OptionalReadsCurrent(scope) || !ReferenceEquals(activeCatalogReads, scope)) return;
        foreach (var card in visibleCatalogCards.ToArray())
        {
            if (scope.Previews.TryGetValue(PreviewReadKey(card), out var preview) &&
                (preview.Work.IsCompleted || ReferenceEquals(preview, completing)) && preview.Error.Length == 0)
            {
                if (card.Kind == "fm") card.FillCover(preview.Cover);
                else card.InitializePreview(preview.Songs);
            }
            if (scope.Covers.TryGetValue(CoverReadKey(card), out var cover) &&
                (cover.Work.IsCompleted || ReferenceEquals(cover, completing)) && cover.Error.Length == 0)
                card.FillCover(cover.Cover);
        }
    }
    private void RememberCatalogCollectionCover(string platform, string id, string cover)
    {
        if (id.Length == 0 || cover.Length == 0) return;
        foreach (var scope in catalogOptionalSnapshots.Values.Where(scope =>
            scope.Platform == platform && OptionalReadsCurrent(scope)).ToArray())
        {
            if (!scope.Covers.TryGetValue(id, out var read))
                scope.Covers.Add(id, read = new());
            if (read.Cover.Length == 0) { read.Cover = cover; read.Error = ""; }
            PublishOptionalResults(scope);
        }
    }
    internal string CatalogCoverReadOutcome(string id) => activeCatalogReads is { } scope &&
        scope.Covers.TryGetValue(id, out var read) ? read.Error.Length > 0 ? read.Error :
            !read.Work.IsCompleted ? "pending" : read.Cover.Length > 0 ? "cover" : "empty" : "unrequested";
    internal string CatalogFMArtworkOutcome(MusicCatalogCard card) => activeCatalogReads is { } scope &&
        scope.Previews.TryGetValue(PreviewReadKey(card), out var read) ? read.Error.Length > 0 ? read.Error :
            !read.Work.IsCompleted ? "pending" : read.Cover.Length > 0 ? "cover" : "empty" : "unrequested";
}
