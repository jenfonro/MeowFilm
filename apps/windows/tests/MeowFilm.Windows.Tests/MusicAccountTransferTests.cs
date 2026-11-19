// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
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
            await using var f = new TransferFixture();
            using var plan = await f.Prepare();
            Check(plan.Conflicts.Count == 0);
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Select(item => item.Platform).SequenceEqual(new[] { "wy", "tx", "kg" }) &&
                result.Items.All(item => item.Status == "imported"));
            using var exported = await f.Owner.ExportAccountTransferAsync();
            Check(exported is not null);
            using var output = new MemoryStream(); await exported!.WriteAsync(output);
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), TransferFixture.Shared()));
            Check(f.Notifications.SequenceEqual(new[] { "wy", "tx", "kg" }));
            Check(f.Requests.Values.All(count => count == 1));
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
            await using var f = new TransferFixture();
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.Single().Status == "imported");
            Check((await f.Owner.ReadDetailsAsync("wy")).Membership.Vip == false);
            await using var reopened = new MusicAccountOwner(f.Store, f.Runtime.CreateAccountProtocol());
            var restored = await reopened.ReadDetailsAsync("wy");
            var session = await reopened.AcquireAsync("wy");
            Check(restored.State == "saved" && restored.Generation == 1 && restored.Profile?.UserId == TransferFixture.Id("wy") &&
                restored.Membership.Vip is null && restored.MembershipFreshness == MusicAccountDetailsFreshness.Missing &&
                session is not null && reopened.IsCurrent(session) && f.Store.Writes == 1 && f.Requests["wy"] == 1);
        });
        yield return Case("slow failed details persistence neither blocks import nor makes the next credential commit uncertain", async () =>
        {
            await using var backing = new FailingDetailsStore();
            await using var cache = new NativeTestDatabase(backing);
            await using var f = new TransferFixture(cache);
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
            await using var f = new TransferFixture();
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
        foreach (var missing in new[] { ("wy", "deviceId"), ("wy", "_ntes_nuid"), ("wy", "_ntes_nnid"),
            ("wy", "WNMCID"), ("kg", "KUGOU_API_GUID"), ("kg", "KUGOU_API_DEV"), ("kg", "KUGOU_API_MAC") })
            yield return Case("external optional device material reaches the protocol " + missing, async () =>
            {
                await using var f = new TransferFixture();
                var input = TransferFixture.Only(missing.Item1);
                input["accounts"]![0]!["credentials"]!.AsObject().Remove(missing.Item2);
                using var plan = await f.Prepare(input);
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Status == "imported");
                Check(f.Requests[missing.Item1] == 1 && f.Store.Writes == 1);
            });
        yield return Case("same UID is a conflict and skip never validates or writes that platform", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); using var plan = await f.Prepare();
            Check(plan.Conflicts.Select(item => item.Platform).SequenceEqual(new[] { "wy" }));
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Skip);
            Check(result.Items.Single(item => item.Platform == "wy").Status == "skipped" && f.Requests["wy"] == 0 &&
                f.Store.Writes == 2 && f.Owner.IsCurrent(old!));
            Check(result.Items.Count(item => item.Status == "imported") == 2);
        });
        yield return Case("cancel choice writes and validates nothing and a later batch captures conflicts again", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
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
                await using var cache = new NativeTestDatabase();
                await using var f = new TransferFixture(cache); await f.Seed(TransferFixture.Only("wy"));
                var old = await f.Owner.AcquireAsync("wy"); var bytes = await f.AccountBytes();
                await f.Owner.RefreshDetailsAsync("wy", 1);
                var cached = cache.ReadAsync(db => db.Revision(MusicAccountOwner.DetailsCacheNamespace)).GetAwaiter().GetResult();
                if (deviceMismatch) f.IdentityFailurePlatform = "wy"; else f.FailPlatform = "wy";
                using var plan = await f.Prepare(TransferFixture.Only("wy"));
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Status == "failed" && result.Items.Single().Code ==
                    (deviceMismatch ? "IDENTITY_MISMATCH" : "AUTH_EXPIRED"));
                var after = await f.AccountBytes();
                Check(f.Owner.IsCurrent(old!) && f.Notifications.Count == 0 && f.Store.Writes == 0 && bytes.SequenceEqual(after));
                Check(cache.ReadAsync(db => db.Revision(MusicAccountOwner.DetailsCacheNamespace)).GetAwaiter().GetResult() == cached);
            }
        });
        yield return Case("bad candidate cannot supersede an existing production protocol details read", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
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
        yield return Case("current external WY comma device association survives import and offline export", async () =>
        {
            await using var f = new TransferFixture();
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.Single().Status == "imported");
            using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            var fields = JsonNode.Parse(output.ToArray())!["accounts"]![0]!["credentials"]!;
            Check(fields["_ntes_nnid"]!.GetValue<string>().Contains(",1700000000000", StringComparison.Ordinal));
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), TransferFixture.Only("wy")));
        });
        yield return Case("wrong exact identity keeps old account while other valid platforms settle", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed();
            var old = await f.Owner.AcquireAsync("wy"); f.WyUserId = "99999";
            using var plan = await f.Prepare();
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Single(item => item.Platform == "wy").Code == "IDENTITY_MISMATCH" && f.Owner.IsCurrent(old!));
            Check(result.Items.Count(item => item.Status == "imported") == 2 && !f.Notifications.Contains("wy"));
        });
        yield return Case("QQ returned identity mismatch fails even when file and Cookie agree", async () =>
        {
            await using var f = new TransferFixture(); f.QqUserId = "99999";
            using var plan = await f.Prepare(TransferFixture.Only("tx"));
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Single().Code == "IDENTITY_MISMATCH" && f.Store.Writes == 0);
        });
        yield return Case("unknown credential fields are inert while invalid transmitted values still fail", async () =>
        {
            await using var f = new TransferFixture();
            var input = TransferFixture.Shared(); input["accounts"]![0]!["credentials"]!["unknown"] = new JsonObject { ["nested"] = "SYNTHETIC_UNKNOWN" };
            input["extra"] = true; input["accounts"]![0]!["extra"] = new JsonArray(1, 2);
            input["accounts"]![1]!["credentials"]!["musickey"] = "bad;injected=value";
            using var plan = await f.Prepare(input);
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Count(item => item.Status == "failed") == 1 &&
                result.Items.Single(item => item.Platform == "wy").Status == "imported" &&
                result.Items.Single(item => item.Platform == "kg").Status == "imported");
            Check(f.Requests["wy"] == 1 && f.Requests["tx"] == 0 && f.Store.Writes == 2);
            using var exported = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream();
            await exported!.WriteAsync(output);
            Check(!Encoding.UTF8.GetString(output.ToArray()).Contains("SYNTHETIC_UNKNOWN", StringComparison.Ordinal));
        });
        yield return Case("empty optional QQ credentials remain strings and KG opaque t1 is unchanged", async () =>
        {
            await using var f = new TransferFixture();
            var input = TransferFixture.Shared(); input["accounts"]![1]!["credentials"]!["refresh_token"] = "";
            using var plan = await f.Prepare(input);
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.All(item => item.Status == "imported"));
            using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), input));
        });
        yield return Case("unsupported Cookie representation fails without encoding or truncating opaque credential", async () =>
        {
            await using var f = new TransferFixture();
            var input = TransferFixture.Only("kg"); input["accounts"]![0]!["credentials"]!["t1"] = "SYNTHETIC;opaque,值";
            using var plan = await f.Prepare(input);
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            Check(result.Items.Single().Code == "INVALID_CREDENTIALS" && f.Requests["kg"] == 0 && f.Store.Writes == 0);
        });
        yield return Case("current external KG exact decimal t1 strings and opaque values roundtrip", async () =>
        {
            foreach (string value in new[] { "123456789012345678901234567890", "1234567890123456789012345678901", "-0.00125", "SYNTHETIC_T1_OPAQUE" })
            {
                await using var f = new TransferFixture();
                var input = TransferFixture.Only("kg"); input["accounts"]![0]!["credentials"]!["t1"] = value;
                using var plan = await f.Prepare(input);
                Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.Single().Status == "imported");
                using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
                Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), input));
            }
        });
        yield return Case("QQ optional protocol metadata reaches its actual consumer unchanged", async () =>
        {
            foreach (string field in new[] { "expired_at", "musickey_create_time", "key_expires_in", "login_type" })
                foreach (string value in new[] { "", "0", "3600", "9007199254740991", "00", "01", "-1", "1.0", "1e3", "9007199254740992", "999999999999999999999" })
                {
                    await using var f = new TransferFixture();
                    var input = TransferFixture.Only("tx"); input["accounts"]![0]!["credentials"]![field] = value;
                    using var plan = await f.Prepare(input);
                    var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                    Check(result.Items.Single().Status == "imported" && f.Requests["tx"] == 1 && f.Store.Writes == 1);
                    using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream();
                    await file!.WriteAsync(output);
                    Check(JsonNode.DeepEquals(JsonNode.Parse(output.ToArray()), input));
                }
        });
        yield return Case("current external QQ aliases retain numeric and empty credential semantics", async () =>
        {
            await using var f = new TransferFixture();
            var input = TransferFixture.Only("tx"); var fields = input["accounts"]![0]!["credentials"]!.AsObject();
            fields["loginType"] = fields["login_type"]!.DeepClone(); fields.Remove("login_type");
            fields["expiredAt"] = fields["expired_at"]!.DeepClone(); fields.Remove("expired_at");
            fields["refresh_token"] = ""; fields["keyExpiresIn"] = "3600"; fields["musickeyCreateTime"] = "1700000000";
            using var plan = await f.Prepare(input);
            Check((await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite)).Items.Single().Status == "imported");
            using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream(); await file!.WriteAsync(output);
            var exported = JsonNode.Parse(output.ToArray())!["accounts"]![0]!["credentials"]!;
            Check(exported["login_type"]!.GetValue<string>() == "2" && exported["expired_at"]!.GetValue<string>() == "1800000000" &&
                exported["refresh_token"]!.GetValue<string>() == "" && exported["key_expires_in"]!.GetValue<string>() == "3600" && exported["musickey_create_time"]!.GetValue<string>() == "1700000000");
        });
        yield return Case("current stored QQ optional numeric metadata is preserved on export", async () =>
        {
            foreach (string field in new[] { "expired_at", "musickey_create_time", "key_expires_in", "login_type" })
            {
                await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("tx"));
                await f.MutateStoredCookie("tx", cookie => cookie + "; " + field + "=01",
                    remove: field);
                var before = await f.AccountBytes();
                using var file = await f.Owner.ExportAccountTransferAsync(); using var output = new MemoryStream();
                await file!.WriteAsync(output);
                Check(JsonNode.Parse(output.ToArray())!["accounts"]![0]!["credentials"]![field]!.GetValue<string>() == "01");
                var after = await f.AccountBytes();
                Check(before.SequenceEqual(after) && f.Store.Writes == 0 && f.Requests["tx"] == 0);
            }
        });
        yield return Case("preparation rejects malformed shape version duplicate keys platforms and nonstring values before network", async () =>
        {
            await using var f = new TransferFixture();
            string valid = TransferFixture.Shared().ToJsonString();
            var invalids = new List<string> { "not json", "[]", valid.Replace("\"version\":1", "\"version\":2"),
                valid.Replace("\"version\":1", "\"version\":1,\"version\":1"), valid.Replace("\"platform\":\"wy\"", "\"platform\":\"kw\""),
                valid.Replace("\"platform\":\"tx\"", "\"platform\":\"wy\""), valid.Replace("\"MUSIC_U\":", "\"MUSIC_U\":\"x\",\"MUSIC_U\":"),
                valid.Replace("\"MUSIC_U\":\"SYNTHETIC_NETEASE_SESSION\"", "\"MUSIC_U\":123") };
            foreach (string invalid in invalids)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalid)); using var file = await MusicAccountTransferFile.ReadAsync(stream);
                await Expect("TRANSFER_FORMAT", () => f.Owner.PrepareAccountTransferAsync(file));
            }
            Check(f.Requests.Values.Sum() == 0 && f.Store.Writes == 0 && f.Store.Reads == 0);
        });
        yield return Case("actual account file over one MiB preserves complete content and file stream errors", async () =>
        {
            await using var f = new TransferFixture();
            var root = TransferFixture.Shared();
            root["extra"] = new string('x', 1024 * 1024 + 1);
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            using var input = new MemoryStream(bytes);
            using var large = await MusicAccountTransferFile.ReadAsync(input);
            using var output = new MemoryStream(); await large.WriteAsync(output);
            Check(output.ToArray().SequenceEqual(bytes));
            using var plan = await f.Owner.PrepareAccountTransferAsync(large);
            Check(plan.Entries.Count == root["accounts"]!.AsArray().Count && f.Store.Writes == 0);
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
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            await f.ChangeStoredName("wy"); var changed = await f.AccountBytes();
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            var after = await f.AccountBytes();
            Check(result.Items.Single().Code == "STORAGE_CONFLICT" && changed.SequenceEqual(after) && f.Store.Writes == 0);
        });
        yield return Case("owner account change during validation wins and old candidate cannot revoke its lease", async () =>
        {
            await using var cache = new NativeTestDatabase();
            await using var f = new TransferFixture(cache); await f.Seed(TransferFixture.Only("wy"));
            f.HoldPlatform = "wy";
            using var plan = await f.Prepare(TransferFixture.Only("wy"));
            var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Explicit cancel fences the preparation epoch, without a second transfer writer.
            await f.Owner.CallAsync("music.accounts.cancel", new { platform = "wy" });
            var current = await f.Owner.AcquireAsync("wy");
            f.Release.TrySetResult(); var result = await importing;
            Check(result.Items.Single().Code == "STORAGE_CONFLICT" && f.Owner.IsCurrent(current!) && f.Store.Writes == 0);
            Check((await cache.ReadDetailsAsync()).Count == 0);
        });
        yield return Case("cancel during candidate stops pending platforms and preserves an existing valid lease", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
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
                await using var cache = new NativeTestDatabase();
                await using var f = new TransferFixture(cache); f.Store.Hold = true; f.Store.LoseReceipt = loseReceipt;
                using var cancellation = new CancellationTokenSource(); using var plan = await f.Prepare();
                var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite, cancellation.Token);
                await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); Check(!importing.IsCompleted);
                Check((await cache.ReadDetailsAsync()).Count == 0);
                f.Store.Release.TrySetResult(); var result = await importing;
                Check(result.Items[0].Status == (loseReceipt ? "failed" : "imported") &&
                    result.Items[0].Code == (loseReceipt ? "COMMIT_UNKNOWN" : null) &&
                    result.Items.Skip(1).All(item => item.Status == "cancelled") && f.Store.Writes == 1);
                if (loseReceipt) Check((await cache.ReadDetailsAsync()).Count == 0);
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
                await f.Seed(TransferFixture.Only("wy")); var old = await f.Owner.AcquireAsync("wy");
                f.Store.Conflict = true; using var plan = await f.Prepare(TransferFixture.Only("wy"));
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Code == "STORAGE_CONFLICT" && f.Owner.IsCurrent(old!) && f.Notifications.Count == 0);
            }
            await using (var f = new TransferFixture())
            {
                f.Store.LoseReceipt = true; using var plan = await f.Prepare(TransferFixture.Only("wy"));
                var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
                Check(result.Items.Single().Code == "COMMIT_UNKNOWN" && f.Store.Writes == 1 && await f.Owner.AcquireAsync("wy") is null);
                await Expect("INVALID_REQUEST", () => f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite));
                Check(f.Store.Writes == 1);
            }
        });
        yield return Case("successful replacement fences existing consumer lease and publishes exactly one settled notification", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); using var plan = await f.Prepare(TransferFixture.Only("wy"));
            var result = await f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            var current = await f.Owner.AcquireAsync("wy");
            Check(result.Items.Single().Status == "imported" && !f.Owner.IsCurrent(old!) && f.Owner.IsCurrent(current!) &&
                current!.Generation == old!.Generation + 1 && current.Scope != old.Scope && f.Notifications.SequenceEqual(new[] { "wy" }));
        });
        yield return Case("close cancels isolated candidate and drains it without granting new permissions", async () =>
        {
            await using var f = new TransferFixture(); await f.Seed(TransferFixture.Only("wy"));
            var old = await f.Owner.AcquireAsync("wy"); f.HoldPlatform = "wy";
            using var plan = await f.Prepare(); var importing = f.Owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
            await f.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Owner.PrepareCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); var result = await importing;
            Check(!f.Owner.IsCurrent(old!) && f.Store.Writes == 0 && result.Items.All(item => item.Status != "imported") && f.Requests["tx"] == 0);
        });
        yield return Case("typed application tracks unknown receipts gates shutdown and uses explicit list reconciliation", async () =>
        {
            await using var f = new TransferFixture(); await f.Owner.DisposeAsync();
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
    private sealed class FailingDetailsStore : NativeTestDatabase
    {
        internal TaskCompletionSource Arrived { get; } = TransferFixture.Signal();
        internal TaskCompletionSource Release { get; } = TransferFixture.Signal();
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            Interlocked.Increment(ref Writes); Arrived.TrySetResult(); await Release.Task.WaitAsync(token);
            throw new IOException("SYNTHETIC_SECRET_CACHE_PATH");
        }
        public override ValueTask DisposeAsync() { Release.TrySetResult(); return base.DisposeAsync(); }
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
    internal IStorageDatabase Inner { get; }
    private readonly bool ownsDatabase;
    internal TransferStore Store { get; }
    internal MusicPlatformRuntimeFixture Runtime { get; } = new();
    internal MusicAccountOwner Owner { get; }
    internal Dictionary<string, int> Requests { get; } = new() { ["wy"] = 0, ["tx"] = 0, ["kg"] = 0 };
    internal List<string> Notifications { get; } = [];
    internal string? FailPlatform, HoldPlatform;
    internal int FailAt;
    internal string WyUserId = "71001", QqUserId = "72002";
    internal string? IdentityFailurePlatform;
    internal TaskCompletionSource Arrived { get; } = Signal();
    internal TaskCompletionSource Release { get; } = Signal();
    internal TransferFixture(IStorageDatabase? database = null)
    {
        Directory.CreateDirectory(Root);
        Inner = database ?? new NativeDatabase(Root);
        ownsDatabase = database is null;
        Store = new(Inner);
        Runtime.OnRequest = Respond;
        var scripts = new NativeMusicScriptTestRuntime();
        Runtime.OnOtherCall = (call, token) => scripts.CallAsync(call.Method, call.Payload, token);
        Owner = Runtime.CreateAccountOwner(Store); Owner.SessionChanged += Notifications.Add;
    }
    internal MusicNativeApplication Application() => new(Store, MusicTestStores.For(Store), Runtime);
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
        await using var runtime = new MusicPlatformRuntimeFixture();
        runtime.OnRequest = (call, _) => {
            if (call.Operation == "qr.cancel") return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { cancelled = true }));
            MusicAccountTransferTests.Check(call.Operation == "account.verify");
            var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(call.Platform, Id(call.Platform)).GetRawText())!.AsObject();
            value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(value));
        };
        await using var owner = runtime.CreateAccountOwner(Inner);
        using var file = await File(input ?? Shared());
        using var plan = await owner.PrepareAccountTransferAsync(file);
        var result = await owner.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Overwrite);
        MusicAccountTransferTests.Check(result.Items.All(item => item.Status == "imported"));
    }
    internal async Task<byte[]> AccountBytes() => (await Inner.ReadAccountsAsync()).Data ?? [];
    internal async Task ChangeStoredName(string platform)
    {
        var snapshot = await Inner.ReadAccountsAsync();
        var root = JsonNode.Parse(snapshot.Data!)!;
        var row = root["entries"]!.AsArray().Single(item => item!["key"]!.GetValue<string>() == AccountDocument.Key(platform));
        row!["value"]!["profile"]!["displayName"] = "Changed synthetic account";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        try { await Inner.WriteAccountsAsync(bytes, snapshot.Revision); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal async Task MutateStoredCookie(string platform, Func<string, string> change, string remove)
    {
        var snapshot = await Inner.ReadAccountsAsync();
        try
        {
            var root = JsonNode.Parse(snapshot.Data!)!;
            var row = root["entries"]!.AsArray().Single(item => item!["key"]!.GetValue<string>() == AccountDocument.Key(platform))!["value"]!;
            string cookie = row["cookie"]!.GetValue<string>();
            cookie = string.Join("; ", cookie.Split("; ").Where(part => !part.StartsWith(remove + "=", StringComparison.Ordinal)));
            row["cookie"] = change(cookie);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await Inner.WriteAccountsAsync(bytes, snapshot.Revision); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(snapshot.Data!); }
    }
    private async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
    {
        if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
        MusicAccountTransferTests.Check(call.Operation is "account.verify" or "account.profile");
        string platform = call.Platform;
        int number = ++Requests[platform];
        if (HoldPlatform == platform && number == 1) { Arrived.TrySetResult(); await Release.Task.WaitAsync(token); }
        if (FailPlatform == platform || FailAt == number) throw new MusicPlatformException("AUTH_EXPIRED");
        if (IdentityFailurePlatform == platform) throw new MusicPlatformException("IDENTITY_MISMATCH");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Json(new MusicAccountDetailsObservation(
            new(platform, platform == "wy" ? WyUserId : platform == "tx" ? QqUserId : Id(platform), "Synthetic account"),
            now, new(new(false, "none", null), now, now + 86400000L, []))).GetRawText())!.AsObject();
        if (call.Operation == "account.verify") value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
        return MusicPlatformRuntimeFixture.Json(value);
    }
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(); Store.Release.TrySetResult();
        await Owner.DisposeAsync(); await Runtime.DisposeAsync(); if (ownsDatabase) await Inner.DisposeAsync();
    }
    internal sealed class TransferStore(IStorageDatabase inner) : NativeTestDatabase(inner)
    {
        internal bool Hold, LoseReceipt, Conflict;
        internal TaskCompletionSource Arrived { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            var receipt = await base.WriteAsync(db =>
            {
                if (Hold) { Arrived.TrySetResult(); Release.Task.GetAwaiter().GetResult(); }
                if (Conflict) throw new DocumentStoreException(DocumentStoreError.Conflict);
                return write(db);
            }, token, retainedBytes, commit);
            if (LoseReceipt) throw new IOException("SYNTHETIC_SECRET_PATH");
            return receipt;
        }
    }
}
