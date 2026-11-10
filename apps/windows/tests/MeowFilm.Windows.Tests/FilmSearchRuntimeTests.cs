using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>Actual search service, SSE, Cat Worker, shared rule program and Node handoff; synthetic sources only.</summary>
public static class FilmSearchRuntimeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-search-runtime: real Node full pages typed identities chosen covers more resume and handoff reach the Windows consumer",
            () => MusicQualityConsumerTests.Sta(Search));
        yield return new("film-tmdb-runtime: real App adapter search detail artwork and restart preserve stable identities",
            () => MusicQualityConsumerTests.Sta(FilmMetadataRuntimeTests.SearchArtwork));
        yield return new("film-tmdb-runtime: source failures stay errors and failed detail preserves complete original search metadata",
            () => MusicQualityConsumerTests.Sta(FilmMetadataRuntimeTests.Failures));
    }
    private static void Check(bool value, string reason) => SearchFixture.Check(value, reason);
    private static async Task Search()
    {
        string root = TestWorkspace.DataPath("film-search-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        await using var metadata = new RuntimeHttpClientTests.Server();
        await using var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root);
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = metadata.BaseUrl });
        await using var documents = new LocalDocumentStore(Path.Combine(root, "documents"), [FilmSourceService.DocumentNamespace]);
        await using var sources = new FilmSourceService(host.Client, documents);
        await sources.StartAsync();
        await sources.ImportLocalAsync("搜索测试猫", Script, "search-runtime");
        await sources.SaveVideoOptionsAsync(new("search-runtime", "a"), new(true, false, true, false), order: 2);
        await sources.SaveVideoOptionsAsync(new("search-runtime", "b"), new(true, false, true, true), order: 17);
        await using var rules = new FilmRuleService(host.Client);
        await rules.SaveAsync(new FilmRuleSet { AggregateRegexRules = ["\\[标记\\]"] });
        string rulesRevision = rules.Revision;
        await using var search = new FilmSearchService(host.Client.CaptureConnectionAsync, sources, host.Metadata.ResolveImage);
        var metadataRequests = new List<string>();
        var session = search.Start("银河");
        await using (var exchange = await metadata.AcceptAsync())
        {
            metadataRequests.Add(exchange.Path);
            Check(exchange.Path.StartsWith("/3/search/multi?", StringComparison.Ordinal), "only the existing TMDB search channel is contacted");
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new { error = "synthetic metadata unavailable" }), 503);
        }
        await SearchFixture.Settle(session);
        Check(session.RawItems.Count == 153 && session.MetadataState == FilmSearchChannelState.Error &&
            FilmJson.Text(session.Snapshot, "rulesRevision") == rulesRevision && session.CanLoadMore,
            "complete first pages and the captured real rule revision reach Windows while a metadata failure stays distinct");
        var group = session.Items.Single(card => FilmJson.Array(card, "aggregateItems").Length == 3);
        var members = FilmJson.Array(group, "aggregateItems");
        Check(members.Count(member => FilmJson.Text(member, "siteId") == "a") == 2 &&
            members.Any(member => member.GetProperty("detailId").ValueKind == JsonValueKind.Number && member.GetProperty("detailId").GetInt32() == 0) &&
            members.Any(member => member.GetProperty("detailId").ValueKind == JsonValueKind.String && member.GetProperty("detailId").GetString() == "0") &&
            FilmJson.Text(group, "poster") == "https://fixture.invalid/b.jpg",
            "same-source typed zero members and the existing B cover choice survive Node aggregation");
        var raw = session.RawItems.Single(card => FilmJson.Text(card, "siteId") == "a" && card.GetProperty("detailId").ValueKind == JsonValueKind.Number);
        Check(raw.GetProperty("raw").GetProperty("vod_name").GetString() == "[标记]银河" &&
            raw.GetProperty("raw").GetProperty("unknown").GetString() == "  原始字段  " &&
            raw.GetProperty("raw").GetProperty("vod_remarks").GetString() == "  原备注  ",
            "derived display never replaces the original Cat fields or typed identity");
        var context = new FilmSearchContext(session, group);
        var request = await context.HandoffAsync(default);
        Check(request.Target.Kind == "group" && request.Target.Members!.Value.GetArrayLength() == 3 &&
            request.SearchReference!.Value.GetProperty("selectionId").GetString() == context.DisplayKey &&
            request.Query == "银河" && request.DataOwner is not null,
            "the native handoff consumes the actual Node target reference and full members without local rules or seed synthesis");
        var rawRequest = await new FilmSearchContext(session, raw).HandoffAsync(default);
        Check(rawRequest.Target.Kind == "source" && rawRequest.Sources!.Count == 1 &&
            rawRequest.Target.Source!.Value.GetProperty("detailId").ValueKind == JsonValueKind.Number && rawRequest.Metadata is null,
            "the actual raw handoff stays the original single source");
        string[] order = session.Items.Select(card => FilmJson.Text(card, "displayKey")).ToArray();
        Check(!metadata.HasConnection, "aggregate and raw handoffs do not implicitly retry the failed TMDB search");
        await search.LoadMoreAsync();
        await using (var exchange = await metadata.AcceptAsync())
        {
            metadataRequests.Add(exchange.Path);
            var query = new Uri("http://fixture.invalid" + exchange.Path).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1].Replace("+", " ", StringComparison.Ordinal) : ""));
            Check(metadataRequests.Count == 2 && exchange.Path == metadataRequests[0] &&
                query.GetValueOrDefault("query") == "银河" && query.GetValueOrDefault("page") == "1" &&
                query.GetValueOrDefault("language") == "zh-CN",
                "one initial request and one explicit more retry use the exact original TMDB path and query without advancing its page");
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new { page = 1, total_pages = 1, results = Array.Empty<object>() }), 200);
        }
        await SearchFixture.Until(() => !session.Loading && session.Round == 2);
        Check(session.MetadataState == FilmSearchChannelState.Completed, "the explicit TMDB retry completes successfully with an empty result");
        Check(session.RawItems.Count == 154 && !session.HasMore &&
            session.Items.Take(order.Length).Select(card => FilmJson.Text(card, "displayKey")).SequenceEqual(order),
            "a real more command consumes one new complete page without repositioning old cards");
        Check((await context.HandoffAsync(default)).Target.Members!.Value.GetArrayLength() == 4,
            "the original opaque selection reads all newly loaded Node members");
        await search.StopAsync(); search.Start(session.Query); await session.Work;
        await SearchFixture.Until(() => !session.Loading);
        string audit = Path.Combine(root, "script-runtime", "volumes", "search-runtime", "search-audit.jsonl");
        var calls = File.ReadAllLines(audit).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Check(calls.Length == 3 && calls.All(call => FilmJson.Text(call, "query") == "银河") &&
            calls.Count(call => FilmJson.Text(call, "site") == "a" && call.GetProperty("page").GetInt32() == 1) == 1 &&
            calls.Count(call => FilmJson.Text(call, "site") == "a" && call.GetProperty("page").GetInt32() == 2) == 1 &&
            calls.Count(call => FilmJson.Text(call, "site") == "b" && call.GetProperty("page").GetInt32() == 1) == 1,
            "handoff, raw/aggregate consumption, stop and resume never repeat successful source pages");
        Check(!metadata.HasConnection, "after the successful TMDB retry, handoff, stop and same-word resume make no further metadata requests");
    }

    private const string Script = """
        const http = require('http');
        const fs = require('fs');
        let server;
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer(async (request, response) => {
            const url = new URL(request.url, 'http://127.0.0.1');
            response.setHeader('content-type', 'application/json');
            if (url.pathname === '/config' || url.pathname === '/full-config') {
              response.end(JSON.stringify({ video: { sites: ['a', 'b'].map(key => ({
                key, name: key, api: '/search/' + key, actions: ['search']
              })) } }));
              return;
            }
            const match = /^\/search\/([ab])\/search$/.exec(url.pathname);
            if (!match) { response.statusCode = 404; response.end('{}'); return; }
            const chunks = [];
            for await (const chunk of request) chunks.push(Buffer.from(chunk));
            const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
            const site = match[1], page = Number(payload.page);
            fs.appendFileSync('/search-audit.jsonl', JSON.stringify({ site, page, query: payload.wd }) + '\n');
            const row = (id, title) => ({ vod_id: id, vod_name: title, vod_remarks: '  原备注  ',
              vod_pic: 'https://fixture.invalid/' + site + '.jpg', unknown: '  原始字段  ' });
            const list = site === 'b' ? [row('b-only', '[标记]银河')]
              : page === 1 ? [row(0, '[标记]银河'), row('0', '[标记]银河'),
                ...Array.from({ length: 150 }, (_, i) => row('film-' + i, '其他作品' + i))]
              : [row('later', '[标记]银河')];
            response.end(JSON.stringify({ page, pagecount: site === 'a' ? 2 : 1, list }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
        """;
}
