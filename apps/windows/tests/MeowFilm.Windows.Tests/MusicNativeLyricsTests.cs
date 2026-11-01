// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

// Synthetic HTTP/text only; no Node, real accounts, upstream or reference execution.
public static class MusicNativeLyricsTests
{
    private const string Hash = "ABCDEF0123456789ABCDEF0123456789";
    private const string Access = "0123456789abcdef0123456789abcdef"; // synthetic candidate capability
    private const string Mid = "midFixture1", Id = "9007199254740993123";
    private static readonly NativeLyricRequest Wy = new("wy", "12345"), Tx = new("tx", Mid), Kg = new("kg", Hash, 180, "42");
    private static TestCase Case(string name, Func<Task> run) => new("native lyrics: " + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("contract borrows dependencies constructor has no IO", async () =>
        {
            using var f = new Fixture((_, _) => Task.FromResult(WyReply("[00:01.000]fixture")));
            INativeMusicLyrics port = f.Service; Equal(0, f.Calls); Equal(0, f.Sessions.Acquires);
            port.Invalidate(); Equal(0, f.Calls);
            var payload = await port.ReadAsync(Wy, default);
            Equal("wy", payload!.Platform); Equal(Wy.RecordingId, payload.RecordingId); Equal("", payload.Romanization);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/api/test");
            await f.Http.SendAsync("wy", request, default); Equal(2, f.Calls);
        });
        yield return Case("wy fixed EAPI flags YRC first YTLRC clock", async () =>
        {
            using var f = new Fixture((req, _) =>
            {
                Equal("/eapi/song/lyric/v1", req.RequestUri!.AbsolutePath);
                var body = Eapi(req); Equal(Wy.RecordingId, S(body["id"])); Equal("false", S(body["cp"]));
                foreach (var key in new[] { "tv", "lv", "rv", "kv", "yv", "ytv", "yrv" }) Equal("0", S(body[key]));
                Equal("false", S(body["e_r"])); True(body["header"]!["MUSIC_U"] is null);
                return Task.FromResult(Json(new { code = 200, yrc = new { lyric = "[1000,300](1000,300,0)甲" },
                    lrc = new { lyric = "[00:04]wrong-clock" }, ytlrc = new { lyric = "[00:01.000]译" }, tlyric = new { lyric = "[00:04]wrong-translation" } }));
            });
            var p = await f.Read(Wy); Equal("[1000,300](1000,300,0)甲", p!.Text); Equal("[00:01.000]译", p.Translation);
        });
        yield return Case("wy anonymous identity and confirmed client updates are reused without granting login", async () =>
        {
            var sent = new List<JsonObject>();
            using var f = new Fixture((req, _) =>
            {
                sent.Add(Eapi(req));
                var response = WyReply("[00:01]fixture");
                response.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
                    "NMTID=SYNTHETIC_LYRIC_NMTID; Domain=music.163.com",
                    "MUSIC_U=SYNTHETIC_RESPONSE_AUTH; Domain=music.163.com" });
                return Task.FromResult(response);
            });
            var before = await f.Clients.Owner.AcquireWyAsync();
            await f.Read(Wy);
            Equal(before.DeviceId, S(sent[0]["header"]!["deviceId"]));
            Equal(before.Nuid, S(sent[0]["header"]!["_ntes_nuid"]));
            var after = await f.Clients.Owner.AcquireWyAsync();
            Equal("SYNTHETIC_LYRIC_NMTID", after.Nmtid); Equal(before.Generation + 1, after.Generation);
            True(f.Clients.Owner.IsCurrent(before) && f.Clients.Owner.IsCurrent(after));
            await f.Read(Wy);
            Equal(after.Nmtid, S(sent[1]["header"]!["NMTID"])); True(sent[1]["header"]!["MUSIC_U"] is null);
            True(ReferenceEquals(after, await f.Clients.Owner.AcquireWyAsync()));
            True(f.Sessions.Current.GetValueOrDefault("wy") is null); Equal(2, f.Calls);
        });
        yield return Case("wy foreign account association never mixes or rewrites the installation", async () =>
        {
            using var f = new Fixture((req, _) =>
            {
                var header = Eapi(req)["header"]!;
                Equal("SYNTHETIC_FOREIGN_ID", S(header["deviceId"]));
                Equal("SYNTHETIC_FOREIGN_NMTID", S(header["NMTID"]));
                True(header["_ntes_nuid"] is null && header["WNMCID"] is null);
                var response = WyReply("[00:01]fixture");
                response.Headers.TryAddWithoutValidation("Set-Cookie",
                    "NMTID=SYNTHETIC_FOREIGN_UPDATED; Domain=music.163.com");
                return Task.FromResult(response);
            });
            var before = await f.Clients.Owner.AcquireWyAsync();
            var account = Session("wy");
            f.Sessions.Current["wy"] = new("wy", account.Scope, account.Generation, account.UserId,
                account.Cookie + "; deviceId=SYNTHETIC_FOREIGN_ID; NMTID=SYNTHETIC_FOREIGN_NMTID");
            await f.Read(Wy);
            True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync())); Equal(1, f.Calls);
        });
        foreach (var (raw, code) in new[] {
            ("""{"code":301,"lrc":{"lyric":"[00:01]fixture"}}""", "AUTH_REQUIRED"),
            ("""{"code":200,"id":"222","lrc":{"lyric":"[00:01]fixture"}}""", "IDENTITY_MISMATCH"),
            ("""{"code":200,"lrc":{"lyric":true}}""", "INVALID_RESPONSE") })
            yield return Case("wy invalid business or recording response cannot persist client fields " + code, async () =>
            {
                using var f = new Fixture((_, _) =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) };
                    response.Headers.TryAddWithoutValidation("Set-Cookie",
                        "NMTID=SYNTHETIC_UNTRUSTED; Domain=music.163.com");
                    return Task.FromResult(response);
                });
                var before = await f.Clients.Owner.AcquireWyAsync();
                await Error(code, () => f.Read(Wy));
                True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync())); Equal(1, f.Calls);
            });
        foreach (var (cookie, code) in new[] {
            ("deviceId=SYNTHETIC_WRONG_ID; Domain=music.163.com", "IDENTITY_MISMATCH"),
            ("NMTID=SYNTHETIC_WRONG_ORIGIN; Domain=example.invalid", "INVALID_RESPONSE"),
            ("MUSIC_U=; Max-Age=0; Domain=music.163.com", "AUTH_EXPIRED") })
            yield return Case("wy rejected response context cannot publish lyrics " + code, async () =>
            {
                using var f = new Fixture((_, _) =>
                {
                    var response = WyReply("[00:01]fixture");
                    response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
                    return Task.FromResult(response);
                });
                var before = await f.Clients.Owner.AcquireWyAsync();
                await Error(code, () => f.Read(Wy));
                True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync())); Equal(1, f.Calls);
            });
        yield return Case("wy client replacement fences late lyrics and response updates", async () =>
        {
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture((_, _) => release.Task);
            var pending = f.Read(Wy);
            try
            {
                await Until(() => f.Calls == 1);
                await f.Clients.Owner.ReloadAsync();
            }
            finally
            {
                var response = WyReply("[00:01]late");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_LATE; Domain=music.163.com");
                release.TrySetResult(response);
            }
            await Error("STORAGE_CONFLICT", () => pending);
            True((await f.Clients.Owner.AcquireWyAsync()).Nmtid is null); Equal(1, f.Calls);
        });
        yield return Case("wy LRC fallback does not take unrelated YTLRC", async () =>
        {
            using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = 200, yrc = new { lyric = "untimed" },
                lrc = new { lyric = "[00:04]原" }, ytlrc = new { lyric = "[00:01]错" }, tlyric = new { lyric = "[00:04]译" } })));
            var p = await f.Read(Wy); Equal("[00:04]原", p!.Text); Equal("[00:04]译", p.Translation);
        });
        yield return Case("wy fallback translation does not rewrite timestamps", async () =>
        {
            using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = 200, yrc = new { lyric = "[1000,300](1000,300,0)原" },
                ytlrc = new { lyric = "untimed" }, tlyric = new { lyric = "[00:04]译" } })));
            Equal("[00:04]译", (await f.Read(Wy))!.Translation);
        });
        foreach (var text in new[] { "", "pure music", "[ti:fixture]" })
            yield return Case("wy absent timed lyric " + text.Length, async () =>
            { using var f = new Fixture((_, _) => Task.FromResult(WyReply(text))); True(await f.Read(Wy) is null); });
        yield return Case("wy explicit ID mismatch is failure", async () =>
        { using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = 200, id = 222, lrc = new { lyric = "[00:01]x" } }))); await Error("IDENTITY_MISMATCH", () => f.Read(Wy)); });
        yield return Case("wy invalid field is not absence", async () =>
        { using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = 200, yrc = new { lyric = 123 } }))); await Error("INVALID_RESPONSE", () => f.Read(Wy)); });
        foreach (var pair in new[] { (301, "AUTH_REQUIRED"), (403, "FORBIDDEN"), (460, "VERIFICATION_REQUIRED"), (509, "RATE_LIMITED"), (999, "UPSTREAM_ERROR") })
            yield return Case("wy business status " + pair.Item1, async () =>
            { using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = pair.Item1, message = "private-body" }))); await Error(pair.Item2, () => f.Read(Wy)); });
        yield return Case("QQ exact MID numeric ID type then three QRC tracks", async () =>
        {
            var qrc = QrcFixture.Encode("<QrcInfos><LyricInfo LyricContent=\"[1000,300]甲(1000,300)&#10;[2000,300]乙&amp;丙(2000,300)\"/></QrcInfos>");
            using var f = new Fixture((req, _) =>
            {
                var body = Body(req); var call = body["req_0"]!; var args = call["param"]!;
                Equal("11", S(body["comm"]!["ct"]));
                if (S(call["method"]) == "CgiGetTrackInfo")
                {
                    Equal("music.trackInfo.UniformRuleCtrl", S(call["module"])); Equal(Mid, S(args["mids"]![0]));
                    True(args["ids"] is null); return Task.FromResult(QqDetail());
                }
                Equal("music.musichallSong.PlayLyricInfo", S(call["module"])); Equal("GetPlayLyricInfo", S(call["method"]));
                Equal(Mid, S(args["songMid"])); True(args["songId"] is null);
                foreach (var key in new[] { "crypt", "type", "qrc", "trans", "roma" }) Equal("1", S(args[key]));
                Equal("false", S(args["needSingingAnnotations"]));
                return Task.FromResult(QqData(new { songID = Id, lyric = qrc,
                    trans = QrcFixture.Encode("[00:01.000]译文"), roma = QrcFixture.Encode("[00:01.000]jia") }));
            });
            var p = await f.Read(Tx); Equal(Mid, p!.RecordingId); Equal("[1000,300]甲(1000,300)\n[2000,300]乙&丙(2000,300)", p.Text);
            Equal("[00:01.000]译文", p.Translation); Equal("[00:01.000]jia", p.Romanization); Equal(2, f.Calls);
        });
        foreach (var mode in new[] { "wrong-mid", "no-id", "special-type", "duplicate" })
            yield return Case("QQ metadata rejects " + mode, async () =>
            {
                using var f = new Fixture((_, _) => Task.FromResult(mode switch {
                    "wrong-mid" => QqDetail("another"), "no-id" => QqDetail(id: "0"), "special-type" => QqDetail(type: 2),
                    _ => QqData(new { tracks = new[] { new { mid = Mid, id = Id, type = 1 }, new { mid = Mid, id = Id, type = 1 } } }) }));
                await Error(mode == "no-id" ? "INVALID_RESPONSE" : "IDENTITY_MISMATCH", () => f.Read(Tx)); Equal(1, f.Calls);
            });
        yield return Case("QQ lyric numeric ID mismatch no fallback", async () =>
        { using var f = QqFixture(new { songID = "123", lyric = "[00:01]x" }); await Error("IDENTITY_MISMATCH", () => f.Read(Tx)); });
        yield return Case("QQ decoded text accepted timestamps are not encoding flags", async () =>
        {
            using var f = QqFixture(new { songID = Id, lyric = "[00:01]原", trans = "[00:01]译", roma = "[00:01]yuan", qrc_t = 999999, lrc_t = 123 });
            Equal("[00:01]yuan", (await f.Read(Tx))!.Romanization);
        });
        yield return Case("QQ encrypted untimed and empty lyrics return null", async () =>
        {
            using var f = QqFixture(new { songID = Id, lyric = QrcFixture.Encode("[ti:fixture]"), trans = "", roma = "" }); True(await f.Read(Tx) is null);
            using var empty = QqFixture(new { songID = Id, lyric = "" }); True(await empty.Read(Tx) is null);
        });
        var invalidQq = new[] { "invalid-hex", "01", new string('0', 16), "WzAwOjAxXXg=", "<x LyricContent=\"[00:01]a\" LyricContent=\"[00:01]b\"/>", "<!DOCTYPE x><x LyricContent=\"[00:01]x\"/>" };
        for (int i = 0; i < invalidQq.Length; i++)
        {
            var invalid = invalidQq[i];
            yield return Case("QQ invalid encoding " + i, async () =>
            { using var f = QqFixture(new { songID = Id, lyric = invalid }); await Error("INVALID_RESPONSE", () => f.Read(Tx)); });
        }
        yield return Case("QQ damaged optional translation is explicit failure", async () =>
        { using var f = QqFixture(new { songID = Id, lyric = "[00:01]a", trans = "not-hex" }); await Error("INVALID_RESPONSE", () => f.Read(Tx)); });
        yield return Case("QQ signature failure never retries", async () =>
        { using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = 2000 }))); await Error("SIGNATURE_REQUIRED", () => f.Read(Tx)); Equal(1, f.Calls); });
        yield return Case("QQ decompression bomb bounded", async () =>
        { using var f = QqFixture(new { songID = Id, lyric = QrcFixture.Encode(new string('x', 262145)) }); await Error("LIMIT", () => f.Read(Tx)); });
        yield return Case("QQ checksum truncation trailing payload rejected", async () =>
        {
            foreach (var mode in new[] { "checksum", "truncated", "trailing" })
            {
                var bytes = Compress("[00:01]fixture");
                if (mode == "checksum") bytes[^1] ^= 1; else if (mode == "truncated") bytes = bytes[..^2]; else bytes = [.. bytes, 7, 8];
                using var f = QqFixture(new { songID = Id, lyric = QrcFixture.EncryptCompressed(bytes) }); await Error("INVALID_RESPONSE", () => f.Read(Tx));
            }
        });
        yield return Case("KG exact hash signing ID KRC words and aligned language", async () =>
        {
            var language = Language(new[] { new[] { "译一" }, new[] { "" }, new[] { "译二" } });
            var krc = "[offset:-250]\n[language:" + language + "]\n[1000,500]<0,250,0>甲<250,250,0>乙\n[1750,0]\n[2000,300]<0,300,0>丙";
            using var f = new Fixture((req, _) =>
            {
                Equal("lyrics.kugou.com", req.RequestUri!.Host); var query = Query(req); VerifyKgSignature(query); True(!req.Headers.Contains("Cookie"));
                if (req.RequestUri.AbsolutePath == "/v1/search")
                {
                    Equal(Hash, query["hash"]); Equal("180000", query["duration"]); Equal("42", query["album_audio_id"]);
                    Equal("", query["keyword"]); Equal("1", query["lrctxt"]); Equal("no", query["man"]);
                    Equal(9, query.Count); True(!query.ContainsKey("token")); True(!query.ContainsKey("mid")); return Task.FromResult(KgSearch());
                }
                Equal("/download", req.RequestUri.AbsolutePath); Equal("17", query["id"]); Equal(Access, query["accesskey"]);
                Equal("krc", query["fmt"]); Equal("utf8", query["charset"]); Equal("android", query["client"]); return Task.FromResult(KgDownload(Krc(krc)));
            });
            var p = await f.Read(Kg); Equal(Hash, p!.RecordingId);
            Equal("[offset:-250]\n[1000,500](1000,250,0)甲(1250,250,0)乙\n[1750,0]\n[2000,300](2000,300,0)丙", p.Text);
            Equal("[offset:-250]\n[00:01.000]译一\n[00:02.000]译二", p.Translation); Equal("", p.Romanization);
        });
        yield return Case("KG defaults and hash canonical wire representation", async () =>
        {
            using var f = new Fixture((req, _) =>
            {
                var q = Query(req); Equal(Hash, q["hash"]); Equal("0", q["duration"]); Equal("0", q["album_audio_id"]);
                return Task.FromResult(Json(new { status = 404, errcode = 404, candidates = Array.Empty<object>() }));
            });
            True(await f.Read(new("kg", Hash.ToLowerInvariant())) is null); Equal(1, f.Calls);
        });
        yield return Case("KG empty or ambiguous candidates never choose first", async () =>
        {
            foreach (var n in new[] { 0, 2 })
            {
                using var f = new Fixture((_, _) => Task.FromResult(Json(new { status = 200, errcode = 200, candidates = Enumerable.Range(0, n).Select(_ => Candidate()).ToArray() })));
                True(await f.Read(Kg) is null); Equal(1, f.Calls);
            }
        });
        foreach (var field in new[] { "hash", "album_audio_id", "duration", "contenttype", "id", "accesskey" })
            yield return Case("KG candidate checks " + field, async () =>
            {
                var row = Candidate(); row[field] = field switch { "hash" => "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "album_audio_id" => "43",
                    "duration" => "999999", "contenttype" => "2", "id" => "0", _ => "bad" };
                using var f = new Fixture((_, _) => Task.FromResult(Json(new { status = 200, errcode = 200, candidates = new[] { row } })));
                await Error(field is "hash" or "album_audio_id" or "duration" ? "IDENTITY_MISMATCH" : "INVALID_RESPONSE", () => f.Read(Kg)); Equal(1, f.Calls);
            });
        yield return Case("KG hash-only search may omit redundant candidate hash", async () =>
        {
            var row = Candidate(); row.Remove("hash"); row.Remove("album_audio_id");
            using var f = new Fixture((req, _) => Task.FromResult(req.RequestUri!.AbsolutePath == "/v1/search"
                ? Json(new { status = 200, errcode = 200, candidates = new[] { row } }) : KgDownload(Krc("[1000,300]<0,300,0>甲"))));
            True(await f.Read(Kg) is not null);
        });
        foreach (var field in new[] { "id", "fmt", "contenttype", "hash" })
            yield return Case("KG downloaded response checks " + field, async () =>
            {
                var row = Download(Krc("[1000,300]甲")); row[field] = field switch { "id" => "18", "fmt" => "lrc", "contenttype" => "1", _ => "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
                using var f = KgFixture(row); await Error(field is "id" or "hash" ? "IDENTITY_MISMATCH" : "INVALID_RESPONSE", () => f.Read(Kg));
            });
        yield return Case("KG auth verification explicit errors", async () =>
        {
            foreach (var error in new[] { 20018, 20028 })
            { using var f = new Fixture((_, _) => Task.FromResult(Json(new { status = 0, errcode = error }))); await Error(error == 20018 ? "AUTH_REQUIRED" : "VERIFICATION_REQUIRED", () => f.Read(Kg)); }
        });
        yield return Case("KG compressed header checksum UTF8 truncation and trailing bytes", async () =>
        {
            var normal = Convert.FromBase64String(Krc("[1000,300]甲")); normal[0] = 0;
            var badsum = Compress("[1000,300]甲"); badsum[^1] ^= 1;
            var fixtures = new[] { Convert.ToBase64String(normal), WrapKrc(badsum), WrapKrc(Compress("[1000,300]甲")[..^2]),
                WrapKrc([.. Compress("[1000,300]甲"), 0, 0]), WrapKrc(CompressBytes([0xff, 0xff])), "invalid-base64" };
            foreach (var content in fixtures) { using var f = KgFixture(Download(content)); await Error("INVALID_RESPONSE", () => f.Read(Kg)); }
        });
        yield return Case("KG decompression bomb and converted segment limit", async () =>
        {
            using var bomb = KgFixture(Download(Krc(new string('x', 262145)))); await Error("LIMIT", () => bomb.Read(Kg));
            using var large = KgFixture(Download(Krc("[1000,300]" + new string('x', 65536)))); await Error("LIMIT", () => large.Read(Kg));
        });
        yield return Case("KG malformed timing explicit error", async () =>
        {
            foreach (var text in new[] { "[9007199254740991,1]a", "[1000,300]prefix<0,300,0>a", "[1000,300]<0,300,0>a(1,2,3)", "[1,broken]a" })
            { using var f = KgFixture(Download(Krc(text))); await Error("INVALID_RESPONSE", () => f.Read(Kg)); }
        });
        yield return Case("KG malformed misaligned or injected language keeps original", async () =>
        {
            foreach (var language in new[] { "eA==", Language(new[] { new[] { "too" }, new[] { "many" } }), Language(new[] { new[] { "[00:01]injected" } }) })
            {
                using var f = KgFixture(Download(Krc("[language:" + language + "]\n[1000,300]<0,300,0>原")));
                var p = await f.Read(Kg); Equal("[1000,300](1000,300,0)原", p!.Text); Equal("", p.Translation);
            }
        });
        yield return Case("KG duplicate effective clocks or offsets suppress translation", async () =>
        {
            foreach (var prefix in new[] { "[offset:-2000]", "[offset:0]\n[offset:0]" })
            {
                using var f = KgFixture(Download(Krc(prefix + "\n[language:" + Language(new[] { new[] { "一" }, new[] { "二" } }) + "]\n[1000,100]甲\n[2000,100]乙")));
                Equal("", (await f.Read(Kg))!.Translation);
            }
        });
        yield return Case("KG empty decoded lyric returns null", async () =>
        { using var f = KgFixture(Download(Krc("[ti:fixture]\n[1000,0]"))); True(await f.Read(Kg) is null); });
        var invalidRequests = new[] { new NativeLyricRequest("kw", "x"), new("wy", "0"), new("wy", "123\n"), new("tx", "a/b"),
            new("kg", new string('0', 32)), new("kg", Hash, double.NaN), new("kg", Hash, -1), new("kg", Hash, 0, "../1") };
        for (int i = 0; i < invalidRequests.Length; i++)
        {
            var request = invalidRequests[i];
            yield return Case("invalid target before session acquisition " + i, async () =>
            { using var f = new Fixture((_, _) => throw new Exception("unexpected")); await Error("INVALID_REQUEST", () => f.Read(request)); Equal(0, f.Calls); Equal(0, f.Sessions.Acquires); });
        }
        yield return Case("no cache persistence or automatic replay", async () =>
        { using var f = new Fixture((_, _) => Task.FromResult(WyReply("[00:01]x"))); await f.Read(Wy); await f.Read(Wy); Equal(2, f.Calls); });
        yield return Case("borrowed session scoped credentials reused", async () =>
        {
            using var f = new Fixture((req, _) => { var p = Eapi(req); Equal("fixture-value", S(p["header"]!["MUSIC_U"])); return Task.FromResult(WyReply("[00:01]x")); });
            f.Sessions.Current["wy"] = Session("wy"); await f.Read(Wy); True(f.Sessions.Acquires >= 4); True(f.Sessions.Current["wy"] is not null);
        });
        yield return Case("session changed between QQ steps blocks second request", async () =>
        {
            using var f = new Fixture((_, _) => Task.FromResult(QqDetail())); f.Sessions.Current["tx"] = Session("tx");
            f.Handler.OnSent = () => f.Sessions.Current["tx"] = Session("tx", 2);
            await Error("STALE_SESSION", () => f.Read(Tx)); Equal(1, f.Calls);
        });
        yield return Case("anonymous to authenticated change fenced", async () =>
        {
            using var f = new Fixture((_, _) =>
            {
                var response = WyReply("[00:01]x");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_RETIRED; Domain=music.163.com");
                return Task.FromResult(response);
            });
            var before = await f.Clients.Owner.AcquireWyAsync();
            f.Handler.OnSent = () => f.Sessions.Current["wy"] = Session("wy");
            await Error("STALE_SESSION", () => f.Read(Wy));
            True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync())); Equal(1, f.Calls);
        });
        yield return Case("credentials unavailable never downgrade to guest", async () =>
        {
            using var f = new Fixture((_, _) => throw new Exception("unexpected")); f.Sessions.Read = (_, _) => throw new Exception("secret raw source message");
            await Error("CREDENTIALS_UNAVAILABLE", () => f.Read(Wy)); Equal(0, f.Calls);
        });
        yield return Case("wrong-platform session rejected before HTTP", async () =>
        { using var f = new Fixture((_, _) => throw new Exception("unexpected")); f.Sessions.Current["wy"] = Session("kg"); await Error("IDENTITY_MISMATCH", () => f.Read(Wy)); Equal(0, f.Calls); });
        yield return Case("caller cancellation before and during HTTP", async () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            using var f = new Fixture(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return WyReply(""); });
            await Error("CANCELLED", () => f.Service.ReadAsync(Wy, cts.Token)); Equal(0, f.Calls);
            using var during = new CancellationTokenSource(); var pending = f.Service.ReadAsync(Wy, during.Token);
            await Until(() => f.Calls == 1); during.Cancel(); await Error("CANCELLED", () => pending);
        });
        yield return Case("invalidate only target platform and reject late response", async () =>
        {
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture((_, _) => release.Task);
            var pending = f.Read(Wy);
            try
            {
                await Until(() => f.Calls == 1); f.Service.Invalidate("kg"); True(!pending.IsCompleted);
                f.Service.Invalidate("wy"); await Task.Delay(30); True(!pending.IsCompleted);
            }
            finally { release.TrySetResult(WyReply("[00:01]late")); }
            await Error("STALE_SESSION", () => pending);
            await Until(() => f.Handler.Physical == 0);
        });
        yield return Case("cancelled HTTP drains without blocking another lyric read", async () =>
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture(async (_, _) => { await release.Task; return WyReply("[00:01]late"); }); using var cts = new CancellationTokenSource();
            var a = f.Service.ReadAsync(Wy, cts.Token); var b = f.Service.ReadAsync(Wy, cts.Token);
            try
            {
                await Until(() => f.Calls == 2); cts.Cancel(); await Task.Delay(30);
                True(!a.IsCompleted && !b.IsCompleted);
                var c = f.Read(Wy); await Until(() => f.Calls == 3);
                release.TrySetResult(true); await c;
            }
            finally { release.TrySetResult(true); }
            await Error("CANCELLED", () => a); await Error("CANCELLED", () => b);
            await Until(() => f.Handler.Physical == 0);
        });
        yield return Case("noncooperative session reads remain owned without a shared request quota", async () =>
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture((_, _) => Task.FromResult(WyReply("[00:01]x"))); int physical = 0;
            f.Sessions.Read = async (_, _) => { Interlocked.Increment(ref physical); try { await release.Task; return null; } finally { Interlocked.Decrement(ref physical); } };
            using var cts = new CancellationTokenSource();
            var reads = Enumerable.Range(0, 20).Select(_ => f.Service.ReadAsync(Wy, cts.Token)).ToArray();
            try
            {
                await Until(() => Volatile.Read(ref physical) == 20); cts.Cancel(); await Task.Delay(30);
                True(reads.All(read => !read.IsCompleted));
                Equal(0, f.Calls);
            }
            finally { release.TrySetResult(true); }
            foreach (var read in reads) await Error("CANCELLED", () => read);
            await Until(() => Volatile.Read(ref physical) == 0);
        });
        foreach (var pair in new[] { (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"), (429, "RATE_LIMITED"), (302, "REDIRECT_DENIED"), (500, "UPSTREAM_ERROR") })
            yield return Case("HTTP fixed failure " + pair.Item1, async () =>
            { using var f = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)pair.Item1) { Content = new StringContent("secret-body") })); await Error(pair.Item2, () => f.Read(Wy)); Equal(1, f.Calls); });
        yield return Case("HTTP verification header sanitized", async () =>
        {
            using var f = new Fixture((_, _) => { var r = WyReply("[00:01]x"); r.Headers.Add("ssa-code", "private-challenge"); return Task.FromResult(r); });
            await Error("VERIFICATION_REQUIRED", () => f.Read(Wy));
        });
        yield return Case("HTTP raw exception never exposed", async () =>
        { using var f = new Fixture((_, _) => throw new IOException("Cookie=private-value; accesskey=private")); await Error("NETWORK_ERROR", () => f.Read(Wy)); });
        var invalidJson = new[] { "{\"code\":200,\"code\":301}", "[]", "{", "{\"code\":200,\"lrc\":{\"lyric\":true}}" };
        for (int i = 0; i < invalidJson.Length; i++)
        {
            var raw = invalidJson[i];
            yield return Case("strict JSON response " + i, async () =>
            { using var f = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) })); await Error("INVALID_RESPONSE", () => f.Read(Wy)); });
        }
        yield return Case("lyric-sized strings remain valid in both native lyrics and ordinary JSON responses", async () =>
        {
            var text = "[00:01]" + new string('x', 60000); using var f = new Fixture((_, _) => Task.FromResult(WyReply(text)));
            Equal(text, (await f.Read(Wy))!.Text);
            var ordinary = await f.Http.SendAsync("wy", new(HttpMethod.Get, "https://music.163.com/api/test"), default);
            Equal(text, ordinary.Body["lrc"]!["lyric"]!.GetValue<string>());
        });
        yield return Case("decoded segment declared and streamed response caps", async () =>
        {
            using var huge = new Fixture((_, _) => Task.FromResult(WyReply("[00:01]" + new string('x', 65536)))); await Error("LIMIT", () => huge.Read(Wy));
            using var raw = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1048577]) })); await Error("LIMIT", () => raw.Read(Wy));
            using var chunked = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekBytes(new byte[1048577])) })); await Error("LIMIT", () => chunked.Read(Wy));
        });
        yield return Case("fixed origins deny arbitrary paths and cross-platform", async () =>
        {
            using var f = new Fixture((_, _) => Task.FromResult(Json(new { code = 200 })));
            foreach (var url in new[] { "http://lyrics.kugou.com/download", "https://lyrics.kugou.com:444/download", "https://lyrics.kugou.com/other",
                "https://lyrics.kugou.com/download/extra", "https://lyrics.kugou.com/v1/search/more", "https://user@lyrics.kugou.com/download", "https://lyrics.kugou.com/download#fragment", "https://lyrics.kugou.com.evil.test/download" })
                await Error("ORIGIN_DENIED", () => f.Http.SendAsync("kg", new(HttpMethod.Get, url), default));
            await Error("ORIGIN_DENIED", () => f.Http.SendAsync("tx", new(HttpMethod.Get, "https://lyrics.kugou.com/download"), default)); Equal(0, f.Calls);
            True(MusicPlatformHttp.IsAllowedOrigin("kg", new("https://lyrics.kugou.com/v1/search"))); True(MusicPlatformHttp.IsAllowedOrigin("kg", new("https://lyrics.kugou.com/download")));
        });
        yield return Case("single 25 second deadline includes session and HTTP", async () =>
        {
            using var f = new Fixture(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return WyReply(""); }); var first = true;
            f.Sessions.Read = async (_, token) => { if (first) { first = false; await Task.Delay(TimeSpan.FromSeconds(10), token); } return null; };
            var watch = Stopwatch.StartNew(); await Error("TIMEOUT", () => f.Read(Wy));
            True(watch.Elapsed >= TimeSpan.FromSeconds(23) && watch.Elapsed < TimeSpan.FromSeconds(30)); Equal(1, f.Calls);
        });
    }
    private sealed class Sessions : IMusicPlatformSessionSource
    {
        internal readonly Dictionary<string, MusicPlatformSession?> Current = [];
        internal Func<string, CancellationToken, ValueTask<MusicPlatformSession?>>? Read;
        internal int Acquires;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string p, CancellationToken ct)
        { Interlocked.Increment(ref Acquires); return Read is null ? ValueTask.FromResult(Current.GetValueOrDefault(p)) : Read(p, ct); }
        public bool IsCurrent(MusicPlatformSession s) => ReferenceEquals(Current.GetValueOrDefault(s.Platform), s);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls, Physical; internal Action? OnSent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Interlocked.Increment(ref Physical);
            try { var r = await send(request, ct); OnSent?.Invoke(); return r; } finally { Interlocked.Decrement(ref Physical); }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Sessions Sessions = new(); internal readonly Handler Handler;
        internal readonly MusicPlatformHttp Http; internal readonly NativeMusicLyrics Service;
        internal readonly MusicClientContextFixture Clients;
        internal int Calls => Handler.Calls;
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        { Handler = new(handler); Http = MusicPlatformHttp.CreateWithHandler(Handler); Clients = new(Http); Service = new(Sessions, Http, Clients.Owner); }
        internal Task<NativeLyricPayload?> Read(NativeLyricRequest request) => Service.ReadAsync(request, default);
        public void Dispose() { Service.Invalidate(); Clients.Dispose(); Http.Dispose(); }
    }
    private static MusicPlatformSession Session(string platform, long generation = 1) => new(platform, "fixture-scope", generation, "10001",
        platform switch { "wy" => "MUSIC_U=fixture-value; __csrf=fixture-csrf", "tx" => "musicid=10001; musickey=fixture-value; login_type=2",
            _ => "userid=10001; token=fixture-value; KUGOU_API_MID=12345; dfid=fixture-dfid" });
    private static Fixture QqFixture(object lyrics) => new((req, _) => Task.FromResult(S(Body(req)["req_0"]!["method"]) == "CgiGetTrackInfo" ? QqDetail() : QqData(lyrics)));
    private static Fixture KgFixture(JsonObject download) => new((req, _) => Task.FromResult(req.RequestUri!.AbsolutePath == "/v1/search" ? KgSearch() : Json(download)));
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage WyReply(string text) => Json(new { code = 200, lrc = new { lyric = text } });
    private static HttpResponseMessage QqData(object data) => Json(new { code = 0, req_0 = new { code = 0, data } });
    private static HttpResponseMessage QqDetail(string mid = Mid, string id = Id, int type = 1) => QqData(new { tracks = new[] { new { mid, id, type } } });
    private static JsonObject Candidate() => new() { ["id"] = "17", ["accesskey"] = Access, ["contenttype"] = 0, ["duration"] = 180000, ["hash"] = Hash, ["album_audio_id"] = "42" };
    private static HttpResponseMessage KgSearch() => Json(new { status = 200, errcode = 200, candidates = new[] { Candidate() } });
    private static JsonObject Download(string value) => new() { ["status"] = 200, ["error_code"] = 0, ["id"] = "17", ["fmt"] = "krc", ["contenttype"] = 0, ["content"] = value };
    private static HttpResponseMessage KgDownload(string content) => Json(Download(content));
    private static string Language(string[][] text) => Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = 1, content = new[] { new { type = 1, lyricContent = text } } }));
    private static string S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
    private static JsonObject Body(HttpRequestMessage request) => JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    private static Dictionary<string, string> Form(string text) => text.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(
        p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
    private static Dictionary<string, string> Query(HttpRequestMessage request) => Form(request.RequestUri!.Query);
    private static JsonObject Eapi(HttpRequestMessage request)
    {
        var form = Form(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()); using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
        var raw = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(form["params"]), PaddingMode.PKCS7)).Split("-36cd479b6b5-");
        Equal("/api/song/lyric/v1", raw[0]); Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("nobody" + raw[0] + "use" + raw[1] + "md5forencrypt"))), raw[2]); return JsonNode.Parse(raw[1])!.AsObject();
    }
    private static void VerifyKgSignature(Dictionary<string, string> query)
    {
        const string salt = "OIlwieks28dk2k092lksi2UIkp";
        var raw = string.Concat(query.Where(p => p.Key != "signature").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(salt + raw + salt))), query["signature"]);
    }
    private static byte[] Compress(string text) => CompressBytes(Encoding.UTF8.GetBytes(text));
    private static byte[] CompressBytes(byte[] bytes)
    {
        using var output = new MemoryStream(); using (var zip = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zip.Write(bytes); return output.ToArray();
    }
    private static string Krc(string text) => WrapKrc(Compress(text));
    private static string WrapKrc(byte[] bytes)
    {
        byte[] key = [64,71,97,119,94,50,116,71,81,54,49,45,206,210,110,105];
        return Convert.ToBase64String([.. "krc1"u8.ToArray(), .. bytes.Select((b, i) => (byte)(b ^ key[i % 16]))]);
    }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(31)); }
        catch (MusicPlatformException e) { Equal(code, e.Code); Equal(e.Code, e.Message); True(e.InnerException is null); Equal("MusicPlatformException: " + code, e.ToString()); return; }
        throw new InvalidOperationException("Expected error " + code);
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Value mismatch: " + expected + " != " + actual); }
    private static async Task Until(Func<bool> condition)
    { var started = Stopwatch.StartNew(); while (!condition()) { if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Fixture did not settle"); await Task.Delay(5); } }
    private sealed class NonSeekBytes(byte[] bytes) : Stream
    {
        private int position;
        public override int Read(byte[] buffer, int offset, int count) { var n = Math.Min(count, bytes.Length - position); bytes.AsSpan(position, n).CopyTo(buffer.AsSpan(offset)); position += n; return n; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Independently structured bit-array encryptor, synthetic vectors only.
    // Fixed QQMusicApi table attribution: GPL-3.0-only;
    // Copyright (c) 2024 沉默の金 <cmzj@cmzj.org>. See LICENSES/NOTICE.md.
    private static class QrcFixture
    {
        private static readonly byte[][] Boxes = [
            [14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13],
            [15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9],
            [10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12],
            [7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14],
            [2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3],
            [12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13],
            [4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12],
            [13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11],

        ];
        private static readonly int[] PC1 = [56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35,62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3];
        private static readonly int[] PC2 = [13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31];
        private static readonly int[] E = [31,0,1,2,3,4,3,4,5,6,7,8,7,8,9,10,11,12,11,12,13,14,15,16,15,16,17,18,19,20,19,20,21,22,23,24,23,24,25,26,27,28,27,28,29,30,31,0];
        private static readonly int[] P = [15,6,19,20,28,11,27,16,0,14,22,25,4,17,30,9,1,7,23,13,31,26,2,8,18,12,29,5,21,10,3,24];
        private static readonly int[] IP = (from bit in new[] { 6,4,2,0,7,5,3,1 } from b in new[] { 4,5,6,7,0,1,2,3 } select b * 8 + 7 - bit).ToArray();
        private static bool[] Bits(byte[] data) => data.SelectMany(b => Enumerable.Range(0, 8).Select(i => (b & (1 << (7 - i))) != 0)).ToArray();
        private static bool[][] Schedule(byte[] key, bool reverse)
        {
            var bits = Bits([.. key.Take(4).Reverse(), .. key.Skip(4).Take(4).Reverse()]);
            var c = PC1.Take(28).Select(i => bits[i]).ToArray(); var d = PC1.Skip(28).Select(i => bits[i]).ToArray(); var result = new List<bool[]>();
            foreach (var shift in new[] { 1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1 })
            {
                c = [.. c.Skip(shift), .. c.Take(shift)]; d = [.. d.Skip(shift), .. d.Take(shift)];
                result.Add(PC2.Take(24).Select(i => c[i]).Concat(PC2.Skip(24).Select(i => i - 27 < 28 && d[i - 27])).ToArray());
            }
            return (reverse ? result.AsEnumerable().Reverse() : result).ToArray();
        }
        private static byte[] Block(byte[] input, bool[][] keys)
        {
            var bits = Bits(input); var permuted = IP.Select(i => bits[i]).ToArray(); var l = permuted[..32]; var r = permuted[32..];
            foreach (var key in keys)
            {
                var expanded = E.Select((i, k) => r[i] ^ key[k]).ToArray(); var substituted = new List<bool>();
                for (int i = 0; i < 8; i++)
                {
                    var six = expanded.Skip(i * 6).Take(6).ToArray(); int row = (six[0] ? 2 : 0) + (six[5] ? 1 : 0);
                    int col = 0; for (int j = 1; j < 5; j++) col = col * 2 + (six[j] ? 1 : 0);
                    var n = Boxes[i][row * 16 + col]; substituted.AddRange(Enumerable.Range(0, 4).Select(j => (n & (1 << (3 - j))) != 0));
                }
                var next = P.Select((index, k) => l[k] ^ substituted[index]).ToArray(); l = r; r = next;
            }
            var end = r.Concat(l).ToArray(); var original = new bool[64]; for (int i = 0; i < 64; i++) original[IP[i]] = end[i];
            return Enumerable.Range(0, 8).Select(i => (byte)original.Skip(i * 8).Take(8).Aggregate(0, (n, b) => n * 2 + (b ? 1 : 0))).ToArray();
        }
        internal static string Encode(string text) => EncryptCompressed(Compress(text));
        internal static string EncryptCompressed(byte[] bytes)
        {
            var padded = new byte[(bytes.Length + 7) / 8 * 8]; bytes.CopyTo(padded, 0); var key = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");
            var schedules = new[] { Schedule(key[..8], false), Schedule(key[8..16], true), Schedule(key[16..], false) };
            for (int i = 0; i < padded.Length; i += 8)
            { var block = padded[i..(i + 8)]; foreach (var schedule in schedules) block = Block(block, schedule); block.CopyTo(padded, i); }
            return Convert.ToHexString(padded);
        }
    }
}
