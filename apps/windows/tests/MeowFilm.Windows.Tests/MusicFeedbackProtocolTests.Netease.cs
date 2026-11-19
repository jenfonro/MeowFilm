using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicFeedbackProtocolTests
{
    private static readonly Guid Playback = Guid.Parse("00000000-0000-4000-8000-000000000042");
    private static readonly DateTimeOffset Occurred = DateTimeOffset.FromUnixTimeMilliseconds(1800000000123);
    private static MusicFeedbackStart Start(MusicFeedbackAttempt attempt) => new(attempt, new("wy", "42"), 200, 0, 1, false)
    { PlaybackId = Playback, StartedAt = Occurred.AddSeconds(-3), AdmittedAt = Occurred };
    private static MusicFeedbackEnd End() => new(5, 5, 200, 5, 1, "ui")
    { PlaybackId = Playback, EndedAt = Occurred.AddSeconds(10) };
    private static TestCase Case(string name, Func<Task> run) => new("feedback-protocol: " + name, run);

    private static IEnumerable<TestCase> NeteaseCases()
    {
        yield return Case("construction and disposal neither acquire borrowed accounts nor fabricate end", async () => {
            await using var f = await Fixture.Create();
            await f.Protocol.DisposeAsync(); await f.Protocol.DisposeAsync();
            Equal(0, f.Calls); Equal(0, f.Accounts.Acquisitions); Equal(0, f.Accounts.Disposals);
            _ = await f.Runtime.Client.RequestAsync("wy", "feedback.qualify", new { }, f.Account, default);
            Equal(1, f.Calls);
        });
        yield return Case("FM semantic facts retain original occurrence time and exact fractional listening", async () => {
            await using var f = await Fixture.Create();
            var input = Start(f.Attempt) with { Source = "userfm", Mode = "playFm", Algorithm = "fixture-alg" };
            await using var session = await f.Protocol.StartAsync(f.Account, input); await f.Protocol.DrainAsync();
            var start = f.Uploads.Single();
            Equal("start", start.GetProperty("event").GetString());
            Equal(input.AdmittedAt.ToUnixTimeMilliseconds() / 1000d, start.GetProperty("occurredAt").GetDouble());
            Equal(0d, start.GetProperty("listened").GetDouble()); Equal(0d, start.GetProperty("mediaListened").GetDouble());
            var target = start.GetProperty("target"); Equal("42", target.GetProperty("nativeId").GetString());
            Equal("userfm", target.GetProperty("source").GetString()); Equal("fixture-alg", target.GetProperty("algorithm").GetString());
            var end = End() with { Listened = 8.75, MediaListened = 17.5, Rate = 2, Reason = "playend", Position = 193.25 };
            await session.FinishAsync(end); await session.FinishAsync(end); await f.Protocol.DrainAsync();
            Equal(2, f.Uploads.Count); Equal(0, f.Saved().Length);
            var terminal = f.Uploads[1]; Equal("end", terminal.GetProperty("event").GetString());
            Equal(8.75, terminal.GetProperty("listened").GetDouble()); Equal(17.5, terminal.GetProperty("mediaListened").GetDouble());
            Equal(2d, terminal.GetProperty("rate").GetDouble()); Equal("playend", terminal.GetProperty("reason").GetString());
            Equal(end.EndedAt.ToUnixTimeMilliseconds() / 1000d, terminal.GetProperty("occurredAt").GetDouble());
            True(!terminal.TryGetProperty("cookie", out _) && !terminal.TryGetProperty("MUSIC_U", out _));
        });
        foreach (var vector in new (string Source, string Id)[] {
            ("list", "7"), ("album", "8"), ("search", " 原始关键词 & 歌手 "),
            ("dailySongRecommend", ""), ("song", ""), ("userfm", ""),
            ("search_song_detail_page_song_rec", ""), ("queue-source-fixture", ""), ("unknown", "") })
            yield return Case("original provenance " + vector.Source, () => {
                var input = Start(new()) with { Source = vector.Source, SourceId = vector.Id, Algorithm = "occurrence-alg" };
                var pending = NeteasePlaybackTransport.Start(Account(), input, 1);
                Equal(vector.Source == "unknown" ? null : vector.Source, pending.Source);
                Equal(vector.Id.Length == 0 ? null : vector.Id, pending.SourceId); Equal("occurrence-alg", pending.Algorithm);
                return Task.CompletedTask;
            });
        foreach (string source in new[] { "search_song_detail_page_song_rec", "playlist", "likeMusic", "queue-source-fixture", "moreRcmd" })
            yield return Case("queue sourceMap remains exact " + source, () => {
                var origin = Origin("queue", "queue-recommendations", source);
                var pending = NeteasePlaybackTransport.Start(Account(), Start(new()) with { Source = source,
                    IsQueueRecommendation = true, Algorithm = origin.Algorithm, Recommendation = origin }, 1);
                Equal(source, pending.Source); Equal("occurrence-alg", pending.Algorithm); True(pending.SourceId is null);
                return Task.CompletedTask;
            });
        yield return Case("heartbeat scene and per-song upstream attribution never invent a local source ID", () => {
            var origin = Origin("heartbeat", "heartbeat", "") with { SourceType = "playlist", SourceId = "700" };
            var input = Start(new()) with { Source = "aiMode", SourceId = "700", Mode = "ai", Algorithm = origin.Algorithm, Recommendation = origin };
            var pending = NeteasePlaybackTransport.Start(Account(), input, 1);
            Equal("heartbeat", pending.PlayScene); True(pending.Source is null && pending.SourceId is null);
            Equal("occurrence-alg", pending.Algorithm);
            var mapped = NeteasePlaybackTransport.Start(Account(), input with { Recommendation = origin with {
                Provider = "queue-recommendations", FeedbackSource = "upstream-heart", Metadata = JsonSerializer.SerializeToElement(new { feedbackSourceId = "upstream-42" }) } }, 1);
            Equal("upstream-heart", mapped.Source); Equal("upstream-42", mapped.SourceId);
            var similar = NeteasePlaybackTransport.Start(Account(), Start(new()) with { Recommendation = Origin("similar", "similar", "") }, 1);
            True(similar.Source is null); return Task.CompletedTask;
        });
        foreach (string code in new[] { "AUTH_EXPIRED", "IDENTITY_MISMATCH", "VERIFICATION_REQUIRED" })
            yield return Case("canonical live qualification failure prevents upload " + code, async () => {
                await using var f = await Fixture.Create(); f.Reply = (_, _, _) => throw new MusicPlatformException(code);
                await Error(() => f.Protocol.QualifyAccountAsync(f.Account, default), code);
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                Equal(code, f.Protocol.LastError); Equal(0, f.Saved().Length); Equal(0, f.Uploads.Count);
                int calls = f.Calls;
                await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(calls, f.Calls);
                await Error(() => session.FinishAsync(End()), "START_UNCONFIRMED");
            });
        yield return Case("Unicode scalar provenance survives durable admission upload and current cold read", async () => {
            await using var f = await Fixture.Create(); f.Offline = true;
            var input = Start(f.Attempt) with { Source = "source\n", SourceId = "keyword\n & 原样", Algorithm = "算法\n🙂" };
            await using var session = await f.Protocol.StartAsync(f.Account, input); await f.Protocol.DrainAsync();
            var pending = f.Saved().Single();
            Equal(input.Source, pending.Source); Equal(input.SourceId, pending.SourceId); Equal(input.Algorithm, pending.Algorithm);
            Equal(0, f.Uploads.Count); await f.Protocol.DisposeAsync();
            await using var settings = new MusicFeedbackSettingsOwner(f.Store); await settings.ReadAsync();
            await using var cold = new NativeMusicFeedbackProtocol(f.Accounts, f.Runtime.Client, f.Clock, f.Store, settings);
            f.Offline = false; await cold.InitializeAsync(); await cold.DrainAsync();
            var target = f.Uploads.Single().GetProperty("target");
            Equal(input.Recording.Id, target.GetProperty("nativeId").GetString());
            Equal(input.Source, target.GetProperty("source").GetString());
            Equal(input.SourceId, target.GetProperty("sourceId").GetString());
            Equal(input.Algorithm, target.GetProperty("algorithm").GetString());
            Equal(0, f.Saved().Length); True(cold.LastError is null);
        });
        foreach (var vector in new (string RecordingId, string Kind, string Source)[] {
            ("123456789012345678", "playlist", "list"), ("recording\n/" + new string('i', 80), "album", "album") })
            yield return Case("controller canonical " + vector.Kind + " retains long ID and raw attribution through cold outbox", async () => {
                await using var f = await Fixture.Create(); int qualifications = 0;
                f.Reply = (call, _, _) => {
                    if (call.Operation == "feedback.qualify" && ++qualifications > 1)
                        throw new MusicPlatformException("NETWORK_ERROR");
                    return Task.FromResult(f.Default(call));
                };
                await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, f.Clock);
                string sourceId = "真实集合\n/" + new string('l', 2400);
                var context = JsonSerializer.SerializeToElement(new { sourcePlatform = "wy", sourceType = vector.Kind, sourceId });
                var song = MusicSongFixture.Online("wy", vector.RecordingId, "song", "artist", "", 200);
                controller.Select(new("entry", MusicSongFixture.Element(song), context)); controller.Started("entry", 0, 200);
                await controller.DrainAsync(); f.Clock.Advance(5); controller.Observe("entry", 5, 200, true);
                controller.Finish(); await controller.DrainAsync();
                var pending = f.Saved().Single();
                Equal(vector.RecordingId, pending.RecordingId); Equal(vector.Source, pending.Source); Equal(sourceId, pending.SourceId);
                True(pending.Start is not null && pending.End is { Time: 5, Realtime: 5 });
                Equal(0, f.Uploads.Count); await controller.DisposeAsync();
                await using var settings = new MusicFeedbackSettingsOwner(f.Store); await settings.ReadAsync();
                await using var cold = new NativeMusicFeedbackProtocol(f.Accounts, f.Runtime.Client, f.Clock, f.Store, settings);
                f.Reply = null; await cold.InitializeAsync(); await cold.DrainAsync();
                Equal(2, f.Uploads.Count); Equal("start", f.Uploads[0].GetProperty("event").GetString());
                Equal("end", f.Uploads[1].GetProperty("event").GetString());
                foreach (var upload in f.Uploads) {
                    var target = upload.GetProperty("target");
                    Equal(vector.RecordingId, target.GetProperty("nativeId").GetString());
                    Equal(vector.Source, target.GetProperty("source").GetString()); Equal(sourceId, target.GetProperty("sourceId").GetString());
                }
                Equal(5d, f.Uploads[1].GetProperty("listened").GetDouble());
                Equal(5d, f.Uploads[1].GetProperty("mediaListened").GetDouble());
                Equal(0, f.Saved().Length); True(cold.LastError is null);
            });
        foreach (string kind in new[] { "search", "search-song" })
            yield return Case("controller retains complete raw search attribution " + kind, async () => {
                await using var f = await Fixture.Create();
                await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, f.Clock);
                string keyword = "原始关键词\n & " + new string('词', 700);
                var context = JsonSerializer.SerializeToElement(new { sourceType = kind, searchKeyword = keyword });
                controller.Select(new("entry", MusicSongFixture.Element(MusicSongFixture.Online("wy", "123456789012345678", "song", "artist", "", 200)), context));
                controller.Started("entry", 0, 200); await controller.DrainAsync();
                var target = f.Uploads.Single().GetProperty("target");
                Equal("123456789012345678", target.GetProperty("nativeId").GetString());
                Equal("search", target.GetProperty("source").GetString()); Equal(keyword, target.GetProperty("sourceId").GetString());
                f.Clock.Advance(5); controller.Observe("entry", 5, 200, true); controller.Finish(); await controller.DrainAsync();
                Equal(2, f.Uploads.Count); Equal(keyword, f.Uploads[1].GetProperty("target").GetProperty("sourceId").GetString());
                Equal(0, f.Saved().Length); True(controller.LastError is null);
            });
        yield return Case("controller consumes a large current song without imposing an event-sized song gate", async () => {
            await using var f = await Fixture.Create();
            await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, f.Clock);
            var song = MusicSongFixture.Element(MusicSongFixture.Online("wy", "42", new string('x', 300000), "artist", "", 200));
            True(System.Text.Encoding.UTF8.GetByteCount(song.GetRawText()) < 8 * 1024 * 1024);
            controller.Select(new("entry", song)); controller.Started("entry", 0, 200); await controller.DrainAsync();
            var upload = f.Uploads.Single();
            Equal("42", upload.GetProperty("target").GetProperty("nativeId").GetString());
            True(JsonSerializer.SerializeToUtf8Bytes(upload).Length <= 32 * 1024);
            f.Clock.Advance(5); controller.Observe("entry", 5, 200, true); controller.Finish(); await controller.DrainAsync();
            Equal(2, f.Uploads.Count); Equal(0, f.Saved().Length); True(controller.LastError is null);
        });
        yield return Case("canonical KG required fields never use ID shape or mutual echoes as proof", () => {
            string hash = "hash\n/" + new string('h', 80), albumAudioId = "audio\n/" + new string('9', 80);
            var native = new JsonObject { ["hash"] = hash, ["albumAudioId"] = albumAudioId, ["mixsongid"] = "unused-echo" };
            var source = MusicSongFixture.Online("kg", "independent-native-id", "song", "artist", "", 200, native: native);
            var recording = new MusicFeedbackRecording("kg", hash, albumAudioId);
            Equal(recording, MusicFeedbackIdentity.Resolve(MusicSongFixture.Element(source), "kg"));
            var song = MusicSongFixture.Online("kw", "primary", "song", "artist", "", 200);
            song["sources"]!.AsArray().Add(source["sources"]![0]!.DeepClone());
            native["hash"] = "another-hash";
            song["sources"]!.AsArray().Add(MusicSongFixture.Online("kg", "another-native-id", "song", "artist", "", 200, native: native)["sources"]![0]!.DeepClone());
            True(MusicFeedbackIdentity.Resolve(MusicSongFixture.Element(song), "kg") is null);
            Equal(recording, MusicFeedbackIdentity.Resolve(MusicSongFixture.Element(song), "kg", recording));
            return Task.CompletedTask;
        });
        yield return Case("ordinary recording ID shapes pass through while platform association remains native", async () => {
            await using var f = await Fixture.Create();
            await Error(() => f.Protocol.StartAsync(f.Account, Start(new()) with { Recording = new("tx", "MID") }), "IDENTITY_MISMATCH");
            foreach (var recording in new[] { new MusicFeedbackRecording("wy", "042"), new("wy", "42", "7"), new("wy", new string('9', 80)) }) {
                await using var session = await f.Protocol.StartAsync(f.Account, Start(new()) with { Recording = recording, Duration = 0, PlaybackId = Guid.NewGuid() });
                await f.Protocol.DrainAsync(); Equal(recording.Id, f.Uploads.Last().GetProperty("target").GetProperty("nativeId").GetString());
            }
            var song = MusicSongFixture.Online("tx", "MID", "song", "artist", "", 200);
            song["sources"] = new JsonArray(song["sources"]![0]!.DeepClone(),
                MusicSongFixture.Online("wy", "42", "song", "artist", "", 200)["sources"]![0]!.DeepClone(),
                MusicSongFixture.Online("wy", "43", "song", "artist", "", 200)["sources"]![0]!.DeepClone());
            True(MusicFeedbackIdentity.Resolve(MusicSongFixture.Element(song), "wy") is null);
        });
        yield return Case("invalid native numbers and Unicode scalars reject before durable admission", async () => {
            await using var f = await Fixture.Create();
            foreach (var input in new[] { Start(new()) with { Duration = double.NaN }, Start(new()) with { Duration = -1 },
                Start(new()) with { Rate = double.PositiveInfinity }, Start(new()) with { Rate = 0 }, Start(new()) with { Position = 201 },
                Start(new()) with { PlaybackId = Guid.Empty }, Start(new()) with { AdmittedAt = default },
                Start(new()) with { Algorithm = "\ud800" }, Start(new()) with { SourceId = "\ud800" }, Start(new()) with { Source = "\udc00" },
                Start(new()) with { Recording = new("wy", "\ud800") } })
                await Error(() => f.Protocol.StartAsync(f.Account, input), "INVALID_REQUEST");
            Equal(0, f.Calls); Equal(0, f.Saved().Length);
        });
        yield return Case("large event admission and current cold storage preserve complete facts", async () => {
            await using var f = await Fixture.Create();
            var input = Start(f.Attempt) with { Source = new string('s', 4 * 1024 * 1024 + 1) };
            await using var session = await f.Protocol.StartAsync(f.Account, input);
            await f.Protocol.DrainAsync();
            Equal(1, f.Uploads.Count); Equal(input.Source, f.Uploads.Single().GetProperty("target").GetProperty("source").GetString());
            var bytes = NeteasePlaybackOutboxDocument.Encode([NeteasePlaybackTransport.Start(f.Account, input, 1)]);
            True(bytes.Length > 4 * 1024 * 1024);
            var restored = NeteasePlaybackOutboxDocument.Decode(new(bytes, StorageRevision.Empty));
            Equal(input.Source, restored.Single().Source);
        });
        yield return Case("high-rate delayed-dispatch facts are not bounded by dispatch elapsed", async () => {
            await using var f = await Fixture.Create();
            await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { Duration = 90000, Rate = 32 }); await f.Protocol.DrainAsync();
            await session.FinishAsync(End() with { Listened = 100, MediaListened = 3200, Rate = 32, Duration = 0, Position = 3200 });
            await f.Protocol.DrainAsync(); Equal(2, f.Uploads.Count);
        });
        foreach (var end in new[] { End() with { Listened = double.NaN }, End() with { MediaListened = -1 }, End() with { Duration = -1 },
            End() with { Position = double.PositiveInfinity }, End() with { Rate = 0 }, End() with { Reason = "fabricated" },
            End() with { PlaybackId = Guid.NewGuid() }, End() with { EndedAt = default } })
            yield return Case("invalid terminal facts remain single-shot " + end, async () => {
                await using var f = await Fixture.Create();
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                await Error(() => session.FinishAsync(end), "INVALID_REQUEST"); await Error(() => session.FinishAsync(End()), "INSTANCE_MISMATCH");
                Equal(1, f.Uploads.Count);
            });
        yield return Case("natural end preserves zero listening and original wall clock", async () => {
            await using var f = await Fixture.Create(); await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            await session.FinishAsync(End() with { Listened = 0, MediaListened = 0, Position = 193.25, Reason = "playend", EndedAt = Occurred.AddSeconds(5) });
            await f.Protocol.DrainAsync(); Equal(2, f.Uploads.Count); Equal(0d, f.Uploads[1].GetProperty("listened").GetDouble());
            Equal(Occurred.AddSeconds(5).ToUnixTimeMilliseconds() / 1000d, f.Uploads[1].GetProperty("occurredAt").GetDouble());
        });
        yield return Case("duplicate local start shares attempt and cannot change account or facts", async () => {
            await using var f = await Fixture.Create(); var entered = Signal(); var release = Signal();
            f.Reply = async (call, _, _) => { if (call.Operation == "feedback.upload") { entered.TrySetResult(); await release.Task; } return f.Default(call); };
            var originalAccount = f.Account;
            var generation = new MusicPlatformSession(originalAccount.Platform, originalAccount.Scope,
                originalAccount.Generation + 1, originalAccount.UserId, originalAccount.Cookie);
            var credential = new MusicPlatformSession(originalAccount.Platform, originalAccount.Scope,
                originalAccount.Generation, originalAccount.UserId, "MUSIC_U=changed");
            try {
                var first = f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await using var session = await first; True(ReferenceEquals(session, await second));
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { Algorithm = "changed" }), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt) with { IsQueueRecommendation = true }), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(generation, Start(f.Attempt)), "SESSION_CHANGED");
                await Error(() => f.Protocol.StartAsync(credential, Start(f.Attempt)), "SESSION_CHANGED");
            } finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal(1, f.Uploads.Count);
            int calls = f.Calls; f.Accounts.Value = generation;
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_MISMATCH");
            f.Accounts.Value = credential;
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_MISMATCH");
            await Error(() => f.Protocol.StartAsync(originalAccount, Start(f.Attempt)), "SESSION_CHANGED");
            Equal(calls, f.Calls); Equal(1, f.Uploads.Count);
        });
        foreach (string code in new[] { "NETWORK_ERROR", "TIMEOUT", "REDIRECT_DENIED", "LIMIT", "INVALID_RESPONSE", "UPSTREAM_UNCONFIRMED", "AUTH_EXPIRED", "FORBIDDEN", "RATE_LIMITED" })
            yield return Case("canonical unknown upload is terminal without replay " + code, async () => {
                await using var f = await Fixture.Create();
                f.Reply = (call, _, _) => Task.FromResult(call.Operation == "feedback.upload" ? MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = code }) : f.Default(call));
                await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
                Equal(code, f.Protocol.LastError); Equal(0, f.Saved().Length);
                await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(1, f.Uploads.Count);
                await Error(() => session.FinishAsync(End()), "START_UNCONFIRMED");
            });
        yield return Case("terminal remains single-shot while upload outcome is unknown", async () => {
            await using var f = await Fixture.Create(); await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            var entered = Signal(); var release = Signal();
            f.Reply = async (call, _, _) => { if (call.Operation == "feedback.upload") { entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = "NETWORK_ERROR" }); } return f.Default(call); };
            try {
                await session.FinishAsync(End()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await session.FinishAsync(End());
                await Error(() => session.FinishAsync(End() with { Listened = 6 }), "INSTANCE_MISMATCH");
                await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_ENDED");
            } finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal("NETWORK_ERROR", f.Protocol.LastError);
            await session.FinishAsync(End()); await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(2, f.Uploads.Count); Equal(0, f.Saved().Length);
        });
        foreach (int stage in new[] { 1, 2, 3, 4 })
            yield return Case("exact account fence stops later dispatch but settles entered writes at step " + stage, async () => {
                await using var f = await Fixture.Create(); var admitted = Signal();
                f.Reply = async (call, _, count) => {
                    await admitted.Task;
                    if (count == stage) { f.Accounts.Current = false; f.Protocol.AuthorizationChanged(accountChanged: true); }
                    return f.Default(call);
                };
                IMusicFeedbackSession admittedSession;
                try { admittedSession = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); }
                finally { admitted.TrySetResult(); }
                await using var session = admittedSession; await f.Protocol.DrainAsync();
                if (stage <= 2) await Error(() => session.FinishAsync(End()), "SESSION_CHANGED");
                else { await session.FinishAsync(End()); await f.Protocol.DrainAsync(); }
                Equal(stage, f.Calls); Equal(stage / 2, f.Uploads.Count); Equal(0, f.Saved().Length);
                await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(stage, f.Calls);
            });
        yield return Case("finish repeats live qualification and expired login never sends end", async () => {
            await using var f = await Fixture.Create(); await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            f.Reply = (_, _, _) => throw new MusicPlatformException("AUTH_EXPIRED");
            await session.FinishAsync(End()); await f.Protocol.DrainAsync(); Equal("AUTH_EXPIRED", f.Protocol.LastError); Equal(1, f.Uploads.Count);
            await session.FinishAsync(End()); await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(1, f.Uploads.Count); Equal(0, f.Saved().Length);
        });
        yield return Case("controller excludes progress before live account qualification", async () => {
            await using var f = await Fixture.Create(); var clock = new FactClock(); await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, clock);
            var entered = Signal(); var release = Signal();
            f.Reply = async (call, _, count) => { if (count == 1) { entered.TrySetResult(); await release.Task; } if (count > 1) throw new MusicPlatformException("NETWORK_ERROR"); return f.Default(call); };
            controller.Select(new("entry", MusicSongFixture.Element(MusicSongFixture.Online("wy", "42", "song", "artist", "", 200)))); controller.Started("entry", 0, 0);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(30); controller.Observe("entry", 30, 0, true); } finally { release.TrySetResult(); }
            await controller.DrainAsync(); clock.Advance(7); controller.Observe("entry", 37, 0, true); controller.Finish(); await controller.DrainAsync();
            var pending = f.Saved().Single(); Equal(Occurred.AddSeconds(30).ToUnixTimeMilliseconds(), pending.Start!.At);
            True(pending.End is { Time: 7, Realtime: 7 }); Equal(0, f.Uploads.Count);
        });
        yield return Case("close abandons unqualified logical waiter but protocol owns physical lookup", async () => {
            await using var f = await Fixture.Create(); await using var controller = new MusicFeedbackController(f.Settings, f.Protocol, f.Accounts, f.Clock);
            var entered = Signal(); var cancelled = Signal(); var release = Signal();
            f.Reply = async (call, token, _) => { using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; return f.Default(call); };
            controller.Select(new("entry", MusicSongFixture.Element(MusicSongFixture.Online("wy", "42", "song", "artist", "", 200)))); controller.Started("entry", 0, 0);
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var prepare = controller.PrepareShutdownAsync(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await prepare.WaitAsync(TimeSpan.FromSeconds(5)); Equal(0, f.Saved().Length); True(!f.Protocol.DrainAsync().IsCompleted);
            } finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal(0, f.Saved().Length); Equal(0, f.Uploads.Count);
        });
        yield return Case("pre-cancellation and stale account admit no durable or runtime work", async () => {
            await using var f = await Fixture.Create(); using var stop = new CancellationTokenSource(); stop.Cancel();
            await Cancelled(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt), stop.Token)); f.Accounts.Current = false;
            await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "SESSION_CHANGED"); Equal(0, f.Calls); Equal(0, f.Saved().Length);
        });
        foreach (string action in new[] { "token", "protocol", "prepare" })
            yield return Case("dispatched upload settles once and physical owner drains " + action, async () => {
                await using var f = await Fixture.Create(); using var stop = new CancellationTokenSource(); var entered = Signal(); var release = Signal(); bool cancellable = true;
                f.Reply = async (call, token, _) => { if (call.Operation == "feedback.upload") { cancellable = token.CanBeCanceled; entered.TrySetResult(); await release.Task; } return f.Default(call); };
                Task completion = Task.CompletedTask; await using var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt), stop.Token);
                try {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); True(!cancellable);
                    if (action == "protocol") completion = f.Protocol.DisposeAsync().AsTask(); else if (action == "prepare") completion = f.Protocol.PrepareShutdownAsync(); else stop.Cancel();
                    True(!f.Protocol.DrainAsync().IsCompleted); if (action == "protocol") True(!completion.IsCompleted);
                    if (action == "prepare") await completion.WaitAsync(TimeSpan.FromSeconds(5));
                } finally { release.TrySetResult(); }
                await completion; await f.Protocol.DrainAsync(); Equal(0, f.Saved().Length); Equal(1, f.Uploads.Count); Equal(0, f.Accounts.Disposals);
            });
        yield return Case("idle disposal never fabricates end or revokes safe pending", async () => {
            await using var f = await Fixture.Create(); f.Offline = true; var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            await session.DisposeAsync(); await session.DisposeAsync(); await f.Protocol.DrainAsync();
            await Error(() => session.FinishAsync(End()), "CLOSED"); await Error(() => f.Protocol.StartAsync(f.Account, Start(f.Attempt)), "INSTANCE_ENDED");
            Equal(1, f.Saved().Length); Equal(0, f.Uploads.Count);
        });
        yield return Case("session release leaves outbox owning physical terminal cleanup", async () => {
            await using var f = await Fixture.Create(); var session = await f.Protocol.StartAsync(f.Account, Start(f.Attempt)); await f.Protocol.DrainAsync();
            var entered = Signal(); var release = Signal(); f.Reply = async (call, _, _) => { if (call.Operation == "feedback.upload") { entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = "NETWORK_ERROR" }); } return f.Default(call); };
            try {
                await session.FinishAsync(End()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); True(!f.Protocol.DrainAsync().IsCompleted);
            } finally { release.TrySetResult(); }
            await f.Protocol.DrainAsync(); Equal(0, f.Saved().Length); Equal("NETWORK_ERROR", f.Protocol.LastError); Equal(2, f.Uploads.Count);
            await session.DisposeAsync(); await f.Protocol.InitializeAsync(); await f.Protocol.DrainAsync(); Equal(2, f.Uploads.Count);
        });
        yield return Case("cancelled duplicate waiter cannot revoke admitted local CAS", async () => {
            await using var f = await Fixture.Create(); using var stop = new CancellationTokenSource(); f.Store.HoldNext = true;
            var original = f.Protocol.StartAsync(f.Account, Start(f.Attempt));
            try { await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var duplicate = f.Protocol.StartAsync(f.Account, Start(f.Attempt), stop.Token); stop.Cancel(); await Cancelled(() => duplicate); True(!original.IsCompleted); }
            finally { f.Store.Release.TrySetResult(); }
            await using var session = await original; await f.Protocol.DrainAsync(); Equal(1, f.Uploads.Count);
        });
        yield return Case("independent attempts retain no native rate ledger or remote receipt history", async () => {
            await using var f = await Fixture.Create(); f.Reply = (_, _, _) => throw new MusicPlatformException("AUTH_EXPIRED");
            for (int i = 0; i < 81; i++) { await using var session = await f.Protocol.StartAsync(f.Account, Start(new()) with { PlaybackId = Guid.NewGuid() }); await f.Protocol.DrainAsync(); }
            Equal(81, f.Calls); Equal(0, f.Saved().Length); Equal(0, f.Uploads.Count);
        });
    }
    private static MusicRecommendationFeedback Origin(string kind, string provider, string source) =>
        new("session", "batch", "entry", "wy", kind, provider, "song", "", "occurrence-alg", "", "", true, false, source);
    private static MusicPlatformSession Account() => new("wy", "synthetic-account", 1, "123", "MUSIC_U=synthetic-session; __csrf=synthetic-csrf");
    private sealed class Accounts : IMusicPlatformSessionSource, IDisposable
    {
        internal bool Current = true; internal int Acquisitions, Disposals; internal MusicPlatformSession Value = Account();
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token) { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Acquisitions); return ValueTask.FromResult<MusicPlatformSession?>(Current ? Value : null); }
        public bool IsCurrent(MusicPlatformSession account) => Current && ReferenceEquals(account, Value);
        public void Dispose() => Disposals++;
    }
    private sealed class Store : NativeTestDatabase
    {
        internal bool HoldNext;
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            if (HoldNext) { HoldNext = false; Entered.TrySetResult(); await Release.Task; }
            return await base.WriteAsync(write, token, retainedBytes, commit);
        }
    }
    private sealed class FactClock : TimeProvider
    {
        private long ticks; public override long TimestampFrequency => 1000; public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Occurred.AddMilliseconds(ticks); internal void Advance(int seconds) => ticks += seconds * 1000;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly MusicFeedbackAttempt Attempt = new(); internal readonly Accounts Accounts = new(); internal MusicPlatformSession Account => Accounts.Value;
        internal readonly MusicPlatformRuntimeFixture Runtime = new(); internal readonly NativeMusicFeedbackProtocol Protocol;
        internal readonly Store Store = new(); internal readonly MusicFeedbackSettingsOwner Settings; internal readonly FactClock Clock = new();
        internal readonly List<JsonElement> Uploads = [];
        internal Func<MusicPlatformRuntimeFixture.Call, CancellationToken, int, Task<JsonElement>>? Reply; internal bool Offline;
        private int calls; internal int Calls => Volatile.Read(ref calls);
        private Fixture() { Settings = new(Store); Runtime.Client.BindSessions(Accounts); Runtime.OnRequest = Send; Protocol = new(Accounts, Runtime.Client, Clock, Store, Settings); }
        internal static async Task<Fixture> Create() { var f = new Fixture(); await f.Settings.ReadAsync(); await f.Settings.SaveAsync(0, ["wy"]); return f; }
        internal NeteasePlaybackPending[] Saved() => NeteasePlaybackOutboxDocument.Decode(Store.ReadRecordAsync(NeteasePlaybackOutboxDocument.Namespace, MeowFilm.Capabilities.Feedback.FeedbackStorageRecords.Outbox).GetAwaiter().GetResult());
        internal JsonElement Default(MusicPlatformRuntimeFixture.Call call) => call.Operation switch {
            "feedback.qualify" => MusicPlatformRuntimeFixture.Json(new { qualified = true }),
            "feedback.upload" => MusicPlatformRuntimeFixture.Json(new { status = "confirmed" }), _ => throw new InvalidOperationException(call.Operation) };
        private async Task<JsonElement> Send(MusicPlatformRuntimeFixture.Call call, CancellationToken token) {
            int count = Interlocked.Increment(ref calls);
            if (call.Operation == "feedback.upload") { var events = call.Parameters.GetProperty("events"); Equal(1, events.GetArrayLength()); Uploads.Add(events[0].Clone()); }
            if (Offline) throw new MusicPlatformException("NETWORK_ERROR"); return Reply is null ? Default(call) : await Reply(call, token, count);
        }
        public async ValueTask DisposeAsync() { Store.Release.TrySetResult(); try { await Protocol.DisposeAsync(); await Settings.DisposeAsync(); } finally { await Runtime.DisposeAsync(); } }
    }
}
