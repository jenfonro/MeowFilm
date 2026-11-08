using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // Page-owned optional reads. Pointer/focus events have no data lifecycle.
    private readonly SemaphoreSlim catalogPreviewSlots = new(4);
    private CancellationTokenSource? catalogPreviewCancellation;
    private readonly Dictionary<string, Task<IReadOnlyList<MusicRankPreview>>> catalogPreviewReads = [];
    private readonly HashSet<MusicCatalogCard> initializedEntries = new(ReferenceEqualityComparer.Instance);
    internal Task CatalogEntryInitialization { get; private set; } = Task.CompletedTask;

    private long AdvanceLoadGeneration()
    {
        RetireCatalogPreviewSurface();
        CancelCatalogLoad(homeContinuationCancellation);
        homeContinuationCancellation = null;
        return ++loadGeneration;
    }
    private void RetireCatalogPreviewSurface()
    {
        var prior = catalogPreviewCancellation; catalogPreviewCancellation = null;
        catalogPreviewReads.Clear(); initializedEntries.Clear(); CatalogEntryInitialization = Task.CompletedTask;
        CancelCatalogLoad(prior); prior?.Dispose();
    }
    private Dictionary<string, object?> CardReadParameters(MusicCatalogCard card)
    {
        var parameters = new Dictionary<string, object?> { ["platform"] = card.Platform };
        if (card.Kind == "daily") parameters["branch"] = CardDailyBranch(card);
        else
        {
            parameters["id"] = card.ActionId;
            if (card.Kind == "playlist") { parameters["page"] = 1; parameters["limit"] = 3; }
            if (card.Locator is { } locator) parameters["personal"] = locator;
        }
        return parameters;
    }
    private static string CardDailyBranch(MusicCatalogCard card) =>
        card.Action is { } action && MusicCatalogDto.Text(action, "branch") == "style" ? "style" : "default";

    private void InitializeCatalogEntries()
    {
        if (stopping || !IsCatalogPage) return;
        var cards = CatalogSections.SelectMany(section => section.Items)
            .Where(card => card.CanRequestPreview && !card.HasPreview && initializedEntries.Add(card)).ToArray();
        if (cards.Length == 0) return;
        catalogPreviewCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = catalogPreviewCancellation.Token;
        var generation = loadGeneration;
        var work = Task.WhenAll(cards.Select(Initialize));
        CatalogEntryInitialization = Task.WhenAll(CatalogEntryInitialization, work);
        loadDrains.Add(work);
        _ = Drain();
        async Task Drain() { try { await work; } finally { loadDrains.Remove(work); } }
        async Task Initialize(MusicCatalogCard card)
        {
            // The identity includes the actual branch/locator, never the caption or hover key.
            var parameters = CardReadParameters(card);
            var key = card.Kind + ":" + JsonSerializer.Serialize(parameters);
            if (!catalogPreviewReads.TryGetValue(key, out var read))
                catalogPreviewReads[key] = read = ReadPreview(card, parameters);
            var songs = await read;
            if (!token.IsCancellationRequested && !stopping && generation == loadGeneration && ContainsCatalogCard(card))
                card.InitializePreview(songs);
        }
        async Task<IReadOnlyList<MusicRankPreview>> ReadPreview(MusicCatalogCard card, Dictionary<string, object?> parameters)
        {
            bool entered = false;
            try
            {
                await catalogPreviewSlots.WaitAsync(token); entered = true;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(TimeSpan.FromSeconds(8));
                if (IsPrivateCatalogCard(card))
                {
                    await EnsureAccountsForRequestAsync(request.Token);
                    RequireCatalogAccount(card);
                }
                RequireCatalogContent();
                string method = "music.catalog." + card.Kind;
                var reply = ValidateReply(method, await application.ExecuteAsync(method, parameters, request.Token));
                request.Token.ThrowIfCancellationRequested();
                if (generation != loadGeneration) return [];
                RequireCatalogContent();
                return MusicCatalogDto.Read(reply, card.Platform, card.Kind, "")
                    .SelectMany(section => section.Items).Where(item => item.Song is not null).Take(3)
                    .Select((item, index) => new MusicRankPreview(item.Title, item.Subtitle, item.Cover, index + 1, null)).ToArray();
            }
            catch { return []; } // Optional failure is retained for this page, not retried by hovering.
            finally { if (entered) catalogPreviewSlots.Release(); }
        }
    }
    public Task PlayCatalogCardAsync(MusicCatalogCard card)
    {
        if (!IsCatalogPage || !IsCatalogHome && loadCancellation is not null ||
            !CanCatalogBrowse || !card.CanPlay || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("play", 0, entry: card);
        if (card.Kind is not ("daily" or "album")) return OpenCatalogCardAsync(card);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var parameters = CardReadParameters(card);
            parameters.Remove("page"); parameters.Remove("limit");
            var reply = await Call("music.catalog." + card.Kind, parameters, signal);
            if (!currentAction()) return;
            var songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(s => s.Items)
                .Where(c => c.Song is not null).Select(c => c.Song!.Raw).ToArray();
            MusicDto.Require(songs.Length is > 0 and <= 1000 &&
                (!reply.TryGetProperty("more", out var more) || more.ValueKind == JsonValueKind.False));
            ValidatePersonalCatalog(reply, Target(card.Kind, card.Platform, card.Title, parameters), songs.Length);
            await RunAsync(async () =>
            {
                if (!currentAction()) return;
                RequireCatalogContent();
                var queue = await Call("music.queue.get", new { offset = 0, limit = 1 }, signal);
                var version = MusicDto.Integer(queue, "version");
                if (!currentAction()) return;
                try
                {
                    queueEpoch++;
                    var result = await CallSettledReceipt("music.queue.replace", new { songs, index = 0, context = new
                    { queueSource = new { id = card.ContentKey, title = card.Title, kind = card.Kind, platform = card.Platform } } }, signal);
                    MusicDto.Require(MusicDto.Integer(result, "version") > version);
                    await ReadQueue(lifetime.Token);
                    if (currentAction()) await StartCurrent(signal);
                }
                catch (OperationCanceledException) { }
                catch { queueSaved = false; throw; }
            });
        }, card: card);
    }
}
