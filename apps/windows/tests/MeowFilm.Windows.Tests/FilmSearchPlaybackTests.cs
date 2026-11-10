using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchPlaybackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("typed members raw rows and confirmed independent cursors reach one group session", TypedHandoff);
        yield return Case("explicit page counts win over repeated typed identities", ExplicitPages);
        yield return Case("metadata uses one captured producer and original cache identity receipts", MetadataReceipt);
        yield return Case("invalid cache receipts and owner failures are never successful content", ReceiptFailures);
        yield return Case("revocation fences late content and late cache receipt", RevokedReceipt);
        yield return Case("ordinary waiter cancellation never revokes the runtime owner", CancelledWaiter);
        yield return Case("late carrier updates one session and cannot expand sources or revive a closed page", LiveUpdate);
        yield return Case("late update from the previous page cannot publish into a replacement owner", StaleUpdate);
        yield return Case("a failed update remains observable without losing a newer coalesced publication", FailedUpdate);
        yield return Case("group and raw reopen preserve accepted targets and opaque candidate identity", ReopenSelection);
        yield return Case("group open and reconnect retain only admitted original members with their typed identity", GroupScope);
        yield return Case("group targets survive new search sessions without merging typed raw or movie and tv identities", StableIdentity);
    }
    private static TestCase Case(string name, Func<Task> action) =>
        new("film-search-playback: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static void Check(bool condition, string reason) => SearchFixture.Check(condition, reason);
    private static JsonElement Encode(object value) => FilmJson.Encode(value);
    private static object Open(FilmPlaybackRequest request) => new
    {
        platform = "windows", target = request.Target.Wire(), sources = request.Sources, query = request.Query,
        results = request.Results, search = request.Search, metadata = request.Metadata, cacheEntries = request.CacheEntries
    };

    private static async Task TypedHandoff()
    {
        using var runtime = new FilmSearchConsumerRuntime();
        var a = SearchFixture.Source("a"); var b = SearchFixture.Source("b", order: 1);
        var sources = new SearchSources(a, b) { DataOwner = runtime.Connection };
        sources.Read = (source, _, page, _) => Task.FromResult(Encode(new
        {
            pagecount = source.Key == a.Key ? 2 : 1,
            list = source.Key == a.Key && page == 1 ? new object[]
            {
                new { vod_id = (object)0, vod_name = "目标 第一季", vod_remarks = " 原始备注 ", privateRaw = new { untouched = "0 " } },
                new { vod_id = (object)"0", vod_name = "目标 第一季", vod_remarks = "字符串身份", extra = 9 }
            } : new object[] { new { vod_id = source.Key == a.Key ? (object)"next" : 0,
                vod_name = "目标 第二季", vod_remarks = "第三集" } }
        }));
        sources.CacheEntry = (source, query, page, raw) => Encode(new
        {
            kind = "search", source = source.Wire, query, page, value = raw,
            completeness = "complete", fetchedAt = 1111L, expiresAt = 2222L
        });
        await using var fixture = new SearchFixture(sources);
        var search = fixture.Service.Start(" 不同的原始词 "); await SearchFixture.Settle(search);
        var context = new FilmSearchContext(search, search.Items.Single());
        var first = FilmSearchPlayback.Capture(context);
        Check(first.Target.Kind == "group" && first.Target.Members!.Value.GetArrayLength() == 3 &&
            context.Sources.Select(FilmSearchJson.SiteIdentity).Distinct().Count() == 3 &&
            FilmJson.Array(first.Search!.Value).All(row => FilmJson.Integer(row, "page") == 1),
            "numeric zero and string zero stay distinct; the handoff page is confirmed one, not next request two");
        Check(first.DataOwner is { } owner && ReferenceEquals(owner.Client, runtime) && first.Query == "不同的原始词",
            "the response producer, not a click-time capture, accompanies the original trimmed query");
        var rows = FilmJson.Array(first.Results!.Value).SelectMany(row => FilmJson.Array(row, "items")).ToArray();
        Check(rows.Any(row => FilmJson.Member(row, "vod_id").ValueKind == JsonValueKind.Number &&
            FilmJson.Text(row, "vod_remarks") == " 原始备注 " &&
            FilmJson.Text(FilmJson.Member(row, "privateRaw"), "untouched") == "0 "),
            "complete untrimmed upstream rows reach results, not reconstructed display titles or remarks");
        var entries = FilmJson.Array(first.CacheEntries!.Value);
        Check(entries.Length == 2 && entries.All(entry => entry.GetProperty("fetchedAt").GetInt64() == 1111 &&
            entry.GetProperty("expiresAt").GetInt64() == 2222 && FilmJson.Text(entry, "completeness") == "complete"),
            "only the supplied original receipts, without local clock substitution, are handed off");
        await fixture.Service.LoadMoreAsync();
        var request = FilmSearchPlayback.Capture(context);
        Check(FilmJson.Array(request.Search!.Value).Single(row => FilmJson.Text(row, "sourceKey") == a.Key)
            .GetProperty("page").GetInt32() == 2 && request.Target.Members!.Value.GetArrayLength() == 4,
            "late members and independently confirmed pages survive the same context");
        var rawItem = search.RawItems.Single(item => FilmJson.Text(item, "siteKey") == a.Key &&
            FilmJson.Member(item, "detailId").ValueKind == JsonValueKind.Number);
        var raw = FilmSearchPlayback.Capture(new(search, rawItem));
        Check(raw.Target.Kind == "source" && raw.Sources!.Count == 1 && raw.Metadata is null &&
            raw.Target.Source!.Value.GetProperty("detailId").ValueKind == JsonValueKind.Number &&
            FilmJson.Array(raw.Results!.Value).Single().GetProperty("items").GetArrayLength() == 1,
            "raw keeps numeric zero and only its actual member/source, never a representative group");
        await using var native = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var card = new FilmCardItem(request.Target.Id, request.Target.Title, "", "") { SearchContext = context };
        var page = FilmPlaybackPage.FromSearch(card, native, null);
        try
        {
            await native.OpenAsync(page, Open(page.CaptureRequest), default);
            Check(runtime.Calls.Count(call => call.Method == "film.session.open") == 1 &&
                FilmJson.Array(runtime.Calls.Single(call => call.Method == "film.session.open").Input, "results")
                    .SelectMany(row => FilmJson.Array(row, "items")).Count() == 4,
                "the real native session accepts the lossless group handoff exactly once");
            Console.WriteLine("OBSERVED film-search-wire typed members=4 rows=4 pages=2,1 rawSources=1 rawNumericZero=true originalReceipt=1111,2222 opens=1");
        }
        finally { page.ReleaseSearch(); }
    }

    private static async Task StableIdentity()
    {
        var source = SearchFixture.Source("stable-provider");
        var original = Encode(new
        {
            pagecount = 1,
            list = new object[]
            {
                new { vod_id = (object)0, vod_name = "目标 第一季", vod_remarks = "数值身份" },
                new { vod_id = (object)"0", vod_name = "目标 第一季", vod_remarks = "字符串身份" }
            }
        });
        SearchSources Sources() => new(source) { Read = (_, _, _, _) => Task.FromResult(original) };
        await using var firstFixture = new SearchFixture(Sources());
        await using var secondFixture = new SearchFixture(Sources());
        var firstSession = firstFixture.Service.Start("目标");
        await SearchFixture.Settle(firstSession);
        var secondSession = secondFixture.Service.Start("目标");
        await SearchFixture.Settle(secondSession);
        var first = new FilmSearchContext(firstSession, firstSession.Items.Single());
        var second = new FilmSearchContext(secondSession, secondSession.Items.Single());
        var firstTarget = FilmSearchPlayback.Capture(first).Target;
        var secondTarget = FilmSearchPlayback.Capture(second).Target;
        string groupKey = FilmSearchJson.Text(first.Item, "groupKey");
        Check(firstSession.Id != secondSession.Id && groupKey.Length > 0 &&
            groupKey == FilmSearchJson.Text(second.Item, "groupKey") && first.DisplayKey == second.DisplayKey &&
            firstTarget.Kind == "group" && secondTarget.Kind == "group" &&
            firstTarget.Id == first.DisplayKey && firstTarget.Id == secondTarget.Id &&
            firstTarget.Members!.Value.GetArrayLength() == 2 &&
            JsonElement.DeepEquals(firstTarget.Members!.Value, secondTarget.Members!.Value),
            "the same provider rows and shared group identity keep the persistent target across distinct real query sessions");
        FilmPlaybackTarget Raw(FilmSearchSession session, JsonValueKind kind) => FilmSearchPlayback.Capture(new(session,
            session.RawItems.Single(item => FilmJson.Member(item, "detailId").ValueKind == kind))).Target;
        var numeric = Raw(firstSession, JsonValueKind.Number);
        var text = Raw(firstSession, JsonValueKind.String);
        Check(numeric.Kind == "source" && text.Kind == "source" && numeric.Id != text.Id &&
            numeric.Id == Raw(secondSession, JsonValueKind.Number).Id &&
            text.Id == Raw(secondSession, JsonValueKind.String).Id &&
            numeric.Source!.Value.GetProperty("detailId").GetInt32() == 0 &&
            text.Source!.Value.GetProperty("detailId").GetString() == "0",
            "raw typed zero identities remain distinct and stable through the real shared projection and Capture");

        var metadata = new SearchMetadata
        {
            ReadSearch = (_, _) => Task.FromResult(Encode(new { results = new object[]
            {
                new { media_type = "movie", id = 7, title = "目标", overview = "电影" },
                new { media_type = "tv", id = 7, name = "目标", overview = "剧集" }
            } }))
        };
        await using var metadataFixture = new SearchFixture(metadata: metadata);
        var metadataSession = metadataFixture.Service.Start("目标");
        await SearchFixture.Settle(metadataSession);
        var movie = FilmSearchPlayback.Capture(new(metadataSession, metadataSession.Items.Single(item =>
            FilmSearchJson.Text(item, "tmdbType") == "movie"))).Target;
        var tv = FilmSearchPlayback.Capture(new(metadataSession, metadataSession.Items.Single(item =>
            FilmSearchJson.Text(item, "tmdbType") == "tv"))).Target;
        Check(movie.Kind == "movie" && tv.Kind == "tv" && movie.MetadataProvider == "tmdb" &&
            tv.MetadataProvider == "tmdb" && movie.MetadataId == "7" && tv.MetadataId == "7" &&
            movie.Id == "tmdb:movie:7" && tv.Id == "tmdb:tv:7" && movie.Id != tv.Id,
            "equal TMDB numbers and titles never collapse the explicit movie and tv identities");
        Console.WriteLine("OBSERVED film-search-wire stable-identity " + JsonSerializer.Serialize(new
        {
            firstSession = firstSession.Id, secondSession = secondSession.Id,
            groupKey, groupId = firstTarget.Id, stableGroup = firstTarget.Id == secondTarget.Id,
            rawNumericId = numeric.Id, rawStringId = text.Id, movieId = movie.Id, tvId = tv.Id
        }));
    }

    private static async Task ExplicitPages()
    {
        var sources = new SearchSources(SearchFixture.Source("a"))
        { Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(3, ("same", "目标", ""))) };
        await using var fixture = new SearchFixture(sources);
        var search = fixture.Service.Start("目标"); await SearchFixture.Settle(search);
        await fixture.Service.LoadMoreAsync();
        Check(search.CanLoadMore && search.RawItems.Count == 1, "known third page is not hidden by a duplicate second page");
        await fixture.Service.LoadMoreAsync();
        var request = FilmSearchPlayback.Capture(new(search, search.Items.Single()));
        Check(!search.CanLoadMore && sources.Calls.Select(call => call.Page).SequenceEqual(new[] { 1, 2, 3 }) &&
            FilmJson.Array(request.Search!.Value).Single().GetProperty("page").GetInt32() == 3 &&
            FilmJson.Bool(FilmJson.Array(request.Search.Value).Single(), "completed"), "true page count controls termination and handoff");
    }

    private static async Task MetadataReceipt()
    {
        using var producer = new FilmSearchConsumerRuntime();
        using var replacement = new FilmSearchConsumerRuntime();
        var facade = new CapturingFacade(producer);
        JsonElement entry = default;
        producer.Respond = (method, input, _) =>
        {
            if (method == "film.data.metadata")
            {
                Check(FilmJson.Text(input, "provider") == "tmdb", "metadata provider remains explicit");
                string path = FilmJson.Text(input, "path");
                var query = input.GetProperty("query");
                Check(FilmJson.Text(query, "language") == "zh-CN", "required language is part of the key");
                if (path == "/3/search/multi")
                    Check(FilmJson.Text(input, "imageSize") == "w500" && query.GetProperty("page").GetInt32() == 1 &&
                        FilmJson.Text(query, "query") == " 原查询 ", "list request preserves exact query and w500 identity");
                else Check(path == "/3/tv/7" && FilmJson.Text(input, "imageSize") == "original" &&
                    FilmJson.Text(query, "append_to_response") == "credits,similar,images", "detail uses the unique targetMetadata identity");
                var value = path == "/3/search/multi" ? SearchFixture.Tmdb("tv", 7, "目标", "简介") :
                    Encode(new { id = 7, name = "目标", seasons = Array.Empty<object>(), raw = new { notDisplay = " kept " } });
                facade.Current = replacement;
                entry = Encode(new { kind = "metadata", provider = "tmdb", path, query,
                    imageSize = FilmJson.Text(input, "imageSize"), settings = new { revision = "opaque-original" },
                    completeness = "complete", fetchedAt = 42L, expiresAt = 84L, value });
                return Task.FromResult(FilmSearchConsumerRuntime.Success(value));
            }
            Check(method == "film.data.cache.entry" &&
                FilmJson.Text(input, "path") == FilmJson.Text(entry, "path") &&
                JsonElement.DeepEquals(input.GetProperty("query"), entry.GetProperty("query")) &&
                FilmJson.Text(input, "imageSize") == FilmJson.Text(entry, "imageSize"),
                "the same fixed producer exports the exact content key");
            return Task.FromResult(FilmSearchConsumerRuntime.Success(entry));
        };
        replacement.Respond = producer.Respond;
        await using var service = new FilmMetadataService(facade);
        IFilmSearchMetadata consumer = service;
        var search = await consumer.SearchAsync(" 原查询 ", default);
        var detail = await consumer.DetailAsync("tv", 7, default);
        Check(facade.Captures == 2 && facade.Calls == 0 &&
            ReferenceEquals(search.Owner!.Client, producer) && ReferenceEquals(detail.Owner!.Client, replacement) &&
            producer.Calls.Count == 2 && replacement.Calls.Count == 2 &&
            detail.CacheEntry!.Value.GetProperty("fetchedAt").GetInt64() == 42 &&
            detail.CacheEntry.Value.GetProperty("settings").GetProperty("revision").GetString() == "opaque-original" &&
            detail.Value.GetProperty("raw").GetProperty("notDisplay").GetString() == " kept ",
            "even after the facade switches epochs, each content and receipt pair stays on its exact producer");
        Console.WriteLine($"OBSERVED film-search-wire metadata captures={facade.Captures} facadeCalls={facade.Calls} producerCalls={producer.Calls.Count},{replacement.Calls.Count} fetchedAt=42 expiresAt=84 revision=opaque-original fullAppend=true imageSize=original");
    }

    private static async Task ReceiptFailures()
    {
        foreach (string failure in new[] { "shape", "contract", "STALE_SESSION", "CLOSED" })
        {
            using var runtime = new FilmSearchConsumerRuntime();
            runtime.Respond = (method, _, _) => Task.FromResult(method == "film.data.metadata"
                ? FilmSearchConsumerRuntime.Success(SearchFixture.Tmdb("tv", 7, "目标", "原始简介"))
                : failure == "shape" ? FilmSearchConsumerRuntime.Success(new[] { 1 })
                : failure == "contract" ? Encode(new { ok = true, contract = "tmdb.data.v1", value = (object?)null })
                : FilmSearchConsumerRuntime.Failure(failure));
            await using var service = new FilmMetadataService(runtime);
            await FilmPlaybackFixture.Error(failure is "shape" or "contract" ? "INVALID_RESPONSE" : failure,
                () => service.SearchAsync("目标", default));
        }
        using var valid = new FilmSearchConsumerRuntime();
        valid.Respond = (method, _, _) => Task.FromResult(FilmSearchConsumerRuntime.Success(
            method == "film.data.metadata" ? (object)SearchFixture.Tmdb("tv", 7, "目标", "简介") : null));
        await using var metadata = new FilmMetadataService(valid);
        var response = await ((IFilmSearchMetadata)metadata).SearchAsync("目标", default);
        Check(response.CacheEntry is null && response.Value.GetProperty("results").GetArrayLength() == 1,
            "only successful null means content exists without a retained cache entry");
    }

    private static async Task RevokedReceipt()
    {
        foreach (bool atReceipt in new[] { false, true })
        {
            using var runtime = new FilmSearchConsumerRuntime();
            var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Respond = async (method, _, _) =>
            {
                if ((method == "film.data.cache.entry") == atReceipt) { entered.TrySetResult(); return await release.Task; }
                return FilmSearchConsumerRuntime.Success(SearchFixture.Tmdb("tv", 7, "目标", "简介"));
            };
            await using var service = new FilmMetadataService(runtime);
            var read = service.SearchAsync("目标", default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.Revoked.Cancel();
            release.TrySetResult(FilmSearchConsumerRuntime.Success(atReceipt ? null : (object)SearchFixture.Tmdb("tv", 7, "目标", "晚到")));
            try { await read; throw new InvalidOperationException("Revoked content was published"); }
            catch (OperationCanceledException) { }
            Check(atReceipt || runtime.Calls.Count == 1, "late content cannot even request a new cache receipt after revocation");
        }
    }

    private static async Task CancelledWaiter()
    {
        using var runtime = new FilmSearchConsumerRuntime();
        using var waiter = new CancellationTokenSource();
        var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Respond = (_, _, _) => receipt.Task;
        await using var metadata = new FilmMetadataService(runtime);
        var read = metadata.SearchAsync("原查询", waiter.Token);
        waiter.Cancel();
        receipt.SetResult(FilmSearchConsumerRuntime.Success(SearchFixture.Tmdb("tv", 7, "目标", "有限晚到")));
        try { await read; throw new InvalidOperationException("Cancelled waiter published"); }
        catch (OperationCanceledException) { }
        Check(!runtime.Revoked.IsCancellationRequested && runtime.Calls.Count == 1,
            "ordinary query cancellation fences publication, not the App epoch or a new cache receipt");
    }

    private static async Task LiveUpdate()
    {
        using var runtime = new FilmSearchConsumerRuntime();
        var second = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1)) { DataOwner = runtime.Connection };
        sources.Read = (source, _, _, _) => source.Cat.InstallationId == "a"
            ? Task.FromResult(SearchFixture.Page(1, ("0", "目标 第一季", "原备注"))) : second.Task;
        var tmdb = new SearchMetadata { DataOwner = runtime.Connection, ReadSearch = (_, _) => metadata.Task };
        await using var fixture = new SearchFixture(sources, tmdb);
        var search = fixture.Service.Start("原词"); await SearchFixture.Until(() => search.Items.Count == 1);
        var context = new FilmSearchContext(search, search.Items.Single());
        await using var native = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var card = new FilmCardItem("selection", "目标", "", "") { SearchContext = context };
        var page = FilmPlaybackPage.FromSearch(card, native, null);
        try
        {
            await native.OpenAsync(page, Open(page.CaptureRequest), default);
            var sessionId = runtime.Calls.Single(call => call.Method == "film.session.open").Input.GetProperty("target").GetProperty("id").GetString();
            second.TrySetResult(SearchFixture.Page(1, ("other", "目标 第二季", "后到")));
            metadata.TrySetResult(SearchFixture.Tmdb("tv", 7, "目标", "简介"));
            await SearchFixture.Settle(search);
            await SearchFixture.Until(() => Updates(runtime).Any(update => FilmJson.Member(update, "metadataTarget").ValueKind == JsonValueKind.Object &&
                FilmJson.Array(update, "results").SelectMany(row => FilmJson.Array(row, "items")).Count() == 2));
            var updates = Updates(runtime);
            Check(runtime.Calls.Count(call => call.Method == "film.session.open") == 1 &&
                updates.All(update => FilmJson.Text(update, "selectionId") == sessionId &&
                    FilmJson.Array(update, "sources").Length == 2) &&
                updates.Select(update => update.GetProperty("revision").GetInt64()).SequenceEqual(
                    updates.Select(update => update.GetProperty("revision").GetInt64()).Order()) &&
                page.ActiveTarget.Kind == "group" && page.SearchContext == context &&
                sources.Calls.Count == 2 && tmdb.Searches.Count == 1,
                "one real session receives only already observed pages and proven metadata; native does not switch target or re-search");
            var raw = FilmJson.Encode(page.CaptureRequest.Target.Wire());
            var expanded = page.Request with { Sources = [.. page.Request.Sources!, SearchFixture.Source("outside").Wire] };
            var foreignPage = new FilmPlaybackPage(card with { SearchContext = null }, expanded, native, null);
            var sanitized = FilmPlaybackOpen.SearchUpdateFor(foreignPage, page.Request.Target,
                new("tmdb:tv:7", "tv", "目标", "tmdb", "7"), search.Revision + 1, runtime.Connection, page.Request.Sources!);
            Check(FilmJson.Array(sanitized, "sources").Length == 2, "an update cannot expand the session's original admitted scope");
            await native.CloseSessionAsync();
            int commands = Updates(runtime).Length;
            search.Publish(); await Task.Yield(); await Task.Yield();
            Check(Updates(runtime).Length == commands && JsonElement.DeepEquals(raw, FilmJson.Encode(page.CaptureRequest.Target.Wire())),
                "a still-retained context cannot send commands or reset target after session close");
            Console.WriteLine($"OBSERVED film-search-wire live-update opens=1 updates={commands} admittedSources=2 originalQuery=true metadataCarrier=tmdb:tv:7 closedUpdates=0");
        }
        finally
        {
            second.TrySetResult(SearchFixture.Page(1)); metadata.TrySetResult(SearchFixture.Tmdb("tv", 7, "目标", ""));
            page.ReleaseSearch();
        }
    }

    private static JsonElement[] Updates(FilmSearchConsumerRuntime runtime) => runtime.Calls
        .Where(call => call.Method == "film.session.command" && FilmJson.Text(call.Input, "action") == "search.update")
        .Select(call => call.Input).ToArray();

    private static async Task StaleUpdate()
    {
        using var runtime = new FilmSearchConsumerRuntime();
        var sources = new SearchSources(SearchFixture.Source("a")) { DataOwner = runtime.Connection,
            Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1, ("0", "目标", ""))) };
        await using var fixture = new SearchFixture(sources);
        var search = fixture.Service.Start("原词"); await SearchFixture.Settle(search);
        await using var native = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var context = new FilmSearchContext(search, search.Items.Single());
        var oldPage = FilmPlaybackPage.FromSearch(new("old", "目标", "", "") { SearchContext = context }, native, null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        native.OperationFailed += (_, code) => errors.Add(code);
        try
        {
            await native.OpenAsync(oldPage, Open(oldPage.CaptureRequest), default);
            await SearchFixture.Until(() => Updates(runtime).Length > 0);
            runtime.Respond = async (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "search.update")
                { entered.TrySetResult(); await release.Task; return FilmSearchConsumerRuntime.Failure("UPSTREAM_ERROR"); }
                return runtime.Handle(method, input);
            };
            search.Publish();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = new FilmPlaybackRequest(new("next", "movie", "另一选择", "tmdb", "8"), []);
            var nextPage = new FilmPlaybackPage(new("next", "另一选择", "", ""), request, native, null);
            var opening = native.OpenAsync(nextPage, Open(request), default);
            release.TrySetResult(); await opening;
            Check(ReferenceEquals(nextPage.Request, request) && nextPage.ActiveTarget.Id == "next" && nextPage.CanPlay &&
                runtime.Calls.Count(call => call.Method == "film.session.open") == 2,
                "the old receipt is fenced by the native owner generation before the replacement page publishes");
            int oldUpdates = Updates(runtime).Length;
            search.Publish(); await Task.Yield(); await Task.Yield();
            Check(Updates(runtime).Length == oldUpdates && nextPage.ActiveTarget.Id == "next",
                "a retained old query cannot command the new business session");
            Check(errors.Count == 0, "the retired update's failure is not delivered as a new page operation error");
            Console.WriteLine($"OBSERVED film-search-wire stale-update opens=2 target={nextPage.ActiveTarget.Id} replacementCommands=0 replacementErrors={errors.Count}");
        }
        finally { release.TrySetResult(); oldPage.ReleaseSearch(); }
    }

    private static async Task ReopenSelection()
    {
        foreach (bool raw in new[] { false, true })
        {
            using var runtime = new FilmSearchConsumerRuntime();
            var sources = new SearchSources(SearchFixture.Source("a")) { DataOwner = runtime.Connection,
                Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1, ("0", "目标 第一季", ""))) };
            await using var fixture = new SearchFixture(sources);
            var search = fixture.Service.Start("原词"); await SearchFixture.Settle(search);
            var context = new FilmSearchContext(search, (raw ? search.RawItems : search.Items).Single());
            await using var session = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
            var page = FilmPlaybackPage.FromSearch(new("selection", "目标", "", "") { SearchContext = context }, session, null);
            try
            {
                await session.OpenAsync(page, Open(page.CaptureRequest), default);
                var selected = raw ? page.ActiveTarget with
                {
                    Source = Encode(new { key = sources.Sources[0].Key, detailId = "0", candidateId = "opaque-selected" })
                } : new FilmPlaybackTarget(page.ActiveTarget.Id, "tv", "真实后选身份", "tmdb", "7", Season: 2, Episode: 4);
                await SearchFixture.Until(() => Updates(runtime).Length > 0);
                runtime.Publish(runtime.LastSessionId, selected, Encode(new { name = "已选资料" }));
                await SearchFixture.Until(() => raw ? FilmJson.Text(page.ActiveTarget.Source!.Value, "candidateId") == "opaque-selected" :
                    page.ActiveTarget.Season == 2 && page.ActiveTarget.Episode == 4);
                await session.CloseSessionAsync();
                await session.OpenAsync(page, Open(page.CaptureRequest), default);
                var reopened = runtime.Calls.Last(call => call.Method == "film.session.open").Input.GetProperty("target");
                Check(raw ? FilmJson.Text(reopened.GetProperty("source"), "candidateId") == "opaque-selected" :
                    FilmJson.Text(reopened, "kind") == "tv" && FilmJson.Integer(reopened, "season") == 2 &&
                        FilmJson.Integer(reopened, "episode") == 4,
                    "a retained page reopens its accepted target/opaque raw candidate, not its initial group closure");
            }
            finally { page.ReleaseSearch(); }
        }
    }

    private static async Task FailedUpdate()
    {
        using var runtime = new FilmSearchConsumerRuntime();
        var sources = new SearchSources(SearchFixture.Source("a")) { DataOwner = runtime.Connection,
            Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1, ("0", "目标", ""))) };
        await using var fixture = new SearchFixture(sources);
        var search = fixture.Service.Start("原词"); await SearchFixture.Settle(search);
        await using var session = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var errors = new List<string>();
        session.OperationFailed += (_, code) => errors.Add(code);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool first = true;
        runtime.Respond = async (method, input, _) =>
        {
            if (method == "film.session.command" && FilmJson.Text(input, "action") == "search.update" && first)
            {
                first = false; entered.TrySetResult(); await release.Task;
                return FilmSearchConsumerRuntime.Failure("UPSTREAM_ERROR");
            }
            return runtime.Handle(method, input);
        };
        var context = new FilmSearchContext(search, search.Items.Single());
        var page = FilmPlaybackPage.FromSearch(new("group", "目标", "", "") { SearchContext = context }, session, null);
        try
        {
            await session.OpenAsync(page, Open(page.CaptureRequest), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            search.Publish();
            long newest = search.Revision;
            release.TrySetResult();
            await SearchFixture.Until(() => Updates(runtime).Length == 2 && errors.Count == 1);
            var updates = Updates(runtime);
            Check(errors.Single() == "UPSTREAM_ERROR" &&
                updates[0].GetProperty("revision").GetInt64() < newest &&
                updates[1].GetProperty("revision").GetInt64() == newest &&
                runtime.Calls.Count(call => call.Method == "film.session.open") == 1 &&
                page.ActiveTarget.Kind == "group" && page.CanPlay,
                "the failed receipt is reported once, not retried or treated as empty, and the already queued latest context still publishes");
            Console.WriteLine($"OBSERVED film-search-wire failed-update errors={errors.Count} code={errors.Single()} updates={updates.Length} latestRevision={newest} opens=1");
        }
        finally { release.TrySetResult(); page.ReleaseSearch(); }
    }

    private static async Task GroupScope()
    {
        foreach (bool reconnect in new[] { false, true })
        {
            foreach (bool allRevoked in new[] { false, true })
            {
                foreach (bool keyOnly in new[] { false, true })
                {
                    using var original = new FilmSearchConsumerRuntime();
                    using var replacement = new FilmSearchConsumerRuntime();
                    var a = SearchFixture.Source("a").Wire;
                    var b = SearchFixture.Source("b").Wire;
                    var c = SearchFixture.Source("new-global").Wire;
                    string aKey = FilmJson.Text(a, "key"), bKey = FilmJson.Text(b, "key");
                    var members = Encode(new object[]
                    {
                        new { sourceKey = aKey, detailId = (object)"a-only" },
                        new { sourceKey = bKey, detailId = (object)0 },
                        new { sourceKey = bKey, detailId = (object)"0" }
                    });
                    var target = new FilmPlaybackTarget("original-group", "group", "目标",
                        Source: Encode(new { key = aKey, detailId = "a-only" }), Members: members);
                    var request = new FilmPlaybackRequest(target, keyOnly ? null : [a, b], "原词",
                        Results: Encode(new[]
                        {
                            new { sourceKey = aKey, items = new object[] { new { vod_id = (object)"a-only", vod_remarks = "A" } } },
                            new { sourceKey = bKey, items = new object[] { new { vod_id = (object)0, vod_remarks = " 原B数字 " },
                                new { vod_id = (object)"0", vod_remarks = "原B文本" } } }
                        })) { DataOwner = original.Connection };
                    FilmSearchConsumerRuntime current = reconnect ? original : replacement;
                    IReadOnlyList<JsonElement> available = reconnect ? [a, b] : allRevoked ? [c] : [b, c];
                    await using var session = new FilmPlaybackSession(
                        token => current.CaptureConnectionAsync(token), new FilmNativeProbe(),
                        prepareOpen: (page, input, _, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            return Task.FromResult(new FilmPlaybackOpen(current.Connection,
                                FilmPlaybackOpen.ParametersFor(page, input, current.Connection, available)));
                        });
                    var page = new FilmPlaybackPage(new("group", "目标", "", ""), request, session, null);
                    Task Opening()
                    {
                        if (!reconnect) return session.OpenAsync(page, Open(request), default);
                        return session.ReconnectAsync();
                    }
                    if (reconnect)
                    {
                        await session.OpenAsync(page, Open(request), default);
                        original.Revoked.Cancel();
                        current = replacement;
                        available = allRevoked ? [c] : [b, c];
                    }
                    if (allRevoked)
                    {
                        await FilmPlaybackFixture.Error("SOURCE_NOT_FOUND", Opening);
                        Check(!replacement.Calls.Any(call => call.Method == "film.session.open"),
                            "all original members revoked fails before Node open; a new global source is not a replacement");
                        Console.WriteLine($"OBSERVED film-search-wire group-scope reconnect={reconnect} keyOnly={keyOnly} allRevoked=true replacementOpens=0 error=SOURCE_NOT_FOUND");
                        continue;
                    }
                    await Opening();
                    var opened = replacement.Calls.Single(call => call.Method == "film.session.open").Input;
                    var admitted = FilmJson.Array(opened, "sources");
                    var accepted = FilmJson.Target(opened.GetProperty("target"));
                    var kept = FilmJson.Array(accepted.Members!.Value);
                    Check(admitted.Length == 1 && FilmJson.Text(admitted[0], "key") == bKey &&
                        accepted.Kind == "group" && accepted.Id == target.Id && accepted.Source is null &&
                        kept.Length == 2 && kept.All(member => FilmJson.Text(member, "sourceKey") == bKey) &&
                        kept[0].GetProperty("detailId").ValueKind == JsonValueKind.Number &&
                        kept[0].GetProperty("detailId").GetInt32() == 0 &&
                        kept[1].GetProperty("detailId").ValueKind == JsonValueKind.String &&
                        kept[1].GetProperty("detailId").GetString() == "0",
                        "first open and epoch reconnect both exclude A and new C without converting the group or typed B identities");
                    Check(FilmJson.Array(opened, "results").Length == 0 &&
                        FilmJson.Array(opened, "resultHints").Single().GetProperty("items").GetArrayLength() == 2 &&
                        FilmJson.Text(FilmJson.Array(opened, "resultHints").Single(), "sourceKey") == bKey &&
                        page.ActiveTarget.Kind == "group" &&
                        JsonElement.DeepEquals(page.CaptureRequest.Target.Members!.Value, accepted.Members!.Value) &&
                        page.CaptureRequest.Target.Source is null,
                        "cross-owner rows are only exact B hints and capture keeps the accepted legal snapshot, not removed A members");
                    Console.WriteLine($"OBSERVED film-search-wire group-scope reconnect={reconnect} keyOnly={keyOnly} allRevoked=false admitted=1 members={kept.Length} detailKinds={kept[0].GetProperty("detailId").ValueKind},{kept[1].GetProperty("detailId").ValueKind} captureGroup={page.ActiveTarget.Kind}");
                    var withdrawn = accepted with { Members = Encode(Array.Empty<object>()),
                        Source = Encode(new { key = bKey, detailId = 0, candidateId = "withdrawn-file" }) };
                    replacement.Publish(replacement.LastSessionId, withdrawn, Encode((object?)null!));
                    await SearchFixture.Until(() => page.ActiveTarget.Members!.Value.GetArrayLength() == 0);
                    Check(FilmJson.Text(page.CaptureRequest.Target.Source!.Value, "candidateId") == "withdrawn-file",
                        "a real revocation snapshot may retain the exact file selector after its last member disappears");
                    session.UpdateSearch(page, accepted, 1);
                    Check(Updates(replacement).Length == 0, "late search publication cannot revive a group with no admitted members");
                    await FilmPlaybackFixture.Error("SOURCE_NOT_FOUND", () =>
                    {
                        FilmPlaybackOpen.ParametersFor(page, Encode(Open(page.CaptureRequest)), replacement.Connection, [c]);
                        return Task.CompletedTask;
                    });
                }
            }
        }
    }

    private sealed class CapturingFacade(FilmSearchConsumerRuntime producer) : IRuntimeClient, IRuntimeConnectionSource
    {
        internal int Captures, Calls;
        internal FilmSearchConsumerRuntime Current = producer;
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
        { Captures++; return Current.CaptureConnectionAsync(cancellationToken); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Content must use the fixed producer, not the facade."); }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No uploads.");
    }
}
