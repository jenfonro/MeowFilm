using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicFeedbackProtocolTests
{
    private const string IdentityPath = "/weapi/w/nuser/account/get";
    private const string ReportPath = "/api/clientlog/encrypt/upload";
    private static readonly Guid Playback = Guid.Parse("00000000-0000-4000-8000-000000000042");
    private static readonly DateTimeOffset Occurred = DateTimeOffset.FromUnixTimeMilliseconds(1800000000123);
    private static MusicFeedbackStart Start(MusicFeedbackAttempt attempt) => new(attempt, new("wy", "42"), 200, 0, 1, false)
    { PlaybackId = Playback, StartedAt = Occurred.AddSeconds(-3), AdmittedAt = Occurred };
    private static MusicFeedbackEnd End() => new(5, 5, 200, 5, 1, "ui")
    { PlaybackId = Playback, EndedAt = Occurred.AddSeconds(10) };
    private static TestCase Case(string name, Func<Task> run) => new("feedback-protocol: " + name, run);

    private static IEnumerable<TestCase> NeteaseCases()
    {
        yield return Case("construction and disposal do not acquire accounts or fabricate end", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Protocol.DisposeAsync(); await f.Protocol.DisposeAsync();
            Equal(0, f.Calls); Equal(0, f.Accounts.Acquisitions); Equal(0, f.Accounts.Disposals);
            True(!f.Handler.Disposed);
            using var request = MusicPlatformRequests.WyWithCookie("/api/v3/song/detail",
                new() { ["c"] = "[]" }, null, MusicClientContextFixture.Wy, true);
            _ = await f.Http.SendAsync("wy", request, CancellationToken.None);
            Equal(1, f.Calls);
        });
        yield return Case("FM flat NCBL records retain original time source and media-wall seconds", async () =>
        {
            await using var f = await Fixture.Create();
            var input = Start(f.Attempt) with { Source = "userfm", Mode = "playFm", Algorithm = "fixture-alg" };
            var pending = NeteasePlaybackTransport.Start(f.Account, input, 1);
            var start = Record(pending, pending.Start!, "_plv");
            Equal("42", Text(start["id"])); Equal("song", Text(start["type"]));
            using var startSyntax = JsonDocument.Parse(start.ToJsonString());
            Equal(JsonValueKind.Number, startSyntax.RootElement.GetProperty("id").ValueKind);
            Equal(42L, startSyntax.RootElement.GetProperty("id").GetInt64());
            Equal("userfm", Text(start["source"])); Equal("fixture-alg", Text(start["alg"]));
            Equal("0", Text(start["time"])); Equal("0", Text(start["realtime"]));
            Equal(input.AdmittedAt.ToUnixTimeMilliseconds().ToString(), Text(start["_logTime"]));
            var terminal = new NeteasePlaybackEvent(Guid.NewGuid(), End().EndedAt.ToUnixTimeMilliseconds(),
                false, 17.5, 8.75, "playend", 2);
            var finish = Record(pending with { End = terminal }, terminal, "_pld");
            Equal("17", Text(finish["time"])); Equal("8", Text(finish["realtime"]));
            Equal("2", Text(finish["play_speed"])); True(start["play_speed"] is null);
            True(terminal is { Time: 17.5, Realtime: 8.75 }); // Wire truncation must not change stored listening facts.
            Equal("playend", Text(finish["end"]));
            foreach (var json in new[] { start, finish })
                foreach (string absent in new[] { "json", "action", "mainsite", "mainsiteWeb", "refer", "deviceId",
                    "download", "wifi", "mode", "position", "duration", "rate", "sourcetype", "is_heart" })
                    True(!json.ContainsKey(absent));
            await using var session = await f.Protocol.StartAsync(f.Account, input);
            Equal(input.Recording, session.Recording);
            await f.Protocol.DrainAsync();
            await session.FinishAsync(End() with { Listened = 8.75, MediaListened = 17.5, Rate = 2,
                Reason = "playend", Position = 193.25 });
            await session.FinishAsync(End() with { Listened = 8.75, MediaListened = 17.5, Rate = 2,
                Reason = "playend", Position = 193.25 });
            await f.Protocol.DrainAsync();
            Equal(2, f.Filenames.Count); Equal(0, f.Saved().Length);
        });
        foreach (var vector in new (string Source, string Id, string Type)[]
        {
            ("list", "7", "list"), ("album", "8", "album"), ("search", " 原始关键词 & 歌手 ", "track"),
            ("dailySongRecommend", "", "dailyRecommend"), ("song", "", "track"), ("userfm", "", ""),
            ("search_song_detail_page_song_rec", "", "track"), ("queue-source-fixture", "", "track"),
            ("unknown", "", "")
        })
            yield return Case("original provenance " + vector.Source, () =>
            {
                var input = Start(new()) with { Source = vector.Source, SourceId = vector.Id,
                    SourceType = vector.Type, Algorithm = "occurrence-alg" };
                var pending = NeteasePlaybackTransport.Start(Account(), input, 1);
                var json = Record(pending, pending.Start!, "_plv");
                Equal(vector.Source == "unknown" ? "" : vector.Source, Text(json["source"]));
                Equal(vector.Id, Text(json["sourceId"])); Equal("occurrence-alg", Text(json["alg"]));
                return Task.CompletedTask;
            });
        foreach (string source in new[] { "search_song_detail_page_song_rec", "playlist", "likeMusic", "queue-source-fixture", "moreRcmd" })
            yield return Case("live queue sourceMap remains exact " + source, () =>
            {
                var origin = Origin("queue", "queue-recommendations", source);
                var input = Start(new()) with { Source = source, SourceType = "track",
                    IsQueueRecommendation = true, Algorithm = origin.Algorithm, Recommendation = origin };
                var pending = NeteasePlaybackTransport.Start(Account(), input, 1);
                var end = new NeteasePlaybackEvent(Guid.NewGuid(), End().EndedAt.ToUnixTimeMilliseconds(), false, 5, 5, "ui");
                Equal(source, Text(Record(pending, pending.Start!, "_plv")["source"]));
                var json = Record(pending with { End = end }, end, "_pld");
                Equal(source, Text(json["source"])); Equal("occurrence-alg", Text(json["alg"]));
                True(json["sourceId"] is null);
                return Task.CompletedTask;
            });
        yield return Case("heartbeat uses real scene original context and per-song algorithm", () =>
        {
            var origin = Origin("heartbeat", "heartbeat", "") with { SourceType = "playlist", SourceId = "700" };
            var input = Start(new()) with { Source = "aiMode", SourceId = "700", Mode = "ai",
                Algorithm = origin.Algorithm, Recommendation = origin };
            var pending = NeteasePlaybackTransport.Start(Account(), input, 1);
            var json = Record(pending, pending.Start!, "_plv");
            Equal("heartbeat", Text(json["play_scene"])); True(json["source"] is null && json["sourceId"] is null);
            Equal("occurrence-alg", Text(json["alg"]));
            var mapped = NeteasePlaybackTransport.Start(Account(), input with
                { Recommendation = origin with { Provider = "queue-recommendations", FeedbackSource = "upstream-heart",
                    Metadata = JsonSerializer.SerializeToElement(new { feedbackSourceId = "upstream-42" }) } }, 1);
            Equal("upstream-heart", Text(Record(mapped, mapped.Start!, "_plv")["source"]));
            Equal("upstream-42", Text(Record(mapped, mapped.Start!, "_plv")["sourceId"]));
            var similar = NeteasePlaybackTransport.Start(Account(), Start(new()) with
                { Recommendation = Origin("similar", "similar", ""), Algorithm = "similar-alg" }, 1);
            True(Record(similar, similar.Start!, "_plv")["source"] is null);
            return Task.CompletedTask;
        });
        yield return Case("metadata contains only original MUSIC_U and no official identity", () =>
        {
            using var doc = JsonDocument.Parse(NeteasePlaybackTransport.Metadata(Account()));
            Equal(1, doc.RootElement.EnumerateObject().Count());
            Equal("synthetic-session", doc.RootElement.GetProperty("MUSIC_U").GetString());
            return Task.CompletedTask;
        });
        yield return Case("response client cookies cannot replace the original upload authorization", async () =>
        {
            await using var f = await Fixture.Create();
            var before = await f.Clients.Owner.AcquireWyAsync();
            f.Reply = (request, _, _) =>
            {
                var response = Default(request);
                response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_RESPONSE; Domain=.music.163.com");
                return Task.FromResult(response);
            };
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            await session.FinishAsync(End()); await f.Protocol.DrainAsync();
            True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()));
            Equal(2, f.Filenames.Count); // Every upload header was checked against the exact account Cookie.
        });
        foreach (var vector in new (string Body, string Code)[]
        {
            ("{\"code\":200,\"profile\":null}", "AUTH_EXPIRED"),
            ("{\"code\":200,\"profile\":{\"userId\":124,\"nickname\":\"synthetic\"}}", "IDENTITY_MISMATCH"),
            ("{\"code\":200,\"profile\":{\"userId\":123,\"nickname\":\"synthetic\"},\"account\":{\"id\":124}}", "IDENTITY_MISMATCH"),
            ("{\"code\":301}", "AUTH_EXPIRED")
        })
            yield return Case("live identity qualification fails before listening admission " + vector.Code + vector.Body.Length, async () =>
            {
                await using var f = await Fixture.Create();
                f.Reply = (_, _, _) => Task.FromResult(Response(vector.Body));
                await Error(() => f.Protocol.QualifyAccountAsync(f.Account, default), vector.Code);
                Equal(0, f.Saved().Length); Equal(0, f.Filenames.Count);
                // Direct adapter admission is local; its independent drain still
                // refuses to upload to an unauthenticated or mismatched account.
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt));
                await f.Protocol.DrainAsync(); Equal(vector.Code, f.Protocol.LastError);
                Equal(0, f.Filenames.Count); Equal(0, f.Saved().Length);
            });
        yield return Case("WY removes extra recording duration precheck while retaining exact ID rejection", async () =>
        {
            await using var f = await Fixture.Create();
            foreach (var invalid in new[]
            {
                Start(new()) with { Recording = new("tx", "001FixtureMID") },
                Start(new()) with { Recording = new("wy", "042") },
                Start(new()) with { Recording = new("wy", "42", "7") }
            }) await Error(() => f.Protocol.StartAsync(f.Account, invalid), "IDENTITY_MISMATCH");
            // Ambiguous trusted mappings cannot be rescued by a song-detail read.
            var song = MusicSongFixture.Online("tx", "MID", "song", "artist", "", 200);
            song["sources"] = new JsonArray(
                song["sources"]![0]!.DeepClone(),
                MusicSongFixture.Online("wy", "42", "song", "artist", "", 200)["sources"]![0]!.DeepClone(),
                MusicSongFixture.Online("wy", "43", "song", "artist", "", 200)["sources"]![0]!.DeepClone());
            True(MusicFeedbackIdentity.Resolve(MusicSongFixture.Element(song), "wy") is null);
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { Duration = 0 });
            await f.Protocol.DrainAsync();
            True(f.Paths.All(path => path is IdentityPath or ReportPath)); Equal(1, f.Filenames.Count);
        });
        yield return Case("invalid local numbers and text reject before persistent admission or HTTP", async () =>
        {
            await using var f = await Fixture.Create();
            foreach (var input in new[]
            {
                Start(new()) with { Duration = double.NaN }, Start(new()) with { Duration = -1 },
                Start(new()) with { Rate = double.PositiveInfinity }, Start(new()) with { Rate = 0 },
                Start(new()) with { Position = 201 }, Start(new()) with { PlaybackId = Guid.Empty },
                Start(new()) with { AdmittedAt = default }, Start(new()) with { Algorithm = "\ud800" },
                Start(new()) with { SourceId = "keyword\n" }, Start(new()) with { Source = "source\n" }
            }) await AnyFeedbackError(() => f.Protocol.StartAsync(f.Account, input));
            Equal(0, f.Calls); Equal(0, f.Saved().Length);
        });
        yield return Case("long high-rate and delayed-dispatch facts are not bounded by dispatch elapsed", async () =>
        {
            await using var f = await Fixture.Create();
            var input = Start(f.Attempt) with { Duration = 90000, Rate = 32 };
            await using var session = await f.Protocol.StartAsync(f.Account, input); await f.Protocol.DrainAsync();
            await session.FinishAsync(End() with { Listened = 100, MediaListened = 3200,
                Rate = 32, Duration = 0, Position = 3200 });
            await f.Protocol.DrainAsync(); Equal(2, f.Filenames.Count);
        });
        foreach (var end in new[]
        {
            End() with { Listened = double.NaN }, End() with { MediaListened = -1 },
            End() with { Duration = -1 }, End() with { Position = double.PositiveInfinity },
            End() with { Rate = 0 }, End() with { Reason = "fabricated" },
            End() with { PlaybackId = Guid.NewGuid() }, End() with { EndedAt = default }
        })
            yield return Case("invalid terminal facts cannot be corrected into a second end " + end, async () =>
            {
                await using var f = await Fixture.Create();
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                await Error(() => session.FinishAsync(end), "INVALID_REQUEST");
                await Error(() => session.FinishAsync(End()), "INSTANCE_MISMATCH");
                Equal(1, f.Filenames.Count);
            });
        yield return Case("natural end preserves observed position zero time and original wall clock", async () =>
        {
            await using var f = await Fixture.Create();
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            await session.FinishAsync(End() with { Listened = 0, MediaListened = 0,
                Position = 193.25, Reason = "playend", EndedAt = Occurred.AddSeconds(5) });
            await f.Protocol.DrainAsync(); Equal(2, f.Filenames.Count);
            var pending = NeteasePlaybackTransport.Start(f.Account, Start(new()), 1);
            var value = new NeteasePlaybackEvent(Guid.NewGuid(), 0, false, 0, 0, "playend");
            Equal("0", Text(Record(pending with { End = value }, value, "_pld")["_logTime"]));
        });
        yield return Case("concurrent duplicate local start shares one session during held upload", async () =>
        {
            await using var f = await Fixture.Create();
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath == ReportPath)
                { entered.TrySetResult(); await release.Task; }
                return Default(request);
            };
            var a = f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var b = f.Protocol.StartAsync(f.Account, Start(f.Attempt));
                await using var session = await a; True(ReferenceEquals(session, await b));
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { Algorithm = "changed" }), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { IsQueueRecommendation = true }), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(new("wy", f.Account.Scope, 2, "123", f.Account.Cookie),
                    Start(f.Attempt)), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(new("wy", f.Account.Scope, 1, "123", "MUSIC_U=changed"),
                    Start(f.Attempt)), "INSTANCE_MISMATCH");
            }
            finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal(1, f.Filenames.Count);
        });
        foreach (string code in new[] { "\"200\"", "true", "0", "500", "null" })
            yield return Case("bad business code never confirms or replays file " + code, async () =>
            {
                await using var f = await Fixture.Create();
                f.Reply = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath == ReportPath
                    ? Response("{\"code\":" + code + ",\"data\":{\"successfiles\":[\"" + Filename(request) + "\"]}}")
                    : Default(request));
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                Equal("UPSTREAM_UNCONFIRMED", f.Protocol.LastError); Equal(0, f.Saved().Length);
                await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(1, f.Filenames.Count);
                await Error(() => session.FinishAsync(End()), "START_UNCONFIRMED");
            });
        yield return Case("numeric code plus exact filename confirms the file", async () =>
        {
            await using var f = await Fixture.Create();
            f.Reply = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath == ReportPath
                ? Response("{\"code\":2e2,\"data\":{\"successfiles\":[\"" + Filename(request) + "\"]}}")
                : Default(request));
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            True(f.Protocol.LastError is null); Equal(0, f.Saved().Length);
            await session.FinishAsync(End()); await f.Protocol.DrainAsync(); Equal(2, f.Filenames.Count);
        });
        foreach (string body in new[]
        {
            "{\"code\":200}", "{\"code\":200,\"data\":{\"successfiles\":[]}}",
            "{\"code\":200,\"data\":{\"successfiles\":[\"other\"]}}",
            "{\"code\":200,\"data\":{\"successfiles\":true}}"
        })
            yield return Case("missing or mismatched filename never confirms " + body, async () =>
            {
                await using var f = await Fixture.Create();
                f.Reply = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath == ReportPath ? Response(body) : Default(request));
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                Equal("UPSTREAM_UNCONFIRMED", f.Protocol.LastError);
                await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(1, f.Filenames.Count);
            });
        foreach (var vector in new (string Result, string Error)[]
        {
            ("network", "NETWORK_ERROR"), ("timeout", "TIMEOUT"), ("redirect", "REDIRECT_DENIED"),
            ("limit", "LIMIT"), ("malformed", "INVALID_RESPONSE"), ("http-error", "UPSTREAM_UNCONFIRMED"),
            ("401", "AUTH_EXPIRED"), ("403", "FORBIDDEN"), ("429", "RATE_LIMITED")
        })
            yield return Case("unknown upload is single-shot " + vector.Result, async () =>
            {
                await using var f = await Fixture.Create();
                f.Reply = (request, _, _) =>
                {
                    if (request.RequestUri!.AbsolutePath != ReportPath) return Task.FromResult(Default(request));
                    if (vector.Result == "network") throw new HttpRequestException("synthetic-private-error");
                    if (vector.Result == "timeout") throw new OperationCanceledException();
                    var response = Response(vector.Result == "limit" ? new string(' ', 65537) :
                        vector.Result == "malformed" ? "not-json" : "{}");
                    response.StatusCode = vector.Result switch
                    {
                        "redirect" => HttpStatusCode.TemporaryRedirect, "http-error" => HttpStatusCode.BadGateway,
                        "401" => HttpStatusCode.Unauthorized, "403" => HttpStatusCode.Forbidden,
                        "429" => HttpStatusCode.TooManyRequests, _ => HttpStatusCode.OK
                    };
                    return Task.FromResult(response);
                };
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                Equal(vector.Error, f.Protocol.LastError); Equal(0, f.Saved().Length);
                await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(1, f.Filenames.Count);
            });
        yield return Case("terminal is locally single-shot while upload has an unknown outcome", async () =>
        {
            await using var f = await Fixture.Create();
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath == ReportPath)
                { entered.TrySetResult(); await release.Task; throw new HttpRequestException("synthetic-private"); }
                return Default(request);
            };
            try
            {
                await session.FinishAsync(End()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.FinishAsync(End());
                await Error(() => session.FinishAsync(End() with { Listened = 6 }), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_ENDED");
            }
            finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal("NETWORK_ERROR", f.Protocol.LastError);
            await session.FinishAsync(End()); await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Equal(2, f.Filenames.Count); Equal(0, f.Saved().Length);
        });
        foreach (int stage in new[] { 1, 2 })
            yield return Case("account generation is fenced after physical step " + stage, async () =>
            {
                await using var f = await Fixture.Create();
                f.Reply = (request, _, count) =>
                { if (count == stage) f.Accounts.Current = false; return Task.FromResult(Default(request)); };
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                Equal(stage, f.Calls); True(f.Protocol.LastError == "SESSION_CHANGED");
                True(!f.Saved().Any(v => v.StartConfirmed));
                await Error(() => session.FinishAsync(End()), "SESSION_CHANGED");
            });
        foreach (int stage in new[] { 3, 4 })
            yield return Case("finish retains original account fence after start ACK at step " + stage, async () =>
            {
                await using var f = await Fixture.Create();
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                f.Reply = (request, _, count) =>
                { if (count == stage) f.Accounts.Current = false; return Task.FromResult(Default(request)); };
                await session.FinishAsync(End()); await f.Protocol.DrainAsync();
                Equal(stage, f.Calls); Equal("SESSION_CHANGED", f.Protocol.LastError);
                Equal(0, f.Saved().Length);
            });
        yield return Case("finish repeats live qualification and expired login never sends end", async () =>
        {
            await using var f = await Fixture.Create();
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":301}"));
            await session.FinishAsync(End()); await f.Protocol.DrainAsync();
            Equal("AUTH_EXPIRED", f.Protocol.LastError); Equal(1, f.Filenames.Count);
            await session.FinishAsync(End()); await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync();
            Equal(1, f.Filenames.Count); Equal(0, f.Saved().Length);
        });
        yield return Case("controller excludes real progress before live account qualification", async () =>
        {
            await using var f = await Fixture.Create(); var clock = new FactClock();
            await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, clock);
            var entered = Signal(); var release = Signal();
            f.Offline = false;
            f.Reply = async (request, _, count) =>
            {
                if (count == 1) { entered.TrySetResult(); await release.Task; }
                // A failed second identity read proves the playback file has
                // never entered SendAsync; it leaves inspectable local facts.
                if (count > 1) throw new HttpRequestException("synthetic preflight offline");
                return Default(request);
            };
            controller.Select(new("entry", MusicSongFixture.Element(
                MusicSongFixture.Online("wy", "42", "song", "artist", "", 200))));
            controller.Started("entry", 0, 0);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                clock.Advance(30); controller.Observe("entry", 30, 0, true);
            }
            finally { release.TrySetResult(); }
            await controller.DrainAsync();
            clock.Advance(7); controller.Observe("entry", 37, 0, true);
            controller.Finish(); await controller.DrainAsync();
            var pending = f.Saved().Single();
            Equal(Occurred.AddSeconds(30).ToUnixTimeMilliseconds(), pending.Start!.At);
            True(pending.End is { Time: 7, Realtime: 7 }); Equal(0, f.Filenames.Count);
        });
        yield return Case("close cancels unqualified account lookup without waiting for its late response", async () =>
        {
            await using var f = await Fixture.Create();
            await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, f.Clock);
            var entered = Signal(); var cancelled = Signal(); var release = Signal();
            f.Reply = async (request, token, _) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task; return Default(request);
            };
            controller.Select(new("entry", MusicSongFixture.Element(
                MusicSongFixture.Online("wy", "42", "song", "artist", "", 200))));
            controller.Started("entry", 0, 0);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var prepare = controller.PrepareShutdownAsync();
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await prepare.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(0, f.Saved().Length); Equal(0, f.Filenames.Count);
                True(!f.Protocol.DrainAsync().IsCompleted);
            }
            finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync();
            Equal(0, f.Saved().Length); Equal(0, f.Filenames.Count);
        });
        yield return Case("pre-cancellation and stale accounts create no disk or HTTP work", async () =>
        {
            await using var f = await Fixture.Create();
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Cancelled(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt), stop.Token));
            f.Accounts.Current = false;
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "SESSION_CHANGED");
            Equal(0, f.Calls); Equal(0, f.Saved().Length);
        });
        foreach (string action in new[] { "token", "protocol", "prepare" })
            yield return Case("cancellation retains physical ownership and rejects late ACK " + action, async () =>
            {
                await using var f = await Fixture.Create(); using var stop = new CancellationTokenSource();
                var entered = Signal(); var cancelled = Signal(); var release = Signal();
                f.Reply = async (request, token, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == ReportPath)
                    {
                        using var registration = token.Register(() => cancelled.TrySetResult());
                        entered.TrySetResult(); await release.Task;
                    }
                    return Default(request);
                };
                Task completion = Task.CompletedTask;
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt), stop.Token);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (action == "protocol") completion = f.Protocol.DisposeAsync().AsTask();
                    else if (action == "prepare") completion = f.Protocol.PrepareShutdownAsync();
                    else stop.Cancel();
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var drain = f.Protocol.DrainAsync(); True(!drain.IsCompleted);
                    if (action == "protocol") True(!completion.IsCompleted);
                    if (action == "prepare") await completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { release.TrySetResult(); }
                await completion; await f.Protocol.DrainAsync();
                Equal(0, f.Saved().Length); Equal(1, f.Filenames.Count);
                Equal(0, f.Accounts.Disposals); True(!f.Handler.Disposed);
            });
        yield return Case("idle session disposal never fabricates end or revokes safe pending", async () =>
        {
            await using var f = await Fixture.Create(); f.Offline = true;
            var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            await session.DisposeAsync(); await session.DisposeAsync(); await f.Protocol.DrainAsync();
            await Error(() => session.FinishAsync(End()), "CLOSED");
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_ENDED");
            Equal(1, f.Saved().Length); Equal(0, f.Filenames.Count);
        });
        yield return Case("session release leaves independent outbox owning physical terminal cleanup", async () =>
        {
            await using var f = await Fixture.Create();
            var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath == ReportPath)
                { entered.TrySetResult(); await release.Task; throw new HttpRequestException("synthetic-private"); }
                return Default(request);
            };
            try
            {
                await session.FinishAsync(End()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                True(!f.Protocol.DrainAsync().IsCompleted);
            }
            finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal(0, f.Saved().Length);
            Equal("NETWORK_ERROR", f.Protocol.LastError); Equal(2, f.Filenames.Count);
            await session.DisposeAsync();
            await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(2, f.Filenames.Count);
        });
        yield return Case("cancelled duplicate waiter cannot revoke original pending local admission", async () =>
        {
            await using var f = await Fixture.Create(); using var stop = new CancellationTokenSource();
            f.Store.HoldNext = true;
            var original = f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            try
            {
                await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var duplicate = f.Protocol.StartAsync(f.Account, Start(f.Attempt), stop.Token); stop.Cancel();
                await Cancelled(() => duplicate); True(!original.IsCompleted);
            }
            finally { f.Store.Release.TrySetResult(); }
            await using var session = await original;
            await f.Protocol.DrainAsync(); Equal(1, f.Filenames.Count);
        });
        foreach (string cookie in new[] { "MUSIC_U=x\r\nHost: other.invalid", "MUSIC_U=x; MUSIC_U=y", "__csrf=synthetic" })
            yield return Case("credential injection duplicate or missing login rejects locally " + cookie.Length, async () =>
            {
                await using var f = await Fixture.Create();
                await Error(() => f.Protocol.StartAsync(new("wy", "synthetic", 1, "123", cookie), Start(f.Attempt)),
                    cookie.StartsWith("__csrf", StringComparison.Ordinal) ? "AUTH_REQUIRED" : "INVALID_CREDENTIALS");
                Equal(0, f.Calls); Equal(0, f.Saved().Length);
            });
        yield return Case("QQ and KG keep their device credential and TLS guards", async () =>
        {
            await using var f = await Fixture.Create();
            await Error(() => f.Protocol.StartAsync(new("tx", "synthetic", 1, "123", "musicid=123; musickey=key"),
                Start(new()) with { Recording = new("tx", "001FixtureMID") }), "DEVICE_CONTEXT_REQUIRED");
            await Error(() => f.Protocol.StartAsync(new("tx", "synthetic", 1, "124", "musicid=123; musickey=key"),
                Start(new()) with { Recording = new("tx", "001FixtureMID") }), "INVALID_CREDENTIALS");
            await Error(() => f.Protocol.StartAsync(new("kg", "synthetic", 1, "123", "userid=123; token=key"),
                Start(new()) with { Recording = new("kg", new string('A', 32), "7") }), "DEVICE_CONTEXT_REQUIRED");
            await Error(() => f.Protocol.StartAsync(new("kg", "synthetic", 1, "123",
                "userid=123; token=key; KUGOU_API_MID=123; KUGOU_API_GUID=abc; KUGOU_API_DEV=synthetic"),
                Start(new()) with { Recording = new("kg", new string('A', 32), "7") }), "UNVERIFIED_TLS_PROTOCOL");
            Equal(0, f.Calls);
        });
        foreach (string url in new[]
        {
            NeteasePlaybackTransport.UploadUrl,
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload",
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=false",
            "https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true&extra=1",
            "https://clientlog.music.163.com/eapi/feedback/weblog",
            "https://clientlog3.music.163.com/other?multiupload=true",
            "http://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true"
        })
            yield return Case("upload permissions are exact and do not enlarge ordinary HTTP " + url, async () =>
            {
                await using var f = await Fixture.Create();
                using var ordinary = new HttpRequestMessage(HttpMethod.Post, url);
                True(!MusicPlatformHttp.IsAllowedOrigin("wy", ordinary.RequestUri!));
                try { await f.Http.SendAsync("wy", ordinary, default); throw new Exception("ordinary origin accepted"); }
                catch (MusicPlatformException error) { Equal("ORIGIN_DENIED", error.Code); }
                using var native = new HttpRequestMessage(HttpMethod.Post, url) { Content = new MultipartFormDataContent() };
                if (url != NeteasePlaybackTransport.UploadUrl)
                {
                    try { await f.Http.SendFeedbackJsonAsync("wy", native, default); throw new Exception("route accepted"); }
                    catch (MusicPlatformException error) { Equal("ORIGIN_DENIED", error.Code); }
                }
                Equal(0, f.Calls);
            });
        yield return Case("independent attempts retain no rate ledger or remote receipt history", async () =>
        {
            await using var f = await Fixture.Create();
            f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":200,\"profile\":null}"));
            for (int i = 0; i < 81; i++)
            {
                await using var session = await f.Protocol.StartAsync(f.Account, Start(new()) with { PlaybackId = Guid.NewGuid() });
                await f.Protocol.DrainAsync();
            }
            Equal(81, f.Calls); Equal(0, f.Saved().Length); Equal(0, f.Filenames.Count);
        });
    }

    private static JsonObject Record(NeteasePlaybackPending pending, NeteasePlaybackEvent value, string action)
    {
        string text = Encoding.UTF8.GetString(NeteasePlaybackTransport.Records(pending, value));
        True(text.EndsWith('\n') && text.Count(c => c == '\n') == 1);
        string[] parts = text[..^1].Split('\x01');
        Equal(3, parts.Length); Equal((value.At / 1000).ToString(), parts[0]); Equal(action, parts[1]);
        return JsonNode.Parse(parts[2])!.AsObject();
    }
    private static MusicRecommendationFeedback Origin(string kind, string provider, string source) =>
        new("session", "batch", "entry", "wy", kind, provider, "song", "", "occurrence-alg", "", "",
            true, false, source);
    private static MusicPlatformSession Account() => new("wy", "synthetic-account", 1, "123",
        "MUSIC_U=synthetic-session; __csrf=synthetic-csrf");
    private static string Filename(HttpRequestMessage request) =>
        ((MultipartFormDataContent)request.Content!).Single().Headers.ContentDisposition!.FileName!.Trim('"');
    private static HttpResponseMessage Default(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        IdentityPath => Response("{\"code\":200,\"profile\":{\"userId\":123,\"nickname\":\"synthetic\"},\"account\":{\"id\":123}}"),
        "/weapi/v3/song/detail" => Response("{\"code\":200,\"songs\":[{\"id\":42,\"dt\":200000}]}"),
        ReportPath => Response(JsonSerializer.Serialize(new { code = 200, data = new { successfiles = new[] { Filename(request) } } })),
        _ => throw new Exception("Unexpected endpoint")
    };
    private sealed class Accounts : IMusicPlatformSessionSource, IDisposable
    {
        internal bool Current = true;
        internal int Acquisitions, Disposals;
        internal MusicPlatformSession Value = Account();
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Acquisitions); return ValueTask.FromResult<MusicPlatformSession?>(Current ? Value : null); }
        public bool IsCurrent(MusicPlatformSession account) => Current;
        public void Dispose() => Disposals++;
    }
    private sealed class Store : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> rows = [];
        internal bool HoldNext;
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            lock (rows)
            {
                var bytes = rows.GetValueOrDefault(ns)?.ToArray();
                return Task.FromResult(new DocumentSnapshot(bytes, bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
            }
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            if (HoldNext)
            {
                HoldNext = false; Entered.TrySetResult(); await Release.Task;
            }
            lock (rows)
            {
                var old = rows.GetValueOrDefault(ns);
                if ((old is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(old)) != expected)
                    throw new DocumentStoreException(DocumentStoreError.Conflict);
                rows[ns] = bytes.ToArray(); return DocumentFingerprint.FromBytes(bytes.Span);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FactClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Occurred.AddMilliseconds(ticks);
        internal void Advance(int seconds) => ticks += seconds * 1000;
    }
    private sealed class Handler(Fixture owner) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => owner.Send(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly MusicFeedbackAttempt Attempt = new();
        internal readonly Accounts Accounts = new();
        internal MusicPlatformSession Account => Accounts.Value;
        internal readonly Handler Handler;
        internal readonly MusicPlatformHttp Http;
        internal readonly NativeMusicFeedbackProtocol Protocol;
        internal readonly MusicClientContextFixture Clients;
        internal readonly Store Store = new();
        internal readonly MusicFeedbackSettingsOwner Settings;
        internal readonly FactClock Clock = new();
        internal readonly List<string> Filenames = [], Paths = [];
        internal Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>>? Reply;
        internal bool Offline;
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        private Fixture()
        {
            Handler = new(this); Http = MusicPlatformHttp.CreateWithHandler(Handler); Clients = new(Http, clock: Clock);
            Settings = new(Store); Protocol = new(Accounts, Http, Clients.Owner, Clock, Store, Settings);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Settings.ReadAsync(); await f.Settings.SaveAsync(0, ["wy"]); return f;
        }
        internal NeteasePlaybackPending[] Saved() => NeteasePlaybackOutboxDocument.Decode(
            Store.ReadAsync(NeteasePlaybackOutboxDocument.Namespace).GetAwaiter().GetResult());
        internal async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            int count = Interlocked.Increment(ref calls);
            Equal(HttpMethod.Post, request.Method); Equal("https", request.RequestUri!.Scheme);
            Paths.Add(request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath is IdentityPath or ReportPath)
            {
                Equal("Meowfilm (Windows)", string.Join(" ", request.Headers.GetValues("User-Agent")));
                Equal(Account.Cookie, request.Headers.GetValues("Cookie").Single()); True(!request.Headers.Contains("Referer"));
            }
            if (request.RequestUri.AbsolutePath == ReportPath)
            {
                Equal(NeteasePlaybackTransport.UploadUrl, request.RequestUri.ToString());
                True(request.Content is MultipartFormDataContent);
                var part = ((MultipartFormDataContent)request.Content!).Single();
                Equal("file", part.Headers.ContentDisposition!.Name!.Trim('"'));
                Equal("multipart/form-data", part.Headers.ContentType!.MediaType);
                string filename = Filename(request);
                True(filename.StartsWith("play_", StringComparison.Ordinal) && Guid.TryParseExact(filename[5..], "N", out _));
                True(!Filenames.Contains(filename)); Filenames.Add(filename);
                byte[] bytes = await part.ReadAsByteArrayAsync(token);
                True(bytes.AsSpan(0, 4).SequenceEqual("NCBL"u8));
                True(!Encoding.UTF8.GetString(bytes).Contains("synthetic-session", StringComparison.Ordinal));
            }
            if (Offline) throw new HttpRequestException("synthetic preflight offline");
            return Reply is null ? Default(request) : await Reply(request, token, count);
        }
        public async ValueTask DisposeAsync()
        {
            Store.Release.TrySetResult();
            await Protocol.DisposeAsync(); await Settings.DisposeAsync(); await Clients.DisposeAsync(); Http.Dispose();
        }
    }
}
