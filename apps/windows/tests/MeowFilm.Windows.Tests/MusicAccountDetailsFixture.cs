// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

internal sealed class MusicAccountDetailsFixture : IAsyncDisposable
{
    internal const string UserId = "9007199254740997";
    internal const string Euin = "SYNTHETIC_DETAILS_EUIN";
    internal static string Cookie(string platform) => platform switch {
        "wy" => "MUSIC_U=SYNTHETIC_WY_DETAILS_TOKEN; __csrf=SYNTHETIC_CSRF",
        "tx" => "musicid=" + UserId + "; musickey=SYNTHETIC_QQ_DETAILS_KEY; encrypt_uin=" + Euin,
        "kg" => "userid=" + UserId + "; token=SYNTHETIC_KG_DETAILS_TOKEN; KUGOU_API_MID=123456789; dfid=SYNTHETIC_DFID",
        _ => throw new InvalidOperationException()
    };
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly string Root;
    internal readonly string Platform;
    internal readonly Store Wrapper;
    internal readonly Store CacheBacking;
    internal readonly NativeTestDatabase Cache;
    internal readonly MusicPlatformRuntimeFixture Runtime;
    internal readonly MusicAccountOwner Owner;
    internal readonly List<string> Steps = [];
    internal bool HoldResponse, UnknownRights;
    internal string? FailureCode;
    internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal();
    internal Func<JsonObject, JsonObject>? Change;
    internal MusicAccountDetailsFixture(string platform, string? root = null, TimeProvider? clock = null)
    {
        Platform = platform;
        Root = root ?? TestWorkspace.DataPath("account-details", Guid.NewGuid().ToString("N"));
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
        Directory.CreateDirectory(Root);
        Wrapper = new(new NativeDatabase(Root));
        CacheBacking = Wrapper;
        Cache = new(Wrapper);
        Runtime = new();
        Runtime.OnRequest = async (call, token) => {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            if (call.Operation == "song") return MusicPlatformRuntimeFixture.Json(MusicSongFixture.Online(call.Platform, "still-borrowed"));
            if (call.Operation is not ("account.verify" or "account.profile")) throw new InvalidOperationException(call.Operation);
            lock (Steps) Steps.Add(call.Operation);
            using var registration = token.Register(() => Cancelled.TrySetResult());
            if (HoldResponse) { Entered.TrySetResult(); await Release.Task; }
            if (FailureCode is not null) throw new MusicPlatformException(FailureCode);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rights = UnknownRights
                ? new MusicAccountEntitlements(new(null, "unknown", null), now, 0, [], true, "NETWORK_ERROR", platform == "kg" ? now + 60000 : 0)
                : new MusicAccountEntitlements(new(true, platform == "wy" ? "vip" : platform == "tx" ? "huge_vip" : "concept:svip", now + 120000), now, now + 120000,
                    platform == "wy" ? ["standard", "higher", "exhigh", "lossless", "hires", "jyeffect"] : []);
            var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Json(new MusicAccountDetailsObservation(
                new(platform, UserId, "Fresh name", "https://images.invalid/avatar"), now, rights)).GetRawText())!.AsObject();
            if (call.Operation == "account.verify") value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return MusicPlatformRuntimeFixture.Json(Change?.Invoke(value) ?? value);
        };
        Owner = Runtime.CreateAccountOwner(Wrapper, clock);
    }
    internal async Task Seed()
    {
        // Generate the current credential format through the real owner, then cold-read it.
        await using var runtime = new MusicPlatformRuntimeFixture();
        runtime.OnRequest = (call, _) => {
            if (call.Operation == "qr.cancel") return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { cancelled = true }));
            if (call.Operation != "account.verify") throw new InvalidOperationException(call.Operation);
            var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(Platform, UserId, displayName: "Saved name").GetRawText())!.AsObject();
            value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(value));
        };
        await using var seed = runtime.CreateAccountOwner(Wrapper.Inner);
        var result = await seed.CallAsync("music.accounts.import", new { platform = Platform, cookie = Cookie(Platform) });
        if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Current account seed failed");
    }
    internal Task<MusicAccountDetails> Refresh(CancellationToken token = default) => Owner.RefreshDetailsAsync(Platform, 1, token);
    internal async Task<JsonObject> Document()
    {
        var doc = await Wrapper.Inner.ReadAccountsAsync();
        try { return JsonNode.Parse(doc.Data!)!.AsObject(); }
        finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal async Task Mutate(Action<JsonObject> change)
    {
        var doc = await Wrapper.Inner.ReadAccountsAsync();
        try {
            var root = JsonNode.Parse(doc.Data!)!.AsObject(); change(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await Wrapper.Inner.WriteAccountsAsync(bytes, doc.Revision); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        } finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal async Task<JsonObject> CacheDocument()
    {
        return await Wrapper.Inner.ReadDetailsAsync();
    }
    internal async Task MutateCache(Action<JsonObject> change)
    {
        var root = await Wrapper.Inner.ReadDetailsAsync(); change(root);
        await Wrapper.Inner.SeedDetailsAsync(root);
    }
    internal static JsonObject DetailsRow(JsonObject root) => root.First().Value!.AsObject();
    internal static JsonObject AccountRow(JsonObject root) => root["entries"]!.AsArray()
        .Single(r => r!["key"]!.GetValue<string>().Contains("\"platform-account\"", StringComparison.Ordinal))!["value"]!.AsObject();
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(); Wrapper.Release.TrySetResult(); CacheBacking.Release.TrySetResult();
        try { await Owner.DisposeAsync(); }
        finally { await Runtime.DisposeAsync(); await Cache.DisposeAsync(); await CacheBacking.DisposeAsync(); await Wrapper.DisposeAsync(); }
    }
    internal sealed class Store(NativeDatabase inner) : NativeTestDatabase(inner, owns: true)
    {
        internal bool Hold, HoldRead, LoseReceipt, Conflict, FailRead;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public override async Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default)
        {
            if (FailRead) throw new DocumentStoreException(DocumentStoreError.Storage);
            var result = await base.ReadAsync(read, token);
            if (HoldRead) { Entered.TrySetResult(); await Release.Task; }
            return result;
        }
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            bool hold = Hold; Hold = false;
            var result = await base.WriteAsync(db =>
            {
                if (Conflict) throw new DocumentStoreException(DocumentStoreError.Conflict);
                return write(db);
            }, token, retainedBytes, commit);
            if (hold) { Entered.TrySetResult(); await Release.Task; }
            if (LoseReceipt) throw new DocumentStoreException(DocumentStoreError.Storage);
            return result;
        }
    }
}
