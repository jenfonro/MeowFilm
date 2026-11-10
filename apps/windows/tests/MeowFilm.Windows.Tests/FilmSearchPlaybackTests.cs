using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchPlaybackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("Node handoff keeps typed members original query raw rows confirmed cursors and cache provenance", TypedHandoff);
        yield return Case("raw selection remains its exact single source and cannot inherit TMDB metadata", RawHandoff);
        yield return Case("late navigation updates carry only the original search reference and preserve active target on reopen", LiveUpdate);
        yield return Case("first open follows handoff while same-owner page reentry sends the exact resume target even after search retirement", Reentry);
        yield return Case("stale and failed update receipts cannot overwrite a replacement page", UpdateFailure);
        yield return Case("metadata uses one captured producer and original cache identity receipts", MetadataReceipt);
        yield return Case("invalid cache receipts and owner failures are never successful content", ReceiptFailures);
        yield return Case("revocation fences late content and late cache receipt", RevokedReceipt);
        yield return Case("ordinary waiter cancellation never revokes the runtime owner", CancelledWaiter);
        yield return Case("group open and reconnect retain only admitted original members with their typed identity", GroupScope);
    }
    private static TestCase Case(string name, Func<Task> action) => new("film-search-playback: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static void Check(bool condition, string reason) => SearchFixture.Check(condition, reason);
    private static JsonElement Encode(object value) => FilmJson.Encode(value);
    private static object Open(FilmPlaybackRequest request) => new
    {
        platform = "windows", target = request.Target.Wire(), sources = request.Sources, query = request.Query,
        results = request.Results, resultHints = request.ResultHints, search = request.Search, metadata = request.Metadata,
        cacheEntries = request.CacheEntries, searchReference = request.SearchReference
    };
    private static JsonElement[] Updates(FilmSearchConsumerRuntime runtime) => runtime.Calls
        .Where(call => call.Method == "film.session.command" && FilmJson.Text(call.Input, "action") == "search.update")
        .Select(call => call.Input).ToArray();

    private static async Task TypedHandoff()
    {
        using var runtime = new FilmSearchConsumerRuntime();
        var a = SearchFixture.Source("a"); var b = SearchFixture.Source("b", order: 1);
        await using var fixture = new SearchFixture(new(a, b), runtime);
        var members = new[] { SearchFixture.Member(a, 0, "原名 第一季"), SearchFixture.Member(a, "0", "原名 第一季"), SearchFixture.Member(b, 0, "原名 第二季") };
        runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"),
            [SearchFixture.Card("group:opaque", "site", "不同显示名", members)], members);
        var session = fixture.Service.Start("不同的原始词"); await SearchFixture.Settle(session);
        var target = new FilmPlaybackTarget("group:opaque", "group", "不同显示名", Members: Encode(new object[]
        {
            new { sourceKey = a.Key, detailId = (object)0 }, new { sourceKey = a.Key, detailId = (object)"0" }, new { sourceKey = b.Key, detailId = (object)0 }
        }));
        var results = Encode(new[] { new { sourceKey = a.Key, items = members.Take(2).Select(row => row.GetProperty("raw")).ToArray() },
            new { sourceKey = b.Key, items = members.Skip(2).Select(row => row.GetProperty("raw")).ToArray() } });
        var cursors = Encode(new[] { new { sourceKey = a.Key, page = 2, completed = false }, new { sourceKey = b.Key, page = 1, completed = true } });
        var entries = Encode(new[] { new { kind = "search", source = a.Wire, query = session.Query, page = 2,
            fetchedAt = 1111L, expiresAt = 2222L, completeness = "complete", value = members[0].GetProperty("raw") } });
        runtime.SearchHandoff = input => SearchFixture.Handoff(Guid.Parse(session.Id), FilmJson.Text(input, "selectionId"), session.Query,
            target, [a.Wire, b.Wire], results, cursors, cacheEntries: entries);
        var context = new FilmSearchContext(session, session.Items.Single());
        var request = await context.HandoffAsync(default);
        var handedOffMembers = request.Target.Members ?? throw new InvalidOperationException("Node group handoff is missing aggregate members.");
        Check(request.Target.Kind == "group" && handedOffMembers.GetArrayLength() == 3 && request.Query == session.Query &&
            JsonElement.DeepEquals(request.Results!.Value, results) && JsonElement.DeepEquals(request.Search!.Value, cursors) &&
            JsonElement.DeepEquals(request.CacheEntries!.Value, entries), "Node facts are copied losslessly without title inference or cursor advancement");
        Check(handedOffMembers[0].GetProperty("detailId").ValueKind == JsonValueKind.Number &&
            handedOffMembers[1].GetProperty("detailId").ValueKind == JsonValueKind.String &&
            ReferenceEquals(request.DataOwner!.Client, runtime), "typed zero identities and actual producer remain distinct");
        var handoff = runtime.Calls.Single(call => call.Method == "film.search.handoff").Input;
        Check(handoff.EnumerateObject().Select(field => field.Name).Order().SequenceEqual(new[] { "selectionId", "sessionId", "sources" }), "no local rules results or cache seeds are uploaded to handoff");
        await using var native = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var page = FilmPlaybackPage.FromSearch(new("card", "显示", "", "") { SearchContext = context }, request, native, null);
        try
        {
            await native.OpenAsync(page, Open(page.CaptureRequest), default);
            var opened = runtime.Calls.Single(call => call.Method == "film.session.open").Input;
            Check(JsonElement.DeepEquals(opened.GetProperty("searchReference"), request.SearchReference!.Value) && !opened.TryGetProperty("rules", out _), "playback receives Node reference without client rules");
        }
        finally { page.ReleaseSearch(); }
    }
    private static async Task RawHandoff()
    {
        await using var fixture = new SearchFixture();
        var session = fixture.Service.Start("原词"); await SearchFixture.Settle(session);
        var raw = SearchFixture.Member(fixture.Sources.Sources[0], 0, "原名");
        var target = new FilmPlaybackTarget("raw-id", "source", "原名", Source: Encode(new { key = fixture.Sources.Sources[0].Key, detailId = 0 }));
        fixture.Runtime.SearchHandoff = input => SearchFixture.Handoff(Guid.Parse(session.Id), FilmJson.Text(input, "selectionId"), session.Query,
            target, [fixture.Sources.Sources[0].Wire], results: Encode(new[] { new { sourceKey = fixture.Sources.Sources[0].Key, items = new[] { raw.GetProperty("raw") } } }));
        var request = await new FilmSearchContext(session, raw).HandoffAsync(default);
        Check(request.Target.Kind == "source" && request.Target.Source!.Value.GetProperty("detailId").ValueKind == JsonValueKind.Number &&
            request.Sources!.Count == 1 && request.Metadata is null && request.Target.Members is null &&
            FilmJson.Array(request.Results!.Value).Single().GetProperty("items").GetArrayLength() == 1,
            "raw Node receipt keeps only the exact zero-valued source and no TMDB/group association");
    }
    private static async Task LiveUpdate()
    {
        await using var fixture = new SearchFixture();
        var runtime = fixture.Runtime;
        var search = fixture.Service.Start("原词"); await SearchFixture.Settle(search);
        var context = new FilmSearchContext(search, search.Items[0]);
        var request = await context.HandoffAsync(default);
        await using var native = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var page = FilmPlaybackPage.FromSearch(new("card", "显示", "", "") { SearchContext = context }, request, native, null);
        try
        {
            await native.OpenAsync(page, Open(page.CaptureRequest), default);
            var initial = runtime.LastSessionId;
            var target = new FilmPlaybackTarget("tmdb:tv:7", "tv", "可靠元数据", "tmdb", "7");
            runtime.Respond = (method, input, token) =>
            {
                if (method != "film.session.command") return Task.FromResult(runtime.Handle(method, input));
                Check(FilmJson.Text(input, "action") == "search.update" &&
                    JsonElement.DeepEquals(input.GetProperty("searchReference"), request.SearchReference!.Value) &&
                    !input.TryGetProperty("metadataTarget", out _) && !input.TryGetProperty("results", out _) && !input.TryGetProperty("cacheEntries", out _),
                    "late association is re-read by Node from the original reference, never locally derived");
                return Task.FromResult(FilmSearchConsumerRuntime.Success(FilmPlaybackFixture.Snapshot(initial,
                    input.GetProperty("revision").GetInt64() + 2, target, Encode(new { name = "可靠元数据", overview = "完整迟到详情" }))));
            };
            runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(search.Id), search.Query, [], [], revision: 5));
            await SearchFixture.Until(() => page.ActiveTarget.Kind == "tv" && page.Overview == "完整迟到详情");
            Check(page.CaptureRequest.Target.MetadataProvider == "tmdb" && page.CaptureRequest.Target.MetadataId == "7" &&
                runtime.Calls.Count(call => call.Method == "film.session.open") == 1 && !runtime.Calls.Any(call => call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
                "late TMDB reaches the existing playback with no client content request");
            var scope = page.CaptureRequest;
            Check(scope.Target.Kind == "tv" && scope.Target.MetadataId == "7", "reentry retains accepted current identity rather than the original group");
            await native.DisposeAsync();
            int count = Updates(runtime).Length;
            runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(search.Id), search.Query, [], [], revision: 6)); await Task.Yield();
            Check(Updates(runtime).Length == count, "closed playback cannot dispatch a late update");
        }
        finally { page.ReleaseSearch(); }
    }
    private static async Task UpdateFailure()
    {
        await using var fixture = new SearchFixture();
        var runtime = fixture.Runtime;
        var search = fixture.Service.Start("原词"); await SearchFixture.Settle(search);
        var context = new FilmSearchContext(search, search.Items[0]);
        var request = await context.HandoffAsync(default);
        await using var native = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe());
        var first = FilmPlaybackPage.FromSearch(new("card", "显示", "", "") { SearchContext = context }, request, native, null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await native.OpenAsync(first, Open(request), default);
            await SearchFixture.Until(() => Updates(runtime).Length > 0);
            var errors = new List<string>(); native.OperationFailed += (_, error) => errors.Add(error);
            runtime.Respond = async (method, input, _) =>
            {
                if (method == "film.session.command") { entered.TrySetResult(); return await receipt.Task; }
                return runtime.Handle(method, input);
            };
            runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(search.Id), search.Query, [], [], revision: 4)); await entered.Task;
            receipt.SetResult(FilmSearchConsumerRuntime.Failure("UPSTREAM_ERROR"));
            await SearchFixture.Until(() => errors.Count == 1);
            Check(first.ActiveTarget.Id == request.Target.Id, "failed update remains observable and cannot fabricate association");
            runtime.Respond = null;
            var replacementRequest = new FilmPlaybackRequest(new("tmdb:movie:8", "movie", "新页", "tmdb", "8"));
            var replacement = new FilmPlaybackPage(new("new", "新页", "", ""), replacementRequest, native, null);
            await native.OpenAsync(replacement, Open(replacementRequest), default);
            runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(search.Id), search.Query, [], [], revision: 5)); await Task.Yield();
            Check(replacement.ActiveTarget.Id == "tmdb:movie:8", "old-page search cannot publish into a replacement owner");
        }
        finally { receipt.TrySetResult(FilmSearchConsumerRuntime.Failure("CLOSED")); first.ReleaseSearch(); }
    }
    private static async Task Reentry()
    {
        foreach (string kind in new[] { "source", "group", "tv" })
        {
            await using var f = new SearchFixture();
            var search = f.Service.Start("原始查询"); await SearchFixture.Settle(search);
            var context = new FilmSearchContext(search, search.Items[0]);
            var request = await context.HandoffAsync(default);
            await using var native = new FilmPlaybackSession(f.Runtime.CaptureConnectionAsync, new FilmNativeProbe());
            var page = FilmPlaybackPage.FromSearch(new("card", "显示标题不是身份", "", "") { SearchContext = context }, request, native, null);
            using var replacement = new FilmSearchConsumerRuntime();
            try
            {
                var first = FilmPlaybackOpen.ParametersFor(page, Encode(Open(request)), f.Runtime.Connection, request.Sources!);
                Check(!first.TryGetProperty("resumeTarget", out _) && first.TryGetProperty("searchReference", out _),
                    "the first open has no resume marker; its input target may still follow the latest Node handoff");
                await native.OpenAsync(page, Open(request), default);
                var selectedSource = Encode(new { key = request.Sources![0].GetProperty("key").GetString(),
                    detailId = kind == "group" ? (object)"same" : 0, lineId = "line-2", candidateId = "opaque-selected-file" });
                var target = kind switch
                {
                    "source" => new FilmPlaybackTarget("selected-source", "source", "同一显示", Source: selectedSource),
                    "group" => request.Target with { Title = "同一显示", Season = 3, Episode = 4, GlobalEpisode = 28, Source = selectedSource },
                    _ => new FilmPlaybackTarget("douban:tv:18", "tv", "同一显示", "douban", "18", Season: 2, Episode: 9, GlobalEpisode: 21,
                        Source: selectedSource)
                };
                f.Runtime.Publish(f.Runtime.LastSessionId, target, Encode(new { title = "已选择资料" }));
                await SearchFixture.Until(() => page.ActiveTarget.Source is { } selected &&
                    FilmJson.Text(selected, "candidateId") == "opaque-selected-file");
                var captured = page.CaptureRequest.Target;
                f.Service.InvalidateQuery("新查询");
                var reopened = FilmPlaybackOpen.ParametersFor(page, Encode(Open(request)), f.Runtime.Connection, request.Sources!);
                Check(JsonElement.DeepEquals(reopened.GetProperty("resumeTarget"), Encode(captured.Wire())) &&
                    JsonElement.DeepEquals(reopened.GetProperty("target"), Encode(captured.Wire())) &&
                    JsonElement.DeepEquals(reopened.GetProperty("searchReference"), request.SearchReference!.Value),
                    "retained same-owner pages keep their original rule reference and complete selected metadata/source/episode coordinates");
                var otherOwner = FilmPlaybackOpen.ParametersFor(page, Encode(Open(request)), replacement.Connection, request.Sources!);
                Check(!otherOwner.TryGetProperty("searchReference", out _) && !otherOwner.TryGetProperty("resumeTarget", out _) &&
                    JsonElement.DeepEquals(otherOwner.GetProperty("target"), Encode(captured.Wire())),
                    "owner replacement uses the exact captured target without a stale search reference or resume marker");
                await native.CloseSessionAsync();
                await native.OpenAsync(page, Open(page.CaptureRequest), default);
                var sent = f.Runtime.Calls.Last(call => call.Method == "film.session.open").Input;
                Check(JsonElement.DeepEquals(sent.GetProperty("resumeTarget"), reopened.GetProperty("resumeTarget")),
                    "the real page reopen forwards the marker and exact coordinates rather than only constructing an unused DTO");
            }
            finally { page.ReleaseSearch(); }
        }
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
                    session.UpdateSearch(page, 1);
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
