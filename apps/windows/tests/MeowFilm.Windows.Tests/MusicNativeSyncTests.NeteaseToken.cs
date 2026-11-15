using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    private static IEnumerable<TestCase> NeteaseTokenCases()
    {
        yield return new("Music network WY preparation is lazy for startup and managed synchronization", NeteasePreparationLazy);
        yield return new("Music network WY opaque preparation travels only to the exact native write", NeteasePreparationOpaque);
        foreach (string code in new[] { "INVALID_REQUEST", "CLOSED", "CANCELLED", "TIMEOUT", "NETWORK_ERROR", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "SDK_FAILURE", "SDK_CLEANUP" }) {
            string current = code; yield return new("Music network WY canonical preparation failure retains intent without dispatch " + code, () => NeteasePreparationFailure(current));
        }
        yield return new("Music network WY unsubscribe preparation failure retains cancel intent", NeteaseUnsubscribePreparationFailure);
        foreach (string reason in new[] { "cancel", "account", "disable", "close", "library" }) {
            string current = reason; yield return new("Music network WY delayed preparation fences dispatch " + reason, () => NeteasePreparationInvalidation(current));
        }
        yield return new("Music network WY confirmed write still requires authoritative presence", NeteasePreparationReadback);
        yield return new("Music network WY production application borrows the same business runtime", NeteasePreparationApplication);
    }
    private static void CheckPreparationNotStored(Fixture fixture, params string[] ids) {
        string document = Encoding.UTF8.GetString(fixture.Store.Data!);
        Check(ids.All(id => !document.Contains(id, StringComparison.Ordinal)) && !document.Contains("preparationId", StringComparison.Ordinal), "PREPARATION_MUST_NOT_PERSIST");
    }
    private static string PreparationFailureCode(JsonElement result) {
        if (!result.GetProperty("ok").GetBoolean()) return S(result, "code");
        Check(S(result.GetProperty("status"), "phase") == "partial"); return S(result.GetProperty("status").GetProperty("issues").EnumerateArray().Single(), "code");
    }
    private static async Task AssertPreparationFailure(Fixture f, JsonElement receipt, string code) {
        Check(PreparationFailureCode(NetworkSync(receipt)) == code && f.Server.Preparations == 1 && f.Server.Writes == 0 && f.Server.SubscriptionWrites == 0 && f.Server.Creates == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); var reference = snapshot.Library.GetProperty("networkPlaylists")[0];
        Check(snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null && reference.GetProperty("present").GetBoolean() &&
            reference.GetProperty("intent").GetProperty("present").GetBoolean() && !reference.GetProperty("subscription").GetProperty("present").GetBoolean());
        CheckPreparationNotStored(f, "synthetic-preparation-1");
    }
    private static async Task NeteasePreparationLazy() {
        await using var f = new Fixture("wy"); f.Server.Prepare = _ => throw new InvalidOperationException("Unexpected preparation");
        await f.Sync.StartAsync(); _ = await f.Sync.ExecuteAsync("music.librarySync.get"); await f.Enable(true, true); Ok(await f.Run());
        await f.AddFavorite(1); Ok(await f.Run()); Check(f.Server.Preparations == 0 && f.Server.Writes == 1 && f.Server.Favorite.Songs.SequenceEqual([1]));
        await f.Sync.PrepareShutdownAsync(); await f.Sync.CancelShutdownPreparationAsync(); Ok(await f.Run()); Check(f.Server.Preparations == 0);
    }
    private static async Task NeteasePreparationOpaque() {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
        const string id = "synthetic-opaque-preparation:音乐/1"; f.Server.Prepare = _ => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { preparationId = id, extra = true }));
        Ok(NetworkSync(await SaveNetwork(f)));
        Check(f.Server.Preparations == 1 && f.Server.SubscriptionWrites == 1 && f.Server.LastWrite!["action"]!.GetValue<string>() == "subscribe" && f.Server.LastWrite["id"]!.GetValue<string>() == "301" && f.Server.LastWrite["preparationId"]!.GetValue<string>() == id);
        Check(f.Runtime.Requests.Single(call => call.Operation == "sync.prepare").Timeout == TimeSpan.FromSeconds(60) &&
            f.Runtime.Calls.Where(call => call.Method != "music.platform.request" || call.Operation != "sync.prepare").All(call => call.Timeout is null));
        CheckPreparationNotStored(f, id);
    }
    private static async Task NeteasePreparationFailure(string code) {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true); f.Server.Prepare = _ => throw new MusicPlatformException(code);
        await AssertPreparationFailure(f, await SaveNetwork(f), code);
        f.Server.Prepare = null; Ok(await f.Run()); Check(f.Server.Preparations == 2 && f.Server.SubscriptionWrites == 1 && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }
    private static async Task NeteaseUnsubscribePreparationFailure() {
        await using var f = new Fixture("wy"); var target = NetworkTarget(f); f.Server.Entries.Add(target); await f.Enable(false, true); Ok(await f.Run());
        var reference = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0];
        f.Server.Prepare = _ => throw new MusicPlatformException("SDK_FAILURE");
        Check(PreparationFailureCode(NetworkSync(await RemoveNetwork(f, reference))) == "SDK_FAILURE");
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); var current = snapshot.Library.GetProperty("networkPlaylists")[0];
        Check(f.Server.Preparations == 1 && f.Server.Writes == 0 && snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null && !current.GetProperty("present").GetBoolean() &&
            !current.GetProperty("intent").GetProperty("present").GetBoolean() && current.GetProperty("subscription").GetProperty("present").GetBoolean() && f.Server.Entries.Contains(target));
        f.Server.Prepare = null; Ok(await f.Run()); Check(f.Server.Preparations == 2 && f.Server.SubscriptionWrites == 1 && !f.Server.Entries.Contains(target) && target.Songs.SequenceEqual([1, 2]));
        CheckPreparationNotStored(f, "synthetic-preparation-2");
    }
    private static async Task NeteasePreparationInvalidation(string reason) {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Server.Prepare = async token => { using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { preparationId = "synthetic-preparation-1" }); };
        using var caller = new CancellationTokenSource(); var saving = SaveNetwork(f, cancellationToken: caller.Token); Task control = Task.CompletedTask;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null && f.Server.SubscriptionWrites == 0);
            if (reason == "account") f.Sessions.Values["wy"] = Sessions.Make("wy", 2, "77");
            else if (reason == "library") await f.AddFavorite(1);
            else { if (reason == "cancel") caller.Cancel(); else if (reason == "disable") control = SetNetworkEnabled(f, false); else control = f.Sync.PrepareShutdownAsync(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!saving.IsCompleted); }
        } finally { release.TrySetResult(); }
        await control.WaitAsync(TimeSpan.FromSeconds(5)); await AssertPreparationFailure(f, await saving.WaitAsync(TimeSpan.FromSeconds(5)), reason == "account" ? "STALE_SESSION" : reason == "library" ? "SYNC_SUPERSEDED" : "CANCELLED");
        if (reason == "account") f.Sessions.Values["wy"] = Sessions.Make("wy", 3, "42"); if (reason == "disable") await SetNetworkEnabled(f, true); if (reason == "close") await f.Sync.CancelShutdownPreparationAsync();
        f.Server.Prepare = null; Ok(await f.Run()); Check(f.Server.Preparations == 2 && f.Server.SubscriptionWrites == 1);
    }
    private static async Task NeteasePreparationReadback() {
        await using var f = new Fixture("wy"); var target = NetworkTarget(f); await f.Enable(false, true); f.Server.DropWrites = true;
        Code(NetworkSync(await SaveNetwork(f)), "WRITE_UNCONFIRMED"); var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        await f.Restart(); f.Server.DropWrites = false;
        for (int attempt = 0; attempt < 2; attempt++) { Code(await f.Run(), "WRITE_UNCONFIRMED"); Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").GetRawText() == pending.GetRawText() && f.Server.Preparations == 1 && f.Server.SubscriptionWrites == 1); }
        f.Server.Entries.Add(target); Ok(await f.Run()); Ok(await f.Run()); Check(f.Server.Preparations == 1 && f.Server.SubscriptionWrites == 1 && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }
    private static async Task NeteasePreparationApplication() {
        await using var store = new MusicPlatformRuntimeFixture.MemoryStore(); await using var runtime = new MusicPlatformRuntimeFixture(); var server = new Server("wy");
        var target = new Entry("301", "Network", writable: false); target.Add(1); server.SubscriptionTargets["301"] = target;
        runtime.OnRequest = (call, token) => {
            if (call.Operation is "account.verify" or "account.profile") {
                var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("wy", "42").GetRawText())!.AsObject();
                if (call.Operation == "account.verify") value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                return Task.FromResult(MusicPlatformRuntimeFixture.Json(value));
            }
            return call.Operation == "qr.cancel" ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { cancelled = true })) : server.Respond(call, token);
        };
        await using var app = new MusicNativeApplication(store, runtime); await app.InitializeAsync(); Ok(await app.ExecuteAsync("music.accounts.list"));
        Ok(await app.ExecuteAsync("music.accounts.import", new { platform = "wy", cookie = "MUSIC_U=synthetic-only; __csrf=synthetic" }));
        var before = Ok(await app.ExecuteAsync("music.librarySync.get")); Ok(await app.ExecuteAsync("music.librarySync.set", new { platform = "wy", favorites = false, playlists = true, ifRevision = before.GetProperty("revision").GetInt64(), confirm = true }));
        Check(server.Preparations == 0); server.Prepare = _ => throw new MusicPlatformException("SDK_FAILURE");
        var saved = Ok(await app.ExecuteAsync("music.catalog.playlist.save", new { platform = "wy", id = "301", name = "Network", count = 1 }));
        Check(PreparationFailureCode(NetworkSync(saved)) == "SDK_FAILURE" && server.Preparations == 1 && server.SubscriptionWrites == 0);
        Ok(await app.ExecuteAsync("music.accounts.list")); Ok(await app.ExecuteAsync("music.library.list")); Ok(await app.ExecuteAsync("music.queue.get")); Check(server.Preparations == 1);
        server.Prepare = null; Ok(await app.ExecuteAsync("music.librarySync.run")); Check(server.Preparations == 2 && server.SubscriptionWrites == 1 && server.MemberReads == 0);
        foreach (string ns in AppIdentity.NativeNamespaces) { var snapshot = await store.ReadAsync(ns); Check(snapshot.Data is null || !Encoding.UTF8.GetString(snapshot.Data).Contains("synthetic-preparation-2", StringComparison.Ordinal)); }
    }
}
