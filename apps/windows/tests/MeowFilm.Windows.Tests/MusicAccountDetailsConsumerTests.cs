using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountDetailsConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.AccountDetailsConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> AccountDetailsConsumerCases()
    {
        yield return Case("account details activation reads local metadata without refreshing login", async () =>
        {
            var source = new DetailsProbe();
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3));
            Equal(0, source.Reads); Equal(0, source.Refreshes);
            await vm.ActivateAsync();
            Equal(1, source.Reads); Equal(0, source.Refreshes);
            Equal("账号-wy", vm.DisplayName);
            True(vm.CanRefresh && vm.HasProfile, "saved profile is not a separate login");
        });
        yield return Case("account details explicit refresh passes exact displayed generation", async () =>
        {
            var source = new DetailsProbe();
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            await vm.RefreshAsync();
            Equal("wy", source.LastPlatform); Equal(3L, source.LastGeneration);
            Equal(1, source.Reads);
        });
        yield return Case("account details switching account drains old refresh and rejects its late display", async () =>
        {
            var source = new DetailsProbe { HoldRefresh = true };
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            var refresh = vm.RefreshAsync(); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            vm.SelectAccount(DetailsAccount("tx", 7));
            True(!vm.HasProfile, "previous identity immediately cleared");
            await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            True(!refresh.IsCompleted, "physical source has not settled");
            source.Release.TrySetResult();
            await refresh; await SearchUntil(() => !vm.IsBusy);
            Equal("账号-tx", vm.DisplayName); Equal(1, source.Refreshes);
            Equal("7", vm.UserId);
        });
        yield return Case("account details hide waits for physical read and reopening performs local read only", async () =>
        {
            var source = new DetailsProbe { HoldRefresh = true };
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            var refresh = vm.RefreshAsync(); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var hide = vm.DeactivateAsync();
            True(!vm.HasProfile && !vm.CanRefresh && !hide.IsCompleted, "hidden operation still owned");
            source.Release.TrySetResult(); await Task.WhenAll(refresh, hide);
            await vm.ActivateAsync();
            Equal(2, source.Reads); Equal(1, source.Refreshes);
            Equal("账号-wy", vm.DisplayName);
        });
        yield return Case("account details stale membership is unknown rather than a negative authorization", async () =>
        {
            var source = new DetailsProbe { Stale = true };
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            Equal("权益未确认", vm.MembershipText); Equal("资料已过期", vm.ProfileState);
            True(vm.HasProfile, "old profile can display with explicit stale state");
            source.Stale = false; source.Vip = false;
            await vm.ReadAsync();
            Equal("非会员", vm.MembershipText);
            Equal(0, source.Refreshes);
        });
        yield return Case("account details repeated disposal retains the same physical refresh drain", async () =>
        {
            var source = new DetailsProbe { HoldRefresh = true };
            var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            var refresh = vm.RefreshAsync(); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var first = vm.DisposeAsync().AsTask();
            var second = vm.DisposeAsync().AsTask();
            try
            {
                True(ReferenceEquals(first, second) && !first.IsCompleted, "one physical close receipt");
                True(!vm.CanRefresh, "admission sealed synchronously");
                await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                True(!second.IsCompleted, "cancellation is not physical settlement");
            }
            finally { source.Release.TrySetResult(); await Task.WhenAll(refresh, first, second); }
        });
        yield return Case("account details unknown account write clears existing profile and sanitizes error", async () =>
        {
            var source = new DetailsProbe();
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            source.Error = "COMMIT_UNKNOWN"; await vm.RefreshAsync();
            True(!vm.HasProfile, "unknown identity is not an authorization");
            Equal("账号写入待核对", vm.Status);
        });
        yield return Case("account details saved session accepts same identity verified observation without a second read", async () =>
        {
            var source = new DetailsProbe { State = "saved" };
            await using var vm = new MusicAccountDetailsViewModel(source);
            var selected = DetailsAccount("wy", 3) with { State = "saved" };
            vm.Observed += details => vm.SelectAccount(selected with { State = details.State,
                DisplayName = details.Profile?.DisplayName ?? "", UserId = details.Profile?.UserId ?? "" });
            vm.SelectAccount(selected); await vm.ActivateAsync();
            source.State = "verified";
            await vm.RefreshAsync();
            Equal("账号-wy", vm.DisplayName);
            True(!vm.IsBusy && vm.CanRefresh, "authoritative verification is not a stale-selection error");
            Equal(1, source.Reads); Equal(1, source.Refreshes);
        });
        yield return Case("account details native invalidation clears rights and reads only the affected account", async () =>
        {
            var source = new DetailsProbe();
            await using var vm = new MusicAccountDetailsViewModel(source);
            vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
            vm.Invalidate("tx"); Equal(1, source.Reads);
            source.State = "revoked"; vm.Invalidate("wy");
            True(!vm.HasProfile, "old display fenced immediately");
            await SearchUntil(() => !vm.IsBusy);
            True(!vm.HasProfile && !vm.CanRefresh, "revoked state is not fresh membership");
            Equal("权益未确认", vm.MembershipText); Equal(2, source.Reads); Equal(0, source.Refreshes);
        });
        yield return Case("account details explicit local read adopts newer owner generation but refresh never accepts a replacement", async () =>
        {
            var source = new DetailsProbe();
            await using var vm = new MusicAccountDetailsViewModel(source);
            var row = DetailsAccount("wy", 3);
            vm.Observed += details =>
            {
                row = row with { Generation = details.Generation, State = details.State,
                    DisplayName = details.Profile?.DisplayName ?? "", UserId = details.Profile?.UserId ?? "" };
                vm.SelectAccount(row);
            };
            vm.SelectAccount(row); await vm.ActivateAsync();
            source.Generation = 4; await vm.ReadAsync();
            Equal(4L, row.Generation); Equal("4", vm.UserId);
            source.Generation = 5; await vm.RefreshAsync();
            Equal(4L, source.LastGeneration); Equal(4L, row.Generation);
            True(!vm.HasProfile, "a refresh is bound to the displayed credential generation");
            Equal("账号会话已变更", vm.Status);
        });
        yield return Case("current platform settings remain native and do not authenticate an empty store", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            True(!row.IsLoggedIn && row.ShowLogin && row.DisplayName == "未登录" && row.Membership == "",
                "logged-out platform capsule remains logged out");
            var reply = await f.App.ExecuteAsync("music.accountDetails.read", new { platform = "wy" });
            var details = reply.GetProperty("details");
            Equal("missing", details.GetProperty("membershipFreshness").GetString());
            Equal("wy", details.GetProperty("platform").GetString());
            True(details.GetProperty("profile").ValueKind == JsonValueKind.Null, "display wire has no credential");
            Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native account detail route rejects malformed generation and unsupported platforms before HTTP", async () =>
        {
            await using var f = await SearchScene.Create();
            foreach (var input in new[]
            {
                "{\"platform\":\"wy\",\"expectedGeneration\":\"3\"}",
                "{\"platform\":\"kw\",\"expectedGeneration\":3}",
                "{\"platform\":\"wy\",\"expectedGeneration\":-1}",
                "{\"platform\":\"wy\",\"expectedGeneration\":9007199254740992}",
                "{\"platform\":\"wy\",\"platform\":\"tx\",\"expectedGeneration\":3}",
                "{\"platform\":\"wy\",\"expectedGeneration\":3,\"cookie\":\"synthetic-forbidden\"}"
            })
            {
                using var parsed = JsonDocument.Parse(input);
                var result = await f.App.ExecuteAsync("music.accountDetails.refresh", parsed.RootElement);
                Equal("INVALID_REQUEST", result.GetProperty("code").GetString());
            }
            Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
        });
    }
    private static MusicPlatformAccount DetailsAccount(string platform, long generation) =>
        new(platform, platform, "verified", "账号-" + platform, generation.ToString(), generation);
    private sealed class DetailsProbe : IMusicAccountDetailsSource
    {
        internal int Reads, Refreshes;
        internal bool HoldRefresh, Stale;
        internal bool? Vip = true;
        internal string LastPlatform = "", Error = "", State = "verified";
        internal long LastGeneration;
        internal long? Generation;
        private readonly Queue<DetailsReadHold> readHolds = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DetailsReadHold HoldNextRead()
        {
            var hold = new DetailsReadHold();
            readHolds.Enqueue(hold);
            return hold;
        }
        public async Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            var generation = Generation ?? (platform == "tx" ? 7 : 3);
            if (readHolds.TryDequeue(out var hold))
            {
                using var registration = token.Register(() => hold.Cancelled.TrySetResult());
                hold.Arrived.TrySetResult();
                await hold.Release.Task;
                token.ThrowIfCancellationRequested();
            }
            return Snapshot(platform, generation);
        }
        public async Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
            CancellationToken token = default)
        {
            Refreshes++; LastPlatform = platform; LastGeneration = expectedGeneration;
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            if (HoldRefresh) await Release.Task; // Deliberately non-cooperative physical response.
            if (Error.Length > 0) throw new MusicOperationException(Error);
            return Snapshot(platform, Generation ?? expectedGeneration);
        }
        private MusicAccountDetails Snapshot(string platform, long generation)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expires = Stale ? now - 1 : now + 86400000;
            if (State == "revoked")
                return new(platform, generation, State, null, 0, 0, new(null, "unknown", null), 0, 0, []);
            var displayName = "账号-" + platform;
            return new(platform, generation, State, new(platform, generation.ToString(), displayName),
                now - 100, expires, new(Vip, Vip == true ? "vip" : "none", expires), now - 100, expires, ["flac"],
                ProfileFreshness: Stale ? MusicAccountDetailsFreshness.Stale : MusicAccountDetailsFreshness.Fresh,
                MembershipFreshness: Stale ? MusicAccountDetailsFreshness.Stale : MusicAccountDetailsFreshness.Fresh);
        }
    }

    private sealed class DetailsReadHold
    {
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
