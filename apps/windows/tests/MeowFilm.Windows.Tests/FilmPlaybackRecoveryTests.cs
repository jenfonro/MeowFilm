using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackRecoveryTests
{
    private const string Query = "原始 查询 · 不按标题重建";
    private const long FetchedAt = 1_794_000_000_000, ExpiresAt = FetchedAt + 3_600_000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static TestCase Test(string name, Func<Task> run) =>
        new("film-recovery: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static string Key(string installation) => $"cat:{installation}:video";
    private static JsonElement Source(string installation, long generation = 1) => FilmJson.Encode(new
    {
        key = Key(installation), engine = "cat", installationId = installation, siteId = "video",
        generation, name = "来源 " + installation, enabled = true, searchable = true
    });
    private static JsonNode Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static JsonElement[] Rows(JsonElement value, string name) => FilmJson.Array(value, name);
    private static JsonElement[] SourceEntries(JsonElement value) => Rows(value, "cacheEntries")
        .Where(entry => FilmJson.Text(entry, "kind") != "metadata").ToArray();
    private static JsonElement MetadataEntry(JsonElement value) => Rows(value, "cacheEntries")
        .Single(entry => FilmJson.Text(entry, "kind") == "metadata");
    private static JsonElement Opened(Epoch epoch) => epoch.Calls.Single(call => call.Method == "film.session.open").Parameters;
    private static void Equal(JsonElement expected, JsonElement actual, string reason) =>
        Check(JsonElement.DeepEquals(expected, actual), reason);
    private static void OnlySources(JsonElement value, params string[] installations) =>
        Check(Rows(value, "sources").Select(row => FilmJson.Text(row, "installationId"))
            .Order(StringComparer.Ordinal).SequenceEqual(installations.Order(StringComparer.Ordinal)),
            "only the original allowed ready installation/site intersection is admitted");
    private static void Untrusted(JsonElement sent, FilmPlaybackRequest original)
    {
        Equal(original.Results!.Value, sent.GetProperty("resultHints"), "all original rows and remarks remain hints, not new-owner evidence");
        Check(Rows(sent, "results").Length == 0 && Rows(sent, "search").Length == 0 && SourceEntries(sent).Length == 0,
            "old results, page 3 completed and source seeds do not acquire authority from first capture or generation ABA");
        Equal(MetadataEntry(FilmJson.Encode(new { cacheEntries = original.CacheEntries })), MetadataEntry(sent),
            "metadata keeps the exact original timestamp, expiration, completeness and settings revision");
        Check(FilmJson.Text(sent, "query") == Query, "the original query is not replaced by the display title");
    }

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("explicit page reconnect drains Failed then opens the prepared ready owner with nonempty ABA context", async () =>
        {
            var drain = new Barrier(); var ready = new Barrier();
            await using var rig = new Rig();
            rig.ConfigureEpoch = (number, epoch) =>
            {
                if (number == 1) epoch.DisposalWait = drain;
                if (number == 2)
                {
                    epoch.StartingStatuses = 1;
                    int statuses = 0;
                    epoch.BeforeCat = async (method, token) =>
                    {
                        if (method == "cat.script.status" && Interlocked.Increment(ref statuses) == 2)
                            await ready.HoldAsync(token);
                    };
                }
            };
            try
            {
                await rig.StartAsync();
                var request = await rig.ProduceAsync(Source("a"), Source("b"));
                var original = FilmJson.Encode(request);
                await rig.OpenAsync(request);
                var first = rig.Epochs[0];
                await rig.Session.CommandAsync("select.episode", new { episode = new { season = 2, episode = 4, globalEpisode = 16 } });
                await Until(() => rig.Page.ActiveTarget.Season == 2 && rig.Page.ActiveTarget.Episode == 4);
                var currentMetadata = FilmJson.Encode(new { title = "当前第二季资料", seasons = new[] { new { season_number = 2, name = "第二季" } } });
                first.Film.Change(root => root["metadata"] = Node(currentMetadata));
                await Until(() => rig.Page.Title == "当前第二季资料");
                first.Crash();
                await Until(() => rig.Page.ConnectionFailed && rig.Host.CanRetry && !rig.Session.HasSession);
                var reconnect = rig.Page.ReconnectAsync();
                await drain.Entered.Task.WaitAsync(Wait);
                Check(rig.Epochs.Length == 1 && !reconnect.IsCompleted && first.Disposals == 1,
                    "explicit recovery cannot launch a replacement before the previous physical drain");
                drain.Open();
                await ready.Entered.Task.WaitAsync(Wait);
                var next = rig.Epochs[1];
                Check(!reconnect.IsCompleted && next.OpenCount == 0 && rig.Sources.Cats.All(cat => cat.State == "starting"),
                    "a starting generation is not an open-ready source snapshot");
                ready.Open(); await reconnect;
                var sent = Opened(next);
                OnlySources(sent, "a", "b"); Untrusted(sent, request);
                Check(Rows(sent, "sources").All(row => FilmJson.Integer(row, "generation") == 1) &&
                    Rows(Opened(first), "sources").All(row => FilmJson.Integer(row, "generation") == 1),
                    "the old and new process both really report generation 1");
                var target = sent.GetProperty("target");
                Check(FilmJson.Integer(target, "season") == 2 && FilmJson.Integer(target, "episode") == 4 &&
                    FilmJson.Integer(target, "globalEpisode") == 16 && FilmJson.Text(target, "id") == request.Target.Id,
                    "recovery preserves the current exact episode rather than the initial target coordinates");
                Equal(currentMetadata, sent.GetProperty("metadata"), "the current visible metadata survives recovery");
                var prepared = rig.Prepared.Last();
                var open = next.Calls.Single(call => call.Method == "film.session.open");
                Equal(prepared.Parameters, sent, "open sends exactly this prepared source snapshot and handoff");
                Check(ReferenceEquals(prepared.Connection.Client, next) && open.Token == prepared.Connection.Revoked &&
                    !prepared.Connection.Revoked.IsCancellationRequested && rig.Sources.Cats.All(cat => cat.CanDetect) &&
                    rig.Session.HasSession && !rig.Page.ConnectionFailed,
                    "session.open uses the actual connection returned by this production prepare, not the App facade or an old epoch");
                Check(rig.Epochs.Length == 2 && next.Calls.All(call =>
                    call.Method is "cat.script.status" or "cat.script.start" or "film.session.open"),
                    "recovery does not replay install/update or eagerly refetch source content");
                Equal(original, FilmJson.Encode(request), "recovery does not mutate the original source generations, rows, pages or seed times");
            }
            finally { drain.Open(); ready.Open(); }
        });

        yield return Test("same producer client retains rows pages and original seed provenance without serializing authority", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            var request = await rig.ProduceAsync(Source("a"));
            var owned = request.Own();
            Check(ReferenceEquals(owned.DataOwner, request.DataOwner) &&
                ReferenceEquals(request.DataOwner!.Client, rig.Epochs[0]) &&
                rig.Epochs[0].Calls.Any(call => call.Method == "film.data.search"),
                "only the actual successful producer binds the process-local connection before consumption");
            var json = FilmJson.Encode(owned);
            Check(!json.EnumerateObject().Any(property => property.Name.Equals("dataOwner", StringComparison.OrdinalIgnoreCase)),
                "navigation JSON cannot persist or invent RuntimeConnection authority");
            await rig.OpenAsync(owned);
            var sent = Opened(rig.Epochs[0]);
            Equal(request.Results!.Value, sent.GetProperty("results"), "same-owner results keep their original raw remarks");
            Equal(request.Search!.Value, sent.GetProperty("search"), "same-owner completed page 3 is not refetched or reset");
            Equal(request.CacheEntries!.Value, sent.GetProperty("cacheEntries"), "same-owner source and metadata records keep original times and partial status");
            Check(Rows(sent, "resultHints").Length == 0 && rig.Epochs[0].Calls.Count(call => call.Method == "film.data.search") == 1,
                "the consumer does not create a second search or downgrade valid producer evidence to hints");
        });

        foreach (string proof in new[] { "missing", "JSON roundtrip", "App facade instead of producer client" })
            yield return Test(proof + " never stamps old results with first consumer capture", async () =>
            {
                await using var rig = new Rig(); await rig.StartAsync();
                var produced = await rig.ProduceAsync(Source("a"));
                var request = proof switch
                {
                    "missing" => produced with { DataOwner = null },
                    "JSON roundtrip" => JsonSerializer.Deserialize<FilmPlaybackRequest>(FilmJson.Encode(produced).GetRawText(), FilmJson.Options)!,
                    _ => produced with { DataOwner = new(rig.Host, produced.DataOwner!.BusinessOrigin, produced.DataOwner!.Revoked) }
                };
                if (proof != "App facade instead of producer client") Check(request.DataOwner is null, "no owner proof is reconstructed from JSON or absence");
                var before = FilmJson.Encode(request);
                await rig.OpenAsync(request);
                Untrusted(Opened(rig.Epochs[0]), request);
                await rig.Session.CloseSessionAsync();
                await rig.Page.ReconnectAsync();
                Untrusted(rig.Epochs[0].Calls.Last(call => call.Method == "film.session.open").Parameters, request);
                Check(rig.Epochs.Length == 1, "a healthy explicit reopen does not restart the runtime");
                Equal(before, FilmJson.Encode(request), "even a second consumption cannot turn the original unproven handoff into a trusted request");
            });

        yield return Test("a changed source remains untrusted on subsequent reopen while its unchanged neighbor retains proof", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            var request = await rig.ProduceAsync(Source("a"), Source("b"));
            rig.Epochs[0].Generations["a"] = 2;
            await rig.OpenAsync(request);
            void CheckSplit(JsonElement sent)
            {
                OnlySources(sent, "a", "b");
                Equal(FilmJson.Encode(new[] { FilmJson.Array(request.Results!.Value).Single(row => FilmJson.Text(row, "sourceKey") == Key("b")) }),
                    sent.GetProperty("results"), "only B retains its original result evidence");
                Equal(FilmJson.Encode(new[] { FilmJson.Array(request.Results!.Value).Single(row => FilmJson.Text(row, "sourceKey") == Key("a")) }),
                    sent.GetProperty("resultHints"), "A remains an original-remark hint rather than relabeled evidence");
                Check(Rows(sent, "search").Length == 1 && FilmJson.Text(Rows(sent, "search")[0], "sourceKey") == Key("b") &&
                    FilmJson.Integer(Rows(sent, "search")[0], "page") == 3 && FilmJson.Bool(Rows(sent, "search")[0], "completed"),
                    "A's cursor is revoked without resetting B's completed page");
                Check(SourceEntries(sent).Length == 2 && SourceEntries(sent).All(entry =>
                    FilmJson.Text(entry.GetProperty("source"), "installationId") == "b"),
                    "only A's search/detail seeds lose authority");
                Equal(MetadataEntry(FilmJson.Encode(new { cacheEntries = request.CacheEntries })), MetadataEntry(sent),
                    "an unrelated source revision does not rewrite metadata freshness");
            }
            CheckSplit(Opened(rig.Epochs[0]));
            await rig.Session.CloseSessionAsync(); await rig.Page.ReconnectAsync();
            CheckSplit(rig.Epochs[0].Calls.Last(call => call.Method == "film.session.open").Parameters);
            Check(FilmJson.Integer(request.Sources![0], "generation") == 1,
                "preparing the initial open must not overwrite the original generation used by later trust checks");
        });

        yield return Test("replacement ready sources intersect the original allowed range without admitting a new site", async () =>
        {
            await using var rig = new Rig("a", "gone");
            rig.ConfigureEpoch = (number, epoch) =>
            {
                if (number == 2)
                {
                    epoch.Generations.TryRemove("gone", out _); epoch.Generations["new"] = 1;
                    epoch.SiteIds = ["video", "new-site"];
                }
            };
            await rig.StartAsync(); var request = await rig.ProduceAsync(Source("a"), Source("gone"));
            await rig.OpenAsync(request); rig.Epochs[0].Crash();
            await Until(() => rig.Page.ConnectionFailed);
            await rig.Page.ReconnectAsync();
            var sent = Opened(rig.Epochs[1]); OnlySources(sent, "a");
            Check(Rows(sent, "resultHints").Length == 1 && FilmJson.Text(Rows(sent, "resultHints")[0], "sourceKey") == Key("a") &&
                Rows(sent, "sources").All(row => FilmJson.Text(row, "siteId") == "video"),
                "neither removed results, a new installation nor a new site under the same installation widens the handoff");
        });

        yield return Test("explicit empty sources remains empty even with old rows and a replacement global source list", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            var request = await rig.ProduceAsync(Source("a"));
            request = request with { Sources = [] };
            await rig.OpenAsync(request); rig.Epochs[0].Crash();
            await Until(() => rig.Page.ConnectionFailed); await rig.Page.ReconnectAsync();
            foreach (var epoch in rig.Epochs)
            {
                var sent = Opened(epoch); OnlySources(sent);
                Check(Rows(sent, "results").Length == 0 && Rows(sent, "resultHints").Length == 0 &&
                    Rows(sent, "search").Length == 0 && SourceEntries(sent).Length == 0,
                    "explicit empty is not null and cannot acquire sources from stale rows or the global list");
                Equal(MetadataEntry(FilmJson.Encode(new { cacheEntries = request.CacheEntries })), MetadataEntry(sent),
                    "empty source scope does not discard independent metadata");
            }
        });

        yield return Test("initial null source scope without prior source facts freezes only its first admitted range", async () =>
        {
            await using var rig = new Rig("a");
            rig.ConfigureEpoch = (number, epoch) => { if (number == 2) epoch.Generations["new"] = 1; };
            await rig.StartAsync();
            await rig.OpenAsync(Card().PlaybackContext!);
            OnlySources(Opened(rig.Epochs[0]), "a");
            rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
            await rig.Page.ReconnectAsync(); OnlySources(Opened(rig.Epochs[1]), "a");
            Check(rig.Page.Request.Sources is null, "remembering first admission does not mutate the caller's null request");
        });

        foreach (bool proof in new[] { true, false })
            yield return Test("null scope with original source records preserves only that source " +
                (proof ? "with" : "without") + " producer authority", async () =>
            {
                await using var rig = new Rig();
                await rig.StartAsync();
                var produced = await rig.ProduceAsync(Source("a"));
                var request = produced with { Sources = null, DataOwner = proof ? produced.DataOwner : null };
                var before = FilmJson.Encode(request);
                await rig.OpenAsync(request);
                var initial = Opened(rig.Epochs[0]);
                OnlySources(initial, "a");
                if (proof)
                {
                    Equal(produced.Results!.Value, initial.GetProperty("results"), "original successful rows and raw remarks retain their actual producer");
                    Equal(produced.Search!.Value, initial.GetProperty("search"), "original acknowledged page does not restart");
                }
                else Untrusted(initial, produced);
                rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
                await rig.Page.ReconnectAsync();
                var reopened = Opened(rig.Epochs[1]);
                OnlySources(reopened, "a"); Untrusted(reopened, produced);
                Equal(before, FilmJson.Encode(request), "scope recovery cannot stamp a new generation on the caller's evidence");
            });

        foreach (bool raw in new[] { false, true })
            yield return Test("null scope with only an exact " + (raw ? "raw target" : "result key") +
                " identifies its source without proving the old generation", async () =>
            {
                await using var rig = new Rig();
                await rig.StartAsync();
                string key = JsonSerializer.Serialize(new[] { "a", "video" });
                var request = Card().PlaybackContext! with
                {
                    Query = Query,
                    DataOwner = await rig.Host.CaptureConnectionAsync(),
                    Results = FilmJson.Encode(new[] { new { sourceKey = key, items = new[]
                        { new { vod_id = 0, vod_name = "准确原始文件", vod_remarks = " 原备注 " } } } }),
                    Search = FilmJson.Encode(new[] { new { sourceKey = key, page = 3, completed = true } })
                };
                if (raw) request = request with { Target = new("raw:only-a", "source", "原始单源",
                    Source: FilmJson.Encode(new { key, detailId = 0, lineId = "0", playId = 0 })) };
                await rig.OpenAsync(request);
                var initial = Opened(rig.Epochs[0]); OnlySources(initial, "a");
                Equal(request.Results!.Value, initial.GetProperty("resultHints"), "a matching key alone preserves hints, never current-generation evidence");
                Check(Rows(initial, "results").Length == 0 && Rows(initial, "search").Length == 0,
                    "even a current client cannot prove the generation of key-only rows");
                rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
                await rig.Page.ReconnectAsync();
                var reopened = Opened(rig.Epochs[1]); OnlySources(reopened, "a");
                Equal(request.Results!.Value, reopened.GetProperty("resultHints"), "reopen retains the exact hints without widening the scope");
                if (raw) Equal(request.Target.Source!.Value, reopened.GetProperty("target").GetProperty("source"),
                    "raw source detail and selectors preserve their original JSON types");
            });

        yield return Test("unknown key-only null source scope fails instead of searching other ready sources", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            var request = Card().PlaybackContext! with
            {
                Target = new("raw:missing", "source", "准确旧源",
                    Source: FilmJson.Encode(new { key = JsonSerializer.Serialize(new[] { "gone", "video" }), detailId = 0 }))
            };
            await Error("SOURCE_NOT_FOUND", () => rig.OpenAsync(request));
            Check(rig.Epochs[0].OpenCount == 0, "an unresolved exact source cannot fall back to the global source list");
        });

        foreach (string fact in new[] { "results", "search", "source cache", "raw source", "group member" })
            yield return Test("null scope rejects " + fact + " without its original source key before any session open", async () =>
            {
                await using var rig = new Rig(); await rig.StartAsync();
                var request = Card().PlaybackContext!;
                request = fact switch
                {
                    "results" => request with
                    {
                        Results = FilmJson.Encode(new[] { new { items = new[] { new { vod_id = 0, vod_name = "真实原行" } } } })
                    },
                    "search" => request with
                    {
                        Search = FilmJson.Encode(new[] { new { page = 3, completed = true } })
                    },
                    "source cache" => request with
                    {
                        CacheEntries = FilmJson.Encode(new[] { new
                        {
                            kind = "detail", source = new { engine = "cat", installationId = "a", siteId = "video", generation = 1 },
                            detailId = 0, value = new { }, fetchedAt = FetchedAt, completeness = "complete"
                        } })
                    },
                    "raw source" => request with
                    {
                        Target = new("raw:no-key", "source", "准确单源", Source: FilmJson.Encode(new { detailId = 0 }))
                    },
                    _ => request with
                    {
                        Target = new("group:no-key", "group", "准确聚合",
                            Members: FilmJson.Encode(new[] { new { detailId = 0 } }))
                    }
                };
                await Error("INVALID_REQUEST", () => rig.OpenAsync(request));
                Check(rig.Epochs[0].OpenCount == 0,
                    "existing facts with missing authority cannot expand to every currently available source");
            });

        foreach (bool numeric in new[] { true, false })
            yield return Test("raw typed " + (numeric ? "numeric" : "text") + " zero selectors survive sanitized snapshots and remain single-source", async () =>
            {
                await using var rig = new Rig(); await rig.StartAsync();
                var request = await rig.ProduceAsync(Source("a"), Source("b"));
                var raw = FilmJson.Encode(new
                {
                    key = Key("a"), detailId = numeric ? (object)0 : "0", lineId = numeric ? (object)0 : "0",
                    episodeId = numeric ? (object)"0" : 0, playId = numeric ? (object)0 : "0"
                });
                request = request with { Target = new("source:exact", "source", "原始单源", Source: raw) };
                await rig.OpenAsync(request); OnlySources(Opened(rig.Epochs[0]), "a");
                var sanitized = FilmJson.Encode(new { key = Key("a"), detailId = raw.GetProperty("detailId"), lineId = raw.GetProperty("lineId") });
                rig.Epochs[0].Film.Change(root => root["target"]!["source"] = Node(sanitized));
                await Until(() => rig.Page.ActiveTarget.Source is { } source && !source.TryGetProperty("playId", out _));
                rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
                await rig.Page.ReconnectAsync();
                var sent = Opened(rig.Epochs[1]); OnlySources(sent, "a");
                Equal(raw, sent.GetProperty("target").GetProperty("source"),
                    "without an opaque selected candidate, omitted private play/episode IDs retain their original exact types");
                Check(Rows(sent, "resultHints").All(row => FilmJson.Text(row, "sourceKey") == Key("a")) &&
                    Rows(sent, "results").Length == 0,
                    "raw recovery cannot borrow B's result rows or become a title-wide aggregate");
            });

        yield return Test("a missing exact raw source fails SOURCE_NOT_FOUND before any new session open", async () =>
        {
            await using var rig = new Rig();
            rig.ConfigureEpoch = (number, epoch) => { if (number == 2) epoch.Generations.TryRemove("a", out _); };
            await rig.StartAsync(); var request = await rig.ProduceAsync(Source("a"), Source("b"));
            request = request with { Target = new("source:exact", "source", "原始单源", Source: FilmJson.Encode(new
                { key = Key("a"), detailId = 0, lineId = "0", episodeId = 0, playId = "0" })) };
            await rig.OpenAsync(request); rig.Epochs[0].Crash();
            await Until(() => rig.Page.ConnectionFailed);
            await Error("SOURCE_NOT_FOUND", () => rig.Page.ReconnectAsync());
            Check(rig.Epochs[1].OpenCount == 0 && !rig.Session.HasSession && rig.Page.ConnectionFailed &&
                rig.Sources.Cats.Single().InstallationId == "b",
                "a healthy different source is not a fallback for the missing raw owner or an empty successful session");
        });

        yield return Test("snapshot discoveries append deduplicated typed hints without overwriting original raw remarks", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            var request = await rig.ProduceAsync(Source("a"));
            await rig.OpenAsync(request);
            object Row(string id, string installation, object detail, string title) => new
            {
                id, title, source = new { key = Key(installation), engine = "cat", installationId = installation,
                    siteId = "video", generation = 1, detailId = detail }
            };
            rig.Epochs[0].Film.Change(root => root["sources"] = JsonSerializer.SerializeToNode(new[]
            {
                Row("old-zero", "a", 0, "不应覆盖原备注"),
                Row("new-text-zero", "a", "0", "新发现字符串零"),
                Row("duplicate-text-zero", "a", "0", "不应重复"),
                Row("outside", "b", "outside", "不得扩大来源")
            }, FilmJson.Options));
            await Until(() => rig.Page.Sources.Any(row => row.Id == "new-text-zero"));
            for (int reconnect = 0; reconnect < 2; reconnect++)
            {
                rig.Epochs[^1].Crash(); await Until(() => rig.Page.ConnectionFailed);
                await rig.Page.ReconnectAsync();
                var sent = Opened(rig.Epochs[^1]); OnlySources(sent, "a");
                var expected = FilmJson.Encode(FilmJson.Array(request.Results!.Value).Concat(new[]
                {
                    FilmJson.Encode(new { sourceKey = Key("a"), items = new[] { new { id = "0", title = "新发现字符串零" } } })
                }).ToArray());
                Equal(expected, sent.GetProperty("resultHints"),
                    "numeric 0 keeps its original raw record; string 0 is one distinct persistent hint, with no out-of-scope row");
                Check(Rows(sent, "results").Length == 0 && Rows(sent, "search").Length == 0,
                    "remembered discovery pointers are not newly completed search pages");
            }
        });

        yield return Test("same epoch observer reconnect only snapshots and preserves one actual native output", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            await rig.OpenAsync(await rig.ProduceAsync(Source("a")));
            var epoch = rig.Epochs[0];
            var candidate = Node(Candidate("observer")).AsObject();
            var source = Node(Source("a")).AsObject(); source["detailId"] = 0; candidate["source"] = source;
            epoch.Film.Attempt("observer", JsonSerializer.SerializeToElement(candidate));
            await Until(() => rig.Native.Commits == 1 && rig.Session.HasOutput && epoch.Calls.Any(call =>
                call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == "media.started"));
            int prepares = rig.Prepared.Count, captures = rig.Captures.Count;
            int cats = epoch.Calls.Count(call => call.Method.StartsWith("cat.script.", StringComparison.Ordinal));
            int attempts = epoch.Calls.Count(call => call.Method == "film.session.attempt");
            epoch.Film.DisconnectEvents(); await Until(() => rig.Page.ConnectionFailed);
            await rig.Page.ReconnectAsync();
            Check(!rig.Page.ConnectionFailed && rig.Session.HasSession && rig.Session.HasOutput &&
                rig.Native.Commits == 1 && rig.Native.Identity == "observer" && rig.Epochs.Length == 1 &&
                rig.Prepared.Count == prepares && rig.Captures.Count == captures,
                "observer recovery does not recover the App host, prepare a new epoch or replay actual playback");
            Check(epoch.OpenCount == 1 && epoch.Calls.Count(call => call.Method == "film.session.snapshot") == 1 &&
                epoch.Calls.Count(call => call.Method == "film.session.attempt") == attempts &&
                epoch.Calls.Count(call => call.Method.StartsWith("cat.script.", StringComparison.Ordinal)) == cats &&
                epoch.Subscriptions == 2,
                "only snapshot and a new subscription occur, without Cat restart/status, source requests or a second media receipt");
        });

        yield return Test("ordinary failed capture never retries until the page explicitly reconnects", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            var request = await rig.ProduceAsync(Source("a"));
            rig.Epochs[0].Crash();
            await Error("CLOSED", () => rig.OpenAsync(request));
            Check(rig.Host.CanRetry && rig.Epochs.Length == 1 && rig.Epochs[0].OpenCount == 0 && rig.Page.ConnectionFailed,
                "ordinary navigation/capture does not silently restart a failed App runtime");
            await rig.Page.ReconnectAsync();
            Check(rig.Epochs.Length == 2 && rig.Epochs[1].OpenCount == 1 && rig.Session.HasSession,
                "the same visible page can explicitly recover an initial capture failure");
            Untrusted(Opened(rig.Epochs[1]), request);
        });

        yield return Test("close while the production prepare callback is pending drains it without admitting its late open", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            await rig.OpenAsync(await rig.ProduceAsync(Source("a")));
            var prepared = new Barrier();
            rig.AfterPrepare = (_, reconnect) => reconnect ? prepared.HoldAsync() : Task.CompletedTask;
            try
            {
                rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
                var reconnect = rig.Page.ReconnectAsync(); await prepared.Entered.Task.WaitAsync(Wait);
                var close = rig.Session.CloseSessionAsync();
                Check(!close.IsCompleted && rig.Epochs[1].OpenCount == 0,
                    "close revokes the prepared operation but still waits for its physical completion");
                prepared.Open(); await Task.WhenAll(reconnect, close);
                Check(!rig.Session.HasSession && rig.Epochs[1].OpenCount == 0,
                    "a late prepared connection cannot reopen a closed page");
            }
            finally { prepared.Open(); }
        });

        yield return Test("a second explicit reconnect supersedes the first preparation and alone admits one new open", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            await rig.OpenAsync(await rig.ProduceAsync(Source("a")));
            var first = new Barrier(); int preparations = 0;
            rig.AfterPrepare = async (_, reconnect) =>
            {
                if (!reconnect) return;
                if (++preparations == 1) await first.HoldAsync();
                else Check(rig.Epochs[1].OpenCount == 0, "the superseded preparation must not open before the latest preparation finishes");
            };
            try
            {
                rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
                var superseded = rig.Page.ReconnectAsync(); await first.Entered.Task.WaitAsync(Wait);
                var latest = rig.Page.ReconnectAsync();
                Check(!latest.IsCompleted && rig.Epochs[1].OpenCount == 0, "the latest operation waits for the retired preparation, not its old open");
                first.Open(); await Task.WhenAll(superseded, latest);
                Check(preparations == 2 && rig.Epochs.Length == 2 && rig.Epochs[1].OpenCount == 1 && rig.Session.HasSession,
                    "two user requests share the recovered host but only the current session generation opens");
            }
            finally { first.Open(); }
        });

        yield return Test("an owner replacement between production captures rejects the prepared source snapshot", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            await rig.OpenAsync(await rig.ProduceAsync(Source("a")));
            rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
            int captures = 0;
            rig.BeforeCapture = async () =>
            {
                if (++captures != 2) return;
                rig.Epochs[1].Crash();
                await rig.Host.RetryAsync();
            };
            await Error("STALE_SESSION", () => rig.Page.ReconnectAsync());
            Check(rig.Epochs.Length == 3 && rig.Epochs.Skip(1).All(epoch => epoch.OpenCount == 0) && !rig.Session.HasSession,
                "the source snapshot of epoch 2 cannot open on epoch 3 or resurrect epoch 2");
            rig.BeforeCapture = null;
            await rig.Page.ReconnectAsync();
            Check(rig.Epochs[1].OpenCount == 0 && rig.Epochs[2].OpenCount == 1 && rig.Session.HasSession,
                "only a new explicit preparation can use the replacement owner");
        });

        yield return Test("an owner revoked after preparation cannot open even when another owner is already ready", async () =>
        {
            await using var rig = new Rig(); await rig.StartAsync();
            await rig.OpenAsync(await rig.ProduceAsync(Source("a")));
            var prepared = new Barrier();
            rig.AfterPrepare = (_, reconnect) => reconnect ? prepared.HoldAsync() : Task.CompletedTask;
            try
            {
                rig.Epochs[0].Crash(); await Until(() => rig.Page.ConnectionFailed);
                var reconnect = rig.Page.ReconnectAsync(); await prepared.Entered.Task.WaitAsync(Wait);
                var revoked = rig.Prepared.Last().Connection.Revoked;
                rig.Epochs[1].Crash(); await rig.Host.RetryAsync();
                Check(rig.Epochs.Length == 3 && rig.Host.State == RuntimeConnectionState.Ready && revoked.IsCancellationRequested,
                    "a real replacement owner is ready while the older prepared capability is revoked");
                prepared.Open();
                try
                {
                    await reconnect;
                    throw new InvalidOperationException("Expected the revoked prepared capability to cancel.");
                }
                catch (OperationCanceledException error)
                {
                    Check(error.CancellationToken == revoked, "the failure belongs to the old captured owner, not an invented caller cancellation");
                }
                Check(rig.Epochs.Skip(1).All(epoch => epoch.OpenCount == 0) && !rig.Session.HasSession,
                    "an already-returned prepare cannot open the old owner or automatically redirect to the replacement");
            }
            finally { prepared.Open(); }
        });
    }

    private sealed class Barrier
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task HoldAsync(CancellationToken token = default)
        {
            Entered.TrySetResult();
            await released.Task.WaitAsync(token);
        }
        internal void Open() => released.TrySetResult();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Epoch> epochs = new();
        internal readonly AppRuntimeConnectionHost Host;
        internal readonly FilmSourceService Sources;
        internal readonly FilmNativeProbe Native = new();
        internal readonly FilmPlaybackSession Session;
        internal readonly ConcurrentQueue<RuntimeConnection> Captures = new();
        internal readonly ConcurrentQueue<FilmPlaybackOpen> Prepared = new();
        internal Action<int, Epoch>? ConfigureEpoch { get; set; }
        internal Func<Task>? BeforeCapture { get; set; }
        internal Func<FilmPlaybackOpen, bool, Task>? AfterPrepare { get; set; }
        internal FilmPlaybackPage Page { get; private set; } = null!;
        internal Epoch[] Epochs => epochs.ToArray();

        internal Rig(params string[] installations)
        {
            if (installations.Length == 0) installations = ["a", "b"];
            Host = new(owner =>
            {
                var epoch = new Epoch(owner, installations);
                ConfigureEpoch?.Invoke(epochs.Count + 1, epoch);
                epochs.Enqueue(epoch);
                return Task.FromResult<IRuntimeSession>(epoch);
            });
            Sources = new(Host, new FilmMemoryStore());
            Session = new(CaptureAsync, Native, prepareOpen: async (page, parameters, reconnect, token) =>
            {
                var result = await FilmCapability.PreparePlaybackOpenAsync(Sources, CaptureAsync, page, parameters, reconnect, token);
                Prepared.Enqueue(result);
                if (AfterPrepare is { } after) await after(result, reconnect);
                return result;
            });
        }
        private async Task<RuntimeConnection> CaptureAsync(CancellationToken token)
        {
            if (BeforeCapture is { } before) await before();
            var connection = await Host.CaptureConnectionAsync(token);
            Captures.Enqueue(connection);
            return connection;
        }
        internal async Task StartAsync()
        {
            Host.Start();
            await Sources.StartAsync();
        }
        internal async Task<FilmPlaybackRequest> ProduceAsync(params JsonElement[] sources)
        {
            // Authority is bound here, at the successful original producer, never by Open/prepare.
            var producer = await Host.CaptureConnectionAsync();
            var client = new FilmPlaybackClient(producer.Client);
            var results = new List<object>(); var searches = new List<object>(); var entries = new List<JsonElement>();
            foreach (var source in sources)
            {
                var page = await client.CallAsync("data.search", new { source, query = Query, page = 3 });
                results.Add(new { sourceKey = FilmJson.Text(source, "key"), items = page.GetProperty("items") });
                searches.Add(new { sourceKey = FilmJson.Text(source, "key"), page = page.GetProperty("page"), completed = !FilmJson.Bool(page, "hasMore") });
                entries.Add(await client.CallAsync("data.cache.entry", new { kind = "search", source, query = Query, page = 3 }));
                entries.Add(await client.CallAsync("data.cache.entry", new { kind = "detail", source, detailId = 0 }));
            }
            var metadata = await client.CallAsync("data.metadata", new { provider = "tmdb", path = "/3/tv/100" });
            entries.Add(await client.CallAsync("data.cache.entry", new { kind = "metadata", provider = "tmdb", path = "/3/tv/100" }));
            return new(Card().PlaybackContext!.Target, Sources: sources, Query: Query, Results: FilmJson.Encode(results),
                Search: FilmJson.Encode(searches), Metadata: metadata, CacheEntries: FilmJson.Encode(entries)) { DataOwner = producer };
        }
        internal async Task OpenAsync(FilmPlaybackRequest request)
        {
            request = request.Own();
            Page = new(Card() with { PlaybackContext = request }, request, Session, null);
            await Session.OpenAsync(Page, new
            {
                platform = "windows", target = request.Target.Wire(), sources = request.Sources, query = request.Query,
                results = request.Results, search = request.Search, metadata = request.Metadata, cacheEntries = request.CacheEntries
            }, default);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Session.DisposeAsync(); }
            finally
            {
                try { await Sources.DisposeAsync(); }
                finally { await Host.DisposeAsync(); }
            }
        }
    }

    private sealed record Call(string Method, JsonElement Parameters, CancellationToken Token);

    // Offline protocol peer only. Filtering, owner trust, hints and recovery are all production code.
    private sealed class Epoch : IRuntimeSession, IRuntimeConnectionSource
    {
        private readonly CancellationTokenSource lifetime;
        private readonly ConcurrentQueue<Call> calls = new();
        private Task? disposal;
        private int startingStatuses, subscriptions, disposals;
        internal readonly FilmRuntimeProbe Film = new();
        internal readonly ConcurrentDictionary<string, long> Generations = new(StringComparer.Ordinal);
        internal readonly CancellationToken Revoked;
        internal Barrier? DisposalWait { get; set; }
        internal Func<string, CancellationToken, Task>? BeforeCat { get; set; }
        internal string[] SiteIds { get; set; } = ["video"];
        internal int StartingStatuses { set => startingStatuses = value; }
        internal Call[] Calls => calls.ToArray();
        internal int OpenCount => Calls.Count(call => call.Method == "film.session.open");
        internal int Subscriptions => Volatile.Read(ref subscriptions);
        internal int Disposals => Volatile.Read(ref disposals);

        internal Epoch(CancellationToken owner, string[] installations)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
            Revoked = lifetime.Token;
            foreach (string installation in installations) Generations[installation] = 1;
        }
        public bool IsConnected => !Revoked.IsCancellationRequested;
        internal void Crash() => lifetime.Cancel();
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new BridgeException("CLOSED");
            // Deliberately reuse the same port across epochs: neither port nor generation proves ownership.
            return Task.FromResult(new RuntimeConnection(this, new("http://127.0.0.1:41002/"), Revoked));
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new BridgeException("CLOSED");
            var input = parameters is JsonElement json ? json.Clone() : FilmJson.Encode(parameters ?? new { });
            calls.Enqueue(new(method, input, cancellationToken));
            if (method is "cat.script.start" or "cat.script.status")
            {
                Check(input.ValueKind == JsonValueKind.Object && !input.EnumerateObject().Any(), "Cat start/status has its exact empty request");
                if (BeforeCat is { } before) await before(method, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bool starting = method == "cat.script.status" && Interlocked.Decrement(ref startingStatuses) >= 0;
                var instances = Generations.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
                {
                    installationId = pair.Key, name = "猫 " + pair.Key, sourceType = "local", sourceUrl = (string?)null,
                    enabled = true, importedAt = 100L, updatedAt = (long?)null, updateState = "idle", updateError = (string?)null,
                    generation = pair.Value, state = starting ? "starting" : "ready", sourceHash = "synthetic-" + pair.Key,
                    error = (string?)null, sites = SiteIds.Select(site => new
                    {
                        key = site, name = "来源 " + pair.Key + " " + site, api = "/spider/synthetic",
                        actions = new[] { "home", "category", "detail", "play", "search" },
                        methods = new { home = "GET", category = "POST", detail = "POST", play = "POST", search = "POST" }
                    }).ToArray()
                }).ToArray();
                return FilmJson.Encode(new { ok = true, contract = "cat.script.v2", value = new { instances } });
            }
            if (method == "film.data.search") return FilmRuntimeProbe.Ok(FilmJson.Encode(new
                { items = new[] { OriginalItem(input.GetProperty("source")) }, page = 3, hasMore = false }));
            if (method == "film.data.metadata") return FilmRuntimeProbe.Ok(Metadata(Card().PlaybackContext!.Target, ""));
            if (method == "film.data.cache.entry") return FilmRuntimeProbe.Ok(Seed(input));
            Check(method.StartsWith("film.", StringComparison.Ordinal), "no unrequested runtime protocol is used");
            return await Film.CallAsync(method, input, cancellationToken);
        }
        private static JsonElement OriginalItem(JsonElement source) => FilmJson.Encode(new
        {
            vod_id = 0, vod_name = "原始片名 " + FilmJson.Text(source, "installationId"),
            vod_remarks = "原备注 · 4K / DDP，不能被新标题覆盖", original_path = "/原目录/原始文件名.mkv"
        });
        private static JsonElement Seed(JsonElement input)
        {
            string kind = FilmJson.RequiredText(input, "kind");
            if (kind == "metadata") return FilmJson.Encode(new
            {
                kind, provider = "tmdb", path = "/3/tv/100", value = Metadata(Card().PlaybackContext!.Target, ""),
                completeness = "complete", fetchedAt = FetchedAt, expiresAt = ExpiresAt,
                settings = new { revision = "opaque-original-revision", language = "zh-CN" }
            });
            var source = input.GetProperty("source");
            if (kind == "search") return FilmJson.Encode(new
            {
                kind, source, query = Query, page = 3, value = new { list = new[] { OriginalItem(source) }, pagecount = 3 },
                completeness = "complete", fetchedAt = FetchedAt, expiresAt = ExpiresAt
            });
            Check(kind == "detail", "only requested search/detail/metadata seeds exist");
            return FilmJson.Encode(new
            {
                kind, source, detailId = 0, value = new { list = new[] { OriginalItem(source) } },
                completeness = "partial", fetchedAt = FetchedAt - 1000, expiresAt = ExpiresAt - 1000
            });
        }
        public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Check(module == "film", "the observer uses the Film module");
            Interlocked.Increment(ref subscriptions);
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Revoked);
            await foreach (var value in Film.EventsAsync(module, sessionId, after, owned.Token)) yield return value;
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Recovery must not upload or replay source management.");
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Image requests are not part of this fixture.");
        public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Image requests are not part of this fixture.");
        public ValueTask DisposeAsync() => new(disposal ??= CloseAsync());
        private async Task CloseAsync()
        {
            Interlocked.Increment(ref disposals);
            lifetime.Cancel();
            if (DisposalWait is { } wait) await wait.HoldAsync();
            await Film.DisposeAsync();
            lifetime.Dispose();
        }
    }
}
