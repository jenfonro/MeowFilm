using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchIdentityTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("front insertion removal and moves retain each opaque selection model", KeyedOrder);
        yield return Case("same-key representative and badge updates notify only changed fields", Fields);
        yield return Case("progress-only snapshots retain both projections without image mapping or card notifications", Status);
        yield return Case("TMDB association replaces only real identities and shares its identity across modes", Modes);
        yield return Case("background mapping leaves Dispatcher free and stop new query or disconnect rejects its late result", BackgroundLifetime);
        yield return Case("a racing command and SSE rebase keyed edits on the latest committed predecessor", RacingPredecessor);
        yield return Case("an accepted malformed display still binds its ID for stop and release", MalformedAllocation);
        foreach (var test in MusicUiTests.FilmSearchIdentityPageCases()) yield return test;
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("film-search-identity: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static void Check(bool condition, string reason) => SearchFixture.Check(condition, reason);
    private static JsonElement Card(string key, string title, string poster = "", string badge = "高清") =>
        FilmJson.Encode(new
        {
            id = "representative:" + key, displayKey = key, sourceKind = "site", title, poster,
            textBadge = badge, siteLabel = "站点", aggregateItems = Array.Empty<object>()
        });
    internal static JsonElement Change(JsonElement value, string name, object replacement)
    {
        var fields = JsonNode.Parse(value.GetRawText())!.AsObject();
        fields[name] = JsonSerializer.SerializeToNode(replacement);
        return JsonSerializer.SerializeToElement(fields);
    }
    private static JsonElement Tmdb(string key, string poster) =>
        Change(SearchFixture.Card(key, "tmdb", "同名", []), "poster", poster);
    private static Task Publish(SearchFixture f, FilmSearchSession session, JsonElement[] cards,
        JsonElement[] raw, long revision) => Publish(f.Runtime, session, cards, raw, revision);
    private static async Task Publish(FilmSearchConsumerRuntime runtime, FilmSearchSession session,
        JsonElement[] cards, JsonElement[] raw, long revision)
    {
        runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, cards, raw, revision));
        await SearchFixture.Until(() => session.Revision == revision);
    }

    private static async Task KeyedOrder()
    {
        await using var f = new SearchFixture();
        JsonElement a = Card("opaque/A", "同名"), b = Card("opaque/B", "同名"), c = Card("opaque/C", "同名");
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [a, b, c], []);
        var session = f.Service.Start("身份"); await SearchFixture.Settle(session);
        var retained = session.CardItems.ToArray();
        var notices = new List<NotifyCollectionChangedAction>();
        session.CardItems.CollectionChanged += (_, e) => notices.Add(e.Action);
        int properties = 0;
        foreach (var model in retained) model.PropertyChanged += (_, _) => properties++;
        await Publish(f, session, [Card("opaque/X", "同名"), a, b, c], [], 2);
        Check(session.CardItems.Skip(1).SequenceEqual(retained) &&
            retained.Select(item => item.SearchContext.DisplayKey).SequenceEqual(["opaque/A", "opaque/B", "opaque/C"]),
            "front insertion cannot rebind A/B/C to their neighbours, even when all titles are equal");
        Check(notices.SequenceEqual([NotifyCollectionChangedAction.Add]) && properties == 0,
            "front insertion adds only the new model without Replace, Reset or unrelated field notifications");
        notices.Clear();
        await Publish(f, session, [c, a], [], 3);
        Check(ReferenceEquals(session.CardItems[0], retained[2]) && ReferenceEquals(session.CardItems[1], retained[0]) &&
            notices.Count(action => action == NotifyCollectionChangedAction.Remove) == 2 &&
            notices.Count(action => action == NotifyCollectionChangedAction.Move) == 1 &&
            notices.All(action => action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Move),
            "deletion and reordering preserve the remaining models through exact Remove and Move operations");
    }

    private static async Task Fields()
    {
        await using var f = new SearchFixture();
        JsonElement a = Card("opaque:unchanged", "A", "https://fixture.invalid/a.jpg"),
            b = Card("opaque:upgraded", "B", "https://fixture.invalid/b.jpg");
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [a, b], []);
        var session = f.Service.Start("字段"); await SearchFixture.Settle(session);
        var first = session.CardItems[0]; var second = session.CardItems[1];
        var firstEvents = new List<string?>(); var secondEvents = new List<string?>();
        first.PropertyChanged += (_, e) => firstEvents.Add(e.PropertyName);
        second.PropertyChanged += (_, e) => secondEvents.Add(e.PropertyName);
        b = Change(b, "textBadge", "共2季");
        await Publish(f, session, [a, b], [], 2);
        Check(ReferenceEquals(session.CardItems[0], first) && ReferenceEquals(session.CardItems[1], second) &&
            firstEvents.Count == 0 && secondEvents.SequenceEqual([nameof(FilmSearchCardItem.TextBadge)]),
            "one nonempty badge change leaves all other bindings, including Poster, untouched");
        secondEvents.Clear();
        b = Change(Change(b, "id", "aggregate:changed-representative"), "textBadge", "");
        b = Change(b, "aggregateItems", new[] { new { sourceKey = "actual-source", detailId = 0 } });
        await Publish(f, session, [a, b], [], 3);
        Check(ReferenceEquals(session.CardItems[1], second) && second.Id == "aggregate:changed-representative" &&
            second.SearchContext.DisplayKey == "opaque:upgraded" &&
            secondEvents.SequenceEqual([nameof(FilmSearchCardItem.Id), nameof(FilmSearchCardItem.TextBadge),
                nameof(FilmSearchCardItem.HasTextBadge)]) &&
            JsonElement.DeepEquals(second.SearchContext.Item, b),
            "a representative ID change is not a selection identity change, and its original members remain intact");
        Check(firstEvents.Count == 0 && second.Poster == "https://fixture.invalid/b.jpg",
            "unrelated cards and unchanged image addresses are not notified");
    }

    private static async Task Status()
    {
        await using var f = new SearchFixture();
        int mappings = 0; var owner = Dispatcher.CurrentDispatcher;
        await using var service = new FilmSearchService(f.Runtime.CaptureConnectionAsync, f.Sources, path =>
        {
            Check(!owner.CheckAccess(), "image-path presentation mapping must not run on the Dispatcher");
            Interlocked.Increment(ref mappings); return path;
        });
        var card = Tmdb("tmdb:tv:7", "/poster.jpg");
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"),
            [card], [card], loading: true, siteState: "loading", metadataState: "loading");
        var session = service.Start("状态"); await session.Work;
        var display = session.Display; var item = session.CardItems.Single();
        int mapped = mappings, changed = 0, collections = 0;
        item.PropertyChanged += (_, _) => changed++;
        session.CardItems.CollectionChanged += (_, _) => collections++;
        session.RawCardItems.CollectionChanged += (_, _) => collections++;
        var snapshot = SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [card], [card], revision: 2,
            hasMore: true);
        snapshot = Change(snapshot, "metadataDetails", new { full = new { overview = "完整元数据", seasons = new[] { 1, 2 } } });
        f.Runtime.PublishSearch(snapshot); await SearchFixture.Until(() => session.Revision == 2);
        Check(ReferenceEquals(display, session.Display) && ReferenceEquals(session.CardItems.Single(), item) &&
            ReferenceEquals(session.RawCardItems.Single(), item) && mapped == mappings && mapped > 0 &&
            changed == 0 && collections == 0 && session.CanLoadMore,
            "progress and status settle without remapping either mode or rebinding a card");
        Check(session.Snapshot.GetProperty("metadataDetails").GetProperty("full").GetProperty("seasons").GetArrayLength() == 2,
            "the retained complete snapshot still includes metadataDetails");
    }

    private static async Task Modes()
    {
        await using var f = new SearchFixture();
        var raw = SearchFixture.Member(f.Sources.Sources[0], "same", "同名");
        var group = SearchFixture.Card("group:opaque", "site", "同名", [raw]);
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [group], [raw]);
        var session = f.Service.Start("模式"); await SearchFixture.Settle(session);
        var groupModel = session.CardItems.Single(); var rawModel = session.RawCardItems.Single();
        var tmdb = SearchFixture.Card("tmdb:tv:7", "tmdb", "同名", [raw]);
        await Publish(f, session, [tmdb], [raw, tmdb], 2);
        Check(!ReferenceEquals(session.CardItems.Single(), groupModel) &&
            ReferenceEquals(session.RawCardItems[0], rawModel) &&
            ReferenceEquals(session.CardItems.Single(), session.RawCardItems[1]),
            "a real group-to-TMDB identity transition replaces the group, keeps raw identity, and shares the TMDB model");
        var opened = session.CardItems.Single().ToCard();
        Check(opened.SearchContext!.DisplayKey == "tmdb:tv:7" &&
            JsonElement.DeepEquals(opened.SearchContext.Item.GetProperty("aggregateItems")[0], raw),
            "activation retains the exact opaque selection and its real member, not a title or representative guess");
    }

    private static async Task BackgroundLifetime()
    {
        foreach (string retirement in new[] { "stop", "query", "disconnect" })
        {
            await using var f = new SearchFixture();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = Dispatcher.CurrentDispatcher;
            await using var service = new FilmSearchService(f.Runtime.CaptureConnectionAsync, f.Sources, path =>
            {
                Check(!owner.CheckAccess(), "JSON presentation mapping must stay off Dispatcher");
                if (path == "/blocked.jpg") { entered.TrySetResult(); release.Wait(); }
                return path;
            });
            var initial = Tmdb("tmdb:tv:7", "/initial.jpg");
            f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [initial], []);
            var session = service.Start("旧词"); await SearchFixture.Settle(session);
            var original = session.CardItems.Single();
            try
            {
                f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query,
                    [Tmdb("tmdb:tv:7", "/blocked.jpg")], [], revision: 2));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.Yield();
                if (retirement == "query")
                {
                    var current = service.Start("新词"); await SearchFixture.Settle(current);
                    Check(current != session && current.CardItems.Single().SearchContext.Session == current,
                        "a blocked old presentation does not block a new query or reuse its selection model");
                    release.Set();
                    await SearchFixture.Until(() => service.TrackedSessions == 1);
                    Check(ReferenceEquals(service.Current, current) && session.Revision == 1 &&
                        current.CardItems.Single().Poster == "/initial.jpg", "retired background results cannot publish");
                }
                else if (retirement == "disconnect")
                {
                    f.Runtime.Revoked.Cancel(); release.Set();
                    await SearchFixture.Until(() => session.Error == "DISCONNECTED");
                    Check(session.Revision == 1 && ReferenceEquals(session.CardItems.Single(), original) &&
                        original.Poster == "/initial.jpg", "a revoked producer cannot publish its prepared projection");
                }
                else
                {
                    await service.StopAsync();
                    release.Set(); await SearchFixture.Until(() => session.ReferenceRevision == 2);
                    Check(session.Revision == 2 && ReferenceEquals(session.CardItems.Single(), original) &&
                        original.Poster == "/initial.jpg", "stop fences an already-running projection without changing its cards");
                    service.Start(session.Query); await session.Work;
                    Check(session.Revision == 2 && ReferenceEquals(session.CardItems.Single(), original) &&
                        original.Poster == "/blocked.jpg", "explicit resume updates the same selection after the fence");
                }
            }
            finally { release.Set(); }
        }
    }

    private static async Task RacingPredecessor()
    {
        await using var f = new SearchFixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new FilmSearchService(f.Runtime.CaptureConnectionAsync, f.Sources, path =>
        {
            if (path == "/sse.jpg") { entered.TrySetResult(); release.Wait(); }
            return path;
        });
        var a = Tmdb("tmdb:tv:7", "/initial.jpg"); var b = Card("opaque/B", "B");
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [a, b], [], hasMore: true);
        var session = service.Start("竞态"); await SearchFixture.Settle(session);
        var original = session.CardItems.ToArray();
        try
        {
            var x = Card("opaque/X", "X"); var latest = Tmdb("tmdb:tv:7", "/sse.jpg");
            f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [b, latest], [], revision: 3));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Runtime.SearchCommand = (input, current) => FilmJson.Text(input, "action") == "more"
                ? SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [x, a, b], [], revision: 2) : current;
            await service.LoadMoreAsync();
            Check(session.Revision == 2 && session.CardItems.Count == 3 &&
                ReferenceEquals(session.CardItems[1], original[0]) && ReferenceEquals(session.CardItems[2], original[1]),
                "a newer command can commit while the SSE projection is preparing in the background");
            release.Set(); await SearchFixture.Until(() => session.Revision == 3);
            Check(session.CardItems.Count == 2 && ReferenceEquals(session.CardItems[0], original[1]) &&
                ReferenceEquals(session.CardItems[1], original[0]) && original[0].Poster == "/sse.jpg",
                "the final SSE edits rebase on the committed command, removing X without duplicates or wrong rebindings");
        }
        finally { release.Set(); }
    }

    private static async Task MalformedAllocation()
    {
        await using var f = new SearchFixture();
        f.Runtime.SearchOpen = (id, input) => FilmJson.Encode(new { sessionId = id, query = FilmJson.Text(input, "query"), revision = 1 });
        var session = f.Service.Start("坏展示"); await session.Work;
        Check(Guid.TryParse(session.Id, out _) && session.Error.Length > 0 && session.CardItems.Count == 0,
            "display validation failure cannot lose the identity of an already allocated Node session");
        f.Service.InvalidateMetadata();
        await SearchFixture.Until(() => f.Service.TrackedSessions == 0);
        Check(f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "stop") == 1 &&
            f.Runtime.Calls.Count(call => call.Method == "film.search.release") == 1,
            "the accepted malformed display still drains its real stop and release actions");
    }
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> FilmSearchIdentityPageCases()
    {
        yield return Case("film search mode changes reuse prepared models and activation preserves the exact item", IdentityModePage);
    }

    private static async Task IdentityModePage()
    {
        await using var f = await UnifiedScene.Create();
        var raw = SearchFixture.Member(f.Sources.Sources[0], 0, "同名");
        var group = SearchFixture.Card("opaque/group", "site", "同名", [raw]);
        var metadata = SearchFixture.Card("opaque/metadata", "tmdb", "同名", []);
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [group, metadata], [raw, metadata]);
        await f.Submit("切换", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var aggregate = f.Page.Cards; var tmdb = aggregate[1];
        int requests = f.Runtime.Calls.Count;
        f.Page.RawListMode = true;
        var rawCards = f.Page.Cards;
        True(!ReferenceEquals(aggregate, rawCards) && ReferenceEquals(rawCards[1], tmdb) &&
            rawCards[0].SearchContext.DisplayKey == FilmJson.Text(raw, "displayKey"),
            "mode switching uses prepared exact identities and shares unchanged metadata");
        f.Page.RawListMode = false;
        True(ReferenceEquals(f.Page.Cards, aggregate) && ReferenceEquals(f.Page.Cards[1], tmdb) &&
            f.Runtime.Calls.Count == requests, "returning to aggregate mode does not reconstruct models or request data");
        f.Page.RawListMode = true;
        var card = new FilmCard { DataContext = rawCards[0] };
        FilmCardItem? selected = null; card.Activated += (_, e) => selected = e.Item;
        ((Button)card.FindName("HitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(selected is not null && ReferenceEquals(selected.SearchContext, rawCards[0].SearchContext) &&
            JsonElement.DeepEquals(selected.SearchContext!.Item, raw) && selected.Title == rawCards[0].Title,
            "the native FilmCard event carries the selected original typed zero row through the existing playback contract");
    }
}
