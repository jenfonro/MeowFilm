// SPDX-License-Identifier: GPL-3.0-only

namespace MeowFilm.Capabilities.Accounts;

public sealed record MusicOfflineAccountRecord(
    string Platform,
    string UserId,
    string DisplayName,
    string? Avatar,
    string Cookie,
    IReadOnlyDictionary<string, string> Credentials);

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

    public async Task<IReadOnlyList<MusicOfflineAccountRecord>> ExportOfflineAccountsAsync(CancellationToken token = default)
    {
        Enter();
        try
        {
            return await DiskAsync(async () =>
            {
                var snapshot = await ReadSnapshot(token).ConfigureAwait(false);
                var items = new List<MusicOfflineAccountRecord>(AccountRules.Platforms.Length);
                foreach (string platform in AccountRules.Platforms)
                {
                    var account = snapshot.Get(platform);
                    lock (gate)
                    {
                        AccountRules.Require(!closed, "CLOSED");
                        token.ThrowIfCancellationRequested();
                        if (states[platform].Revoked || account.Cookie is null || account.Profile is null) continue;
                    }
                    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        var rawParts = AccountRules.Cookies(account.Cookie);
                        foreach (string allowed in MusicAccountTransferCredentials.Allowed(platform))
                            if (rawParts.TryGetValue(allowed, out var val))
                                parsed[allowed] = val;
                        if (parsed.Count == 0)
                            foreach (var (k, v) in rawParts)
                                parsed[k] = v;
                    }
                    catch { /* Preserve raw stored cookie string if non-canonical. */ }
                    items.Add(new MusicOfflineAccountRecord(
                        platform,
                        account.Profile.UserId,
                        account.Profile.DisplayName,
                        account.Profile.Avatar,
                        account.Cookie,
                        parsed));
                }
                return (IReadOnlyList<MusicOfflineAccountRecord>)items;
            }, token).ConfigureAwait(false);
        }
        finally { Leave(); }
    }

    public Task ReplaceOfflineAccountsAsync(
        IReadOnlyDictionary<string, MusicOfflineAccountRecord?> targets,
        CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(async token =>
        {
            foreach (string platform in AccountRules.Platforms)
            {
                if (!targets.TryGetValue(platform, out var target)) continue;
                if (target is null)
                {
                    await Logout(platform, token).ConfigureAwait(false);
                    continue;
                }
                var normalized = TryNormalizeOfflineImport(platform, target);
                if (normalized is null) continue;
                await CommitOfflineAsync(platform, normalized.Value.Cookie, normalized.Value.Profile, token).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);

    private async Task CommitOfflineAsync(string platform, string cookie, MusicAccountProfile profile, CancellationToken token)
    {
        using var op = Start(platform, token);
        try
        {
            var before = await Load(platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            AccountRules.Require(before.Generation < AccountRules.MaxInteger, "LIMIT");
            var next = new StoredAccount
            {
                Generation = before.Generation + 1,
                Cookie = cookie,
                Profile = profile,
                Scope = Guid.NewGuid().ToString("D"),
                ValidatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds()
            };
            await DiskAsync(async () =>
            {
                Guard(op);
                op.Token.ThrowIfCancellationRequested();
                var snapshot = await ReadSnapshot(op.Token, platform).ConfigureAwait(false);
                Guard(op);
                var current = snapshot.Get(platform);
                AccountRules.Require(current.Generation == before.Generation &&
                    current.Scope == before.Scope &&
                    current.Profile?.UserId == before.Profile?.UserId, "STORAGE_CONFLICT");
                var (saved, _) = await store.ReplaceAsync(platform, before, next, snapshot.Revision, CancellationToken.None).ConfigureAwait(false);
                return saved;
            }, op.Token).WaitAsync(op.Token).ConfigureAwait(false);

            lock (gate)
            {
                Guard(op);
                var state = states[platform];
                state.Revoked = false;
                state.Current = null;
                state.Verified = null;
            }
            await DeleteDetailsCacheAsync(platform).ConfigureAwait(false);
            Guard(op);
            Notify(platform);
        }
        finally { Finish(op); }
    }

    private static (string Cookie, MusicAccountProfile Profile)? TryNormalizeOfflineImport(
        string platform, MusicOfflineAccountRecord input)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawVal) in input.Credentials)
        {
            string key = (rawKey ?? "").Trim();
            string val = (rawVal ?? "").Trim();
            if (IsValidCookiePair(key, val))
                pairs[key] = val;
        }
        if (pairs.Count == 0 && !string.IsNullOrWhiteSpace(input.Cookie))
        {
            foreach (string part in input.Cookie.Split(';'))
            {
                string trimmed = part.Trim();
                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                string key = trimmed[..eq].Trim();
                string val = trimmed[(eq + 1)..].Trim();
                if (IsValidCookiePair(key, val))
                    pairs.TryAdd(key, val);
            }
        }
        if (pairs.Count == 0) return null;

        string userId = CleanText(input.UserId, 128);
        if (platform == "tx")
        {
            string cookieUid = pairs.GetValueOrDefault("musicid") ?? pairs.GetValueOrDefault("str_musicid") ?? "";
            if (userId.Length == 0) userId = cookieUid;
            if (userId.Length > 0 && cookieUid.Length == 0) pairs["musicid"] = userId;
        }
        else if (platform == "kg")
        {
            string cookieUid = pairs.GetValueOrDefault("userid") ?? "";
            if (userId.Length == 0) userId = cookieUid;
            if (userId.Length > 0 && cookieUid.Length == 0) pairs["userid"] = userId;
        }
        if (userId.Length == 0) userId = platform + "-imported";

        string cookie = string.Join("; ", pairs.Select(p => p.Key + "=" + p.Value));
        try { _ = AccountRules.Cookies(cookie); }
        catch { return null; }

        string displayName = CleanText(input.DisplayName, 128);
        string? avatar = CleanText(input.Avatar, 1024);
        if (string.IsNullOrEmpty(avatar)) avatar = null;
        return (cookie, new MusicAccountProfile(platform, userId, displayName, avatar));
    }

    private static bool IsValidCookiePair(string key, string value) =>
        key.Length is >= 1 and <= 80 &&
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') &&
        key is not ("__proto__" or "prototype" or "constructor") &&
        key.ToLowerInvariant() is not ("domain" or "path" or "expires" or "max-age" or "secure" or "httponly" or "samesite" or "priority") &&
        value.Length > 0 &&
        value.All(c => c is >= '!' and <= '~' && c is not (';' or '"' or '\\') && (c != ',' || key == "_ntes_nnid"));

    private static string CleanText(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var chars = raw.Trim().Where(c => c >= ' ' && c != '\u007f').Take(maxLen).ToArray();
        return new string(chars);
    }
}
