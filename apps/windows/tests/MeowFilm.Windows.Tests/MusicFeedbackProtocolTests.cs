// SPDX-License-Identifier: GPL-3.0-only
// Synthetic HTTP only. WY uses the confirmed NCBL playback contract.
// Existing QQ/KG vectors retain their independent protocol coverage.
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

public static partial class MusicFeedbackProtocolTests
{
    public static IEnumerable<TestCase> Cases() => NeteaseCases();
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
}
