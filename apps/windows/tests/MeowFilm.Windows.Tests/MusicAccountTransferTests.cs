// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountTransferTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("shared fixture imports through production owner and exports exact interoperable fields", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var clientBefore = await f.ClientBytes();
            using var plan = await f.Prepare();
            Check(plan.Conflicts.Count == 0);
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Select(item => item.Platform).SequenceEqual(new[] { "wy", "tx", "kg" }) &&
                result.Items.All(item => item.Status == "imported"));
            using var exported = await f.Owner.ExportAccountTransferAsync();
            Check(exported is not null);
            using var output = new MemoryStream(); await exported!.WriteAsync(output);
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), TransferFixture.Shared()));
            var clientAfter = await f.ClientBytes();
            Check(clientBefore.SequenceEqual(clientAfter));
            Check(f.Notifications.SequenceEqual(new[] { "wy", "tx", "kg" }));
            Check(f.Requests["wy"] == 2 && f.Requests["tx"] == 3 && f.Requests["kg"] == 2);
            foreach (string platform in new[] { "wy", "tx", "kg" })
            {
                var session = await f.Owner.AcquireAsync(platform); Check(session is not null && f.Owner.IsCurrent(session));
                var details = await f.Owner.ReadDetailsAsync(platform);
                Check(details.Profile?.UserId == TransferFixture.Id(platform) && details.Generation == 1 &&
                    details.State == "verified" && details.ProfileFreshness == MusicAccountDetailsFreshness.Fresh &&
                    details.Membership.Vip == false && details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh &&
                    details.MembershipCheckedAt > 0 && details.MembershipRecheckAt > details.MembershipCheckedAt);
            }
        });
        yield return Case("imported observations remain disposable and a cold owner restores only saved credentials", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.Single().Status == "imported");
            Check((await f.Owner.ReadDetailsAsync("wy")).Membership.Vip == false);
            await using var reopened = f.Clients.CreateAccountOwner(f.Store);
            var restored = await reopened.ReadDetailsAsync("wy");
            var session = await reopened.AcquireAsync("wy");
            Check(restored.State == "saved" && restored.Generation == 1 && restored.Profile?.UserId == TransferFixture.Id("wy") &&
                restored.Membership.Vip is null && restored.MembershipFreshness == MusicAccountDetailsFreshness.Missing &&
                session is not null && reopened.IsCurrent(session) && f.Store.Writes == 1 && f.Requests["wy"] == 2);
        });
        yield return Case("slow failed details persistence neither blocks import nor makes the next credential commit uncertain", async () =>
        {
            await using var backing = new FailingDetailsStore();
            await using var cache = new CacheDocumentStore(backing);
            await using var f = new TransferFixture(cache); await f.Initialize();
            try
            {
                using var first = await f.Prepare(TransferFixture.Only("wy"));
                Check((await f.Owner.ImportAccountTransferAsync(first, MusicAccountTransferPolicy.Overwrite)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Items.Single().Status == "imported");
                await backing.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var old = await f.Owner.AcquireAsync("wy");
                var details = await f.Owner.ReadDetailsAsync("wy").WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Owner.IsCurrent(old!) && details.State == "verified" && details.Membership.Vip == false &&
                    details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
                using var second = await f.Prepare(TransferFixture.Only("wy"));
                Check((await f.Owner.ImportAccountTransferAsync(second, MusicAccountTransferPolicy.Overwrite)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Items.Single().Status == "imported");
                var current = await f.Owner.AcquireAsync("wy");
                Check(!f.Owner.IsCurrent(old!) && current?.Generation == 2 && f.Owner.IsCurrent(current!) &&
                    f.Store.Writes == 2 && f.Notifications.SequenceEqual(new[] { "wy", "wy" }));
            }
            finally { backing.Release.TrySetResult(); }
            await cache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var settled = await f.Owner.ReadDetailsAsync("wy");
            Check(backing.Writes == 2 && settled.Generation == 2 && settled.State == "verified" &&
                settled.Membership.Vip == false && settled.MembershipFreshness == MusicAccountDetailsFreshness.Fresh &&
                f.Store.Writes == 2 && f.Notifications.Count == 2);
        });
        yield return Case("saved pending accounts export offline and normalize QQ aliases without changing local protection", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var seed = TransferFixture.Shared();
            var tx = seed["accounts"]![1]!["credentials"]!.AsObject();
            tx["loginType"] = tx["login_type"]!.DeepClone(); tx.Remove("login_type");
            tx["expiredAt"] = tx["expired_at"]!.DeepClone(); tx.Remove("expired_at");
            await f.Seed(seed);
            var read = await f.Owner.CallAsync("music.accounts.list");
            Check(read.GetProperty("items").EnumerateArray().All(item => item.GetProperty("state").GetString() == "saved"));
            using var file = await f.Owner.ExportAccountTransferAsync();
            using var output = new MemoryStream(); await file!.WriteAsync(output);
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), TransferFixture.Shared()));
            Check(f.Requests.Values.Sum() == 0 && f.Store.Writes == 0);
            foreach (string path in Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories))
                Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("SYNTHETIC_NETEASE_SESSION", StringComparison.Ordinal));
        });
        yield return Case("WY export without bound device uses exact current request client", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var seed = TransferFixture.Only("wy");
            var parts = seed["accounts"]![0]!["credentials"]!.AsObject();
            foreach (string key in new[] { "deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID", "NMTID" }) parts.Remove(key);
            await f.Seed(seed);
            var client = await f.Clients.Owner.AcquireWyAsync();
            using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            var fields = JsonNode.Parse(output.ToArray())!["accounts"]![0]!["credentials"]!;
            Check(fields["deviceId"]!.GetValue<string>() == client.DeviceId && fields["_ntes_nuid"]!.GetValue<string>() == client.Nuid &&
                fields["_ntes_nnid"]!.GetValue<string>() == client.Nnid && fields["WNMCID"]!.GetValue<string>() == client.Wnmcid);
            Check(f.Requests.Values.Sum() == 0);
        });
        yield return Case("same UID is a conflict and skip never validates or writes that platform", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); using var plan = await f.Prepare();
            Check(plan.Conflicts.Select(item => item.Platform).SequenceEqual(new[] { "wy" }));
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Skip);
            Check(result.Items.Single(item => item.Platform == "wy").Status == "skipped" && f.Requests["wy"] == 0 &&
                f.Store.Writes == 2 && f.Owner.IsCurrent(old!));
            Check(result.Items.Count(item => item.Status == "imported") == 2);
        });
        yield return Case("cancel choice writes and validates nothing and a later batch captures conflicts again", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            using (var first = await f.Prepare())
            {
                Check(first.Conflicts.Count == 1);
                var result = await f.Owner.ImportAccountTransferAsync(first, MusicAccountTransferPolicy.Cancel);
                Check(result.Items.All(item => item.Status == "cancelled") && f.Store.Writes == 0 && f.Requests.Values.Sum() == 0);
            }
            using var second = await f.Prepare(); Check(second.Conflicts.Count == 1);
            Check((await f.Owner.ImportAccountTransferAsync(second, MusicAccountTransferPolicy.Overwrite)).Items.All(item => item.Status == "imported"));
        });
        yield return Case("invalid candidate preserves disk and an already issued valid lease without any fence", async () =>
        {
            foreach (bool deviceMismatch in new[] { false, true })
            {
                await using var cache = new CacheDocumentStore();
                await using var f = new TransferFixture(cache); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
                var old = await f.Owner.AcquireAsync("wy"); var bytes = await f.AccountBytes();
                await f.Owner.RefreshDetailsAsync("wy", 1);
                var cached = cache.ReadMemory(MusicAccountOwner.DetailsCacheNamespace).Fingerprint;
                if (deviceMismatch) f.WyResponseDevice = "SYNTHETIC_WRONG_DEVICE"; else f.FailPlatform = "wy";
                using var plan = await f.Prepare(TransferFixture.Only("wy"));
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Status == "failed" && result.Items.Single().Code ==
                    (deviceMismatch ? "IDENTITY_MISMATCH" : "AUTH_EXPIRED"));
                var after = await f.AccountBytes();
                Check(f.Owner.IsCurrent(old!) && f.Notifications.Count == 0 && f.Store.Writes == 0 && bytes.SequenceEqual(after));
                Check(cache.ReadMemory(MusicAccountOwner.DetailsCacheNamespace).Fingerprint == cached);
            }
        });
        yield return Case("bad candidate cannot supersede an existing production protocol details read", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); f.HoldPlatform = "wy";
            var reading = f.Owner.RefreshDetailsAsync("wy", 1);
            await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.FailAt = 2;
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            var summary = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(summary.Items.Single().Code == "AUTH_EXPIRED" && !reading.IsCompleted && f.Owner.IsCurrent(old!));
            f.Release.TrySetResult();
            Check((await reading).Profile?.UserId == "71001" && f.Owner.IsCurrent(old!));
        });
        yield return Case("matching device uses current owner fields while foreign devices keep their association", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var wy = await f.Clients.Owner.AcquireWyAsync();
            var cookies = AccountRules.Cookies("MUSIC_U=SYNTHETIC_AUTH; deviceId=" + wy.DeviceId +
                "; _ntes_nuid=SYNTHETIC_ACCOUNT_NUID; _ntes_nnid=SYNTHETIC_ACCOUNT_NUID,1700000000000; WNMCID=SYNTHETIC_WNMCID");
            var header = string.Join("; ", cookies.Select(item => item.Key + "=" + item.Value));
            var composed = MusicWyRequestContext.Cookies(wy, header);
            Check(composed["_ntes_nuid"] == wy.Nuid && composed["_ntes_nnid"] == wy.Nnid);
            string foreign = header.Replace(wy.DeviceId, "SYNTHETIC_FOREIGN_DEVICE", StringComparison.Ordinal);
            var foreignComposed = MusicWyRequestContext.Cookies(wy, foreign);
            Check(foreignComposed["_ntes_nuid"] == cookies["_ntes_nuid"] && foreignComposed["_ntes_nnid"] == cookies["_ntes_nnid"]);
            var after = await f.Clients.Owner.AcceptWyAsync(wy, foreign, ["NMTID=SYNTHETIC_RESPONSE"], () => { }, default);
            Check(ReferenceEquals(wy, after));
            var updated = await f.Clients.Owner.AcceptWyAsync(wy, header, ["NMTID=SYNTHETIC_RESPONSE"], () => { }, default);
            Check(MusicWyRequestContext.Cookies(updated, header)["NMTID"] == "SYNTHETIC_RESPONSE");
            var kg = MusicClientContextFixture.Kg with { Dfid = "SYNTHETIC_LOCAL_DFID" };
            var actual = PlatformProtocols.KgIdentity(new() { ["KUGOU_API_MID"] = kg.Mid,
                ["KUGOU_API_GUID"] = kg.Seed, ["dfid"] = "SYNTHETIC_ACCOUNT_DFID" }, kg);
            Check(actual.Mid == kg.Mid && actual.Dfid == "SYNTHETIC_LOCAL_DFID");
            var foreignDevice = new MusicKgClientContext(1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var foreignKg = PlatformProtocols.KgIdentity(new() { ["KUGOU_API_MID"] = foreignDevice.Mid,
                ["KUGOU_API_GUID"] = foreignDevice.Seed, ["dfid"] = "SYNTHETIC_ACCOUNT_DFID" }, kg);
            Check(foreignKg.Mid == foreignDevice.Mid && foreignKg.Dfid == "SYNTHETIC_ACCOUNT_DFID");
        });
        yield return Case("wrong exact identity keeps old account while other valid platforms settle", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed();
            var old = await f.Owner.AcquireAsync("wy"); f.WyUserId = "99999";
            using var plan = await f.Prepare();
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Single(item => item.Platform == "wy").Code == "IDENTITY_MISMATCH" && f.Owner.IsCurrent(old!));
            Check(result.Items.Count(item => item.Status == "imported") == 2 && !f.Notifications.Contains("wy"));
        });
        yield return Case("QQ returned identity mismatch fails even when file and Cookie agree", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); f.QqUserId = "99999";
            using var plan = await f.Prepare(TransferFixture.Only("tx"));
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Single().Code == "IDENTITY_MISMATCH" && f.Store.Writes == 0);
        });
        yield return Case("unknown credential field and invalid credential value are per platform failures", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var input = TransferFixture.Shared(); input["accounts"]![0]!["credentials"]!["unknown"] = "SYNTHETIC_UNKNOWN";
            input["accounts"]![1]!["credentials"]!["musickey"] = "bad;injected=value";
            using var plan = await f.Prepare(input);
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Count(item => item.Status == "failed") == 2 && result.Items.Single(item => item.Platform == "kg").Status == "imported");
            Check(f.Requests["wy"] == 0 && f.Requests["tx"] == 0 && f.Store.Writes == 1);
        });
        yield return Case("empty optional QQ credentials remain strings and KG opaque t1 is unchanged", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var input = TransferFixture.Shared(); input["accounts"]![1]!["credentials"]!["refresh_token"] = "";
            using var plan = await f.Prepare(input);
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.All(item => item.Status == "imported"));
            using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), input));
        });
        yield return Case("unsupported Cookie representation fails without encoding or truncating opaque credential", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            var input = TransferFixture.Only("kg"); input["accounts"]![0]!["credentials"]!["t1"] = "SYNTHETIC;opaque,值";
            using var plan = await f.Prepare(input);
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Single().Code == "INVALID_CREDENTIALS" && f.Requests["kg"] == 0 && f.Store.Writes == 0);
        });
        yield return Case("native KG login exports numeric t1 as exact decimal strings and opaque t1 unchanged", async () =>
        {
            foreach (var (raw, expected) in new[] { ("123456789012345678901234567890", "123456789012345678901234567890"),
                ("1.234567890123456789012345678901e30", "1234567890123456789012345678901"),
                ("-1.25e-3", "-0.00125"), ("\"SYNTHETIC_T1_OPAQUE\"", "SYNTHETIC_T1_OPAQUE") })
            {
                await using var f = new KgAccountFixture(); f.Transport.T1 = JsonNode.Parse(raw)!;
                var begin = await MusicNativeKgAccountTests.Begin(f.Owner);
                Check(MusicNativeKgAccountTests.Ok(begin));
                Check(MusicNativeKgAccountTests.Ok(await MusicNativeKgAccountTests.Poll(f.Owner, MusicNativeKgAccountTests.Id(begin))));
                using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
                var fields = JsonNode.Parse(output.ToArray())!["accounts"]![0]!["credentials"]!;
                Check(fields["t1"]!.GetValue<string>() == expected && fields["KUGOU_API_GUID"]!.GetValue<string>() == f.Transport.Seed);
            }
        });
        yield return Case("native QQ login exports optional empty authorization and numeric metadata as strings", async () =>
        {
            string root = TestWorkspace.DataPath("account-transfer-qq", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            await using var store = new LocalDocumentStore(root, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            using var http = MusicPlatformHttp.CreateWithHandler(new QqAccountFixtureHandler { IncludeOptionalCredentials = true });
            await using var clients = new MusicClientContextFixture(http, store);
            await using var owner = clients.CreateAccountOwner(store);
            var begin = await owner.CallAsync("music.accounts.qr.begin", new { platform = "tx" });
            Check(begin.GetProperty("ok").GetBoolean());
            var result = await owner.CallAsync("music.accounts.qr.poll", new { platform = "tx",
                challengeId = begin.GetProperty("challenge").GetProperty("challengeId").GetString() });
            Check(result.GetProperty("ok").GetBoolean());
            using var file = await owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            var fields = JsonNode.Parse(output.ToArray())!["accounts"]![0]!["credentials"]!;
            foreach (string key in new[] { "openid", "refresh_token", "access_token", "unionid", "refresh_key" })
                Check(fields[key]!.GetValue<string>() == "");
            Check(fields["expired_at"]!.GetValue<string>() == "1800000000" && fields["key_expires_in"]!.GetValue<string>() == "3600" &&
                fields["musickey_create_time"]!.GetValue<string>() == "1700000000" &&
                fields["login_type"]!.GetValue<string>() == "2" && fields["encrypt_uin"]!.GetValue<string>() == "SYNTHETIC_EUIN");
        });
        yield return Case("QQ numeric transfer fields reject invalid values per item and retain exact safe integer boundaries", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("tx"));
            var old = await f.Owner.AcquireAsync("tx"); var before = await f.AccountBytes();
            foreach (string field in new[] { "expired_at", "musickey_create_time", "key_expires_in", "login_type" })
                foreach (string value in new[] { "", "00", "01", "-1", "1.0", "1e3", "9007199254740992", "999999999999999999999" })
                {
                    var input = TransferFixture.Only("tx"); input["accounts"]![0]!["credentials"]![field] = value;
                    using var plan = await f.Prepare(input);
                    var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                    Check(result.Items.Single().Status == "failed" && result.Items.Single().Code == "INVALID_CREDENTIALS");
                }
            var after = await f.AccountBytes();
            Check(f.Store.Writes == 0 && f.Requests["tx"] == 0 && f.Owner.IsCurrent(old!) && before.SequenceEqual(after));
            var valid = TransferFixture.Only("tx");
            var fields = valid["accounts"]![0]!["credentials"]!;
            fields["expired_at"] = "9007199254740991"; fields["musickey_create_time"] = "0"; fields["key_expires_in"] = "3600";
            using var accepted = await f.Prepare(valid);
            Check((await f.Owner.ImportAccountTransferAsync(accepted, MusicAccountTransferPolicy.Overwrite)).Items.Single().Status == "imported");
            using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), valid));
        });
        yield return Case("legacy QQ export fails on invalid numeric canonical fields and aliases without dropping them", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize();
            foreach (string field in new[] { "expired_at", "musickey_create_time", "key_expires_in", "login_type",
                "expiredAt", "musickeyCreateTime", "keyExpiresIn", "loginType" })
                foreach (string value in new[] { "", "01", "9007199254740992" })
                {
                    var input = TransferFixture.Only("tx");
                    var fields = input["accounts"]![0]!["credentials"]!.AsObject();
                    fields.Remove("expired_at"); fields.Remove("login_type"); fields[field] = value;
                    await f.Seed(input); var before = await f.AccountBytes();
                    await Expect("INVALID_CREDENTIALS", () => f.Owner.ExportAccountTransferAsync());
                    var after = await f.AccountBytes();
                    Check(before.SequenceEqual(after));
                }
            Check(f.Store.Writes == 0 && f.Requests["tx"] == 0);
        });
        yield return Case("native QQ login rejects invalid numeric protocol fields before saving authorization", async () =>
        {
            foreach (string value in new[] { "", "01", "-1", "1.0", "9007199254740992" })
            {
                using var http = MusicPlatformHttp.CreateWithHandler(new QqAccountFixtureHandler
                    { IncludeOptionalCredentials = true, InvalidNumericCredential = value });
                await using var clients = new MusicClientContextFixture(http);
                await using var protocol = clients.CreateAccountProtocol();
                var challenge = await protocol.BeginAsync("tx"); int handoffs = 0;
                await Expect("INVALID_RESPONSE", () => protocol.PollAsync("tx", challenge.ChallengeId,
                    (_, _) => { handoffs++; return Task.FromResult(true); }));
                Check(handoffs == 0);
            }
        });
        yield return Case("preparation rejects malformed shape version duplicate keys platforms and nonstring values before network", async () =>
        {
            await using var f = new TransferFixture();
            string valid = TransferFixture.Shared().ToJsonString();
            var invalids = new List<string> { "not json", "[]", valid.Replace("\"version\":1", "\"version\":2"),
                valid.Replace("\"version\":1", "\"version\":1,\"version\":1"), valid.Replace("\"platform\":\"wy\"", "\"platform\":\"kw\""),
                valid.Replace("\"platform\":\"tx\"", "\"platform\":\"wy\""), valid.Replace("\"MUSIC_U\":", "\"MUSIC_U\":\"x\",\"MUSIC_U\":"),
                valid.Replace("\"MUSIC_U\":\"SYNTHETIC_NETEASE_SESSION\"", "\"MUSIC_U\":123"),
                valid.Replace("\"platform\":\"wy\"", "\"platform\":\"wy\",\"extra\":0"),
                valid.Replace("\"format\":", "\"extra\":true,\"format\":") };
            foreach (string invalid in invalids)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalid)); using var file = await MusicAccountTransferFile.ReadAsync(stream);
                await Expect("TRANSFER_FORMAT", () => f.Owner.PrepareAccountTransferAsync(file));
            }
            Check(f.Requests.Values.Sum() == 0 && f.Store.Writes == 0 && f.Store.Reads == 0);
        });
        yield return Case("actual file read rejects over one MiB and file stream errors stay finite", async () =>
        {
            using var oversized = new MemoryStream(new byte[MusicAccountTransferFile.MaximumBytes + 1]);
            await Expect("LIMIT", () => MusicAccountTransferFile.ReadAsync(oversized));
            using var broken = new BrokenStream(); await Expect("TRANSFER_FILE", () => MusicAccountTransferFile.ReadAsync(broken));
            using var file = await TransferFixture.File(TransferFixture.Shared());
            await Expect("TRANSFER_FILE", () => file.WriteAsync(broken));
        });
        yield return Case("empty collection makes no write and no stored login produces no export file", async () =>
        {
            await using var f = new TransferFixture();
            var input = TransferFixture.Shared(); input["accounts"] = new JsonArray();
            using var plan = await f.Prepare(input);
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.Count == 0);
            Check(await f.Owner.ExportAccountTransferAsync() is null && f.Store.Writes == 0 && f.Requests.Values.Sum() == 0);
            foreach (string version in new[] { "1.0", "1e0" })
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input.ToJsonString().Replace("\"version\":1", "\"version\":" + version)));
                using var file = await MusicAccountTransferFile.ReadAsync(stream);
                using var equivalent = await f.Owner.PrepareAccountTransferAsync(file);
                Check((await f.Owner.ImportAccountTransferAsync(equivalent, MusicAccountTransferPolicy.Overwrite)).Items.Count == 0);
            }
        });
        yield return Case("same generation independent writer after confirmation cannot be overwritten", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            await f.ChangeStoredName("wy"); var changed = await f.AccountBytes();
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            var after = await f.AccountBytes();
            Check(result.Items.Single().Code == "STORAGE_CONFLICT" && changed.SequenceEqual(after) && f.Store.Writes == 0);
        });
        yield return Case("owner account change during validation wins and old candidate cannot revoke its lease", async () =>
        {
            await using var cache = new CacheDocumentStore();
            await using var f = new TransferFixture(cache); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            f.HoldPlatform = "wy";
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Explicit cancel fences the preparation epoch, without a second transfer writer.
            await f.Owner.CallAsync("music.accounts.cancel", new { platform = "wy" });
            var current = await f.Owner.AcquireAsync("wy");
            f.Release.TrySetResult(); var result = await importing;
            Check(result.Items.Single().Code == "STORAGE_CONFLICT" && f.Owner.IsCurrent(current!) && f.Store.Writes == 0);
            Check(cache.ReadMemory(MusicAccountOwner.DetailsCacheNamespace).Data is null);
        });
        yield return Case("cancel during candidate stops pending platforms and preserves an existing valid lease", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); f.HoldPlatform = "wy";
            using var cancellation = new CancellationTokenSource(); using var plan = await f.Prepare();
            var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite, cancellation.Token);
            await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            var result = await importing; Check(f.Owner.IsCurrent(old!) && f.Store.Writes == 0 && f.Notifications.Count == 0);
            Check(result.Items[0].Status == "cancelled" && result.Items[0].Code == "CANCELLED" &&
                result.Items.Skip(1).All(item => item.Status == "cancelled") && f.Requests["tx"] == 0 && f.Requests["kg"] == 0);
        });
        yield return Case("cancel after CAS admission waits for a known receipt and retains successful platform", async () =>
        {
            foreach (bool loseReceipt in new[] { false, true })
            {
                await using var cache = new CacheDocumentStore();
                await using var f = new TransferFixture(cache); await f.Initialize(); f.Store.Hold = true; f.Store.LoseReceipt = loseReceipt;
                using var cancellation = new CancellationTokenSource(); using var plan = await f.Prepare();
                var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite, cancellation.Token);
                await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); Check(!importing.IsCompleted);
                Check(cache.ReadMemory(MusicAccountOwner.DetailsCacheNamespace).Data is null);
                f.Store.Release.TrySetResult(); var result = await importing;
                Check(result.Items[0].Status == (loseReceipt ? "failed" : "imported") &&
                    result.Items[0].Code == (loseReceipt ? "COMMIT_UNKNOWN" : null) &&
                    result.Items.Skip(1).All(item => item.Status == "cancelled") && f.Store.Writes == 1);
                if (loseReceipt) Check(cache.ReadMemory(MusicAccountOwner.DetailsCacheNamespace).Data is null);
                else
                {
                    var details = await f.Owner.ReadDetailsAsync("wy");
                    Check(details.State == "verified" && details.Membership.Vip == false &&
                        details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh);
                }
            }
        });
        yield return Case("storage conflict keeps old lease and unknown receipt is never automatically replayed", async () =>
        {
            await using (var f = new TransferFixture())
            {
                await f.Initialize(); await f.Seed(TransferFixture.Only("wy")); var old = await f.Owner.AcquireAsync("wy");
                f.Store.Conflict = true; using var plan = await f.Prepare(TransferFixture.Only("wy"));
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Code == "STORAGE_CONFLICT" && f.Owner.IsCurrent(old!) && f.Notifications.Count == 0);
            }
            await using (var f = new TransferFixture())
            {
                await f.Initialize(); f.Store.LoseReceipt = true; using var plan = await f.Prepare(TransferFixture.Only("wy"));
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Code == "COMMIT_UNKNOWN" && f.Store.Writes == 1 && await f.Owner.AcquireAsync("wy") is null);
                await Expect("INVALID_REQUEST", () => f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite));
                Check(f.Store.Writes == 1);
            }
        });
        yield return Case("successful replacement fences existing consumer lease and publishes exactly one settled notification", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); using var plan = await f.Prepare(TransferFixture.Only("wy"));
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            var current = await f.Owner.AcquireAsync("wy");
            Check(result.Items.Single().Status == "imported" && !f.Owner.IsCurrent(old!) && f.Owner.IsCurrent(current!) &&
                current!.Generation == old!.Generation + 1 && current.Scope != old.Scope && f.Notifications.SequenceEqual(new[] { "wy" }));
        });
        yield return Case("close cancels isolated candidate and drains it without granting new permissions", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); f.HoldPlatform = "wy";
            using var plan = await f.Prepare(); var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Owner.PrepareCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); var result = await importing;
            Check(!f.Owner.IsCurrent(old!) && f.Store.Writes == 0 && result.Items.All(item => item.Status != "imported") && f.Requests["tx"] == 0);
        });
        yield return Case("typed application tracks unknown receipts gates shutdown and uses explicit list reconciliation", async () =>
        {
            await using var f = new TransferFixture(); await f.Initialize(); await f.Owner.DisposeAsync();
            var application = f.Application();
            try
            {
                f.Store.LoseReceipt = true;
                using var file = await TransferFixture.File(TransferFixture.Only("wy"));
                using var plan = await application.PrepareAccountTransferAsync(file);
                var result = await application.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Code == "COMMIT_UNKNOWN" && f.Store.Writes == 1);
                await Expect("COMMIT_UNKNOWN", () => application.PrepareShutdownAsync());
                await application.CancelShutdownPreparationAsync(); f.Store.LoseReceipt = false;
                var observed = await application.ExecuteAsync("music.accounts.list"); Check(observed.GetProperty("ok").GetBoolean());
                await application.PrepareShutdownAsync(); Check(f.Store.Writes == 1);
                await application.DisposeAsync();
            }
            finally { await application.AbortAsync(); }
        });
        yield return Case("file and plan serialize and display without any credential fields", async () =>
        {
            await using var f = new TransferFixture(); using var file = await TransferFixture.File(TransferFixture.Shared());
            using var plan = await f.Owner.PrepareAccountTransferAsync(file);
            foreach (var text in new[] { JsonSerializer.Serialize(file), JsonSerializer.Serialize(plan), file.ToString(), plan.ToString() })
                Check(!text.Contains("SYNTHETIC", StringComparison.Ordinal) && !text.Contains("MUSIC_U", StringComparison.Ordinal));
            file.Dispose(); await ExpectDisposed(() => file.WriteAsync(new MemoryStream()));
            plan.Dispose(); await Expect("INVALID_REQUEST", () => f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite));
        });
    }
    private static TestCase Case(string name, Func<Task> run) => new("music-account-transfer: " + name, run);
    internal static void Check(bool value) { if (!value) throw new InvalidOperationException("Account transfer assertion"); }
    internal static async Task Expect(string code, Func<Task> run)
    {
        try { await run(); throw new InvalidOperationException("Expected finite transfer failure"); }
        catch (MusicAccountException error) when (error.Code == code) { Check(error.Message == code && error.InnerException is null); }
    }
    private static async Task ExpectDisposed(Func<Task> run)
    { try { await run(); throw new InvalidOperationException("Expected disposed payload"); } catch (ObjectDisposedException) { } }
    private sealed class FailingDetailsStore : IDocumentStore
    {
        internal int Writes;
        internal TaskCompletionSource Arrived { get; } = TransferFixture.Signal();
        internal TaskCompletionSource Release { get; } = TransferFixture.Signal();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) =>
            Task.FromResult(new DocumentSnapshot(null, DocumentFingerprint.Missing));
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected,
            CancellationToken token = default)
        {
            Writes++; Arrived.TrySetResult(); await Release.Task.WaitAsync(token);
            throw new IOException("SYNTHETIC_SECRET_CACHE_PATH");
        }
        public ValueTask DisposeAsync() { Release.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new IOException("SYNTHETIC_SECRET_PATH");
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("SYNTHETIC_SECRET_PATH");
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("SYNTHETIC_SECRET_PATH");
        public override void Flush() => throw new IOException("SYNTHETIC_SECRET_PATH");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class TransferFixture : IAsyncDisposable
{
    internal string Root { get; } = TestWorkspace.DataPath("account-transfer", Guid.NewGuid().ToString("N"));
    internal LocalDocumentStore Inner { get; }
    internal TransferStore Store { get; }
    internal MusicPlatformHttp Http { get; }
    internal MusicClientContextFixture Clients { get; }
    internal MusicAccountOwner Owner { get; }
    private readonly NativeMusicScriptTestRuntime runtime = new();
    internal Dictionary<string, int> Requests { get; } = new() { ["wy"] = 0, ["tx"] = 0, ["kg"] = 0 };
    internal List<string> Notifications { get; } = [];
    internal string? FailPlatform, HoldPlatform;
    internal int FailAt;
    internal string WyUserId = "71001", QqUserId = "72002";
    internal string? WyResponseDevice;
    internal TaskCompletionSource Arrived { get; } = Signal();
    internal TaskCompletionSource Release { get; } = Signal();
    internal TransferFixture(IDocumentStore? detailsCacheStore = null)
    {
        Directory.CreateDirectory(Root);
        Inner = new(Root, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
        Store = new(Inner); Http = MusicPlatformHttp.CreateWithHandler(new Handler(this)); Clients = new(Http, Inner);
        Owner = Clients.CreateAccountOwner(Store, detailsCacheStore: detailsCacheStore); Owner.SessionChanged += Notifications.Add;
    }
    internal MusicNativeApplication Application() => new(Store, runtime, new Handler(this));
    internal Task Initialize() => Clients.Owner.InitializeAsync();
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static JsonObject Shared() => JsonNode.Parse(System.IO.File.ReadAllBytes(Path.Combine(TestWorkspace.SourceRoot,
        "tests", "fixtures", "music-account-transfer", "accounts-v1.json")))!.AsObject();
    internal static JsonObject Only(string platform)
    {
        var input = Shared(); input["accounts"] = new JsonArray(input["accounts"]!.AsArray()
            .Single(row => row!["platform"]!.GetValue<string>() == platform)!.DeepClone()); return input;
    }
    internal static string Id(string platform) => platform switch { "wy" => "71001", "tx" => "72002", "kg" => "73003", _ => "" };
    internal static async Task<MusicAccountTransferFile> File(JsonObject input)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(input));
        return await MusicAccountTransferFile.ReadAsync(stream);
    }
    internal async Task<MusicAccountTransferPlan> Prepare(JsonObject? input = null)
    { using var file = await File(input ?? Shared()); return await Owner.PrepareAccountTransferAsync(file); }
    internal async Task Seed(JsonObject? input = null)
    {
        var rows = new JsonArray();
        foreach (var entry in (input ?? Shared())["accounts"]!.AsArray())
        {
            string platform = entry!["platform"]!.GetValue<string>();
            var value = new JsonObject { ["version"] = 1, ["generation"] = 1,
                ["cookie"] = string.Join("; ", entry["credentials"]!.AsObject().Select(field => field.Key + "=" + field.Value!.GetValue<string>())),
                ["profile"] = new JsonObject { ["platform"] = platform, ["userId"] = entry["userId"]!.GetValue<string>(), ["displayName"] = "Saved synthetic account" },
                ["scope"] = "synthetic-scope-" + platform, ["validatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            rows.Add(new JsonObject { ["key"] = AccountDocument.Key(platform), ["value"] = value });
        }
        var snapshot = await Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["version"] = 1, ["entries"] = rows });
        try { await Inner.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, snapshot.Fingerprint); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal async Task<byte[]> AccountBytes() => (await Inner.ReadAsync(MusicAccountOwner.DocumentNamespace)).Data ?? [];
    internal async Task<byte[]> ClientBytes() => (await Inner.ReadAsync(MusicPlatformClientContextOwner.DocumentNamespace)).Data ?? [];
    internal async Task ChangeStoredName(string platform)
    {
        var snapshot = await Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
        var root = JsonNode.Parse(snapshot.Data!)!;
        var row = root["entries"]!.AsArray().Single(item => item!["key"]!.GetValue<string>() == AccountDocument.Key(platform));
        row!["value"]!["profile"]!["displayName"] = "Changed synthetic account";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        try { await Inner.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, snapshot.Fingerprint); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(); Store.Release.TrySetResult();
        await Owner.DisposeAsync(); await Clients.DisposeAsync(); Http.Dispose(); await runtime.DisposeAsync(); await Inner.DisposeAsync();
    }
    private sealed class Handler(TransferFixture f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string host = request.RequestUri!.Host, path = request.RequestUri.AbsolutePath;
            string platform = host == "music.163.com" ? "wy" : host.Contains("qq.com", StringComparison.Ordinal) ? "tx" : "kg";
            int call = ++f.Requests[platform];
            if (f.HoldPlatform == platform && call == 1) { f.Arrived.TrySetResult(); await f.Release.Task.WaitAsync(token); }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); JsonObject data;
            if (platform == "wy")
            {
                bool profile = path == "/weapi/w/nuser/account/get";
                data = profile ? new() { ["code"] = (f.FailPlatform == "wy" || f.FailAt == call) ? 301 : 200,
                    ["account"] = new JsonObject { ["id"] = f.WyUserId }, ["profile"] = new JsonObject {
                        ["userId"] = f.WyUserId, ["nickname"] = "Synthetic WY", ["avatarUrl"] = "https://images.invalid/synthetic" } }
                    : new() { ["code"] = 200, ["data"] = new JsonObject { ["userId"] = f.WyUserId, ["now"] = now,
                        ["associator"] = new JsonObject { ["expireTime"] = 0 }, ["musicPackage"] = new JsonObject { ["expireTime"] = 0 },
                        ["redplus"] = new JsonObject { ["expireTime"] = 0 } } };
            }
            else if (platform == "tx")
            {
                string method = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["req_0"]!["method"]!.GetValue<string>();
                var value = method == "GetLoginUserInfo" ? new JsonObject { ["code"] = f.FailPlatform == "tx" ? 1000 : 0, ["musicid"] = f.QqUserId }
                    : method == "GetHomepageHeader" ? new JsonObject { ["Info"] = new JsonObject { ["BaseInfo"] = new JsonObject {
                        ["EncryptedUin"] = "SYNTHETIC_EUIN", ["Name"] = "Synthetic QQ", ["Avatar"] = "https://images.invalid/synthetic" } } }
                    : new JsonObject { ["svip"] = 0, ["identity"] = new JsonObject { ["HugeVip"] = 0 } };
                data = new() { ["code"] = 0, ["req_0"] = new JsonObject { ["code"] = 0, ["data"] = value } };
            }
            else data = path == "/v3/get_my_info" ? new() { ["status"] = 1, ["error_code"] = f.FailPlatform == "kg" ? 20018 : 0,
                    ["data"] = new JsonObject { ["userid"] = Id("kg"), ["nickname"] = "Synthetic KG", ["pic"] = "https://images.invalid/synthetic" } }
                : new() { ["status"] = 1, ["error_code"] = 0, ["data"] = new JsonObject { ["userid"] = Id("kg"), ["is_vip"] = 0 } };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(data)) };
            if (platform == "wy") response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_RESPONSE_DEVICE; Path=/");
            if (platform == "wy" && f.WyResponseDevice is { } changedDevice)
                response.Headers.TryAddWithoutValidation("Set-Cookie", "deviceId=" + changedDevice + "; Path=/");
            return response;
        }
    }
    internal sealed class TransferStore(LocalDocumentStore inner) : IDocumentStore
    {
        internal int Writes, Reads;
        internal bool Hold, LoseReceipt, Conflict;
        internal TaskCompletionSource Arrived { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        { if (ns == MusicAccountOwner.DocumentNamespace) Reads++; return inner.ReadAsync(ns, token); }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            if (ns != MusicAccountOwner.DocumentNamespace) return await inner.WriteAsync(ns, bytes, expected, token);
            Writes++;
            if (Hold) { Arrived.TrySetResult(); await Release.Task; }
            if (Conflict) throw new DocumentStoreException(DocumentStoreError.Conflict);
            var receipt = await inner.WriteAsync(ns, bytes, expected, token);
            if (LoseReceipt) throw new IOException("SYNTHETIC_SECRET_PATH");
            return receipt;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
