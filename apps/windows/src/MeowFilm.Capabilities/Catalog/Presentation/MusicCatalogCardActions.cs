using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private readonly Dictionary<string, (long Generation, IReadOnlyList<string> Titles)> entryPreviews = [];
    private readonly Dictionary<string, CatalogPreviewFlight> entryPreviewFlights = [];
    private readonly object entryPreviewGate = new();
    private sealed class CatalogPreviewFlight(
        string key,
        long generation,
        MusicCatalogCard card,
        CancellationTokenSource cancellation) : IDisposable
    {
        internal string Key { get; } = key;
        internal long Generation { get; } = generation;
        internal MusicCatalogCard Card { get; } = card;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task<IReadOnlyList<string>> Work { get; set; } =
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        internal int Subscribers;
        internal bool Retired;
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
        public void Dispose() => Cancellation.Dispose();
    }

    private long AdvanceLoadGeneration()
    {
        CatalogPreviewFlight[] retired;
        long generation;
        lock (entryPreviewGate)
        {
            generation = ++loadGeneration;
            retired = RetireCatalogPreviewSurfaceLocked();
        }
        foreach (var flight in retired) flight.Cancel();
        return generation;
    }

    private void RetireCatalogPreviewSurface()
    {
        CatalogPreviewFlight[] retired;
        lock (entryPreviewGate) retired = RetireCatalogPreviewSurfaceLocked();
        foreach (var flight in retired) flight.Cancel();
    }

    private CatalogPreviewFlight[] RetireCatalogPreviewSurfaceLocked()
    {
        entryPreviews.Clear();
        var retired = entryPreviewFlights.Values.ToArray();
        entryPreviewFlights.Clear();
        foreach (var flight in retired) flight.Retired = true;
        return retired;
    }

    private Dictionary<string, object?> CardReadParameters(MusicCatalogCard card)
    {
        var parameters = new Dictionary<string, object?> { ["platform"] = card.Platform };
        if (card.Kind == "daily")
            parameters["branch"] = CardDailyBranch(card);
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

    // Optional read-only preview, bounded independently from explicit play intent.
    // Missing/failed data never fabricates titles or starts a recommendation session.
    public async Task<IReadOnlyList<string>> ReadCatalogCardPreviewAsync(MusicCatalogCard card, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || stopping || !IsCatalogPage || loadCancellation is not null ||
            !CanCatalogBrowse || !card.CanRequestPreview || !ContainsCatalogCard(card)) return [];
        try { RequireCatalogContent(); } catch (MusicOperationException) { return []; }
        if (card.HasPreview)
            return card.PreviewSongs.Take(3).Select((song, index) => $"{index + 1} {song.Title}").ToArray();
        var generation = loadGeneration;
        CatalogPreviewFlight? flight = null;
        IReadOnlyList<string> cachedTitles = Array.Empty<string>();
        bool cacheHit = false, stale = false;
        lock (entryPreviewGate)
        {
            if (generation != loadGeneration)
                stale = true;
            else if (entryPreviews.TryGetValue(card.Key, out var cached) && cached.Generation == generation)
            {
                cacheHit = true;
                cachedTitles = cached.Titles;
            }
            else if (entryPreviewFlights.TryGetValue(card.Key, out var pending))
            {
                if (pending.Generation == generation && !pending.Retired &&
                    !pending.Cancellation.IsCancellationRequested)
                {
                    pending.Subscribers++;
                    flight = pending;
                }
                else if (ReferenceEquals(entryPreviewFlights.GetValueOrDefault(card.Key), pending))
                {
                    entryPreviewFlights.Remove(card.Key);
                }
            }
            if (!stale && !cacheHit && flight is null)
            {
                var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(8));
                flight = new(card.Key, generation, card, request) { Subscribers = 1 };
                var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                loadDrains.Add(drained.Task);
                entryPreviewFlights[card.Key] = flight;
                flight.Work = ReadCatalogCardPreviewCoreAsync(card, flight, drained);
            }
        }
        if (stale) return [];
        if (cacheHit) return cachedTitles;
        if (flight is null) return [];
        return await WaitForCatalogPreviewAsync(flight, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> WaitForCatalogPreviewAsync(
        CatalogPreviewFlight flight, CancellationToken cancellationToken)
    {
        try
        {
            return await flight.Work.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return []; }
        finally
        {
            bool cancel = false;
            lock (entryPreviewGate)
            {
                flight.Subscribers--;
                if (flight.Subscribers == 0 && !flight.Work.IsCompleted)
                {
                    flight.Retired = true;
                    if (entryPreviewFlights.TryGetValue(flight.Key, out var current) &&
                        ReferenceEquals(current, flight))
                        entryPreviewFlights.Remove(flight.Key);
                    cancel = true;
                }
            }
            if (cancel) flight.Cancel();
        }
    }

    private async Task<IReadOnlyList<string>> ReadCatalogCardPreviewCoreAsync(
        MusicCatalogCard card, CatalogPreviewFlight flight, TaskCompletionSource drained)
    {
        await Task.Yield(); // Publish the shared flight before any binding callback can re-enter.
        try
        {
            string method = "music.catalog." + card.Kind;
            var reply = ValidateReply(method, await application.ExecuteAsync(
                method, CardReadParameters(card), flight.Cancellation.Token));
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            if (stopping || !IsCatalogPage || flight.Generation != loadGeneration ||
                !ReferenceEquals(flight.Card, card) || !ContainsCatalogCard(card)) return [];
            RequireCatalogContent();
            var cards = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "");
            var titles = cards.SelectMany(s => s.Items).Where(c => c.Song is not null).Take(3)
                .Select((c, i) => $"{i + 1} {c.Title}").ToArray();
            lock (entryPreviewGate)
            {
                if (flight.Retired || flight.Cancellation.IsCancellationRequested ||
                    stopping || !IsCatalogPage || flight.Generation != loadGeneration ||
                    !ReferenceEquals(flight.Card, card) || !ContainsCatalogCard(card)) return [];
                if (entryPreviews.Count >= 32) entryPreviews.Remove(entryPreviews.Keys.First());
                entryPreviews[card.Key] = (flight.Generation, titles);
            }
            return titles;
        }
        catch
        {
            // Do not retry an unavailable optional preview on every pointer entry.
            // A refresh/navigation gets a new generation; cancellation is not a failure.
            if (!flight.Retired && !flight.Cancellation.IsCancellationRequested &&
                flight.Generation == loadGeneration && IsCatalogPage &&
                ReferenceEquals(flight.Card, card) && ContainsCatalogCard(card))
            {
                lock (entryPreviewGate)
                {
                    if (flight.Retired || flight.Cancellation.IsCancellationRequested ||
                        flight.Generation != loadGeneration || !IsCatalogPage ||
                        !ReferenceEquals(flight.Card, card) || !ContainsCatalogCard(card)) return [];
                    if (entryPreviews.Count >= 32) entryPreviews.Remove(entryPreviews.Keys.First());
                    entryPreviews[card.Key] = (flight.Generation, Array.Empty<string>());
                }
            }
            return []; // Optional preview cannot replace the page's error state.
        }
        finally
        {
            lock (entryPreviewGate)
            {
                if (entryPreviewFlights.TryGetValue(flight.Key, out var current) &&
                    ReferenceEquals(current, flight))
                    entryPreviewFlights.Remove(flight.Key);
            }
            flight.Dispose();
            loadDrains.Remove(drained.Task);
            drained.TrySetResult();
        }
    }
    public Task PlayCatalogCardAsync(MusicCatalogCard card)
    {
        if (!IsCatalogPage || loadCancellation is not null || !CanCatalogBrowse || !card.CanPlay || !ContainsCatalogCard(card)) return Task.CompletedTask;
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
        });
    }
}
