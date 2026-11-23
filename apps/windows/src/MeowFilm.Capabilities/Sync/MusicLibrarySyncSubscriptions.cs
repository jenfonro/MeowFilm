using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Sync;

internal sealed partial class MusicLibrarySyncEngine
{
    private MusicNetworkPlaylistRecord NetworkPlaylist(string id) =>
        library.NetworkPlaylists.Single(reference => reference.PlaylistId == id);

    private void ReplaceNetworkPlaylist(MusicNetworkPlaylistRecord updated)
    {
        var list = library.NetworkPlaylists.ToArray();
        int idx = Array.FindIndex(list, r => r.PlaylistId == updated.PlaylistId);
        list[idx] = updated;
        library = library with { NetworkPlaylists = list };
    }

    private static MusicNetworkPlaylistRecord ApplySubscriptionBaseline(
        MusicNetworkPlaylistRecord reference, MusicSyncAdapter adapter, SyncSubscription? actual) =>
        reference with
        {
            SubAccountId = adapter.AccountId,
            SubPresent = actual is not null,
            SubListId = actual?.Locator.ListId,
            SubCreatorId = actual?.Locator.CreatorId,
            SubCreatorListId = actual?.Locator.CreatorListId
        };

    private void MergeSubscriptions(Dictionary<string, SyncDirectory> pulls)
    {
        foreach (var (platform, pull) in pulls)
        {
            if (!Enabled(platform, "playlist")) continue;
            SetAccount(platform, pull.AccountId);
            var adapter = adapters[platform];
            var actual = pull.Subscriptions.ToDictionary(subscription => subscription.Id, StringComparer.Ordinal);
            foreach (var reference in library.NetworkPlaylists.Where(r => r.Platform == platform).ToArray())
            {
                var before = reference;
                var next = reference;
                actual.TryGetValue(reference.RemoteId, out var remote);
                if (next.IntentAccountId is not null)
                    MusicRules.Require(next.IntentAccountId == adapter.AccountId, "ACCOUNT_REBIND_REQUIRED");
                else if (next.SubAccountId is not null)
                {
                    MusicRules.Require(next.SubAccountId == adapter.AccountId, "ACCOUNT_REBIND_REQUIRED");
                    if ((next.SubPresent == true) != (remote is not null))
                        next = next with { Present = remote is not null };
                }
                else if (remote is null && next.Present)
                    next = next with { IntentAccountId = adapter.AccountId, IntentPresent = true };
                else if (remote is not null)
                    next = next with { Present = true };

                if (remote is not null)
                    next = next with { Name = remote.Name, Cover = remote.Cover, SongCount = remote.Count };

                next = ApplySubscriptionBaseline(next, adapter, remote);
                if (next != before) next = next with { Revision = checked(next.Revision + 1) };
                ReplaceNetworkPlaylist(next);
                actual.Remove(reference.RemoteId);
            }
            foreach (var remote in actual.Values)
            {
                var created = ApplySubscriptionBaseline(
                    MusicLibraryOwner.NewNetworkPlaylist(library, platform, remote.Id, remote.Name, remote.Cover, remote.Count),
                    adapter, remote);
                library = library with { NetworkPlaylists = [.. library.NetworkPlaylists, created] };
            }
        }
    }

    private async Task<SyncDirectory> ReadSubscriptions(MusicSyncAdapter adapter)
    {
        var actual = await adapter.ReadSubscriptionsAsync();
        MusicRules.Require(actual.AccountId == adapter.AccountId, "IDENTITY_MISMATCH");
        await Guard();
        return actual;
    }

    private async Task PushSubscriptions(MusicSyncAdapter adapter)
    {
        foreach (string id in library.NetworkPlaylists
            .Where(r => r.Platform == adapter.Platform && r.IntentAccountId is not null)
            .Select(r => r.PlaylistId).ToArray())
        {
            try
            {
                var reference = NetworkPlaylist(id);
                MusicRules.Require(reference.IntentAccountId == adapter.AccountId, "ACCOUNT_REBIND_REQUIRED");
                bool desired = reference.IntentPresent == true;
                var directory = await ReadSubscriptions(adapter);
                MusicRules.Require(!directory.Directory.Any(row => row.Owned && row.Id == reference.RemoteId), "READ_ONLY");
                var actual = directory.Subscriptions.SingleOrDefault(subscription => subscription.Id == reference.RemoteId);
                if ((actual is not null) == desired)
                {
                    var settled = ApplySubscriptionBaseline(reference, adapter, actual) with
                    {
                        IntentAccountId = null,
                        IntentPresent = null,
                        Revision = checked(reference.Revision + 1)
                    };
                    ReplaceNetworkPlaylist(settled);
                    await Save();
                    continue;
                }
                MusicRules.Require(reference.SubAccountId == adapter.AccountId &&
                    (reference.SubPresent == true) == (actual is not null), "REMOTE_CHANGED");
                string operation = desired ? "subscribe" : "unsubscribe";
                adapter.CheckWriteCapability(operation);
                var target = desired
                    ? await adapter.SubscriptionTargetAsync(reference.RemoteId, reference.Name, reference.Cover, reference.SongCount)
                    : actual!;
                var dispatch = await adapter.PrepareSubscriptionAsync(target, desired);
                await Guard();
                MusicRules.Require(state.Pending is null, "PENDING_WRITE");
                state = state with
                {
                    Pending = new MusicSyncPendingRecord(
                        adapter.Platform, adapter.AccountId, id, target.Id, operation,
                        actual is not null, desired, target.Name,
                        target.Locator.ListId, target.Locator.CreatorId, target.Locator.CreatorListId,
                        [], [], [], [])
                };
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
                if (state.Pending is not null) throw;
            }
        }
    }

    private async Task RecoverSubscriptionPending(MusicSyncAdapter adapter)
    {
        var pending = state.Pending!;
        string id = pending.CollectionId;
        MusicRules.Require(adapter.Platform == pending.Platform && adapter.AccountId == pending.AccountId, "PENDING_ACCOUNT_CHANGED");
        var directory = await ReadSubscriptions(adapter);
        MusicRules.Require(!directory.Directory.Any(row => row.Owned && row.Id == pending.RemoteId), "READ_ONLY");
        var actual = directory.Subscriptions.SingleOrDefault(subscription => subscription.Id == pending.RemoteId);
        var reference = NetworkPlaylist(id);
        bool desired = pending.DesiredPresent == true;
        bool applied = (actual is not null) == desired;
        var next = ApplySubscriptionBaseline(reference, adapter, actual) with { Revision = checked(reference.Revision + 1) };
        if (applied && next.IntentAccountId == adapter.AccountId && next.IntentPresent == desired)
            next = next with { IntentAccountId = null, IntentPresent = null };
        ReplaceNetworkPlaylist(next);
        // An unchanged directory cannot rule out a late effect from the dispatched request.
        // Only observing the requested presence can release this durable fence.
        if (applied) state = state with { Pending = null };
        await Save();
        MusicRules.Require(applied, "WRITE_UNCONFIRMED");
    }
}
