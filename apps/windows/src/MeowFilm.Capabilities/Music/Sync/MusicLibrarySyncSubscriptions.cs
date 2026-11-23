using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Sync;

internal sealed partial class MusicLibrarySyncEngine
{
    private IEnumerable<JsonObject> NetworkPlaylists => MusicLibraryOwner.NetworkPlaylists(library).OfType<JsonObject>();
    private JsonObject NetworkPlaylist(string id) => NetworkPlaylists.Single(reference => S(reference["id"]) == id);
    private static JsonObject SubscriptionBaseline(MusicSyncAdapter adapter, SyncSubscription? actual) =>
        O(("accountId", adapter.AccountId), ("present", actual is not null), ("locator", actual?.Locator ?? new()));
    private void MergeSubscriptions(Dictionary<string, SyncDirectory> pulls)
    {
        foreach (var (platform, pull) in pulls)
        {
            if (!Enabled(platform, "playlist")) continue;
            library["accounts"]![platform] = pull.AccountId;
            var adapter = adapters[platform];
            var actual = pull.Subscriptions.ToDictionary(subscription => subscription.Id, StringComparer.Ordinal);
            foreach (var reference in NetworkPlaylists.Where(reference => S(reference["platform"]) == platform).ToArray())
            {
                var before = reference.DeepClone();
                actual.TryGetValue(S(reference["remoteId"]), out var remote);
                if (reference["intent"] is JsonObject intent)
                    LJ.Require(S(intent["accountId"]) == adapter.AccountId, "ACCOUNT_REBIND_REQUIRED");
                else if (reference["subscription"] is JsonObject baseline)
                {
                    LJ.Require(S(baseline["accountId"]) == adapter.AccountId, "ACCOUNT_REBIND_REQUIRED");
                    if (LJ.B(baseline["present"]) != (remote is not null)) reference["present"] = remote is not null;
                }
                else if (remote is null && LJ.B(reference["present"]))
                    reference["intent"] = O(("accountId", adapter.AccountId), ("present", true));
                else if (remote is not null) reference["present"] = true;
                if (remote is not null)
                {
                    reference["name"] = remote.Name; reference["cover"] = remote.Cover; reference["count"] = remote.Count;
                }
                reference["subscription"] = SubscriptionBaseline(adapter, remote);
                if (!JsonNode.DeepEquals(before, reference)) reference["revision"] = LJ.I(reference["revision"]) + 1;
                actual.Remove(S(reference["remoteId"]));
            }
            foreach (var remote in actual.Values)
            {
                var reference = MusicLibraryOwner.NewNetworkPlaylist(library, platform, remote.Id, remote.Name, remote.Cover, remote.Count);
                reference["subscription"] = SubscriptionBaseline(adapter, remote);
                MusicLibraryOwner.NetworkPlaylists(library).Add(reference);
            }
        }
    }
    private async Task<SyncDirectory> ReadSubscriptions(MusicSyncAdapter adapter)
    {
        var actual = await adapter.ReadSubscriptionsAsync();
        LJ.Require(actual.AccountId == adapter.AccountId, "IDENTITY_MISMATCH");
        await Guard(); return actual;
    }
    private async Task PushSubscriptions(MusicSyncAdapter adapter)
    {
        foreach (string id in NetworkPlaylists.Where(reference => S(reference["platform"]) == adapter.Platform &&
            reference["intent"] is JsonObject).Select(reference => S(reference["id"])).ToArray())
        {
            try
            {
                var reference = NetworkPlaylist(id); var intent = LJ.Object(reference["intent"]);
                LJ.Require(S(intent["accountId"]) == adapter.AccountId, "ACCOUNT_REBIND_REQUIRED");
                bool desired = LJ.B(intent["present"]);
                var directory = await ReadSubscriptions(adapter);
                LJ.Require(!directory.Directory.Any(row => row.Owned && row.Id == S(reference["remoteId"])), "READ_ONLY");
                var actual = directory.Subscriptions.SingleOrDefault(subscription => subscription.Id == S(reference["remoteId"]));
                if ((actual is not null) == desired)
                {
                    reference["subscription"] = SubscriptionBaseline(adapter, actual); reference["intent"] = null;
                    reference["revision"] = LJ.I(reference["revision"]) + 1; await Save(); continue;
                }
                LJ.Require(reference["subscription"] is JsonObject baseline &&
                    S(baseline["accountId"]) == adapter.AccountId && LJ.B(baseline["present"]) == (actual is not null), "REMOTE_CHANGED");
                string operation = desired ? "subscribe" : "unsubscribe";
                adapter.CheckWriteCapability(operation);
                var target = desired ? await adapter.SubscriptionTargetAsync(S(reference["remoteId"]), S(reference["name"]),
                    S(reference["cover"]), LJ.I(reference["count"])) : actual!;
                var dispatch = await adapter.PrepareSubscriptionAsync(target, desired);
                await Guard();
                LJ.Require(state["pending"] is null, "PENDING_WRITE");
                state["pending"] = O(("platform", adapter.Platform), ("accountId", adapter.AccountId), ("collectionId", id),
                    ("remoteId", target.Id), ("operation", operation), ("beforePresent", actual is not null),
                    ("desiredPresent", desired), ("locator", target.Locator), ("name", target.Name));
                Status("pushing"); await Save();
                try
                {
                    await dispatch();
                }
                catch (MusicSyncWriteException error) when (error.Status is "notSent" or "rejected")
                { await ClearUnappliedWrite(); throw; }
                catch (Exception error) when (!Fatal(error)) { /* Only an authoritative directory can settle this durable fence. */ }
                await RecoverSubscriptionPending(adapter);
            }
            catch (Exception error) when (!Fatal(error))
            {
                Issue(adapter.Platform, id, Error(error));
                if (state["pending"] is not null) throw;
            }
        }
    }
    private async Task RecoverSubscriptionPending(MusicSyncAdapter adapter)
    {
        var pending = LJ.Object(state["pending"]); string id = S(pending["collectionId"]);
        LJ.Require(adapter.Platform == S(pending["platform"]) && adapter.AccountId == S(pending["accountId"]), "PENDING_ACCOUNT_CHANGED");
        var directory = await ReadSubscriptions(adapter);
        LJ.Require(!directory.Directory.Any(row => row.Owned && row.Id == S(pending["remoteId"])), "READ_ONLY");
        var actual = directory.Subscriptions.SingleOrDefault(subscription => subscription.Id == S(pending["remoteId"]));
        var reference = NetworkPlaylist(id); bool applied = (actual is not null) == LJ.B(pending["desiredPresent"]);
        reference["subscription"] = SubscriptionBaseline(adapter, actual);
        if (applied && reference["intent"] is JsonObject intent && S(intent["accountId"]) == adapter.AccountId &&
            LJ.B(intent["present"]) == LJ.B(pending["desiredPresent"])) reference["intent"] = null;
        reference["revision"] = LJ.I(reference["revision"]) + 1;
        // An unchanged directory cannot rule out a late effect from the dispatched request.
        // Only observing the requested presence can release this durable fence.
        if (applied) state["pending"] = null;
        await Save(); LJ.Require(applied, "WRITE_UNCONFIRMED");
    }
}
