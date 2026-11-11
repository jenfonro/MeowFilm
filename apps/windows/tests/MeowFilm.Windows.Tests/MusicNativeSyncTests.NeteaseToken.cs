using System.Text;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    private static IEnumerable<TestCase> NeteaseTokenCases()
    {
        yield return new("Music network WY token is lazy for startup reads and managed synchronization", NeteaseTokenLazy);
        yield return new("Music network WY token accepts cookie octets without changing encrypted header or id", NeteaseTokenCookieOctets);
        yield return new("Music network WY malformed token envelopes never dispatch or retain a write fence", NeteaseTokenMalformed);
        foreach (string code in new[] { "INVALID_REQUEST", "CLOSED", "CANCELLED", "TIMEOUT", "NETWORK_ERROR",
            "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "SDK_FAILURE", "SDK_CLEANUP" })
        {
            string current = code;
            yield return new("Music network WY token " + code + " is sanitized and has no dispatched fence",
                () => NeteaseTokenFailure(current));
        }
        yield return new("Music network WY token exceptions never expose provider details", NeteaseTokenException);
        yield return new("Music network WY unsubscribe token failure retains cancel intent without a dispatched fence", NeteaseUnsubscribeTokenFailure);
        foreach (string reason in new[] { "cancel", "account", "disable", "close", "library" })
        {
            string current = reason;
            yield return new("Music network WY waiting token " + reason + " prevents dispatch and preserves intent",
                () => NeteaseTokenInvalidation(current));
        }
        yield return new("Music network WY successful HTTP without authoritative presence stays fenced", NeteaseTokenHttpSuccess);
        yield return new("Music network WY production application borrows the App token runtime", NeteaseTokenApplication);
    }

    // Synthetic runtime: only the one token method is callable, and input must contain no account data.
    private sealed class TokenRuntime : IRuntimeClient
    {
        internal int Calls;
        internal Func<CancellationToken, Task<JsonElement>>? Handler;
        internal static string Token(int number) => "SYNTHETIC_WATCHMAN_" + number;
        internal static JsonElement Success(string token) => E(new { ok = true, contract = "netease.token.v1", value = new { token } });
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(method == "netease.token.get" && JsonSerializer.SerializeToElement(parameters).GetRawText() == "{}",
                "TOKEN_METHOD_AND_EMPTY_PARAMS_ONLY");
            int call = Interlocked.Increment(ref Calls);
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(Success(Token(call)));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected source upload in subscription fixture");
    }

    private static void CheckWySubscription(Server server, bool subscribe, string expectedToken)
    {
        var request = server.LastWrite!;
        Check(server.Operations.Last() == (subscribe ? "/eapi/playlist/subscribe" : "/eapi/playlist/unsubscribe") &&
            request["id"]!.GetValue<string>() == "301");
        Check(request.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["id", "e_r", "header"]) &&
            !request["e_r"]!.GetValue<bool>(), "SUBSCRIBE_BODY_MUST_RETAIN_REAL_ID_WITHOUT_T_OR_STATIC_CHECKTOKEN");
        var header = request["header"]!.AsObject();
        Check(header["X-antiCheatToken"]!.GetValue<string>() == expectedToken &&
            header["MUSIC_U"]!.GetValue<string>() == "synthetic-only" &&
            header["__csrf"]!.GetValue<string>() == "synthetic" &&
            header["os"]!.GetValue<string>() == "pc" &&
            header["deviceId"]!.GetValue<string>().Length > 0);
        Check(server.LastCookie == string.Join("; ", header.Select(pair => pair.Key + "=" + pair.Value!.GetValue<string>())),
            "EAPI_COOKIE_HEADER_MUST_MATCH_ENCRYPTED_HEADER");
    }
    private static void CheckTokenNotStored(Fixture fixture, params string[] tokens)
    {
        string document = Encoding.UTF8.GetString(fixture.Store.Data!);
        Check(tokens.All(token => !document.Contains(token, StringComparison.Ordinal)) &&
            !document.Contains("X-antiCheatToken", StringComparison.Ordinal), "TOKEN_MUST_NOT_PERSIST");
    }
    private static string TokenFailureCode(JsonElement result)
    {
        if (result.GetProperty("ok").ValueKind == JsonValueKind.False) return S(result, "code");
        var status = result.GetProperty("status");
        Check(S(status, "phase") == "partial");
        return S(status.GetProperty("issues").EnumerateArray().Single(), "code");
    }
    private static async Task AssertTokenPreparationFailure(Fixture f, JsonElement receipt, string code)
    {
        var sync = NetworkSync(receipt);
        Check(TokenFailureCode(sync) == code && f.Tokens.Calls == 1 &&
            f.Server.SubscriptionWrites == 0 && f.Server.Writes == 0 && f.Server.Creates == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        var reference = snapshot.Library.GetProperty("networkPlaylists")[0];
        Check(snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            reference.GetProperty("present").GetBoolean() && reference.GetProperty("intent").GetProperty("present").GetBoolean() &&
            !reference.GetProperty("subscription").GetProperty("present").GetBoolean());
        CheckTokenNotStored(f, TokenRuntime.Token(1));
    }

    private static async Task NeteaseTokenLazy()
    {
        await using var f = new Fixture("wy");
        f.Tokens.Handler = _ => throw new InvalidOperationException("Synthetic SDK is unavailable");
        await f.Sync.StartAsync();
        _ = await f.Sync.ExecuteAsync("music.librarySync.get");
        await f.Enable(true, true);
        Ok(await f.Run());
        await f.AddFavorite(1); Ok(await f.Run());
        Check(f.Tokens.Calls == 0 && f.Server.Writes == 1 && f.Server.Favorite.Songs.SequenceEqual([1]));
        await f.Sync.PrepareShutdownAsync();
        await f.Sync.CancelShutdownPreparationAsync();
        Ok(await f.Run()); Check(f.Tokens.Calls == 0);
    }
    private static async Task NeteaseTokenCookieOctets()
    {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
        string valid = string.Concat(Enumerable.Range(0x21, 0x7e - 0x21 + 1).Select(value => (char)value)
            .Where(value => value is not ('"' or ',' or ';' or '\\')));
        f.Tokens.Handler = _ => Task.FromResult(TokenRuntime.Success(valid));
        Ok(NetworkSync(await SaveNetwork(f)));
        CheckWySubscription(f.Server, true, valid); CheckTokenNotStored(f, valid);
        Check(f.Tokens.Calls == 1 && f.Server.SubscriptionWrites == 1);
    }
    private static IEnumerable<JsonElement> MalformedTokens()
    {
        foreach (string token in new[] { "", " ", "a b", "a\tb", "a\rb", "a\nb", "a\0b", "a\u001fb", "a\u007fb", "a\u0080b",
            "a中b", "a😀b", "a;b", "a,b", "a\"b", "a\\b" })
            yield return TokenRuntime.Success(token);
        yield return E(new { ok = true, contract = "music.script.v3", value = new { token = TokenRuntime.Token(1) } });
        yield return E(new { ok = true, contract = "netease.token.v1", value = new { token = (string?)null } });
        yield return E(new { ok = true, contract = "netease.token.v1", value = new { token = 1 } });
        yield return E(new { ok = true, contract = "netease.token.v1", value = new { token = TokenRuntime.Token(1), extra = true } });
        yield return E(new { ok = true, contract = "netease.token.v1", value = new { } });
        yield return E(new { ok = false, contract = "netease.token.v1", code = "SYNTHETIC_PRIVATE_ERROR" });
        yield return E(new { ok = false, contract = "netease.token.v1", code = "SDK_FAILURE", message = "SYNTHETIC_PRIVATE_DETAIL" });
    }
    private static async Task NeteaseTokenMalformed()
    {
        foreach (var malformed in MalformedTokens())
        {
            await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
            f.Tokens.Handler = _ => Task.FromResult(malformed);
            await AssertTokenPreparationFailure(f, await SaveNetwork(f), "UPSTREAM_FORMAT");
        }
    }
    private static async Task NeteaseTokenFailure(string code)
    {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
        f.Tokens.Handler = _ => Task.FromResult(E(new { ok = false, contract = "netease.token.v1", code }));
        await AssertTokenPreparationFailure(f, await SaveNetwork(f), code);
        f.Tokens.Handler = null; Ok(await f.Run());
        Check(f.Tokens.Calls == 2 && f.Server.SubscriptionWrites == 1 &&
            (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null,
            "PREPARATION_FAILURE_MUST_ALLOW_A_FRESH_ATTEMPT");
    }
    private static async Task NeteaseTokenException()
    {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
        f.Tokens.Handler = _ => throw new InvalidOperationException("SYNTHETIC_PRIVATE_DETAIL " + TokenRuntime.Token(1));
        var receipt = await SaveNetwork(f);
        await AssertTokenPreparationFailure(f, receipt, "SDK_FAILURE");
        Check(!receipt.GetRawText().Contains("SYNTHETIC", StringComparison.Ordinal));
        CheckTokenNotStored(f, "SYNTHETIC_PRIVATE_DETAIL", TokenRuntime.Token(1));
    }
    private static async Task NeteaseUnsubscribeTokenFailure()
    {
        await using var f = new Fixture("wy"); var target = NetworkTarget(f); f.Server.Entries.Add(target);
        await f.Enable(false, true); Ok(await f.Run()); Check(f.Tokens.Calls == 0);
        var reference = Ok(await f.Owner.ExecuteAsync("music.library.network.list")).GetProperty("items")[0];
        f.Tokens.Handler = _ => Task.FromResult(E(new { ok = false, contract = "netease.token.v1", code = "SDK_FAILURE" }));
        Check(TokenFailureCode(NetworkSync(await RemoveNetwork(f, reference))) == "SDK_FAILURE");
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        var current = snapshot.Library.GetProperty("networkPlaylists")[0];
        Check(f.Tokens.Calls == 1 && f.Server.SubscriptionWrites == 0 && f.Server.Writes == 0 &&
            snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            !current.GetProperty("present").GetBoolean() && !current.GetProperty("intent").GetProperty("present").GetBoolean() &&
            current.GetProperty("subscription").GetProperty("present").GetBoolean() && f.Server.Entries.Contains(target));
        f.Tokens.Handler = null; Ok(await f.Run());
        CheckWySubscription(f.Server, false, TokenRuntime.Token(2));
        Check(f.Tokens.Calls == 2 && f.Server.SubscriptionWrites == 1 && !f.Server.Entries.Contains(target) &&
            target.Songs.SequenceEqual([1, 2]) && f.Server.SubscriptionTargets["301"] == target);
        CheckTokenNotStored(f, TokenRuntime.Token(1), TokenRuntime.Token(2));
    }
    private static async Task NeteaseTokenInvalidation(string reason)
    {
        await using var f = new Fixture("wy"); NetworkTarget(f); await f.Enable(false, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Tokens.Handler = async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            // Deliberately return a late successful token, so publication must recheck its guard.
            await release.Task;
            return TokenRuntime.Success(TokenRuntime.Token(1));
        };
        using var caller = new CancellationTokenSource();
        var saving = SaveNetwork(f, cancellationToken: caller.Token);
        Task control = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null &&
                f.Server.SubscriptionWrites == 0);
            if (reason == "account") f.Sessions.Values["wy"] = Sessions.Make("wy", 2, "77");
            else if (reason == "library") await f.AddFavorite(1);
            else
            {
                if (reason == "cancel") caller.Cancel();
                else if (reason == "disable") control = SetNetworkEnabled(f, false);
                else control = f.Sync.PrepareShutdownAsync();
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { release.TrySetResult(); }
        await control.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertTokenPreparationFailure(f, await saving.WaitAsync(TimeSpan.FromSeconds(5)),
            reason == "account" ? "STALE_SESSION" : reason == "library" ? "SYNC_SUPERSEDED" : "CANCELLED");
        if (reason == "account") f.Sessions.Values["wy"] = Sessions.Make("wy", 3, "42");
        if (reason == "disable") await SetNetworkEnabled(f, true);
        if (reason == "close") await f.Sync.CancelShutdownPreparationAsync();
        f.Tokens.Handler = null; Ok(await f.Run());
        Check(f.Tokens.Calls == 2 && f.Server.SubscriptionWrites == 1);
    }
    private static async Task NeteaseTokenHttpSuccess()
    {
        await using var f = new Fixture("wy"); var target = NetworkTarget(f); await f.Enable(false, true);
        f.Server.DropWrites = true;
        Code(NetworkSync(await SaveNetwork(f)), "WRITE_UNCONFIRMED");
        var pending = (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending");
        await f.Restart(); f.Server.DropWrites = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Code(await f.Run(), "WRITE_UNCONFIRMED");
            Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").GetRawText() == pending.GetRawText() &&
                f.Tokens.Calls == 1 && f.Server.SubscriptionWrites == 1);
        }
        f.Server.Entries.Add(target); Ok(await f.Run()); Ok(await f.Run());
        Check(f.Tokens.Calls == 1 && f.Server.SubscriptionWrites == 1 &&
            (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
    }
    private static async Task NeteaseTokenApplication()
    {
        await using var store = new MusicClientContextFixture.MemoryStore();
        var tokens = new TokenRuntime(); var server = new Server("wy");
        var target = new Entry("301", "Network", writable: false); target.Add(1); server.SubscriptionTargets["301"] = target;
        await using var app = new MusicNativeApplication(store, tokens, server);
        await app.InitializeAsync();
        Ok(await app.ExecuteAsync("music.accounts.list"));
        Ok(await app.ExecuteAsync("music.accounts.import", new { platform = "wy", cookie = "MUSIC_U=synthetic-only; __csrf=synthetic" }));
        var before = Ok(await app.ExecuteAsync("music.librarySync.get"));
        Ok(await app.ExecuteAsync("music.librarySync.set", new
        { platform = "wy", favorites = false, playlists = true, ifRevision = before.GetProperty("revision").GetInt64(), confirm = true }));
        Check(tokens.Calls == 0, "APP_STARTUP_AND_ACCOUNT_LIFECYCLE_MUST_NOT_LOAD_THE_SDK");
        tokens.Handler = _ => Task.FromResult(E(new { ok = false, contract = "netease.token.v1", code = "SDK_FAILURE" }));
        var saved = Ok(await app.ExecuteAsync("music.catalog.playlist.save", new { platform = "wy", id = "301", name = "Network", count = 1 }));
        Check(TokenFailureCode(NetworkSync(saved)) == "SDK_FAILURE" && tokens.Calls == 1 && server.SubscriptionWrites == 0);
        Ok(await app.ExecuteAsync("music.accounts.list"));
        Ok(await app.ExecuteAsync("music.library.list"));
        Ok(await app.ExecuteAsync("music.queue.get"));
        Check(tokens.Calls == 1, "SDK_FAILURE_MUST_NOT_DISABLE_NATIVE_ACCOUNTS_LIBRARY_OR_QUEUE");
        tokens.Handler = null; Ok(await app.ExecuteAsync("music.librarySync.run"));
        CheckWySubscription(server, true, TokenRuntime.Token(2));
        Check(tokens.Calls == 2 && server.SubscriptionWrites == 1 && server.MemberReads == 0);
        foreach (string ns in AppIdentity.NativeNamespaces)
        {
            var snapshot = await store.ReadAsync(ns);
            Check(snapshot.Data is null || !Encoding.UTF8.GetString(snapshot.Data).Contains(TokenRuntime.Token(2), StringComparison.Ordinal));
        }
        Ok(await app.ExecuteAsync("music.queue.get"));
    }
}
