using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;
using MeowFilm.Capabilities.PlaybackCache;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicPlaybackCacheTests
{
    private static readonly string QualityPolicySignature = new('a', 64);
    private static readonly string CacheSignature = new('b', 64);
    private static readonly string OtherCacheSignature = new('c', 64);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music cache clear fences reads already holding an index without revoking open leases", async () =>
        {
            await using var f = new Fixture();
            string signature = MusicAudioFileCache.ScriptSignature("source", "wy", "recording", "flac");
            string scope = "script:" + signature;
            await f.Cache.StorePlayedAsync(scope, "song", signature, f.ScriptInfo("source", "flac"), () => true, default);
            await using var lease = await f.Cache.OpenAsync(scope, "song", signature, default);
            Check(lease is not null);
            // Hold only the file/publication boundary, not disk I/O. These three
            // reads capture the old index before clear advances its epoch.
            var gate = (SemaphoreSlim)typeof(MusicAudioFileCache).GetField("metadataGate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Cache)!;
            await gate.WaitAsync();
            var opening = f.Cache.OpenAsync(scope, "song", signature, default);
            var info = f.Cache.ReadInfoAsync(scope, "song", signature, default);
            var history = f.Cache.ReadScriptHistoryAsync("song", default);
            var clearing = f.Cache.ClearAsync(default);
            try { Check(!opening.IsCompleted && !info.IsCompleted && !history.IsCompleted && !clearing.IsCompleted); }
            finally { gate.Release(); }
            await clearing.WaitAsync(TimeSpan.FromSeconds(5));
            await using var rejected = await opening;
            Check(rejected is null && await info is null && (await history).Length == 0);
            Check(lease!.Content.ReadByte() == 'R' && !lease.Revoked.IsCancellationRequested);
            Check(await f.Cache.ReadInfoAsync(scope, "song", signature, default) is null,
                "clear removes cache eligibility while the OS retains already-open bytes");
        });
        yield return new("Music cache clear wins delayed initial index reads and keeps caller cancellation", async () =>
        {
            await using var f = new Fixture();
            string signature = MusicAudioFileCache.ScriptSignature("source", "wy", "recording", "flac");
            string scope = "script:" + signature;
            await f.Cache.StorePlayedAsync(scope, "song", signature, f.ScriptInfo("source", "flac"), () => true, default);
            byte[] index = f.CacheStore.ReadMemory(MusicAudioFileCache.Namespace).Data!;
            var backing = new HeldIndexStore(index);
            await using var store = new CacheDocumentStore(backing);
            var cache = new MusicAudioFileCache(f.Root, store, clock: f.Clock);
            var opening = cache.OpenAsync(scope, "song", signature, default);
            var info = cache.ReadInfoAsync(scope, "song", signature, default);
            var history = cache.ReadScriptHistoryAsync("song", default);
            using var cancelled = new CancellationTokenSource();
            var cancelledOpen = cache.OpenAsync(scope, "song", signature, cancelled.Token);
            var cancelledInfo = cache.ReadInfoAsync(scope, "song", signature, cancelled.Token);
            var cancelledHistory = cache.ReadScriptHistoryAsync("song", cancelled.Token);
            await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                cancelled.Cancel();
                foreach (Task task in new Task[] { cancelledOpen, cancelledInfo, cancelledHistory })
                {
                    try { await task; throw new InvalidOperationException("Expected cancellation, not cache miss"); }
                    catch (OperationCanceledException) { }
                }
                await cache.ClearAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { backing.Release.TrySetResult(); }
            await using var rejected = await opening;
            Check(rejected is null && await info is null && (await history).Length == 0);
            Check((await cache.ReadScriptHistoryAsync("song", default)).Length == 0);
            await store.DrainAsync();
        });
        yield return new("Music cache cold erase fences delayed initial index reads", async () =>
        {
            await using var f = new Fixture();
            string signature = MusicAudioFileCache.ScriptSignature("source", "wy", "recording", "flac");
            string scope = "script:" + signature;
            await f.Cache.StorePlayedAsync(scope, "song", signature, f.ScriptInfo("source", "flac"), () => true, default);
            var backing = new HeldIndexStore(f.CacheStore.ReadMemory(MusicAudioFileCache.Namespace).Data!);
            await using var store = new CacheDocumentStore(backing);
            var cache = new MusicAudioFileCache(f.Root, store, clock: f.Clock);
            var opening = cache.OpenAsync(scope, "song", signature, default);
            var info = cache.ReadInfoAsync(scope, "song", signature, default);
            var history = cache.ReadScriptHistoryAsync("song", default);
            await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await cache.EraseAsync(scope, "song", default).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { backing.Release.TrySetResult(); }
            await using var rejected = await opening;
            Check(rejected is null && await info is null && (await history).Length == 0);
            Check(await cache.ReadInfoAsync(scope, "song", signature, default) is null &&
                (await cache.ReadScriptHistoryAsync("song", default)).Length == 0,
                "a late disk read cannot revive an erased cold entry");
            await store.DrainAsync();
        });
        yield return new("Music cache explicit clear reports physical deletion failure but automatic eviction stays optional", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            string path = Directory.EnumerateFiles(f.Root, "*.audio").Single();
            // A foreign read does not grant delete sharing, unlike our active playback lease.
            using (var foreign = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { await f.Cache.ClearAsync(default); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && File.Exists(path), "explicit cleanup cannot claim a foreign-locked file was removed");
                Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
            }
            await f.Cache.ClearAsync(default);
            Check(!File.Exists(path));
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            path = Directory.EnumerateFiles(f.Root, "*.audio").Single();
            using (var foreign = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await f.Cache.EraseAsync("scope", "song", default);
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            foreach (Task task in new[] { f.Cache.EraseAsync("scope", "song", cancelled.Token),
                f.Cache.TouchAsync("scope", "song", CacheSignature, cancelled.Token), f.Cache.ClearAsync(cancelled.Token) })
            {
                try { await task; throw new InvalidOperationException("Expected cancellation, not successful invalidation"); }
                catch (OperationCanceledException) { }
            }
        });
        yield return new("Script complete history keeps provider recording and tier across restart without persisting credentials", async () =>
        {
            await using var f = new Fixture();
            foreach (string provider in new[] { "removed-source", "another-source" })
                foreach (string quality in new[] { "320k", "flac" })
                {
                    string signature = MusicAudioFileCache.ScriptSignature(provider, "wy", "recording", quality);
                    await f.Cache.StorePlayedAsync("script:" + signature, "song", signature,
                        f.ScriptInfo(provider, quality), () => true, default);
                }
            await f.CacheStore.DrainAsync();
            await using var reopenedStore = new CacheDocumentStore(f.Store);
            var restarted = f.NewCache(cacheStore: reopenedStore);
            var history = await restarted.ReadScriptHistoryAsync("song", default);
            Check(history.Length == 4 && (await restarted.ReadScriptHistoryAsync("another-song", default)).Length == 0);
            foreach (var info in history)
            {
                Check(info.GetProperty("providerName").GetString() == "Saved source name" &&
                    !info.TryGetProperty("url", out _) && !info.TryGetProperty("headers", out _));
                await using var lease = await restarted.OpenAsync(info.GetProperty("scope").GetString()!, "song",
                    info.GetProperty("qualityCacheSignature").GetString()!, default);
                Check(lease?.Length == f.Bytes.Length);
            }
            await restarted.ClearAsync(default);
            Check((await restarted.ReadScriptHistoryAsync("song", default)).Length == 0);
        });
        yield return new("Played script cache waits for cancelled speculative producer without another transfer or lost publication", async () =>
        {
            await using var f = new Fixture();
            using var prepared = f.Cache.Prepare("scope", "next", CacheSignature, f.Info(song: "next"), () => true, default);
            Check(await prepared.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
            string signature = MusicAudioFileCache.ScriptSignature("source", "wy", "recording", "flac");
            var played = f.Cache.StorePlayedAsync("script:" + signature, "song", signature,
                f.ScriptInfo("source", "flac"), () => true, default);
            Check(!played.IsCompleted && f.Requests == 1, "one producer while speculative file awaits its Started event");
            prepared.Dispose();
            await Task.WhenAll(prepared.Work, played).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Requests == 2 && (await f.Cache.ReadScriptHistoryAsync("song", default)).Length == 1 &&
                await f.Cache.OpenAsync("scope", "next", CacheSignature, default) is null,
                "played history is published after speculative drain, never before playback");
        });
        yield return new("Script cache honors media headers and rejects altered provenance", async () =>
        {
            await using var f = new Fixture();
            f.RequiredHeader = "X-Music-Test: cache-token";
            string signature = MusicAudioFileCache.ScriptSignature("source", "wy", "recording", "flac");
            var info = f.ScriptInfo("source", "flac");
            await f.Cache.StorePlayedAsync("script:" + signature, "song", signature, info, () => true, default);
            Check((await f.Cache.ReadScriptHistoryAsync("song", default)).Length == 1);
            var raw = JsonNode.Parse(info.GetRawText())!.AsObject();
            raw["installationId"] = "different-source";
            await f.Cache.StorePlayedAsync("script:" + signature, "song", signature,
                JsonSerializer.SerializeToElement(raw), () => true, default);
            Check((await f.Cache.ReadScriptHistoryAsync("song", default)).Length == 1);
        });
        foreach (double expected in new[] { 0d, 1d, 11d, 11.01d })
            yield return new("Music file duration absolute boundary " + expected, async () =>
            {
                await using var f = new Fixture();
                string signature = MusicAudioFileCache.ScriptSignature("source", "wy", "recording", "flac");
                var raw = JsonNode.Parse(f.ScriptInfo("source", "flac").GetRawText())!.AsObject();
                raw["duration"] = expected;
                var cache = f.NewCache((_, _, _) => Task.FromResult(1d));
                await cache.StorePlayedAsync("script:" + signature, "song", signature,
                    JsonSerializer.SerializeToElement(raw), () => true, default);
                var history = await cache.ReadScriptHistoryAsync("song", default);
                Check(history.Length == (expected <= 11 ? 1 : 0));
            });
        yield return new("Music complete file quality reads actual FLAC precision MP3 bitrate and never infers master", () =>
        {
            foreach (var (rate, depth, quality) in new[] { (44100, 16, "flac"), (44100, 24, "flac24bit"), (96000, 16, "flac24bit") })
            {
                byte[] bytes = new byte[52]; "fLaC"u8.CopyTo(bytes);
                bytes[4] = 128; bytes[7] = 34;
                ulong packed = ((ulong)rate << 44) | (1UL << 41) | ((ulong)(depth - 1) << 36) | 44100;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
                bytes[42] = 255; bytes[43] = 248;
                var facts = AudioCacheStructure.Inspect(new MemoryStream(bytes), default);
                Check(facts?.Quality == quality && facts.SampleRate == rate && facts.BitDepth == depth && facts.Channels == 2);
            }
            byte[] mp3 = new byte[417 * 3];
            for (int p = 0; p < mp3.Length; p += 417) { mp3[p] = 255; mp3[p + 1] = 251; mp3[p + 2] = 144; }
            var lossy = AudioCacheStructure.Inspect(new MemoryStream(mp3), default);
            Check(lossy?.Quality == "128k" && lossy.SampleRate == 44100 && lossy.BitDepth == 0);
            return Task.CompletedTask;
        });
        yield return new("Music cache publishes observed encoding only after native validation not requested master", async () =>
        {
            await using var f = new Fixture();
            var raw = System.Text.Json.Nodes.JsonNode.Parse(f.Info().GetRawText())!.AsObject();
            raw["quality"] = "master"; raw["qualityVerified"] = false;
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, JsonSerializer.SerializeToElement(raw), () => true, default);
            var info = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default);
            Check(info?["actualQuality"]?.GetValue<string>() == "flac" && info["qualityVerified"]!.GetValue<bool>());
            Check(info!["quality"]!.GetValue<string>() == "master" && info["bitDepth"]!.GetValue<int>() == 16);
            Check(info["url"] is null && info["headers"] is null);
        });
        yield return new("Music online-only complete cache retains bytes without manufacturing offline permission", async () =>
        {
            await using var f = new Fixture();
            var raw = System.Text.Json.Nodes.JsonNode.Parse(f.Info(scope: "public:wy").GetRawText())!.AsObject();
            raw["offlineAllowed"] = false;
            await f.Cache.StorePlayedAsync("public:wy", "song", CacheSignature, JsonSerializer.SerializeToElement(raw), () => true, default);
            await f.CacheStore.DrainAsync();
            await using var reopenedStore = new CacheDocumentStore(f.Store);
            var restarted = f.NewCache(cacheStore: reopenedStore);
            var info = await restarted.ReadInfoAsync("public:wy", "song", CacheSignature, default);
            Check(info is not null && info["offlineAllowed"]!.GetValue<bool>() == false &&
                info["licenseExpiresAt"] is null, "byte cache must not grant offline entitlement");
            await using var lease = await restarted.OpenAsync("public:wy", "song", CacheSignature, default);
            Check(lease is not null && lease.Length == f.Bytes.Length);
            await f.CacheStore.DrainAsync();
            Check(f.Store.Data.Keys.SequenceEqual(new[] { MusicAudioFileCache.Namespace }));
        });
        yield return new("Music file prefetch serves validated bytes but publishes only at real started and cancels unplayed files", async () =>
        {
            await using var f = new Fixture();
            using (var pending = f.Cache.Prepare("scope", "song", CacheSignature, f.Info(), () => true, default))
            {
                Check(await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
                using (var competing = f.NewCache().Prepare("scope", "other-process", CacheSignature,
                    f.Info(song: "other-process"), () => true, default))
                {
                    await competing.Work.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(await competing.Ready.Task is null, "another owner cannot prune a live producer");
                }
                Check(await f.Cache.OpenAsync("scope", "song", CacheSignature, default) is null, "unplayed bytes are not a cache hit");
                await using (var lease = await pending.OpenAsync(default))
                    Check(lease is not null && lease.Length == f.Bytes.Length, "next player reads prepared bytes");
                f.Clock.Now += 1000;
                pending.Played.TrySetResult(f.Clock.Now);
                await pending.Work.WaitAsync(TimeSpan.FromSeconds(5));
                await using var hit = await f.Cache.OpenAsync("scope", "song", CacheSignature, default);
                Check(hit is not null);
            }
            using (var abandoned = f.Cache.Prepare("scope", "other", CacheSignature,
                f.Info(song: "other"), () => true, default))
            {
                Check(await abandoned.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
                abandoned.Dispose(); await abandoned.Work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(await f.Cache.OpenAsync("scope", "other", CacheSignature, default) is null);
            }
            Check(Directory.EnumerateFiles(f.Root, "*.audio").Count() == 1);
        });
        yield return new("Music cache clear fences pending publication and leaves active read and unrelated documents untouched", async () =>
        {
            await using var f = new Fixture();
            f.Store.Data["music.library.v1"] = "library"u8.ToArray();
            f.Store.Data["unrelated.settings.v1"] = "settings"u8.ToArray();
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            await using (var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default))
            {
                using var pending = f.Cache.Prepare("scope", "next", CacheSignature,
                    f.Info(song: "next"), () => true, default);
                Check(await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
                await f.Cache.ClearAsync(default);
                pending.Played.TrySetResult(f.Clock.Now);
                await pending.Work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(await f.Cache.OpenAsync("scope", "song", CacheSignature, default) is null &&
                    await f.Cache.OpenAsync("scope", "next", CacheSignature, default) is null);
                Check(lease is not null && lease.Content.ReadByte() == 'R', "clearing cannot interrupt current playback");
                Check(Encoding.UTF8.GetString(f.Store.Data["music.library.v1"]) == "library" &&
                    Encoding.UTF8.GetString(f.Store.Data["unrelated.settings.v1"]) == "settings");
            }
            await f.Cache.ClearAsync(default);
            Check(!Directory.EnumerateFiles(f.Root, "*.audio").Any(), "reclaim released lease");
        });
        yield return new("Music cache persisted receipt has no signed URL or headers and reading does not touch TTL", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            await f.CacheStore.DrainAsync();
            int writes = f.Store.Writes;
            var info = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default);
            Check(info is not null && info["url"] is null && info["headers"] is null && info["sourceKey"]!.GetValue<string>() == "wy:1");
            Check(f.Store.Writes == writes);
            f.Clock.Now += MusicAudioFileCache.Ttl;
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
        });
        yield return new("Music file cache bad indexes miss and fresh media rebuilds normally", async () =>
        {
            foreach (byte[] data in new[] { "{"u8.ToArray(), "{\"entries\":null}"u8.ToArray(),
                "{\"entries\":{\"bad\":{}}}"u8.ToArray() })
            {
                await using var f = new Fixture();
                f.CacheStore.Save(MusicAudioFileCache.Namespace, data);
                Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
                Check(await f.Cache.OpenAsync("scope", "song", CacheSignature, default) is null);
                await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
                await using var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default);
                Check(f.Requests == 1 && lease?.Length == f.Bytes.Length);
            }
        });
        yield return new("Music file cache metadata identity license and size mismatches miss without blocking producers", async () =>
        {
            foreach (Action<JsonObject> change in new Action<JsonObject>[]
            {
                entry => entry["info"]!["scope"] = "other",
                entry => entry["info"]!["songId"] = "other-song",
                entry => entry["signature"] = OtherCacheSignature,
                entry => entry["info"]!["qualityCacheSignature"] = OtherCacheSignature,
                entry => entry["info"]!["duration"] = 2,
                entry => entry["info"]!["size"] = 5,
                entry => entry["info"]!["licenseExpiresAt"] = 1,
                entry => entry["info"]!.AsObject().Remove("scope"),
                entry =>
                {
                    entry["info"]!["licenseExpiresAt"] = 2_000_000_000_000L;
                    entry["info"]!["authorizationExpiresAt"] = 2_000_000_000_000L;
                    entry["info"]!["offlineAllowed"] = true;
                }
            })
            {
                await using var f = new Fixture();
                await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
                var root = JsonNode.Parse(f.CacheStore.ReadMemory(MusicAudioFileCache.Namespace).Data!)!.AsObject();
                change(root["entries"]!.AsObject().Single().Value!.AsObject());
                f.CacheStore.Save(MusicAudioFileCache.Namespace, JsonSerializer.SerializeToUtf8Bytes(root));
                Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
                Check(await f.Cache.OpenAsync("scope", "song", CacheSignature, default) is null);
                await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
                var repaired = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default);
                Check(repaired is not null && !repaired["offlineAllowed"]!.GetValue<bool>());
            }
        });
        yield return new("Music file cache invalid producer identity never starts network or publishes", async () =>
        {
            foreach (Action<JsonObject> change in new Action<JsonObject>[]
            {
                info => info["scope"] = "other", info => info["songId"] = "other",
                info => info["qualityCacheSignature"] = OtherCacheSignature,
                info => info.Remove("delivery"), info => info["size"] = "1"
            })
            {
                await using var f = new Fixture();
                var info = JsonNode.Parse(f.Info().GetRawText())!.AsObject(); change(info);
                await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, JsonSerializer.SerializeToElement(info), () => true, default);
                using var prepared = f.Cache.Prepare("scope", "song", CacheSignature, JsonSerializer.SerializeToElement(info), () => true, default);
                await prepared.Work;
                Check(await prepared.Ready.Task is null && f.Requests == 0);
            }
        });
        yield return new("Music file cache storage protection and persistence failures are optional", async () =>
        {
            await using var f = new Fixture();
            f.Store.Failure = DocumentStoreError.Protection;
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is not null);
            await f.CacheStore.DrainAsync();
        });
        yield return new("Music file cache explicit clear discards bad index and cache bytes only", async () =>
        {
            await using var f = new Fixture();
            f.CacheStore.Save(MusicAudioFileCache.Namespace, "{"u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".audio"), f.Bytes);
            await f.Cache.ClearAsync(default);
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null &&
                !Directory.EnumerateFiles(f.Root, "*.audio").Any());
        });
        yield return new("Music cache accepts bounded APE metadata and extensible PCM while rejecting damaged tags", () =>
        {
            byte[] mp3 = new byte[417 * 3];
            for (int p = 0; p < mp3.Length; p += 417) { mp3[p] = 255; mp3[p + 1] = 251; mp3[p + 2] = 144; }
            using var tagged = new MemoryStream(); tagged.Write(mp3);
            using (var w = new BinaryWriter(tagged, Encoding.UTF8, true))
            {
                w.Write(1u); w.Write(0u); w.Write("Title\0x"u8);
                w.Write("APETAGEX"u8); w.Write(2000u); w.Write(47u); w.Write(1u); w.Write(0u); w.Write(0L);
            }
            AudioCacheStructure.Validate(tagged, default);
            byte[] invalid = tagged.ToArray(); invalid[^16] = 2;
            bool rejected = false;
            try { AudioCacheStructure.Validate(new MemoryStream(invalid), default); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
            byte[] wav = Wav(); using var extended = new MemoryStream();
            extended.Write(wav.AsSpan(0, 36));
            using (var w = new BinaryWriter(extended, Encoding.UTF8, true))
            {
                w.Write((ushort)22); w.Write((ushort)16); w.Write(4u);
                w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
                w.Write(wav.AsSpan(36)); extended.Position = 4; w.Write((int)extended.Length - 8);
                extended.Position = 16; w.Write(40); w.Write((ushort)0xfffe);
            }
            AudioCacheStructure.Validate(extended, default);
            return Task.CompletedTask;
        });
        yield return new("Music file cache streams complete media independently of download queue and survives restart", async () =>
        {
            await using var f = new Fixture();
            var info = f.Info(scope: "account");
            await f.Cache.StorePlayedAsync("account", "song", CacheSignature, info, () => true, default);
            await f.CacheStore.DrainAsync();
            Check(f.Store.Data.Keys.SequenceEqual(new[] { MusicAudioFileCache.Namespace }));
            await using var reopenedStore = new CacheDocumentStore(f.Store);
            var restarted = f.NewCache(cacheStore: reopenedStore);
            await using var lease = await restarted.OpenAsync("account", "song", CacheSignature, default);
            Check(lease is not null && lease.Length == f.Bytes.Length && !lease.Content.CanWrite);
            Check(await restarted.OpenAsync("other-account", "song", CacheSignature, default) is null);
            Check(await restarted.OpenAsync("account", "song", OtherCacheSignature, default) is null);
            // Signed URL expiration is not file expiration.
            f.Clock.Now += 120000;
            await using var afterUrl = await restarted.OpenAsync("account", "song", CacheSignature, default);
            Check(afterUrl is not null);
        });
        yield return new("Music file cache real-play touch extends TTL but never license lifetime", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("account", "song", CacheSignature,
                f.Info(scope: "account", license: f.Clock.Now + 2 * 86400000), () => true, default);
            f.Clock.Now += 86400000;
            await f.Cache.TouchAsync("account", "song", CacheSignature, default);
            f.Clock.Now += 86400001;
            Check(await f.Cache.OpenAsync("account", "song", CacheSignature, default) is null);
        });
        yield return new("Music file cache refuses incomplete body wrong duration and stale publication", async () =>
        {
            foreach (string failure in new[] { "incomplete", "size", "duration", "stale" })
            {
                await using var f = new Fixture();
                var cache = failure == "duration" ? f.NewCache((_, _, _) => Task.FromResult(12d)) : f.Cache;
                await cache.StorePlayedAsync("account", "song", CacheSignature,
                    f.Info(scope: "account", full: failure != "incomplete", size: failure == "size" ? 50 : null),
                    () => failure != "stale", default);
                Check(await cache.OpenAsync("account", "song", CacheSignature, default) is null, failure);
                Check(!Directory.EnumerateFiles(f.Root, "*.audio").Any(), failure + " orphan");
            }
        });
        yield return new("Music file cache rejects tampering and revoked read leases", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("account", "song", CacheSignature,
                f.Info(scope: "account"), () => true, default);
            using (var cancellation = new CancellationTokenSource())
            {
                await using var lease = await f.Cache.OpenAsync("account", "song", CacheSignature, cancellation.Token);
                Check(lease is not null); cancellation.Cancel(); Check(lease!.Revoked.IsCancellationRequested);
            }
            string file = Directory.EnumerateFiles(f.Root, "*.audio").Single();
            var changed = f.Bytes.ToArray(); changed[^1] ^= 1; await File.WriteAllBytesAsync(file, changed);
            Check(await f.Cache.OpenAsync("account", "song", CacheSignature, default) is null);
        });
        yield return new("Music cache native silent decoder validates real synthetic PCM file", async () =>
        {
            await using var f = new Fixture();
            string path = Path.Combine(f.Root, "probe.wav");
            await File.WriteAllBytesAsync(path, f.Bytes);
            double duration = await NativeAudioCacheProbe.ValidateAsync(path, 1, default);
            Check(Math.Abs(duration - 1) < .1);
            Check(Math.Abs(await NativeAudioCacheProbe.ValidateAsync(path, 11, default) - 1) < .1);
            Check(Math.Abs(await NativeAudioCacheProbe.ValidateAsync(path, 0, default) - 1) < .1);
            bool rejected = false;
            try { await NativeAudioCacheProbe.ValidateAsync(path, 11.01, default); }
            catch (PlaybackException) { rejected = true; }
            Check(rejected, "decoded duration beyond ten seconds must fail");
        });
    }
    private sealed class HeldIndexStore(byte[] index) : IDocumentStore
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(index.ToArray(), DocumentFingerprint.FromBytes(index));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken token = default) => Task.FromResult(DocumentFingerprint.FromBytes(data.Span));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = TestWorkspace.DataPath("meowfilm-audio-cache-test-" + Guid.NewGuid().ToString("N"));
        internal readonly MemoryStore Store = new();
        internal readonly CacheDocumentStore CacheStore;
        internal readonly Clock Clock = new();
        internal readonly byte[] Bytes = Wav();
        internal int Requests;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task server;
        internal readonly Uri Origin;
        internal readonly MusicAudioFileCache Cache;
        internal Fixture()
        {
            CacheStore = new(Store);
            Directory.CreateDirectory(Root); listener.Start();
            Origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
            server = Serve(); Cache = NewCache();
        }
        internal MusicAudioFileCache NewCache(Func<string, double, CancellationToken, Task<double>>? validate = null,
            CacheDocumentStore? cacheStore = null) =>
            new(Root, cacheStore ?? CacheStore, validate ?? ((_, expected, _) => Task.FromResult(expected)), Clock,
                new NativeHttpMediaOptions());
        internal string? RequiredHeader;
        internal JsonElement ScriptInfo(string provider, string quality)
        {
            string signature = MusicAudioFileCache.ScriptSignature(provider, "wy", "recording", quality);
            var raw = JsonNode.Parse(Info().GetRawText())!.AsObject();
            raw["kind"] = "script"; raw["installationId"] = provider; raw["providerName"] = "Saved source name";
            raw["sourceKey"] = "recording"; raw["scope"] = "script:" + signature;
            raw["quality"] = quality; raw["requestedQuality"] = quality;
            raw["qualityCacheSignature"] = signature;
            raw["headers"] = new JsonObject { ["X-Music-Test"] = "cache-token" };
            foreach (string field in new[] { "generation", "qualityPolicySignature", "offlineAllowed", "delivery", "authorizationExpiresAt" })
                raw.Remove(field);
            return JsonSerializer.SerializeToElement(raw);
        }
        internal JsonElement Info(string scope = "scope", string song = "song",
            string? signature = null, bool full = true, long? size = null, long? license = null) =>
            JsonSerializer.SerializeToElement(new
            {
                ok = true,
                kind = "official",
                platform = "wy",
                songId = song,
                sourceKey = "wy:1",
                scope,
                generation = 1,
                quality = "flac",
                requestedQuality = "auto",
                actualQuality = "",
                qualityVerified = false,
                playbackVerified = false,
                qualityPolicySignature = QualityPolicySignature,
                qualityCacheSignature = signature ?? CacheSignature,
                url = new Uri(Origin, "track.wav").AbsoluteUri,
                headers = new { },
                expiresAt = Clock.Now + 60000,
                duration = 1d,
                size = size ?? Bytes.Length,
                full,
                licenseExpiresAt = license,
                delivery = "wy-official",
                offlineAllowed = full && license is not null,
                authorizationExpiresAt = license
            });
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream = client.GetStream();
                    var request = new List<byte>(); var one = new byte[1];
                    while (request.Count < 16384 && await stream.ReadAsync(one, lifetime.Token) != 0)
                    {
                        request.Add(one[0]);
                        if (request.Count >= 4 && request.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
                    }
                    Interlocked.Increment(ref Requests);
                    if (RequiredHeader is not null && !Encoding.ASCII.GetString(request.ToArray()).Contains(RequiredHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), lifetime.Token);
                        continue;
                    }
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Bytes.Length}\r\nContent-Type: audio/wav\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, lifetime.Token);
                    await stream.WriteAsync(Bytes, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop(); await server; lifetime.Dispose();
            await CacheStore.DisposeAsync();
            // Only the explicitly created UUID test root, no computed external directory.
            TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
            if (!Path.GetFileName(Root).StartsWith("meowfilm-audio-cache-test-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(Root, true); await Store.DisposeAsync();
        }
    }
}
