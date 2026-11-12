using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    private static IEnumerable<TestCase> NetworkPlaylistCases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
        {
            string p = platform;
            yield return new($"Music network {p} disabled save and remove are local metadata only", () => NetworkLocalOnly(p));
        }
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            string p = platform;
            yield return new($"Music network {p} subscribes once and unsubscribes only the account relation", () => NetworkSubscription(p));
            yield return new($"Music network {p} subscription timeout is confirmed without replay", () => NetworkTimeout(p, true));
            yield return new($"Music network {p} subscription timeout stays fenced across runs and reopen until its late effect", () => NetworkTimeout(p, false));
            yield return new($"Music network {p} unsubscribe timeout stays fenced across runs and reopen until its late effect", () => NetworkUnsubscribeTimeout(p));
            yield return new($"Music network {p} subscription import rejects previously removed identity handles across reopen", () => NetworkReimportCas(p));
        }
        foreach (string platform in new[] { "wy", "tx" })
        {
            string p = platform;
            yield return new($"Music network {p} owned playlist never dispatches a subscription or token", () => NetworkOwnedTarget(p));
        }
        yield return new("Music network same-name subscriptions never join managed member fusion", NetworkMixedNames);
        yield return new("Music network first enable imports subscriptions before adding local references", NetworkFirstEnable);
        yield return new("Music network external changes and disabled local intent reconcile separately", NetworkExternalChanges);
        yield return new("Music network directed mutation does not run other enabled platforms", NetworkPlatformIsolation);
        yield return new("Music network remote rejection preserves local save and reports incomplete sync", NetworkRejected);
        yield return new("Music network pending subscription cannot replay on five-minute scheduler ticks", NetworkScheduledPending);
        yield return new("Music network missing account preserves local save without a request", NetworkMissingAccount);
        yield return new("Music network delayed read from replaced account cannot dispatch", () => NetworkAccountReplacement(false));
        yield return new("Music network delayed write from replaced account cannot acknowledge", () => NetworkAccountReplacement(true));
        yield return new("Music network disabling cancels a running mutation and retains local intent", NetworkDisable);
        yield return new("Music network disabled before directed dispatch returns a local-only receipt", NetworkDisabledBeforeRound);
        yield return new("Music network unknown before CAS cannot dispatch", () => NetworkUnknownCas(false));
        yield return new("Music network unknown after CAS retains the fence until the desired presence is observed", () => NetworkUnknownCas(true));
        yield return new("Music network KG unsubscribe lost response retains account list fence", NetworkKgUnsubscribeRecovery);
        yield return new("Music network KG unsubscribe codec targets account list and type one", NetworkKgUnsubscribeRequest);
        foreach (string platform in new[] { "tx", "kg" })
            foreach (string defect in new[] { "none", "members", "same-presence", "managed-target", "account", "remote", "locator" })
            {
                string p = platform, d = defect;
                yield return new($"Music network {p} persisted subscription fence format {d}", () => NetworkFenceFormat(p, d));
            }
    }

    private static string NetworkRemoteId(string platform) => platform == "kg" ? "global-source-301" : "301";
    private static Entry NetworkTarget(Fixture f, string name = "Network")
    {
        string id = NetworkRemoteId(f.Server.Platform);
        var target = new Entry(f.Server.Platform == "kg" ? "1:901" : id, name, writable: false)
        { GlobalId = id, CreatorListId = "301", Cover = "https://art.example.test/network.jpg" };
        target.Add(1); target.Add(2);
        f.Server.SubscriptionTargets[id] = target;
        return target;
    }
    private static Task<JsonElement> SaveNetwork(Fixture f, string? platform = null, string? id = null, string name = "Network",
        CancellationToken cancellationToken = default) =>
        f.Owner.ExecuteAsync("music.library.network.save", new
        {
            platform = platform ?? f.Server.Platform,
            id = id ?? NetworkRemoteId(platform ?? f.Server.Platform),
            name,
            cover = "https://art.example.test/network.jpg",
            count = 2
        }, cancellationToken);
    private static Task<JsonElement> RemoveNetwork(Fixture f, JsonElement reference) =>
        f.Owner.ExecuteAsync("music.library.network.remove", new
        {
            id = reference.GetProperty("id").GetString(),
            ifRevision = reference.GetProperty("revision").GetInt64()
        });
    private static JsonElement NetworkSync(JsonElement receipt)
    {
        Check(Ok(receipt).GetProperty("localSaved").GetBoolean());
        return receipt.GetProperty("sync");
    }
    private static async Task SetNetworkEnabled(Fixture f, bool enabled)
    {
        var before = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
        Ok(await f.Sync.ExecuteAsync("music.librarySync.set", new
        {
            platform = f.Server.Platform, favorites = false, playlists = enabled,
            ifRevision = before.GetProperty("revision").GetInt64(), confirm = true
        }));
    }
    private static int LocalSongCount(JsonElement library) => library.GetProperty("songs").EnumerateObject().Count();

    private static async Task NetworkLocalOnly(string platform)
    {
        await using var f = new Fixture();
        var saved = Ok(await SaveNetwork(f, platform));
        Check(S(NetworkSync(saved), "state") == "local-only");
        int writes = f.Store.Writes;
        var again = Ok(await SaveNetwork(f, platform));
        Check(f.Store.Writes == writes && again.GetProperty("collection").GetRawText() == saved.GetProperty("collection").GetRawText());
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(LocalSongCount(snapshot.Library) == 0 && snapshot.Library.GetProperty("collections").GetArrayLength() == 1 &&
            snapshot.Library.GetProperty("networkPlaylists").GetArrayLength() == 1);
        var removed = Ok(await RemoveNetwork(f, saved.GetProperty("collection")));
        Check(!removed.GetProperty("present").GetBoolean() && S(NetworkSync(removed), "state") == "local-only");
        Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 0);
        Ok(await f.Run());
        Check(f.Server.Reads == 0 && f.Server.Writes == 0 && f.Server.Creates == 0);
    }

    private static async Task NetworkSubscription(string platform)
    {
        await using var f = new Fixture(platform); var target = NetworkTarget(f); await f.Enable(false, true);
        var saved = Ok(await SaveNetwork(f)); Ok(NetworkSync(saved));
        Check(f.Server.SubscriptionWrites == 1 && f.Server.Creates == 0 && f.Server.MemberReads == 0 && f.Server.SearchReads == 0);
        var request = f.Server.LastWrite!;
        if (platform == "wy")
        {
            Check(f.Tokens.Calls == 1);
            CheckWySubscription(f.Server, true, TokenRuntime.Token(1));
        }
        else if (platform == "tx")
        {
            Check(request["req_0"]!["module"]!.ToString() == "music.musicasset.PlaylistFavWrite" &&
                request["req_0"]!["method"]!.ToString() == "FavPlaylist");
            var param = request["req_0"]!["param"]!;
            Check(param["uin"]!.ToString() == "synthetic-euin" && param["v_playlistId"]!.AsArray().Single()!.ToString() == "301");
        }
        else
        {
            Check(f.Server.MetadataReads == 1 && request["type"]!.GetValue<int>() == 1 &&
                request["list_create_gid"]!.ToString() == target.GlobalId &&
                request["list_create_userid"]!.ToString() == "77" && request["list_create_listid"]!.ToString() == "301");
        }
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(LocalSongCount(snapshot.Library) == 0 && snapshot.Library.GetProperty("collections").GetArrayLength() == 1);
        var baseline = snapshot.Library.GetProperty("networkPlaylists")[0].GetProperty("subscription");
        Check(baseline.GetProperty("present").GetBoolean() && S(baseline, "accountId") == "42");
        if (platform == "kg") Check(S(baseline.GetProperty("locator"), "listId") == "901");
        Ok(NetworkSync(await SaveNetwork(f)));
        Check(f.Server.SubscriptionWrites == 1, "Repeat favorite must not replay a confirmed subscription");
        if (platform == "wy") Check(f.Tokens.Calls == 1, "Confirmed subscription must not acquire another token");
        int cloudWrites = 0;
        if (platform == "kg")
            await InstallCloud(f, async (message, token) =>
            {
                cloudWrites++;
                Check(message.RequestUri!.AbsolutePath == "/v2/delete_list" &&
                    CloudQuery(message)["userid"] == "42" && (await message.Content!.ReadAsByteArrayAsync(token)).Length > 0);
                f.Server.Entries.Remove(target);
                // The encrypted response is lost; authoritative readback, not this payload, confirms removal.
                return [1, 2, 3];
            });
        var reference = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0];
        Ok(NetworkSync(await RemoveNetwork(f, reference)));
        Check(!f.Server.Entries.Contains(target) && target.Songs.SequenceEqual([1, 2]) &&
            f.Server.SubscriptionTargets[NetworkRemoteId(platform)] == target);
        if (platform == "wy")
        {
            Check(f.Server.Operations.SequenceEqual(["/eapi/playlist/subscribe", "/eapi/playlist/unsubscribe"]) && f.Tokens.Calls == 2);
            CheckWySubscription(f.Server, false, TokenRuntime.Token(2));
        }
        else Check(platform == "tx" ? f.Server.Operations.SequenceEqual(["FavPlaylist", "CancelFavPlaylist"]) : cloudWrites == 1);
        Check(f.Server.MemberReads == 0 && f.Server.Creates == 0);
        if (platform == "wy") CheckTokenNotStored(f, TokenRuntime.Token(1), TokenRuntime.Token(2));
        Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 0);
        Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }

    private static async Task NetworkMixedNames()
    {
        await using var f = new Fixture(); var owned = new Entry("701", "Same"); owned.Add(1);
        var subscription = new Entry("702", "Same", writable: false); subscription.Add(2);
        f.Server.Entries.AddRange([owned, subscription]);
        Ok(await SaveNetwork(f, "kw", "702", "Same")); Ok(await SaveNetwork(f, "mg", "702", "Same"));
        await f.Enable(false, true); Ok(await f.Run());
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("collections").GetArrayLength() == 2 && LocalSongCount(snapshot.Library) == 1 &&
            snapshot.Library.GetProperty("networkPlaylists").GetArrayLength() == 3);
        var managed = snapshot.Library.GetProperty("collections")[1];
        Check(managed.GetProperty("bindings").GetArrayLength() == 1 && S(managed.GetProperty("bindings")[0], "id") == "701");
        Check(f.Server.SubscriptionMemberReads == 0 && f.Server.MemberReads == 2 && f.Server.SearchReads == 0 &&
            f.Server.Writes == 0 && f.Server.Creates == 0);
        var list = Ok(await f.Owner.ExecuteAsync("music.library.list")).GetProperty("items");
        Check(list.EnumerateArray().Count(item => S(item, "name") == "Same") == 4);
    }
    private static async Task NetworkOwnedTarget(string platform)
    {
        await using var f = new Fixture(platform);
        var owned = new Entry("701", "Owned"); owned.Add(1); f.Server.Entries.Add(owned);
        await f.Enable(false, true); Ok(await f.Run());
        var managed = (await f.Owner.ReadLibrarySyncAsync()).Library.GetProperty("collections").GetRawText();
        int memberReads = f.Server.MemberReads;
        var saved = Ok(await SaveNetwork(f, id: "701", name: "Owned"));
        var sync = Ok(NetworkSync(saved));
        Check(S(sync.GetProperty("status"), "phase") == "partial");
        Check(sync.GetProperty("status").GetProperty("issues").EnumerateArray().Any(issue => S(issue, "code") == "READ_ONLY"));
        Check(f.Server.Writes == 0 && f.Server.SubscriptionWrites == 0 && f.Tokens.Calls == 0);
        Check(f.Server.MemberReads == memberReads && f.Server.SubscriptionMemberReads == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null);
        Check(snapshot.Library.GetProperty("collections").GetRawText() == managed);
        var references = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items");
        Check(references.GetArrayLength() == 1 && S(references[0], "remoteId") == "701");
        Check(f.Server.Entries.Contains(owned) && owned.Songs.SequenceEqual([1]));
    }
    private static async Task NetworkFirstEnable()
    {
        await using var f = new Fixture(); var localTarget = NetworkTarget(f);
        var official = new Entry("702", "Existing subscription", writable: false); official.Add(3); f.Server.Entries.Add(official);
        Ok(await SaveNetwork(f));
        await f.Enable(false, true); Ok(await f.Run());
        Check(f.Server.Entries.Contains(official) && f.Server.Entries.Contains(localTarget) &&
            f.Server.Operations.SequenceEqual(["FavPlaylist"]) && f.Server.SubscriptionMemberReads == 0);
        var list = Ok(await f.Owner.ExecuteAsync("music.library.network.list"));
        Check(list.GetProperty("total").GetInt64() == 2);
    }
    private static async Task NetworkExternalChanges()
    {
        await using var f = new Fixture(); var target = NetworkTarget(f); await f.Enable(false, true);
        Ok(NetworkSync(await SaveNetwork(f))); f.Server.Entries.Remove(target); Ok(await f.Run());
        Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 0 &&
            f.Server.SubscriptionWrites == 1);
        f.Server.Entries.Add(target); Ok(await f.Run());
        var reference = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0];
        await SetNetworkEnabled(f, false); int reads = f.Server.Reads;
        var removed = Ok(await RemoveNetwork(f, reference)); Check(S(NetworkSync(removed), "state") == "local-only");
        Check(f.Server.Reads == reads && f.Server.SubscriptionWrites == 1 && f.Server.Entries.Contains(target));
        await SetNetworkEnabled(f, true); Ok(await f.Run());
        Check(f.Server.Operations.SequenceEqual(["FavPlaylist", "CancelFavPlaylist"]) &&
            Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 0);
    }
    private static async Task NetworkReimportCas(string platform)
    {
        await using var f = new Fixture(platform); var target = NetworkTarget(f);
        var stale = Ok(await SaveNetwork(f)).GetProperty("collection");
        Ok(await RemoveNetwork(f, stale));
        Check((await f.Owner.ReadLibrarySyncAsync()).Library.GetProperty("networkPlaylists").GetArrayLength() == 0);
        f.Server.Entries.Add(target); await f.Enable(false, true); Ok(await f.Run());
        var current = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0];
        Check(S(current, "id") == S(stale, "id") &&
            current.GetProperty("revision").GetInt64() > stale.GetProperty("revision").GetInt64());
        int writes = f.Store.Writes, requests = f.Server.Reads + f.Server.Writes; var bytes = f.Store.Data!.ToArray();
        Code(await RemoveNetwork(f, stale), "REVISION_CONFLICT");
        await f.Restart();
        Code(await RemoveNetwork(f, stale), "REVISION_CONFLICT");
        var reopened = Ok(await f.Owner.ExecuteAsync("music.library.network.list"));
        Check(reopened.GetProperty("total").GetInt64() == 1 &&
            reopened.GetProperty("items")[0].GetRawText() == current.GetRawText() &&
            f.Store.Writes == writes && f.Store.Data.SequenceEqual(bytes) &&
            f.Server.Reads + f.Server.Writes == requests && f.Server.SubscriptionWrites == 0 &&
            f.Server.MemberReads == 0 && f.Server.Creates == 0);
    }
    private static async Task NetworkPlatformIsolation()
    {
        await using var f = new Fixture(); NetworkTarget(f);
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            var before = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
            Ok(await f.Sync.ExecuteAsync("music.librarySync.set", new
            {
                platform, favorites = true, playlists = true,
                ifRevision = before.GetProperty("revision").GetInt64(), confirm = true
            }));
        }
        Ok(NetworkSync(await SaveNetwork(f)));
        Check(f.Server.Operations.SequenceEqual(["FavPlaylist"]) && f.Server.MemberReads == 0 && f.Server.Creates == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("accounts").EnumerateObject().Select(p => p.Name).SequenceEqual(["tx"]));
    }
    private static async Task NetworkRejected()
    {
        await using var f = new Fixture(); var target = NetworkTarget(f); await f.Enable(false, true); f.Server.SubscriptionRejected = true;
        var sync = NetworkSync(await SaveNetwork(f)); Code(sync, "WRITE_UNCONFIRMED");
        Check(S(sync.GetProperty("status"), "phase") == "blocked");
        Check(f.Server.SubscriptionWrites == 1 && !f.Server.Entries.Any(entry => !entry.Writable));
        Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 1);
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); var pending = snapshot.State.GetProperty("pending");
        Check(S(pending, "operation") == "subscribe" &&
            snapshot.State.GetProperty("status").GetProperty("issues").EnumerateArray().Any(issue => S(issue, "code") == "WRITE_UNCONFIRMED"));
        f.Server.SubscriptionRejected = false;
        for (int reopen = 0; reopen < 2; reopen++)
        {
            if (reopen > 0) await f.Restart();
            Code(await f.Run(), "WRITE_UNCONFIRMED");
            snapshot = await f.Owner.ReadLibrarySyncAsync();
            Check(snapshot.State.GetProperty("pending").GetRawText() == pending.GetRawText() &&
                snapshot.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").GetProperty("present").GetBoolean() &&
                f.Server.SubscriptionWrites == 1 && !f.Server.Entries.Contains(target));
        }
        f.Server.Entries.Add(target); Ok(await f.Run());
        snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            snapshot.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").ValueKind == JsonValueKind.Null &&
            f.Server.SubscriptionWrites == 1);
    }
    private static async Task NetworkMissingAccount()
    {
        await using var f = new Fixture(); await f.Enable(false, true); f.Sessions.Values.Remove("tx");
        Code(NetworkSync(await SaveNetwork(f)), "AUTH_REQUIRED");
        Check(f.Server.Reads == 0 && f.Server.Writes == 0 &&
            Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 1);
    }
    private static async Task NetworkTimeout(string platform, bool applied)
    {
        await using var f = new Fixture(platform); var target = NetworkTarget(f); await f.Enable(false, true);
        f.Server.DropWrites = !applied; f.Server.ThrowAfterSubscription = true; f.Server.FailSubscriptionReadback = true;
        Code(NetworkSync(await SaveNetwork(f)), "UPSTREAM_ERROR");
        var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        Check(f.Server.SubscriptionWrites == 1 && S(pending, "operation") == "subscribe");
        await f.Restart(); f.Server.FailReads = false; f.Server.FailSubscriptionReadback = false; f.Server.DropWrites = false;
        if (!applied)
        {
            for (int reopen = 0; reopen < 2; reopen++)
            {
                if (reopen > 0) await f.Restart();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Code(await f.Run(), "WRITE_UNCONFIRMED");
                    var blocked = await f.Owner.ReadLibrarySyncAsync();
                    var reference = blocked.Library.GetProperty("networkPlaylists")[0];
                    Check(blocked.State.GetProperty("pending").GetRawText() == pending.GetRawText() &&
                        reference.GetProperty("present").GetBoolean() && reference.GetProperty("intent").GetProperty("present").GetBoolean() &&
                        !reference.GetProperty("subscription").GetProperty("present").GetBoolean() &&
                        f.Server.SubscriptionWrites == 1 && !f.Server.Entries.Contains(target));
                }
            }
            f.Server.Entries.Add(target);
        }
        Ok(await f.Run()); Ok(await f.Run());
        var settled = await f.Owner.ReadLibrarySyncAsync();
        Check(f.Server.SubscriptionWrites == 1 && settled.State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            settled.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").ValueKind == JsonValueKind.Null &&
            settled.Library.GetProperty("networkPlaylists")[0].GetProperty("subscription").GetProperty("present").GetBoolean());
        if (platform == "wy") Check(f.Tokens.Calls == 1, "Readback must not request a new token or replay");
        Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 1 &&
            f.Server.MemberReads == 0 && f.Server.Creates == 0);
    }
    private static async Task NetworkUnsubscribeTimeout(string platform)
    {
        await using var f = new Fixture(platform); var target = NetworkTarget(f); f.Server.Entries.Add(target);
        await f.Enable(false, true); Ok(await f.Run()); int cloudWrites = 0;
        Task<byte[]> Unsubscribe(HttpRequestMessage request, CancellationToken token)
        {
            cloudWrites++;
            Check(request.RequestUri!.AbsolutePath == "/v2/delete_list");
            if (cloudWrites == 1) f.Server.FailReads = true;
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
        if (platform == "kg") await InstallCloud(f, Unsubscribe);
        else { f.Server.DropWrites = true; f.Server.ThrowAfterSubscription = true; f.Server.FailSubscriptionReadback = true; }
        var reference = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0];
        Code(NetworkSync(await RemoveNetwork(f, reference)), "UPSTREAM_ERROR");
        var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        Check(S(pending, "operation") == "unsubscribe" && pending.GetProperty("beforePresent").GetBoolean() &&
            !pending.GetProperty("desiredPresent").GetBoolean());
        if (platform == "kg") Check(S(pending.GetProperty("locator"), "listId") == "901");
        f.Server.FailReads = false; f.Server.FailSubscriptionReadback = false; f.Server.DropWrites = false;
        for (int reopen = 0; reopen < 2; reopen++)
        {
            if (reopen > 0)
            {
                await f.Restart();
                if (platform == "kg") await InstallCloud(f, Unsubscribe);
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Code(await f.Run(), "WRITE_UNCONFIRMED");
                var blocked = await f.Owner.ReadLibrarySyncAsync();
                var current = blocked.Library.GetProperty("networkPlaylists")[0];
                Check(blocked.State.GetProperty("pending").GetRawText() == pending.GetRawText() &&
                    !current.GetProperty("present").GetBoolean() && !current.GetProperty("intent").GetProperty("present").GetBoolean() &&
                    current.GetProperty("subscription").GetProperty("present").GetBoolean() && f.Server.Entries.Contains(target) &&
                    (platform != "kg" ? f.Server.SubscriptionWrites == 1 : cloudWrites == 1));
            }
        }
        f.Server.Entries.Remove(target); Ok(await f.Run()); Ok(await f.Run());
        var settled = await f.Owner.ReadLibrarySyncAsync();
        Check(settled.State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            settled.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").ValueKind == JsonValueKind.Null &&
            !settled.Library.GetProperty("networkPlaylists")[0].GetProperty("subscription").GetProperty("present").GetBoolean() &&
            (platform != "kg" ? f.Server.SubscriptionWrites == 1 : cloudWrites == 1));
        Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 0 &&
            target.Songs.SequenceEqual([1, 2]) && f.Server.MemberReads == 0 && f.Server.Creates == 0);
        if (platform == "wy") Check(f.Tokens.Calls == 1, "Unsubscribe recovery must remain read-only");
    }
    private static async Task NetworkScheduledPending()
    {
        var clock = new Clock(); await using var f = new Fixture(clock: clock);
        NetworkTarget(f); await f.Enable(false, true); f.Server.DropWrites = true; f.Server.ThrowAfterSubscription = true;
        Code(NetworkSync(await SaveNetwork(f)), "WRITE_UNCONFIRMED");
        var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        f.Server.DropWrites = false; await f.Sync.StartAsync(); Check(clock.Timers.Count == 1); int reads = f.Server.Reads;
        for (int tick = 0; tick < 3; tick++)
        {
            clock.Advance(TimeSpan.FromMinutes(5)); await Task.Delay(30);
            Check(f.Server.Reads == reads && f.Server.SubscriptionWrites == 1);
        }
        var blocked = await f.Owner.ReadLibrarySyncAsync();
        Check(blocked.State.GetProperty("pending").GetRawText() == pending.GetRawText() &&
            blocked.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").GetProperty("present").GetBoolean());
    }
    private static async Task NetworkAccountReplacement(bool afterWrite)
    {
        await using var f = new Fixture(); NetworkTarget(f); await f.Enable(false, true);
        if (afterWrite) f.Server.AfterWrite = () => f.Sessions.Values["tx"] = Sessions.Make("tx", 2, "77");
        else { f.Server.Entered = new(); f.Server.Release = new(); }
        var entered = f.Server.Entered; var saving = SaveNetwork(f);
        if (!afterWrite)
        {
            await entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Sessions.Values["tx"] = Sessions.Make("tx", 2, "77"); f.Server.Release!.SetResult();
        }
        Code(NetworkSync(await saving), "STALE_SESSION");
        Check(f.Server.SubscriptionWrites == (afterWrite ? 1 : 0));
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(S(snapshot.Library.GetProperty("networkPlaylists")[0].GetProperty("intent"), "accountId") == "42");
        Code(await f.Run(), "ACCOUNT_REPLACED"); Check(f.Server.SubscriptionWrites == (afterWrite ? 1 : 0));
        if (afterWrite)
        {
            Check(snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Object);
            f.Sessions.Values["tx"] = Sessions.Make("tx", 3, "42"); Ok(await f.Run());
            Check(f.Server.SubscriptionWrites == 1 && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
        }
        else
        {
            var before = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
            Ok(await f.Sync.ExecuteAsync("music.librarySync.resetAccount", new
            { platform = "tx", ifRevision = before.GetProperty("revision").GetInt64(), confirm = true }));
            Check(Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 1 &&
                (await f.Owner.ReadLibrarySyncAsync()).Library.GetProperty("networkPlaylists")[0].GetProperty("intent").ValueKind == JsonValueKind.Null);
        }
    }
    private static async Task NetworkDisable()
    {
        await using var f = new Fixture(); NetworkTarget(f); await f.Enable(false, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Server.Entered = entered; f.Server.Release = new(); var saving = SaveNetwork(f);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await SetNetworkEnabled(f, false);
        Code(NetworkSync(await saving), "CANCELLED");
        int reads = f.Server.Reads; Ok(await f.Run()); Check(f.Server.Reads == reads && f.Server.SubscriptionWrites == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("networkPlaylists")[0].GetProperty("present").GetBoolean() &&
            snapshot.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").GetProperty("present").GetBoolean());
        await SetNetworkEnabled(f, true); Ok(await f.Run()); Check(f.Server.SubscriptionWrites == 1);
    }
    private static async Task NetworkUnknownCas(bool after)
    {
        await using var f = new Fixture(); var target = NetworkTarget(f); await f.Enable(false, true);
        f.Store.FailMarker = true; f.Store.FailAfter = after;
        Code(NetworkSync(await SaveNetwork(f)), "COMMIT_UNKNOWN"); Check(f.Server.SubscriptionWrites == 0);
        await f.Owner.ReconcileAsync();
        if (!after) { Ok(await f.Run()); Check(f.Server.SubscriptionWrites == 1); return; }
        var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        for (int reopen = 0; reopen < 2; reopen++)
        {
            if (reopen > 0) await f.Restart();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Code(await f.Run(), "WRITE_UNCONFIRMED");
                var blocked = await f.Owner.ReadLibrarySyncAsync();
                Check(blocked.State.GetProperty("pending").GetRawText() == pending.GetRawText() &&
                    blocked.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").GetProperty("present").GetBoolean() &&
                    f.Server.SubscriptionWrites == 0);
            }
        }
        f.Server.Entries.Add(target); Ok(await f.Run());
        var settled = await f.Owner.ReadLibrarySyncAsync();
        Check(settled.State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            settled.Library.GetProperty("networkPlaylists")[0].GetProperty("intent").ValueKind == JsonValueKind.Null &&
            f.Server.SubscriptionWrites == 0);
    }
    private static async Task NetworkDisabledBeforeRound()
    {
        await using var f = new Fixture(); NetworkTarget(f); await f.Enable(false, true);
        var synchronize = f.Owner.SynchronizeNetworkPlaylists!;
        f.Owner.SynchronizeNetworkPlaylists = async (platform, token) =>
        {
            await SetNetworkEnabled(f, false);
            return await synchronize(platform, token);
        };
        Check(S(NetworkSync(await SaveNetwork(f)), "state") == "local-only");
        Check(f.Server.Reads == 0 && f.Server.SubscriptionWrites == 0 &&
            Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("total").GetInt64() == 1);
    }
    private static async Task NetworkFenceFormat(string platform, string defect)
    {
        await using var f = new Fixture(platform); NetworkTarget(f); await f.Enable(false, true);
        f.Server.ThrowAfterSubscription = true; f.Server.FailSubscriptionReadback = true;
        Code(NetworkSync(await SaveNetwork(f)), "UPSTREAM_ERROR");
        var document = JsonNode.Parse(f.Store.Data!)!.AsObject();
        var sync = document["entries"]!.AsArray().Select(entry => entry!["value"])
            .OfType<JsonObject>().Single(value => value.ContainsKey("platforms"));
        var pending = sync["pending"]!.AsObject();
        switch (defect)
        {
            case "members": pending["before"] = new JsonArray(); break;
            case "same-presence": pending["desiredPresent"] = false; break;
            case "managed-target": pending["collectionId"] = "favorites"; break;
            case "account": pending["accountId"] = "77"; break;
            case "remote": pending["remoteId"] = "999"; break;
            case "locator": pending["locator"]!["listId"] = "901"; break;
        }
        f.Store.Data = LJ.Bytes(document);
        var original = f.Store.Data.ToArray(); int writes = f.Store.Writes, requests = f.Server.Reads + f.Server.Writes;
        await using var reader = new MusicLibraryOwner(f.Store);
        var result = await reader.ExecuteAsync("music.library.list");
        if (defect == "none")
        {
            Check(Ok(result).GetProperty("total").GetInt64() == 2);
            Check(S((await reader.ReadLibrarySyncAsync()).State.GetProperty("pending"), "operation") == "subscribe");
        }
        else Code(result, "CORRUPT_SYNC");
        Check(f.Store.Data.SequenceEqual(original) && f.Store.Writes == writes &&
            f.Server.Reads + f.Server.Writes == requests);
    }
    private static async Task NetworkKgUnsubscribeRecovery()
    {
        await using var f = new Fixture("kg"); var target = NetworkTarget(f); f.Server.Entries.Add(target);
        await f.Enable(false, true); Ok(await f.Run());
        var reference = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0]; int writes = 0;
        await InstallCloud(f, (_, _) =>
        {
            writes++; f.Server.Entries.Remove(target); f.Server.FailReads = true;
            return Task.FromResult(new byte[] { 1, 2, 3 });
        });
        Code(NetworkSync(await RemoveNetwork(f, reference)), "UPSTREAM_ERROR");
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(S(snapshot.State.GetProperty("pending"), "operation") == "unsubscribe" &&
            S(snapshot.State.GetProperty("pending").GetProperty("locator"), "listId") == "901");
        await f.Restart(); f.Server.FailReads = false; Ok(await f.Run());
        Check(writes == 1 && target.Songs.SequenceEqual([1, 2]) &&
            (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }
    private static async Task NetworkKgUnsubscribeRequest()
    {
        var body = new JsonObject { ["listid"] = 901, ["total_ver"] = 0, ["type"] = 1 }; int calls = 0;
        await CloudCall("/v2/delete_list", async (request, token) =>
        {
            calls++;
            string encoded = await request.Content!.ReadAsStringAsync(token);
            Check(JsonNode.DeepEquals(JsonNode.Parse(CloudDecode(Convert.FromBase64String(encoded))), body));
            return CloudCipher("""{"status":1,"error_code":0,"data":{}}""");
        }, body: body);
        Check(calls == 1);
    }
}
