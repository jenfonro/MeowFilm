// SPDX-License-Identifier: GPL-3.0-only

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner
{
    private readonly CancellationTokenSource transferLifetime = new();

    public async Task<MusicAccountTransferPlan> PrepareAccountTransferAsync(MusicAccountTransferFile file, CancellationToken token = default)
    {
        var entries = file.Decode();
        Enter();
        try
        {
            return await DiskAsync(async () =>
            {
                var snapshot = await ReadSnapshot(token).ConfigureAwait(false);
                var baselines = new Dictionary<string, (StoredAccount, long)>();
                var conflicts = new List<MusicAccountTransferConflict>();
                lock (gate)
                {
                    AccountRules.Require(!closed, "CLOSED"); token.ThrowIfCancellationRequested();
                    foreach (var entry in entries)
                    {
                        var account = snapshot.Get(entry.Platform);
                        var state = states[entry.Platform];
                        AccountRules.Require(state.Active is null, "BUSY");
                        baselines.Add(entry.Platform, (account, state.Epoch));
                        if (account.Cookie is not null && !state.Revoked) conflicts.Add(new(entry.Platform));
                    }
                }
                return new MusicAccountTransferPlan(this, entries, baselines, conflicts.AsReadOnly());
            }, token).ConfigureAwait(false);
        }
        finally { Leave(); }
    }

    public async Task<MusicAccountTransferFile?> ExportAccountTransferAsync(CancellationToken token = default)
    {
        Enter();
        try
        {
            return await DiskAsync(async () =>
            {
                var snapshot = await ReadSnapshot(token).ConfigureAwait(false);
                var entries = new List<TransferEntry>();
                foreach (string platform in AccountRules.Platforms)
                {
                    var account = snapshot.Get(platform);
                    lock (gate)
                    {
                        AccountRules.Require(!closed, "CLOSED"); token.ThrowIfCancellationRequested();
                        if (states[platform].Revoked || account.Cookie is null) continue;
                        AccountRules.Require(states[platform].Active is null, "BUSY");
                        entries.Add(MusicAccountTransferCredentials.Export(platform, account));
                    }
                }
                token.ThrowIfCancellationRequested();
                return entries.Count == 0 ? null : MusicAccountTransferFile.Encode(entries);
            }, token).ConfigureAwait(false);
        }
        finally { Leave(); }
    }

    public async Task<MusicAccountTransferSummary> ImportAccountTransferAsync(MusicAccountTransferPlan plan,
        MusicAccountTransferPolicy policy, CancellationToken token = default)
    {
        List<TransferEntry> entries;
        Dictionary<string, (StoredAccount Account, long Epoch)> baselines;
        HashSet<string> conflicts;
        lock (plan)
        {
            AccountRules.Require(ReferenceEquals(plan.Owner, this) && !plan.Consumed &&
                policy is MusicAccountTransferPolicy.Skip or MusicAccountTransferPolicy.Overwrite or MusicAccountTransferPolicy.Cancel);
            plan.Consumed = true;
            entries = plan.Entries.ToList(); baselines = new(plan.Baselines);
            conflicts = plan.Conflicts.Select(value => value.Platform).ToHashSet(StringComparer.Ordinal);
            plan.Entries.Clear(); plan.Baselines.Clear();
        }
        if (policy == MusicAccountTransferPolicy.Cancel)
        {
            foreach (var entry in entries) entry.Fields.Clear();
            return new(entries.Select(value => new MusicAccountTransferItem(value.Platform, "cancelled", "CANCELLED")).ToArray());
        }
        bool entered = false;
        try
        {
            Enter(); entered = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, transferLifetime.Token);
            var results = new List<MusicAccountTransferItem>();
            foreach (var entry in entries)
            {
                string platform = entry.Platform;
                if (cancellation.IsCancellationRequested)
                { results.Add(new(platform, "cancelled", "CANCELLED")); continue; }
                if (policy == MusicAccountTransferPolicy.Skip && conflicts.Contains(platform))
                { results.Add(new(platform, "skipped")); continue; }
                try
                {
                    string cookie = MusicAccountTransferCredentials.Cookie(entry);
                    var baseline = baselines[platform];
                    CheckTransferBaseline(platform, baseline.Epoch);
                    var verified = await protocol.ValidateAsync(platform, cookie, cancellation.Token).ConfigureAwait(false);
                    var observation = verified.Details;
                    AccountRules.Require(observation.Profile.UserId == entry.UserId, "IDENTITY_MISMATCH");
                    AccountRules.Require(observation.Profile.Platform == platform &&
                        !string.IsNullOrEmpty(observation.Profile.UserId), "IDENTITY_MISMATCH");
                    await CommitTransfer(platform, baseline.Account, baseline.Epoch, verified.Cookie, observation, cancellation.Token).ConfigureAwait(false);
                    results.Add(new(platform, "imported"));
                }
                catch (Exception error)
                {
                    string code = AccountRules.Error(error);
                    bool cancelled = code == "CANCELLED" || code == "CLOSED" && cancellation.IsCancellationRequested;
                    results.Add(new(platform, cancelled ? "cancelled" : "failed", code));
                }
            }
            return new(results.ToArray());
        }
        finally
        {
            foreach (var entry in entries) entry.Fields.Clear();
            baselines.Clear(); if (entered) Leave();
        }
    }

    private void CheckTransferBaseline(string platform, long epoch)
    {
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            var state = states[platform];
            AccountRules.Require(state.Epoch == epoch && state.Active is null, "STORAGE_CONFLICT");
        }
    }

    private Task<StoredAccount> CommitTransfer(string platform, StoredAccount before, long epoch, string cookie,
        MusicAccountDetailsObservation observation, CancellationToken token) => DiskAsync(async () =>
    {
        CheckTransferBaseline(platform, epoch); token.ThrowIfCancellationRequested();
        var snapshot = await ReadSnapshot(token, platform).ConfigureAwait(false);
        var current = snapshot.Get(platform);
        AccountRules.Require(current.Generation == before.Generation &&
            current.Scope == before.Scope &&
            current.Profile?.UserId == before.Profile?.UserId, "STORAGE_CONFLICT");
        AccountRules.Require(before.Generation < AccountRules.MaxInteger, "LIMIT");
        var next = new StoredAccount
        {
            Generation = before.Generation + 1, Cookie = cookie,
            Profile = observation.Profile, Scope = Guid.NewGuid().ToString("D"),
            ValidatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        lock (gate) { CheckTransferBaseline(platform, epoch); token.ThrowIfCancellationRequested(); }
        var (committed, _) = await store.ReplaceAsync(platform, before, next, snapshot.Revision, CancellationToken.None).ConfigureAwait(false);
        StoredAccountDetails? detailsToSave = null;
        lock (gate)
        {
            var state = states[platform];
            if (!closed && state.Epoch == epoch)
            {
                _ = FenceLocked(platform);
                state.Revoked = false; state.Verified = committed;
                detailsToSave = new(committed.Generation, committed.Scope!,
                    observation.Profile.UserId, observation.Profile, observation.ProfileFetchedAt,
                    observation.ProfileFetchedAt + StoredAccountDetails.ProfileTtl, observation.Entitlements);
            }
        }
        if (detailsToSave is not null)
            await SaveDetailsCacheAsync(platform, detailsToSave, () => CheckTransferBaseline(platform, epoch + 1)).ConfigureAwait(false);
        Notify(platform);
        return committed;
    }, token);
}
