// SPDX-License-Identifier: GPL-3.0-only
// Synthetic HTTP only. Source vectors: pinned iWebPlus tests/netease.test.cjs,
// qq-feedback.test.cjs and kugou-feedback.test.cjs. No upstream accounts/reports.
using System.Net;
using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static class MusicFeedbackProtocolTests
{
    private const string IdentityPath = "/weapi/w/nuser/account/get";
    private const string SongPath = "/weapi/v3/song/detail";
    private const string ReportPath = "/eapi/feedback/weblog";
    private static TestCase Case(string name, Func<Task> run) => new("feedback-protocol: " + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("construction and disposal do not acquire accounts or send end", async () =>
        {
            await using var f = new Fixture();
            await f.Protocol.DisposeAsync();
            await f.Protocol.DisposeAsync();
            Equal(0, f.Calls); Equal(0, f.Accounts.Acquisitions); Equal(0, f.Accounts.Disposals);
            True(!f.Handler.Disposed);
            using var request = MusicPlatformRequests.WyWithCookie("/api/v3/song/detail", new() { ["c"] = "[]" }, null, MusicClientContextFixture.Wy, true);
            _ = await f.Http.SendAsync("wy", request, CancellationToken.None);
            Equal(1, f.Calls);
        });
        yield return Case("fixed FM vector exact identity encrypted start and wall-second finish once", async () =>
        {
            await using var f = new Fixture();
            var input = Start(f.Attempt) with { Source = "userfm", Mode = "playFm", Algorithm = "fixture-alg" };
            await using var session = await f.Protocol.StartAsync(f.Account, input);
            Equal(input.Recording, session.Recording); Equal(3, f.Calls);
            Equal("startplay", Text(f.Logs[0]["action"]));
            var json = f.Logs[0]["json"]!.AsObject();
            Equal("42", Text(json["id"])); Equal("radio", Text(json["source"]));
            Equal("fixture-alg", Text(json["alg"])); Equal("song", Text(json["type"]));
            Equal("1", Text(json["mainsite"])); Equal("1", Text(json["mainsiteWeb"]));
            True(json["mode"] is null && json["is_heart"] is null && json["sourcetype"] is null && json["time"] is null);
            f.Clock.Advance(10);
            var end = End() with { Listened = 8.75, MediaListened = 17.5, Rate = 2, Position = 200, Reason = "playend" };
            await session.FinishAsync(end);
            await session.FinishAsync(end);
            Equal(5, f.Calls); Equal(2, f.Logs.Count);
            Equal("play", Text(f.Logs[1]["action"]));
            var finish = f.Logs[1]["json"]!.AsObject();
            Equal("8", Text(finish["time"])); Equal("playend", Text(finish["end"]));
            Equal("0", Text(finish["download"])); Equal("0", Text(finish["wifi"]));
            foreach (string key in new[] { "mediaListened", "duration", "position", "rate", "mode", "is_heart", "resource_time", "realtime" })
                True(finish[key] is null);
            Equal(0, f.Accounts.Acquisitions);
        });
        yield return Case("validated WY steps reuse each confirmed client update", async () =>
        {
            await using var f = new Fixture();
            f.Reply = async (request, _, count) =>
            {
                var client = await f.Clients.Owner.AcquireWyAsync();
                if (count > 1)
                    True(client.Nmtid == "SYNTHETIC_FEEDBACK_" + (count - 1) &&
                        request.Headers.GetValues("Cookie").Single().Contains("NMTID=" + client.Nmtid, StringComparison.Ordinal));
                var response = Default(request);
                response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_FEEDBACK_" + count + "; Domain=.music.163.com");
                return response;
            };
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            Equal("SYNTHETIC_FEEDBACK_3", (await f.Clients.Owner.AcquireWyAsync()).Nmtid);
            f.Clock.Advance(10);
            await session.FinishAsync(End());
            Equal("SYNTHETIC_FEEDBACK_5", (await f.Clients.Owner.AcquireWyAsync()).Nmtid);
            Equal(5, f.Calls); Equal(2, f.Logs.Count);
        });
        // These source/sourceId/sourceType vectors are copied as data, never executed as JS.
        foreach (var vector in new (string Source, string Id, string Type, string Wire)[]
        {
            ("playlist", "7", "list", "list"), ("likeMusic", "7", "list", "list"),
            ("album", "8", "album", "album"), ("search", " 原始关键词 & 歌手 ", "track", "search"),
            ("dailySongRecommend", "", "dailyRecommend", "dailySongRecommend"), ("song", "", "track", "song"),
            ("aiMode", "7", "track", "aiMode"), ("moreRcmd", "", "track", "moreRcmd"), ("unknown", "", "", "")
        })
            yield return Case("fixed provenance " + vector.Source, async () =>
            {
                await using var f = new Fixture();
                await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt) with
                    { Source = vector.Source, SourceId = vector.Id, SourceType = vector.Type, Mode = "ai" });
                var json = f.Logs.Single()["json"]!.AsObject();
                if (vector.Wire.Length > 0) { Equal(vector.Wire, Text(json["source"])); Equal(vector.Id, Text(json["sourceId"])); }
                else True(json["source"] is null && json["sourceId"] is null);
                True(json["mode"] is null && json["is_heart"] is null);
                if (vector.Source is "aiMode" or "moreRcmd")
                {
                    f.Clock.Advance(10);
                    await s.FinishAsync(End());
                    Equal(vector.Wire, Text(f.Logs.Last()["json"]!["source"]));
                    Equal(vector.Id, Text(f.Logs.Last()["json"]!["sourceId"]));
                }
            });
        foreach (string source in new[] { "search_song_detail_page_song_rec", "playlist", "queue-source-fixture" })
            yield return Case("live queue sourceMap wire " + source, async () =>
            {
                await using var f = new Fixture();
                await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt) with
                    { Source = source, SourceType = "track", IsQueueRecommendation = true, Algorithm = "queue-alg" });
                var start = f.Logs.Single()["json"]!.AsObject();
                Equal(source, Text(start["source"])); Equal("", Text(start["sourceId"]));
                Equal("queue-alg", Text(start["alg"]));
                f.Clock.Advance(10);
                await s.FinishAsync(End());
                Equal(source, Text(f.Logs.Last()["json"]!["source"]));
            });
        yield return Case("saved account is not authenticated until live matching profile", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":200,\"profile\":null}"));
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "AUTH_EXPIRED");
            Equal(1, f.Calls); Equal(0, f.Logs.Count);
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
            Equal(1, f.Calls);
        });
        foreach (var body in new[]
        {
            "{\"code\":200,\"profile\":{\"userId\":124,\"nickname\":\"synthetic\"}}",
            "{\"code\":200,\"profile\":{\"userId\":123,\"nickname\":\"synthetic\"},\"account\":{\"id\":124}}"
        })
            yield return Case("live account mismatch " + (body.Contains("\"account\"") ? "account" : "profile"), async () =>
            {
                await using var f = new Fixture();
                var before = await f.Clients.Owner.AcquireWyAsync();
                f.Reply = (_, _, _) => Task.FromResult(Response(body, "NMTID=SYNTHETIC_REJECTED; Domain=.music.163.com"));
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "IDENTITY_MISMATCH");
                Equal(1, f.Calls); Equal(0, f.Logs.Count);
                True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()));
            });
        foreach (var vector in new (string Name, string Body, string Error)[]
        {
            ("different native ID", "{\"code\":200,\"songs\":[{\"id\":43,\"dt\":200000}]}", "IDENTITY_MISMATCH"),
            ("ambiguous recordings", "{\"code\":200,\"songs\":[{\"id\":42,\"dt\":200000},{\"id\":42,\"dt\":200000}]}", "IDENTITY_MISMATCH"),
            ("incompatible duration", "{\"code\":200,\"songs\":[{\"id\":42,\"dt\":180000}]}", "IDENTITY_MISMATCH"),
            ("unknown native duration", "{\"code\":200,\"songs\":[{\"id\":42}]}", "INVALID_RESPONSE")
        })
            yield return Case("recording rejects " + vector.Name, async () =>
            {
                await using var f = new Fixture();
                var before = await f.Clients.Owner.AcquireWyAsync();
                f.Reply = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath == SongPath
                    ? Response(vector.Body, "NMTID=SYNTHETIC_REJECTED; Domain=.music.163.com") : Default(request));
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), vector.Error);
                Equal(2, f.Calls); Equal(0, f.Logs.Count);
                True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()));
            });
        yield return Case("invalid local identities durations rates and provenance send zero HTTP", async () =>
        {
            await using var f = new Fixture();
            foreach (var input in new[]
            {
                Start(f.Attempt) with { Recording = new("tx", "001FixtureMID") },
                Start(f.Attempt) with { Recording = new("wy", "042") },
                Start(f.Attempt) with { Recording = new("wy", "42", "7") },
                Start(f.Attempt) with { Duration = double.NaN }, Start(f.Attempt) with { Duration = 0 },
                Start(f.Attempt) with { Rate = double.PositiveInfinity }, Start(f.Attempt) with { Position = 201 },
                Start(f.Attempt) with { Source = "search", SourceId = "keyword\n" },
                Start(f.Attempt) with { Source = "playlist", SourceId = "7", SourceType = "album" },
                Start(f.Attempt) with { Source = "queue-source-fixture", SourceType = "track" },
                Start(f.Attempt) with { Source = "queue-source\n", SourceType = "track", IsQueueRecommendation = true },
                Start(f.Attempt) with { Source = "queue-source", SourceId = "7", SourceType = "track", IsQueueRecommendation = true },
                Start(f.Attempt) with { Source = "queue-source", SourceType = "list", IsQueueRecommendation = true },
                Start(f.Attempt) with { Algorithm = "\ud800" }
            })
                await AnyFeedbackError(() => f.Protocol.StartAsync(f.Account, input));
            Equal(0, f.Calls);
        });
        yield return Case("stale account before start has no HTTP", async () =>
        {
            await using var f = new Fixture(); f.Accounts.Current = false;
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "SESSION_CHANGED");
            Equal(0, f.Calls);
        });
        foreach (int stage in new[] { 1, 2, 3 })
            yield return Case("account generation fenced after physical step " + stage, async () =>
            {
                await using var f = new Fixture();
                f.Reply = (request, _, count) =>
                {
                    if (count == stage) f.Accounts.Current = false;
                    return Task.FromResult(Default(request));
                };
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "SESSION_CHANGED");
                Equal(stage, f.Calls);
            });
        yield return Case("concurrent duplicate start shares one physical chain and same session", async () =>
        {
            await using var f = new Fixture();
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, count) =>
            {
                if (count == 1) { entered.SetResult(); await release.Task; }
                return Default(request);
            };
            var first = f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await entered.Task;
            var second = f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            try
            {
                True(!first.IsCompleted && !second.IsCompleted); Equal(1, f.Calls);
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { Algorithm = "changed" }), "INSTANCE_MISMATCH");
            }
            finally { release.TrySetResult(); }
            await using var session = await first;
            True(ReferenceEquals(session, await second)); Equal(3, f.Calls);
        });
        yield return Case("same scope new generation and changed credential cannot adopt an instance", async () =>
        {
            await using var f = new Fixture();
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            await Error(() => f.Protocol.StartAsync(new("wy", f.Account.Scope, 2, "123", f.Account.Cookie), Start(f.Attempt)), "INSTANCE_MISMATCH");
            await Error(() => f.Protocol.StartAsync(new("wy", f.Account.Scope, 1, "123", "MUSIC_U=synthetic-new"), Start(f.Attempt)), "INSTANCE_MISMATCH");
            Equal(3, f.Calls);
        });
        yield return Case("recommendation provenance qualification cannot change within a playback attempt", async () =>
        {
            await using var f = new Fixture();
            var input = Start(f.Attempt) with { Source = "moreRcmd", SourceType = "track" };
            await using var session = await f.Protocol.StartAsync(f.Account, input);
            await Error(() => f.Protocol.StartAsync(f.Account, input with { IsQueueRecommendation = true }),
                "INSTANCE_MISMATCH");
            Equal(3, f.Calls);
        });
        foreach (string code in new[] { "\"200\"", "true", "0", "500", "null" })
            yield return Case("non-receipt never accepts or retries start " + code, async () =>
            {
                await using var f = new Fixture();
                f.Reply = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath == ReportPath
                    ? Response("{\"code\":" + code + "}") : Default(request));
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "UPSTREAM_UNCONFIRMED");
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
                Equal(3, f.Calls);
                f.Clock.Advance(5 * 3600);
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
                Equal(3, f.Calls);
            });
        yield return Case("numeric receipt representation preserves JS numeric equality without string coercion", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (request, _, _) => Task.FromResult(request.RequestUri!.AbsolutePath == ReportPath
                ? Response("{\"code\":2e2}") : Default(request));
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            Equal(3, f.Calls);
        });
        yield return Case("ambiguous end is single-shot concurrent and cannot be changed or retried", async () =>
        {
            await using var f = new Fixture();
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); f.Clock.Advance(10);
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, _) =>
            {
                if (request.RequestUri!.AbsolutePath == ReportPath)
                { entered.SetResult(); await release.Task; throw new HttpRequestException("synthetic-private-error"); }
                return Default(request);
            };
            var a = s.FinishAsync(End()); await entered.Task;
            var b = s.FinishAsync(End());
            try
            {
                True(!a.IsCompleted && !b.IsCompleted);
                await Error(() => s.FinishAsync(End() with { Listened = 6 }), "INSTANCE_MISMATCH");
            }
            finally { release.TrySetResult(); }
            await Error(() => a, "NETWORK_ERROR"); await Error(() => b, "NETWORK_ERROR");
            await Error(() => s.FinishAsync(End()), "NETWORK_ERROR");
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_ENDED");
            Equal(5, f.Calls);
        });
        yield return Case("finish repeats live identity and never writes for expired login", async () =>
        {
            await using var f = new Fixture();
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); f.Clock.Advance(10);
            f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":301}"));
            await Error(() => s.FinishAsync(End()), "AUTH_EXPIRED");
            await Error(() => s.FinishAsync(End()), "AUTH_EXPIRED");
            Equal(4, f.Calls); Equal(1, f.Logs.Count);
        });
        foreach (int stage in new[] { 4, 5 })
            yield return Case("finish account change fenced at physical step " + stage, async () =>
            {
                await using var f = new Fixture();
                await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); f.Clock.Advance(10);
                f.Reply = (request, _, count) =>
                {
                    if (count == stage) f.Accounts.Current = false;
                    return Task.FromResult(Default(request));
                };
                await Error(() => s.FinishAsync(End()), "SESSION_CHANGED");
                Equal(stage, f.Calls);
            });
        foreach (var vector in new (string Name, MusicFeedbackEnd End, string Code)[]
        {
            ("forged wall time", End() with { Listened = 100 }, "INVALID_LISTEN_TIME"),
            ("forged media time", End() with { MediaListened = 100 }, "INVALID_LISTEN_TIME"),
            ("changed duration", End() with { Duration = 150 }, "IDENTITY_MISMATCH"),
            ("false completion", End() with { Reason = "playend", Position = 2 }, "INVALID_REQUEST"),
            ("nonfinite", End() with { Listened = double.NaN }, "INVALID_REQUEST")
        })
            yield return Case("finish validates " + vector.Name + " without I/O or correction replay", async () =>
            {
                await using var f = new Fixture();
                await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); f.Clock.Advance(10);
                await Error(() => s.FinishAsync(vector.End), vector.Code);
                await Error(() => s.FinishAsync(End()), "INSTANCE_MISMATCH");
                Equal(3, f.Calls);
            });
        yield return Case("zero heard time and seeks never become a full track", async () =>
        {
            await using var f = new Fixture();
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); f.Clock.Advance(5);
            await s.FinishAsync(End() with { Listened = 0, MediaListened = 0, Position = 200, Reason = "playend" });
            Equal("0", Text(f.Logs[1]["json"]!["time"]));
        });
        yield return Case("monotonic pause and wall clock jump retain only observed listening", async () =>
        {
            await using var f = new Fixture();
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            f.Clock.Advance(5 * 3600); f.Clock.Wall = DateTimeOffset.UnixEpoch;
            await s.FinishAsync(End() with { Listened = 12, MediaListened = 6, Rate = .5, Position = 180 });
            Equal("12", Text(f.Logs[1]["json"]!["time"])); Equal(5, f.Calls);
        });
        yield return Case("pre-cancellation is zero I/O", async () =>
        {
            await using var f = new Fixture(); using var token = new CancellationTokenSource(); token.Cancel();
            await Cancelled(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt), token.Token));
            Equal(0, f.Calls);
        });
        yield return Case("disposing an accepted idle session never fabricates end", async () =>
        {
            await using var f = new Fixture();
            var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            await s.DisposeAsync(); await s.DisposeAsync();
            await Error(() => s.FinishAsync(End()), "CLOSED");
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_ENDED");
            Equal(3, f.Calls); Equal(1, f.Logs.Count);
        });
        yield return Case("start cancellation drains a noncooperative physical handler and never retries", async () =>
        {
            await using var f = new Fixture(); using var token = new CancellationTokenSource();
            var entered = Signal(); var cancelled = Signal(); var release = Signal();
            f.Reply = async (request, ct, _) =>
            {
                using var registration = ct.Register(() => cancelled.TrySetResult());
                entered.SetResult(); await release.Task; return Default(request);
            };
            var start = f.Protocol.StartAsync(f.Account, Start(f.Attempt), token.Token); await entered.Task;
            token.Cancel(); await cancelled.Task;
            try { True(!start.IsCompleted, "transport must retain physical request until settled"); }
            finally { release.TrySetResult(); }
            await Cancelled(() => start);
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
            Equal(1, f.Calls);
        });
        yield return Case("protocol disposal drains ignored cancellation without disposing borrowed ports", async () =>
        {
            await using var f = new Fixture();
            var entered = Signal(); var cancelled = Signal(); var release = Signal();
            f.Reply = async (request, ct, _) =>
            {
                using var registration = ct.Register(() => cancelled.TrySetResult());
                entered.SetResult(); await release.Task; return Default(request);
            };
            var start = f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await entered.Task;
            var disposal = f.Protocol.DisposeAsync().AsTask(); await cancelled.Task;
            try { True(!disposal.IsCompleted && !start.IsCompleted); }
            finally { release.TrySetResult(); }
            await Error(() => start, "CLOSED"); await disposal;
            True(!f.Handler.Disposed); Equal(0, f.Accounts.Disposals); Equal(1, f.Calls);
        });
        yield return Case("session disposal drains ignored finish cancellation without fabricated retry", async () =>
        {
            await using var f = new Fixture();
            var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); f.Clock.Advance(10);
            var entered = Signal(); var cancelled = Signal(); var release = Signal();
            f.Reply = async (request, ct, count) =>
            {
                if (count == 5)
                {
                    using var registration = ct.Register(() => cancelled.TrySetResult());
                    entered.SetResult(); await release.Task;
                }
                return Default(request);
            };
            var finish = s.FinishAsync(End()); await entered.Task;
            var disposal = s.DisposeAsync().AsTask(); await cancelled.Task;
            try { True(!disposal.IsCompleted && !finish.IsCompleted); }
            finally { release.TrySetResult(); }
            await Error(() => finish, "CLOSED"); await disposal;
            await s.DisposeAsync(); Equal(5, f.Calls);
        });
        yield return Case("original token remains an invalidation fence after successful start", async () =>
        {
            await using var f = new Fixture(); using var token = new CancellationTokenSource();
            await using var s = await f.Protocol.StartAsync(f.Account, Start(f.Attempt), token.Token);
            token.Cancel(); f.Clock.Advance(10);
            await Cancelled(() => s.FinishAsync(End()));
            Equal(3, f.Calls);
        });
        yield return Case("duplicate waiter cancellation does not cancel original start", async () =>
        {
            await using var f = new Fixture(); using var token = new CancellationTokenSource();
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, count) =>
            {
                if (count == 1) { entered.SetResult(); await release.Task; }
                return Default(request);
            };
            var a = f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await entered.Task;
            var b = f.Protocol.StartAsync(f.Account, Start(f.Attempt), token.Token); token.Cancel();
            try { await Cancelled(() => b); True(!a.IsCompleted); }
            finally { release.TrySetResult(); }
            await using var s = await a; Equal(3, f.Calls);
        });
        yield return Case("QQ lacks verified device session so cannot fabricate registration or emit telemetry", async () =>
        {
            await using var f = new Fixture();
            var account = new MusicPlatformSession("tx", "synthetic-qq", 1, "123", "musicid=123; musickey=synthetic-key");
            await Error(() => f.Protocol.StartAsync(account, Start(f.Attempt) with { Recording = new("tx", "001FixtureMID") }), "DEVICE_CONTEXT_REQUIRED");
            Equal(0, f.Calls);
        });
        yield return Case("KG without native device cannot emit CSCC", async () =>
        {
            await using var f = new Fixture();
            var account = new MusicPlatformSession("kg", "synthetic-kg", 1, "123", "userid=123; token=synthetic-token");
            await Error(() => f.Protocol.StartAsync(account, Start(f.Attempt) with
                { Recording = new("kg", "ABCDEF0123456789ABCDEF0123456789", "477417208") }), "DEVICE_CONTEXT_REQUIRED");
            Equal(0, f.Calls);
        });
        yield return Case("KG valid device still cannot guess TLS or substitute grade sync for receipt", async () =>
        {
            await using var f = new Fixture();
            var account = new MusicPlatformSession("kg", "synthetic-kg", 1, "123",
                "userid=123; token=synthetic-token; KUGOU_API_MID=123456; KUGOU_API_GUID=0123456789abcdef0123456789abcdef; KUGOU_API_DEV=synthetic-device");
            await Error(() => f.Protocol.StartAsync(account, Start(f.Attempt) with
                { Recording = new("kg", "abcdef0123456789abcdef0123456789", "477417208") }), "UNVERIFIED_TLS_PROTOCOL");
            Equal(0, f.Calls);
        });
        yield return Case("cross-platform and credential identity mismatch do not send", async () =>
        {
            await using var f = new Fixture();
            var account = new MusicPlatformSession("tx", "synthetic-qq", 1, "124", "musicid=123; musickey=synthetic-key");
            await Error(() => f.Protocol.StartAsync(account, Start(f.Attempt) with { Recording = new("tx", "001FixtureMID") }), "INVALID_CREDENTIALS");
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { Recording = new("kg", new string('A', 32)) }), "IDENTITY_MISMATCH");
            Equal(0, f.Calls);
        });
        yield return Case("credential injection duplicate cookies and absent login are rejected before HTTP", async () =>
        {
            foreach (string cookie in new[] { "MUSIC_U=x\r\nHost: other.invalid", "MUSIC_U=x; MUSIC_U=y", "__csrf=synthetic" })
            {
                await using var f = new Fixture();
                var account = new MusicPlatformSession("wy", "synthetic-invalid-cookie", 1, "123", cookie);
                await Error(() => f.Protocol.StartAsync(account, Start(f.Attempt)), cookie.StartsWith("__csrf", StringComparison.Ordinal)
                    ? "AUTH_REQUIRED" : "INVALID_CREDENTIALS");
                Equal(0, f.Calls);
            }
        });
        foreach (var vector in new (HttpStatusCode Status, string Code)[]
        {
            (HttpStatusCode.Unauthorized, "AUTH_EXPIRED"), (HttpStatusCode.Forbidden, "FORBIDDEN"),
            (HttpStatusCode.TooManyRequests, "RATE_LIMITED")
        })
            yield return Case("HTTP " + (int)vector.Status + " never creates a session or retries", async () =>
            {
                await using var f = new Fixture();
                f.Reply = (_, _, _) => Task.FromResult(new HttpResponseMessage(vector.Status) { Content = new StringContent("{}") });
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), vector.Code);
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
                Equal(1, f.Calls);
            });
        yield return Case("unconfirmed timeout remains reserved rather than automatically repeated", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (request, _, _) => request.RequestUri!.AbsolutePath == ReportPath
                ? Task.FromException<HttpResponseMessage>(new OperationCanceledException())
                : Task.FromResult(Default(request));
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "TIMEOUT");
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
            Equal(3, f.Calls);
        });
        yield return Case("bounded response and redirect remain failures not successful receipts", async () =>
        {
            foreach (bool redirect in new[] { false, true })
            {
                await using var f = new Fixture();
                f.Reply = (request, _, _) =>
                {
                    if (request.RequestUri!.AbsolutePath != ReportPath) return Task.FromResult(Default(request));
                    var response = Response(redirect ? "{\"code\":200}" : new string(' ', 65537));
                    if (redirect) { response.StatusCode = HttpStatusCode.TemporaryRedirect; response.Headers.Location = new("https://example.invalid"); }
                    return Task.FromResult(response);
                };
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), redirect ? "REDIRECT_DENIED" : "LIMIT");
                Equal(3, f.Calls);
            }
        });
        yield return Case("feedback does not enlarge ordinary HTTP origin permissions", async () =>
        {
            await using var f = new Fixture();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://clientlog.music.163.com/eapi/feedback/weblog");
            True(!MusicPlatformHttp.IsAllowedOrigin("wy", request.RequestUri!));
            try { await f.Http.SendAsync("wy", request, CancellationToken.None); throw new Exception("origin accepted"); }
            catch (MusicPlatformException error) { Equal("ORIGIN_DENIED", error.Code); }
            Equal(0, f.Calls);
        });
        yield return Case("independent playback attempts do not accumulate receipts or exhaust a rate ledger", async () =>
        {
            await using var f = new Fixture();
            f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":200,\"profile\":null}"));
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "AUTH_EXPIRED");
            for (int i = 0; i < 80; i++)
                await Error(() => f.Protocol.StartAsync(f.Account, Start(new())), "AUTH_EXPIRED");
            Equal(81, f.Calls);
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "START_UNCONFIRMED");
            Equal(81, f.Calls);
        });
    }
    // Separate post-WY inventory: the QQ harness enumerates only this method.
    public static IEnumerable<TestCase> QqCases()
    {
        static TestCase Q(string name, Func<Task> run) => new("feedback-qq: " + name, run);
        yield return Q("idle construction owns no login devices or report work", async () =>
        {
            await using var f = new QqFixture();
            await f.Protocol.DisposeAsync();
            Equal(0, f.Accounts.Acquisitions); Equal(0, f.Calls); Equal(0, f.Accounts.Disposals);
            True(!f.Handler.Disposed);
        });
        foreach (var vector in new (string Account, double Heard)[] { ("101", 1), ("202", 9.75), ("101", 3.75) })
            yield return Q("fixed report account " + vector.Account + " wall " + vector.Heard, async () =>
            {
                await using var f = new QqFixture(vector.Account);
                var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                Equal(QqStart(f.Attempt).Recording, s.Recording); Equal(1, f.Calls); Equal(1, f.Accounts.Acquisitions);
                Equal(0, f.Reports.Count);
                f.Clock.Advance(10);
                var end = QqEnd() with { Listened = vector.Heard, MediaListened = vector.Heard * 2, Rate = 2, Position = 180 };
                await s.FinishAsync(end); await s.FinishAsync(end);
                Equal(4, f.Calls); Equal(2, f.Accounts.Acquisitions); Equal(1, f.Reports.Count);
                var xml = f.Reports.Single(); var root = xml.Root!; var item = root.Element("item")!;
                long seconds = (long)Math.Floor(vector.Heard);
                Equal(13, item.Attributes().Count()); Equal("1", (string?)item.Attribute("cmd"));
                Equal(vector.Account, (string?)item.Attribute("QQ")); Equal("synthetic-device-uid", (string?)item.Attribute("uid"));
                Equal("1", (string?)item.Attribute("songtype")); Equal("5", (string?)item.Attribute("playtype"));
                Equal(seconds.ToString(), (string?)item.Attribute("time"));
                Equal((seconds * 1000).ToString(), (string?)item.Attribute("play_duration_mi"));
                Equal("240000", (string?)item.Attribute("audiotime")); Equal("4321", (string?)item.Attribute("songid"));
                Equal("77", (string?)item.Attribute("singerid")); Equal("synthetic-vkey", (string?)item.Attribute("vkey"));
                string optime = f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
                Equal(optime, (string?)item.Attribute("optime"));
                Equal(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(optime + seconds + vector.Account + "gk2$Lh-&l4#!4iow"))),
                    (string?)item.Attribute("timekey"));
                Equal("228", root.Element("cid")!.Value); Equal(vector.Account, root.Element("qq")!.Value);
                Equal("synthetic-key-" + vector.Account, root.Element("authst")!.Value);
                Equal("synthetic-device-uid", root.Element("uid")!.Value); Equal("synthetic-private-sid", root.Element("sid")!.Value);
                Equal(f.Accounts.Device.Model, root.Element("phonetype")!.Value);
                Equal("10002_" + vector.Account + "_" + optime, root.Element("traceid")!.Value);
                foreach (string absent in new[] { "vip_level", "supersound", "nettype", "playpage_time", "play_speed", "source", "alg", "position" })
                    True(root.Element(absent) is null && item.Attribute(absent) is null);
                var vkey = f.Json.Single(x => Text(x["req_0"]!["method"]) == "UrlGetVkey");
                Equal(vector.Account, Text(vkey["req_0"]!["param"]!["uin"]));
                Equal("M500syntheticsynthetic.mp3", Text(vkey["req_0"]!["param"]!["filename"]![0]));
                True(Guid.TryParseExact(Text(vkey["req_0"]!["param"]!["guid"]), "N", out _));
                Equal(2, f.Accounts.Leases.Count(x => x.Disposed)); // Completion releases both verification and registration leases.
                await s.DisposeAsync(); Equal(2, f.Accounts.Leases.Count(x => x.Disposed));
                Equal(0, f.Accounts.Disposals); True(!f.Handler.Disposed);
            });
        yield return Q("fixed 86400-second wire vector does not fabricate session qualification", async () =>
        {
            await using var f = new QqFixture();
            using var request = await FeedbackProtocolQqRequests.Report(f.Account, f.Accounts.Device, new(4321, 240, 77),
                "synthetic-vkey", 86400, f.Clock.GetUtcNow().ToUnixTimeSeconds());
            var xml = ReadReport(await request.Content!.ReadAsByteArrayAsync());
            Equal("86400000", (string?)xml.Root!.Element("item")!.Attribute("play_duration_mi"));
            Equal("240000", (string?)xml.Root.Element("item")!.Attribute("audiotime"));
            Equal(0, f.Calls);
        });
        yield return Q("live login rejection is not anonymous registration and is never retried", async () =>
        {
            await using var f = new QqFixture(); f.Accounts.AuthError = "AUTH_REQUIRED";
            await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), "AUTH_EXPIRED");
            await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), "START_UNCONFIRMED");
            Equal(1, f.Accounts.Acquisitions); Equal(0, f.Calls); Equal(0, f.Accounts.Leases.Count);
        });
        yield return Q("context must retain the exact account reference rather than a cloned identity", async () =>
        {
            await using var f = new QqFixture();
            f.Accounts.ReturnAccount = new("tx", f.Account.Scope, f.Account.Generation, f.Account.UserId, f.Account.Cookie);
            await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), "SESSION_CHANGED");
            Equal(0, f.Calls); True(f.Accounts.Leases.Single().Disposed);
        });
        foreach (string error in new[] { "COMMIT_UNKNOWN", "DEVICE_CONTEXT_EXPIRED", "SESSION_CHANGED", "VERIFICATION_REQUIRED" })
            yield return Q("context stable expected error " + error, async () =>
            {
                await using var f = new QqFixture(); f.Accounts.AuthError = error;
                await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), error);
                Equal(0, f.Calls);
            });
        foreach (string bad in new[] { "qimei", "uid", "device", "future", "expired", "previous-day" })
            yield return Q("invalid context " + bad + " cannot register or synthesize device data", async () =>
            {
                await using var f = new QqFixture(); var d = f.Accounts.Device;
                f.Accounts.Device = QqDevice(f.Clock, qimei: bad == "qimei" ? "" : d.Qimei36,
                    uid: bad == "uid" ? "" : d.Uid, openUdid: bad == "device" ? "random" : d.OpenUdid,
                    qimeiAt: bad == "future" ? d.QimeiRetrievedAt + 1 : bad == "expired" ? d.QimeiRetrievedAt - 86400000 : d.QimeiRetrievedAt,
                    sessionAt: bad == "previous-day" ? d.SessionRetrievedAt - 86400000 : d.SessionRetrievedAt);
                await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)),
                    bad is "future" or "expired" or "previous-day" ? "DEVICE_CONTEXT_EXPIRED" : "DEVICE_CONTEXT_REQUIRED");
                Equal(0, f.Calls); True(f.Accounts.Leases.Single().Disposed);
            });
        yield return Q("concurrent registration shares one live verification and one exact track read", async () =>
        {
            await using var f = new QqFixture(); var entered = Signal(); var release = Signal();
            f.Accounts.AcquireWork = async (_, _) => { entered.TrySetResult(); await release.Task; };
            var first = f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); await entered.Task;
            var second = f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            Equal(1, f.Accounts.Acquisitions); Equal(0, f.Calls); True(!second.IsCompleted);
            release.TrySetResult();
            await using var a = await first; var b = await second; True(ReferenceEquals(a, b)); Equal(1, f.Calls);
        });
        foreach (var vector in new (string Name, string Tracks, string Code)[]
        {
            ("absent", "[]", "IDENTITY_MISMATCH"),
            ("duplicate", "[" + QqTrackJson + "," + QqTrackJson + "]", "IDENTITY_MISMATCH"),
            ("wrong-mid", "[" + QqTrackJson.Replace("synthetic", "other") + "]", "IDENTITY_MISMATCH"),
            ("zero-id", "[" + QqTrackJson.Replace("4321", "0") + "]", "IDENTITY_MISMATCH"),
            ("unknown-duration", "[" + QqTrackJson.Replace("240", "0") + "]", "IDENTITY_MISMATCH"),
            ("different-duration", "[" + QqTrackJson.Replace("240", "150") + "]", "IDENTITY_MISMATCH"),
            ("string-id", "[" + QqTrackJson.Replace("4321", "\"4321\"") + "]", "INVALID_RESPONSE")
        })
            yield return Q("registration rejects exact recording " + vector.Name, async () =>
            {
                await using var f = new QqFixture();
                f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"tracks\":" + vector.Tracks + "}}}"));
                await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), vector.Code);
                Equal(1, f.Calls); Equal(0, f.Reports.Count); True(f.Accounts.Leases.Single().Disposed);
            });
        yield return Q("account change after track receipt cannot return an accepted registration", async () =>
        {
            await using var f = new QqFixture();
            f.Reply = (request, _, _) => { f.Accounts.Current = false; return Task.FromResult(f.Default(request)); };
            await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), "SESSION_CHANGED");
            True(f.Accounts.Leases.Single().Disposed); Equal(1, f.Calls);
        });
        foreach (var vector in new (string Code, string Error)[]
        {
            ("\"0\"", "UPSTREAM_UNCONFIRMED"), ("false", "UPSTREAM_UNCONFIRMED"),
            ("0.0", "UPSTREAM_UNCONFIRMED"), ("2000", "SIGNATURE_REQUIRED"), ("2001", "RATE_LIMITED")
        })
            yield return Q("CGI code " + vector.Code + " never bypasses receipt validation", async () =>
            {
                await using var f = new QqFixture();
                f.Reply = (_, _, _) => Task.FromResult(Response("{\"code\":" + vector.Code +
                    ",\"req_0\":{\"code\":0,\"data\":{\"tracks\":[" + QqTrackJson + "]}}}"));
                await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), vector.Error);
                Equal(1, f.Calls); True(f.Accounts.Leases.Single().Disposed);
            });
        yield return Q("finish performs live login again without inheriting saved authentication", async () =>
        {
            await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            f.Clock.Advance(5); f.Accounts.AuthError = "AUTH_REQUIRED";
            await Error(() => s.FinishAsync(QqEnd()), "AUTH_EXPIRED"); await Error(() => s.FinishAsync(QqEnd()), "AUTH_EXPIRED");
            Equal(2, f.Accounts.Acquisitions); Equal(1, f.Calls);
        });
        yield return Q("expired device registration never renews itself to send an old report", async () =>
        {
            await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            f.Clock.Advance(86400);
            await Error(() => s.FinishAsync(QqEnd()), "DEVICE_CONTEXT_EXPIRED");
            Equal(1, f.Accounts.Acquisitions); Equal(1, f.Calls);
        });
        foreach (string field in new[] { "installation", "generation", "session", "qimei", "uid" })
            yield return Q("finish cannot substitute " + field + " from a newly acquired context", async () =>
            {
                await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Clock.Advance(5); var d = f.Accounts.Device;
                f.Accounts.NextDevice = new(field == "installation" ? "other-installation" : d.InstallationId,
                    field == "generation" ? d.DeviceGeneration + 1 : d.DeviceGeneration,
                    field == "session" ? d.SessionGeneration + 1 : d.SessionGeneration,
                    d.OpenUdid, d.OpenUdid2, d.AndroidId, d.OsVersion, d.Model,
                    field == "qimei" ? new string('B', 36) : d.Qimei36, d.QimeiRetrievedAt,
                    field == "uid" ? "different-uid" : d.Uid, d.Sid, d.SessionRetrievedAt);
                await Error(() => s.FinishAsync(QqEnd()), "SESSION_CHANGED");
                Equal(2, f.Accounts.Acquisitions); Equal(1, f.Calls); Equal(2, f.Accounts.Leases.Count(x => x.Disposed));
            });
        foreach (double seconds in new[] { 0d, .999 })
            yield return Q("sub-second listen " + seconds + " is not rounded up or retried", async () =>
            {
                await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Clock.Advance(5); var end = QqEnd() with { Listened = seconds, MediaListened = seconds };
                await Error(() => s.FinishAsync(end), "INSUFFICIENT_LISTEN_TIME");
                await Error(() => s.FinishAsync(end), "INSUFFICIENT_LISTEN_TIME");
                await Error(() => s.FinishAsync(QqEnd()), "INSTANCE_MISMATCH");
                Equal(1, f.Calls); Equal(1, f.Accounts.Acquisitions);
            });
        yield return Q("forged listened clock fails before a fresh context or report", async () =>
        {
            await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            await Error(() => s.FinishAsync(QqEnd() with { Listened = 50 }), "INVALID_LISTEN_TIME");
            Equal(1, f.Calls); Equal(1, f.Accounts.Acquisitions);
        });
        foreach (string changed in new[] { "id", "interval" })
            yield return Q("finish rechecks unchanged native " + changed + " before vkey", async () =>
            {
                await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Clock.Advance(5);
                f.Reply = (_, _, _) => Task.FromResult(Response(QqTracks(QqTrackJson.Replace(changed == "id" ? "4321" : "240",
                    changed == "id" ? "4322" : "241"))));
                await Error(() => s.FinishAsync(QqEnd()), "IDENTITY_MISMATCH"); Equal(2, f.Calls); Equal(0, f.Reports.Count);
            });
        foreach (var vector in new (string Name, string Rows)[]
        {
            ("denied", "[" + QqVkeyJson.Replace("\"result\":0", "\"result\":104003") + "]"),
            ("wrong-mid", "[" + QqVkeyJson.Replace("\"songmid\":\"synthetic\"", "\"songmid\":\"other\"") + "]"),
            ("empty", "[" + QqVkeyJson.Replace("synthetic-vkey", "") + "]"),
            ("trial", "[" + QqVkeyJson.Replace("M500", "RS02") + "]"),
            ("another-file", "[" + QqVkeyJson.Replace("syntheticsynthetic.mp3", "otherother.mp3") + "]"),
            ("ambiguous", "[" + QqVkeyJson + "," + QqVkeyJson + "]"),
            ("string-result", "[" + QqVkeyJson.Replace("\"result\":0", "\"result\":\"0\"") + "]")
        })
            yield return Q("same-account vkey rejects " + vector.Name + " without report or fallback", async () =>
            {
                await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Clock.Advance(5);
                f.Reply = (request, _, count) => Task.FromResult(count == 3
                    ? Response("{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":" + vector.Rows + "}}}") : f.Default(request));
                await Error(() => s.FinishAsync(QqEnd()), "VKEY_REQUIRED"); await Error(() => s.FinishAsync(QqEnd()), "VKEY_REQUIRED");
                Equal(3, f.Calls); Equal(0, f.Reports.Count);
            });
        foreach (string code in new[] { "false", "true", "\"0\"", "0.0", "0e0", "null", "-1", "200" })
            yield return Q("binary receipt non-integer-zero " + code + " never confirms or retries", async () =>
            {
                await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Clock.Advance(5);
                f.Reply = (request, _, count) => Task.FromResult(count == 4 ? QqBinary(QqAck("{\"code\":" + code + "}")) : f.Default(request));
                await Error(() => s.FinishAsync(QqEnd()), "UPSTREAM_UNCONFIRMED");
                await Error(() => s.FinishAsync(QqEnd()), "UPSTREAM_UNCONFIRMED"); Equal(4, f.Calls); Equal(1, f.Reports.Count);
            });
        foreach (string shape in new[] { "empty", "prefix-only", "bad-prefix", "not-zlib", "not-json", "missing-code", "duplicate-code", "truncated", "trailing", "inflated-limit" })
            yield return Q("malformed or unbounded receipt " + shape + " fails closed", async () =>
            {
                byte[] valid = QqAck("{\"code\":0}");
                byte[] bytes = shape switch
                {
                    "empty" => [], "prefix-only" => new byte[5],
                    "bad-prefix" => Encoding.ASCII.GetBytes("wrong").Concat(valid[5..]).ToArray(),
                    "not-zlib" => new byte[5].Concat("not-zlib"u8.ToArray()).ToArray(),
                    "not-json" => QqAck("not-json"), "missing-code" => QqAck("{}"),
                    "duplicate-code" => QqAck("{\"code\":1,\"code\":0}"),
                    "truncated" => valid[..^1], "trailing" => valid.Concat(new byte[] { 0 }).ToArray(),
                    _ => QqAck("{\"code\":0,\"padding\":\"" + new string('x', 65536) + "\"}")
                };
                await Error(() => { FeedbackProtocolQqReceipt.Verify(bytes, CancellationToken.None); return Task.CompletedTask; },
                    shape == "inflated-limit" ? "LIMIT" : "UPSTREAM_UNCONFIRMED");
            });
        yield return Q("XML escapes credential device and vkey without inventing nodes", async () =>
        {
            await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            f.Clock.Advance(5);
            const string value = "synthetic<&\"vkey";
            f.Reply = (request, _, count) => Task.FromResult(count == 3
                ? Response("{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[" +
                    QqVkeyJson.Replace("synthetic-vkey", "synthetic<&\\\"vkey") + "]}}}") : f.Default(request));
            await s.FinishAsync(QqEnd());
            Equal(value, (string?)f.Reports.Single().Root!.Element("item")!.Attribute("vkey"));
            Equal(f.Accounts.Device.Model, f.Reports.Single().Root!.Element("phonetype")!.Value);
            Equal(1, f.Reports.Single().Descendants("item").Count());
        });
        foreach (int step in new[] { 2, 3, 4 })
            yield return Q("account generation change after physical finish step " + step + " fences publication", async () =>
            {
                await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Clock.Advance(5);
                f.Reply = (request, _, count) => { if (count == step) f.Accounts.Current = false; return Task.FromResult(f.Default(request)); };
                await Error(() => s.FinishAsync(QqEnd()), "SESSION_CHANGED"); Equal(step, f.Calls);
            });
        yield return Q("device revocation cancels in-flight report but disposal waits physical handler", async () =>
        {
            await using var f = new QqFixture(); var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); f.Clock.Advance(5);
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, count) => { if (count == 4) { entered.TrySetResult(); await release.Task; } return f.Default(request); };
            var end = s.FinishAsync(QqEnd()); await entered.Task;
            f.Accounts.Leases[0].Revoke();
            var disposal = s.DisposeAsync().AsTask();
            try { True(!end.IsCompleted && !disposal.IsCompleted); Equal(0, f.Accounts.Leases.Count(x => x.Disposed)); }
            finally { release.TrySetResult(); }
            await Invalidated(() => end); await disposal; Equal(4, f.Calls); True(f.Accounts.Leases.All(x => x.Disposed));
        });
        yield return Q("concurrent ambiguous finishes share one physical report and preserve failure", async () =>
        {
            await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); f.Clock.Advance(5);
            var entered = Signal(); var release = Signal();
            f.Reply = async (request, _, count) =>
            {
                if (count == 4) { entered.TrySetResult(); await release.Task; return QqBinary(new byte[5]); }
                return f.Default(request);
            };
            var a = s.FinishAsync(QqEnd()); await entered.Task; var b = s.FinishAsync(QqEnd());
            Equal(4, f.Calls); release.TrySetResult();
            await Error(() => a, "UPSTREAM_UNCONFIRMED"); await Error(() => b, "UPSTREAM_UNCONFIRMED");
            await Error(() => s.FinishAsync(QqEnd() with { Listened = 4 }), "INSTANCE_MISMATCH"); Equal(4, f.Calls);
        });
        yield return Q("lost report response never causes a second vkey or report attempt", async () =>
        {
            await using var f = new QqFixture(); await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); f.Clock.Advance(5);
            f.Reply = (request, _, count) => count == 4 ? Task.FromException<HttpResponseMessage>(new OperationCanceledException())
                : Task.FromResult(f.Default(request));
            await Error(() => s.FinishAsync(QqEnd()), "TIMEOUT"); await Error(() => s.FinishAsync(QqEnd()), "TIMEOUT");
            Equal(4, f.Calls); Equal(2, f.Accounts.Acquisitions);
        });
        yield return Q("original lifetime cancellation retires accepted registration without final send", async () =>
        {
            await using var f = new QqFixture(); using var token = new CancellationTokenSource();
            await using var s = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt), token.Token);
            token.Cancel(); await Cancelled(() => s.FinishAsync(QqEnd())); Equal(1, f.Calls); Equal(1, f.Accounts.Acquisitions);
        });
        yield return Q("close waits noncooperative context acquisition and actual lease release", async () =>
        {
            await using var f = new QqFixture(); var entered = Signal(); var releaseAcquire = Signal();
            var releaseEntered = Signal(); var releaseDispose = Signal();
            f.Accounts.AcquireWork = async (_, _) => { entered.TrySetResult(); await releaseAcquire.Task; };
            f.Accounts.ReleaseWork = async _ => { releaseEntered.TrySetResult(); await releaseDispose.Task; };
            var start = f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); await entered.Task;
            var close = f.Protocol.DisposeAsync().AsTask(); True(!close.IsCompleted);
            releaseAcquire.TrySetResult(); await releaseEntered.Task;
            try { True(!close.IsCompleted && !start.IsCompleted); Equal(0, f.Calls); }
            finally { releaseDispose.TrySetResult(); }
            await Invalidated(() => start); await close; Equal(0, f.Accounts.Disposals); True(!f.Handler.Disposed);
        });
        yield return Q("protocol close surfaces lease release failure without hanging or disposing borrowed ports", async () =>
        {
            var f = new QqFixture();
            try
            {
                _ = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
                f.Accounts.ReleaseWork = _ => Task.FromException(new MusicPlatformException("RELEASE_FAILED"));
                await Error(() => f.Protocol.DisposeAsync().AsTask(), "RELEASE_FAILED");
                await Error(() => f.Protocol.DisposeAsync().AsTask(), "RELEASE_FAILED");
                Equal(0, f.Accounts.Disposals); True(!f.Handler.Disposed); Equal(1, f.Calls);
            }
            finally { f.Http.Dispose(); }
        });
    }

    private const string QqTrackJson = "{\"mid\":\"synthetic\",\"id\":4321,\"interval\":240,\"singer\":[{\"id\":77}]}";
    private const string QqVkeyJson = "{\"songmid\":\"synthetic\",\"filename\":\"M500syntheticsynthetic.mp3\",\"result\":0,\"vkey\":\"synthetic-vkey\"}";
    private static string QqTracks(string track) => "{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"tracks\":[" + track + "]}}}";
    private static MusicFeedbackStart QqStart(MusicFeedbackAttempt attempt) => new(attempt, new("tx", "synthetic"), 240, 0, 1, false);
    private static MusicFeedbackEnd QqEnd() => new(5, 5, 240, 5, 1, "ui");
    private static byte[] QqAck(string json)
    {
        using var output = new MemoryStream(); output.Write(new byte[5]);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }
    private static HttpResponseMessage QqBinary(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static XDocument ReadReport(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return XDocument.Load(gzip);
    }
    private static async Task Invalidated(Func<Task> work)
    {
        try { await work(); } catch (OperationCanceledException) { return; }
        catch (MusicFeedbackException error) when (error.Code is "SESSION_CHANGED" or "CLOSED" or "DEVICE_CONTEXT_EXPIRED") { return; }
        throw new Exception("Expected explicit invalidation");
    }
    private static MusicQqFeedbackDeviceSnapshot QqDevice(TimeProvider clock, string? qimei = null, string? uid = null,
        string? openUdid = null, long? qimeiAt = null, long? sessionAt = null) => new("synthetic-installation", 1, 1,
            openUdid ?? "0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210", "123456789abcdef0",
            "12", "Synthetic & \"Device\" <B>", qimei ?? "0123456789abcdef0123456789abcdef0123",
            qimeiAt ?? clock.GetUtcNow().ToUnixTimeMilliseconds(), uid ?? "synthetic-device-uid", "synthetic-private-sid",
            sessionAt ?? clock.GetUtcNow().ToUnixTimeMilliseconds());
    private sealed class QqClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(GetTimestamp());
        internal void Advance(double seconds) => Interlocked.Add(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
    private sealed class QqAccounts(MusicPlatformSession account, TimeProvider clock) : IMusicPlatformSessionSource, IMusicQqFeedbackContextSource, IDisposable
    {
        internal bool Current = true;
        internal int Acquisitions, Disposals;
        internal string? AuthError;
        internal MusicPlatformSession? ReturnAccount;
        internal MusicQqFeedbackDeviceSnapshot Device = QqDevice(clock);
        internal MusicQqFeedbackDeviceSnapshot? NextDevice;
        internal readonly List<QqLease> Leases = [];
        internal Func<int, CancellationToken, Task>? AcquireWork;
        internal Func<QqLease, Task>? ReleaseWork;
        public bool IsCurrent(MusicPlatformSession value) => Current && ReferenceEquals(value, account);
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token) =>
            throw new Exception("Feedback cannot reacquire arbitrary account");
        public async ValueTask<IMusicQqFeedbackContextLease> AcquireForAccountAsync(MusicPlatformSession exactAccount, CancellationToken token)
        {
            True(ReferenceEquals(exactAccount, account));
            int count = Interlocked.Increment(ref Acquisitions);
            if (AcquireWork is not null) await AcquireWork(count, token);
            if (AuthError is not null) throw new MusicPlatformException(AuthError);
            var lease = new QqLease(this, ReturnAccount ?? account, count > 1 ? NextDevice ?? Device : Device, token);
            Leases.Add(lease); return lease;
        }
        public void Dispose() => Disposals++;
    }
    private sealed class QqLease(QqAccounts owner, MusicPlatformSession account, MusicQqFeedbackDeviceSnapshot device,
        CancellationToken token) : IMusicQqFeedbackContextLease
    {
        private readonly CancellationTokenSource revoked = CancellationTokenSource.CreateLinkedTokenSource(token);
        private Task? disposal;
        internal bool Disposed;
        public MusicPlatformSession Account => account;
        public bool IsCurrent => owner.Current && !Disposed && !revoked.IsCancellationRequested;
        public CancellationToken Revoked => revoked.Token;
        public MusicQqFeedbackDeviceSnapshot Device => device;
        internal void Revoke() => revoked.Cancel();
        public ValueTask DisposeAsync() => new(disposal ??= Release());
        private async Task Release()
        {
            Disposed = true; await revoked.CancelAsync();
            try { if (owner.ReleaseWork is not null) await owner.ReleaseWork(this); }
            finally { revoked.Dispose(); }
        }
    }
    private sealed class QqHandler(QqFixture owner) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => owner.Send(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class QqFixture : IAsyncDisposable
    {
        internal readonly MusicFeedbackAttempt Attempt = new();
        internal readonly MusicPlatformSession Account;
        internal readonly QqAccounts Accounts;
        internal readonly QqClock Clock = new();
        internal readonly QqHandler Handler;
        internal readonly MusicPlatformHttp Http;
        internal readonly NativeMusicFeedbackProtocol Protocol;
        internal readonly MusicClientContextFixture Clients;
        internal readonly List<JsonObject> Json = [];
        internal readonly List<XDocument> Reports = [];
        internal Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>>? Reply;
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal QqFixture(string id = "101")
        {
            Account = new("tx", "synthetic-qq-scope", 1, id,
                "musicid=" + id + "; str_musicid=" + id + "; musickey=synthetic-key-" + id + "; login_type=2");
            Accounts = new(Account, Clock); Handler = new(this); Http = MusicPlatformHttp.CreateWithHandler(Handler);
            Clients = new(Http, clock: Clock); Protocol = new(Accounts, Http, Clients.Owner, Clock);
        }
        internal HttpResponseMessage Default(HttpRequestMessage request)
        {
            if (request.RequestUri!.Host == "stat6.y.qq.com") return QqBinary(QqAck("{\"code\":0}"));
            return Text(Json.Last()["req_0"]!["method"]) == "CgiGetTrackInfo" ? Response(QqTracks(QqTrackJson))
                : Response("{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[" + QqVkeyJson + "]}}}");
        }
        internal async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            int count = Interlocked.Increment(ref calls);
            Equal("https", request.RequestUri!.Scheme); Equal("", request.RequestUri.Query); Equal(HttpMethod.Post, request.Method);
            Equal("uin=" + Account.UserId + "; qqmusic_uin=" + Account.UserId + "; qm_keyst=synthetic-key-" + Account.UserId +
                "; qqmusic_key=synthetic-key-" + Account.UserId, request.Headers.GetValues("Cookie").Single());
            if (request.RequestUri.Host == "stat6.y.qq.com")
            {
                Equal("/android/fcgi-bin/imusic_tj", request.RequestUri.AbsolutePath);
                Equal("gzip", request.Content!.Headers.ContentEncoding.Single());
                Reports.Add(ReadReport(await request.Content.ReadAsByteArrayAsync()));
            }
            else
            {
                Equal("u.y.qq.com", request.RequestUri.Host); Equal("/cgi-bin/musicu.fcg", request.RequestUri.AbsolutePath);
                var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                Json.Add(json); Equal(2, json.Count);
                Equal(Account.UserId, Text(json["comm"]!["qq"]));
                Equal("synthetic-key-" + Account.UserId, Text(json["comm"]!["authst"]));
                Equal("synthetic-device-uid", Text(json["comm"]!["uid"]));
                Equal(Accounts.Device.OpenUdid, Text(json["comm"]!["OpenUDID"]));
                if (Text(json["req_0"]!["method"]) == "CgiGetTrackInfo")
                {
                    Equal("music.trackInfo.UniformRuleCtrl", Text(json["req_0"]!["module"]));
                    Equal("[0]", json["req_0"]!["param"]!["types"]!.ToJsonString());
                    Equal("[0]", json["req_0"]!["param"]!["modify_stamp"]!.ToJsonString());
                    Equal("synthetic", Text(json["req_0"]!["param"]!["mids"]![0]));
                }
                else Equal("music.vkey.GetVkey", Text(json["req_0"]!["module"]));
            }
            return Reply is null ? Default(request) : await Reply(request, token, count);
        }
        public async ValueTask DisposeAsync()
        {
            await Protocol.DisposeAsync(); await Clients.DisposeAsync(); Http.Dispose();
        }
    }

    private static MusicFeedbackStart Start(MusicFeedbackAttempt attempt) => new(attempt, new("wy", "42"), 200, 0, 1, false);
    private static MusicFeedbackEnd End() => new(5, 5, 200, 5, 1, "ui");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
    private static void True(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static async Task Error(Func<Task> work, string code)
    {
        try { await work(); }
        catch (MusicFeedbackException error)
        {
            Equal(code, error.Code); Equal(code, error.Message);
            True(error.InnerException is null && !error.ToString().Contains("synthetic-private", StringComparison.Ordinal));
            return;
        }
        throw new Exception("Expected feedback error " + code);
    }
    private static async Task AnyFeedbackError(Func<Task> work)
    {
        try { await work(); } catch (MusicFeedbackException) { return; }
        throw new Exception("Expected feedback failure");
    }
    private static async Task Cancelled(Func<Task> work)
    {
        try { await work(); } catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation");
    }
    private static HttpResponseMessage Response(string json, string? cookie = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (cookie is not null) response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }
    private static HttpResponseMessage Default(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        IdentityPath => Response("{\"code\":200,\"profile\":{\"userId\":123,\"nickname\":\"synthetic\"},\"account\":{\"id\":123}}"),
        SongPath => Response("{\"code\":200,\"songs\":[{\"id\":42,\"dt\":200000}]}"),
        ReportPath => Response("{\"code\":200}"),
        _ => throw new Exception("Unexpected endpoint")
    };
    private sealed class FakeClock : TimeProvider
    {
        private long ticks;
        internal DateTimeOffset Wall = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => Wall + TimeSpan.FromTicks(GetTimestamp());
        internal void Advance(double seconds) => Interlocked.Add(ref ticks, (long)(seconds * TimeSpan.TicksPerSecond));
    }
    private sealed class Accounts : IMusicPlatformSessionSource, IDisposable
    {
        internal bool Current = true;
        internal int Acquisitions, Disposals;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        { Acquisitions++; throw new Exception("Protocol must use supplied exact account"); }
        public bool IsCurrent(MusicPlatformSession session) => Current;
        public void Dispose() => Disposals++;
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
        internal readonly FakeClock Clock = new();
        internal readonly MusicPlatformSession Account = new("wy", "synthetic-account-a", 1, "123", "MUSIC_U=synthetic-session; __csrf=synthetic-csrf");
        internal readonly Handler Handler;
        internal readonly MusicPlatformHttp Http;
        internal readonly NativeMusicFeedbackProtocol Protocol;
        internal readonly MusicClientContextFixture Clients;
        internal readonly List<JsonObject> Logs = [];
        internal Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>>? Reply;
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal Fixture()
        {
            Handler = new(this); Http = MusicPlatformHttp.CreateWithHandler(Handler);
            Clients = new(Http, clock: Clock); Protocol = new(Accounts, Http, Clients.Owner, Clock);
        }
        internal async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            int count = Interlocked.Increment(ref calls);
            Equal("https", request.RequestUri!.Scheme); Equal(HttpMethod.Post, request.Method);
            Equal("", request.RequestUri.Query);
            if (request.RequestUri.AbsolutePath == ReportPath)
            {
                Equal("clientlog.music.163.com", request.RequestUri.Host);
                Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                string encoded = await request.Content.ReadAsStringAsync();
                True(encoded.StartsWith("params=", StringComparison.Ordinal));
                var cipher = Convert.FromHexString(Uri.UnescapeDataString(encoded[7..]));
                using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
                string plain = Encoding.UTF8.GetString(aes.DecryptEcb(cipher, PaddingMode.PKCS7));
                var fields = plain.Split("-36cd479b6b5-");
                Equal(3, fields.Length); Equal("/api/feedback/weblog", fields[0]);
                Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{fields[0]}use{fields[1]}md5forencrypt"))), fields[2]);
                var data = JsonNode.Parse(fields[1])!.AsObject();
                Equal("synthetic-session", Text(data["header"]!["MUSIC_U"]));
                var logs = JsonNode.Parse(data["logs"]!.GetValue<string>())!.AsArray();
                Equal(1, logs.Count); lock (Logs) Logs.Add(logs[0]!.AsObject());
                True(!data.ContainsKey("url") && !data.ContainsKey("cookie"));
            }
            else Equal("music.163.com", request.RequestUri.Host);
            return Reply is null ? Default(request) : await Reply(request, token, count);
        }
        public async ValueTask DisposeAsync()
        {
            await Protocol.DisposeAsync();
            await Clients.DisposeAsync();
            Http.Dispose();
        }
    }
}
