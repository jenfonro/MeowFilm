using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Sync;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    public static IEnumerable<TestCase> CloudCases()
    {
        yield return new("Music sync KG cloud order consumes semantic occurrence keys and one readback", CloudOrder);
        yield return new("Music sync KG cloud unknown delete survives reopen without replay", CloudDeleteRecovery);
        yield return new("Music sync KG cloud dispatched write cannot acknowledge a replaced native session", CloudStaleSession);
        yield return new("Music sync KG cloud preflight retains the exact collection locator", CloudListIdentity);
        foreach (string status in new[] { "notSent", "rejected", "unknown" }) {
            string current = status; yield return new("Music sync KG cloud canonical " + status + " owns the correct native fence", () => CloudReceipt(current));
        }
        yield return new("Music sync KG cloud pre-cancel prevents dispatch but sent write retains its real receipt", CloudCancellation);
    }
    private static async Task CloudOrder()
    {
        await using var f = new Fixture("kg"); await Reorder(f); Ok(await f.Run());
        Check(f.Server.Writes == 1 && f.Server.Operations.SequenceEqual(["order"]) && f.Server.Favorite.Songs.SequenceEqual([2, 1]));
        var members = f.Server.LastWrite!["members"]!.AsArray();
        Check(members.Select(member => member!["key"]!.GetValue<string>()).SequenceEqual(["5001", "5000"]));
        Check(members.Select(member => member!["id"]!.GetValue<string>()).SequenceEqual([Native("kg", 2), Native("kg", 1)]));
        Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
        Ok(await f.Run()); Check(f.Server.Writes == 1);
    }
    private static async Task CloudDeleteRecovery()
    {
        await using var f = new Fixture("kg"); f.Server.Entries.Add(new("0:701", "Local")); await f.Enable(false, true); Ok(await f.Run());
        var c = Ok(await f.Owner.ExecuteAsync("music.library.list")).GetProperty("items").EnumerateArray().Single(c => S(c, "kind") == "playlist");
        Ok(await f.Owner.ExecuteAsync("music.library.remove", new { id = S(c, "id"), ifRevision = c.GetProperty("revision").GetInt64() }));
        f.Server.OnWrite = (call, _) => { Check(S(call.Parameters, "action") == "delete"); f.Server.FailReads = true; return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = "NETWORK_ERROR" })); };
        Code(await f.Run(), "CONFIRMATION_REQUIRED"); await Confirm(f); Code(await f.Run(), "UPSTREAM_ERROR");
        Check(f.Server.Writes == 1 && S((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending"), "operation") == "delete");
        await f.Restart(); f.Server.FailReads = false; Code(await f.Run(), "DELETE_UNCONFIRMED"); Check(f.Server.Writes == 1);
        f.Server.Entries.RemoveAll(entry => entry.Id == "0:701"); Ok(await f.Run());
        Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null && f.Server.Writes == 1);
    }
    private static async Task CloudStaleSession()
    {
        await using var f = new Fixture("kg"); await Reorder(f); f.Server.AfterWrite = () => f.Sessions.Values["kg"] = Sessions.Make("kg", 2, "77");
        Code(await f.Run(), "STALE_SESSION"); Check(f.Server.Writes == 1 && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Object);
        Code(await f.Run(), "ACCOUNT_REPLACED"); Check(f.Server.Writes == 1);
    }
    private static async Task CloudListIdentity()
    {
        await using var f = new Fixture("kg"); await Reorder(f); int baseline = f.Server.Reads;
        f.Server.ChangeRemote = remote => f.Server.Reads == baseline + 2 ? remote with { Locator = new JsonObject { ["listId"] = "999", ["version"] = "2" } } : remote;
        var result = Ok(await f.Run());
        Check(result.GetProperty("status").GetProperty("issues").EnumerateArray().Any(issue => S(issue, "code") == "REMOTE_CHANGED") && f.Server.Writes == 0);
    }
    private static async Task CloudReceipt(string status)
    {
        await using var f = new Fixture("kg"); await Reorder(f); f.Server.WriteStatus = status; f.Server.WriteError = "UPSTREAM_ERROR"; f.Server.DropWrites = true;
        var result = await f.Run(); var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        if (status == "unknown") {
            Code(result, "WRITE_UNCONFIRMED"); Check(pending.ValueKind == JsonValueKind.Object); await f.Restart(); Code(await f.Run(), "WRITE_UNCONFIRMED"); Check(f.Server.Writes == 1);
        } else {
            Check(Ok(result).GetProperty("status").GetProperty("issues").EnumerateArray().Any(issue => S(issue, "code") == "UPSTREAM_ERROR") && pending.ValueKind == JsonValueKind.Null);
            f.Server.WriteStatus = null; f.Server.DropWrites = false; Ok(await f.Run()); Check(f.Server.Writes == 2);
        }
    }
    private static async Task CloudCancellation()
    {
        await using var runtime = new MusicPlatformRuntimeFixture(); var account = Sessions.Make("kg", 1, "42"); runtime.Sessions.Current = account;
        var remote = new SyncRemote("0:101", "Favorites", "favorites", true, "42", [], new() { ["listId"] = "101" });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = new MusicSyncAdapter(account, runtime.Client, () => Task.CompletedTask, () => { }, cancelled.Token);
        try { await stopped.DeleteAsync(remote); throw new InvalidOperationException("Expected unsent cancellation"); } catch (MusicSyncWriteException error) { Check(error.Status == "notSent" && error.Code == "CANCELLED"); }
        Check(runtime.Requests.Count == 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.OnRequest = async (call, token) => { Check(call.Operation == "sync.write" && !token.CanBeCanceled); entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { status = "confirmed" }); };
        using var caller = new CancellationTokenSource(); var adapter = new MusicSyncAdapter(account, runtime.Client, () => Task.CompletedTask, () => { }, caller.Token);
        var sending = adapter.DeleteAsync(remote);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); Check(!sending.IsCompleted); release.TrySetResult(); await sending; Check(runtime.Requests.Count == 1); }
        finally { release.TrySetResult(); }
    }
}
