using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    // A retained content snapshot owns optional metadata, including empty/error results.
    // Visibility admits work; it does not own an already admitted read.
    private sealed class CatalogOptionalReads
    {
        public string Platform = "", Account = "";
        public long Observation;
        public bool Retired;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Dictionary<string, CatalogOptionalRead> Previews = [], Collections = [];
    }
    private sealed class CatalogOptionalRead
    {
        public Task Work = Task.CompletedTask;
        public string Name = "", Cover = "", Error = "", CollectionId = "";
        public long? PlayCount;
        public IReadOnlyList<MusicRankPreview> Songs = [];
        public bool Admitted;
    }
    private readonly SemaphoreSlim catalogPreviewSlots = new(4);
    private readonly Dictionary<string, CatalogOptionalReads> catalogOptionalSnapshots = [];
    private readonly HashSet<MusicCatalogCard> visibleCatalogCards = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MusicCatalogCard> catalogDisplayCards = new(ReferenceEqualityComparer.Instance);
    private CatalogOptionalReads? activeCatalogReads;
    internal Task CatalogEntryInitialization { get; private set; } = Task.CompletedTask;

    internal void OnLoadGenerationAdvanced()
    {
        RetireCatalogPreviewSurface();
        if (stopping) RetireCatalogOptionalReads();
        CancelCatalogLoad(homeContinuationCancellation);
        homeContinuationCancellation = null;
    }

    internal void RetireCatalogPreviewSurface()
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
                await Task.WhenAll(scope.Previews.Values.Concat(scope.Collections.Values).Select(read => read.Work));
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
        RefreshCatalogDemandMembers();
        ProcessVisibleCatalogCards(visibleCatalogCards);
    }

    private bool OptionalReadsCurrent(CatalogOptionalReads scope) => !stopping && !scope.Retired &&
        scope.Platform.Length > 0 && scope.Account == CatalogAccountKey(scope.Platform) &&
        scope.Observation == CatalogAccountObservation(scope.Platform);

    private bool HasOptionalDemand(CatalogOptionalReads scope, Func<MusicCatalogCard, bool> predicate) =>
        ReferenceEquals(activeCatalogReads, scope) && IsCatalogPage && visibleCatalogCards.Any(predicate);

    private string PreviewReadKey(MusicCatalogCard card) => card.Kind + ":" + JsonSerializer.Serialize(CardReadParameters(card));
    private static string CollectionReadKey(MusicCatalogCard card) => card.CoverRef is { Kind: "playlist" } reference
        ? reference.Id : "";

    private static bool NeedsCollectionMetadata(MusicCatalogCard card, CatalogOptionalRead? root)
    {
        bool needsName = card.Title.Length == 0 || card.IsPreviewEntry && (!card.HasLabel || card.ContextText.Length == 0);
        return card.Cover.Length == 0 && string.IsNullOrEmpty(root?.Cover) ||
            needsName && string.IsNullOrEmpty(root?.Name) ||
            card.IsPreviewEntry && !card.HasLabel && card.PlayCount is null && root?.PlayCount is null;
    }

    // Rebuild membership only when the business display projection changes.
    private void RefreshCatalogDemandMembers()
    {
        catalogDisplayCards.Clear();
        foreach (var section in DisplayCatalogSections)
        {
            catalogDisplayCards.UnionWith(section.Items);
            if (section.Action is { } action) catalogDisplayCards.Add(action);
        }
        if (HasChannelTabs) catalogDisplayCards.UnionWith(ChannelTabs);
        visibleCatalogCards.IntersectWith(catalogDisplayCards);
    }

    internal void UpdateCatalogVisibleCards(IEnumerable<MusicCatalogCard> cards)
    {
        var current = new HashSet<MusicCatalogCard>(cards, ReferenceEqualityComparer.Instance);
        current.IntersectWith(catalogDisplayCards);
        if (visibleCatalogCards.SetEquals(current)) return;
        var added = current.Where(card => !visibleCatalogCards.Contains(card)).ToArray();
        visibleCatalogCards.Clear();
        visibleCatalogCards.UnionWith(current);
        if (added.Length > 0) ProcessVisibleCatalogCards(added);
    }

    private void ProcessVisibleCatalogCards(IEnumerable<MusicCatalogCard> cards)
    {
        if (activeCatalogReads is not { } scope || !OptionalReadsCurrent(scope)) return;
        var current = cards.ToArray();
        PublishOptionalResults(scope, cards: current);
        foreach (var card in current.Where(card => card.CanRequestPreview && !card.HasPreview || card.CanRequestFMArtwork))
        {
            string key = PreviewReadKey(card);
            if (scope.Previews.ContainsKey(key)) continue;
            var read = new CatalogOptionalRead { CollectionId = card.Kind == "playlist" ? card.ActionId : "" };
            scope.Previews.Add(key, read);
            read.Work = ReadCatalogPreview(scope, key, card, read);
            TrackCatalogOptionalRead(read.Work);
        }
        var reads = new Dictionary<string, CatalogOptionalRead>(StringComparer.Ordinal);
        foreach (var card in current)
        {
            string id = CollectionReadKey(card);
            if (id.Length == 0) continue;
            scope.Collections.TryGetValue(id, out var root);
            if (root is { Admitted: true } or { Work.IsCompleted: false } || !NeedsCollectionMetadata(card, root)) continue;
            reads[id] = scope.Collections[id] = root ?? new();
        }
        if (reads.Count == 0) return;
        var work = ReadCatalogCollections(scope, reads);
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
            if (card.Kind == "playlist") RememberCatalogCollectionMetadata(scope.Platform, card.ActionId,
                MusicCatalogDto.Text(reply, "name"), read.Cover, MusicCatalogDto.LongNumber(reply, "playCount"));
        }
        catch (Exception error) { read.Error = OptionalReadError(error); }
        finally
        {
            if (entered) catalogPreviewSlots.Release();
            PublishOptionalResults(scope, completing: read);
        }
    }

    private async Task ReadCatalogCollections(CatalogOptionalReads scope, Dictionary<string, CatalogOptionalRead> reads)
    {
        await Task.Yield();
        bool entered = false;
        var admitted = new Dictionary<string, CatalogOptionalRead>(StringComparer.Ordinal);
        try
        {
            // Reuse the admitted preview root before considering a separate summary.
            foreach (var pair in reads)
            {
                var preview = scope.Previews.Values.FirstOrDefault(read => read.CollectionId == pair.Key);
                if (preview is not null) await preview.Work;
            }
            await catalogPreviewSlots.WaitAsync(scope.Cancellation.Token); entered = true;
            foreach (var pair in reads)
            {
                if (!OptionalReadsCurrent(scope) || !HasOptionalDemand(scope,
                    card => CollectionReadKey(card) == pair.Key && NeedsCollectionMetadata(card, pair.Value))) continue;
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
            if (entered) catalogPreviewSlots.Release();
            PublishOptionalResults(scope);
        }
    }

    private static string OptionalReadError(Exception error) => error switch
    {
        MusicOperationException failure => failure.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };

    private void PublishOptionalResults(CatalogOptionalReads scope, CatalogOptionalRead? completing = null,
        IEnumerable<MusicCatalogCard>? cards = null)
    {
        if (!OptionalReadsCurrent(scope) || !ReferenceEquals(activeCatalogReads, scope)) return;
        foreach (var card in (cards ?? visibleCatalogCards).ToArray())
        {
            if ((card.IsPreviewEntry || card.CanRequestFMArtwork) && scope.Previews.TryGetValue(PreviewReadKey(card), out var preview) &&
                (preview.Work.IsCompleted || ReferenceEquals(preview, completing)) && preview.Error.Length == 0)
            {
                if (card.Kind == "fm") card.FillCover(preview.Cover);
                else card.InitializePreview(preview.Songs);
            }
            if (scope.Collections.TryGetValue(CollectionReadKey(card), out var root))
                card.FillCollectionMetadata(root.Name, root.Cover, root.PlayCount);
        }
    }

    private void RememberCatalogCollectionMetadata(string platform, string id, string name, string cover, long? playCount)
    {
        if (id.Length == 0 || name.Length == 0 && cover.Length == 0 && playCount is null) return;
        foreach (var scope in catalogOptionalSnapshots.Values.Where(scope =>
            scope.Platform == platform && OptionalReadsCurrent(scope)).ToArray())
        {
            if (!scope.Collections.TryGetValue(id, out var read))
                scope.Collections.Add(id, read = new());
            if (name.Length > 0) read.Name = name;
            if (read.Cover.Length == 0) read.Cover = cover;
            read.PlayCount ??= playCount;
            PublishOptionalResults(scope);
        }
    }

    internal string CatalogCoverReadOutcome(string id) => activeCatalogReads is { } scope &&
        scope.Collections.TryGetValue(id, out var read) ? read.Error.Length > 0 ? read.Error :
            !read.Work.IsCompleted ? "pending" : read.Cover.Length > 0 ? "cover" : "empty" : "unrequested";

    internal string CatalogFMArtworkOutcome(MusicCatalogCard card) => activeCatalogReads is { } scope &&
        scope.Previews.TryGetValue(PreviewReadKey(card), out var read) ? read.Error.Length > 0 ? read.Error :
            !read.Work.IsCompleted ? "pending" : read.Cover.Length > 0 ? "cover" : "empty" : "unrequested";
}
