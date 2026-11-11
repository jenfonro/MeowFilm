// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner : IMusicAccountTransferSource
{
    private readonly CancellationTokenSource transferLifetime = new();

    public async Task<MusicAccountTransferPlan> PrepareAccountTransferAsync(MusicAccountTransferFile file, CancellationToken token = default)
    {
        // Validate the complete external shape before any account read or network operation.
        var entries = file.Decode();
        Enter();
        try
        {
            return await DiskAsync(async () =>
            {
                var document = await ReadDocument(token).ConfigureAwait(false);
                var baselines = new Dictionary<string, (StoredAccount, long)>();
                var conflicts = new List<MusicAccountTransferConflict>();
                lock (gate)
                {
                    AccountRules.Require(!closed, "CLOSED"); token.ThrowIfCancellationRequested();
                    foreach (var entry in entries)
                    {
                        var account = document.Get(entry.Platform);
                        var state = states[entry.Platform];
                        AccountRules.Require(!state.Uncertain, "COMMIT_UNKNOWN");
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
                var document = await ReadDocument(token).ConfigureAwait(false);
                var entries = new List<TransferEntry>();
                foreach (string platform in AccountRules.Platforms)
                {
                    var account = document.Get(platform);
                    long epoch;
                    lock (gate)
                    {
                        AccountRules.Require(!closed, "CLOSED"); token.ThrowIfCancellationRequested();
                        if (states[platform].Revoked || account.Cookie is null) continue;
                        AccountRules.Require(!states[platform].Uncertain, "COMMIT_UNKNOWN");
                        AccountRules.Require(states[platform].Active is null, "BUSY");
                        epoch = states[platform].Epoch;
                    }
                    var client = platform is "wy" or "kg" ? await clients.AcquireAsync(platform, token).ConfigureAwait(false) : null;
                    lock (gate)
                    {
                        AccountRules.Require(!closed && states[platform].Epoch == epoch && !states[platform].Revoked, "STALE_SESSION");
                        entries.Add(MusicAccountTransferCredentials.Export(platform, account, client));
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
                    // An isolated protocol execution must neither Fence the account nor replace active login/reads.
                    AccountRules.Require(protocol is IMusicAccountTransferProtocol, "UNSUPPORTED");
                    var observation = await ((IMusicAccountTransferProtocol)protocol)
                        .ValidateTransferAsync(platform, cookie, cancellation.Token).ConfigureAwait(false);
                    AccountRules.Require(observation.Profile.UserId == entry.UserId, "IDENTITY_MISMATCH");
                    observation = ValidateDetailsObservation(observation, platform, cookie);
                    await CommitTransfer(platform, baseline.Account, baseline.Epoch, cookie, observation, cancellation.Token).ConfigureAwait(false);
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
            AccountRules.Require(!state.Uncertain, "COMMIT_UNKNOWN");
            AccountRules.Require(state.Epoch == epoch && state.Active is null, "STORAGE_CONFLICT");
        }
    }
    private Task<StoredAccount> CommitTransfer(string platform, StoredAccount before, long epoch, string cookie,
        MusicAccountDetailsObservation observation, CancellationToken token) => DiskAsync(async () =>
    {
        CheckTransferBaseline(platform, epoch); token.ThrowIfCancellationRequested();
        var document = await ReadDocument(token).ConfigureAwait(false);
        AccountRules.Require(before.Generation < AccountRules.MaxInteger, "LIMIT");
        var next = new StoredAccount { Generation = before.Generation + 1, Cookie = cookie,
            Profile = observation.Profile, Scope = Guid.NewGuid().ToString("D"),
            ValidatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() };
        byte[] bytes = document.Replace(platform, before, next);
        bool admitted = false;
        try
        {
            lock (gate)
            {
                CheckTransferBaseline(platform, epoch); token.ThrowIfCancellationRequested();
                states[platform].Uncertain = true; admitted = true;
            }
            // Once admitted, settle the physical write/readback. Cancellation never invents a failed receipt.
            var actual = await store.WriteAsync(DocumentNamespace, bytes, document.Fingerprint, CancellationToken.None).ConfigureAwait(false);
            AccountRules.Require(actual == DocumentFingerprint.FromBytes(bytes), "COMMIT_UNKNOWN");
            var readback = await ReadDocument(CancellationToken.None).ConfigureAwait(false);
            AccountRules.Require(readback.Fingerprint == actual, "COMMIT_UNKNOWN");
            var committed = readback.Get(platform);
            lock (gate)
            {
                var state = states[platform];
                state.Uncertain = false;
                if (!closed && state.Epoch == epoch)
                {
                    FenceLocked(platform);
                    state.Revoked = false; state.Verified = committed;
                    SaveDetailsCache(platform, new(committed.Generation, committed.Scope!, committed.ValueFingerprint!,
                        observation.Profile.UserId, observation.Profile, observation.ProfileFetchedAt,
                        observation.ProfileFetchedAt + AccountDetailsSnapshot.ProfileTtl, observation.Entitlements),
                        () => CheckTransferBaseline(platform, epoch + 1));
                }
                // A known physical receipt still counts after cancellation/close, but never reauthorizes stale state.
            }
            Notify(platform);
            return committed;
        }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict)
        {
            lock (gate) if (states[platform].Epoch == epoch) states[platform].Uncertain = false;
            throw new MusicAccountException("STORAGE_CONFLICT");
        }
        catch
        {
            if (admitted) { Notify(platform); throw new MusicAccountException("COMMIT_UNKNOWN"); }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }, token);
}
