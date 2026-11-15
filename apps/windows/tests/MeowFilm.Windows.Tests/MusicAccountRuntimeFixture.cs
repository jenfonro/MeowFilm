// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Canonical account peer; registration, cookies, QR crypto and cooldowns belong to Node.
internal sealed class MusicAccountRuntimeFixture : IAsyncDisposable
{
    internal const string UserId = "9007199254740997", Phone = "10000000000", Captcha = "864209";
    internal readonly string Platform, Root;
    internal readonly MusicPlatformRuntimeFixture Runtime = new();
    internal readonly StoreProbe Store;
    internal readonly MusicAccountOwner Owner;
    internal string PollStatus = "authenticated";
    internal string? FailureCode, HoldOperation;
    internal Func<Task>? BeforeResponse;
    internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal();
    internal MusicAccountRuntimeFixture(string platform, string? root = null)
    {
        Platform = platform; Root = root ?? TestWorkspace.DataPath("account-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = new(new LocalDocumentStore(Root, [MusicAccountOwner.DocumentNamespace], protectedNamespaces: [MusicAccountOwner.DocumentNamespace]));
        Runtime.OnRequest = Respond;
        Owner = Runtime.CreateAccountOwner(Store);
    }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Cookie => Platform switch {
        "wy" => "MUSIC_U=SYNTHETIC_WY_SESSION; __csrf=SYNTHETIC_CSRF",
        "tx" => "musicid=" + UserId + "; musickey=SYNTHETIC_QQ_KEY; encrypt_uin=SYNTHETIC_EUIN",
        _ => "userid=" + UserId + "; token=SYNTHETIC_KG_TOKEN; KUGOU_API_MID=123456789; t1=SYNTHETIC_T1"
    };
    internal async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
    {
        if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
        using var registration = token.Register(() => Cancelled.TrySetResult());
        if (HoldOperation == call.Operation) { Entered.TrySetResult(); await Release.Task; }
        if (BeforeResponse is not null) { var action = BeforeResponse; BeforeResponse = null; await action(); }
        if (FailureCode is not null) throw new MusicPlatformException(FailureCode);
        if (call.Operation == "qr.create") return MusicPlatformRuntimeFixture.Json(new {
            challengeId = call.Parameters.GetProperty("challengeId").GetString(),
            url = Platform == "tx" ? "" : Platform == "kg" ? "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=SYNTHETIC_QR"
                : "https://music.163.com/login?codekey=SYNTHETIC_QR",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeMilliseconds(),
            imageBase64 = Platform == "tx" ? Convert.ToBase64String(QrPng()) : null });
        if (call.Operation == "sms.send") return MusicPlatformRuntimeFixture.Json(new { confirmed = true, retryAfterSeconds = 60 });
        var observation = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(Platform, UserId, vip: true, displayName: "Synthetic account").GetRawText())!.AsObject();
        if (call.Operation == "account.verify") observation["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
        if (call.Operation is "account.verify" or "account.profile") return MusicPlatformRuntimeFixture.Json(observation);
        if (call.Operation == "sms.login") { observation["cookie"] = Cookie; return MusicPlatformRuntimeFixture.Json(observation); }
        if (call.Operation == "qr.poll")
        {
            observation["cookie"] = Cookie;
            return MusicPlatformRuntimeFixture.Json(new { status = PollStatus, account = PollStatus == "authenticated" ? observation : null });
        }
        throw new InvalidOperationException(call.Operation);
    }
    internal Task<JsonElement> Begin() => Owner.CallAsync("music.accounts.qr.begin", new { platform = Platform });
    internal Task<JsonElement> Poll(string challenge, CancellationToken token = default) => Owner.CallAsync("music.accounts.qr.poll", new { platform = Platform, challengeId = challenge }, token);
    internal async Task<JsonElement> Login() => await Poll(Id(Ok(await Begin())));
    internal Task<JsonElement> Import() => Owner.CallAsync("music.accounts.import", new { platform = Platform, cookie = Cookie });
    internal Task<JsonElement> Send(CancellationToken token = default) => Owner.CallAsync("music.accounts.sms.send", new { platform = Platform, phone = Phone }, token);
    internal Task<JsonElement> SmsLogin(CancellationToken token = default) => Owner.CallAsync("music.accounts.sms.login", new { platform = Platform, phone = Phone, countryCode = "86", captcha = Captcha }, token);
    internal static JsonElement Ok(JsonElement value) { Check(value.GetProperty("ok").GetBoolean(), "Expected account success"); return value; }
    internal static string Id(JsonElement value) => value.GetProperty("challenge").GetProperty("challengeId").GetString()!;
    internal static string Code(JsonElement value) => value.GetProperty("code").GetString()!;
    internal static void Check(bool value, string reason = "Canonical account invariant") { if (!value) throw new InvalidOperationException(reason); }
    // Local image bytes only; no QQ transport, callback or credential recipe.
    internal static byte[] QrPng(int width = 16, int inflated = 0)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string kind, byte[] data)
        {
            byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); output.Write(length);
            byte[] content = [.. Encoding.ASCII.GetBytes(kind), .. data]; output.Write(content);
            uint crc = uint.MaxValue;
            foreach (byte b in content) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
        }
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 16); header[8] = 8; Chunk("IHDR", header);
        byte[] pixels = new byte[inflated > 0 ? inflated : 16 * (width + 1)];
        for (int y = 0; y < 16 && inflated == 0; y++)
            for (int x = 0; x < width; x++) pixels[y * (width + 1) + x + 1] = (byte)((x + y) % 2 == 0 ? 0 : 255);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(pixels);
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(); Store.Release.TrySetResult();
        await Owner.DisposeAsync(); await Runtime.DisposeAsync(); await Store.DisposeAsync();
    }
    internal sealed class StoreProbe(LocalDocumentStore inner) : IDocumentStore
    {
        internal int Writes, Disposals;
        internal bool HoldWrite, LoseReceipt, Conflict, FailRead;
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) => FailRead
            ? throw new DocumentStoreException(DocumentStoreError.Unavailable) : inner.ReadAsync(ns, token);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            Writes++;
            if (HoldWrite) { Entered.TrySetResult(); await Release.Task; token = CancellationToken.None; }
            if (Conflict) throw new DocumentStoreException(DocumentStoreError.Conflict);
            var result = await inner.WriteAsync(ns, data, expected, token);
            if (LoseReceipt) throw new DocumentStoreException(DocumentStoreError.Storage);
            return result;
        }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    internal static IEnumerable<TestCase> QrCases(string platform, string prefix)
    {
        TestCase Case(string name, Func<Task> run) => new(prefix + name, run);
        yield return Case("canonical-QR-real-CAS-DPAPI-current-reopen-revalidate-logout", async () => {
            string root;
            await using (var f = new MusicAccountRuntimeFixture(platform))
            {
                root = f.Root; int notifications = 0; f.Owner.SessionChanged += _ => notifications++;
                var result = Ok(await f.Login()); Check(result.GetProperty("status").GetString() == "authenticated");
                var session = await f.Owner.AcquireAsync(platform); Check(session is not null && f.Owner.IsCurrent(session));
                var details = await f.Owner.ReadDetailsAsync(platform); Check(details.State == "verified" && details.Membership.Vip == true && notifications > 0 && f.Store.Writes == 1);
                Check(f.Runtime.Requests.Count(call => call.Operation == "qr.poll") == 1);
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("SYNTHETIC", StringComparison.Ordinal));
            }
            await using var cold = new MusicAccountRuntimeFixture(platform, root);
            var local = await cold.Owner.ReadDetailsAsync(platform);
            Check(local.State == "saved" && local.Profile?.UserId == UserId && local.Membership.Vip is null && cold.Runtime.Requests.Count == 0);
            Ok(await cold.Owner.CallAsync("music.accounts.validate", new { platform }));
            Check((await cold.Owner.ReadDetailsAsync(platform)).State == "verified");
            Ok(await cold.Owner.CallAsync("music.accounts.logout", new { platform }));
            Check(await cold.Owner.AcquireAsync(platform) is null);
        });
        foreach (string status in new[] { "waiting", "confirming", "expired", "refused" })
        {
            string s = status;
            yield return Case("canonical-QR-state-" + s, async () => {
                await using var f = new MusicAccountRuntimeFixture(platform) { PollStatus = s };
                var result = Ok(await f.Login()); Check(result.GetProperty("status").GetString() == s && f.Store.Writes == 0);
            });
        }
        foreach (string code in new[] { "INVALID_RESPONSE", "IDENTITY_MISMATCH", "AUTH_EXPIRED", "FORBIDDEN", "VERIFICATION_REQUIRED", "RATE_LIMITED", "TIMEOUT", "LIMIT" })
        {
            string c = code;
            yield return Case("canonical-QR-failure-" + c + "-no-save", async () => {
                await using var f = new MusicAccountRuntimeFixture(platform);
                string id = Id(Ok(await f.Begin())); f.FailureCode = c;
                Check(Code(await f.Poll(id)) == c && f.Store.Writes == 0 && await f.Owner.AcquireAsync(platform) is null);
            });
        }
        yield return Case("QR-cancel-no-late-handoff-and-physical-close-drain", async () => {
            await using var f = new MusicAccountRuntimeFixture(platform) { HoldOperation = "qr.poll" };
            string id = Id(Ok(await f.Begin())); var polling = f.Poll(id);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ok(await f.Owner.CallAsync("music.accounts.cancel", new { platform }));
            await f.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = f.Owner.DisposeAsync().AsTask(); Check(!close.IsCompleted);
            f.Release.TrySetResult(); Check(!((await polling).GetProperty("ok").GetBoolean())); await close;
            Check(f.Store.Writes == 0 && f.Store.Disposals == 0);
        });
        yield return Case("QR-new-challenge-fences-old-poll-and-single-poll-admission", async () => {
            await using var f = new MusicAccountRuntimeFixture(platform) { HoldOperation = "qr.poll" };
            string old = Id(Ok(await f.Begin())); var pending = f.Poll(old);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(Code(await f.Poll(old)) == "BUSY");
            string current = Id(Ok(await f.Begin())); Check(current != old);
            f.HoldOperation = null; f.Release.TrySetResult(); Check(!((await pending).GetProperty("ok").GetBoolean()));
            Ok(await f.Poll(current)); Check(f.Store.Writes == 1);
        });
        yield return Case("QR-unknown-CAS-consumed-no-replay-explicit-reconciliation", async () => {
            await using var f = new MusicAccountRuntimeFixture(platform); f.Store.LoseReceipt = true;
            string id = Id(Ok(await f.Begin())); Check(Code(await f.Poll(id)) == "HANDOFF_UNCONFIRMED");
            Check(await f.Owner.AcquireAsync(platform) is null); int requests = f.Runtime.Requests.Count;
            Check(Ok(await f.Poll(id)).GetProperty("status").GetString() == "expired" && f.Runtime.Requests.Count == requests && f.Store.Writes == 1);
            Ok(await f.Owner.CallAsync("music.accounts.list")); Check(await f.Owner.AcquireAsync(platform) is not null);
        });
        yield return Case("QR-admitted-cancel-preserves-physical-CAS-gate", async () => {
            await using var f = new MusicAccountRuntimeFixture(platform); f.Store.HoldWrite = true;
            var login = f.Login(); await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ok(await f.Owner.CallAsync("music.accounts.cancel", new { platform }));
            Check(Code(await login) == "HANDOFF_UNCONFIRMED");
            var read = f.Owner.CallAsync("music.accounts.list"); Check(!read.IsCompleted);
            f.Store.Release.TrySetResult(); Ok(await read); Check(f.Store.Writes == 1 && await f.Owner.AcquireAsync(platform) is not null);
        });
        yield return Case("QR-logout-during-authorization-fences-old-grant", async () => {
            await using var f = new MusicAccountRuntimeFixture(platform); Ok(await f.Import());
            var session = await f.Owner.AcquireAsync(platform); f.HoldOperation = "qr.poll";
            string id = Id(Ok(await f.Begin())); var polling = f.Poll(id); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ok(await f.Owner.CallAsync("music.accounts.logout", new { platform })); f.Release.TrySetResult();
            Check(!((await polling).GetProperty("ok").GetBoolean()) && !f.Owner.IsCurrent(session!) && await f.Owner.AcquireAsync(platform) is null && f.Store.Writes == 2);
        });
        yield return Case("QR-other-owner-logout-wins-durable-CAS", async () => {
            await using var f = new MusicAccountRuntimeFixture(platform); Ok(await f.Import());
            await using var other = new MusicAccountRuntimeFixture(platform, f.Root);
            string id = Id(Ok(await f.Begin()));
            f.BeforeResponse = async () => Ok(await other.Owner.CallAsync("music.accounts.logout", new { platform }));
            Check(!((await f.Poll(id)).GetProperty("ok").GetBoolean()) && f.Store.Writes == 1);
            Check(await f.Owner.AcquireAsync(platform) is null);
        });
    }
}
