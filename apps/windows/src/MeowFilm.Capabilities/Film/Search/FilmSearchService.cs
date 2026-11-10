using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Scripting;

namespace MeowFilm.Capabilities;

/// <summary>UI-dispatcher owner of query state. The native engine owns its execution thread.</summary>
public sealed class FilmSearchService : IAsyncDisposable
{
    public const string ProgramResourceName = "MeowFilm.FilmSearch.js";
    private readonly IJavaScriptJsonEngine engine;
    private readonly IFilmSearchSources sources;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> readRules;
    private readonly IFilmSearchMetadata? metadata;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<FilmSearchSession> sessions = [];
    private bool stopping, closed;
    private Task? preparation, disposal;
    public FilmSearchSession? Current { get; private set; }
    internal int TrackedSessions => sessions.Count;
    public event EventHandler? Changed;

    public FilmSearchService(IJavaScriptJsonEngine engine, IFilmSearchSources sources,
        FilmRuleService rules, IFilmSearchMetadata? metadata = null)
        : this(engine, sources, async token =>
        {
            await rules.InitializeAsync(token);
            return rules.GetSavedSnapshot().AggregateRegexRules;
        }, metadata) { }

    internal FilmSearchService(IJavaScriptJsonEngine engine, IFilmSearchSources sources,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readRules, IFilmSearchMetadata? metadata = null)
    {
        this.engine = engine;
        this.sources = sources;
        this.readRules = readRules;
        this.metadata = metadata;
        sources.Changed += SourcesChanged;
    }

    public static string LoadProgramSource()
    {
        using var stream = typeof(FilmSearchService).Assembly.GetManifestResourceStream(ProgramResourceName)
            ?? throw new InvalidOperationException("Film search resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public FilmSearchSession Start(string query, CancellationToken token = default)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new InvalidOperationException("SHUTTING_DOWN");
        token.ThrowIfCancellationRequested();
        query = query.Trim();
        ArgumentException.ThrowIfNullOrEmpty(query);
        if (Current is { } retained && retained.Query == query)
        {
            if (retained.Cancellation.IsCancellationRequested && !retained.Resuming)
            {
                retained.Resuming = true;
                retained.Work = RestartAsync(retained, retained.Work);
            }
            return retained;
        }
        var previous = Current;
        if (previous is not null) Cancel(previous);
        var session = new FilmSearchSession(query) { Loading = true,
            SitesState = FilmSearchChannelState.Loading,
            MetadataState = metadata is null ? FilmSearchChannelState.Unavailable : FilmSearchChannelState.Loading };
        sessions.Add(session);
        Current = session;
        if (previous is not null) _ = RetireAsync(previous);
        session.Changed += SessionChanged;
        session.Work = RunRoundAsync(session, initialize: true);
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public Task LoadMoreAsync()
    {
        dispatcher.VerifyAccess();
        if (closed || stopping || Current is not { CanLoadMore: true } session) return Task.CompletedTask;
        session.Loading = true;
        session.Publish();
        return session.Work = RunRoundAsync(session, initialize: false);
    }

    public void InvalidateQuery(string query)
    {
        dispatcher.VerifyAccess();
        if (Current is { } session && session.Query != query)
        {
            Current = null;
            Cancel(session);
            _ = RetireAsync(session);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ResolveImage(string path) => metadata?.ResolveImage(path) ?? path;

    public void InvalidateMetadata()
    {
        dispatcher.VerifyAccess();
        var session = Current;
        Current = null;
        if (session is not null) { Cancel(session); _ = RetireAsync(session); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RestartAsync(FilmSearchSession session, Task previous)
    {
        // Keep the snapshot and successful cursors. The retired attempt must physically drain
        // before replacing its cancellation source; late callbacks cannot acquire the new lease.
        await previous;
        if (closed || stopping || !ReferenceEquals(Current, session)) { session.Resuming = false; return; }
        session.Cancellation.Dispose();
        session.Cancellation = new();
        session.Loading = true;
        session.Resuming = false;
        session.Publish();
        await RunRoundAsync(session, initialize: true);
    }

    private async Task RunRoundAsync(FilmSearchSession session, bool initialize)
    {
        await Task.Yield();
        var token = session.Cancellation.Token;
        try
        {
            if (initialize)
            {
                if (!session.SourcesLoaded) session.Rules = (await readRules(token)).ToArray();
                await Task.WhenAll(ReadSitesAsync(session),
                    session.MetadataState is FilmSearchChannelState.Completed or FilmSearchChannelState.Error or FilmSearchChannelState.Unavailable
                        ? Task.CompletedTask : ReadMetadataAsync(session));
            }
            else await ReadPagesAsync(session, session.Cursors.Where(cursor => !cursor.Ended).ToArray());
            token.ThrowIfCancellationRequested();
            await DisplayAsync(session);
            session.AggregateOrder = session.Items.Select(item => FilmSearchJson.Text(item, "displayKey")).ToArray();
            session.RawOrder = session.RawItems.Select(item => FilmSearchJson.Text(item, "displayKey")).ToArray();
        }
        catch (OperationCanceledException)
        {
            if (session.SitesState == FilmSearchChannelState.Loading) session.SitesState = FilmSearchChannelState.Cancelled;
            if (session.MetadataState == FilmSearchChannelState.Loading) session.MetadataState = FilmSearchChannelState.Cancelled;
        }
        catch { session.SitesState = FilmSearchChannelState.Error; }
        finally
        {
            session.Loading = false;
            session.Publish();
        }
    }

    private async Task ReadSitesAsync(FilmSearchSession session)
    {
        var token = session.Cancellation.Token;
        IReadOnlyList<FilmSearchSource> available;
        session.ReadingSources = true;
        try { available = await sources.ReadSearchSourcesAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch { session.SitesState = FilmSearchChannelState.Error; session.Publish(); return; }
        finally { session.ReadingSources = false; }
        token.ThrowIfCancellationRequested();
        var retained = session.Cursors.Where(cursor => available.Any(source =>
            source.Key == cursor.Source.Key && source.Cat.Generation == cursor.Source.Cat.Generation) &&
            sources.IsSearchSourceCurrent(cursor.Source)).ToArray();
        var keys = retained.Select(cursor => cursor.Source.Key).ToHashSet(StringComparer.Ordinal);
        session.SiteItems.RemoveAll(item => !keys.Contains(FilmSearchJson.Text(item, "siteKey")));
        session.Identities.Clear();
        foreach (var item in session.SiteItems) session.Identities.Add(FilmSearchJson.SiteIdentity(item));
        session.Cursors.Clear();
        session.Cursors.AddRange(available.Select(source => retained.FirstOrDefault(cursor => cursor.Source.Key == source.Key)
            ?? new FilmSearchSession.Cursor(source)));
        session.SourcesLoaded = true;
        session.CoverSite = available.FirstOrDefault(source => source.Site.Options.AggregateImages)?.Key ?? "";
        if (available.Count == 0) { session.SitesState = FilmSearchChannelState.NoSources; session.Publish(); return; }
        // Recovery retries only pages interrupted before acknowledgement, never successful pages.
        await ReadPagesAsync(session, session.Cursors.Where(cursor => !cursor.Ended && cursor.NeedsRecovery).ToArray());
    }

    private async Task ReadPagesAsync(FilmSearchSession session, IReadOnlyList<FilmSearchSession.Cursor> cursors)
    {
        session.SitesState = FilmSearchChannelState.Loading;
        int succeeded = 0;
        await Task.WhenAll(cursors.Select(Read));
        session.Cancellation.Token.ThrowIfCancellationRequested();
        session.SitesState = cursors.Count > 0 && succeeded == 0 ? FilmSearchChannelState.Error : FilmSearchChannelState.Completed;
        session.Publish();

        async Task Read(FilmSearchSession.Cursor cursor)
        {
            var token = session.Cancellation.Token;
            JsonElement result;
            cursor.NeedsRecovery = true;
            try
            {
                var source = cursor.Source;
                var response = await sources.SearchAsync(source, session.Query, cursor.Page, token);
                token.ThrowIfCancellationRequested();
                if (!sources.IsSearchSourceCurrent(source)) { Cancel(session); token.ThrowIfCancellationRequested(); }
                var payload = FilmJson.Member(response.Value, "raw");
                if (payload.ValueKind != JsonValueKind.Object) throw new FilmPlaybackException("INVALID_RESPONSE");
                result = await engine.InvokeAsync(new
                {
                    operation = "site", query = session.Query, aggregateRules = session.Rules,
                    site = new { siteKey = source.Key, siteId = source.Site.Key.SiteKey,
                        installationId = source.Cat.InstallationId, generation = source.Cat.Generation,
                        siteName = source.Site.Name, order = source.Site.Order }, page = cursor.Page, payload
                }, token);
                token.ThrowIfCancellationRequested();
                if (!sources.IsSearchSourceCurrent(source)) { Cancel(session); token.ThrowIfCancellationRequested(); }
                cursor.Pages[cursor.Page] = response;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || !sources.IsSearchSourceCurrent(cursor.Source))
            { Cancel(session); throw; }
            catch { cursor.NeedsRecovery = false; return; } // Silent source failure: no advancement, explicit more retries this page.
            int added = 0;
            foreach (var item in FilmSearchJson.Items(result))
                if (session.Identities.Add(FilmSearchJson.SiteIdentity(item)))
                { session.SiteItems.Add(item); added++; }
            bool explicitPages = result.GetProperty("explicitPageCount").GetBoolean();
            cursor.Ended = !result.GetProperty("hasMore").GetBoolean() || !explicitPages && added == 0;
            cursor.Page = checked(cursor.Page + 1);
            cursor.NeedsRecovery = false;
            succeeded++;
            // Display/integration failure is not a source failure and must reach the channel owner.
            await DisplayAsync(session);
        }
    }

    private async Task ReadMetadataAsync(FilmSearchSession session)
    {
        if (metadata is null) return;
        var token = session.Cancellation.Token;
        session.MetadataState = FilmSearchChannelState.Loading;
        try
        {
            if (!session.MetadataSearchCompleted)
            {
                if (session.MetadataSearchPayload is null)
                {
                    var original = await metadata.SearchAsync(session.Query, token);
                    token.ThrowIfCancellationRequested();
                    session.MetadataQuery = session.Query;
                    session.MetadataSearchPayload = original.Value.Clone();
                    session.MetadataData["search:" + session.Query] = original;
                }
                var payload = session.MetadataSearchPayload!.Value;
                string effectiveQuery = session.Query;
                if (payload.GetProperty("results").GetArrayLength() == 0)
                {
                    var fallback = await engine.InvokeAsync(new { operation = "fallback", query = session.Query }, token);
                    string query = FilmSearchJson.Text(fallback, "query");
                    if (query.Length > 0)
                    {
                        var next = await metadata.SearchAsync(query, token);
                        token.ThrowIfCancellationRequested();
                        session.MetadataData["search:" + query] = next;
                        if (next.Value.GetProperty("results").GetArrayLength() > 0) { payload = next.Value; effectiveQuery = query; }
                    }
                }
                session.MetadataQuery = effectiveQuery;
                session.MetadataSearchPayload = payload.Clone();
                session.MetadataSearchCompleted = true;
            }
            if (session.TmdbItems.Count == 0)
            {
                var result = await engine.InvokeAsync(new { operation = "tmdb", query = session.Query,
                    payload = session.MetadataSearchPayload!.Value }, token);
                token.ThrowIfCancellationRequested();
                session.TmdbItems.AddRange(FilmSearchJson.Items(result));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { session.MetadataState = FilmSearchChannelState.Error; session.Publish(); return; }
        await DisplayAsync(session);
        await Task.WhenAll(session.TmdbItems.ToArray().Select(Enhance));
        token.ThrowIfCancellationRequested();
        session.MetadataState = FilmSearchChannelState.Completed;
        session.Publish();

        async Task Enhance(JsonElement item)
        {
            string key = FilmSearchJson.MetadataIdentity(item);
            JsonElement payload, enhanced;
            try
            {
                if (session.Details.TryGetValue(key, out var retained)) payload = retained;
                else
                {
                    var response = await metadata.DetailAsync(FilmSearchJson.Text(item, "tmdbType"),
                        FilmSearchJson.Number(item, "tmdbId"), token);
                    token.ThrowIfCancellationRequested();
                    payload = response.Value;
                    session.MetadataData["detail:" + key] = response;
                }
                token.ThrowIfCancellationRequested();
                session.Details[key] = payload;
                enhanced = await engine.InvokeAsync(new { operation = "detail", item, payload }, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch { return; } // Preserve full search metadata when just this detail fails.
            int index = session.TmdbItems.FindIndex(value => FilmSearchJson.MetadataIdentity(value) == key);
            session.TmdbItems[index] = enhanced;
            await DisplayAsync(session);
        }
    }

    private async Task DisplayAsync(FilmSearchSession session)
    {
        // Capture both projections from the same input. A later publication supersedes older work.
        long revision = ++displayRevision;
        var siteItems = session.SiteItems.ToArray();
        var tmdbItems = session.TmdbItems.ToArray();
        var siteOrder = session.Cursors.Select(cursor => cursor.Source.Key).ToArray();
        async Task<JsonElement[]> Build(bool rawListMode, string[] previousOrder) => FilmSearchJson.Items(
            await engine.InvokeAsync(new { operation = "display", query = session.Query, aggregateRules = session.Rules,
                siteItems, tmdbItems, siteOrder, searchCoverSite = session.CoverSite, rawListMode, previousOrder },
                session.Cancellation.Token));
        var aggregate = await Build(false, session.AggregateOrder);
        var raw = await Build(true, session.RawOrder);
        session.Cancellation.Token.ThrowIfCancellationRequested();
        if (revision != displayRevision) return;
        session.Items = Array.AsReadOnly(aggregate);
        session.RawItems = Array.AsReadOnly(raw);
        session.Publish();
    }
    private long displayRevision;

    private async Task RetireAsync(FilmSearchSession session)
    {
        await session.Work;
        ReleaseSession(session);
    }
    private void ReleaseSession(FilmSearchSession session)
    {
        if (!sessions.Remove(session)) return;
        session.Changed -= SessionChanged;
        session.Cancellation.Dispose();
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        if (!closed && ReferenceEquals(Current, sender)) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void SourcesChanged(object? sender, EventArgs e)
    {
        if (dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) Apply(); else _ = dispatcher.InvokeAsync(Apply);
        void Apply()
        {
            if (closed || stopping || Current is not { } session) return;
            if (!session.SourcesLoaded || session.ReadingSources) return;
            var available = sources.CurrentSearchSources;
            if (!session.Cursors.Select(cursor => (cursor.Source.Key, cursor.Source.Cat.Generation))
                .SequenceEqual(available.Select(source => (source.Key, source.Cat.Generation)))) Cancel(session);
        }
    }
    private static void Cancel(FilmSearchSession session)
    {
        session.Cancellation.Cancel();
        session.SitesState = FilmSearchChannelState.Cancelled;
        if (session.MetadataState == FilmSearchChannelState.Loading) session.MetadataState = FilmSearchChannelState.Cancelled;
        session.Publish();
    }
    public Task PrepareShutdownAsync()
    {
        dispatcher.VerifyAccess();
        if (preparation is not null) return preparation;
        stopping = true;
        foreach (var session in sessions) Cancel(session);
        return preparation = DrainAsync();
    }
    private async Task DrainAsync()
    {
        await Task.WhenAll(sessions.Select(session => session.Work));
    }
    public void Resume()
    {
        dispatcher.VerifyAccess();
        if (closed) throw new InvalidOperationException("CLOSED");
        if (preparation is null) return;
        if (!preparation.IsCompletedSuccessfully) throw new InvalidOperationException("SHUTDOWN_NOT_DRAINED");
        preparation = null;
        stopping = false;
        if (Current is { } session) Start(session.Query);
    }
    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        if (disposal is not null) return new(disposal);
        var drained = PrepareShutdownAsync();
        closed = true;
        sources.Changed -= SourcesChanged;
        return new(disposal = CloseAsync(drained));
    }
    private async Task CloseAsync(Task drained)
    {
        await drained;
        await engine.DisposeAsync();
        foreach (var session in sessions.ToArray()) ReleaseSession(session);
    }
}
