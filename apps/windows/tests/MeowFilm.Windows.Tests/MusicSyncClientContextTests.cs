// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Sync;

namespace MeowFilm.Windows.Tests;

public static class MusicSyncClientContextTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music sync delegated matching and subsequent read borrow the same canonical client", async () => {
            await using var runtime = new MusicPlatformRuntimeFixture(); var account = new MusicPlatformSession("wy", "synthetic-sync", 1, "42", "MUSIC_U=SYNTHETIC"); runtime.Sessions.Current = account;
            int guards = 0;
            runtime.OnRequest = (call, _) => Task.FromResult(call.Operation switch {
                "search" => MusicPlatformRuntimeFixture.Json(new { items = new[] { Target("wy", "42") }, more = false, continuation = (object?)null }),
                "sync.read" => MusicPlatformRuntimeFixture.Json(new SyncDirectory("42", [], [], [])), _ => throw new InvalidOperationException(call.Operation) });
            var adapter = new MusicSyncAdapter(account, runtime.Client, () => Task.CompletedTask, () => { Check(runtime.Sessions.IsCurrent(account)); guards++; }, default);
            var mapped = await adapter.ResolveAsync(Target("tx", "original")); Check(mapped is not null && NativeMusicSong.NativeId(mapped) == "42");
            var directory = await adapter.ReadAsync(["playlist"]); Check(directory.AccountId == "42" && guards >= 4 && runtime.Requests.Count == 2);
            Check(runtime.Requests.All(call => call.Payload.GetProperty("expectedSession").GetProperty("scope").GetString() == account.Scope &&
                call.Payload.GetProperty("expectedSession").GetProperty("generation").GetInt64() == account.Generation));
        });
        foreach (bool ambiguous in new[] { false, true })
            yield return new("Music sync KG matching reads beyond 300 candidates without an invented quota " + ambiguous, async () => {
                await using var runtime = new MusicPlatformRuntimeFixture(); var account = new MusicPlatformSession("kg", "synthetic-sync-kg", 1, "42", "userid=42; token=SYNTHETIC"); runtime.Sessions.Current = account;
                runtime.OnRequest = (call, _) => {
                    Check(call.Operation == "search"); int page = call.Parameters.GetProperty("page").GetInt32();
                    var items = Enumerable.Range(0, 30).Select(_ => Target("kg", new string('A', 32))).ToList();
                    if (ambiguous && page == 11) items.Add(Target("kg", new string('B', 32)));
                    return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { items, more = page < 11, continuation = page < 11 ? new { page = page + 1 } : null }));
                };
                var adapter = new MusicSyncAdapter(account, runtime.Client, () => Task.CompletedTask, () => Check(runtime.Sessions.IsCurrent(account)), default);
                var mapped = await adapter.ResolveAsync(Target("tx", "original"));
                Check(runtime.Requests.Count == 11 && (ambiguous ? mapped is null : mapped is not null && NativeMusicSong.NativeId(mapped) == new string('A', 32)));
            });
        yield return new("Music sync real continuation stall remains a termination boundary", async () => {
            await using var runtime = new MusicPlatformRuntimeFixture(); var account = new MusicPlatformSession("kg", "scope", 1, "42", "userid=42; token=SYNTHETIC"); runtime.Sessions.Current = account;
            runtime.OnRequest = (call, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { items = Array.Empty<object>(), more = true, continuation = new { page = call.Parameters.GetProperty("page").GetInt32() } }));
            var adapter = new MusicSyncAdapter(account, runtime.Client, () => Task.CompletedTask, () => { }, default);
            try { await adapter.ResolveAsync(Target("tx", "original")); throw new InvalidOperationException("Expected stall"); } catch (MusicLibraryException error) { Check(error.Code == "PAGINATION_STALLED"); }
            Check(runtime.Requests.Count == 1);
        });
    }
    private static JsonObject Target(string platform, string id) => MusicSongFixture.Online(platform, id, "Synthetic track", "Synthetic artist", "Synthetic album", 200);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Sync canonical adapter invariant"); }
}
